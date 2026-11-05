using Server.Engines.Craft;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Forge and anvil detection. While the character stands at the spot this is the server's own
/// <see cref="DefBlacksmithy.CheckAnvilAndForge"/>; for a spot with nobody on it (startup validation, a task set
/// while offline) the same rules are applied from the standing point.
/// </summary>
public static class SmithyStations
{
    /// <summary>Range DefBlacksmithy.CanCraft uses, and the smelting target's range.</summary>
    public const int Range = 2;

    // Same item ids as DefBlacksmithy.CheckAnvilAndForge and the ore smelting target in Ore.cs.
    public static bool IsAnvil(int id) => id is 4015 or 4016 or 11733 or 11734;

    public static bool IsForge(int id) => id is 4017 or >= 6522 and <= 6569 or 11736;

    public static void Find(PlayerMobile pm, Map map, Point3D location, out bool anvil, out bool forge)
    {
        if (pm != null && pm.Map == map && pm.Location == location)
        {
            DefBlacksmithy.CheckAnvilAndForge(pm, Range, out anvil, out forge);
            return;
        }

        anvil = false;
        forge = false;

        if (map == null || map == Map.Internal)
        {
            return;
        }

        // Mobile eye height, as Map.LineOfSight uses for a character.
        var eye = new Point3D(location.X, location.Y, location.Z + 14);

        foreach (var item in map.GetItemsInRange(location, Range))
        {
            var type = item.GetType();
            var isAnvil = type.IsDefined(typeof(AnvilAttribute), false) || IsAnvil(item.ItemID);
            var isForge = type.IsDefined(typeof(ForgeAttribute), false) || IsForge(item.ItemID);

            if ((isAnvil || isForge) && item.Z <= location.Z + 16 && location.Z <= item.Z + 16 &&
                map.LineOfSight(eye, map.GetPoint(item, false)))
            {
                anvil |= isAnvil;
                forge |= isForge;
            }
        }

        for (var x = -Range; x <= Range && (!anvil || !forge); x++)
        {
            for (var y = -Range; y <= Range && (!anvil || !forge); y++)
            {
                foreach (var tile in map.Tiles.GetStaticAndMultiTiles(location.X + x, location.Y + y))
                {
                    var isAnvil = IsAnvil(tile.ID);
                    var isForge = IsForge(tile.ID);

                    if ((isAnvil || isForge) && tile.Z <= location.Z + 16 && location.Z <= tile.Z + 16 &&
                        map.LineOfSight(eye, new Point3D(location.X + x, location.Y + y, tile.Z + tile.Height / 2 + 1)))
                    {
                        anvil |= isAnvil;
                        forge |= isForge;
                    }
                }
            }
        }
    }
}
