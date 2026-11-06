using System.Collections.Generic;
using Server.Misc;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// One line of the checklist. <c>Blocking</c> lines must be <c>Ok</c> before the character sets off; the others are
/// warnings the player should know about (e.g. goods will go to the bank).
/// </summary>
public record IdleCheckLine(string Check, bool Ok, bool Blocking, string Message);

public record IdlePreflightInfo(string TaskId, string TaskName, bool Ready, IReadOnlyList<IdleCheckLine> Checks);

/// <summary>
/// "Can this character go and do this task?" checked before anything moves, as a plain checklist for the game and
/// for a web client. Where the task is done (target, forge, standing tile) is not part of it: that is picked later.
/// </summary>
public static class IdlePreflight
{
    public static IdlePreflightInfo Run(PlayerMobile pm, string taskId, IdleOrder order = null)
    {
        var def = IdleSystem.GetTask(taskId);
        var handler = IdleTaskHandler.Get(def?.Kind);
        var checks = new List<IdleCheckLine>();

        if (!IdleSystem.Settings.Enabled || def == null || handler == null)
        {
            checks.Add(new IdleCheckLine("Task", false, true, "That idle task is not available."));
            return new IdlePreflightInfo(taskId, def?.Name, false, checks);
        }

        checks.Add(new IdleCheckLine("Task", true, true, def.Name));
        checks.Add(CheckCharacter(pm));
        checks.Add(CheckBusy(pm, def));

        var online = pm.Map != null && pm.Map != Map.Internal;
        checks.Add(
            online
                ? new IdleCheckLine("In world", true, true, "In the world.")
                : new IdleCheckLine("In world", false, true, "Log in first: walking to work needs the character in the world.")
        );

        var orderOk = handler.ValidateOrder(pm, def, order, out var error);
        if (order != null || !orderOk)
        {
            checks.Add(new IdleCheckLine("Order", orderOk, true, orderOk ? DescribeOrder(order) : error));
        }

        // Supplies are only meaningful once the order itself makes sense (materials depend on the recipe).
        if (orderOk)
        {
            var supplied = handler.HasSupplies(pm, def, order, out error);
            checks.Add(new IdleCheckLine("Supplies", supplied, true, supplied ? "Tools and materials are ready." : error));
        }

        AddWeightChecks(pm, checks);

        var ready = true;
        foreach (var line in checks)
        {
            ready &= line.Ok || !line.Blocking;
        }

        return new IdlePreflightInfo(def.Id, def.Name, ready, checks);
    }

    /// <summary>
    /// The task of a kind ("Smelt", "Craft") that accepts the order, else the first task of that kind, so an order
    /// verb can name a task without knowing its id.
    /// </summary>
    public static IdleTaskDefinition FindTaskForOrder(PlayerMobile pm, string kind, IdleOrder order)
    {
        IdleTaskDefinition first = null;

        foreach (var def in IdleSystem.Tasks.Values)
        {
            if (!def.Kind.InsensitiveEquals(kind) || IdleTaskHandler.Get(def.Kind) is not { } handler)
            {
                continue;
            }

            if (handler.ValidateOrder(pm, def, order, out _))
            {
                return def;
            }

            first ??= def;
        }

        return first;
    }

    private static IdleCheckLine CheckCharacter(PlayerMobile pm) =>
        IdleRules.CheckCharacter(pm) switch
        {
            IdleLogoutResult.Dead     => new IdleCheckLine("Character", false, true, "You are dead."),
            IdleLogoutResult.Criminal => new IdleCheckLine("Character", false, true, "You are flagged criminal."),
            IdleLogoutResult.InCombat => new IdleCheckLine("Character", false, true, "You are fighting or in war mode."),
            _                         => new IdleCheckLine("Character", true, true, "Alive, not criminal, not fighting.")
        };

    private static IdleCheckLine CheckBusy(PlayerMobile pm, IdleTaskDefinition def)
    {
        var current = IdleSystem.GetTask(IdleSystem.GetState(pm)?.TaskId);

        if (IdleOnlineWork.IsWorking(pm))
        {
            return new IdleCheckLine(
                "Busy", false, true, $"You are already working ({current?.Name ?? "a task"}). Stop it first with [Idle stop."
            );
        }

        if (current != null && current != def)
        {
            return new IdleCheckLine("Busy", true, false, $"This replaces your current task ({current.Name}).");
        }

        return new IdleCheckLine("Busy", true, true, "Not working on anything else.");
    }

    private static string DescribeOrder(IdleOrder order) =>
        order.RecipeId == null
            ? $"Order: {order.Requested}."
            : $"Order: {order.Requested} x {order.RecipeId}{(order.Material == null ? "" : $" ({order.Material})")}.";

    /// <summary>
    /// An overloaded character cannot walk at all. A full pack is only a warning, because idle work sends what does
    /// not fit to the bank (IdleSettlement.Deliver); with the bank full as well there is nowhere to put anything.
    /// Staff are exempt, as in the settlement.
    /// </summary>
    private static void AddWeightChecks(PlayerMobile pm, List<IdleCheckLine> checks)
    {
        if (pm.AccessLevel >= AccessLevel.GameMaster)
        {
            return;
        }

        if (StaminaSystem.IsOverloaded(pm))
        {
            checks.Add(new IdleCheckLine("Weight", false, true, "You are carrying too much to walk. Drop or bank something."));
            return;
        }

        var carried = IdleSettlement.CarriedWeight(pm);
        var limit = pm.MaxWeight - System.Math.Max(0, IdleSystem.Settings.KeepFreeStones);

        if (carried < limit)
        {
            checks.Add(new IdleCheckLine("Weight", true, true, $"Room for {limit - carried} stones."));
            return;
        }

        var bank = pm.BankBox;
        if (bank == null || bank.TotalItems >= bank.MaxItems)
        {
            checks.Add(
                new IdleCheckLine("Weight", false, true, "Your pack is full and so is your bank box: there is nowhere to put what you make.")
            );
            return;
        }

        checks.Add(new IdleCheckLine("Weight", true, false, "Your pack is full: what you make will go to your bank box."));
    }
}
