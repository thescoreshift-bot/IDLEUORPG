using System;
using System.Collections.Generic;
using Server.Engines.Craft;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Crafting from a live craft table (e.g. DefBlacksmithy). Each attempt follows CraftItem: the exceptional roll,
/// the skill checks and success roll, material use (full on success, the era's failure share on failure), one tool
/// use, and on success the real item with its quality and maker's mark.
/// </summary>
public class CraftIdleHandler : IdleTaskHandler
{
    public static void Configure() => Register("Craft", new CraftIdleHandler());

    // The craft systems are created during Initialize, possibly after the catalog loads, so they are looked up on use.
    public static CraftSystem GetSystem(IdleTaskDefinition def) => IdleRecipes.FindSystem(def.CraftSystem);

    public override bool Bind(IdleTaskDefinition def, out string error)
    {
        var type = AssemblyHandler.FindTypeByName($"Def{def.CraftSystem}");
        error = type != null && typeof(CraftSystem).IsAssignableFrom(type) ? null : $"unknown craft system '{def.CraftSystem}'";
        return error == null;
    }

    public override SkillName GetSkill(IdleTaskDefinition def) => GetSystem(def)?.MainSkill ?? SkillName.Blacksmith;

    public override int GetRange(IdleTaskDefinition def) => SmithyStations.Range;

    public override bool ValidateTarget(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, out string error)
    {
        error = target.Map == null || target.Map == Map.Internal ? "That is not a valid spot." : null;
        return error == null;
    }

    public override bool CheckStation(PlayerMobile pm, IdleTaskDefinition def, Map map, Point3D location, out string error)
    {
        error = null;

        if (GetSystem(def) is not DefBlacksmithy)
        {
            return true;
        }

        SmithyStations.Find(pm, map, location, out var anvil, out var forge);
        if (anvil && forge)
        {
            return true;
        }

        error = "You must be near an anvil and a forge to smith items.";
        return false;
    }

    public override bool UsesStation(IdleTaskDefinition def) => GetSystem(def) is DefBlacksmithy;

    public override string ExplainStation(IdleTaskDefinition def, Map map, Point3D location) =>
        UsesStation(def) ? SmithyStations.Explain(map, location) : null;

    public override bool ValidateOrder(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, out string error)
    {
        var system = GetSystem(def);
        if (order?.RecipeId == null || order.Requested <= 0)
        {
            error = "Say what to craft and how many.";
            return false;
        }

        var recipe = IdleRecipes.Find(system, order.RecipeId);
        if (recipe == null)
        {
            error = $"There is no recipe called '{order.RecipeId}'. Use [Idle recipes to list them.";
            return false;
        }

        order.RecipeId = recipe.Id;

        if (!TryResolveMaterial(pm, system, recipe, order.Material, out var typeRes, out error))
        {
            return false;
        }

        order.Material = typeRes?.Name;

        var item = recipe.CraftItem;
        var chance = item.GetSuccessChance(pm, typeRes, system, false, out var allRequiredSkills);

        if (!allRequiredSkills || chance <= 0.0)
        {
            error = "You don't have the required skills to attempt this item.";
            return false;
        }

        if (item.Recipe != null && !pm.HasRecipe(item.Recipe))
        {
            error = "You must learn that recipe from a scroll.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The material to use in place of the recipe's main resource (e.g. "DullCopperIngot" for IronIngot), with the
    /// same skill requirement the craft menu enforces. Null material = the recipe's own resource.
    /// </summary>
    public static bool TryResolveMaterial(
        PlayerMobile pm, CraftSystem system, IdleRecipe recipe, string material, out Type typeRes, out string error)
    {
        typeRes = null;
        error = null;

        if (string.IsNullOrEmpty(material))
        {
            return true;
        }

        foreach (var option in recipe.Materials)
        {
            if (!option.TypeName.InsensitiveEquals(material) && !option.Name.InsensitiveEquals(material))
            {
                continue;
            }

            if (pm.Skills[system.MainSkill].Base < option.RequiredSkill)
            {
                error = $"You need {option.RequiredSkill:F1} {system.MainSkill} to work {option.Name}.";
                return false;
            }

            typeRes = AssemblyHandler.FindTypeByName(option.TypeName);
            return true;
        }

        error = recipe.Materials.Count == 0
            ? "That recipe cannot be made from other materials."
            : $"'{material}' is not a material for that recipe.";
        return false;
    }

    public override bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, out string error)
    {
        error = "Say what to craft and how many.";
        return false;
    }

    public override bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, out string error)
    {
        var system = GetSystem(def);
        var recipe = IdleRecipes.Find(system, order?.RecipeId);

        if (recipe == null)
        {
            error = "That recipe is no longer available.";
            return false;
        }

        if (!TryResolveMaterial(pm, system, recipe, order.Material, out var typeRes, out error))
        {
            return false;
        }

        if (!FindTools(pm, system, out _, out error))
        {
            return false;
        }

        foreach (var (types, amount, name) in GetCosts(system, recipe.CraftItem, typeRes))
        {
            if (CountMaterial(pm, types) < amount)
            {
                error = $"You don't have enough {name} (backpack and bank).";
                return false;
            }
        }

        error = null;
        return true;
    }

