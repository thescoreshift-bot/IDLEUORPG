using System;
using Server.Commands;
using Server.Mobiles;
using Server.Targeting;
using Server.Text;

namespace Server.Custom.Idle;

public static class IdleCommands
{
    public static void Configure()
    {
        CommandSystem.Register("Idle", AccessLevel.Player, Idle_OnCommand);
        CommandSystem.Register("IdleSim", AccessLevel.GameMaster, IdleSim_OnCommand);
    }

    [Usage("Idle [stop | status | resume | list | recipes [filter] | at <location> [order] | smelt <amount> | craft <recipe> <amount> [material]]")]
    [Description(
        "Choose the spot your character works while you are logged out. With no argument, target the spot. " +
        "smelt/craft start an order where you stand; 'at <location>' can be followed by the same order."
    )]
    public static void Idle_OnCommand(CommandEventArgs e)
    {
        if (e.Mobile is not PlayerMobile pm)
        {
            return;
        }

        if (!IdleSystem.Settings.Enabled)
        {
            pm.SendMessage("Idle tasks are disabled.");
            return;
        }

        switch (e.Length > 0 ? e.GetString(0).ToLowerInvariant() : "")
        {
            case "":
                {
                    pm.SendMessage("Target the spot you want to work while you are away.");
                    pm.Target = new IdleSpotTarget();
                    break;
                }
            case "stop":
                {
                    pm.SendMessage(IdleSystem.StopTask(pm) ? "Your idle task has been cleared." : "You have no idle task.");
                    break;
                }
            case "status":
                {
                    SendStatus(pm);
                    break;
                }
            case "list":
                {
                    SendLocations(pm);
                    break;
                }
            case "at" when e.Length > 1:
                {
                    StartAt(pm, e);
                    break;
                }
            case "smelt" or "craft":
                {
                    StartHere(pm, e);
                    break;
                }
            case "resume":
                {
                    if (IdleOnlineWork.IsWorking(pm))
                    {
                        pm.SendMessage("You are already working.");
                    }
                    else if (IdleOnlineWork.TryStart(pm, out var error))
                    {
                        SendStarted(pm);
                    }
                    else
                    {
                        pm.SendMessage(0x22, error ?? "You cannot work right now.");
                    }

                    break;
                }
            case "recipes":
                {
                    SendRecipes(pm, e.Length > 1 ? e.GetString(1) : null);
                    break;
                }
            default:
                {
                    pm.SendMessage(
                        "Usage: [Idle [stop | status | resume | list | recipes [filter] | at <location> [order] | smelt <amount> | craft <recipe> <amount> [material]]"
                    );
                    break;
                }
        }
    }

    /// <summary>Task kind that carries out an order verb.</summary>
    private static string KindForVerb(string verb) =>
        verb switch
        {
            "smelt" => "Smelt",
            "craft" => "Craft",
            _       => null
        };

    /// <summary>Reads "smelt &lt;amount&gt;" or "craft &lt;recipe&gt; &lt;amount&gt; [material]" starting at argument <paramref name="start"/>.</summary>
    private static bool TryParseOrder(CommandEventArgs e, int start, out string verb, out IdleOrder order, out string error)
    {
        verb = e.Length > start ? e.GetString(start).ToLowerInvariant() : null;
        order = null;
        error = null;

        if (verb == null)
        {
            return true;
        }

        switch (verb)
        {
            case "smelt" when e.Length > start + 1:
                {
                    order = new IdleOrder { Requested = e.GetInt32(start + 1) };
                    break;
                }
            case "craft" when e.Length > start + 2:
                {
                    order = new IdleOrder
                    {
                        RecipeId = e.GetString(start + 1),
                        Requested = e.GetInt32(start + 2),
                        Material = e.Length > start + 3 ? e.GetString(start + 3) : null
                    };
                    break;
                }
            default:
                {
                    error = "Orders are: smelt <amount>, or craft <recipe> <amount> [material].";
                    return false;
                }
        }

        if (order.Requested is <= 0 or > 100000)
        {
            error = "The amount must be between 1 and 100000.";
            return false;
        }

        return true;
    }

