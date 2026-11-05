using System;
using System.Collections.Generic;
using ModernUO.CodeGeneratedEvents;
using Server.Engines.Craft;
using Server.Engines.Harvest;
using Server.Mobiles;
using Server.Text;

namespace Server.Custom.Idle;

/// <summary>
/// Live work: while a character with a task is in the world at its spot, a timer works the task one attempt at a
/// time with the normal animation and sound, using the same per-attempt rules as the offline settlement, and
/// delivers results at once. Work continues through the logout delay and hands over to the offline clock when the
/// character actually leaves the world.
/// </summary>
public static class IdleOnlineWork
{
    private static readonly Dictionary<PlayerMobile, Worker> _workers = new();

    public static void Configure()
    {
        EventSink.Movement += OnMovement;
        EventSink.AggressiveAction += OnAggressiveAction;
        EventSink.Logout += OnLogout;
    }

    public static bool IsWorking(PlayerMobile pm) => pm != null && _workers.ContainsKey(pm);

    /// <summary>Live totals since work started, or null when not working. For status displays and external callers.</summary>
    public static IdleReport GetSession(PlayerMobile pm) => pm != null && _workers.TryGetValue(pm, out var w) ? w.Session : null;

    public static DateTime GetWorkingSince(PlayerMobile pm) =>
        pm != null && _workers.TryGetValue(pm, out var w) ? w.StartedAt : DateTime.MinValue;

    /// <summary>The live speed right now: the online rate, or the offline rate after the full-rate hours.</summary>
    public static double GetLiveRate(PlayerMobile pm, IdleState state, IdleTaskDefinition def)
    {
        var settings = IdleSystem.Settings;
        var fullHours = settings.OnlineFullRateHours;
        var fullRate = fullHours <= 0 || state.OnlineSince == default ||
                       Core.Now - state.OnlineSince < TimeSpan.FromHours(fullHours);

        return (fullRate ? settings.OnlineRate : settings.OfflineRate) * def.Rate * IdleFocus.GetMultiplier(state, Core.Now);
    }

    /// <summary>
    /// Starts live work for the character's task if they are in the world at the spot and eligible. Returns false
    /// with the reason otherwise.
    /// </summary>
    public static bool TryStart(PlayerMobile pm, out string error)
    {
        var state = IdleSystem.GetState(pm);
        var def = IdleSystem.GetTask(state?.TaskId);
        var handler = IdleTaskHandler.Get(def?.Kind);

        if (state == null || def == null || handler == null)
        {
            error = "You have no idle task.";
            return false;
        }

        if (pm.Map == null || pm.Map == Map.Internal || pm.Deleted)
        {
            error = null;
            return false;
        }

        var result = IdleSystem.Evaluate(pm, state, pm.Map, pm.Location);
        if (result != IdleLogoutResult.Eligible)
        {
            error = IdleSystem.GetStartMessage(result);
            return false;
        }

        if (pm.Warmode)
        {
            error = "You cannot work while in war mode.";
            return false;
        }

        Stop(pm, null, false);

        if (state.OnlineSince == default)
        {
            state.OnlineSince = Core.Now;
        }

        state.Interrupted = IdleLogoutResult.None;

        var worker = new Worker(pm, state, def, handler);
        _workers[pm] = worker;
        worker.Schedule(TimeSpan.FromSeconds(1));

        error = null;
        return true;
    }

    /// <summary>
    /// Stops live work. With a reason, the player is told why along with the session totals; the task itself stays
    /// set unless <paramref name="clearTask"/>.
    /// </summary>
    public static void Stop(PlayerMobile pm, string reason, bool clearTask)
    {
        if (pm == null || !_workers.Remove(pm, out var worker))
        {
            return;
        }

        worker.Cancel();

        if (reason != null)
        {
            worker.SendSummary(reason);
        }

        if (clearTask)
        {
            IdleSystem.StopTask(pm);
        }
    }

    /// <summary>Stops work after a rule broke; during the logout delay that also cancels the offline work to come.</summary>
    private static void Interrupt(PlayerMobile pm, string reason, IdleLogoutResult result)
    {
        if (pm.NetState == null && IdleSystem.GetState(pm) is { } state)
        {
            state.Interrupted = result;
        }

        Stop(pm, reason, false);
    }

