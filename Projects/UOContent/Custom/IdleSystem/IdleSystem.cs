using System;
using System.Collections.Generic;
using System.IO;
using ModernUO.CodeGeneratedEvents;
using Server.Json;
using Server.Logging;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Offline progress: a character registers one task at a spot, logs out there, and on the next login the time
/// away is settled into items and skill gains. Only time spent logged out counts.
/// </summary>
public class IdleSystem : GenericPersistence
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleSystem));

    private static readonly string SettingsPath = Path.Combine(Core.BaseDirectory, "Configuration/idle.json");
    private static readonly string CatalogPath = Path.Combine(Core.BaseDirectory, "Data/Idle/tasks.json");

    private static readonly Dictionary<PlayerMobile, IdleState> _states = new();
    private static readonly Dictionary<string, IdleTaskDefinition> _tasks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IdleLocation> _locations = new(StringComparer.OrdinalIgnoreCase);

    // Results settled at connect time, shown once the client has finished logging in. Not persisted: the
    // items and gains themselves are already applied, only the summary would be lost to a restart.
    private static readonly Dictionary<PlayerMobile, PendingSummary> _pending = new();

    private static IdleSystem _persistence;

    private readonly record struct PendingSummary(IdleReport Report, string Message);

    public static IdleSettings Settings { get; private set; } = new();

    public static IReadOnlyDictionary<string, IdleTaskDefinition> Tasks => _tasks;
    public static IReadOnlyDictionary<string, IdleLocation> Locations => _locations;

    public IdleSystem() : base("IdleSystem", 10)
    {
    }

    public static void Configure()
    {
        LoadSettings();
        _persistence = new IdleSystem();
    }

    public static void Initialize()
    {
        LoadCatalog();
        IdleRoutes.Load();
        EventSink.Logout += OnLogout;
        EventSink.Connected += OnConnected;
        EventSink.ServerStarted += IdleRecipes.Warm;
    }

    private static void LoadSettings()
    {
        try
        {
            Settings = JsonConfig.Deserialize<IdleSettings>(SettingsPath);
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to read {Path}; idle system disabled until fixed", SettingsPath);
            Settings = new IdleSettings { Enabled = false };
            return;
        }

        if (Settings == null)
        {
            Settings = new IdleSettings();
            JsonConfig.Serialize(SettingsPath, Settings);
        }
    }

    /// <summary>Runs after all handlers registered themselves during Configure.</summary>
    private static void LoadCatalog()
    {
        _tasks.Clear();
        _locations.Clear();

        IdleTaskCatalog catalog;
        try
        {
            catalog = JsonConfig.Deserialize<IdleTaskCatalog>(CatalogPath);
        }
        catch (Exception e)
        {
            logger.Error(e, "Failed to read {Path}; no idle tasks loaded", CatalogPath);
            return;
        }

        if (catalog == null)
        {
            logger.Warning("{Path} not found; no idle tasks loaded", CatalogPath);
            return;
        }

        foreach (var def in catalog.Tasks)
        {
            if (string.IsNullOrWhiteSpace(def.Id) || !def.Enabled)
            {
                continue;
            }

            var handler = IdleTaskHandler.Get(def.Kind);
            if (handler == null)
            {
                logger.Warning("Idle task {Id}: unknown kind {Kind}", def.Id, def.Kind);
                continue;
            }

            if (!handler.Bind(def, out var error))
            {
                logger.Warning("Idle task {Id}: {Error}", def.Id, error);
                continue;
            }

            foreach (var name in def.DiscardItems)
            {
                var type = AssemblyHandler.FindTypeByName(name);
                if (type == null)
                {
                    logger.Warning("Idle task {Id}: unknown discard item type {Type}", def.Id, name);
                }
                else
                {
                    def.DiscardTypes.Add(type);
                }
            }

            _tasks[def.Id] = def;
        }

        foreach (var loc in catalog.Locations)
        {
            var hasTask = false;
            foreach (var taskId in loc.AllTaskIds())
            {
                hasTask |= _tasks.ContainsKey(taskId);
            }

            if (string.IsNullOrWhiteSpace(loc.Id) || loc.Map == null || !hasTask)
            {
                logger.Warning("Idle location {Id}: missing id, map, or a loaded task", loc.Id);
                continue;
            }

            foreach (var taskId in loc.AllTaskIds())
            {
                var def = GetTask(taskId);
                var handler = def == null ? null : IdleTaskHandler.Get(def.Kind);
                if (handler != null && !handler.CheckStation(null, def, loc.Map, loc.Location, out var error))
                {
                    logger.Warning(
                        "Idle location {Id} ({Task}): {Error} [{Detail}]",
                        loc.Id, taskId, error, handler.ExplainStation(def, loc.Map, loc.Location)
                    );
                }
            }

            _locations[loc.Id] = loc;
        }

        var handWritten = _locations.Count;
        var generated = IdleSpotLoader.Load(_locations, id => _tasks.ContainsKey(id));

        IdleSpotIndex.Clear();
        foreach (var loc in _locations.Values)
        {
            IdleSpotIndex.Add(loc);
        }

        logger.Information(
            "Idle system: {Tasks} task(s), {Locations} location(s), {Generated} generated spot(s)",
            _tasks.Count, handWritten, generated
        );
    }

    public static IdleState GetState(PlayerMobile pm) => pm != null && _states.TryGetValue(pm, out var state) ? state : null;

    public static IdleTaskDefinition GetTask(string id) => id != null && _tasks.TryGetValue(id, out var def) ? def : null;

    public static bool IsInCombat(Mobile m) => m.Combatant != null || m.Aggressors.Count > 0 || m.Aggressed.Count > 0;

    /// <summary>
    /// Starts a task at a named location from the catalog. Entry point for non-click sources. A location that serves
    /// several tasks needs <paramref name="taskId"/>; crafting-style tasks need an <paramref name="order"/>.
    /// </summary>
    public static bool TryStartTaskAt(
        PlayerMobile pm, string locationId, out string error, string taskId = null, IdleOrder order = null
    )
    {
        if (locationId == null || !_locations.TryGetValue(locationId, out var loc))
        {
            error = "Unknown idle location.";
            return false;
        }

        if (taskId == null)
        {
            taskId = loc.TaskId;
            foreach (var id in loc.AllTaskIds())
            {
                taskId ??= id;
            }
        }
        else
        {
            var supported = false;
            foreach (var id in loc.AllTaskIds())
            {
                supported |= id.InsensitiveEquals(taskId);
            }

            if (!supported)
            {
                error = "That task cannot be done at that location.";
                return false;
            }
        }

        return TryStartTask(pm, taskId, new IdleTarget(loc.Id, loc.Map, loc.Location), out error, order);
    }

    /// <summary>
    /// Registers a task, replacing any current one. The character must be (or have logged out) within range of
    /// the target. For a character already logged out, offline time starts counting now.
    /// </summary>
    public static bool TryStartTask(
        PlayerMobile pm, string taskId, IdleTarget target, out string error, IdleOrder order = null
    )
    {
        var def = GetTask(taskId);
        var handler = IdleTaskHandler.Get(def?.Kind);

        if (!Settings.Enabled || def == null || handler == null)
        {
            error = "That idle task is not available.";
            return false;
        }

        var offline = pm.Map == Map.Internal;
        var map = offline ? pm.LogoutMap : pm.Map;
        var location = offline ? pm.LogoutLocation : pm.Location;

        if (!pm.Alive)
        {
            error = "You cannot do that while dead.";
            return false;
        }

        if (pm.Criminal)
        {
            error = "You cannot start an idle task while flagged criminal.";
            return false;
        }

        if (IsInCombat(pm))
        {
            error = "You cannot start an idle task while in combat.";
            return false;
        }

        if (target.Map == null || target.Map == Map.Internal || map != target.Map ||
            !Utility.InRange(location, target.Location, handler.GetRange(def)))
        {
            error = "You are too far away from that spot.";
            return false;
        }

        if (!handler.ValidateTarget(pm, def, target, out error) || !handler.ValidateOrder(pm, def, order, out error) ||
            !handler.CheckStation(pm, def, map, location, out error) || !handler.HasSupplies(pm, def, order, out error))
        {
            return false;
        }

        if (order != null)
        {
            order.Remaining = order.Requested;
        }

        var state = GetState(pm) ?? (_states[pm] = new IdleState(pm));
        state.TaskId = def.Id;
        state.Target = target;
        state.StartedAt = Core.Now;
        state.LogoutResult = offline ? IdleLogoutResult.Eligible : IdleLogoutResult.None;
        state.LogoutTime = offline ? Core.Now : default;
        state.Order = order;
        state.Interrupted = IdleLogoutResult.None;

        // In the world: the character starts working right away. Otherwise it works offline as before.
        if (!offline)
        {
            IdleOnlineWork.TryStart(pm, out _);
        }

        error = null;
        return true;
    }

    /// <summary>Clears the task. Live work stops with the session totals.</summary>
    public static bool StopTask(PlayerMobile pm)
    {
        if (pm == null || !_states.Remove(pm))
        {
            return false;
        }

        IdleOnlineWork.Stop(pm, "You stop working.", false);
        return true;
    }

    /// <summary>Whether leaving the world at this spot right now would earn offline progress.</summary>
    public static IdleLogoutResult Evaluate(PlayerMobile pm, IdleState state, Map map, Point3D location)
    {
        var def = GetTask(state.TaskId);
        var handler = IdleTaskHandler.Get(def?.Kind);

        if (def == null || handler == null)
        {
            return IdleLogoutResult.TaskUnavailable;
        }

        if (!pm.Alive)
        {
            return IdleLogoutResult.Dead;
        }

        if (pm.Criminal)
        {
            return IdleLogoutResult.Criminal;
        }

        if (IsInCombat(pm))
        {
            return IdleLogoutResult.InCombat;
        }

        if (map != state.Target.Map || !Utility.InRange(location, state.Target.Location, handler.GetRange(def)))
        {
            return IdleLogoutResult.NotAtTarget;
        }

        if (!handler.CheckStation(pm, def, map, location, out _))
        {
            return IdleLogoutResult.NoStation;
        }

        return handler.HasSupplies(pm, def, state.Order, out _) ? IdleLogoutResult.Eligible : IdleLogoutResult.NoSupplies;
    }

    /// <summary>
    /// Fires when the character actually leaves the world, i.e. after the normal (combat-aware) logout delay.
    /// Location is read before the core internalizes the mobile.
    /// </summary>
    private static void OnLogout(Mobile m)
    {
        if (m is not PlayerMobile pm || !_states.TryGetValue(pm, out var state))
        {
            return;
        }

        state.LogoutTime = Core.Now;
        state.LogoutResult = state.Interrupted != IdleLogoutResult.None
            ? state.Interrupted
            : Evaluate(pm, state, m.Map, m.Location);
        state.Interrupted = IdleLogoutResult.None;
    }

    /// <summary>
    /// Settles as soon as a character is selected. At this point the login code has packets blocked and the client
    /// has not yet been sent the character's skills or stats, so the gains arrive as the starting values instead of
    /// one "your skill has increased" message per gain. The summary waits for <see cref="OnLogin"/>.
    /// </summary>
    private static void OnConnected(Mobile m)
    {
        if (m is not PlayerMobile pm || !_states.TryGetValue(pm, out var state) ||
            state.LogoutResult == IdleLogoutResult.None)
        {
            return;
        }

        var result = state.LogoutResult;
        var away = Core.Now - state.LogoutTime;
        state.LogoutResult = IdleLogoutResult.None;

        if (!Settings.Enabled || away < Settings.MinOfflineTime)
        {
            return;
        }

        if (result != IdleLogoutResult.Eligible)
        {
            _pending[pm] = new PendingSummary(null, GetResultMessage(result));
            return;
        }

        var report = Settle(pm, state, away, IdleSettleMode.Login);
        if (report != null)
        {
            _pending[pm] = new PendingSummary(report, null);
        }
    }

    [OnEvent(nameof(PlayerMobile.PlayerLoginEvent))]
    public static void OnLogin(PlayerMobile pm)
    {
        if (!_pending.Remove(pm, out var pending))
        {
            ResumeLiveWork(pm);
            return;
        }

        if (pending.Message != null)
        {
            pm.SendMessage(0x22, pending.Message);
        }

        if (pending.Report != null)
        {
            // Second check once the login has fully finished, in case anything changed the carried weight since settling.
            logger.Information(
                "Idle weight {Player}: at login {Weight}/{Max} (settled at {Settled})",
                pm,
                IdleSettlement.CarriedWeight(pm),
                pm.MaxWeight,
                pending.Report.WeightAfter
            );
            IdleSettlement.EnforceWeightLimit(pm, pending.Report);
            pending.Report.WeightAfter = IdleSettlement.CarriedWeight(pm);

            ShowReport(pm, pending.Report);
        }

        ResumeLiveWork(pm);
    }

    private static void ResumeLiveWork(PlayerMobile pm)
    {
        if (!Settings.Enabled || !Settings.ResumeOnLogin || GetState(pm) is not { } state)
        {
            return;
        }

        if (IdleOnlineWork.TryStart(pm, out _))
        {
            pm.SendMessage(0x59, $"You resume {GetTask(state.TaskId)?.Name ?? "your idle task"}. Moving away stops it.");
        }
    }

    /// <summary>Settles a stretch of offline time for the character's current task.</summary>
    public static IdleReport Settle(PlayerMobile pm, IdleState state, TimeSpan away, IdleSettleMode mode)
    {
        var def = GetTask(state.TaskId);
        var handler = IdleTaskHandler.Get(def?.Kind);

        if (def == null || handler == null)
        {
            return null;
        }

        // Real offline time can overlap a Focus window; staff simulations have no real time to overlap.
        var focusSeconds = 0.0;
        if (mode == IdleSettleMode.Login)
        {
            var counted = away > Settings.MaxOfflineTime ? Settings.MaxOfflineTime : away;
            focusSeconds = IdleFocus.GetOverlapSeconds(state, state.LogoutTime, state.LogoutTime + counted);
        }

        var report = IdleSettlement.Settle(pm, def, handler, state.Target, state.Order, away, mode, focusSeconds);

        // A finished order ends the task; the summary says so through the order status.
        if (mode != IdleSettleMode.Preview && state.Order?.Remaining <= 0)
        {
            _states.Remove(pm);
            IdleOnlineWork.Stop(pm, null, false);
        }

        logger.Information(
            "Idle settle {Player}: task {Task}, {Counted} counted, {Attempts} attempts, {Successes} successes ({Mode})",
            pm,
            def.Id,
            report.Counted,
            report.Attempts,
            report.Successes,
            mode
        );

        return report;
    }

    /// <summary>
    /// Settles offline time for a check-in from outside the game, then moves the offline clock's start to now so the
    /// same time is never settled twice. Each check-in counts at most the usual maximum offline time.
    /// </summary>
    public static IdleReport SettleCheckIn(PlayerMobile pm, IdleState state, out string message)
    {
        var away = Core.Now - state.LogoutTime;

        if (!Settings.Enabled || away < Settings.MinOfflineTime)
        {
            message = "Not enough time has passed since the last check.";
            return null;
        }

        var report = Settle(pm, state, away, IdleSettleMode.Login);
        state.LogoutTime = Core.Now;

        message = report == null ? "The idle task is no longer available." : null;
        return report;
    }

    /// <summary>A character with an idle task, by name (staff tools; only characters with a task are searched).</summary>
    public static PlayerMobile FindPlayerWithTask(string name)
    {
        foreach (var pm in _states.Keys)
        {
            if (pm.RawName.InsensitiveEquals(name))
            {
                return pm;
            }
        }

        return null;
    }

    public static void ShowReport(PlayerMobile pm, IdleReport report)
    {
        IdleSummaryGump.SendText(pm, report);

        if (Settings.ShowSummaryGump)
        {
            // Give the client a moment to finish loading the world before opening a gump.
            Timer.StartTimer(TimeSpan.FromSeconds(2), () => IdleSummaryGump.DisplayTo(pm, report));
        }
    }

    /// <summary>Why live work cannot start where the character stands.</summary>
    public static string GetStartMessage(IdleLogoutResult result) =>
        result switch
        {
            IdleLogoutResult.TaskUnavailable => "Your idle task is no longer available.",
            IdleLogoutResult.NotAtTarget     => "You are not at your idle spot.",
            IdleLogoutResult.Dead            => "You cannot work while dead.",
            IdleLogoutResult.Criminal        => "You cannot work while flagged criminal.",
            IdleLogoutResult.InCombat        => "You cannot work while in combat.",
            IdleLogoutResult.NoSupplies      => "You lack the supplies your idle task needs.",
            IdleLogoutResult.NoStation       => "You are not near the work station your idle task needs.",
            _                                => "You cannot work right now."
        };

    public static string GetResultMessage(IdleLogoutResult result) =>
        result switch
        {
            IdleLogoutResult.TaskUnavailable => "Your idle task is no longer available, so no offline work was done.",
            IdleLogoutResult.NotAtTarget     => "You logged out away from your idle spot, so no offline work was done.",
            IdleLogoutResult.Dead            => "You logged out while dead, so no offline work was done.",
            IdleLogoutResult.Criminal        => "You logged out while criminal, so no offline work was done.",
            IdleLogoutResult.InCombat        => "You logged out while in combat, so no offline work was done.",
            IdleLogoutResult.NoSupplies      => "You logged out without the supplies your idle task needs.",
            IdleLogoutResult.NoStation       => "You logged out away from the work station your idle task needs.",
            _                                => "No offline work was done."
        };

    [OnEvent(nameof(PlayerMobile.PlayerDeletedEvent))]
    public static void OnPlayerDeleted(PlayerMobile pm)
    {
        _states.Remove(pm);
        _pending.Remove(pm);
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(0); // version

        writer.WriteEncodedInt(_states.Count);
        foreach (var (pm, state) in _states)
        {
            writer.Write(pm);
            state.Serialize(writer);
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version

        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var pm = reader.ReadEntity<PlayerMobile>();
            var state = new IdleState(pm);
            state.Deserialize(reader);

            if (pm?.Deleted == false)
            {
                _states[pm] = state;
            }
        }
    }
}
