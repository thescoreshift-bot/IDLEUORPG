using System;
using System.Collections.Generic;

namespace Server.Custom.Idle;

/// <summary>
/// Collects the route map while [IdleGenSpots walks the map. Every newly reached position joins the area (node) of
/// the position it was reached from when both are in the same cell, else starts a new area. Areas of one cell that
/// turn out to touch are merged; a step into another cell's area becomes a link. The result is the same set of
/// positions as the walk itself, grouped into areas and links.
/// </summary>
internal sealed class IdleRouteBuilder
{
    private const int CellShift = IdleRouteMap.CellShift;
    private const int CellSize = IdleRouteMap.CellSize;

    private readonly Map _map;
    private readonly int _width;
    private readonly int _cellsW;
    private readonly int _cellsH;

    // Per tile: the area of the ground-floor position, as an index among its cell's areas (1-based, 0 = none).
    private readonly byte[] _local;
    private readonly byte[] _cellAreas;
    private readonly Dictionary<int, int> _localToNode = new();

    private readonly List<NodeAcc> _nodes = [];
    // Keyed by (from, to): a long packed as from << 32 | to would hash to from ^ to, which collides for neighbours.
    private readonly Dictionary<(int From, int To), EdgeAcc> _edges = new();

    private struct NodeAcc
    {
        public int Cell;
        public byte Local;
        public int Parent;
        public long SumX;
        public long SumY;
        public long SumZ;
        public int Count;
    }

    private struct EdgeAcc
    {
        public Point3D From;
        public Point3D To;
        public int Score;
        public bool Teleport;
    }

    public IdleRouteBuilder(Map map)
    {
        _map = map;
        _width = map.Width;
        _cellsW = (map.Width + CellSize - 1) >> CellShift;
        _cellsH = (map.Height + CellSize - 1) >> CellShift;
        _local = new byte[map.Width * map.Height];
        _cellAreas = new byte[_cellsW * _cellsH];
    }

    public int Areas => _nodes.Count;

    private int CellOf(int x, int y) => (y >> CellShift) * _cellsW + (x >> CellShift);

    /// <summary>The area a newly reached position belongs to: the one it was reached from if in the same cell.</summary>
    public int Claim(int x, int y, int z, int fromNode, Point3D from, bool teleport)
    {
        var cell = CellOf(x, y);

        if (fromNode >= 0 && !teleport && CellOf(from.X, from.Y) == cell)
        {
            return fromNode;
        }

        int node;
        var local = _cellAreas[cell];

        // A cell with 255 areas already is a maze of single tiles; the rest share the last area.
        if (local == byte.MaxValue)
        {
            node = _localToNode[(cell << 8) | local];
        }
        else
        {
            _cellAreas[cell] = ++local;
            node = _nodes.Count;
            _nodes.Add(new NodeAcc { Cell = cell, Local = local, Parent = node });
            _localToNode[(cell << 8) | local] = node;
        }

        Link(fromNode, node, from, new Point3D(x, y, z), teleport);
        return node;
    }

    public void SetGround(int index, int node) => _local[index] = _nodes[node].Local;

    public int GroundNode(int index, int x, int y) => _localToNode[(CellOf(x, y) << 8) | _local[index]];

    public void Add(int node, int x, int y, int z)
    {
        // The area may have merged into another since this position's neighbour claimed it.
        node = Find(node);
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        ref var acc = ref span[node];
        acc.SumX += x;
        acc.SumY += y;
        acc.SumZ += z;
        acc.Count++;
    }

    /// <summary>A step (or teleporter) from area <paramref name="a"/> into an already reached area <paramref name="b"/>.</summary>
    public void Link(int a, int b, Point3D from, Point3D to, bool teleport)
    {
        if (a < 0)
        {
            return;
        }

        var ra = Find(a);
        var rb = Find(b);

        if (ra == rb)
        {
            return;
        }

        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);

        if (!teleport && span[ra].Cell == span[rb].Cell)
        {
            Union(ra, rb);
            return;
        }

        // The crossing nearest the middle of the shared border makes the straightest route.
        var ca = span[ra].Cell;
        var cb = span[rb].Cell;
        var midX = (ca % _cellsW + cb % _cellsW) * CellSize + CellSize;
        var midY = (ca / _cellsW + cb / _cellsW) * CellSize + CellSize;
        var score = teleport ? 0 : Math.Max(Math.Abs(from.X * 2 - midX), Math.Abs(from.Y * 2 - midY));

