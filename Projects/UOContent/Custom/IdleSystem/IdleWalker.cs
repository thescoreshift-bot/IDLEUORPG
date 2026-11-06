using System;
using System.Collections.Generic;
using Server.Items;
using Server.Misc;
using Server.Mobiles;
using Server.Network;
using Server.PathAlgorithms;
using Server.Text;
using MovementRules = Server.Movement.Movement;

namespace Server.Custom.Idle;

/// <summary>
/// "Go and work": walks a character in the world to the spot <see cref="IdleRouting.Pick"/> chose and starts the task
/// there. The client walks its own character, running while stamina allows (<see cref="IdlePace"/>): the server sends
/// one 0x97 (move player) step and sends the next only once the character's position shows the step was taken and the
/// step's own delay has passed, so client and server never disagree about where the character is. A step the client
/// does not take after <see cref="IdleTravelSettings.StepTries"/> asks is made by the server instead, at walking pace
/// (checked like a normal step, then sent to the client and to everyone around). The walk follows the route map's
/// waypoints and finds the steps between them with the server's pathfinder.
/// </summary>
public static class IdleWalker
{
    private static readonly Dictionary<PlayerMobile, Walk> _walks = new();

    // Each character's latest go, kept after the walk ends so a client can see how it ended. Not saved.
    private static readonly Dictionary<PlayerMobile, IdleTravelRecord> _records = new();
    private static IdleReachProbe _probe;

    // A walker that is not alive passes closed doors, which the walk opens as it reaches them. As a player it keeps to
    // the player's stricter rule for diagonal steps. Not being alive also keeps the Player setter from starting stat
    // regeneration timers.
    private static IdleReachProbe Probe => _probe ??= new IdleReachProbe { Player = true };

    public static void Configure()
    {
        EventSink.Movement += OnMovement;
        EventSink.Logout += OnLogout;
    }

    public static bool IsWalking(PlayerMobile pm) => pm != null && _walks.ContainsKey(pm);

    /// <summary>
    /// Checks the character, picks the spot, and sets off. Returns false with the reason (one line per problem) when
    /// the walk cannot start.
    /// </summary>
    public static bool TryGo(PlayerMobile pm, string taskId, IdleOrder order, out List<string> problems)
    {
        problems = [];
        if (pm == null)
        {
            return false;
        }

        if (_walks.TryGetValue(pm, out var old))
        {
            old.Finish("Replaced", "You set off somewhere else.", 0, false);
        }

        var record = new IdleTravelRecord { TaskId = IdleSystem.GetTask(taskId)?.Id ?? taskId, Order = order };
        _records[pm] = record;

        var check = IdlePreflight.Run(pm, taskId, order);
        if (!check.Ready)
        {
            foreach (var line in check.Checks)
            {
                if (!line.Ok && line.Blocking)
                {
                    problems.Add(line.Message);
                }
            }

            record.Stopped("NotReady", string.Join(" ", problems));
            return false;
        }

        var def = IdleSystem.GetTask(taskId);
        var pick = IdleRouting.Pick(pm, def.Id);
        if (pick.Status != "Ok")
        {
            problems.Add(pick.Message);
            record.Stopped(pick.Status, pick.Message);
            return false;
        }

        record.Phase = "Walking";
        record.Message = pick.Message;
        record.Route = pick.Pick;
        record.Alternatives = pick.Alternatives;
        record.PickReason = pick.Reason;
        record.UpdatedAt = Core.Now;

        var walk = new Walk(pm, def, order, pick, record);
        _walks[pm] = walk;

        var spot = pick.Pick.Spot;
        pm.SendMessage(
            0x44,
            $"You set off for {spot.Name} ({spot.Id}): {pick.Pick.WalkTiles} tiles, about " +
            $"{IdleRouting.FormatEta(pick.Pick.EtaSeconds)}. {pick.Message}"
        );
        var pace = IdlePace.For(pm);
        if (pace.CanRun)
        {
            pm.SendMessage(0x3B2, "You run while your stamina allows. Moving by hand, fighting, or [Idle stop ends the walk.");
        }
        else
        {
            pm.SendMessage(0x3B2, $"You walk ({pace.WalkReason}). Moving by hand, fighting, or [Idle stop ends the walk.");
        }

        walk.Start();
        return true;
    }

