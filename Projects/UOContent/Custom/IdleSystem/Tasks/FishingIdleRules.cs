using System;
using System.Collections.Generic;
using System.Reflection;
using Server.Engines.Harvest;
using Server.Items;

namespace Server.Custom.Idle;

/// <summary>
/// Fishing's catch replacements (footwear, magic fish, and deep-water specials), read from the live private table in
/// <see cref="Fishing"/> so offline odds always match online. Read by reflection because the table is private and
/// changing Fishing.cs to expose it would mean editing ModernUO content.
/// </summary>
public static class FishingIdleRules
{
    private record Mutation(double ReqSkill, double MinSkill, double MaxSkill, bool DeepWater, Type[] Types);

    private static List<Mutation> _mutations;

    /// <summary>Loads the table once; returns false with a reason if Fishing's internals changed shape.</summary>
    public static bool TryLoad(out string error)
    {
        if (_mutations != null)
        {
            error = null;
            return true;
        }

        const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        try
        {
            var table = typeof(Fishing).GetField("_mutateTable", BindingFlags.NonPublic | BindingFlags.Static)
                ?.GetValue(null) as Array;

            if (table == null)
            {
                error = "Fishing._mutateTable not found";
                return false;
            }

            var list = new List<Mutation>();

            foreach (var entry in table)
            {
                var type = entry.GetType();
                T Read<T>(string name) => (T)type.GetField(name, any)!.GetValue(entry);

                var expansion = Read<Expansion>("m_Expansion");
                var maxExpansion = Read<Expansion?>("m_MaxExpansion");

                // Same era filter Fishing.MutateType applies on every catch.
                if (Core.Expansion < expansion || Core.Expansion > maxExpansion)
                {
                    continue;
                }

                list.Add(
                    new Mutation(
                        Read<double>("m_ReqSkill"),
                        Read<double>("m_MinSkill"),
                        Read<double>("m_MaxSkill"),
                        Read<bool>("m_DeepWater"),
                        Read<Type[]>("m_Types")
                    )
                );
            }

            _mutations = list;
            error = null;
            return true;
        }
        catch (Exception e)
        {
            error = $"could not read Fishing's catch table ({e.Message})";
            return false;
        }
    }

    /// <summary>Deep water is where online fishing surfaces sea serpents carrying the special catches.</summary>
    public static bool IsDeepWater(Map map, int x, int y) => SpecialFishingNet.FullValidation(map, x, y);

    /// <summary>
    /// Shares of each catch type per successful cast, mirroring Fishing.MutateType: entries are rolled in order and
    /// the first hit replaces the fish. Only shallow-water entries apply, since idle fishing is shore-only.
    /// </summary>
    public static void AddCatchShares(Type caught, double skillBase, double skillValue, List<(Type Type, double Share)> shares)
    {
        var remaining = 1.0;

        foreach (var m in _mutations)
        {
            if (m.DeepWater || skillBase < m.ReqSkill || m.Types.Length == 0)
            {
                continue;
            }

            var chance = Math.Clamp((skillValue - m.MinSkill) / (m.MaxSkill - m.MinSkill), 0.0, 1.0);
            var hit = remaining * chance;

            foreach (var type in m.Types)
            {
                shares.Add((type, hit / m.Types.Length));
            }

            remaining -= hit;
        }

        shares.Add((caught, remaining));
    }
}