    private static void OnMovement(MovementEventArgs e)
    {
        if (e.Mobile is PlayerMobile pm && _workers.ContainsKey(pm))
        {
            // The event fires before the step is applied; check where the character ended up.
            Timer.StartTimer(TimeSpan.Zero, () => CheckRules(pm));
        }
    }

    private static void OnAggressiveAction(AggressiveActionEventArgs e)
    {
        if (e.Aggressor is PlayerMobile attacker && _workers.ContainsKey(attacker))
        {
            Interrupt(attacker, "You stop working to fight.", IdleLogoutResult.InCombat);
        }

        if (e.Aggressed is PlayerMobile victim && _workers.ContainsKey(victim))
        {
            Interrupt(victim, "You stop working: you are under attack!", IdleLogoutResult.InCombat);
        }
    }

    [OnEvent(nameof(PlayerMobile.PlayerDeathEvent))]
    public static void OnDeath(PlayerMobile pm)
    {
        if (_workers.ContainsKey(pm))
        {
            Interrupt(pm, "You have died.", IdleLogoutResult.Dead);
        }
    }

    [OnEvent(nameof(PlayerMobile.PlayerDeletedEvent))]
    public static void OnPlayerDeleted(PlayerMobile pm) => Stop(pm, null, false);

    /// <summary>The character has left the world: live work ends here and the offline clock takes over.</summary>
    private static void OnLogout(Mobile m)
    {
        if (m is not PlayerMobile pm)
        {
            return;
        }

        Stop(pm, null, false);

        if (IdleSystem.GetState(pm) is { } state)
        {
            state.OnlineSince = default;
        }
    }

    private static void CheckRules(PlayerMobile pm)
    {
        if (_workers.TryGetValue(pm, out var worker))
        {
            worker.CheckRules();
        }
    }

    private sealed class Worker
    {
        private readonly PlayerMobile _pm;
        private readonly IdleState _state;
        private readonly IdleTaskDefinition _def;
        private readonly IdleTaskHandler _handler;
        private readonly int[] _startSkills;
        private readonly IdleDiscreteContext _context;
        private IdleTaskRun _run;
        private TimerExecutionToken _timer;

        public Worker(PlayerMobile pm, IdleState state, IdleTaskDefinition def, IdleTaskHandler handler)
        {
            _pm = pm;
            _state = state;
            _def = def;
            _handler = handler;
            StartedAt = Core.Now;
            Session = new IdleReport { TaskName = def.Name ?? def.Id, Mode = IdleSettleMode.Online };
            _context = IdleSettlement.CreateLiveContext(pm, def, handler, state.Target, Session);

            _startSkills = new int[pm.Skills.Length];
            for (var i = 0; i < _startSkills.Length; i++)
            {
                _startSkills[i] = pm.Skills[i].BaseFixedPoint;
            }
        }

        public DateTime StartedAt { get; }
        public IdleReport Session { get; }

        public void Schedule(TimeSpan delay)
        {
            _timer.Cancel();
            Timer.StartTimer(delay, Tick, out _timer);
        }

        public void Cancel() => _timer.Cancel();

        /// <summary>The stop rules. Returns false (and stops) if one broke.</summary>
        public bool CheckRules()
        {
            var (reason, result) = GetBrokenRule();
            if (reason == null)
            {
                return true;
            }

            Interrupt(_pm, reason, result);
            return false;
        }

        private (string, IdleLogoutResult) GetBrokenRule()
        {
            if (!_pm.Alive)
            {
                return ("You have died.", IdleLogoutResult.Dead);
            }

            if (_pm.Criminal)
            {
                return ("You stop working: you are a criminal.", IdleLogoutResult.Criminal);
            }

            if (_pm.Warmode || IdleSystem.IsInCombat(_pm))
            {
                return ("You stop working to fight.", IdleLogoutResult.InCombat);
            }

            var target = _state.Target;
            if (_pm.Map != target.Map || !Utility.InRange(_pm.Location, target.Location, _handler.GetRange(_def)))
            {
                return ("You stop working: you moved away from your work spot.", IdleLogoutResult.NotAtTarget);
            }

            if (IsWorkingByHand())
            {
                return ("You stop idle work to work by hand.", IdleLogoutResult.None);
            }

            if (!_handler.CheckStation(_pm, _def, _pm.Map, _pm.Location, out var error))
            {
                return ($"You stop working: {error}", IdleLogoutResult.NoStation);
            }

            return (null, IdleLogoutResult.None);
        }