    /// <summary>The player stops the walk ([Idle stop or a client's stop button); they are told where it ended.</summary>
    public static bool Stop(PlayerMobile pm, string message)
    {
        if (pm == null || !_walks.TryGetValue(pm, out var walk))
        {
            return false;
        }

        walk.Finish("StoppedByPlayer", message ?? "You stop walking.", 0x22);
        return true;
    }

    /// <summary>One line on the walk in progress, or null when the character is not walking.</summary>
    public static string Describe(PlayerMobile pm) => pm != null && _walks.TryGetValue(pm, out var walk) ? walk.Describe() : null;

    internal static IdleTravelRecord GetRecord(PlayerMobile pm) =>
        pm != null && _records.TryGetValue(pm, out var record) ? record : null;

    /// <summary>The live part of the status, while walking.</summary>
    internal static IdleTravelLive? GetLive(PlayerMobile pm) =>
        pm != null && _walks.TryGetValue(pm, out var walk) ? walk.Live() : null;

    /// <summary>For a deleted character: ends any walk and drops its record.</summary>
    public static void Forget(PlayerMobile pm)
    {
        if (pm != null && _walks.TryGetValue(pm, out var walk))
        {
            walk.Finish("LoggedOut", null, 0, false);
        }

        _records.Remove(pm);
    }

    private static void OnMovement(MovementEventArgs e)
    {
        // Only real steps raise the event (turns do not), before the step is applied. The walker's own step has the
        // direction it asked for; any other step is the player's.
        if (e.Mobile is not PlayerMobile pm || !_walks.TryGetValue(pm, out var walk))
        {
            return;
        }

        if (walk.Expects(e.Direction))
        {
            walk.Saw(e.Direction);
        }
        else
        {
            walk.Finish("MovedByHand", "You moved by hand, so the walk stopped.", 0x22);
        }
    }

    private static void OnLogout(Mobile m)
    {
        if (m is PlayerMobile pm && _walks.TryGetValue(pm, out var walk))
        {
            walk.Finish("LoggedOut", "You logged out, so the walk stopped.", 0, false);
        }
    }

    private static Direction[] FindPath(Map map, Point3D from, Point3D to) =>
        BitmapAStarAlgorithm.Instance.CheckCondition(Probe, map, from, to)
            ? BitmapAStarAlgorithm.Instance.Find(Probe, map, from, to)
            : null;

    private static bool SameTile(Point3D a, Point3D b) => a.X == b.X && a.Y == b.Y;

    private sealed class Walk : Timer
    {
        // Fine enough that a 200 ms running step is not stretched much by waiting for the next check.
        private const int TickMs = 25;

        // How long a teleporter may take to move the character once it stands on it.
        private const int TeleportWaitMs = 3000;

        // Path recalculations at one place before the route is searched again, and route searches before giving up.
        private const int MaxLegRetries = 3;
        private const int MaxReroutes = 3;

        private readonly PlayerMobile _pm;
        private readonly IdleTaskDefinition _def;
        private readonly IdleOrder _order;
        private readonly Map _map;
        private readonly long _started;
        private readonly double _limitMs;
        private readonly double _etaSeconds;
        private readonly int _stepTimeoutMs;
        private readonly int _stepTries;
        private readonly int _stuckMs;

        private IdleSpotInfo _spot;
        private Point3D _goal;
        private List<Point3D> _waypoints;
        private HashSet<int> _teleportLegs;
        private int _next;

        private Direction[] _path;
        private int _pathIndex;

        private Direction? _pending;
        private Point3D _pendingTo;
        private Direction _facingAtSend;
        private long _sentAt;
        private int _tries;

        // Run or walk: the hysteresis state, the kind and delay of the step last asked for, and whether the server saw
        // the last confirmed step as running (the client can ignore the run flag, or run when asked to walk).
        private bool _running;
        private bool _sentRun;
        private int _lastStepDelayMs;
        private bool _sawRun;
        private bool _ranUnasked;
        private bool _toldAlwaysRun;

        private Point3D _lastKnown;
        private long _lastProgress;
        private long _waitUntil;
        private long _teleportSince;
        private bool _onTeleporter;

        private int _legRetries;
        private int _reroutes;
        private int _doorTries;
        private bool _retargeted;

