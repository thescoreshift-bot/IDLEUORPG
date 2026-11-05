using System;
using System.Collections.Generic;
using Server.Items;
using Server.Logging;
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

    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleSettlement));

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
        PlayerMobile pm, IdleTaskDefinition def, IdleTaskHandler handler, IdleTarget target, IdleOrder order, TimeSpan away,
        IdleSettleMode mode
    )
    {
        var settings = IdleSystem.Settings;
        var counted = away > settings.MaxOfflineTime ? settings.MaxOfflineTime : away;

        var report = new IdleReport
        {
            TaskName = def.Name ?? def.Id,
            Mode = mode,
            Away = away,
            Counted = counted
        };

        var notes = mode == IdleSettleMode.Login ? null : report.Notes;

        var skill = pm.Skills[handler.GetSkill(def)];
        if (skill == null || counted <= TimeSpan.Zero)
        {
            return report;
        }

        // A preview runs the real gain code, then puts every skill and stat back.
        var skillSnapshot = SnapshotSkills(pm);

        var run = handler.BeginRun(pm, def, target, order);

        // Region rules (e.g. jail) still apply; the anti-macro check does not, since unattended repetition
        // is the whole point here and is instead limited by the offline rate and time cap.
        var region = Region.Find(target.Location, target.Map);
        var regionAllowsGain = pm.Skills.Cap > 0 && region.AllowGain(pm, skill, target.Location);
        var gainMultiplier = settings.SkillGain.GainChanceMultiplier * def.SkillGainMultiplier;
        var canGain = settings.SkillGain.Enabled && gainMultiplier > 0 && regionAllowsGain;

        var startStr = pm.RawStr;
        var startDex = pm.RawDex;
        var startInt = pm.RawInt;

        var budget = counted.TotalSeconds * settings.OfflineRate * def.Rate;
        var successesLeft = run.SuccessesAllowed == int.MaxValue ? double.PositiveInfinity : run.SuccessesAllowed;

        if (notes != null)
        {
            notes.Add(
                $"Time: {IdleSummaryGump.FormatTime(away)} away, {IdleSummaryGump.FormatTime(counted)} counted (max {IdleSummaryGump.FormatTime(settings.MaxOfflineTime)})"
            );
            notes.Add(
                $"Work: {counted.TotalSeconds:N0}s x offline {settings.OfflineRate:0.##} x task {def.Rate:0.##} = {budget:N0}s of online-equivalent work"
            );
            notes.Add(
                $"Skill gain: {(canGain ? "on" : "off")} (x{gainMultiplier:0.##}, region {(regionAllowsGain ? "allows" : "blocks")}, anti-macro not applied offline)"
            );
            run.Describe(notes);
        }

        if (run is IdleDiscreteTaskRun discrete)
        {
            SetWeightBaseline(pm, report, settings, mode);

            var context = new IdleDiscreteContext(pm, def, mode, report, notes, budget, canGain, gainMultiplier);
            discrete.Run(context);

            report.Attempts = context.Attempts;
            report.Successes = context.Successes;

            notes?.Add(
                $"Totals: {context.Attempts:N0} attempts, {context.Successes:N0} successes, {context.Gains} skill gain event(s), {budget - context.SecondsLeft:N0}s of {budget:N0}s used"
            );

            if (settings.SkillGain.StatGains && !_usePub45StatGain && regionAllowsGain)
            {
                ApplyStatGains(pm, skill.Info, counted, context.StatRolls, notes);
            }

            return Finish(pm, report, notes, mode, skillSnapshot, startStr, startDex, startInt);
        }

        var yields = new Dictionary<Type, double>();
        var model = new IdleAttemptModel();
        double attempts = 0;
        double successes = 0;
        double statRolls = 0;
        var gains = 0;
        var first = true;

        for (var i = 0; i < MaxSegments && budget > Epsilon; i++)
        {
            model.Clear();
            run.Evaluate(skill.Base, skill.Value, model);

            if (model.SecondsPerAttempt <= 0)
            {
                break;
            }

            var p = model.SuccessChance;
            var gainRate = canGain ? GainChancePerAttempt(pm, skill, model) * gainMultiplier : 0;

            if (first && notes != null)
            {
                notes.Add($"Start: {DescribeAttempt(skill, p, model.SecondsPerAttempt, gainRate)}");
                first = false;
            }

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
            if (reachedGain || gainRate > 0 && Utility.RandomDouble() < segment * gainRate)
            {
                if (TryGain(pm, skill))
                {
                    gains++;
                }
                else
                {
                    canGain = false; // Capped or locked: stop modelling gains, keep working.
                }
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

        if (notes != null)
        {
            model.Clear();
            run.Evaluate(skill.Base, skill.Value, model);
            var endGain = canGain ? GainChancePerAttempt(pm, skill, model) * gainMultiplier : 0;
            notes.Add($"End: {DescribeAttempt(skill, model.SuccessChance, model.SecondsPerAttempt, endGain)}");
            notes.Add($"Totals: {attempts:N1} attempts, {successes:N1} successes expected; {gains} skill gain event(s)");
        }

        if (mode != IdleSettleMode.Preview)
        {
            run.ConsumeSupplies(report.Successes);
        }

        if (settings.SkillGain.StatGains && !_usePub45StatGain && regionAllowsGain)
        {
            ApplyStatGains(pm, skill.Info, counted, statRolls, notes);
        }

        SetWeightBaseline(pm, report, settings, mode);

        foreach (var (type, expected) in yields)
        {
            var amount = RoundRandom(expected);
            var discard = def.DiscardTypes.Contains(type);
            var line = Deliver(pm, type, amount, mode != IdleSettleMode.Preview && !discard, report.PackLimit);
            if (line != null)
            {
                report.Items.Add(line);
                line.Discarded = discard;
                notes?.Add($"Yield: {line.Name} expected {expected:N2} -> {amount:N0}{(discard ? " (discarded)" : "")}");
            }
        }

        return Finish(pm, report, notes, mode, skillSnapshot, startStr, startDex, startInt);
    }

    private static void SetWeightBaseline(PlayerMobile pm, IdleReport report, IdleSettings settings, IdleSettleMode mode)
    {
        report.WeightLimit = pm.MaxWeight;
        report.WeightBefore = CarriedWeight(pm);
        report.KeepFreeStones = Math.Max(0, settings.KeepFreeStones);

        // The reserve only limits what idle work adds; a character already inside it keeps what they carry.
        report.PackLimit = Math.Max(report.WeightLimit - report.KeepFreeStones, report.WeightBefore);

        if (mode != IdleSettleMode.Preview)
        {
            logger.Information(
                "Idle weight {Player}: before delivery {Weight}/{Max}, pack limit {Limit} (reserve {Reserve}; body {Body} + items {Items}, Str {Str})",
                pm,
                report.WeightBefore,
                report.WeightLimit,
                report.PackLimit,
                report.KeepFreeStones,
                Mobile.BodyWeight,
                pm.TotalWeight,
                pm.Str
            );
        }
    }

    private static IdleReport Finish(
        PlayerMobile pm, IdleReport report, List<string> notes, IdleSettleMode mode, int[] skillSnapshot, int startStr,
        int startDex, int startInt
    )
    {
        if (mode != IdleSettleMode.Preview)
        {
            EnforceWeightLimit(pm, report);
        }

        report.WeightAfter = CarriedWeight(pm);

        if (notes != null)
        {
            notes.Add(
                $"Weight rule: body {Mobile.BodyWeight} + everything carried (equipped items, the backpack and its contents) must stay at or below max {report.WeightLimit} (40 + 3.5 x Str {pm.Str})"
            );
            notes.Add(
                $"Reserve: idle work fills the pack only up to {report.PackLimit} (max {report.WeightLimit} - KeepFreeStones {report.KeepFreeStones}, never below the weight already carried)"
            );
            notes.Add(
                mode == IdleSettleMode.Preview
                    ? $"Weight: {report.WeightBefore} / {report.WeightLimit} now, reserve {report.KeepFreeStones}; room for {report.PackLimit - report.WeightBefore} stones (preview places nothing)"
                    : $"Weight: {report.WeightBefore} -> {report.WeightAfter} / {report.WeightLimit}, reserve {report.KeepFreeStones}"
            );
        }

        var skills = pm.Skills;
        for (var i = 0; i < skillSnapshot.Length; i++)
        {
            if (skills[i].BaseFixedPoint != skillSnapshot[i])
            {
                report.Skills.Add(new IdleSkillLine(skills[i].Name, skillSnapshot[i] / 10.0, skills[i].Base));
            }
        }

        AddStatLine(report, "Strength", startStr, pm.RawStr);
        AddStatLine(report, "Dexterity", startDex, pm.RawDex);
        AddStatLine(report, "Intelligence", startInt, pm.RawInt);

        if (mode == IdleSettleMode.Preview)
        {
            RestoreSkills(pm, skillSnapshot, startStr, startDex, startInt);
        }

        return report;
    }

    private static string DescribeAttempt(Skill skill, double successChance, double secondsPerAttempt, double gainRate) =>
        $"skill {skill.Base:F1}, success {successChance:P1}, {secondsPerAttempt:F2}s/attempt, gain {gainRate:P2}/attempt";

    private static int[] SnapshotSkills(PlayerMobile pm)
    {
        var skills = pm.Skills;
        var values = new int[skills.Length];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = skills[i].BaseFixedPoint;
        }

        return values;
    }

    private static void RestoreSkills(PlayerMobile pm, int[] values, int str, int dex, int intel)
    {
        var skills = pm.Skills;
        for (var i = 0; i < values.Length; i++)
        {
            skills[i].BaseFixedPoint = values[i];
        }

        pm.RawStr = str;
        pm.RawDex = dex;
        pm.RawInt = intel;
    }

    /// <summary>
    /// Expected gain chance per attempt, mirroring SkillCheck.CheckSkill (pre-AoS bonus for failures included).
    /// Checks that are too hard (chance below 0) or trivial (chance 1+) never roll for a gain online either.
    /// </summary>
    private static double GainChancePerAttempt(PlayerMobile pm, Skill skill, IdleAttemptModel model)
    {
        var total = 0.0;

        foreach (var (weight, chance) in model.Checks)
        {
            if (chance >= 0.0 && chance < 1.0)
            {
                total += weight * GainChance(pm, skill, chance);
            }
        }

        return total;
    }

    /// <summary>Gain chance of one CheckSkill call with success chance <paramref name="chance"/> (0 to below 1).</summary>
    internal static double GainChance(PlayerMobile pm, Skill skill, double chance)
    {
        if (skill.Base < 10.0)
        {
            return 1.0;
        }

        var skills = pm.Skills;
        var gc = (double)(skills.Cap - skills.Total) / skills.Cap;
        gc += (skill.Cap - skill.Base) / skill.Cap;
        gc /= 2;

        // Online: + (1 - chance) * (success ? 0.5 : failBonus); averaged over the success roll here.
        var failBonus = Core.AOS ? 0.0 : 0.2;
        gc += (1.0 - chance) * (chance * 0.5 + (1.0 - chance) * failBonus);
        gc /= 2;

        gc *= skill.Info.GainFactor;
        return Math.Max(gc, 0.01);
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
    internal static bool TryGain(PlayerMobile pm, Skill skill)
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
    private static void ApplyStatGains(PlayerMobile pm, SkillInfo info, TimeSpan counted, double rolls, List<string> notes)
    {
        if (rolls <= 0 || _statGainDelay <= TimeSpan.Zero)
        {
            return;
        }

        var windows = Math.Max(1.0, counted.TotalSeconds / _statGainDelay.TotalSeconds);
        var rollsPerWindow = rolls / windows;

        notes?.Add(
            $"Stats: {windows:N1} window(s) of {IdleSummaryGump.FormatTime(_statGainDelay)}, {rollsPerWindow:N1} rolls each (max 1 gain per stat per window)"
        );

        GainStat(pm, Stat.Str, info.StrGain, pm.StrLock, windows, rollsPerWindow, notes);
        GainStat(pm, Stat.Dex, info.DexGain, pm.DexLock, windows, rollsPerWindow, notes);
        GainStat(pm, Stat.Int, info.IntGain, pm.IntLock, windows, rollsPerWindow, notes);
    }

    private static void GainStat(
        PlayerMobile pm, Stat stat, double statGain, StatLockType statLock, double windows, double rollsPerWindow,
        List<string> notes
    )
    {
        if (statGain <= 0 || statLock != StatLockType.Up)
        {
            return;
        }

        var chance = Math.Min(1.0, statGain / 33.3 * _statGainChanceMultiplier);
        var perWindow = 1.0 - Math.Pow(1.0 - chance, rollsPerWindow);
        var gains = RoundRandom(windows * perWindow);

        notes?.Add($"  {stat}: {chance:P2}/roll, {perWindow:P1}/window, expected {windows * perWindow:N2} -> {gains} gain roll(s)");

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

    /// <summary>Backpack first (its limits and the character's carry limit), then bank box; the rest is lost.</summary>
    internal static IdleItemLine Deliver(PlayerMobile pm, Type type, int amount, bool place, int limit, bool log = true)
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
            Type = type,
            UnitWeight = sample.Weight,
            ItemId = sample.ItemID,
            Hue = sample.Hue,
            Name = GetName(sample),
            Amount = amount
        };

        var stackable = sample.Stackable;
        sample.Delete();

        if (!place)
        {
            return line;
        }

        var remaining = stackable ? amount : Math.Min(amount, MaxNonStackable);

        line.ToPack = Place(pm.Backpack, pm, type, stackable, remaining, limit, log);
        remaining -= line.ToPack;

        if (remaining > 0)
        {
            line.ToBank = Place(pm.BankBox, pm, type, stackable, remaining, limit, log);
        }

        if (!log)
        {
            return line;
        }

        logger.Information(
            "Idle deliver {Player}: {Amount} {Item} at {Unit} stone(s) each -> pack {Pack}, bank {Bank}, lost {Lost}; weight now {Weight}/{Max}",
            pm,
            amount,
            line.Name,
            line.UnitWeight,
            line.ToPack,
            line.ToBank,
            line.Lost,
            CarriedWeight(pm),
            pm.MaxWeight
        );

        return line;
    }

    /// <summary>
    /// The weight the status bar shows and the server checks for overloading (PlayerMobile.OnSubItemAdded,
    /// StaminaSystem): a fixed body weight plus every item the character has, equipped or in the backpack.
    /// </summary>
    public static int CarriedWeight(Mobile m) => Mobile.BodyWeight + m.TotalWeight;

    /// <summary>
    /// Safety net after delivery: while the character is overloaded, moves whole stacks of what this settlement
    /// produced from the top of the backpack to the bank, newest first. The player's other belongings are never moved.
    /// </summary>
    public static void EnforceWeightLimit(PlayerMobile pm, IdleReport report)
    {
        var pack = pm.Backpack;
        var bank = pm.BankBox;

        if (pack == null || bank == null || pm.AccessLevel >= AccessLevel.GameMaster || CarriedWeight(pm) <= report.PackLimit)
        {
            return;
        }

        var types = new HashSet<Type>();
        foreach (var line in report.Items)
        {
            if (line.Type != null && line.ToPack > 0)
            {
                types.Add(line.Type);
            }
        }

        var items = pack.Items;
        for (var i = items.Count - 1; i >= 0 && CarriedWeight(pm) > report.PackLimit; i--)
        {
            var item = items[i];
            if (!types.Contains(item.GetType()) || !item.Stackable && !report.PlacedItems.Contains(item) ||
                !bank.CheckHold(pm, item, false, true))
            {
                continue;
            }

            var amount = item.Amount;
            var weight = item.PileWeight + item.TotalWeight;
            var name = GetName(item);

            if (!bank.TryDropItem(pm, item, false))
            {
                continue;
            }

            report.MovedToBank.Add(new IdleMoveLine(name, amount, weight));

            foreach (var line in report.Items)
            {
                if (line.Type == item.GetType())
                {
                    var shifted = Math.Min(amount, line.ToPack);
                    line.ToPack -= shifted;
                    line.ToBank += shifted;
                    break;
                }
            }

            logger.Information(
                "Idle weight {Player}: overloaded, moved {Amount} {Item} ({Stones} stones) to the bank; weight now {Weight}/{Max}",
                pm,
                amount,
                name,
                weight,
                CarriedWeight(pm),
                pm.MaxWeight
            );
        }

        if (CarriedWeight(pm) > report.PackLimit)
        {
            logger.Warning(
                "Idle weight {Player}: still overloaded at {Weight}/{Max} after the safety pass",
                pm,
                CarriedWeight(pm),
                pm.MaxWeight
            );
        }
    }

    private static int Place(Container container, PlayerMobile pm, Type type, bool stackable, int amount, int limit, bool log)
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
                var fit = LargestFit(container, pm, item, Math.Min(amount - placed, MaxStack), limit);
                if (fit == 0)
                {
                    item.Delete();
                    break;
                }

                item.Amount = fit;
            }
            else if (!Fits(container, pm, item, limit))
            {
                item.Delete();
                break;
            }

            var count = item.Amount;
            if (!container.TryDropItem(pm, item, false))
            {
                item.Delete();
                break;
            }

            placed += stackable ? count : 1;

            if (log && stackable && container is not BankBox)
            {
                logger.Information(
                    "Idle deliver {Player}: pack step +{Count}, {Placed} placed so far; weight now {Weight}/{Max}",
                    pm,
                    count,
                    placed,
                    CarriedWeight(pm),
                    pm.MaxWeight
                );
            }
        }

        return placed;
    }

    private static int LargestFit(Container container, PlayerMobile pm, Item item, int max, int limit)
    {
        item.Amount = max;
        if (Fits(container, pm, item, limit))
        {
            return max;
        }

        int lo = 0, hi = max - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            item.Amount = mid;

            if (Fits(container, pm, item, limit))
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

    /// <summary>
    /// The container's own limits, plus, for anything the character carries, the character's weight limit, so offline
    /// gains never leave them overloaded. Staff skip container limits (as online) but not the carry limit.
    /// </summary>
    internal static bool Fits(Container container, PlayerMobile pm, Item item, int limit)
    {
        if (!container.CheckHold(pm, item, false, true))
        {
            return false;
        }

        return container is BankBox || CarriedWeight(pm) + item.PileWeight + item.TotalWeight <= limit;
    }

    public static string GetName(Item item) => item.Name ?? Localization.GetText(item.LabelNumber) ?? item.GetType().Name;

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
