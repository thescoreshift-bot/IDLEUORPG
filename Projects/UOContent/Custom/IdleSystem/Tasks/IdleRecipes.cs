using System;
using System.Collections.Generic;
using System.Reflection;
using Server.Engines.Craft;
using Server.Logging;

namespace Server.Custom.Idle;

/// <summary>One material a recipe needs, per attempt.</summary>
public record IdleRecipeResource(string TypeName, string Name, int Amount);

/// <summary>A material the main resource can be swapped for (e.g. colored ingots) and the skill it needs.</summary>
public record IdleRecipeMaterial(string TypeName, string Name, double RequiredSkill);

/// <summary>A craftable recipe as plain data, read from the live craft table. Also the shape a web API can return.</summary>
public record IdleRecipe(
    string Id,
    string Name,
    string Group,
    SkillName Skill,
    double MinSkill,
    double MaxSkill,
    IReadOnlyList<IdleRecipeResource> Resources,
    IReadOnlyList<IdleRecipeMaterial> Materials,
    bool NeedsRecipeScroll
)
{
    /// <summary>The live entry this was read from.</summary>
    public CraftItem CraftItem { get; init; }
}

/// <summary>
/// Recipes of a craft system, read from its live table (e.g. DefBlacksmithy) so ids, skill ranges, and costs always
/// match online crafting. A recipe id is the crafted item's type name, e.g. "PlateChest".
/// </summary>
public static class IdleRecipes
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleRecipes));

    private static readonly Dictionary<CraftSystem, List<IdleRecipe>> _cache = new();

    /// <summary>
    /// Reads every craft task's recipe list once at startup. The first read walks the whole craft table and its names
    /// (about half a second for blacksmithing), which would otherwise stall the loop on the first player's check.
    /// </summary>
    public static void Warm()
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var recipes = 0;

        foreach (var def in IdleSystem.Tasks.Values)
        {
            if (def.Kind.InsensitiveEquals("Craft"))
            {
                recipes += GetRecipes(FindSystem(def.CraftSystem)).Count;
            }
        }

        if (recipes > 0)
        {
            logger.Information(
                "Idle recipes: {Count} read in {Ms:0} ms",
                recipes, System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds
            );
        }
    }

    /// <summary>The craft system behind a name like "Blacksmithy" (class DefBlacksmithy), or null.</summary>
    public static CraftSystem FindSystem(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var type = AssemblyHandler.FindTypeByName($"Def{name}") ?? AssemblyHandler.FindTypeByName(name);
        var property = type?.GetProperty("CraftSystem", BindingFlags.Public | BindingFlags.Static);
        return property?.GetValue(null) as CraftSystem;
    }

    /// <summary>Recipes of the craft task <paramref name="taskId"/>; empty if it is not a craft task. For external callers.</summary>
    public static IReadOnlyList<IdleRecipe> GetRecipes(string taskId)
    {
        var def = IdleSystem.GetTask(taskId);
        return def?.Kind.InsensitiveEquals("Craft") == true ? GetRecipes(FindSystem(def.CraftSystem)) : [];
    }

    public static IReadOnlyList<IdleRecipe> GetRecipes(CraftSystem system)
    {
        if (system == null)
        {
            return [];
        }

        if (_cache.TryGetValue(system, out var list))
        {
            return list;
        }

        list = new List<IdleRecipe>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in system.CraftItems)
        {
            if (!IsIdleCraftable(item) || !seen.Add(item.ItemType.Name))
            {
                continue;
            }

            var main = FindMainSkill(system, item);

            var resources = new List<IdleRecipeResource>();
            foreach (var res in item.Resources)
            {
                resources.Add(new IdleRecipeResource(res.ItemType.Name, GetTypeName(res.ItemType), res.Amount));
            }

            var materials = new List<IdleRecipeMaterial>();
            var subRes = item.UseSubRes2 ? system.CraftSubRes2 : system.CraftSubRes;
            if (subRes.Init && item.Resources.Exists(r => r.ItemType == subRes.ResType))
            {
                foreach (var sub in subRes)
                {
                    materials.Add(new IdleRecipeMaterial(sub.ItemType.Name, GetTypeName(sub.ItemType), sub.RequiredSkill));
                }
            }

            list.Add(
                new IdleRecipe(
                    item.ItemType.Name,
                    item.NameString ?? Localization.GetText(item.NameNumber) ?? item.ItemType.Name,
                    item.GroupNameString ?? Localization.GetText(item.GroupNameNumber) ?? "",
                    system.MainSkill,
                    main?.MinSkill ?? 0,
                    main?.MaxSkill ?? 0,
                    resources,
                    materials,
                    item.Recipe != null
                )
                {
                    CraftItem = item
                }
            );
        }

        _cache[system] = list;
        return list;
    }

    public static IdleRecipe Find(CraftSystem system, string id)
    {
        foreach (var recipe in GetRecipes(system))
        {
            if (recipe.Id.InsensitiveEquals(id))
            {
                return recipe;
            }
        }

        return null;
    }

    /// <summary>
    /// Recipes that work unattended: a plain item from materials. Skipped: custom crafts (runic effects, special
    /// results), anything costing mana/stamina/hits, "use all resources" batches, and other expansions.
    /// </summary>
    private static bool IsIdleCraftable(CraftItem item) =>
        item.ItemType != null && !typeof(CustomCraft).IsAssignableFrom(item.ItemType) && item.Mana <= 0 &&
        item.Stam <= 0 && item.Hits <= 0 && !item.UseAllRes && !item.NeedHeat && !item.NeedOven && !item.NeedMill &&
        item.Resources.Count > 0 && Core.Expansion >= item.RequiredExpansion;

    private static CraftSkill FindMainSkill(CraftSystem system, CraftItem item)
    {
        foreach (var skill in item.Skills)
        {
            if (skill.SkillToMake == system.MainSkill)
            {
                return skill;
            }
        }

        return null;
    }

    public static string GetTypeName(Type type) => Localization.GetText(CraftItem.LabelNumber(type)) ?? type.Name;
}
