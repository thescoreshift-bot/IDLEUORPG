using System;
using System.Collections.Generic;
using Server.Items;
using Server.Misc;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Turns time away into results with expected-value math. Work is advanced from one skill gain to the next
/// instead of swing by swing, because success and gain chances only change when the skill does.
/// </summary>
public static class IdleSettlement
{
    // A full 0 -> 100 climb is ~1000 gains; this only guards against a misconfigured task looping forever.
    private const int MaxSegments = 20000;
    private const double Epsilon = 1e-6;
    private const int MaxStack = 60000;
    private const int MaxNonStackable = 100;

    private static TimeSpan _statGainDelay;
    private static double _statGainChanceMultiplier;
    private static bool _usePub45StatGain;

    public static void Initialize()
    {
        // Same keys and defaults as SkillCheck, which registers them during Configure.
        _statGainDelay = ServerConfiguration.GetSetting("stats.gainDelay", TimeSpan.FromMinutes(Core.ML ? 0.05 : 10));
        _statGainChanceMultiplier = ServerConfiguration.GetSetting("stats.gainChanceMultiplier", 1.0);
        _usePub45StatGain = ServerConfiguration.GetSetting("stats.usePub45StatGain", Core.ML);
    }

    public static IdleReport Settle(
        PlayerMobile pm, IdleTaskDefinition def, IdleTaskHandler handler, IdleTarget target, TimeSpan away, bool simulated
    )
    {
        var settings = IdleSystem.Settings;
        var counted = away > settings.MaxOfflineTime ? settings.MaxOfflineTime : away;

        var report = new IdleReport
        {
            TaskName = def.Name ?? def.Id,
            Simulated = simulated,
            Away = away,
            Counted = counted
        };

        var skill = pm.Skills[handler.GetSkill(def)];
        if (skill == null || counted <= TimeSpan.Zero)
        {
            return report;
        }

        var run = handler.BeginRun(pm, def, target);

        // Region rules (e.g. jail) still apply; the anti-macro check does not, since unattended repetition
        // is the whole point here and is instead limited by the offline rate and time cap.
        var region = Region.Find(target.Location, target.Map);
        var regionAllowsGain = pm.Skills.Cap > 0 && region.AllowGain(pm, skill, target.Location);
        var gainMultiplier = settings.SkillGain.GainChanceMultiplier * def.SkillGainMultiplier;
        var canGain = settings.SkillGain.Enabled && gainMultiplier > 0 && regionAllowsGain;

        var startBase = skill.Base;
        var startStr = pm.RawStr;
        var startDex = pm.RawDex;
        var startInt = pm.RawInt;

        var budget = counted.TotalSeconds * settings.OfflineRate * def.Rate;
        var successesLeft = run.SuccessesAllowed == int.MaxValue ? double.PositiveInfinity : run.SuccessesAllowed;

        var yields = new Dictionary<Type, double>();
        var model = new IdleAttemptModel();
        double attempts = 0;
        double successes = 0;
        double statRolls = 0;

        for (var i = 0; i < MaxSegments && budget > Epsilon; i++)
        {
            model.Clear();
            run.Evaluate(skill.Base, skill.Value, model);

            if (model.SecondsPerAttempt <= 0)
            {
                break;
            }

            var p = model.SuccessChance;
            var available = budget / model.SecondsPerAttempt;
            var suppliesLimited = false;

            if (p > 0 && successesLeft / p <= available)
            {
                available = successesLeft / p;
                suppliesLimited = true;
            }

            if (available < Epsilon)
            {
                if (suppliesLimited)
                {
                    report.StopReason = run.SuppliesExhaustedMessage;
                }

                break;
            }

            var gainRate = canGain ? GainChancePerAttempt(pm, skill, model) * gainMultiplier : 0;
            var toNextGain = gainRate > 0 ? 1.0 / gainRate : double.PositiveInfinity;
            var segment = Math.Min(available, toNextGain);

            attempts += segment;
            successes += segment * p;
            statRolls += segment * StatRollChance(model);
            successesLeft -= segment * p;
            budget -= segment * model.SecondsPerAttempt;

            foreach (var (type, perAttempt) in model.Yield)
            {
                yields[type] = yields.GetValueOrDefault(type) + perAttempt * segment;
            }

            var reachedGain = toNextGain <= available;

            // A final partial stretch still had a proportional chance at one more gain, which keeps the total exact on average.
            if ((reachedGain || gainRate > 0 && Utility.RandomDouble() < segment * gainRate) && !TryGain(pm, skill))
            {
                canGain = false; // Capped or locked: stop modelling gains, keep working.
            }

            if (!reachedGain)
            {
                if (suppliesLimited)
                {
                    report.StopReason = run.SuppliesExhaustedMessage;
                }

                break;
            }
        }

        report.Attempts = RoundRandom(attempts);
        report.Successes = Math.Min(RoundRandom(successes), run.SuccessesAllowed);
        run.ConsumeSupplies(report.Successes);

        if (settings.SkillGain.StatGains && !_usePub45StatGain && regionAllowsGain)
        {
            ApplyStatGains(pm, skill.Info, counted, statRolls);
        }

        foreach (var (type, amount) in yields)
        {
            var line = Deliver(pm, type, RoundRandom(amount));
            if (line != null)
            {
                report.Items.Add(line);
            }
        }

        if (skill.Base != startBase)
        {
            report.Skills.Add(new IdleSkillLine(skill.Name, startBase, skill.Base));
        }

        AddStatLine(report, "Strength", startStr, pm.RawStr);
        AddStatLine(report, "Dexterity", startDex, pm.RawDex);
        AddStatLine(report, "Intelligence", startInt, pm.RawInt);

        return report;
    }

