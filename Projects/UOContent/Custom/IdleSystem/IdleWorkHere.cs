using System;
using System.Collections.Generic;
using Server.Engines.Craft;
using Server.Mobiles;

namespace Server.Custom.Idle;

public record IdlePointInfo(int X, int Y, int Z)
{
    public static IdlePointInfo From(Point3D p) => new(p.X, p.Y, p.Z);
}

/// <summary>A known spot (hand-written or generated) as seen from a character's position.</summary>
public record IdleSpotInfo(
    string Id,
    string Name,
    IReadOnlyList<string> TaskIds,
    string Map,
    IdlePointInfo Target,
    IdlePointInfo Stand,
    int Distance,
    string Direction,
    string Town,
    int TownDistance,
    string Region,
    // Town, UnguardedTown, Guarded, Wilderness, Dungeon; null for hand-written locations
    string RegionKind,
    int HostileSpawners,
    bool? Reachable,
    bool Generated
);

/// <summary>
/// Result of "work here". Status: Started (the task runs now), TargetNearby (a target is within the search radius but
/// not within reach of where the character stands; Target/Stand say where to go), SpotKnown (nothing within the
/// radius; NearestSpot is the closest known spot), NotFound, or Failed (Message says why, e.g. no tool).
/// </summary>
public record IdleWorkHereInfo(
    string Status,
    string Message,
    string TaskId,
    IdlePointInfo Target,
    IdlePointInfo Stand,
    int Distance,
    string Direction,
    IdleSpotInfo NearestSpot
);

/// <summary>
/// "Work here": starts a task at the character's position if a target is within reach, otherwise points to the
/// nearest target within <see cref="IdleSettings.WorkHereRadius"/>, otherwise to the nearest known spot. Never moves
/// the character. Works for characters in the world and logged out (from where they logged out).
/// </summary>
public static class IdleWorkHere
{
    public const string LocationId = "work-here";

    private const int MaxRadius = 24;

    private static readonly Dictionary<int, (int X, int Y)[]> _offsets = new();

    public static IdleWorkHereInfo Run(PlayerMobile pm, string taskId, IdleOrder order = null)
    {
        var def = IdleSystem.GetTask(taskId);
        var handler = IdleTaskHandler.Get(def?.Kind);

        if (!IdleSystem.Settings.Enabled || def == null || handler == null)
        {
            return Failed(taskId, "That idle task is not available.");
        }

        if (!TryGetPosition(pm, out var map, out var here))
        {
            return Failed(def.Id, "The character is not on a map.");
        }

        var radius = Math.Clamp(IdleSystem.Settings.WorkHereRadius, 0, MaxRadius);

        if (handler is HarvestIdleHandler harvest)
        {
            return Harvest(pm, def, harvest, map, here, radius, order);
        }

        if (handler.UsesStation(def))
        {
            return Station(pm, def, handler, map, here, radius, order);
        }

        // Tasks with no target or station work where the character stands.
        return Start(pm, def, new IdleTarget(IdleTarget.Here, map, here), order);
    }

    /// <summary>Where the character is, or where it logged out.</summary>
    public static bool TryGetPosition(PlayerMobile pm, out Map map, out Point3D location)
    {
        var offline = pm.Map == Map.Internal;
        map = offline ? pm.LogoutMap : pm.Map;
        location = offline ? pm.LogoutLocation : pm.Location;
        return map != null && map != Map.Internal;
    }

