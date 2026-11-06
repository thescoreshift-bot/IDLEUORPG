using System;
using System.Collections.Generic;
using Server.Mobiles;

namespace Server.Custom.Idle;

public record IdleOrderInfo(string RecipeId, string Material, int Requested, int Remaining);

public record IdleItemInfo(string Name, int Amount, int ToPack, int ToBank, int Exceptional, bool Discarded);

public record IdleSkillInfo(string Name, double From, double To);

/// <summary>Results of one stretch of work, as plain data.</summary>
public record IdleSummaryInfo(
    string TaskName,
    double AwaySeconds,
    double CountedSeconds,
    int Attempts,
    int Successes,
    IReadOnlyList<IdleItemInfo> Items,
    IReadOnlyList<IdleItemInfo> Used,
    IReadOnlyList<IdleSkillInfo> Skills,
    string OrderStatus,
    string StopReason
)
{
    public static IdleSummaryInfo From(IdleReport r)
    {
        var items = new List<IdleItemInfo>();
        foreach (var i in r.Items)
        {
            items.Add(new IdleItemInfo(i.Name, i.Amount, i.ToPack, i.ToBank, i.Exceptional, i.Discarded));
        }

        var used = new List<IdleItemInfo>();
        foreach (var u in r.Used)
        {
            used.Add(new IdleItemInfo(u.Name, u.Amount, 0, 0, 0, false));
        }

        var skills = new List<IdleSkillInfo>();
        foreach (var s in r.Skills)
        {
            skills.Add(new IdleSkillInfo(s.Name, s.From, s.To));
        }

        return new IdleSummaryInfo(
            r.TaskName, r.Away.TotalSeconds, r.Counted.TotalSeconds, r.Attempts, r.Successes, items, used, skills,
            r.OrderStatus, r.StopReason
        );
    }
}

/// <summary>Where a character's idle work stands right now, as plain data for a phone or web client.</summary>
public record IdleStatusInfo(
    string TaskId,
    string TaskName,
    string LocationId,
    string Map,
    int X,
    int Y,
    int Z,
    IdleOrderInfo Order,
    // "None" (no task), "Online" (working live), "Offline" (working while logged out), "Idle" (task set, not working)
    string Mode,
    double Rate,
    DateTime? Since,
    double AccruedSeconds,
    string Problem,
    IdleSummaryInfo Live,
    IdleFocusInfo Focus
);

/// <summary>
/// Entry points for clients outside the game (a phone or web app). Everything here works whether the character is
/// logged in or not, and returns plain data. Starting and stopping go through <see cref="IdleSystem.TryStartTaskAt"/>
/// and <see cref="IdleSystem.StopTask"/>.
/// </summary>
public static class IdleApi
{
    public static IdleStatusInfo GetStatus(PlayerMobile pm)
    {
        var state = IdleSystem.GetState(pm);
        var def = IdleSystem.GetTask(state?.TaskId);

        if (state == null || def == null)
        {
            return new IdleStatusInfo(null, null, null, null, 0, 0, 0, null, "None", 0, null, 0, null, null, IdleFocus.GetInfo(pm));
        }

        var order = state.Order == null
            ? null
            : new IdleOrderInfo(state.Order.RecipeId, state.Order.Material, state.Order.Requested, state.Order.Remaining);

        var target = state.Target;
        var settings = IdleSystem.Settings;
        string mode;
        double rate = 0;
        DateTime? since = null;
        double accrued = 0;
        string problem = null;
        IdleSummaryInfo live = null;

        if (IdleOnlineWork.GetSession(pm) is { } session)
        {
            mode = "Online";
            rate = IdleOnlineWork.GetLiveRate(pm, state, def);
            since = IdleOnlineWork.GetWorkingSince(pm);
            live = IdleSummaryInfo.From(session);
        }
        else if (pm.Map == Map.Internal && state.LogoutResult == IdleLogoutResult.Eligible)
        {
            mode = "Offline";
            rate = settings.OfflineRate * def.Rate * IdleFocus.GetMultiplier(state, Core.Now);
            since = state.LogoutTime;
            var away = Core.Now - state.LogoutTime;
            accrued = Math.Min(away.TotalSeconds, settings.MaxOfflineTime.TotalSeconds);
        }
        else
        {
            mode = "Idle";
            if (pm.Map == Map.Internal)
            {
                problem = IdleSystem.GetResultMessage(state.LogoutResult);
            }
            else
            {
                var result = IdleSystem.Evaluate(pm, state, pm.Map, pm.Location);
                problem = result == IdleLogoutResult.Eligible ? null : IdleSystem.GetStartMessage(result);
            }
        }

        return new IdleStatusInfo(
            def.Id, def.Name, target.LocationId, target.Map?.Name, target.Location.X, target.Location.Y, target.Location.Z,
            order, mode, rate, since, accrued, problem, live, IdleFocus.GetInfo(pm)
        );
    }