    private static void StartAt(PlayerMobile pm, CommandEventArgs e)
    {
        var locationId = e.GetString(1);

        if (!TryParseOrder(e, 2, out var verb, out var order, out var error))
        {
            pm.SendMessage(0x22, error);
            return;
        }

        if (verb == null)
        {
            if (IdleSystem.TryStartTaskAt(pm, locationId, out error))
            {
                SendStarted(pm);
            }
            else
            {
                pm.SendMessage(0x22, error);
            }

            return;
        }

        if (!IdleSystem.Locations.TryGetValue(locationId, out var loc))
        {
            pm.SendMessage(0x22, "Unknown idle location.");
            return;
        }

        var kind = KindForVerb(verb);
        string firstError = null;

        foreach (var taskId in loc.AllTaskIds())
        {
            if (IdleSystem.GetTask(taskId)?.Kind.InsensitiveEquals(kind) != true)
            {
                continue;
            }

            if (IdleSystem.TryStartTaskAt(pm, locationId, out error, taskId, order))
            {
                SendStarted(pm);
                return;
            }

            firstError ??= error;
        }

        pm.SendMessage(0x22, firstError ?? "That cannot be done at that location.");
    }

    private static void StartHere(PlayerMobile pm, CommandEventArgs e)
    {
        if (!TryParseOrder(e, 0, out var verb, out var order, out var error))
        {
            pm.SendMessage(0x22, error);
            return;
        }

        var kind = KindForVerb(verb);
        var target = new IdleTarget(IdleTarget.Here, pm.Map, pm.Location);
        string firstError = null;

        foreach (var def in IdleSystem.Tasks.Values)
        {
            if (!def.Kind.InsensitiveEquals(kind))
            {
                continue;
            }

            if (IdleSystem.TryStartTask(pm, def.Id, target, out error, order))
            {
                SendStarted(pm);
                return;
            }

            firstError ??= error;
        }

        pm.SendMessage(0x22, firstError ?? "That idle task is not available.");
    }

    private static void SendStarted(PlayerMobile pm)
    {
        var state = IdleSystem.GetState(pm);
        var def = IdleSystem.GetTask(state?.TaskId);
        if (def == null)
        {
            return;
        }

        var range = IdleTaskHandler.Get(def.Kind).GetRange(def);
        pm.SendMessage(0x59, $"Idle task set: {def.Name} at {state.Target.Location} ({state.Target.LocationId}).");

        if (state.Order != null)
        {
            pm.SendMessage(0x59, $"Order: {DescribeOrder(state.Order)}.");
        }

        if (IdleOnlineWork.IsWorking(pm))
        {
            pm.SendMessage(0x59, $"You start working. Moving more than {range} tiles away, fighting, or working by hand stops it.");
            pm.SendMessage(0x59, "Log out here and your character keeps working while you are away.");
        }
        else
        {
            pm.SendMessage(0x59, $"Log out within {range} tiles of this spot and your character will keep working while you are away.");
        }
    }

    private static void SendStatus(PlayerMobile pm)
    {
        var state = IdleSystem.GetState(pm);
        var def = IdleSystem.GetTask(state?.TaskId);

        if (def == null)
        {
            pm.SendMessage("You have no idle task. Use [Idle to choose a spot.");
            return;
        }

        pm.SendMessage($"Idle task: {def.Name} at {state.Target.Location} on {state.Target.Map} ({state.Target.LocationId}).");

        if (state.Order != null)
        {
            pm.SendMessage($"Order: {DescribeOrder(state.Order)}, {state.Order.Remaining:N0} of {state.Order.Requested:N0} left.");
        }

        var session = IdleOnlineWork.GetSession(pm);
        if (session != null)
        {
            var rate = IdleOnlineWork.GetLiveRate(pm, state, def);
            var since = IdleSummaryGump.FormatTime(Core.Now - IdleOnlineWork.GetWorkingSince(pm));
            pm.SendMessage(0x59, $"Working now for {since} at {rate:P0} speed: {session.Attempts:N0} attempts, {session.Successes:N0} successes.");

            foreach (var item in session.Items)
            {
                if (!item.Discarded)
                {
                    pm.SendMessage(0x59, $"  {item.Amount:N0} {item.Name} (pack {item.ToPack:N0}, bank {item.ToBank:N0})");
                }
            }
        }
        else
        {
            pm.SendMessage("Not working right now. [Idle resume starts again at the spot.");
        }

        var result = IdleSystem.Evaluate(pm, state, pm.Map, pm.Location);
        if (result == IdleLogoutResult.Eligible)
        {
            pm.SendMessage(0x59, "If you log out here, your character will keep working.");
        }
        else
        {
            pm.SendMessage(0x22, $"If you log out now: {IdleSystem.GetResultMessage(result)}");
        }
    }