    public override IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target) => null;

    public override IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, IdleOrder order)
    {
        var system = GetSystem(def);
        var recipe = IdleRecipes.Find(system, order?.RecipeId);
        TryResolveMaterial(pm, system, recipe, order?.Material, out var typeRes, out _);
        return new CraftIdleRun(pm, def, system, recipe, typeRes, order);
    }

    /// <summary>
    /// Tools in the order CraftItem would use them: a tool in hand must be the one used (BaseTool.CheckTool), then
    /// tools in the backpack. A different system's tool in hand blocks crafting, as online.
    /// </summary>
    public static bool FindTools(PlayerMobile pm, CraftSystem system, out List<BaseTool> tools, out string error)
    {
        tools = new List<BaseTool>();
        error = null;

        foreach (var layer in (ReadOnlySpan<Layer>)[Layer.OneHanded, Layer.TwoHanded])
        {
            if (pm.FindItemOnLayer(layer) is BaseTool held and not AncientSmithyHammer)
            {
                if (held.CraftSystem != system)
                {
                    error = "If you have a tool equipped, you must use that tool.";
                    return false;
                }

                if (held.UsesRemaining > 0)
                {
                    tools.Add(held);
                }
            }
        }

        var pack = pm.Backpack;
        if (pack != null)
        {
            foreach (var tool in pack.FindItemsByType<BaseTool>())
            {
                if (tool.CraftSystem == system && tool.UsesRemaining > 0 && tool is not AncientSmithyHammer &&
                    !tools.Contains(tool))
                {
                    tools.Add(tool);
                }
            }
        }

        if (tools.Count == 0)
        {
            error = system switch
            {
                DefBlacksmithy => "You need a smith's hammer or tongs in your hand or backpack.",
                DefTailoring   => "You need a sewing kit in your hand or backpack.",
                DefTinkering   => "You need tinker's tools in your hand or backpack.",
                _              => "You need a working tool for that craft in your hand or backpack."
            };
            return false;
        }

        return true;
    }

    /// <summary>
    /// Per-attempt material cost, mirroring CraftItem.ConsumeRes: the main resource swapped for the chosen material,
    /// with the equivalent types it accepts.
    /// </summary>
    public static List<(Type[] Types, int Amount, string Name)> GetCosts(CraftSystem system, CraftItem item, Type typeRes)
    {
        var costs = new List<(Type[], int, string)>();
        var resCol = item.UseSubRes2 ? system.CraftSubRes2 : system.CraftSubRes;

        foreach (var res in item.Resources)
        {
            var baseType = res.ItemType;
            if (baseType == resCol.ResType && typeRes != null)
            {
                baseType = typeRes;
            }

            costs.Add((CraftItem.GetEquivalentTypes(baseType) ?? [baseType], res.Amount, IdleRecipes.GetTypeName(baseType)));
        }

        return costs;
    }

    public static int CountMaterial(PlayerMobile pm, Type[] types) =>
        (pm.Backpack?.GetAmount(types) ?? 0) + (pm.BankBox?.GetAmount(types) ?? 0);

    /// <summary>Takes materials from the backpack first, then the bank box.</summary>
    public static void ConsumeMaterial(PlayerMobile pm, Type[] types, int amount)
    {
        foreach (var container in (ReadOnlySpan<Container>)[pm.Backpack, pm.BankBox])
        {
            if (container == null)
            {
                continue;
            }

            foreach (var type in types)
            {
                if (amount <= 0)
                {
                    return;
                }

                amount -= container.ConsumeUpTo(type, amount);
            }
        }
    }
}

public class CraftIdleRun : IdleDiscreteTaskRun
{
    private readonly PlayerMobile _pm;
    private readonly IdleTaskDefinition _def;
    private readonly CraftSystem _system;
    private readonly IdleRecipe _recipe;
    private readonly Type _typeRes;
    private readonly IdleOrder _order;