    /// <summary>
    /// Expected gain chance per attempt, mirroring SkillCheck.CheckSkill (pre-AoS bonus for failures included).
    /// Checks that are too hard (chance below 0) or trivial (chance 1+) never roll for a gain online either.
    /// </summary>
    private static double GainChancePerAttempt(PlayerMobile pm, Skill skill, IdleAttemptModel model)
    {
        var skills = pm.Skills;
        var total = 0.0;

        foreach (var (weight, chance) in model.Checks)
        {
            if (chance < 0.0 || chance >= 1.0)
            {
                continue;
            }

            double gc;

            if (skill.Base < 10.0)
            {
                gc = 1.0;
            }
            else
            {
                gc = (double)(skills.Cap - skills.Total) / skills.Cap;
                gc += (skill.Cap - skill.Base) / skill.Cap;
                gc /= 2;

                // Online: + (1 - chance) * (success ? 0.5 : failBonus); averaged over the success roll here.
                var failBonus = Core.AOS ? 0.0 : 0.2;
                gc += (1.0 - chance) * (chance * 0.5 + (1.0 - chance) * failBonus);
                gc /= 2;

                gc *= skill.Info.GainFactor;
                gc = Math.Max(gc, 0.01);
            }

            total += weight * gc;
        }

        return total;
    }

    /// <summary>Legacy stat gains roll on each successful, challenging check.</summary>
    private static double StatRollChance(IdleAttemptModel model)
    {
        var total = 0.0;
        foreach (var (weight, chance) in model.Checks)
        {
            if (chance >= 0.0 && chance < 1.0)
            {
                total += weight * chance;
            }
        }

        return total;
    }

    /// <summary>Uses the real gain routine so caps, locks, and lowering "down" skills behave exactly as online.</summary>
    private static bool TryGain(PlayerMobile pm, Skill skill)
    {
        if (skill.Lock != SkillLock.Up || skill.Base >= skill.Cap)
        {
            return false;
        }

        var before = skill.BaseFixedPoint;
        SkillCheck.Gain(pm, skill);
        return skill.BaseFixedPoint != before;
    }

