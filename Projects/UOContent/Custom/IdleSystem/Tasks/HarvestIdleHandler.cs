using System;
using System.Collections.Generic;
using System.Reflection;
using Server.Engines.Harvest;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Runs any task backed by a <see cref="HarvestDefinition"/> (mining, lumberjacking, fishing) by reading the live
/// resource and vein tables, so offline results follow the same tables as online harvesting.
/// </summary>
public class HarvestIdleHandler : IdleTaskHandler
{
    private static readonly Dictionary<string, (HarvestSystem System, HarvestDefinition Definition)> _bound =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Configure() => Register("Harvest", new HarvestIdleHandler());

    public static HarvestSystem GetSystem(string name) =>
        name?.ToLowerInvariant() switch
        {
            "mining"        => Mining.System,
            "lumberjacking" => Lumberjacking.System,
            "fishing"       => Fishing.System,
            _               => null
        };

    public override bool Bind(IdleTaskDefinition def, out string error)
    {
        var system = GetSystem(def.HarvestSystem);
        if (system == null)
        {
            error = $"unknown harvest system '{def.HarvestSystem}'";
            return false;
        }

        // Definitions are exposed as named properties (Mining.OreAndStone, Mining.Sand); fall back to the first one.
        var definition = string.IsNullOrEmpty(def.HarvestDefinition)
            ? system.GetDefinition()
            : system.GetType().GetProperty(def.HarvestDefinition, BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(system) as HarvestDefinition;

        if (definition?.Resources == null || definition.Veins == null)
        {
            error = $"harvest definition '{def.HarvestDefinition}' not found on {def.HarvestSystem}";
            return false;
        }

        if (system is Fishing && !FishingIdleRules.TryLoad(out error))
        {
            return false;
        }

        _bound[def.Id] = (system, definition);
        error = null;
        return true;
    }

    private static (HarvestSystem System, HarvestDefinition Definition) Get(IdleTaskDefinition def) => _bound[def.Id];

    public override SkillName GetSkill(IdleTaskDefinition def) => Get(def).Definition.Skill;

    public override int GetRange(IdleTaskDefinition def) => Get(def).Definition.MaxRange;

    public override bool TryCreateTarget(PlayerMobile pm, IdleTaskDefinition def, object targeted, out IdleTarget target)
    {
        var (system, definition) = Get(def);

        if (system.GetHarvestDetails(pm, null, targeted, out var tileID, out var map, out var loc, out var isLand) &&
            definition.Validate(tileID, isLand))
        {
            target = new IdleTarget(IdleTarget.Clicked, map, loc);
            return true;
        }

        target = default;
        return false;
    }

    /// <summary>Checks the map itself at the target, so named locations and API requests are held to the same rule as clicks.</summary>
    public override bool ValidateTarget(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, out string error)
    {
        var (system, definition) = Get(def);
        var map = target.Map;

        if (map == null || map == Map.Internal)
        {
            error = "That spot is not on a valid map.";
            return false;
        }

        var x = target.Location.X;
        var y = target.Location.Y;

        // Deep-water specials (maps, bottles, nets) arrive on a sea serpent that must be killed, which cannot happen offline.
        if (system is Fishing && FishingIdleRules.IsDeepWater(map, x, y))
        {
            error = "Idle fishing only works in shallow water near the shore.";
            return false;
        }

        if (definition.Validate(map.Tiles.GetLandTile(x, y).ID & TileData.MaxLandValue, true))
        {
            error = null;
            return true;
        }

        foreach (var tile in map.Tiles.GetStaticTiles(x, y))
        {
            if (definition.Validate(tile.ID, false))
            {
                error = null;
                return true;
            }
        }

        error = "There is nothing to harvest at that spot for this task.";
        return false;
    }

    public override bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, out string error)
    {
        var system = Get(def).System;
        var tools = FindTools(pm, system);

        if (tools.Count == 0)
        {
            error = "You need a working tool for this task in your backpack or hands.";
            return false;
        }

        // The harvest system's own gate: worn-out tools, riding, polymorph.
        if (!system.CheckHarvest(pm, tools[0]))
        {
            error = "You cannot do that task right now.";
            return false;
        }

        error = null;
        return true;
    }

