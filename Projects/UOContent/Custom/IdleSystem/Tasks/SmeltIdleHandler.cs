using System;
using System.Collections.Generic;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Smelting ore into ingots at a forge with Mining, pile by pile as online: a success turns the whole pile into
/// ingots, a failure burns half of it away and the pile is tried again. Ore comes from the backpack, then the bank.
/// </summary>
public class SmeltIdleHandler : IdleTaskHandler
{
    public static void Configure() => Register("Smelt", new SmeltIdleHandler());

    public override bool Bind(IdleTaskDefinition def, out string error)
    {
        error = null;
        return true;
    }

    public override SkillName GetSkill(IdleTaskDefinition def) => SkillName.Mining;

    public override int GetRange(IdleTaskDefinition def) => SmithyStations.Range;

    public override bool ValidateTarget(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, out string error)
    {
        error = target.Map == null || target.Map == Map.Internal ? "That is not a valid spot." : null;
        return error == null;
    }

    public override bool ValidateOrder(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, out string error)
    {
        error = order == null || order.RecipeId != null || order.Requested <= 0 ? "Say how much ore to smelt." : null;
        return error == null;
    }

    public override bool CheckStation(PlayerMobile pm, IdleTaskDefinition def, Map map, Point3D location, out string error)
    {
        SmithyStations.Find(pm, map, location, out _, out var forge);
        error = forge ? null : "You must be near a forge to smelt ore.";
        return forge;
    }