        private int _steps;
        private int _serverSteps;
        private int _doors;
        private readonly IdleTravelRecord _record;

        public Walk(PlayerMobile pm, IdleTaskDefinition def, IdleOrder order, IdleSpotPickInfo pick, IdleTravelRecord record)
            : base(TimeSpan.FromMilliseconds(TickMs), TimeSpan.FromMilliseconds(TickMs))
        {
            _pm = pm;
            _record = record;
            _def = def;
            _order = order;
            _map = pm.Map;
            _spot = pick.Pick.Spot;
            _goal = new Point3D(_spot.Stand.X, _spot.Stand.Y, _spot.Stand.Z);
            SetRoute(pick.Pick.Waypoints, pick.Pick.TeleportLegs);

            var travel = IdleSystem.Settings.Travel ?? new IdleTravelSettings();
            _stepTimeoutMs = (int)(Math.Clamp(travel.StepTimeoutSeconds, 0.3, 10) * 1000);
            _stepTries = Math.Clamp(travel.StepTries, 1, 10);
            _stuckMs = (int)(Math.Clamp(travel.StuckSeconds, 3, 300) * 1000);

            // Twice the walking-only time plus a minute: past that something is wrong even if steps still trickle in.
            // Walking-only, so a client that walks when asked to run, or rests on the way, never trips it.
            var walkOnly = IdlePace.EstimateSeconds(pick.Pick.WalkTiles, pick.Pick.Teleports, IdlePace.For(pm), false);
            _limitMs = (walkOnly * 2 + 60) * 1000;
            _etaSeconds = pick.Pick.EtaSeconds;

            _started = Core.TickCount;
            _lastProgress = _started;
            _sentAt = _started - 10000;
            _waitUntil = _started;
            _lastKnown = pm.Location;

            // Decided now so the status says how the walk sets off before the first step.
            WantRun();
        }

        private void SetRoute(IReadOnlyList<IdlePointInfo> points, IReadOnlyList<int> teleportLegs)
        {
            _waypoints = new List<Point3D>(points.Count);
            foreach (var p in points)
            {
                _waypoints.Add(new Point3D(p.X, p.Y, p.Z));
            }

            _teleportLegs = new HashSet<int>(teleportLegs ?? []);
            _next = 1;
            _path = null;
            _pathIndex = 0;
            _onTeleporter = false;
        }

        public bool Expects(Direction d) => _pending is { } pending && (d & Direction.Mask) == pending;

        /// <summary>The server is applying the step the walker asked for; notes whether it is a running step.</summary>
        public void Saw(Direction d)
        {
            _sawRun = (d & Direction.Running) != 0;
            _ranUnasked |= _sawRun && !_sentRun;
        }

        public string Describe()
        {
            var live = Live();
            return $"Going to {_spot.Name} ({_spot.Id}): about {live.TilesLeft} tiles to go, " +
                   $"{IdleRouting.FormatEta(live.EtaSeconds)}. {live.Pace} {_steps} steps taken.";
        }

        /// <summary>Distance and time left from where the character is now, and how it is moving.</summary>
        public IdleTravelLive Live()
        {
            var left = RemainingTiles(out var teleports);
            var pace = IdlePace.For(_pm);
            var eta = IdlePace.EstimateSeconds(left, teleports, pace, true, _running);
            return new IdleTravelLive(left, eta, _sawRun, PaceText(pace), _steps, (Core.TickCount - _started) / 1000.0);
        }

        private string PaceText(IdlePaceState pace)
        {
            if (_running && _sawRun)
            {
                return "Running.";
            }

            if (_running)
            {
                // Asked to run, but the server saw a walking step (or none yet).
                return _steps == 0 ? "Setting off at a run." : "Walking: your client did not run when asked.";
            }

            if (_sawRun)
            {
                return $"Asked to walk, but your client is running (Always Run?). {Level(pace)}";
            }

            return pace.WalkReason != null
                ? $"Walking ({pace.WalkReason})."
                : $"Walking to rest. {Level(pace)} Running again from {Percent(pace.RunAbove, pace.Max)}%.";
        }

        private string Level(IdlePaceState pace) =>
            pace.MountCarriesCost
                ? $"Your mount has {Percent(pace.Level, pace.Max)}% of its strength."
                : $"Stamina {_pm.Stam}/{_pm.StamMax}.";