    private static IdleWorkHereInfo Harvest(
        PlayerMobile pm, IdleTaskDefinition def, HarvestIdleHandler handler, Map map, Point3D here, int radius,
        IdleOrder order
    )
    {
        var definition = handler.GetDefinition(def);
        var range = handler.GetRange(def);

        // Within reach of where the character stands: start now. Nearest tiles first.
        foreach (var (dx, dy) in Offsets(range))
        {
            var target = TargetAt(definition, map, here.X + dx, here.Y + dy);
            if (HarvestIdleHandler.MatchesVisibleTile(definition, map, target.X, target.Y) &&
                handler.ValidateTarget(pm, def, new IdleTarget(LocationId, map, target), out _))
            {
                return Start(pm, def, new IdleTarget(LocationId, map, target), order);
            }
        }

        // Farther out: the nearest target with somewhere to stand, measured to where the character would stand.
        IdleWorkHereInfo best = null;
        var bestDistance = int.MaxValue;
        var bestCutoff = int.MaxValue;

        foreach (var (dx, dy) in Offsets(radius))
        {
            // Offsets are sorted by distance; a stand is less than 2 * range closer than its target (diagonal reach).
            var ring = (int)Math.Sqrt(dx * dx + dy * dy);
            if (ring - 2 * range > bestCutoff)
            {
                break;
            }

            if (Math.Max(Math.Abs(dx), Math.Abs(dy)) <= range)
            {
                continue;
            }

            var target = TargetAt(definition, map, here.X + dx, here.Y + dy);
            if (!HarvestIdleHandler.MatchesVisibleTile(definition, map, target.X, target.Y) ||
                !handler.ValidateTarget(pm, def, new IdleTarget(LocationId, map, target), out _) ||
                !IdleStandFinder.TryFind(map, target, range, here, null, out var stand))
            {
                continue;
            }

            var distance = IdleSpotIndex.Distance(here, stand);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestCutoff = distance;
                best = Nearby(def, target, stand, here);
            }
        }