    /// <summary>Recipes of every craft task, with this character's current success chance.</summary>
    private static void SendRecipes(PlayerMobile pm, string filter)
    {
        var shown = 0;

        foreach (var def in IdleSystem.Tasks.Values)
        {
            var system = def.Kind.InsensitiveEquals("Craft") ? CraftIdleHandler.GetSystem(def) : null;
            if (system == null)
            {
                continue;
            }

            pm.SendMessage(0x35, $"{def.Name} recipes ([Idle craft <id> <amount> [material]):");

            foreach (var recipe in IdleRecipes.GetRecipes(system))
            {
                if (filter != null && !recipe.Id.InsensitiveContains(filter) && !recipe.Name.InsensitiveContains(filter) &&
                    !recipe.Group.InsensitiveContains(filter))
                {
                    continue;
                }

                using var cost = ValueStringBuilder.Create();
                foreach (var res in recipe.Resources)
                {
                    if (cost.Length > 0)
                    {
                        cost.Append(", ");
                    }

                    cost.Append($"{res.Amount} {res.Name}");
                }

                var chance = Math.Clamp(recipe.CraftItem.GetSuccessChance(pm, null, system, false, out _), 0.0, 1.0);
                pm.SendMessage(
                    $"{recipe.Id}: {recipe.Name} ({recipe.Group}), {recipe.MinSkill:F1}-{recipe.MaxSkill:F1}, {cost.AsSpan()}, your chance {chance:P0}"
                );
                shown++;
            }
        }

        if (shown == 0)
        {
            pm.SendMessage("No recipes match.");
        }
    }

    public static string DescribeOrder(IdleOrder order) =>
        order.RecipeId == null
            ? $"smelt {order.Requested:N0} ore"
            : $"craft {order.Requested:N0} {order.RecipeId}{(order.Material != null ? $" from {order.Material}" : "")}";

    private static void SendLocations(PlayerMobile pm)
    {
        if (IdleSystem.Locations.Count == 0)
        {
            pm.SendMessage("There are no named idle locations.");
            return;
        }

        foreach (var loc in IdleSystem.Locations.Values)
        {
            using var tasks = ValueStringBuilder.Create();
            foreach (var taskId in loc.AllTaskIds())
            {
                if (tasks.Length > 0)
                {
                    tasks.Append(", ");
                }

                tasks.Append(IdleSystem.GetTask(taskId)?.Name ?? taskId);
            }

            pm.SendMessage($"{loc.Id}: {loc.Name} ({tasks.AsSpan()}) at {loc.Location} on {loc.Map}");
        }
    }

    private class IdleSpotTarget : Target
    {
        public IdleSpotTarget() : base(12, true, TargetFlags.None)
        {
        }

        protected override void OnTarget(Mobile from, object targeted)
        {
            if (from is not PlayerMobile pm)
            {
                return;
            }

            string firstError = null;

            foreach (var def in IdleSystem.Tasks.Values)
            {
                var handler = IdleTaskHandler.Get(def.Kind);
                if (handler?.TryCreateTarget(pm, def, targeted, out var target) != true)
                {
                    continue;
                }

                if (IdleSystem.TryStartTask(pm, def.Id, target, out var error))
                {
                    SendStarted(pm);
                    return;
                }

                firstError ??= error;
            }

            pm.SendMessage(0x22, firstError ?? "You cannot do an idle task there.");
        }
    }