        private static int Percent(double value, int max) => (int)Math.Round(value * 100 / Math.Max(1, max));

        private int RemainingTiles(out int teleports)
        {
            var tiles = 0;
            teleports = 0;
            var last = _pm.Location;
            for (var i = _next; i < _waypoints.Count; i++)
            {
                if (_teleportLegs.Contains(i - 1))
                {
                    teleports++;
                }
                else
                {
                    tiles += IdleRouteMap.Steps(last, _waypoints[i]);
                }

                last = _waypoints[i];
            }

            return tiles;
        }

        /// <summary>Whether the next step runs: the pace rules with this walk's hysteresis state.</summary>
        private bool WantRun()
        {
            var pace = IdlePace.For(_pm);
            _running = pace.CanRun && IdlePace.Decide(_running, pace.Level, pace.WalkBelow, pace.RunAbove);
            return _running;
        }

        protected override void OnTick()
        {
            var now = Core.TickCount;

            if (_pm.Deleted || _pm.NetState == null)
            {
                Finish("LoggedOut", "You lost your connection, so the walk stopped.", 0, false);
                return;
            }

            if (CheckStopRules(now, out var code) is { } reason)
            {
                Finish(code, reason, 0x22);
                return;
            }

            if (_pending is { } pending)
            {
                if (!CheckPending(pending, now))
                {
                    return;
                }
            }
            else if (_pm.Location != _lastKnown && !TryTeleported())
            {
                Finish(
                    "MovedAway", $"You were moved away from the route (to {_pm.X}, {_pm.Y}), so the walk stopped.", 0x22
                );
                return;
            }

            if (now - _waitUntil < 0)
            {
                return;
            }

            if (_onTeleporter)
            {
                if (!TryTeleported() && now - _teleportSince > TeleportWaitMs)
                {
                    Finish(
                        "TeleporterFailed", $"The teleporter at {_pm.X}, {_pm.Y} did not move you, so the walk stopped.", 0x22
                    );
                }

                return;
            }

            if (now - _sentAt < _lastStepDelayMs + IdlePace.MarginMs || _pm.Frozen || _pm.Paralyzed)
            {
                return;
            }

            // Switching from running to walking, the client wants the longer of the two delays before the next step.
            var run = WantRun();
            if (now - _sentAt < Math.Max(_lastStepDelayMs, IdlePace.DelayMs(_pm.Mounted, run)) + IdlePace.MarginMs)
            {
                return;
            }

            if (!EnsureLeg(out var arrived))
            {
                return;
            }

            if (arrived)
            {
                Arrive();
                return;
            }

            TakeStep(_path[_pathIndex], run, now);
        }

        /// <summary>The shared character rules, then the walk's own time limits.</summary>
        private string CheckStopRules(long now, out string code)
        {
            switch (IdleRules.CheckCharacter(_pm))
            {
                case IdleLogoutResult.Dead:
                    {
                        code = "Died";
                        return "You died, so the walk stopped.";
                    }
                case IdleLogoutResult.Criminal:
                    {
                        code = "Criminal";
                        return "You are flagged criminal, so the walk stopped.";
                    }
                case IdleLogoutResult.InCombat:
                    {
                        code = "Combat";
                        return "You are fighting or in war mode, so the walk stopped.";
                    }
            }

            if (_pm.Map != _map)
            {
                code = "LeftMap";
                return "You left the map, so the walk stopped.";
            }

            if (!_onTeleporter && now - _lastProgress > _stuckMs)
            {
                code = "Stuck";
                return $"You have not moved for {_stuckMs / 1000} seconds at {_pm.X}, {_pm.Y}, so the walk stopped.";
            }

            if (now - _started > _limitMs)
            {
                code = "TooSlow";
                return $"The walk is taking far longer than expected, so it stopped at {_pm.X}, {_pm.Y}.";
            }

            code = null;
            return null;
        }

