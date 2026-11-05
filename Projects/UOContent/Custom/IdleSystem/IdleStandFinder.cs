using System;

namespace Server.Custom.Idle;

/// <summary>
/// Finds a tile a character can stand on near a work target, with the server's own spawn-fit rules (surfaces, height
/// clearance, items in the way). Used by the spot generator and by "work here".
/// </summary>
public static class IdleStandFinder
{
    /// <summary>Height window tried first around the target, then the wider one (cliffs, mountainsides).</summary>
    private const int NarrowZ = 16;
    private const int WideZ = 40;

    /// <summary>
    /// Looks for a standable tile within <paramref name="range"/> of <paramref name="target"/>, nearest to
    /// <paramref name="near"/> (or to the target when null). <paramref name="allowed"/> can veto tiles (excluded areas).
    /// </summary>
    public static bool TryFind(
        Map map, Point3D target, int range, Point3D? near, Func<int, int, bool> allowed, out Point3D stand
    ) =>
        TryFind(map, target, range, near, target.Z - NarrowZ, target.Z + NarrowZ, allowed, out stand) ||
        TryFind(map, target, range, near, target.Z - WideZ, target.Z + WideZ, allowed, out stand);

    /// <summary>As <see cref="TryFind(Map, Point3D, int, Point3D?, Func{int, int, bool}, out Point3D)"/> within a fixed height window.</summary>
    public static bool TryFind(
        Map map, Point3D target, int range, Point3D? near, int minZ, int maxZ, Func<int, int, bool> allowed,
        out Point3D stand
    )
    {
        stand = default;

        if (map == null || map == Map.Internal || range < 0 || range > 8)
        {
            return false;
        }

        var side = range * 2 + 1;
        Span<int> order = stackalloc int[side * side];
        Span<int> keys = stackalloc int[side * side];
        var origin = near ?? target;

        // Offsets sorted by distance to the point we want to stand nearest to (insertion sort; at most 289 entries).
        var count = 0;
        for (var dx = -range; dx <= range; dx++)
        {
            for (var dy = -range; dy <= range; dy++)
            {
                var x = target.X + dx;
                var y = target.Y + dy;
                var ox = x - origin.X;
                var oy = y - origin.Y;
                var key = ox * ox + oy * oy;

                var i = count++;
                while (i > 0 && keys[i - 1] > key)
                {
                    keys[i] = keys[i - 1];
                    order[i] = order[i - 1];
                    i--;
                }

                keys[i] = key;
                order[i] = (dx + range) * side + dy + range;
            }
        }

        for (var i = 0; i < count; i++)
        {
            var x = target.X + order[i] / side - range;
            var y = target.Y + order[i] % side - range;

            if (allowed?.Invoke(x, y) == false)
            {
                continue;
            }

            if (map.CanSpawnMobile(x, y, minZ, maxZ, false, false, out var z))
            {
                stand = new Point3D(x, y, z);
                return true;
            }
        }

        return false;
    }
}