    public override IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target)
    {
        var (system, definition) = Get(def);
        return new HarvestIdleRun(pm, def, system, definition, target, FindTools(pm, system));
    }

    /// <summary>Held tools first (the one online harvesting would wear), then backpack.</summary>
    public static List<Item> FindTools(PlayerMobile pm, HarvestSystem system)
    {
        var tools = new List<Item>();

        AddIfTool(tools, pm.FindItemOnLayer(Layer.OneHanded), system);
        AddIfTool(tools, pm.FindItemOnLayer(Layer.TwoHanded), system);

        var pack = pm.Backpack;
        if (pack != null)
        {
            foreach (var item in pack.FindItemsByType<Item>())
            {
                AddIfTool(tools, item, system);
            }
        }

        return tools;
    }

    private static void AddIfTool(List<Item> tools, Item item, HarvestSystem system)
    {
        if (item?.Deleted != false || item is IUsesRemaining { UsesRemaining: <= 0 })
        {
            return;
        }

        var toolSystem = item switch
        {
            BaseHarvestTool t => t.HarvestSystem,
            BaseAxe a         => a.HarvestSystem,
            BasePoleArm p     => p.HarvestSystem,
            FishingPole       => Fishing.System,
            _                 => null
        };

        if (toolSystem == system)
        {
            tools.Add(item);
        }
    }
}

public class HarvestIdleRun : IdleTaskRun
{
    private readonly PlayerMobile _pm;
    private readonly IdleTaskDefinition _def;
    private readonly HarvestSystem _system;
    private readonly HarvestDefinition _definition;
    private readonly IdleTarget _target;
    private readonly List<Item> _tools;
    private readonly Region _region;
    private readonly HarvestVein _targetVein;
    private readonly int _unitsPerSuccess;
    private readonly int _successesAllowed;
    private readonly Dictionary<Type, bool> _stackable = new();
    private readonly List<(Type, double)> _shares = new();
    private readonly List<(Type, double)> _fishShares = new();

    public HarvestIdleRun(
        PlayerMobile pm, IdleTaskDefinition def, HarvestSystem system, HarvestDefinition definition, IdleTarget target,
        List<Item> tools
    )
    {
        _pm = pm;
        _def = def;
        _system = system;
        _definition = definition;
        _target = target;
        _tools = tools;
        _region = Region.Find(target.Location, target.Map);
        _targetVein = definition.GetVeinAt(target.Map, target.Location.X, target.Location.Y);

        // Online, Felucca yields the larger amount whenever the bank holds enough, which a fresh spot always does.
        _unitsPerSuccess = target.Map == Map.Felucca ? definition.ConsumedPerFeluccaHarvest : definition.ConsumedPerHarvest;

        long uses = 0;
        foreach (var tool in tools)
        {
            if (tool is not IUsesRemaining usable)
            {
                uses = int.MaxValue; // A tool without wear never runs out.
                break;
            }

            uses += usable.UsesRemaining;
        }

        _successesAllowed = (int)Math.Min(uses, int.MaxValue);
    }

    public override int SuccessesAllowed => _successesAllowed;

    public override string SuppliesExhaustedMessage => "Your tools wore out.";

    public override void Evaluate(double skillBase, double skillValue, IdleAttemptModel model)
    {
        var unitsConsumed = 0.0;

        if (_def.VeinMode == IdleVeinMode.Target && _targetVein != null)
        {
            unitsConsumed += AddVein(_targetVein, 1.0, skillBase, skillValue, model);
        }
        else
        {
            var veins = _definition.Veins;
            var total = 0.0;
            foreach (var vein in veins)
            {
                total += vein.VeinChance;
            }

            foreach (var vein in veins)
            {
                unitsConsumed += AddVein(vein, vein.VeinChance / total, skillBase, skillValue, model);
            }
        }

        model.SecondsPerAttempt = BaseSecondsPerAttempt + RelocateSecondsPerAttempt(unitsConsumed);
    }