        return best ?? Fallback(def, map, here, radius);
    }

    private static Point3D TargetAt(Engines.Harvest.HarvestDefinition definition, Map map, int x, int y) =>
        new(x, y, HarvestIdleHandler.TargetZ(definition, map, x, y));

    private static IdleWorkHereInfo Station(
        PlayerMobile pm, IdleTaskDefinition def, IdleTaskHandler handler, Map map, Point3D here, int radius,
        IdleOrder order
    )
    {
        if (handler.CheckStation(pm, def, map, here, out _))
        {
            return Start(pm, def, new IdleTarget(IdleTarget.Here, map, here), order);
        }

        // Forges within the radius (plus reach), then the nearest tile beside one that passes the task's station check.
        var reach = radius + SmithyStations.Range;
        var forges = new List<Point3D>();

        foreach (var item in map.GetItemsInRange(here, reach))
        {
            if (item.GetType().IsDefined(typeof(ForgeAttribute), false) || SmithyStations.IsForge(item.ItemID))
            {
                forges.Add(item.Location);
            }
        }

        for (var dx = -reach; dx <= reach; dx++)
        {
            for (var dy = -reach; dy <= reach; dy++)
            {
                foreach (var tile in map.Tiles.GetStaticAndMultiTiles(here.X + dx, here.Y + dy))
                {
                    if (SmithyStations.IsForge(tile.ID))
                    {
                        forges.Add(new Point3D(here.X + dx, here.Y + dy, tile.Z));
                    }
                }
            }
        }

        IdleWorkHereInfo best = null;
        var bestDistance = int.MaxValue;

        foreach (var forge in forges)
        {
            for (var dx = -SmithyStations.Range; dx <= SmithyStations.Range; dx++)
            {
                for (var dy = -SmithyStations.Range; dy <= SmithyStations.Range; dy++)
                {
                    var x = forge.X + dx;
                    var y = forge.Y + dy;

                    // The floor the forge stands on, not a level above or below it.
                    if (!map.CanSpawnMobile(x, y, forge.Z - 8, forge.Z + 12, false, false, out var z))
                    {
                        continue;
                    }

                    var stand = new Point3D(x, y, z);
                    var distance = IdleSpotIndex.Distance(here, stand);

                    if (distance < bestDistance && distance <= radius && handler.CheckStation(null, def, map, stand, out _))
                    {
                        bestDistance = distance;
                        best = Nearby(def, forge, stand, here);
                    }
                }
            }
        }

        return best ?? Fallback(def, map, here, radius);
    }

    private static IdleWorkHereInfo Start(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, IdleOrder order)
    {
        if (!IdleSystem.TryStartTask(pm, def.Id, target, out var error, order))
        {
            return Failed(def.Id, error);
        }

        var stand = IdlePointInfo.From(pm.Map == Map.Internal ? pm.LogoutLocation : pm.Location);
        return new IdleWorkHereInfo(
            "Started", $"{def.Name} started here.", def.Id, IdlePointInfo.From(target.Location), stand, 0, "here", null
        );
    }

    private static IdleWorkHereInfo Nearby(IdleTaskDefinition def, Point3D target, Point3D stand, Point3D here)
    {
        var distance = IdleSpotIndex.Distance(here, stand);
        var direction = DirectionName(here, stand);
        return new IdleWorkHereInfo(
            "TargetNearby",
            $"Nothing to work within reach. The nearest {def.Name.ToLowerInvariant()} spot is {distance} tiles {direction}: " +
            $"stand at {stand.X}, {stand.Y} and try again.",
            def.Id, IdlePointInfo.From(target), IdlePointInfo.From(stand), distance, direction, null
        );
    }

    private static IdleWorkHereInfo Fallback(IdleTaskDefinition def, Map map, Point3D here, int radius)
    {
        var results = new List<(IdleLocation Location, int Distance)>(1);
        IdleSpotIndex.FindNearest(map, here, def.Id, 1, 0, results);

        if (results.Count == 0)
        {
            return new IdleWorkHereInfo(
                "NotFound", $"No {def.Name.ToLowerInvariant()} spot within {radius} tiles, and no known spot on this map.",
                def.Id, null, null, 0, null, null
            );
        }

        var spot = ToInfo(results[0].Location, results[0].Distance, here);
        return new IdleWorkHereInfo(
            "SpotKnown",
            $"No {def.Name.ToLowerInvariant()} spot within {radius} tiles. Nearest known spot: {spot.Name} ({spot.Id}), " +
            $"{spot.Distance} tiles {spot.Direction}. Go there and use [Idle at {spot.Id}.",
            def.Id, null, null, spot.Distance, spot.Direction, spot
        );
    }

    private static IdleWorkHereInfo Failed(string taskId, string message) =>
        new("Failed", message ?? "That cannot be done here.", taskId, null, null, 0, null, null);

    /// <summary>Up to <paramref name="max"/> known spots for the task, nearest first.</summary>
    public static List<IdleSpotInfo> NearestSpots(Map map, Point3D from, string taskId, int max)
    {
        var results = new List<(IdleLocation Location, int Distance)>();
        IdleSpotIndex.FindNearest(map, from, taskId, Math.Clamp(max, 1, 50), 0, results);

        var spots = new List<IdleSpotInfo>(results.Count);
        foreach (var (loc, distance) in results)
        {
            spots.Add(ToInfo(loc, distance, from));
        }

        return spots;
    }

    public static IdleSpotInfo ToInfo(IdleLocation loc, int distance, Point3D from)
    {
        var stand = IdleSpotIndex.StandPoint(loc);
        var tasks = new List<string>(loc.AllTaskIds());
        var spot = loc as IdleSpot;

        return new IdleSpotInfo(
            loc.Id, loc.Name, tasks, loc.Map?.Name, IdlePointInfo.From(loc.Location), IdlePointInfo.From(stand), distance,
            DirectionName(from, stand), spot?.Town, spot?.TownDistance ?? 0, spot?.Region, spot?.RegionKind,
            spot?.HostileSpawners ?? 0, spot?.Reachable, spot?.Generated == true
        );
    }

    public static string DirectionName(Point3D from, Point3D to)
    {
        if (from.X == to.X && from.Y == to.Y)
        {
            return "here";
        }

        return (Utility.GetDirection(from, to) & Direction.Mask) switch
        {
            Direction.North => "north",
            Direction.Right => "northeast",
            Direction.East  => "east",
            Direction.Down  => "southeast",
            Direction.South => "south",
            Direction.Left  => "southwest",
            Direction.West  => "west",
            _               => "northwest"
        };
    }

    /// <summary>Offsets within a square of the given radius, nearest to the center first. Cached per radius.</summary>
    internal static (int X, int Y)[] Offsets(int radius)
    {
        if (_offsets.TryGetValue(radius, out var offsets))
        {
            return offsets;
        }

        var side = radius * 2 + 1;
        offsets = new (int X, int Y)[side * side];
        var i = 0;

        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                offsets[i++] = (dx, dy);
            }
        }

        Array.Sort(offsets, (a, b) => (a.X * a.X + a.Y * a.Y).CompareTo(b.X * b.X + b.Y * b.Y));
        return _offsets[radius] = offsets;
    }
}
