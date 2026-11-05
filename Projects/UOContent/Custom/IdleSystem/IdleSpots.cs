using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using Server.Json;
using Server.Logging;

namespace Server.Custom.Idle;

/// <summary>
/// A location found by the spot generator. Behaves like a hand-written <see cref="IdleLocation"/> (it can be started
/// with [Idle at), plus what the generator learned about it, for clients to show distance and risk.
/// </summary>
public class IdleSpot : IdleLocation
{
    /// <summary>A standable tile within the task's range of <see cref="IdleLocation.Location"/>.</summary>
    public Point3D? Stand { get; set; }

    public string Town { get; set; }

    /// <summary>Straight-line tiles from the town's center.</summary>
    public int TownDistance { get; set; }

    public string Region { get; set; }

    /// <summary>Town, Guarded, Wilderness, or Dungeon.</summary>
    public string RegionKind { get; set; }

    /// <summary>Spawners of hostile creatures near the spot.</summary>
    public int HostileSpawners { get; set; }

    /// <summary>
    /// Walkable from a starting city, moongate, or teleporter network. Null when the reachability pass did not run.
    /// </summary>
    public bool? Reachable { get; set; }

    /// <summary>Harvestable tiles within range of the standing tile; more means less walking between targets.</summary>
    public int Targets { get; set; }

    [JsonIgnore]
    public bool Generated { get; set; }
}

/// <summary>Contents of <c>Data/Idle/spots.generated.json</c>.</summary>
public class IdleSpotFile
{
    public int Version { get; set; } = 1;
    public DateTime Generated { get; set; }

    /// <summary>Map files the scan read; a mismatch at startup means the spots may no longer fit the map.</summary>
    public List<IdleMapFileStamp> MapFiles { get; set; } = new();

    public List<IdleSpot> Spots { get; set; } = new();
}

public class IdleMapFileStamp
{
    public string Map { get; set; }
    public string File { get; set; }
    public long Size { get; set; }

    public static void Collect(Map map, List<IdleMapFileStamp> stamps)
    {
        var tiles = map?.Tiles;
        if (tiles == null)
        {
            return;
        }

        foreach (var stream in (ReadOnlySpan<FileStream>)[tiles.MapStream, tiles.IndexStream, tiles.DataStream])
        {
            if (stream != null)
            {
                stamps.Add(new IdleMapFileStamp { Map = map.Name, File = Path.GetFileName(stream.Name), Size = stream.Length });
            }
        }
    }
}

/// <summary>
/// Nearest-spot lookups over every loaded location (hand-written and generated), bucketed by task and map in a
/// coarse grid so a query only looks at nearby cells.
/// </summary>
public static class IdleSpotIndex
{
    private const int CellShift = 6;
    private const int CellSize = 1 << CellShift;

    private static readonly Dictionary<(Map Map, string TaskId), Dictionary<int, List<IdleLocation>>> _cells = new();

    public static void Clear() => _cells.Clear();

    public static void Add(IdleLocation loc)
    {
        var p = StandPoint(loc);
        var key = CellKey(p.X >> CellShift, p.Y >> CellShift);

        foreach (var taskId in loc.AllTaskIds())
        {
            var mapKey = (loc.Map, taskId.ToLowerInvariant());
            if (!_cells.TryGetValue(mapKey, out var cells))
            {
                _cells[mapKey] = cells = new Dictionary<int, List<IdleLocation>>();
            }

            if (!cells.TryGetValue(key, out var list))
            {
                cells[key] = list = new List<IdleLocation>();
            }

            list.Add(loc);
        }
    }

    /// <summary>Where a character stands to work the location: the generator's standing tile, or the location itself.</summary>
    public static Point3D StandPoint(IdleLocation loc) => loc is IdleSpot { Stand: { } stand } ? stand : loc.Location;

    public static int Distance(Point3D a, Point3D b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (int)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }

    /// <summary>
    /// Fills <paramref name="results"/> with up to <paramref name="max"/> locations for the task, nearest first by
    /// straight-line distance to where one stands. <paramref name="maxDistance"/> 0 = no limit.
    /// </summary>
    public static void FindNearest(
        Map map, Point3D from, string taskId, int max, int maxDistance, List<(IdleLocation Location, int Distance)> results
    )
    {
        results.Clear();

        if (map == null || taskId == null || max <= 0 || !_cells.TryGetValue((map, taskId.ToLowerInvariant()), out var cells))
        {
            return;
        }

        var cx = from.X >> CellShift;
        var cy = from.Y >> CellShift;
        var maxRing = Math.Max(map.Width, map.Height) >> CellShift;

        for (var ring = 0; ring <= maxRing; ring++)
        {
            // Everything in this ring or beyond is at least (ring - 1) cells away.
            var ringFloor = (ring - 1) * CellSize;
            if (results.Count >= max && results[^1].Distance <= ringFloor || maxDistance > 0 && ringFloor > maxDistance)
            {
                break;
            }

            for (var x = cx - ring; x <= cx + ring; x++)
            {
                for (var y = cy - ring; y <= cy + ring; y++)
                {
                    if (Math.Max(Math.Abs(x - cx), Math.Abs(y - cy)) != ring || !cells.TryGetValue(CellKey(x, y), out var list))
                    {
                        continue;
                    }

                    foreach (var loc in list)
                    {
                        var d = Distance(from, StandPoint(loc));
                        if (maxDistance <= 0 || d <= maxDistance)
                        {
                            Insert(results, loc, d, max);
                        }
                    }
                }
            }
        }
    }

    private static void Insert(List<(IdleLocation Location, int Distance)> results, IdleLocation loc, int distance, int max)
    {
        var i = results.Count;
        while (i > 0 && results[i - 1].Distance > distance)
        {
            i--;
        }

        if (i >= max)
        {
            return;
        }

        results.Insert(i, (loc, distance));
        if (results.Count > max)
        {
            results.RemoveAt(results.Count - 1);
        }
    }

    private static int CellKey(int x, int y) => (x << 16) | (y & 0xFFFF);
}

public static class IdleSpotLoader
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleSpotLoader));

    public static readonly string GeneratedPath = Path.Combine(Core.BaseDirectory, "Data/Idle/spots.generated.json");

    /// <summary>
    /// Adds the generated spots to <paramref name="locations"/>. Hand-written locations already there keep their id.
    /// Returns how many spots were added.
    /// </summary>
    public static int Load(Dictionary<string, IdleLocation> locations, Func<string, bool> taskExists)
    {
        if (!File.Exists(GeneratedPath))
        {
            return 0;
        }

        IdleSpotFile file;
        try
        {
            file = JsonConfig.Deserialize<IdleSpotFile>(GeneratedPath);
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to read {Path}; no generated idle spots loaded", GeneratedPath);
            return 0;
        }

        if (file?.Spots == null)
        {
            return 0;
        }

        WarnIfStale(file);

        var added = 0;
        var skipped = 0;

        foreach (var spot in file.Spots)
        {
            var hasTask = false;
            foreach (var taskId in spot.AllTaskIds())
            {
                hasTask |= taskExists(taskId);
            }

            if (string.IsNullOrWhiteSpace(spot.Id) || spot.Map == null || !hasTask || locations.ContainsKey(spot.Id))
            {
                skipped++;
                continue;
            }

            spot.Generated = true;
            locations[spot.Id] = spot;
            added++;
        }

        if (skipped > 0)
        {
            logger.Information(
                "Generated idle spots: {Skipped} skipped (id taken by a hand-written location, or task not loaded)",
                skipped
            );
        }

        return added;
    }

    private static void WarnIfStale(IdleSpotFile file)
    {
        var current = new List<IdleMapFileStamp>();
        var checkedMaps = new HashSet<string>();

        foreach (var stamp in file.MapFiles)
        {
            if (stamp.Map != null && checkedMaps.Add(stamp.Map))
            {
                IdleMapFileStamp.Collect(Map.Parse(stamp.Map), current);
            }
        }

        foreach (var stamp in file.MapFiles)
        {
            var match = false;
            foreach (var now in current)
            {
                match |= now.Map == stamp.Map && now.File.InsensitiveEquals(stamp.File) && now.Size == stamp.Size;
            }

            if (!match)
            {
                logger.Warning(
                    "Generated idle spots were made from {Map} file {File} ({Size} bytes), which has changed; re-run [IdleGenSpots",
                    stamp.Map, stamp.File, stamp.Size
                );
            }
        }
    }
}