    /// <summary>
    /// The checklist before going to work: ready yes/no plus every check with what is missing (a tool, the order's
    /// materials, room to carry). Changes nothing.
    /// </summary>
    public static IdlePreflightInfo Preflight(PlayerMobile pm, string taskId, IdleOrder order = null) =>
        IdlePreflight.Run(pm, taskId, order);

    /// <summary>
    /// "Work here": starts the task where the character is (or logged out) if a target is within reach; otherwise says
    /// where the nearest target or known spot is. Never moves the character.
    /// </summary>
    public static IdleWorkHereInfo WorkHere(PlayerMobile pm, string taskId, IdleOrder order = null) =>
        IdleWorkHere.Run(pm, taskId, order);

    /// <summary>Known spots for a task nearest to the character (or where it logged out), up to 50.</summary>
    public static IReadOnlyList<IdleSpotInfo> NearestSpots(PlayerMobile pm, string taskId, int max = 5) =>
        IdleWorkHere.TryGetPosition(pm, out var map, out var location)
            ? IdleWorkHere.NearestSpots(map, location, taskId, max)
            : [];

    /// <summary>
    /// Walking routes to the task's known spots, nearest on foot first, with distance, time, teleporters, and how much
    /// of the way is unguarded. From where the character is, or where it logged out. Changes nothing.
    /// </summary>
    public static IdleRoutesInfo Routes(PlayerMobile pm, string taskId, int max = 5) =>
        IdleRouting.Find(pm, taskId, Math.Clamp(max, 1, 20));

    /// <summary>
    /// The spot the character would walk to for a task (the nearest on foot, or a safer one a little farther), why it
    /// was picked, and up to <paramref name="alternatives"/> other spots. Changes nothing.
    /// </summary>
    public static IdleSpotPickInfo PickSpot(PlayerMobile pm, string taskId, int alternatives = 4) =>
        IdleRouting.Pick(pm, taskId, Math.Clamp(alternatives, 0, 19));

    /// <summary>
    /// "Go and work": checks the character, walks it (in the world) to the picked spot, and starts the task there.
    /// False with the reasons when it cannot set off; the walk's progress and end are told to the player in game.
    /// </summary>
    public static bool Go(PlayerMobile pm, string taskId, IdleOrder order, out IReadOnlyList<string> problems)
    {
        var ok = IdleWalker.TryGo(pm, taskId, order, out var list);
        problems = list;
        return ok;
    }

    /// <summary>Ends a walk in progress. False when the character was not walking.</summary>
    public static bool StopWalking(PlayerMobile pm) => IdleWalker.Stop(pm, "You stop walking.");

    /// <summary>The Focus button: starts Focus for the character's task, online or offline.</summary>
    public static IdleFocusInfo ActivateFocus(PlayerMobile pm, out string message)
    {
        IdleFocus.TryActivate(pm, out message);
        return IdleFocus.GetInfo(pm);
    }

    /// <summary>
    /// "While you were away" for a client that checks in without the game: settles the offline time since the
    /// character left (or since the last check-in) and returns the summary. The next check-in or login only settles
    /// time after this one. Returns null with a message when there is nothing to settle: the character is in the
    /// world (live totals are in <see cref="GetStatus"/>), has no eligible offline work, or too little time passed.
    /// </summary>
    public static IdleSummaryInfo CheckIn(PlayerMobile pm, out string message)
    {
        var state = IdleSystem.GetState(pm);

        if (state == null || IdleSystem.GetTask(state.TaskId) == null)
        {
            message = "No idle task.";
            return null;
        }

        if (pm.Map != Map.Internal)
        {
            message = "The character is in the world; see the live totals in the status.";
            return null;
        }

        if (state.LogoutResult != IdleLogoutResult.Eligible)
        {
            message = IdleSystem.GetResultMessage(state.LogoutResult);
            return null;
        }

        var report = IdleSystem.SettleCheckIn(pm, state, out message);
        return report == null ? null : IdleSummaryInfo.From(report);
    }
}