    private double BaseSecondsPerAttempt
    {
        get
        {
            if (_def.SecondsPerAttempt > 0)
            {
                return _def.SecondsPerAttempt;
            }

            var counts = _definition.EffectCounts;
            var average = 0.0;
            foreach (var count in counts)
            {
                average += count;
            }

            return _definition.EffectDelay.TotalSeconds * Math.Max(1.0, average / Math.Max(1, counts.Length));
        }
    }

    /// <summary>Each spot holds MinTotal..MaxTotal units; walking to the next one is spread over the attempts that empty it.</summary>
    private double RelocateSecondsPerAttempt(double unitsConsumedPerAttempt)
    {
        var averageBank = (_definition.MinTotal + _definition.MaxTotal) / 2.0;
        return _def.RelocateSeconds > 0 && averageBank > 0
            ? _def.RelocateSeconds * unitsConsumedPerAttempt / averageBank
            : 0;
    }

    /// <summary>Mirrors HarvestSystem.MutateResource: fallback roll, then fallback if the primary is out of reach.</summary>
    private double AddVein(HarvestVein vein, double weight, double skillBase, double skillValue, IdleAttemptModel model)
    {
        var primary = vein.PrimaryResource;
        var fallback = vein.FallbackResource;

        if (fallback == null)
        {
            return AddResource(primary, weight, skillBase, skillValue, model);
        }

        var fallbackRoll = Math.Clamp(vein.ChanceToFallback, 0.0, 1.0);
        var primaryPath = skillValue < primary.ReqSkill || skillValue < primary.MinSkill ? fallback : primary;

        return AddResource(fallback, weight * fallbackRoll, skillBase, skillValue, model) +
               AddResource(primaryPath, weight * (1.0 - fallbackRoll), skillBase, skillValue, model);
    }

    /// <summary>Mirrors FinishHarvesting: no check below ReqSkill, else CheckSkill(MinSkill, MaxSkill).</summary>
    private double AddResource(HarvestResource resource, double weight, double skillBase, double skillValue, IdleAttemptModel model)
    {
        if (resource == null || weight <= 0 || skillBase < resource.ReqSkill)
        {
            return 0;
        }

        // Unclamped on purpose: below MinSkill is "too difficult" (no gain), at MaxSkill+ is "no challenge" (no gain).
        var chance = resource.MaxSkill > resource.MinSkill
            ? (skillValue - resource.MinSkill) / (resource.MaxSkill - resource.MinSkill)
            : 1.0;

        model.Checks.Add((weight, chance));

        var successes = weight * Math.Clamp(chance, 0.0, 1.0);
        var unitsConsumed = 0.0;

        _shares.Clear();
        GetCatchShares(resource, skillBase, skillValue, _shares);

        foreach (var (type, share) in _shares)
        {
            if (share <= 0)
            {
                continue;
            }

            var units = IsStackable(type) ? _unitsPerSuccess : 1;
            model.AddYield(type, successes * share * units);
            unitsConsumed += successes * share * units;
        }

        var bonuses = _definition.BonusResources;
        if (bonuses != null)
        {
            foreach (var bonus in bonuses)
            {
                if (bonus.Type != null && skillBase >= bonus.ReqSkill)
                {
                    model.AddYield(bonus.Type, successes * bonus.Chance / 100.0);
                }
            }
        }

        return unitsConsumed;
    }

    /// <summary>
    /// Mining's ore-or-granite choice (Mining.GetResourceType): Types[0], or Types[1] 10% of the time for a 100+ miner
    /// who learned stone mining and toggled it on. Other harvest systems pick uniformly among the resource's types.
    /// Elementals (Types[2]) only come from a Gargoyle's pickaxe and are never spawned offline.
    /// </summary>
    private bool IsMiningOre => _system is Mining && _definition == Mining.System.OreAndStone;