    public CraftIdleRun(
        PlayerMobile pm, IdleTaskDefinition def, CraftSystem system, IdleRecipe recipe, Type typeRes, IdleOrder order
    )
    {
        _pm = pm;
        _def = def;
        _system = system;
        _recipe = recipe;
        _typeRes = typeRes;
        _order = order;
    }

    public override void Describe(List<string> notes)
    {
        if (_recipe == null)
        {
            return;
        }

        var item = _recipe.CraftItem;
        var chance = item.GetSuccessChance(_pm, _typeRes, _system, false, out _);
        var exceptional = Math.Max(0, item.GetExceptionalChance(_system, chance, _pm));

        notes.Add(
            $"Order: {_recipe.Name} ({_recipe.Id}), {_order.Remaining:N0} more of {_order.Requested:N0}, material {_typeRes?.Name ?? "default"}"
        );
        notes.Add(
            $"Recipe: {_system.MainSkill} {_recipe.MinSkill:F1}-{_recipe.MaxSkill:F1}; success {Math.Clamp(chance, 0, 1):P1}, exceptional {Math.Min(1, exceptional):P1} at the start"
        );

        foreach (var (types, amount, name) in CraftIdleHandler.GetCosts(_system, item, _typeRes))
        {
            notes.Add($"Cost: {amount} {name} per attempt; {CraftIdleHandler.CountMaterial(_pm, types):N0} in backpack and bank");
        }

        var failShare = _system.ConsumeOnFailure(_pm, item.Resources[0].ItemType, item) ? Core.UOTD ? "all" : "half" : "none";
        notes.Add($"Failure: {failShare} of the materials are lost; every attempt uses one tool charge; {_def.SecondsPerAttempt:0.##}s per attempt");
    }

    // Work state, kept between steps so live (online) work can run one attempt per tick.
    private bool _started;
    private List<(Type[] Types, int Amount, string Name)> _costs;
    private int[] _available;
    private List<BaseTool> _tools;
    private int[] _uses;
    private int _toolIndex;
    private int _toolUses;
    private int _reportedToolUses;
    private int _left;
    private int _made;
    private string _stop;
    private Skill _mainSkill;
    private CraftContext _craftContext;
    private Item _sample;
    private IdleItemLine _line;

    public override bool Step(IdleDiscreteContext context)
    {
        if (_recipe == null || _system == null)
        {
            context.Report.StopReason = "That recipe is no longer available.";
            return false;
        }

        var item = _recipe.CraftItem;

        if (!_started)
        {
            _started = true;
            _costs = CraftIdleHandler.GetCosts(_system, item, _typeRes);
            _left = _order.Remaining;
            _mainSkill = _pm.Skills[_system.MainSkill];
            _craftContext = _system.GetContext(_pm);
            ReadSupplies();
        }
        else if (context.Online)
        {
            // Live, the player may add or move ingots and tools between attempts.
            ReadSupplies();
        }

        if (_sample?.Deleted != false)
        {
            _sample = item.ItemType.CreateInstance<Item>();
            _line = context.GetLine(_sample);
        }

        if (_left <= 0)
        {
            return false;
        }

        while (_toolIndex < _tools.Count && _uses[_toolIndex] <= 0)
        {
            _toolIndex++;
        }

        if (_toolIndex >= _tools.Count)
        {
            _stop = "Your tools wore out.";
            return false;
        }

        for (var i = 0; i < _costs.Count; i++)
        {
            if (_available[i] < _costs[i].Amount)
            {
                _stop = $"You ran out of {_costs[i].Name}.";
                return false;
            }
        }

        if (!context.HasRoomFor(_sample))
        {
            _stop = "There is no more room in your backpack or bank box.";
            return false;
        }

        if (!context.TrySpend(_def.SecondsPerAttempt))
        {
            _stop = "time";
            return false;
        }

        context.Attempts++;

        // The craft timer rolls quality first (CheckSkills without gains); CompleteCraft then does the skill
        // checks and the success roll on the chance from before those checks.
        var chance = item.GetSuccessChance(_pm, _typeRes, _system, false, out _);
        var quality = item.GetExceptionalChance(_system, chance, _pm) > Utility.RandomDouble() ? 2 : 1;

        foreach (var craftSkill in item.Skills)
        {
            context.CheckSkill(craftSkill.SkillToMake, craftSkill.MinSkill, craftSkill.MaxSkill);
        }

        var success = chance > Utility.RandomDouble();
        var tool = _tools[_toolIndex];

        for (var i = 0; i < _costs.Count; i++)
        {
            var amount = _costs[i].Amount;
            if (!success)
            {
                if (!_system.ConsumeOnFailure(_pm, _costs[i].Types[0], item))
                {
                    amount = 0;
                }
                else if (!Core.UOTD)
                {
                    amount -= amount / 2;
                }
            }

            _available[i] -= amount;
            context.AddUsed(_costs[i].Name, amount);

            if (!context.Preview)
            {
                CraftIdleHandler.ConsumeMaterial(_pm, _costs[i].Types, amount);
            }
        }

        _uses[_toolIndex]--;
        _toolUses++;

        if (!context.Preview)
        {
            WearTool(tool, success);

            if (tool.Deleted)
            {
                context.Notify("Your tool has worn out.");
            }
        }

        if (!success)
        {
            return true;
        }

        context.Successes++;
        _made++;
        _left--;

        var result = context.Preview ? item.ItemType.CreateInstance<Item>() : Create(item, quality, _mainSkill, _craftContext, tool);

        if (quality == 2)
        {
            _line.Exceptional++;
            context.Notify($"You create an exceptional {_line.Name}.");
        }

        _line.Amount++;
        context.Deliver(result, _line);
        return true;
    }