        /// <summary>Returns true when the step asked for is done and the next may be sent.</summary>
        private bool CheckPending(Direction pending, long now)
        {
            if (SameTile(_pm.Location, _pendingTo))
            {
                Confirmed(now);
                return true;
            }

            // Stepping onto a teleporter can move the character before the step is seen.
            if (_pm.Location != _lastKnown)
            {
                if (TryTeleported())
                {
                    _pending = null;
                    return true;
                }

                Finish(
                    "MovedAway", $"You were moved away from the route (to {_pm.X}, {_pm.Y}), so the walk stopped.", 0x22
                );
                return false;
            }

            // The client turns first when it faces another way; it then needs a second request for the step itself.
            if ((_pm.Direction & Direction.Mask) == pending && _facingAtSend != pending)
            {
                if (now - _sentAt >= _lastStepDelayMs + IdlePace.MarginMs)
                {
                    _facingAtSend = pending;
                    Send(pending, _sentRun, now);
                }

                return false;
            }

            // The client ran when asked to walk (Always Run) and the server refused the run for lack of stamina: asking
            // again only repeats that, so the server takes the step at walking pace.
            var refusedRun = _ranUnasked && _pm.Stam <= 0;

            if (refusedRun)
            {
                if (now - _sentAt < IdlePace.DelayMs(_pm.Mounted, false) + IdlePace.MarginMs)
                {
                    return false;
                }

                if (!_toldAlwaysRun)
                {
                    _toldAlwaysRun = true;
                    _pm.SendMessage(
                        0x3B2,
                        "Your client keeps running while you are out of stamina (Always Run?). The walk carries on at " +
                        "walking pace; turn Always Run off to let your client walk."
                    );
                }
            }
            else
            {
                if (now - _sentAt < _stepTimeoutMs || _pm.Frozen || _pm.Paralyzed)
                {
                    return false;
                }

                if (++_tries < _stepTries)
                {
                    Send(pending, _sentRun, now);
                    return false;
                }
            }

            // The client did not take the step: the server takes it, if it is a legal step.
            _pending = null;
            _tries = 0;

            if (StaminaSystem.IsOverloaded(_pm))
            {
                Finish("Overloaded", "You are carrying too much to walk, so the walk stopped.", 0x22);
                return false;
            }

            if (!MovementRules.CheckMovement(_pm, _map, _pm.Location, pending, out var z))
            {
                Blocked(now);
                return false;
            }

            ServerStep(pending, new Point3D(_pendingTo.X, _pendingTo.Y, z));
            Confirmed(now);
            return true;
        }

        private void Confirmed(long now)
        {
            _pending = null;
            _tries = 0;
            _steps++;
            _pathIndex++;
            _lastKnown = _pm.Location;
            _lastProgress = now;
            _legRetries = 0;
            _doorTries = 0;
        }

        /// <summary>
        /// If the character just used the teleporter the route expects, carries on from where it landed.
        /// </summary>
        private bool TryTeleported()
        {
            var source = _next;
            if (!_teleportLegs.Contains(source) || source + 1 >= _waypoints.Count ||
                IdleRouteMap.Steps(_pm.Location, _waypoints[source + 1]) > 2)
            {
                return false;
            }

            _next = source + 2;
            _path = null;
            _onTeleporter = false;
            _lastKnown = _pm.Location;
            _lastProgress = Core.TickCount;
            return true;
        }

        /// <summary>
        /// Makes sure there is a path to the next waypoint. False while waiting (blocked, re-planning). Sets
        /// <paramref name="arrived"/> when the goal is reached.
        /// </summary>
        private bool EnsureLeg(out bool arrived)
        {
            arrived = false;

            while (_path == null || _pathIndex >= _path.Length)
            {
                _path = null;

                if (_next >= _waypoints.Count)
                {
                    arrived = true;
                    return true;
                }

                // Crossing points come in pairs one step apart; aim straight for the far one.
                if (!_teleportLegs.Contains(_next) && _next + 1 < _waypoints.Count &&
                    IdleRouteMap.Steps(_waypoints[_next], _waypoints[_next + 1]) <= 1 &&
                    IdleRouteMap.Steps(_pm.Location, _waypoints[_next + 1]) < 30)
                {
                    _next++;
                }

                var target = _waypoints[_next];

                if (SameTile(_pm.Location, target))
                {
                    if (_teleportLegs.Contains(_next))
                    {
                        _onTeleporter = true;
                        _teleportSince = Core.TickCount;
                        return false;
                    }

                    _next++;
                    continue;
                }

                var path = FindPath(_map, _pm.Location, target);
                if (path is { Length: > 0 })
                {
                    _path = path;
                    _pathIndex = 0;
                    return true;
                }

                // A waypoint that cannot be reached from here: try the one after it, then search the route again.
                if (_next + 1 < _waypoints.Count && !_teleportLegs.Contains(_next) &&
                    FindPath(_map, _pm.Location, _waypoints[_next + 1]) is { Length: > 0 } skip)
                {
                    _next++;
                    _path = skip;
                    _pathIndex = 0;
                    return true;
                }

                return Reroute();
            }

            return true;
        }

