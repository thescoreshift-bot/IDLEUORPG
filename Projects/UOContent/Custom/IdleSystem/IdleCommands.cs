using System;
using Server.Commands;
using Server.Mobiles;
using Server.Targeting;

namespace Server.Custom.Idle;

public static class IdleCommands
{
    public static void Configure()
    {
        CommandSystem.Register("Idle", AccessLevel.Player, Idle_OnCommand);
        CommandSystem.Register("IdleSim", AccessLevel.GameMaster, IdleSim_OnCommand);
    }

    [Usage("Idle [stop | status | list | at <location>]")]
    [Description("Choose the spot your character works while you are logged out. With no argument, target the spot.")]
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
                    if (IdleSystem.TryStartTaskAt(pm, e.GetString(1), out var error))
                    {
                        SendStarted(pm);
                    }
                    else
                    {
                        pm.SendMessage(0x22, error);
                    }

                    break;
                }
            default:
                {
                    pm.SendMessage("Usage: [Idle [stop | status | list | at <location>]");
                    break;
                }
        }
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
        pm.SendMessage(0x59, $"Log out within {range} tiles of this spot and your character will keep working while you are away.");
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

    private static void SendLocations(PlayerMobile pm)
    {
        if (IdleSystem.Locations.Count == 0)
        {
            pm.SendMessage("There are no named idle locations.");
            return;
        }

        foreach (var loc in IdleSystem.Locations.Values)
        {
            pm.SendMessage($"{loc.Id}: {loc.Name} ({IdleSystem.GetTask(loc.TaskId)?.Name}) at {loc.Location} on {loc.Map}");
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
            }

            foreach (var move in report.MovedToBank)
            {
                from.SendMessage(0x22, $"Overloaded: moved {move.Amount:N0} {move.Name} ({move.Weight} stones) from pack to bank");
            }

            if (!_preview)
            {
                from.SendMessage(
                    0x59,
                    $"Weight: {report.WeightBefore} -> {report.WeightAfter} / {report.WeightLimit} stones (as the status bar shows it)"
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
