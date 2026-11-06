using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Server.Logging;
using Server.Regions;

namespace Server.Custom.Idle;

/// <summary>
/// A walking route found on the coarse route map. <see cref="Waypoints"/> runs from the start, through the crossing
/// points between map cells (a teleporter is a pair of points with no walking between them), to the goal.
/// <paramref name="TeleportLegs"/> holds each index i where stepping onto waypoint i is a teleporter that lands on
/// waypoint i + 1.
/// </summary>
public sealed record IdleRoute(
    int Tiles, int Teleports, int UnguardedTiles, IReadOnlyList<Point3D> Waypoints, IReadOnlyList<int> TeleportLegs
);

public static class IdleRegions
{
    /// <summary>Town, UnguardedTown, Guarded, Dungeon, or Wilderness.</summary>
    public static string KindOf(Region region)
    {
        if (region.GetRegion<TownRegion>() is { } town)
        {
            return town.IsDisabled() ? "UnguardedTown" : "Town";
        }

        if (region.GetRegion<GuardedRegion>() is { } guarded && !guarded.IsDisabled())
        {
            return "Guarded";
        }

        return region.IsPartOf<DungeonRegion>() ? "Dungeon" : "Wilderness";
    }

    public static bool IsGuarded(Region region) =>
        region.GetRegion<GuardedRegion>() is { } guarded && !guarded.IsDisabled();
}

/// <summary>
/// The walkable world in 16x16-tile cells. Each walkable patch of a cell (one side of a wall or river) is a node;
/// edges are the places where walking crosses into the next cell, and teleporters. Built by [IdleGenSpots from the
/// same walk that tags spots reachable, so a route exists exactly when walking there is possible. Distances are
/// estimates: walking inside a cell is counted as a straight line between crossing points.
/// </summary>
public sealed class IdleRouteMap
{
    public const int CellShift = 4;
    public const int CellSize = 1 << CellShift;
    public const byte TeleportFlag = 1;

    private const int Magic = 0x54524449; // "IDRT"
    private const int FormatVersion = 1;

    // Shared search buffers: the loop is single-threaded, and a stamp per search avoids clearing them.
    private static int[] _dist = [];
    private static int[] _prevEdge = [];
    private static int[] _stamp = [];
    private static int[] _goalStamp = [];
    private static int _generation;
    private static readonly PriorityQueue<int, int> _queue = new();

    private readonly int _cellsW;
    private readonly int _cellsH;

    // Nodes are sorted by cell; a cell's nodes are _cellStart[c] .. _cellStart[c + 1] - 1.
    private readonly int[] _cellStart;
    private readonly Point3D[] _nodeCenter;
    private readonly int[] _nodeTiles;

    // Edges are sorted by source node; a node's edges are _edgeStart[n] .. _edgeStart[n + 1] - 1.
    private readonly int[] _edgeStart;
    private readonly int[] _edgeFromNode;
    private readonly int[] _edgeToNode;
    private readonly Point3D[] _edgeFrom;
    private readonly Point3D[] _edgeTo;
    private readonly byte[] _edgeFlags;
    private readonly int[] _edgeCost;

    // Cells with more than one node: per tile, 1-based node within the cell (0 = not walkable) and its floor height.
    private readonly Dictionary<int, byte[]> _labels;
    private readonly Dictionary<int, sbyte[]> _labelZ;

    // Areas inside a dungeon region; read on the first search, once regions are loaded.
    private bool[] _dungeon;

    public Map Map { get; }
    public DateTime Generated { get; }
    public List<IdleMapFileStamp> MapFiles { get; }
    public int NodeCount => _nodeCenter.Length;
    public int EdgeCount => _edgeToNode.Length;

    internal IdleRouteMap(
        Map map, DateTime generated, List<IdleMapFileStamp> mapFiles, int cellsW, int cellsH, int[] cellStart,
        Point3D[] nodeCenter, int[] nodeTiles, int[] edgeFromNode, int[] edgeToNode, Point3D[] edgeFrom,
        Point3D[] edgeTo, byte[] edgeFlags, Dictionary<int, byte[]> labels, Dictionary<int, sbyte[]> labelZ
    )
    {
        Map = map;
        Generated = generated;
        MapFiles = mapFiles;
        _cellsW = cellsW;
        _cellsH = cellsH;
        _cellStart = cellStart;
        _nodeCenter = nodeCenter;
        _nodeTiles = nodeTiles;
        _edgeFromNode = edgeFromNode;
        _edgeToNode = edgeToNode;
        _edgeFrom = edgeFrom;
        _edgeTo = edgeTo;
        _edgeFlags = edgeFlags;
        _labels = labels;
        _labelZ = labelZ;

        _edgeStart = new int[nodeCenter.Length + 1];
        foreach (var from in edgeFromNode)
        {
            _edgeStart[from + 1]++;
        }

        for (var i = 0; i < nodeCenter.Length; i++)
        {
            _edgeStart[i + 1] += _edgeStart[i];
        }

        _edgeCost = new int[edgeToNode.Length];
        for (var e = 0; e < edgeToNode.Length; e++)
        {
            var hop = (edgeFlags[e] & TeleportFlag) != 0 ? 0 : Steps(edgeFrom[e], edgeTo[e]);
            _edgeCost[e] = Steps(nodeCenter[edgeFromNode[e]], edgeFrom[e]) + hop + Steps(edgeTo[e], nodeCenter[edgeToNode[e]]);
        }
    }