    /// <summary>One live craft attempt with the craft system's own effect (the anvil sound for blacksmithing).</summary>
    public override double LiveStep(IdleDiscreteContext context)
    {
        var worked = Step(context);
        if (worked)
        {
            _system.PlayCraftEffect(_pm);
        }

        Complete(context);
        return worked ? _def.SecondsPerAttempt : -1;
    }

    private void ReadSupplies()
    {
        _available = new int[_costs.Count];
        for (var i = 0; i < _costs.Count; i++)
        {
            _available[i] = CraftIdleHandler.CountMaterial(_pm, _costs[i].Types);
        }

        CraftIdleHandler.FindTools(_pm, _system, out _tools, out _);
        _uses = new int[_tools.Count];
        for (var i = 0; i < _tools.Count; i++)
        {
            _uses[i] = _tools[i].UsesRemaining;
        }

        _toolIndex = 0;
    }

    public override void Complete(IdleDiscreteContext context)
    {
        if (!_started)
        {
            return;
        }

        _sample?.Delete();

        context.AddUsed("tool charges", _toolUses - _reportedToolUses);
        _reportedToolUses = _toolUses;

        if (_stop != null && _stop != "time")
        {
            context.Report.StopReason = _stop;
        }

        if (!context.Preview)
        {
            _order.Remaining = _left;
        }

        context.Report.OrderStatus = _left == 0
            ? $"{_order.Requested:N0} {_recipe.Name} made, order complete"
            : $"{_made:N0} made this time, {_left:N0} of {_order.Requested:N0} left{(_stop == "time" ? " (continues next time)" : "")}";
    }

    /// <summary>The real item, finished by its own OnCraft so quality, resource, and maker's mark match online.</summary>
    private Item Create(CraftItem item, int quality, Skill mainSkill, CraftContext craftContext, BaseTool tool)
    {
        // Offline there is no one to answer the maker's mark prompt, so an eligible crafter always marks, unless they
        // chose never to mark.
        var makersMark = quality == 2 && mainSkill.Base >= 100.0 && item.IsMarkable(item.ItemType) &&
                         craftContext?.MarkOption != CraftMarkOption.DoNotMark;

        var result = item.ItemType.CreateInstance<Item>();
        result.PlayerConstructed = true;

        if (result is ICraftable craftable)
        {
            craftable.OnCraft(quality, makersMark, _pm, _system, _typeRes, tool, item, 0);
        }

        return result;
    }

    /// <summary>Tool wear as CompleteCraft applies it, including the Ancient Smithy Hammer on a success.</summary>
    private void WearTool(BaseTool tool, bool success)
    {
        tool.UsesRemaining--;

        if (success && _system is DefBlacksmithy)
        {
            var hammer = _pm.FindItemOnLayer<AncientSmithyHammer>(Layer.OneHanded);
            if (hammer != null && hammer != tool)
            {
                hammer.UsesRemaining--;
                if (hammer.UsesRemaining < 1)
                {
                    hammer.Delete();
                }
            }
        }

        if (tool.UsesRemaining < 1 && tool.BreakOnDepletion)
        {
            tool.Delete();
        }
    }
}
