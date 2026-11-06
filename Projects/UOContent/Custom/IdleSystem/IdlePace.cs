using System;
using Server.Misc;
using Server.Mobiles;
using MovementRules = Server.Movement.Movement;

namespace Server.Custom.Idle;

/// <summary>
/// What decides how fast a character can travel right now. <see cref="Level"/> and its thresholds are rider stamina
/// points on foot, or the mount's remaining steps when the mount carries the cost.
/// </summary>
public readonly record struct IdlePaceState(
    bool Mounted,
    bool MountCarriesCost,
    bool CanRun,
    string WalkReason,
    double Level,
    int Max,
    int WalkBelow,
    int RunAbove,
    // Running steps per point of stamina the rider loses (on foot, or mounted without mount stamina).
    int StepsPerPoint,
    double RegenSecondsPerPoint,
    double LatencyMs
);

/// <summary>
/// Run-or-walk rules and travel time, shared by the live walker, ETAs, and (later) offline travel so all agree.
/// Mirrors the server's own costs: on foot a running step uses one of <see cref="PlayerMobile.StepsMax"/> + 1 steps
/// per stamina point and walking is free; stamina comes back at the server's regeneration rate. With mount stamina on,
/// the rider's stamina is not used: each running step uses one of the mount's <see cref="IHasSteps.StepsMax"/>, which
/// only come back while it rests.
/// </summary>
public static class IdlePace
{
    /// <summary>Sent this long after the client's own step delay, so it never refuses a step for being early.</summary>
    public const int MarginMs = 25;

    // A confirmed step cannot come back faster than one round trip plus a client frame.
    private const int FrameMs = 30;

    // Measured on the scratch server: paths take about 10-15% more steps than the route map counts tiles, and every
    // change of direction costs a turn request before the step.
    private const double Overhead = 1.3;

    // The server takes an extra point of stamina for every running step below this share of stamina.
    private const double ExtraLossBelow = 0.10;

    public static int DelayMs(bool mounted, bool run) =>
        mounted
            ? run ? MovementRules.RunMountDelay : MovementRules.WalkMountDelay
            : run ? MovementRules.RunFootDelay : MovementRules.WalkFootDelay;

    /// <summary>Time one confirmed step takes: the step delay, or the round trip when that is longer.</summary>
    public static double StepMs(bool mounted, bool run, double latencyMs) =>
        Math.Max(DelayMs(mounted, run) + MarginMs, latencyMs + FrameMs);

    /// <summary>Hysteresis: once running, keep running down to WalkBelow; once walking, wait for RunAbove.</summary>
    public static bool Decide(bool running, double level, int walkBelow, int runAbove) =>
        running ? level >= walkBelow : level >= runAbove;

    public static IdlePaceState For(PlayerMobile pm)
    {
        var travel = IdleSystem.Settings.Travel ?? new IdleTravelSettings();
        var mount = pm.Mount;
        var mounted = mount != null;
        var mountCost = mounted && StaminaSystem.EnableMountStamina;

        string walkReason = null;
        if (!travel.Run)
        {
            walkReason = "running is turned off";
        }
        else if (pm.AccessLevel > AccessLevel.Player)
        {
            walkReason = "staff walk";
        }
        else if (pm.Hidden)
        {
            walkReason = "hidden";
        }
        else if (StaminaSystem.IsOverloaded(pm))
        {
            walkReason = "carrying too much";
        }
        else if (mounted && !travel.RunWhenMounted)
        {
            walkReason = "running mounted is turned off";
        }

        int max, walkBelow, runAbove;
        double level;

        if (mountCost)
        {
            max = Math.Max(1, mount.StepsMax);
            level = Math.Max(0, max - StaminaSystem.PeekStepsTaken(mount));
            (walkBelow, runAbove) = Thresholds(max, travel.MountWalkBelowPercent, travel.MountRunAbovePercent, 1);
        }
        else
        {
            max = Math.Max(1, pm.StamMax);
            level = pm.Stam;
            (walkBelow, runAbove) = Thresholds(max, travel.WalkBelowPercent, travel.RunAbovePercent, 3);
        }

        return new IdlePaceState(
            mounted, mountCost, walkReason == null, walkReason, level, max, walkBelow, runAbove,
            mounted ? pm.StepsMax * 3 + 1 : pm.StepsMax + 1, Mobile.GetStamRegenRate(pm).TotalSeconds,
            pm.NetState?.AverageRtt ?? 0
        );
    }

    /// <summary>Thresholds in points, the walk one at least <paramref name="floor"/>, the run one above it.</summary>
    private static (int WalkBelow, int RunAbove) Thresholds(int max, double walkPercent, double runPercent, int floor)
    {
        var walk = Math.Max(floor, (int)Math.Ceiling(max * Math.Clamp(walkPercent, 0, 100) / 100));
        var run = Math.Max(walk + 1, (int)Math.Ceiling(max * Math.Clamp(runPercent, 0, 100) / 100));
        return (walk, run);
    }

    /// <summary>
    /// Seconds to travel <paramref name="tiles"/> steps from <paramref name="s"/>, running where the rules allow
    /// (never when <paramref name="allowRun"/> is false), plus a second per teleporter. <paramref name="running"/> is
    /// the hysteresis state to start from: whether the character is running now.
    /// </summary>
    public static double EstimateSeconds(
        int tiles, int teleports, IdlePaceState s, bool allowRun = true, bool running = false
    )
    {
        var level = s.Level;
        var ms = 0.0;
        var regenPerMs = s.RegenSecondsPerPoint > 0 ? 1 / (s.RegenSecondsPerPoint * 1000) : 0;

        for (var i = 0; i < tiles; i++)
        {
            running = allowRun && s.CanRun && Decide(running, level, s.WalkBelow, s.RunAbove);
            var step = StepMs(s.Mounted, running, s.LatencyMs);
            ms += step;

            if (s.MountCarriesCost)
            {
                // The mount regains steps only while resting, never on the move.
                if (running)
                {
                    level--;
                }

                continue;
            }

            if (running)
            {
                level -= 1.0 / s.StepsPerPoint + (level < s.Max * ExtraLossBelow ? 1 : 0);
            }

            level = Math.Min(s.Max, level + step * regenPerMs);
        }

        return ms * Overhead / 1000 + teleports;
    }

    /// <summary>The ETA a character in its current state would get for a route.</summary>
    public static double EstimateSeconds(PlayerMobile pm, int tiles, int teleports) =>
        EstimateSeconds(tiles, teleports, For(pm));
}
