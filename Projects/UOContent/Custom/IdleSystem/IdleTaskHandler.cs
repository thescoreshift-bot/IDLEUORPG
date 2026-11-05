using System;
using System.Collections.Generic;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Code behind one <see cref="IdleTaskDefinition.Kind"/>. A handler knows how to validate a target and how to
/// describe one attempt as expected values; the settlement math (time, skill gain, delivery) is shared.
/// </summary>
public abstract class IdleTaskHandler
{
    private static readonly Dictionary<string, IdleTaskHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string kind, IdleTaskHandler handler) => _handlers[kind] = handler;

    public static IdleTaskHandler Get(string kind) =>
        kind != null && _handlers.TryGetValue(kind, out var handler) ? handler : null;

    /// <summary>Resolves data references (e.g. harvest system names). Returns false to disable the task.</summary>
    public abstract bool Bind(IdleTaskDefinition def, out string error);

    public abstract SkillName GetSkill(IdleTaskDefinition def);

    /// <summary>How far from the target a character may stand (and log out) and still work it.</summary>
    public abstract int GetRange(IdleTaskDefinition def);

    /// <summary>Checks that the target spot can be worked by this task (server-side, never trusts the client).</summary>
    public abstract bool ValidateTarget(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, out string error);

    /// <summary>Turns an in-game click into a target for this task. Returns false if the click does not fit the task.</summary>
    public virtual bool TryCreateTarget(PlayerMobile pm, IdleTaskDefinition def, object targeted, out IdleTarget target)
    {
        target = default;
        return false;
    }

    /// <summary>Checks that the character carries what the task needs (tools, bait, reagents).</summary>
    public abstract bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, out string error);

    /// <summary>Starts a settlement. The run reads supplies once and consumes them at the end.</summary>
    public abstract IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target);

    /// <summary>Checks an order (what to make, how many). Tasks that take no order reject one.</summary>
    public virtual bool ValidateOrder(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, out string error)
    {
        error = order == null ? null : "That task does not take an order.";
        return order == null;
    }

    /// <summary>Supplies check for tasks whose needs depend on the order (materials for a recipe).</summary>
    public virtual bool HasSupplies(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, out string error) =>
        HasSupplies(pm, def, out error);

    public virtual IdleTaskRun BeginRun(PlayerMobile pm, IdleTaskDefinition def, IdleTarget target, IdleOrder order) =>
        BeginRun(pm, def, target);

    /// <summary>
    /// Checks the work station (forge, anvil) around where the character stands or logged out. Called with the
    /// character's own position, so a handler may use the server's live check while the character is in the world.
    /// </summary>
    public virtual bool CheckStation(PlayerMobile pm, IdleTaskDefinition def, Map map, Point3D location, out string error)
    {
        error = null;
        return true;
    }
}

/// <summary>One settlement of a task. Lives only for the duration of a login.</summary>
public abstract class IdleTaskRun
{
    /// <summary>How many successful attempts the carried supplies allow. int.MaxValue = unlimited.</summary>
    public abstract int SuccessesAllowed { get; }

    /// <summary>Message shown when supplies ran out before the time did.</summary>
    public virtual string SuppliesExhaustedMessage => "You ran out of supplies.";

    /// <summary>Describes one attempt at the given skill level.</summary>
    public abstract void Evaluate(double skillBase, double skillValue, IdleAttemptModel model);

    /// <summary>Applies supply wear for the successes that actually happened.</summary>
    public abstract void ConsumeSupplies(int successes);

    /// <summary>Adds the task-specific rules that applied, for the staff simulation breakdown.</summary>
    public virtual void Describe(List<string> notes)
    {
    }
}

/// <summary>
/// A run that rolls every attempt with the real dice instead of expected values. Used where counts are small
/// (bounded by materials) and each result is its own item, e.g. crafting with its quality and maker's mark.
/// </summary>
public abstract class IdleDiscreteTaskRun : IdleTaskRun
{
    public override int SuccessesAllowed => int.MaxValue;

    public override void Evaluate(double skillBase, double skillValue, IdleAttemptModel model)
    {
    }

    public override void ConsumeSupplies(int successes)
    {
    }

    /// <summary>
    /// Makes one attempt. Returns false, without attempting, when work cannot go on (time, materials, tools, room,
    /// or the order ran out). Offline settlement loops it; live work calls it once per tick.
    /// </summary>
    public abstract bool Step(IdleDiscreteContext context);

    /// <summary>
    /// Delivers anything batched, and writes the stop reason and order progress. Safe to call after every step.
    /// </summary>
    public virtual void Complete(IdleDiscreteContext context)
    {
    }

    /// <summary>Works until a step says to stop.</summary>
    public void Run(IdleDiscreteContext context)
    {
        while (Step(context))
        {
        }

        Complete(context);
    }
}

/// <summary>
/// Expected outcome of one attempt. Each <see cref="Checks"/> entry is a skill check that happens with probability
/// <c>Weight</c> and succeeds with probability <c>Chance</c>, exactly what the online code would pass to
/// CheckSkill. Attempts with no check (e.g. skill below an ore's requirement) are simply missing weight.
/// </summary>
public class IdleAttemptModel
{
    public readonly List<(double Weight, double Chance)> Checks = new();

    /// <summary>Expected units of each item type per attempt.</summary>
    public readonly Dictionary<Type, double> Yield = new();

    /// <summary>Online seconds per attempt, including any amortized walking to fresh spots.</summary>
    public double SecondsPerAttempt { get; set; }

    public void Clear()
    {
        Checks.Clear();
        Yield.Clear();
        SecondsPerAttempt = 0;
    }

    public void AddYield(Type type, double amount)
    {
        if (type != null && amount > 0)
        {
            Yield[type] = Yield.GetValueOrDefault(type) + amount;
        }
    }

    /// <summary>Probability that an attempt succeeds (and therefore wears the tool).</summary>
    public double SuccessChance
    {
        get
        {
            var p = 0.0;
            foreach (var (weight, chance) in Checks)
            {
                p += weight * Math.Clamp(chance, 0.0, 1.0);
            }

            return p;
        }
    }
}