    /// <summary>
    /// Online, a stat can rise at most once per gain delay. Each delay window gets the share of rolls made in it,
    /// and the window gains if any roll in it would have succeeded.
    /// </summary>
    private static void ApplyStatGains(PlayerMobile pm, SkillInfo info, TimeSpan counted, double rolls)
    {
        if (rolls <= 0 || _statGainDelay <= TimeSpan.Zero)
        {
            return;
        }

        var windows = Math.Max(1.0, counted.TotalSeconds / _statGainDelay.TotalSeconds);
        var rollsPerWindow = rolls / windows;

        GainStat(pm, Stat.Str, info.StrGain, pm.StrLock, windows, rollsPerWindow);
        GainStat(pm, Stat.Dex, info.DexGain, pm.DexLock, windows, rollsPerWindow);
        GainStat(pm, Stat.Int, info.IntGain, pm.IntLock, windows, rollsPerWindow);
    }

    private static void GainStat(PlayerMobile pm, Stat stat, double statGain, StatLockType statLock, double windows, double rollsPerWindow)
    {
        if (statGain <= 0 || statLock != StatLockType.Up)
        {
            return;
        }

        var chance = Math.Min(1.0, statGain / 33.3 * _statGainChanceMultiplier);
        var perWindow = 1.0 - Math.Pow(1.0 - chance, rollsPerWindow);
        var gains = RoundRandom(windows * perWindow);

        for (var i = 0; i < gains; i++)
        {
            var atrophy = pm.RawStatTotal / (double)pm.StatCap >= Utility.RandomDouble();
            SkillCheck.IncreaseStat(pm, stat, atrophy);
        }
    }

    private static void AddStatLine(IdleReport report, string name, int from, int to)
    {
        if (from != to)
        {
            report.Stats.Add(new IdleStatLine(name, from, to));
        }
    }

    /// <summary>Rounds so that the expected result equals the input (2.3 gives 3 with 30% chance, else 2).</summary>
    public static int RoundRandom(double value)
    {
        if (value <= 0)
        {
            return 0;
        }

        if (value >= int.MaxValue)
        {
            return int.MaxValue;
        }

        var whole = Math.Floor(value);
        return (int)whole + (Utility.RandomDouble() < value - whole ? 1 : 0);
    }

    /// <summary>Backpack first (respecting its weight and item limits), then bank box; the rest is lost.</summary>
    private static IdleItemLine Deliver(PlayerMobile pm, Type type, int amount)
    {
        if (amount <= 0)
        {
            return null;
        }

        var sample = Construct(type);
        if (sample == null)
        {
            return null;
        }

        var line = new IdleItemLine
        {
            ItemId = sample.ItemID,
            Hue = sample.Hue,
            Name = sample.Name ?? Localization.GetText(sample.LabelNumber) ?? type.Name,
            Amount = amount
        };

        var stackable = sample.Stackable;
        sample.Delete();

        var remaining = stackable ? amount : Math.Min(amount, MaxNonStackable);

        line.ToPack = Place(pm.Backpack, pm, type, stackable, remaining);
        remaining -= line.ToPack;

        if (remaining > 0)
        {
            line.ToBank = Place(pm.BankBox, pm, type, stackable, remaining);
        }

        return line;
    }

    private static int Place(Container container, PlayerMobile pm, Type type, bool stackable, int amount)
    {
        if (container == null)
        {
            return 0;
        }

        var placed = 0;

        while (placed < amount)
        {
            var item = Construct(type);
            if (item == null)
            {
                break;
            }

            if (stackable)
            {
                var fit = LargestFit(container, pm, item, Math.Min(amount - placed, MaxStack));
                if (fit == 0)
                {
                    item.Delete();
                    break;
                }

                item.Amount = fit;
            }

            var count = item.Amount;
            if (!container.TryDropItem(pm, item, false))
            {
                item.Delete();
                break;
            }

            placed += stackable ? count : 1;
        }

        return placed;
    }

    private static int LargestFit(Container container, PlayerMobile pm, Item item, int max)
    {
        item.Amount = max;
        if (container.CheckHold(pm, item, false, true))
        {
            return max;
        }

        int lo = 0, hi = max - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            item.Amount = mid;

            if (container.CheckHold(pm, item, false, true))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private static Item Construct(Type type)
    {
        try
        {
            return type.CreateInstance<Item>();
        }
        catch
        {
            return null;
        }
    }
}