        private void TakeStep(Direction d, bool run, long now)
        {
            int nx = _pm.X, ny = _pm.Y;
            MovementRules.Offset(d, ref nx, ref ny);

            if (!MovementRules.CheckMovement(_pm, _map, _pm.Location, d, out _))
            {
                if (SplitDiagonal(d))
                {
                    TakeStep(_path[_pathIndex], run, now);
                    return;
                }

                // A diagonal step also needs the tiles beside it open.
                if (_doorTries < 2 && (OpenDoor(nx, ny) || OpenDoor(nx, _pm.Y) || OpenDoor(_pm.X, ny)))
                {
                    _doorTries++;
                    _doors++;
                    _waitUntil = now + 300;
                    return;
                }

                Blocked(now);
                return;
            }

            _pending = d;
            _pendingTo = new Point3D(nx, ny, _pm.Z);
            _facingAtSend = _pm.Direction & Direction.Mask;
            _tries = 0;
            Send(d, run, now);
        }

        private bool OpenDoor(int x, int y)
        {
            if (x == _pm.X && y == _pm.Y)
            {
                return false;
            }

            foreach (var door in _map.GetItemsAt<BaseDoor>(new Point2D(x, y)))
            {
                if (!door.Open)
                {
                    door.Use(_pm);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A player may only step diagonally when both tiles beside the step are open; the pathfinder's walker needs
        /// just one. A blocked diagonal becomes its two straight steps, in whichever order is open.
        /// </summary>
        private bool SplitDiagonal(Direction d)
        {
            if (((int)d & 1) == 0)
            {
                return false;
            }

            var a = (Direction)(((int)d - 1) & 7);
            var b = (Direction)(((int)d + 1) & 7);

            if (!TryPair(a, b) && !TryPair(b, a))
            {
                return false;
            }

            var path = new Direction[_path.Length + 1];
            Array.Copy(_path, path, _pathIndex);
            path[_pathIndex] = a;
            path[_pathIndex + 1] = b;
            Array.Copy(_path, _pathIndex + 1, path, _pathIndex + 2, _path.Length - _pathIndex - 1);
            _path = path;
            return true;

            bool TryPair(Direction first, Direction second)
            {
                if (!MovementRules.CheckMovement(_pm, _map, _pm.Location, first, out var z))
                {
                    return false;
                }

                int x = _pm.X, y = _pm.Y;
                MovementRules.Offset(first, ref x, ref y);
                if (!MovementRules.CheckMovement(_pm, _map, new Point3D(x, y, z), second, out _))
                {
                    return false;
                }

                (a, b) = (first, second);
                return true;
            }
        }

        /// <summary>Something stands in the way: wait a moment and find another path, then search the route again.</summary>
        private void Blocked(long now)
        {
            _path = null;
            _waitUntil = now + 500;

            if (++_legRetries > MaxLegRetries)
            {
                _legRetries = 0;
                Reroute();
            }
        }

        private bool Reroute()
        {
            if (++_reroutes > MaxReroutes || IdleRoutes.Get(_map) is not { } routeMap ||
                routeMap.FindRoutes(_pm.Location, [_goal])[0] is not { } route)
            {
                Finish("Blocked", $"The way is blocked at {_pm.X}, {_pm.Y}, so the walk stopped.", 0x22);
                return false;
            }

            var points = new List<IdlePointInfo>(route.Waypoints.Count);
            foreach (var p in route.Waypoints)
            {
                points.Add(IdlePointInfo.From(p));
            }

            SetRoute(points, route.TeleportLegs);
            _waitUntil = Core.TickCount + 250;
            return false;
        }

        /// <summary>Asks the client for one step; the 0x80 bit of the direction makes it a running step.</summary>
        private void Send(Direction d, bool run, long now)
        {
            _sentAt = now;
            _sentRun = run;
            _ranUnasked = false;
            _lastStepDelayMs = IdlePace.DelayMs(_pm.Mounted, run);

            var dir = (byte)(d & Direction.Mask);
            Span<byte> packet = [0x97, run ? (byte)(dir | (byte)Direction.Running) : dir];
            _pm.NetState?.Send(packet);
        }

        /// <summary>
        /// The fallback step: moves the character on the server, resynchronizes its own client (0x20), and shows the
        /// step to everyone around.
        /// </summary>
        private void ServerStep(Direction d, Point3D next)
        {
            _pm.Direction = d;
            _pm.SetLocation(next, false);
            _serverSteps++;

            var ns = _pm.NetState;
            ns?.ResetMovementState();
            ns?.SendMobileUpdate(_pm);

            foreach (var m in _map.GetMobilesInRange(next, Core.GlobalMaxUpdateRange))
            {
                if (m != _pm && m.NetState is { } other && m.CanSee(_pm))
                {
                    other.SendMobileMoving(m, _pm);
                }
            }
        }

        /// <summary>At the stand: the spot is checked again as the task starts; if it no longer works, a target nearby.</summary>
        private void Arrive()
        {
            if (IdleSystem.TryStartTaskAt(_pm, _spot.Id, out var error, _def.Id, _order))
            {
                Finish(null, $"You arrive at {_spot.Name} and start {_def.Name.ToLowerInvariant()}.", 0x44);
                return;
            }

            var here = IdleWorkHere.Run(_pm, _def.Id, _order);

            if (here.Status == "Started")
            {
                Finish(
                    null, $"You arrive near {_spot.Name} and start {_def.Name.ToLowerInvariant()} on a target beside it.",
                    0x44
                );
                return;
            }

            if (here.Status == "TargetNearby" && !_retargeted && here.Stand is { } stand)
            {
                // One short walk to a target nearby, for a spot that ran dry or is taken.
                _retargeted = true;
                _goal = new Point3D(stand.X, stand.Y, stand.Z);
                _spot = _spot with { Stand = stand, Target = here.Target };
                SetRoute([IdlePointInfo.From(_pm.Location), stand], null);
                _pm.SendMessage(0x3B2, $"{error ?? "The spot cannot be worked."} Walking to a target {here.Distance} tiles away.");
                _record.Message = $"The spot could not be worked; walking to a target {here.Distance} tiles away.";
                _record.UpdatedAt = Core.Now;
                _waitUntil = Core.TickCount + 250;
                return;
            }

            Finish("CannotStart", $"You arrive, but cannot start: {error ?? here.Message}", 0x22);
        }

        /// <summary>
        /// Ends the walk. <paramref name="stopReason"/> null means it arrived and the task started (Working, or Arrived
        /// when live work did not start); otherwise it is the record's StopReason. <paramref name="tell"/> false keeps
        /// the player from being messaged (logged out, replaced).
        /// </summary>
        public void Finish(string stopReason, string message, int hue, bool tell = true)
        {
            Stop();
            _walks.Remove(_pm);

            _record.Steps = _steps;
            _record.ElapsedSeconds = (Core.TickCount - _started) / 1000.0;

            if (stopReason == null)
            {
                _record.ReachedSpot = true;
                _record.StopReason = null;
                _record.Phase = IdleOnlineWork.IsWorking(_pm) ? "Working" : "Arrived";
                if (_record.Phase == "Arrived")
                {
                    message = "You are at the spot with the task set, but work did not start. [Idle resume starts it.";
                    hue = 0x3B2;
                }

                _record.Message = message;
                _record.UpdatedAt = Core.Now;
            }
            else
            {
                _record.Stopped(stopReason, message);
            }

            if (!tell || message == null)
            {
                return;
            }

            _pm.SendMessage(hue, message);

            using var sb = ValueStringBuilder.Create();
            var took = (Core.TickCount - _started) / 1000.0;
            sb.Append($"Walked {_steps} steps in {IdleRouting.FormatEta(took)} (estimate {IdleRouting.FormatEta(_etaSeconds)})");
            if (_doors > 0)
            {
                sb.Append($", opened {_doors} door(s)");
            }

            if (_serverSteps > 0)
            {
                sb.Append($", {_serverSteps} step(s) made by the server");
            }

            sb.Append('.');
            _pm.SendMessage(0x3B2, sb.ToString());
        }
    }
}
