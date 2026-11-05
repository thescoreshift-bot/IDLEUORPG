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
        EventSink.Logout += OnLogout;
        EventSink.Connected += OnConnected;
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

            _tasks[def.Id] = def;
        }

        foreach (var loc in catalog.Locations)
        {
            if (string.IsNullOrWhiteSpace(loc.Id) || loc.Map == null || !_tasks.ContainsKey(loc.TaskId ?? ""))
            {
                logger.Warning("Idle location {Id}: missing id, map, or a loaded task", loc.Id);
                continue;
            }

            _locations[loc.Id] = loc;
        }

        logger.Information("Idle system: {Tasks} task(s), {Locations} location(s)", _tasks.Count, _locations.Count);
    }

    public static IdleState GetState(PlayerMobile pm) => pm != null && _states.TryGetValue(pm, out var state) ? state : null;

    public static IdleTaskDefinition GetTask(string id) => id != null && _tasks.TryGetValue(id, out var def) ? def : null;

    public static bool IsInCombat(Mobile m) => m.Combatant != null || m.Aggressors.Count > 0 || m.Aggressed.Count > 0;

    /// <summary>Starts a task at a named location from the catalog. Entry point for non-click sources.</summary>
    public static bool TryStartTaskAt(PlayerMobile pm, string locationId, out string error)
    {
        if (locationId == null || !_locations.TryGetValue(locationId, out var loc))
        {
            error = "Unknown idle location.";
            return false;
        }

        return TryStartTask(pm, loc.TaskId, new IdleTarget(loc.Id, loc.Map, loc.Location), out error);
    }

    /// <summary>
    /// Registers a task, replacing any current one. The character must be (or have logged out) within range of
    /// the target. For a character already logged out, offline time starts counting now.
    /// </summary>
    public static bool TryStartTask(PlayerMobile pm, string taskId, IdleTarget target, out string error)
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

        if (!handler.ValidateTarget(pm, def, target, out error) || !handler.HasSupplies(pm, def, out error))
        {
            return false;
        }

        var state = GetState(pm) ?? (_states[pm] = new IdleState(pm));
        state.TaskId = def.Id;
        state.Target = target;
        state.StartedAt = Core.Now;
        state.LogoutResult = offline ? IdleLogoutResult.Eligible : IdleLogoutResult.None;
        state.LogoutTime = offline ? Core.Now : default;

        error = null;
        return true;
    }

    public static bool StopTask(PlayerMobile pm) => pm != null && _states.Remove(pm);

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

        return handler.HasSupplies(pm, def, out _) ? IdleLogoutResult.Eligible : IdleLogoutResult.NoSupplies;
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
        state.LogoutResult = Evaluate(pm, state, m.Map, m.Location);
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
            return;
        }

        if (pending.Message != null)
        {
            pm.SendMessage(0x22, pending.Message);
        }

        if (pending.Report != null)
        {
            ShowReport(pm, pending.Report);
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

        var report = IdleSettlement.Settle(pm, def, handler, state.Target, away, mode);

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

    public static void ShowReport(PlayerMobile pm, IdleReport report)
    {
        IdleSummaryGump.SendText(pm, report);

        if (Settings.ShowSummaryGump)
        {
            // Give the client a moment to finish loading the world before opening a gump.
            Timer.StartTimer(TimeSpan.FromSeconds(2), () => IdleSummaryGump.DisplayTo(pm, report));
        }
    }

    public static string GetResultMessage(IdleLogoutResult result) =>
        result switch
        {
            IdleLogoutResult.TaskUnavailable => "Your idle task is no longer available, so no offline work was done.",
            IdleLogoutResult.NotAtTarget     => "You logged out away from your idle spot, so no offline work was done.",
            IdleLogoutResult.Dead            => "You logged out while dead, so no offline work was done.",
            IdleLogoutResult.Criminal        => "You logged out while criminal, so no offline work was done.",
            IdleLogoutResult.InCombat        => "You logged out while in combat, so no offline work was done.",
            IdleLogoutResult.NoSupplies      => "You logged out without the supplies your idle task needs.",
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