    public override bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, out string error)
    {
        error = "Say how much ore to smelt.";
        return false;
    }

    public override bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, out string error)
    {
        foreach (var ore in FindOre(pm))
        {
            if (CanSmelt(pm, ore))
            {
                error = null;
                return true;
            }
        }

        error = "You have no ore you know how to smelt.";
        return false;
    }

    public override IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target) => null;

    public override IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, IdleOrder order) =>
        new SmeltIdleRun(pm, def, order);

    /// <summary>Ore piles in the backpack first, then the bank box.</summary>
    public static List<BaseOre> FindOre(PlayerMobile pm)
    {
        var list = new List<BaseOre>();
        AddOre(pm.Backpack, list);
        AddOre(pm.BankBox, list);
        return list;
    }

    private static void AddOre(Container container, List<BaseOre> list)
    {
        if (container == null)
        {
            return;
        }

        foreach (var ore in container.FindItemsByType<BaseOre>())
        {
            if (ore.Movable)
            {
                list.Add(ore);
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // The numbers below are copied from BaseOre.InternalTarget.OnTarget in Items/Resources/Blacksmithing/Ore.cs.
    // They live in a private class there, so they cannot be read; keep them in step if that file changes.
    // ---------------------------------------------------------------------------------------------------------

    public const int SmallOre = 0x19B7;
    public const int MediumClumpOre = 0x19B8;
    public const int LargeOre = 0x19B9;
    public const int MaxPerSmelt = 30000;

    public static double GetDifficulty(CraftResource resource) =>
        resource switch
        {
            CraftResource.DullCopper => 65.0,
            CraftResource.ShadowIron => 70.0,
            CraftResource.Copper     => 75.0,
            CraftResource.Bronze     => 80.0,
            CraftResource.Gold       => 85.0,
            CraftResource.Agapite    => 90.0,
            CraftResource.Verite     => 95.0,
            CraftResource.Valorite   => 99.0,
            _                        => 50.0
        };

    /// <summary>"You have no idea how to smelt this strange ore!" and the too-small-pile check.</summary>
    public static bool CanSmelt(PlayerMobile pm, BaseOre ore)
    {
        var difficulty = GetDifficulty(ore.Resource);
        return !(difficulty > 50.0 && difficulty > pm.Skills.Mining.Value) && !(ore.ItemID == SmallOre && ore.Amount < 2);
    }

    /// <summary>Ingots from a successful smelt of <paramref name="amount"/> ore; sets how much ore it uses.</summary>
    public static int GetIngots(int itemId, int amount, out int consumed)
    {
        consumed = Math.Min(amount, MaxPerSmelt);

        if (itemId == SmallOre)
        {
            var ingots = consumed / 2;
            if (consumed % 2 != 0)
            {
                consumed--;
            }

            return ingots;
        }

        return itemId == LargeOre ? consumed * 2 : consumed;
    }

    /// <summary>What a failed smelt leaves: half the pile, or a single ore shrinks to a smaller graphic.</summary>
    public static int Burn(ref int itemId, int amount)
    {
        if (amount < 2)
        {
            itemId = itemId == LargeOre ? MediumClumpOre : SmallOre;
            return amount;
        }

        return amount / 2;
    }

    // ---------------------------------------------------------------------------------------------------------
}

public class SmeltIdleRun : IdleDiscreteTaskRun
{
    private readonly PlayerMobile _pm;
    private readonly IdleTaskDefinition _def;
    private readonly IdleOrder _order;

    public SmeltIdleRun(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order)
    {
        _pm = pm;
        _def = def;
        _order = order;
    }

    public override void Describe(List<string> notes)
    {
        notes.Add($"Order: smelt {_order.Remaining:N0} more ore ({_order.Requested:N0} ordered)");
        notes.Add(
            $"Smelting: one attempt per ore pile, {_def.SecondsPerAttempt:0.##}s each; Mining check difficulty -25/+25 around 50 (iron) to 99 (valorite)"
        );
        notes.Add("Success: the whole pile becomes ingots (large ore x2, small ore 1 per 2). Failure: half the pile burns away and it is tried again");
    }

    public override void Run(IdleDiscreteContext context)
    {
        var remaining = _order.Remaining;
        var ingots = new Dictionary<Type, int>();
        var burnt = 0;
        string stop = null;

        foreach (var pile in SmeltIdleHandler.FindOre(_pm))
        {
            if (remaining <= 0 || stop != null)
            {
                break;
            }

            if (!SmeltIdleHandler.CanSmelt(_pm, pile))
            {
                continue;
            }

            var difficulty = SmeltIdleHandler.GetDifficulty(pile.Resource);
            var minSkill = difficulty - 25.0;
            var maxSkill = difficulty + 25.0;

            var take = Math.Min(pile.Amount, remaining);
            var amount = take;
            var itemId = pile.ItemID;

            while (amount > 0)
            {
                if (itemId == SmeltIdleHandler.SmallOre && amount < 2)
                {
                    break; // Not enough metal-bearing ore left in this pile to make an ingot.
                }

                if (!context.TrySpend(_def.SecondsPerAttempt))
                {
                    stop = "time";
                    break;
                }

                context.Attempts++;

                // CheckTargetSkill(Mining, forge, min, max): the gain roll, then the success roll on the same chance.
                context.CheckSkill(SkillName.Mining, minSkill, maxSkill);
                var chance = (_pm.Skills.Mining.Value - minSkill) / (maxSkill - minSkill);

                if (chance > Utility.RandomDouble())
                {
                    context.Successes++;
                    var made = SmeltIdleHandler.GetIngots(itemId, amount, out var consumed);
                    var ingotType = GetIngotType(pile);
                    ingots[ingotType] = ingots.GetValueOrDefault(ingotType) + made;
                    amount -= consumed;
                    break;
                }

                var kept = SmeltIdleHandler.Burn(ref itemId, amount);
                burnt += amount - kept;
                amount = kept;
            }

            if (stop != null && amount == take)
            {
                break; // Ran out of time before touching this pile.
            }

            var used = take - amount;

            // A pile cut short by time keeps its rest for next time; otherwise the leftover (a single small ore) is done.
            remaining -= stop == "time" ? used : take;
            context.AddUsed(IdleSettlement.GetName(pile), used);

            if (!context.Preview)
            {
                var wholePile = take == pile.Amount;
                pile.Consume(used);

                if (wholePile && !pile.Deleted)
                {
                    pile.ItemID = itemId;
                }
            }
        }

        if (stop == null && remaining > 0)
        {
            context.Report.StopReason = "You ran out of ore you know how to smelt.";
        }

        foreach (var (type, amount) in ingots)
        {
            var line = IdleSettlement.Deliver(_pm, type, amount, !context.Preview, context.Report.PackLimit);
            if (line != null)
            {
                context.Report.Items.Add(line);
            }
        }

        context.Notes?.Add($"Ore burnt away by failed smelts: {burnt:N0}");

        var done = _order.Remaining - Math.Max(0, remaining);
        var left = Math.Max(0, remaining);

        if (!context.Preview)
        {
            _order.Remaining = left;
        }

        context.Report.OrderStatus = left == 0
            ? $"smelted {_order.Requested:N0} ore, order complete"
            : $"{done:N0} ore smelted this time, {left:N0} of {_order.Requested:N0} left{(stop == "time" ? " (continues next time)" : "")}";
    }

    private static Type GetIngotType(BaseOre ore)
    {
        var ingot = ore.GetIngot();
        var type = ingot.GetType();
        ingot.Delete();
        return type;
    }
}