    private double GetAlternateTypeChance(double skillBase) =>
        IsMiningOre && skillBase >= 100.0 && _pm.StoneMining && _pm.ToggleMiningStone ? 0.1 : 0.0;

    private double GetTypeShare(int count, int index, double stoneChance)
    {
        if (!IsMiningOre)
        {
            return 1.0 / count;
        }

        return index switch
        {
            0 => count > 1 ? 1.0 - stoneChance : 1.0,
            1 => stoneChance,
            _ => 0.0
        };
    }

    /// <summary>
    /// What one success turns into. Fishing replaces the whole of HarvestSystem.MutateType with its catch table, so the
    /// region swap does not apply there; every other system keeps the base GetResourceType + region swap.
    /// </summary>
    private void GetCatchShares(HarvestResource resource, double skillBase, double skillValue, List<(Type, double)> shares)
    {
        var types = resource.Types;

        if (_system is Fishing)
        {
            foreach (var type in types)
            {
                _fishShares.Clear();
                FishingIdleRules.AddCatchShares(type, skillBase, skillValue, _fishShares);

                foreach (var (caught, share) in _fishShares)
                {
                    shares.Add((caught, share / types.Length));
                }
            }

            return;
        }

        var stoneChance = GetAlternateTypeChance(skillBase);
        for (var i = 0; i < types.Length; i++)
        {
            shares.Add((_region.GetResource(types[i]), GetTypeShare(types.Length, i, stoneChance)));
        }
    }

    private bool IsStackable(Type type)
    {
        if (!_stackable.TryGetValue(type, out var stackable))
        {
            Item sample = null;
            try
            {
                sample = type.CreateInstance<Item>();
                stackable = sample?.Stackable == true;
            }
            catch
            {
                stackable = false;
            }

            sample?.Delete();
            _stackable[type] = stackable;
        }

        return stackable;
    }

    /// <summary>Wears tools in the same order online harvesting would use them, one use per success.</summary>
    public override void ConsumeSupplies(int successes)
    {
        if (_successesAllowed == int.MaxValue)
        {
            return;
        }

        foreach (var tool in _tools)
        {
            if (successes <= 0)
            {
                break;
            }

            if (tool.Deleted || tool is not IUsesRemaining usable)
            {
                continue;
            }

            var used = Math.Min(successes, usable.UsesRemaining);
            usable.UsesRemaining -= used;
            usable.ShowUsesRemaining = true;
            successes -= used;

            if (usable.UsesRemaining <= 0)
            {
                tool.Delete();
            }
        }
    }

    public override void Describe(List<string> notes)
    {
        notes.Add(
            _def.VeinMode == IdleVeinMode.Target
                ? $"Veins: only the spot's own vein ({VeinName(_targetVein)})"
                : $"Veins: world vein mix (spot's own vein is {VeinName(_targetVein)})"
        );
        notes.Add($"Units per success: {_unitsPerSuccess} ({(_target.Map == Map.Felucca ? "Felucca double" : "normal")})");
        notes.Add(
            $"Spot capacity {_definition.MinTotal}-{_definition.MaxTotal} units, {_def.RelocateSeconds:F0}s to move on when empty"
        );
        notes.Add(
            _successesAllowed == int.MaxValue
                ? $"Tools: {_tools.Count}, no wear"
                : $"Tools: {_tools.Count}, {_successesAllowed:N0} uses (1 per success)"
        );

        if (_system is Fishing)
        {
            notes.Add("Catches: Fishing's own shallow-water table (footwear, magic fish); no deep-water specials offline");
        }

        if (IsMiningOre)
        {
            notes.Add(
                GetAlternateTypeChance(100.0) > 0
                    ? "Stone mining on: 10% granite at 100+ skill"
                    : "Stone mining off: ore only"
            );
        }
    }

    private static string VeinName(HarvestVein vein)
    {
        var type = vein?.PrimaryResource?.Types is { Length: > 0 } types ? types[0] : null;
        return type?.Name ?? "none";
    }
}
