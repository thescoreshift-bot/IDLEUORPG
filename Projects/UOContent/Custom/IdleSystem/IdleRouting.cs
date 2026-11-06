using System;
using System.Collections.Generic;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>A walking route to one spot. Distances are estimates from the coarse route map.</summary>
public record IdleRouteInfo(
    IdleSpotInfo Spot,
    int WalkTiles,
    double EtaSeconds,
    int Teleports,
    // Tiles of the route outside guard protection.
    int UnguardedTiles,
    IReadOnlyList<IdlePointInfo> Waypoints
);

/// <summary>
/// Status: Ok (Routes is nearest on foot first), NoRouteMap (no route map for the map yet), NoSpots (no known spot
/// for the task), NoRoute (spots exist but none can be walked to from here), or Failed (Message says why).
/// </summary>
public record IdleRoutesInfo(string Status, string Message, string TaskId, IReadOnlyList<IdleRouteInfo> Routes);

/// <summary>Walking routes from a character (or where it logged out) to the known spots of a task.</summary>
public static class IdleRouting
{
    /// <summary>Spots nearest in a straight line that are routed and compared on foot.</summary>
    private const int Candidates = 20;

    public static IdleRoutesInfo Find(PlayerMobile pm, string taskId, int max = 5)
    {
        var def = IdleSystem.GetTask(taskId);
        if (def == null)
        {
            return new IdleRoutesInfo("Failed", "That idle task is not available.", taskId, []);
        }

        if (!IdleWorkHere.TryGetPosition(pm, out var map, out var from))
        {
            return new IdleRoutesInfo("Failed", "The character is not on a map.", def.Id, []);
        }

        var routeMap = IdleRoutes.Get(map);
        if (routeMap == null)
        {
            return new IdleRoutesInfo("NoRouteMap", $"Walking routes are not available on {map} yet.", def.Id, []);
        }

        var nearest = new List<(IdleLocation Location, int Distance)>();
        IdleSpotIndex.FindNearest(map, from, def.Id, Candidates, 0, nearest);

        if (nearest.Count == 0)
        {
            return new IdleRoutesInfo("NoSpots", $"No known {def.Name} spots on {map}.", def.Id, []);
        }

        // The walk never reached these; a route would only lead to the nearest walkable area beside them.
        nearest.RemoveAll(n => n.Location is IdleSpot { Reachable: false });

        if (nearest.Count == 0)
        {
            return new IdleRoutesInfo("NoRoute", $"None of the nearest {def.Name} spots can be walked to.", def.Id, []);
        }

        var goals = new List<Point3D>(nearest.Count);
        foreach (var (loc, _) in nearest)
        {
            goals.Add(IdleSpotIndex.StandPoint(loc));
        }

        // Spots more than twice as far on foot as the nearest are not worth listing, and searching for them can cover
        // half the map when a task has few spots.
        var routes = routeMap.FindRoutes(from, goals, 0, 2);
        var found = new List<IdleRouteInfo>();

        for (var i = 0; i < routes.Length; i++)
        {
            if (routes[i] is not { } route)
            {
                continue;
            }

            var waypoints = new List<IdlePointInfo>(route.Waypoints.Count);
            foreach (var p in route.Waypoints)
            {
                waypoints.Add(IdlePointInfo.From(p));
            }

            found.Add(
                new IdleRouteInfo(
                    IdleWorkHere.ToInfo(nearest[i].Location, nearest[i].Distance, from), route.Tiles, route.EtaSeconds,
                    route.Teleports, route.UnguardedTiles, waypoints
                )
            );
        }

        if (found.Count == 0)
        {
            return new IdleRoutesInfo(
                "NoRoute", $"None of the {nearest.Count} nearest {def.Name} spots can be walked to from here.", def.Id, []
            );
        }

        found.Sort((a, b) => a.WalkTiles.CompareTo(b.WalkTiles));
        if (found.Count > max)
        {
            found.RemoveRange(Math.Max(1, max), found.Count - Math.Max(1, max));
        }

        return new IdleRoutesInfo("Ok", null, def.Id, found);
    }

    /// <summary>"40 s", "3 min 5 s", "1 h 12 min".</summary>
    public static string FormatEta(double seconds)
    {
        var s = (int)Math.Round(seconds);
        if (s < 60)
        {
            return $"{s} s";
        }

        return s < 3600 ? $"{s / 60} min {s % 60} s" : $"{s / 3600} h {s % 3600 / 60} min";
    }
}
