using System;
using Server.Commands;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>Focus as plain data, for status displays and a client's Focus button.</summary>
public record IdleFocusInfo(bool Enabled, bool Active, double SecondsLeft, double SpeedBonus, double ReadyInSeconds);

/// <summary>
/// Focus: a short window of real time during which idle work runs faster, live or offline. One window at a time;
/// the next can start only after the window and the cooldown have both passed.
/// Rested charges (Focus banked while away) would be counted here, next to the cooldown; not built yet.
/// </summary>
public static class IdleFocus
{
    public static void Configure()
    {
        CommandSystem.Register("Focus", AccessLevel.Player, Focus_OnCommand);
    }

    /// <summary>Speed multiplier for work done at <paramref name="time"/> (1.0 outside a Focus window).</summary>
    public static double GetMultiplier(IdleState state, DateTime time)
    {
        var settings = IdleSystem.Settings;
        return settings.FocusEnabled && state != null && state.FocusStart != default && time >= state.FocusStart &&
               time < state.FocusStart + TimeSpan.FromSeconds(settings.FocusDurationSeconds)
            ? 1.0 + settings.FocusSpeedBonus
            : 1.0;
    }

    /// <summary>Seconds of the Focus window that fall within [from, to).</summary>
    public static double GetOverlapSeconds(IdleState state, DateTime from, DateTime to)
    {
        var settings = IdleSystem.Settings;
        if (!settings.FocusEnabled || state == null || state.FocusStart == default)
        {
            return 0;
        }

        var start = state.FocusStart > from ? state.FocusStart : from;
        var focusEnd = state.FocusStart + TimeSpan.FromSeconds(settings.FocusDurationSeconds);
        var end = focusEnd < to ? focusEnd : to;
        return end > start ? (end - start).TotalSeconds : 0;
    }

    public static IdleFocusInfo GetInfo(PlayerMobile pm)
    {
        var settings = IdleSystem.Settings;
        var state = IdleSystem.GetState(pm);
        var now = Core.Now;

        if (!settings.FocusEnabled)
        {
            return new IdleFocusInfo(false, false, 0, settings.FocusSpeedBonus, 0);
        }

        var active = GetMultiplier(state, now) > 1.0;
        var left = active ? (state.FocusStart + TimeSpan.FromSeconds(settings.FocusDurationSeconds) - now).TotalSeconds : 0;
        var ready = state == null || state.FocusStart == default ? 0 : Math.Max(0, (ReadyAt(state) - now).TotalSeconds);

        return new IdleFocusInfo(true, active, left, settings.FocusSpeedBonus, ready);
    }

    /// <summary>Starts Focus for a character with an idle task. Entry point for the [Focus command and outside clients.</summary>
    public static bool TryActivate(PlayerMobile pm, out string error)
    {
        var settings = IdleSystem.Settings;
        var state = IdleSystem.GetState(pm);

        if (!settings.FocusEnabled || !IdleSystem.Settings.Enabled)
        {
            error = "Focus is disabled.";
            return false;
        }

        if (state == null)
        {
            error = "You need an idle task to focus on.";
            return false;
        }

        var now = Core.Now;
        if (state.FocusStart != default && now < ReadyAt(state))
        {
            error = GetMultiplier(state, now) > 1.0
                ? "You are already focused."
                : $"You can focus again in {IdleSummaryGump.FormatTime(ReadyAt(state) - now)}.";
            return false;
        }

        state.FocusStart = now;
        error = null;
        return true;
    }

    private static DateTime ReadyAt(IdleState state)
    {
        var settings = IdleSystem.Settings;
        return state.FocusStart + TimeSpan.FromSeconds(settings.FocusDurationSeconds + settings.FocusCooldownSeconds);
    }

    [Usage("Focus")]
    [Description("Work faster on your idle task for a short while. Has a cooldown.")]
    public static void Focus_OnCommand(CommandEventArgs e)
    {
        if (e.Mobile is not PlayerMobile pm)
        {
            return;
        }

        if (!TryActivate(pm, out var error))
        {
            pm.SendMessage(0x22, error);
            return;
        }

        var settings = IdleSystem.Settings;
        pm.SendMessage(
            0x59,
            $"You focus on your work: {settings.FocusSpeedBonus:P0} faster for {IdleSummaryGump.FormatTime(TimeSpan.FromSeconds(settings.FocusDurationSeconds))}."
        );
    }
}
