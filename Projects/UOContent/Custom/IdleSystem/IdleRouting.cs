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
    // Safest first: Safe (guarded spot, guarded all the way), UnguardedRoute (guarded spot, part of the way
    // unguarded), UnguardedSpot (the spot is outside guard zones), HostileSpot (hostile spawners near the spot).
    string Risk,
    IReadOnlyList<IdlePointInfo> Waypoints,
    // Each index i where stepping onto waypoint i is a teleporter that lands on waypoint i + 1.
    IReadOnlyList<int> TeleportLegs
);

/// <summary>
/// Status: Ok (Routes is nearest on foot first), NoRouteMap (no route map for the map yet), NoSpots (no known spot
/// for the task), NoRoute (spots exist but none can be walked to from here), or Failed (Message says why).
/// </summary>
public record IdleRoutesInfo(string Status, string Message, string TaskId, IReadOnlyList<IdleRouteInfo> Routes);

/// <summary>
/// The spot a character would walk to for a task. Status as in <see cref="IdleRoutesInfo"/>. Reason: Nearest (the
/// nearest on foot), SaferSpot (a safer spot up to <see cref="IdleTravelSettings.SafeDetourTiles"/> farther), or
/// SaferRoute (as safe a spot, with fewer unguarded tiles on the way). Message explains it in words. DetourTiles is
/// how much farther the pick is than the nearest on foot. Alternatives are the other spots, nearest on foot first.
/// </summary>
public record IdleSpotPickInfo(
    string Status,
    string Message,
    string TaskId,
    IdleRouteInfo Pick,
    string Reason,
    int DetourTiles,
    IReadOnlyList<IdleRouteInfo> Alternatives
);

/// <summary>Walking routes from a character (or where it logged out) to the known spots of a task.</summary>
public static class IdleRouting
{
    /// <summary>Spots nearest in a straight line that are routed and compared on foot.</summary>
    private const int Candidates = 20;

    private static readonly string[] _risks = ["Safe", "UnguardedRoute", "UnguardedSpot", "HostileSpot"];

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

            var spot = IdleWorkHere.ToInfo(nearest[i].Location, nearest[i].Distance, from);
            found.Add(
                new IdleRouteInfo(
                    spot, route.Tiles, route.EtaSeconds, route.Teleports, route.UnguardedTiles,
                    _risks[RiskOf(spot, goals[i], map, route.UnguardedTiles)], waypoints, route.TeleportLegs
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

    /// <summary>
    /// The spot to walk to: the nearest on foot, unless a safer one is at most
    /// <see cref="IdleTravelSettings.SafeDetourTiles"/> farther. Safer means a lower <see cref="IdleRouteInfo.Risk"/>,
    /// or the same risk with more unguarded tiles saved than extra tiles walked.
    /// </summary>
    public static IdleSpotPickInfo Pick(PlayerMobile pm, string taskId, int alternatives = 4)
    {
        var info = Find(pm, taskId, Candidates);
        if (info.Status != "Ok")
        {
            return new IdleSpotPickInfo(info.Status, info.Message, info.TaskId, null, null, 0, []);
        }

        var routes = info.Routes;
        var nearest = routes[0];
        var detour = Math.Max(0, IdleSystem.Settings.Travel?.SafeDetourTiles ?? 0);
        var best = nearest;

        // Routes are nearest on foot first, so the first safer one found at each level is the shortest.
        for (var i = 1; i < routes.Count && routes[i].WalkTiles <= nearest.WalkTiles + detour; i++)
        {
            var r = routes[i];
            var rank = Rank(r);
            var bestRank = Rank(best);

            if (rank < bestRank ||
                rank == bestRank && best.UnguardedTiles - r.UnguardedTiles > r.WalkTiles - best.WalkTiles)
            {
                best = r;
            }
        }

        var others = new List<IdleRouteInfo>(Math.Min(alternatives, routes.Count));
        foreach (var r in routes)
        {
            if (r != best && others.Count < alternatives)
            {
                others.Add(r);
            }
        }

        var extra = best.WalkTiles - nearest.WalkTiles;

        if (best == nearest)
        {
            var message = nearest.Risk == "Safe" || detour == 0 || routes.Count == 1
                ? "The nearest on foot."
                : $"The nearest on foot; no safer spot within {detour} more tiles.";
            return new IdleSpotPickInfo("Ok", message, info.TaskId, best, "Nearest", 0, others);
        }

        var farther = $"{Tiles(extra)} farther than the nearest spot ({nearest.Spot.Name})";

        if (Rank(best) < Rank(nearest))
        {
            var why = nearest.Risk switch
            {
                "HostileSpot"   => "has hostile spawners nearby",
                "UnguardedSpot" => "is outside guard zones",
                _               => $"has {Tiles(nearest.UnguardedTiles)} unguarded on the way"
            };
            return new IdleSpotPickInfo(
                "Ok", $"{farther}, which {why}.", info.TaskId, best, "SaferSpot", extra, others
            );
        }

        var saved = nearest.UnguardedTiles - best.UnguardedTiles;
        return new IdleSpotPickInfo(
            "Ok", $"{farther}, with {saved} fewer unguarded tile{(saved == 1 ? "" : "s")} on the way.",
            info.TaskId, best, "SaferRoute", extra, others
        );
    }

    private static string Tiles(int n) => n == 1 ? "1 tile" : $"{n} tiles";

    private static int Rank(IdleRouteInfo route) => Array.IndexOf(_risks, route.Risk);

    private static int RiskOf(IdleSpotInfo spot, Point3D stand, Map map, int unguardedTiles)
    {
        if (spot.HostileSpawners > 0)
        {
            return 3;
        }

        // Hand-written locations carry no region kind.
        var kind = spot.RegionKind ?? IdleRegions.KindOf(Region.Find(stand, map));
        if (kind is not ("Town" or "Guarded"))
        {
            return 2;
        }

        return unguardedTiles > 0 ? 1 : 0;
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
