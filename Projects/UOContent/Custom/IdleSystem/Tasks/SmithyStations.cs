using Server.Engines.Craft;
using Server.Items;
using Server.Mobiles;
using Server.Text;

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

    /// <summary>What the offline check saw, per station kind: candidates in range and why each was rejected.</summary>
    public struct Survey
    {
        public bool Anvil;
        public bool Forge;
        public int AnvilsSeen;
        public int ForgesSeen;
        public int RejectedHeight;
        public int RejectedSight;
    }

    public static void Find(PlayerMobile pm, Map map, Point3D location, out bool anvil, out bool forge)
    {
        if (pm != null && pm.Map == map && pm.Location == location)
        {
            DefBlacksmithy.CheckAnvilAndForge(pm, Range, out anvil, out forge);
            return;
        }

        var survey = Scan(map, location, true);
        anvil = survey.Anvil;
        forge = survey.Forge;
    }

    /// <summary>For logs: the offline check's view of a spot, e.g. "anvils 1, forges 1, 1 out of height reach".</summary>
    public static string Explain(Map map, Point3D location)
    {
        var s = Scan(map, location, false);

        using var sb = ValueStringBuilder.Create();
        sb.Append($"at {location}: anvils in range {s.AnvilsSeen}, forges in range {s.ForgesSeen}");

        if (s.RejectedHeight > 0)
        {
            sb.Append($", {s.RejectedHeight} more than 16 above or below");
        }

        if (s.RejectedSight > 0)
        {
            sb.Append($", {s.RejectedSight} out of line of sight");
        }

        sb.Append($"; usable: anvil {s.Anvil}, forge {s.Forge}");
        return sb.ToString();
    }

    /// <param name="stopWhenFound">Stops at the first anvil and forge found; false surveys everything for logs.</param>
    public static Survey Scan(Map map, Point3D location, bool stopWhenFound)
    {
        var s = new Survey();

        if (map == null || map == Map.Internal)
        {
            return s;
        }

        // Mobile eye height, as Map.LineOfSight uses for a character.
        var eye = new Point3D(location.X, location.Y, location.Z + 14);

        foreach (var item in map.GetItemsInRange(location, Range))
        {
            var type = item.GetType();
            var isAnvil = type.IsDefined(typeof(AnvilAttribute), false) || IsAnvil(item.ItemID);
            var isForge = type.IsDefined(typeof(ForgeAttribute), false) || IsForge(item.ItemID);

            if (isAnvil || isForge)
            {
                Consider(ref s, map, eye, location.Z, isAnvil, isForge, item.Z, map.GetPoint(item, false));
            }
        }

        for (var x = -Range; x <= Range && (!stopWhenFound || !s.Anvil || !s.Forge); x++)
        {
            for (var y = -Range; y <= Range && (!stopWhenFound || !s.Anvil || !s.Forge); y++)
            {
                foreach (var tile in map.Tiles.GetStaticAndMultiTiles(location.X + x, location.Y + y))
                {
                    var isAnvil = IsAnvil(tile.ID);
                    var isForge = IsForge(tile.ID);

                    if (isAnvil || isForge)
                    {
                        var center = new Point3D(location.X + x, location.Y + y, tile.Z + tile.Height / 2 + 1);
                        Consider(ref s, map, eye, location.Z, isAnvil, isForge, tile.Z, center);
                    }
                }
            }
        }

        return s;
    }

    private static void Consider(
        ref Survey s, Map map, Point3D eye, int standZ, bool isAnvil, bool isForge, int z, Point3D center
    )
    {
        s.AnvilsSeen += isAnvil ? 1 : 0;
        s.ForgesSeen += isForge ? 1 : 0;

        if (z > standZ + 16 || standZ > z + 16)
        {
            s.RejectedHeight++;
            return;
        }

        if (!map.LineOfSight(eye, center))
        {
            s.RejectedSight++;
            return;
        }

        s.Anvil |= isAnvil;
        s.Forge |= isForge;
    }
}