    /// <summary>Walking steps between two points ignoring obstacles: diagonal steps take as long as straight ones.</summary>
    public static int Steps(Point3D a, Point3D b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>The node a position belongs to, or the nearest one around it; -1 if the area has no walkable node.</summary>
    public int FindNode(Point3D p)
    {
        var cx = p.X >> CellShift;
        var cy = p.Y >> CellShift;

        if (p.X < 0 || p.Y < 0 || cx >= _cellsW || cy >= _cellsH)
        {
            return -1;
        }

        var cell = cy * _cellsW + cx;
        var start = _cellStart[cell];
        var count = _cellStart[cell + 1] - start;

        if (count == 1)
        {
            return start;
        }

        if (count > 1 && _labels.TryGetValue(cell, out var labels))
        {
            var i = ((p.Y & (CellSize - 1)) << CellShift) | (p.X & (CellSize - 1));
            if (labels[i] > 0 && Math.Abs(_labelZ[cell][i] - p.Z) <= 16)
            {
                return start + labels[i] - 1;
            }
        }

        // Not on a tile the walk reached (an upper floor, or off the walkable area): the closest node nearby.
        var best = -1;
        var bestScore = int.MaxValue;
        var ring = count > 0 ? 0 : 1;

        for (var y = cy - ring; y <= cy + ring; y++)
        {
            for (var x = cx - ring; x <= cx + ring; x++)
            {
                if (x < 0 || y < 0 || x >= _cellsW || y >= _cellsH)
                {
                    continue;
                }

                var c = y * _cellsW + x;
                for (var n = _cellStart[c]; n < _cellStart[c + 1]; n++)
                {
                    var score = Steps(p, _nodeCenter[n]) + Math.Abs(p.Z - _nodeCenter[n].Z) / 4;
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = n;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Walking routes from <paramref name="from"/> to each goal, in one search; null where no route exists or the
    /// route is longer than <paramref name="maxTiles"/> (0 = no limit). With <paramref name="spread"/> above 0, goals
    /// more than that many times as far as the nearest one (plus 100 tiles) are not searched for. Routes do not pass
    /// through dungeons unless they start or end in one.
    /// </summary>
    public IdleRoute[] FindRoutes(Point3D from, IReadOnlyList<Point3D> goals, int maxTiles = 0, double spread = 0)
    {
        var results = new IdleRoute[goals.Count];
        var start = FindNode(from);

        if (start < 0 || goals.Count == 0)
        {
            return results;
        }

        PrepareBuffers();
        var gen = ++_generation;
        var avoidDungeons = !_dungeon[start];
        var limit = maxTiles > 0 ? maxTiles : int.MaxValue;
        var nearestFound = false;

        var goalNodes = new int[goals.Count];
        var pending = 0;
        for (var i = 0; i < goals.Count; i++)
        {
            var node = goalNodes[i] = FindNode(goals[i]);
            if (node >= 0 && _goalStamp[node] != gen)
            {
                _goalStamp[node] = gen;
                pending++;
            }
        }

        _queue.Clear();
        _dist[start] = 0;
        _prevEdge[start] = -1;
        _stamp[start] = gen;
        _queue.Enqueue(start, 0);

        while (pending > 0 && _queue.TryDequeue(out var u, out var d))
        {
            if (d != _dist[u])
            {
                continue;
            }

            if (d > limit)
            {
                break;
            }

            if (_goalStamp[u] == gen)
            {
                // Settled: the shortest path to this goal is known.
                _goalStamp[u] = -gen;
                pending--;

                if (spread > 0 && !nearestFound)
                {
                    nearestFound = true;
                    limit = (int)Math.Min(limit, d * spread + 100);
                }
            }

            for (var e = _edgeStart[u]; e < _edgeStart[u + 1]; e++)
            {
                var v = _edgeToNode[e];
                if (avoidDungeons && _dungeon[v] && _goalStamp[v] != gen)
                {
                    continue;
                }

                var nd = d + _edgeCost[e];
                if (_stamp[v] != gen || nd < _dist[v])
                {
                    _stamp[v] = gen;
                    _dist[v] = nd;
                    _prevEdge[v] = e;
                    _queue.Enqueue(v, nd);
                }
            }
        }

        _queue.Clear();

        for (var i = 0; i < goals.Count; i++)
        {
            var node = goalNodes[i];
            if (node >= 0 && _goalStamp[node] == -gen)
            {
                var route = Build(from, goals[i], start, node);
                results[i] = maxTiles > 0 && route.Tiles > maxTiles ? null : route;
            }
        }

        return results;
    }

    private void PrepareBuffers()
    {
        if (_dungeon == null)
        {
            _dungeon = new bool[NodeCount];
            for (var n = 0; n < NodeCount; n++)
            {
                _dungeon[n] = Region.Find(_nodeCenter[n], Map).IsPartOf<DungeonRegion>();
            }
        }

        if (_dist.Length >= NodeCount)
        {
            return;
        }

        _dist = new int[NodeCount];
        _prevEdge = new int[NodeCount];
        _stamp = new int[NodeCount];
        _goalStamp = new int[NodeCount];
        _generation = 0;
    }

    private IdleRoute Build(Point3D from, Point3D goal, int start, int node)
    {
        var edges = new List<int>();
        for (var n = node; n != start;)
        {
            var e = _prevEdge[n];
            edges.Add(e);
            n = _edgeFromNode[e];
        }

        edges.Reverse();

        var points = new List<Point3D>(edges.Count * 2 + 2) { from };
        var teleportLegs = new List<int>();
        var tiles = 0;
        var teleports = 0;
        var unguarded = 0;
        var last = from;

        void WalkTo(Point3D p)
        {
            var steps = Steps(last, p);
            tiles += steps;
            unguarded += UnguardedSteps(last, p, steps);
            points.Add(p);
            last = p;
        }

        foreach (var e in edges)
        {
            WalkTo(_edgeFrom[e]);

            if ((_edgeFlags[e] & TeleportFlag) != 0)
            {
                teleports++;
                teleportLegs.Add(points.Count - 1);
                points.Add(_edgeTo[e]);
                last = _edgeTo[e];
            }
            else
            {
                WalkTo(_edgeTo[e]);
            }
        }

        WalkTo(goal);
        return new IdleRoute(tiles, teleports, unguarded, points, teleportLegs);
    }

    /// <summary>Steps of a straight segment outside guard protection, sampled every few tiles.</summary>
    private int UnguardedSteps(Point3D a, Point3D b, int steps)
    {
        const int sample = 4;
        var unguarded = 0;

        for (var s = 0; s < steps; s += sample)
        {
            var t = (s + Math.Min(sample, steps - s) / 2.0) / steps;
            var p = new Point3D(a.X + (int)Math.Round((b.X - a.X) * t), a.Y + (int)Math.Round((b.Y - a.Y) * t), a.Z);
            if (!IdleRegions.IsGuarded(Region.Find(p, Map)))
            {
                unguarded += Math.Min(sample, steps - s);
            }
        }

        return unguarded;
    }

    public void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.tmp";

        using (var file = File.Create(temp))
        using (var zip = new GZipStream(file, CompressionLevel.Optimal))
        using (var w = new BinaryWriter(zip))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(Map.Name);
            w.Write(Generated.ToBinary());

            w.Write(MapFiles.Count);
            foreach (var stamp in MapFiles)
            {
                w.Write(stamp.Map);
                w.Write(stamp.File);
                w.Write(stamp.Size);
            }

            w.Write(CellShift);
            w.Write(_cellsW);
            w.Write(_cellsH);

            w.Write(_cellStart.Length);
            foreach (var s in _cellStart)
            {
                w.Write(s);
            }

            w.Write(NodeCount);
            for (var n = 0; n < NodeCount; n++)
            {
                WritePoint(w, _nodeCenter[n]);
                w.Write(_nodeTiles[n]);
            }

            w.Write(EdgeCount);
            for (var e = 0; e < EdgeCount; e++)
            {
                w.Write(_edgeFromNode[e]);
                w.Write(_edgeToNode[e]);
                WritePoint(w, _edgeFrom[e]);
                WritePoint(w, _edgeTo[e]);
                w.Write(_edgeFlags[e]);
            }

            w.Write(_labels.Count);
            foreach (var (cell, labels) in _labels)
            {
                w.Write(cell);
                w.Write(labels);
                foreach (var z in _labelZ[cell])
                {
                    w.Write(z);
                }
            }
        }

        File.Move(temp, path, true);
    }

    public static IdleRouteMap Read(string path)
    {
        using var file = File.OpenRead(path);
        using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var r = new BinaryReader(zip);

        if (r.ReadInt32() != Magic || r.ReadInt32() != FormatVersion)
        {
            throw new InvalidDataException("Not a route map of this version.");
        }

        var map = Map.Parse(r.ReadString()) ?? throw new InvalidDataException("Unknown map.");
        var generated = DateTime.FromBinary(r.ReadInt64());

        var stamps = new List<IdleMapFileStamp>();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            stamps.Add(new IdleMapFileStamp { Map = r.ReadString(), File = r.ReadString(), Size = r.ReadInt64() });
        }

        if (r.ReadInt32() != CellShift)
        {
            throw new InvalidDataException("Different cell size.");
        }

        var cellsW = r.ReadInt32();
        var cellsH = r.ReadInt32();

        var cellStart = new int[r.ReadInt32()];
        for (var i = 0; i < cellStart.Length; i++)
        {
            cellStart[i] = r.ReadInt32();
        }

        var nodeCount = r.ReadInt32();
        var centers = new Point3D[nodeCount];
        var tiles = new int[nodeCount];
        for (var n = 0; n < nodeCount; n++)
        {
            centers[n] = ReadPoint(r);
            tiles[n] = r.ReadInt32();
        }

        var edgeCount = r.ReadInt32();
        var fromNode = new int[edgeCount];
        var toNode = new int[edgeCount];
        var from = new Point3D[edgeCount];
        var to = new Point3D[edgeCount];
        var flags = new byte[edgeCount];
        for (var e = 0; e < edgeCount; e++)
        {
            fromNode[e] = r.ReadInt32();
            toNode[e] = r.ReadInt32();
            from[e] = ReadPoint(r);
            to[e] = ReadPoint(r);
            flags[e] = r.ReadByte();
        }

        const int cellTiles = CellSize * CellSize;
        var labels = new Dictionary<int, byte[]>();
        var labelZ = new Dictionary<int, sbyte[]>();
        for (var i = r.ReadInt32(); i > 0; i--)
        {
            var cell = r.ReadInt32();
            labels[cell] = r.ReadBytes(cellTiles);
            var z = new sbyte[cellTiles];
            for (var t = 0; t < cellTiles; t++)
            {
                z[t] = r.ReadSByte();
            }

            labelZ[cell] = z;
        }

        return new IdleRouteMap(
            map, generated, stamps, cellsW, cellsH, cellStart, centers, tiles, fromNode, toNode, from, to, flags, labels,
            labelZ
        );
    }

    private static void WritePoint(BinaryWriter w, Point3D p)
    {
        w.Write((short)p.X);
        w.Write((short)p.Y);
        w.Write((sbyte)Math.Clamp(p.Z, -128, 127));
    }

    private static Point3D ReadPoint(BinaryReader r) => new(r.ReadInt16(), r.ReadInt16(), r.ReadSByte());
}

/// <summary>The route maps made by [IdleGenSpots, one file per map, loaded at startup.</summary>
public static class IdleRoutes
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleRoutes));

    private static readonly Dictionary<Map, IdleRouteMap> _maps = new();

    public static string PathFor(Map map) =>
        Path.Combine(Core.BaseDirectory, "Data", "Idle", $"routes.{map.Name.ToLowerInvariant()}.bin");

    public static IdleRouteMap Get(Map map) => map != null && _maps.TryGetValue(map, out var routes) ? routes : null;

    public static void Load()
    {
        _maps.Clear();

        foreach (var map in Map.AllMaps)
        {
            if (map == Map.Internal)
            {
                continue;
            }

            var path = PathFor(map);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var routes = IdleRouteMap.Read(path);
                _maps[map] = routes;
                logger.Information(
                    "Idle route map for {Map}: {Nodes} areas, {Edges} links ({Ms:0} ms)",
                    map, routes.NodeCount, routes.EdgeCount, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds
                );
                WarnIfStale(routes);
            }
            catch (Exception e)
            {
                logger.Error(e, "Failed to read {Path}; no walking routes on {Map}", path, map);
            }
        }
    }

    private static void WarnIfStale(IdleRouteMap routes)
    {
        var current = new List<IdleMapFileStamp>();
        IdleMapFileStamp.Collect(routes.Map, current);

        foreach (var stamp in routes.MapFiles)
        {
            var match = false;
            foreach (var now in current)
            {
                match |= now.File.InsensitiveEquals(stamp.File) && now.Size == stamp.Size;
            }

            if (!match)
            {
                logger.Warning(
                    "The idle route map was made from {Map} file {File} ({Size} bytes), which has changed; re-run [IdleGenSpots",
                    stamp.Map, stamp.File, stamp.Size
                );
            }
        }
    }
}