        /// <summary>
        /// The player started harvesting or crafting themselves: a harvest cursor is up, a harvest system or tool is
        /// locked by a running harvest, or a craft is in progress.
        /// </summary>
        private bool IsWorkingByHand()
        {
            if (_pm.Target is HarvestTarget || !_pm.CanBeginAction(typeof(CraftSystem)) ||
                !_pm.CanBeginAction(Mining.System) || !_pm.CanBeginAction(Lumberjacking.System) ||
                !_pm.CanBeginAction(Fishing.System))
            {
                return true;
            }

            var oneHanded = _pm.FindItemOnLayer(Layer.OneHanded);
            var twoHanded = _pm.FindItemOnLayer(Layer.TwoHanded);
            return oneHanded != null && !_pm.CanBeginAction(oneHanded) ||
                   twoHanded != null && !_pm.CanBeginAction(twoHanded);
        }

        private void Tick()
        {
            if (!_workers.TryGetValue(_pm, out var current) || current != this)
            {
                return;
            }

            if (!CheckRules())
            {
                return;
            }

            if (!_handler.HasSupplies(_pm, _def, _state.Order, out var error))
            {
                Stop(_pm, error ?? "You lack the supplies to keep working.", false);
                return;
            }

            // Harvest runs read tools fresh each attempt; smelting and crafting keep their work state between attempts.
            if (_run == null || _run is not IdleDiscreteTaskRun)
            {
                _run = _handler.BeginRun(_pm, _def, _state.Target, _state.Order);
            }

            var rate = GetLiveRate(_pm, _state, _def);
            _context.LiveRate = rate;
            IdleSettlement.SetLivePackLimit(_pm, Session);

            var seconds = _run.LiveStep(_context);
            Session.Attempts = _context.Attempts;
            Session.Successes = _context.Successes;

            if (_state.Order?.Remaining <= 0)
            {
                Stop(_pm, "Your order is complete.", true);
                return;
            }

            if (seconds < 0)
            {
                Stop(_pm, Session.StopReason ?? "You cannot keep working.", false);
                return;
            }

            Schedule(TimeSpan.FromSeconds(Math.Max(0.25, seconds / Math.Max(rate, 0.01))));
        }

        public void SendSummary(string reason)
        {
            if (_pm.NetState == null)
            {
                return;
            }

            _pm.SendMessage(0x22, reason);

            using var sb = ValueStringBuilder.Create();
            foreach (var item in Session.Items)
            {
                if (item.Discarded)
                {
                    continue;
                }

                sb.Append(sb.Length > 0 ? ", " : "");
                sb.Append($"{item.Amount:N0} {item.Name}");
                if (item.Exceptional > 0)
                {
                    sb.Append($" ({item.Exceptional:N0} exceptional)");
                }
            }

            var skills = _pm.Skills;
            for (var i = 0; i < _startSkills.Length; i++)
            {
                var delta = skills[i].BaseFixedPoint - _startSkills[i];
                if (delta != 0)
                {
                    sb.Append(sb.Length > 0 ? ", " : "");
                    sb.Append($"{skills[i].Name} {(delta > 0 ? "+" : "")}{delta / 10.0:F1}");
                }
            }

            var elapsed = IdleSummaryGump.FormatTime(Core.Now - StartedAt);
            if (sb.Length == 0)
            {
                _pm.SendMessage(0x59, $"You worked {elapsed} at {Session.TaskName} without results.");
            }
            else
            {
                _pm.SendMessage(0x59, $"You worked {elapsed} at {Session.TaskName}: {sb.AsSpan()}.");
            }

            if (Session.OrderStatus != null)
            {
                _pm.SendMessage(0x59, $"Order: {Session.OrderStatus}");
            }
        }
    }
}