    [Usage("IdleSim <hours> [preview]")]
    [Description(
        "Settles <hours> of fake offline time for a targeted player's idle task and prints the exact numbers and rules. " +
        "'preview' only reports and changes nothing."
    )]
    public static void IdleSim_OnCommand(CommandEventArgs e)
    {
        var hours = e.Length > 0 ? e.GetDouble(0) : 0;

        if (hours <= 0 || hours > 1000)
        {
            e.Mobile.SendMessage("Usage: [IdleSim <hours> [preview]   (hours between 0 and 1000)");
            return;
        }

        var preview = e.Length > 1 && e.GetString(1).InsensitiveEquals("preview");
        e.Mobile.SendMessage($"Target a player to simulate {hours:0.##} hour(s) offline{(preview ? " (preview only)" : "")}.");
        e.Mobile.Target = new IdleSimTarget(TimeSpan.FromHours(hours), preview);
    }

    private class IdleSimTarget : Target
    {
        private readonly TimeSpan _away;
        private readonly bool _preview;

        public IdleSimTarget(TimeSpan away, bool preview) : base(-1, false, TargetFlags.None)
        {
            _away = away;
            _preview = preview;
        }

        protected override void OnTarget(Mobile from, object targeted)
        {
            if (targeted is not PlayerMobile pm)
            {
                from.SendMessage("That is not a player.");
                return;
            }

            var state = IdleSystem.GetState(pm);
            if (state == null)
            {
                from.SendMessage("That player has no idle task.");
                return;
            }

            // Same rules as a real logout, checked where the character stands now.
            if (pm.Map != Map.Internal)
            {
                var result = IdleSystem.Evaluate(pm, state, pm.Map, pm.Location);
                if (result != IdleLogoutResult.Eligible)
                {
                    from.SendMessage(0x22, $"Not eligible: {IdleSystem.GetResultMessage(result)}");
                    return;
                }
            }

            var mode = _preview ? IdleSettleMode.Preview : IdleSettleMode.Simulate;
            var report = IdleSystem.Settle(pm, state, _away, mode);

            if (report == null)
            {
                from.SendMessage(0x22, "That player's idle task is no longer available.");
                return;
            }

            from.SendMessage(0x35, $"--- IdleSim: {pm.Name}, {report.TaskName}, {(_preview ? "PREVIEW (nothing changed)" : "APPLIED")} ---");

            foreach (var note in report.Notes)
            {
                from.SendMessage(0x3B2, note);
            }

            foreach (var used in report.Used)
            {
                from.SendMessage(0x59, $"Used: {used.Amount:N0} {used.Name}");
            }

            foreach (var item in report.Items)
            {
                if (item.Discarded)
                {
                    from.SendMessage(0x59, $"Discarded: {item.Amount:N0} {item.Name}");
                }
                else if (_preview)
                {
                    from.SendMessage(0x59, $"Would give: {item.Amount:N0} {item.Name}");
                }
                else
                {
                    from.SendMessage(
                        0x59,
                        $"Gave: {item.Amount:N0} {item.Name} (pack {item.ToPack:N0}, bank {item.ToBank:N0}, lost {item.Lost:N0})"
                    );
                }

                if (item.Exceptional > 0)
                {
                    from.SendMessage(0x59, $"  of which exceptional: {item.Exceptional:N0}");
                }
            }

            foreach (var move in report.MovedToBank)
            {
                from.SendMessage(0x22, $"Overloaded: moved {move.Amount:N0} {move.Name} ({move.Weight} stones) from pack to bank");
            }

            if (!_preview)
            {
                from.SendMessage(
                    0x59,
                    $"Weight: {report.WeightBefore} -> {report.WeightAfter} / {report.WeightLimit}, reserve {report.KeepFreeStones} (as the status bar shows it)"
                );
            }

            foreach (var skill in report.Skills)
            {
                from.SendMessage(0x59, $"Skill: {skill.Name} {skill.From:F1} -> {skill.To:F1} (+{skill.To - skill.From:F1})");
            }

            foreach (var stat in report.Stats)
            {
                from.SendMessage(0x59, $"Stat: {stat.Name} {stat.From} -> {stat.To}");
            }

            if (report.OrderStatus != null)
            {
                from.SendMessage(0x59, $"Order: {report.OrderStatus}");
            }

            if (report.StopReason != null)
            {
                from.SendMessage(0x22, $"Stopped early: {report.StopReason}");
            }

            if (!_preview)
            {
                IdleSystem.ShowReport(pm, report);
            }
        }
    }
}
