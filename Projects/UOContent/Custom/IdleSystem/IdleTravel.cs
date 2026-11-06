using System;
using System.Collections.Generic;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// The latest "go and work" of a character, for a game or web client.
/// <para>
/// Phase: None (never went anywhere), Planning (checking and picking the spot), Walking, Arrived (at the spot with the
/// task set, but work is not running: Resume starts it), Working, or Stopped (StopReason says why).
/// </para>
/// <para>
/// StopReason: NotReady (the checklist failed; Message lists what), NoRouteMap, NoSpots, NoRoute, Failed, MovedByHand,
/// Died, Criminal, Combat, LeftMap, MovedAway, TeleporterFailed, Stuck, TooSlow, Overloaded, Blocked, CannotStart (at
/// the spot, the task would not start), StoppedByPlayer, LoggedOut, Replaced (another go started), or WorkStopped (work
/// ended after arriving).
/// </para>
/// TilesLeft, EtaSeconds, Running and Pace are live while walking. Route is the picked spot with its distance, time,
/// and risk; Alternatives are the other spots, nearest on foot first. CanResume says whether Resume has something to
/// pick up.
/// </summary>
public record IdleTravelInfo(
    string Phase,
    string Message,
    string TaskId,
    string TaskName,
    IdleRouteInfo Route,
    IReadOnlyList<IdleRouteInfo> Alternatives,
    string PickReason,
    string Map,
    IdlePointInfo Position,
    int TilesLeft,
    double EtaSeconds,
    bool Running,
    string Pace,
    int StepsTaken,
    double ElapsedSeconds,
    string StopReason,
    bool CanResume,
    DateTime StartedAt,
    DateTime UpdatedAt
);

/// <summary>Status: Working (work started where the character stands), Walking (set off again), or Failed.</summary>
public record IdleResumeInfo(string Status, string Message, IdleTravelInfo Travel);

internal readonly record struct IdleTravelLive(
    int TilesLeft, double EtaSeconds, bool Running, string Pace, int Steps, double ElapsedSeconds
);

/// <summary>What the walker keeps of a character's latest go, after the walk itself has ended.</summary>
internal sealed class IdleTravelRecord
{
    public string Phase = "Planning";
    public string Message;
    public string TaskId;
    public IdleOrder Order;
    public IdleRouteInfo Route;
    public IReadOnlyList<IdleRouteInfo> Alternatives = [];
    public string PickReason;
    public string StopReason;
    public bool ReachedSpot;
    public int Steps;
    public double ElapsedSeconds;
    public DateTime StartedAt = Core.Now;
    public DateTime UpdatedAt = Core.Now;

    public void Stopped(string reason, string message)
    {
        Phase = "Stopped";
        StopReason = reason;
        Message = message;
        UpdatedAt = Core.Now;
    }
}

/// <summary>The travel status and the one-press Resume.</summary>
public static class IdleTravel
{
    public static IdleTravelInfo Get(PlayerMobile pm)
    {
        var record = IdleWalker.GetRecord(pm);
        IdleWorkHere.TryGetPosition(pm, out var map, out var position);
        var where = map == null ? null : IdlePointInfo.From(position);

        if (record == null)
        {
            return new IdleTravelInfo(
                "None", "Not on the way anywhere.", null, null, null, [], null, map?.Name, where, 0, 0, false, null, 0, 0,
                null, false, default, default
            );
        }

        var phase = record.Phase;
        var message = record.Message;
        var stopReason = record.StopReason;

        // After arriving, the phase follows the work itself.
        if (record.ReachedSpot)
        {
            var working = IdleOnlineWork.IsWorking(pm) && IdleSystem.GetState(pm)?.TaskId == record.TaskId;
            if (working)
            {
                phase = "Working";
                stopReason = null;
            }
            else if (phase == "Working")
            {
                phase = "Stopped";
                stopReason = "WorkStopped";
                message = "Work stopped after arriving.";
            }
        }

        var live = IdleWalker.GetLive(pm);
        var def = IdleSystem.GetTask(record.TaskId);

        return new IdleTravelInfo(
            phase, message, record.TaskId, def?.Name, record.Route, record.Alternatives, record.PickReason, map?.Name,
            where, live?.TilesLeft ?? 0, live?.EtaSeconds ?? 0, live?.Running ?? false, live?.Pace,
            live?.Steps ?? record.Steps, live?.ElapsedSeconds ?? record.ElapsedSeconds, stopReason,
            phase is "Stopped" or "Arrived" && record.TaskId != null, record.StartedAt, record.UpdatedAt
        );
    }

    /// <summary>
    /// The one-press Resume: starts work where the character stands if it is at its task's spot, otherwise sets off
    /// again for the latest go's task (picking the spot afresh from here).
    /// </summary>
    public static IdleResumeInfo Resume(PlayerMobile pm)
    {
        if (IdleWalker.IsWalking(pm))
        {
            return new IdleResumeInfo("Failed", "You are already on your way.", Get(pm));
        }

        if (IdleOnlineWork.IsWorking(pm))
        {
            return new IdleResumeInfo("Failed", "You are already working.", Get(pm));
        }

        var record = IdleWalker.GetRecord(pm);

        // A walk that stopped on the way is the newest intent; otherwise try the task where the character stands.
        string error = null;
        if ((record == null || record.ReachedSpot || record.Phase != "Stopped") && IdleOnlineWork.TryStart(pm, out error))
        {
            if (record?.ReachedSpot == true)
            {
                record.Phase = "Working";
                record.UpdatedAt = Core.Now;
            }

            return new IdleResumeInfo("Working", "You resume working.", Get(pm));
        }

        if (record?.TaskId != null)
        {
            if (IdleWalker.TryGo(pm, record.TaskId, record.Order, out var problems))
            {
                return new IdleResumeInfo("Walking", "You set off again.", Get(pm));
            }

            return new IdleResumeInfo("Failed", string.Join(" ", problems), Get(pm));
        }

        return new IdleResumeInfo("Failed", error ?? "There is nothing to resume.", Get(pm));
    }
}