        var key = (ra, rb);
        if (!_edges.TryGetValue(key, out var edge) || score < edge.Score || teleport && !edge.Teleport)
        {
            _edges[key] = new EdgeAcc { From = from, To = to, Score = score, Teleport = teleport };
        }
    }

    private int Find(int node)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        while (span[node].Parent != node)
        {
            span[node].Parent = span[span[node].Parent].Parent;
            node = span[node].Parent;
        }

        return node;
    }

    private void Union(int a, int b)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nodes);
        if (span[a].Count < span[b].Count)
        {
            (a, b) = (b, a);
        }

        span[b].Parent = a;
        span[a].SumX += span[b].SumX;
        span[a].SumY += span[b].SumY;
        span[a].SumZ += span[b].SumZ;
        span[a].Count += span[b].Count;
    }

    /// <summary>
    /// Turns the collected areas and links into a route map, a slice at a time. <paramref name="floor"/> is the walk's
    /// per-tile floor height (offset by 128, 0 = not reached). The map is in <see cref="Result"/> afterwards.
    /// </summary>
    public IEnumerable<bool> Build(byte[] floor, List<IdleMapFileStamp> stamps)
    {
        // Pause every this many items; each is a few dictionary or array operations.
        const int slice = 0x3FFF;

        // Merged areas collapse into their root; roots are numbered in cell order so a cell's areas are contiguous.
        var rootKeys = new List<long>();
        for (var i = 0; i < _nodes.Count; i++)
        {
            if (Find(i) == i)
            {
                rootKeys.Add(((long)_nodes[i].Cell << 32) | (uint)i);
            }

            if ((i & slice) == slice)
            {
                yield return true;
            }
        }

        rootKeys.Sort();
        yield return true;

        var dense = new int[_nodes.Count];
        var cellStart = new int[_cellsW * _cellsH + 1];
        var centers = new Point3D[rootKeys.Count];
        var tiles = new int[rootKeys.Count];

        for (var i = 0; i < rootKeys.Count; i++)
        {
            var root = (int)(uint)rootKeys[i];
            var acc = _nodes[root];
            dense[root] = i;
            cellStart[acc.Cell + 1]++;
            centers[i] = new Point3D((int)(acc.SumX / acc.Count), (int)(acc.SumY / acc.Count), (int)(acc.SumZ / acc.Count));
            tiles[i] = acc.Count;

            if ((i & slice) == slice)
            {
                yield return true;
            }
        }

        for (var c = 0; c < _cellsW * _cellsH; c++)
        {
            cellStart[c + 1] += cellStart[c];
        }

        for (var i = 0; i < _nodes.Count; i++)
        {
            dense[i] = dense[Find(i)];

            if ((i & slice) == slice)
            {
                yield return true;
            }
        }

        // One link per pair of areas, keeping the best crossing.
        var links = new Dictionary<(int From, int To), EdgeAcc>();
        var n = 0;
        foreach (var (key, edge) in _edges)
        {
            var a = dense[key.From];
            var b = dense[key.To];

            if (a != b)
            {
                var denseKey = (a, b);
                if (!links.TryGetValue(denseKey, out var best) || !edge.Teleport && (best.Teleport || edge.Score < best.Score))
                {
                    links[denseKey] = edge;
                }
            }

            if ((++n & slice) == 0)
            {
                yield return true;
            }
        }

        _edges.Clear();
        _edges.TrimExcess();

        var keys = new (int From, int To)[links.Count];
        links.Keys.CopyTo(keys, 0);
        Array.Sort(keys);
        yield return true;

        var fromNode = new int[keys.Length];
        var toNode = new int[keys.Length];
        var from = new Point3D[keys.Length];
        var to = new Point3D[keys.Length];
        var flags = new byte[keys.Length];

        for (var e = 0; e < keys.Length; e++)
        {
            var edge = links[keys[e]];
            fromNode[e] = keys[e].From;
            toNode[e] = keys[e].To;
            from[e] = edge.From;
            to[e] = edge.To;
            flags[e] = edge.Teleport ? IdleRouteMap.TeleportFlag : (byte)0;

            if ((e & slice) == slice)
            {
                yield return true;
            }
        }

        // Cells split into several areas keep a per-tile area map, so a position finds the right side of a wall.
        var labels = new Dictionary<int, byte[]>();
        var labelZ = new Dictionary<int, sbyte[]>();
        var work = 0;

        for (var cell = 0; cell < _cellsW * _cellsH; cell++)
        {
            var first = cellStart[cell];
            if (cellStart[cell + 1] - first < 2)
            {
                if ((++work & slice) == 0)
                {
                    yield return true;
                }

                continue;
            }

            var cellLabels = new byte[CellSize * CellSize];
            var cellZ = new sbyte[CellSize * CellSize];
            var x0 = cell % _cellsW << CellShift;
            var y0 = cell / _cellsW << CellShift;

            for (var dy = 0; dy < CellSize; dy++)
            {
                for (var dx = 0; dx < CellSize; dx++)
                {
                    var x = x0 + dx;
                    var y = y0 + dy;
                    if (x >= _map.Width || y >= _map.Height)
                    {
                        continue;
                    }

                    var index = y * _width + x;
                    var local = _local[index];
                    if (local == 0 || floor[index] == 0)
                    {
                        continue;
                    }

                    var t = (dy << CellShift) | dx;
                    cellLabels[t] = (byte)(dense[_localToNode[(cell << 8) | local]] - first + 1);
                    cellZ[t] = (sbyte)(floor[index] - 128);
                }
            }

            labels[cell] = cellLabels;
            labelZ[cell] = cellZ;

            // A split cell costs about as much as 256 plain items.
            work += CellSize * CellSize;
            if (work > slice)
            {
                work = 0;
                yield return true;
            }
        }

        Result = new IdleRouteMap(
            _map, Core.Now, stamps, _cellsW, _cellsH, cellStart, centers, tiles, fromNode, toNode, from, to, flags, labels,
            labelZ
        );
    }

    public IdleRouteMap Result { get; private set; }
}
