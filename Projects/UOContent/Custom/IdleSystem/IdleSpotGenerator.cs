using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Server.Collections;
using Server.Commands;
using Server.Engines.CharacterCreation;
using Server.Engines.Craft;
using Server.Engines.Harvest;
using Server.Engines.Spawners;
using Server.Items;
using Server.Json;
using Server.Logging;
using Server.Mobiles;
using Server.Regions;
using Server.Text;
using MovementRules = Server.Movement.Movement;

namespace Server.Custom.Idle;

/// <summary>
/// [IdleGenSpots: scans a whole map for idle work spots (trees, mineable rock, shallow shore water, forges) and writes
/// them to <c>Data/Idle/spots.generated.json</c>. Runs on the game loop in time-boxed slices, so the world keeps
/// running; every spot is checked with the same rules the server applies when a task starts.
/// </summary>
/// <remarks>
/// Every map block the scan reads stays in the server's tile cache until restart. A full Felucca scan therefore
/// adds a few hundred MB of resident memory; working-set growth is reported after every phase.
/// </remarks>
public sealed class IdleSpotGenerator
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(IdleSpotGenerator));

    /// <summary>Target tiles tried per spacing cell when looking for the best spot in it.</summary>
    private const int SamplesPerCell = 8;

    /// <summary>Spots closer than this to a town's center are named "near" it; farther ones give the distance.</summary>
    private const int NearTownDistance = 150;

    private static IdleSpotGenerator _current;

    private readonly Mobile _from;
    private readonly Map _map;
    private readonly bool _dryRun;
    private readonly bool _reachability;
    private readonly IdleSpotGeneratorSettings _settings;
    private readonly IEnumerator<bool> _work;
    private readonly long _started;
    private readonly long _startWorkingSet;
    private readonly int _width;
    private readonly int _height;
    private readonly List<HarvestTask> _harvest = [];
    private readonly List<Point3D> _forges = [];
    private readonly List<IdleSpot> _smithies = [];
    private readonly Dictionary<string, Point3D> _smithyForges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _rejected = new(StringComparer.Ordinal);

    private TimerExecutionToken _timer;
    private double _loopMilliseconds;
    private double _longestTickMilliseconds;
    private string _longestTickPhase;
    private long _peakWorkingSet;
    private string _phase = "starting";
    private double _phaseStartLoop;
    private ReachMap _reach;

    private IdleSpotGenerator(Mobile from, Map map, bool dryRun, bool reachability)
    {
        _from = from;
        _map = map;
        _dryRun = dryRun;
        _settings = IdleSystem.Settings.SpotGenerator ?? new IdleSpotGeneratorSettings();
        _reachability = reachability && _settings.Reachability;
        _width = map.Width;
        _height = map.Height;
        _started = Stopwatch.GetTimestamp();
        _startWorkingSet = Environment.WorkingSet;
        _peakWorkingSet = _startWorkingSet;
        _work = Run().GetEnumerator();
    }

    public static bool IsRunning => _current != null;

    public static void Configure()
    {
        CommandSystem.Register("IdleGenSpots", AccessLevel.Administrator, OnCommand);

        ConsoleInputHandler.RegisterCommand(
            ["idlegen"],
            "Idle spot generator: idlegen [dry] [noreach] | idlegen status | idlegen stop",
            args => Core.LoopContext.Post(() => Dispatch(null, args?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []))
        );
    }

    [Usage("IdleGenSpots [dry] [noreach] | status | stop")]
    [Description(
        "Scans Felucca for idle work spots and writes Data/Idle/spots.generated.json, plus the walking route map " +
        "(Data/Idle/routes.felucca.bin). 'dry' scans without writing; 'noreach' skips the walkability pass and the route map. Restart the server afterwards to load the spots and free the tile cache."
    )]
    private static void OnCommand(CommandEventArgs e) => Dispatch(e.Mobile, e.Arguments);

    private static void Dispatch(Mobile from, string[] args)
    {
        var dry = false;
        var reach = true;

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "status":
                    {
                        Tell(from, _current == null ? "The idle spot generator is not running." : _current.Status());
                        return;
                    }
                case "stop":
                    {
                        if (_current == null)
                        {
                            Tell(from, "The idle spot generator is not running.");
                        }
                        else
                        {
                            _current.Finish("Stopped; nothing was written.");
                        }

                        return;
                    }
                case "dry":
                    {
                        dry = true;
                        break;
                    }
                case "noreach":
                    {
                        reach = false;
                        break;
                    }
                default:
                    {
                        Tell(from, "Usage: IdleGenSpots [dry] [noreach] | status | stop");
                        return;
                    }
            }
        }

        if (_current != null)
        {
            Tell(from, "The idle spot generator is already running. 'status' shows progress.");
            return;
        }

        _current = new IdleSpotGenerator(from, Map.Felucca, dry, reach);
        _current.Report(
            $"Idle spot generator started on {Map.Felucca}{(dry ? " (dry run)" : "")}{(_current._reachability ? "" : ", no reachability")}. " +
            $"Using up to {_current._settings.BudgetMilliseconds:0} ms per tick. Working set {Mb(_current._startWorkingSet)}."
        );

        Timer.StartTimer(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), _current.Tick, out _current._timer);
    }

    private static void Tell(Mobile from, string message)
    {
        if (from == null)
        {
            logger.Information("{Message}", message);
        }
        else
        {
            from.SendMessage(message);
        }
    }

    private static string Mb(long bytes) => $"{bytes / (1024 * 1024):N0} MB";

    private void Report(string message)
    {
        logger.Information("{Message}", message);

        if (_from is { Deleted: false, NetState: not null })
        {
            _from.SendMessage(0x59, message);
        }
    }

    private string Status() =>
        $"Idle spot generator: {_phase}. {Stopwatch.GetElapsedTime(_started).TotalSeconds:N0} s elapsed, " +
        $"{_loopMilliseconds / 1000:N1} s of loop time, longest tick {_longestTickMilliseconds:N0} ms, " +
        $"working set {Mb(Environment.WorkingSet)} " +
        $"(+{Mb(Environment.WorkingSet - _startWorkingSet)}).";

    private void Tick()
    {
        // The world save snapshot owns the loop while it runs; resume on the next tick.
        if (World.Saving)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();

        try
        {
            while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < _settings.BudgetMilliseconds)
            {
                if (!_work.MoveNext())
                {
                    Finish(null);
                    return;
                }
            }
        }
        catch (Exception e)
        {
            logger.Error(e, "Idle spot generator failed during {Phase}", _phase);
            Finish($"Failed during {_phase}: {e.Message}. Nothing was written.");
        }
        finally
        {
            var tick = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            _loopMilliseconds += tick;
            if (tick > _longestTickMilliseconds)
            {
                _longestTickMilliseconds = tick;
                _longestTickPhase = _phase;
            }
            _peakWorkingSet = Math.Max(_peakWorkingSet, Environment.WorkingSet);
        }
    }

    private void Finish(string message)
    {
        _timer.Cancel();
        _reach = null;

        if (_current == this)
        {
            _current = null;
        }

        if (message != null)
        {
            Report(message);
        }
    }

    private void BeginPhase(string phase)
    {
        _phase = phase;
        _phaseStartLoop = _loopMilliseconds;
    }

    private void EndPhase(string summary)
    {
        var ws = Environment.WorkingSet;
        _peakWorkingSet = Math.Max(_peakWorkingSet, ws);
        Report(
            $"{_phase}: {summary} ({(_loopMilliseconds - _phaseStartLoop) / 1000:N1} s loop time; " +
            $"working set {Mb(ws)}, +{Mb(ws - _startWorkingSet)} since start)"
        );
    }

    private void Reject(string reason) => _rejected[reason] = _rejected.GetValueOrDefault(reason) + 1;

    private bool IsExcludedArea(int x, int y)
    {
        foreach (var area in _settings.ExcludeAreas)
        {
            if (area.Contains(_map, x, y))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAllowedStand(int x, int y) => !IsExcludedArea(x, y);

    private IEnumerable<bool> Run()
    {
        foreach (var step in Prepare())
        {
            yield return step;
        }

        foreach (var step in ScanTiles())
        {
            yield return step;
        }

        foreach (var step in ScanItems())
        {
            yield return step;
        }

        foreach (var task in _harvest)
        {
            foreach (var step in PickHarvestSpots(task))
            {
                yield return step;
            }
        }

        foreach (var step in PickSmithySpots())
        {
            yield return step;
        }

        if (_reachability)
        {
            foreach (var step in WalkReachability())
            {
                yield return step;
            }
        }

        foreach (var step in TagAndValidate())
        {
            yield return step;
        }

        WriteResults();
    }

    private sealed class HarvestTask
    {
        public IdleTaskDefinition Def;
        public HarvestIdleHandler Handler;
        public HarvestDefinition Definition;
        public bool Fishing;
        public int Range;
        public bool[] Land;
        public bool[] Statics;
        public ulong[] Bits;
        public long Tiles;
        public readonly List<IdleSpot> Spots = [];
    }

    private IEnumerable<bool> Prepare()
    {
        BeginPhase("Preparing");

        foreach (var def in IdleSystem.Tasks.Values)
        {
            if (IdleTaskHandler.Get(def.Kind) is not HarvestIdleHandler handler)
            {
                continue;
            }

            var definition = handler.GetDefinition(def);
            var task = new HarvestTask
            {
                Def = def,
                Handler = handler,
                Definition = definition,
                Fishing = handler.IsFishing(def),
                Range = handler.GetRange(def),
                Land = new bool[TileData.MaxLandValue + 1],
                Statics = new bool[TileData.MaxItemValue + 1],
                Bits = new ulong[((long)_width * _height + 63) / 64]
            };

            // The same predicate ValidateTarget uses, tabulated so the full-map pass is a lookup per tile.
            for (var id = 0; id < task.Land.Length; id++)
            {
                task.Land[id] = definition.Validate(id, true);
            }

            for (var id = 0; id < task.Statics.Length; id++)
            {
                task.Statics[id] = definition.Validate(id, false);
            }

            _harvest.Add(task);
        }

        EndPhase($"{_harvest.Count} harvest task(s)");
        yield return true;
    }

    private IEnumerable<bool> ScanTiles()
    {
        BeginPhase("Scanning map tiles");

        var tiles = _map.Tiles;
        var blocksWide = tiles.BlockWidth;
        var blocksHigh = tiles.BlockHeight;

        for (var bx = 0; bx < blocksWide; bx++)
        {
            for (var by = 0; by < blocksHigh; by++)
            {
                ScanBlock(tiles, bx, by);

                // First reads come from disk; 64 blocks keeps a slice short even on a cold file cache.
                if ((by & 63) == 63)
                {
                    yield return true;
                }
            }
        }

        using var sb = ValueStringBuilder.Create();
        foreach (var task in _harvest)
        {
            sb.Append($"{task.Def.Id} {task.Tiles:N0} tiles, ");
        }

        sb.Append($"{_forges.Count} forge statics");
        EndPhase(sb.ToString());
    }

    private void ScanBlock(TileMatrix tiles, int bx, int by)
    {
        var x0 = bx << TileMatrix.SectorShift;
        var y0 = by << TileMatrix.SectorShift;

        if (IsExcludedArea(x0, y0) && IsExcludedArea(x0 + 7, y0 + 7) && IsExcludedArea(x0, y0 + 7) && IsExcludedArea(x0 + 7, y0))
        {
            return;
        }

        var land = tiles.GetLandBlock(bx, by);
        var statics = tiles.GetStaticBlock(bx, by);

        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                var x = x0 + i;
                var y = y0 + j;

                if (x >= _width || y >= _height || IsExcludedArea(x, y))
                {
                    continue;
                }

                var landTile = land[(j << 3) + i];
                var landId = landTile.ID & TileData.MaxLandValue;
                var cell = statics[i][j];
                var index = (long)y * _width + x;

                foreach (var task in _harvest)
                {
                    var match = false;

                    for (var k = 0; !match && k < cell.Length; k++)
                    {
                        match = task.Statics[cell[k].ID & TileData.MaxItemValue];
                    }

                    // Land under a floor or other solid static cannot be targeted in game (MatchesVisibleTile).
                    if (!match && task.Land[landId])
                    {
                        match = true;
                        for (var k = 0; match && k < cell.Length; k++)
                        {
                            match = !HarvestIdleHandler.CoversLand(cell[k].ID, cell[k].Z, landTile.Z);
                        }
                    }

                    if (match)
                    {
                        task.Bits[index >> 6] |= 1UL << (int)(index & 63);
                        task.Tiles++;
                    }
                }

                for (var k = 0; k < cell.Length; k++)
                {
                    if (SmithyStations.IsForge(cell[k].ID))
                    {
                        _forges.Add(new Point3D(x, y, cell[k].Z));
                    }
                }
            }
        }
    }

    private IEnumerable<bool> ScanItems()
    {
        BeginPhase("Finding forge items");

        const int chunk = 256;
        var found = 0;

        for (var x = 0; x < _width; x += chunk)
        {
            for (var y = 0; y < _height; y += chunk)
            {
                foreach (var item in _map.GetItemsInBounds(new Rectangle2D(x, y, chunk, chunk)))
                {
                    if ((item.GetType().IsDefined(typeof(ForgeAttribute), false) || SmithyStations.IsForge(item.ItemID)) &&
                        !IsExcludedArea(item.X, item.Y))
                    {
                        _forges.Add(item.Location);
                        found++;
                    }
                }
            }

            yield return true;
        }

        EndPhase($"{found} forge items, {_forges.Count} forges in all");
    }

    private bool HasBit(HarvestTask task, int x, int y)
    {
        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            return false;
        }

        var index = (long)y * _width + x;
        return (task.Bits[index >> 6] & (1UL << (int)(index & 63))) != 0;
    }

    private IEnumerable<bool> PickHarvestSpots(HarvestTask task)
    {
        BeginPhase($"Picking {task.Def.Id} spots");

        var cellSize = Math.Max(4, _settings.MinSpacing);
        var candidates = new List<IdleSpot>();

        for (var cy = 0; cy < _height; cy += cellSize)
        {
            for (var cx = 0; cx < _width; cx += cellSize)
            {
                if (PickInCell(task, cx, cy, cellSize) is { } spot)
                {
                    candidates.Add(spot);
                }

                // A shoreline cell can cost ~1 ms (deep-water checks), so a full row would overrun the budget.
                if ((cx / cellSize & 7) == 7)
                {
                    yield return true;
                }
            }
        }

        // Densest first, so where spots compete for spacing the one with the most targets around it wins.
        candidates.Sort((a, b) => b.Targets.CompareTo(a.Targets));
        Space(candidates, task.Spots, _settings.MinSpacing);

        EndPhase($"{candidates.Count:N0} candidates, {task.Spots.Count:N0} spots after spacing");
    }

    private IdleSpot PickInCell(HarvestTask task, int cx, int cy, int cellSize)
    {
        using var targets = PooledRefList<int>.Create();

        var maxX = Math.Min(cx + cellSize, _width);
        var maxY = Math.Min(cy + cellSize, _height);

        for (var y = cy; y < maxY; y++)
        {
            for (var x = cx; x < maxX; x++)
            {
                if (HasBit(task, x, y))
                {
                    targets.Add((y << 16) | x);
                }
            }
        }

        if (targets.Count == 0)
        {
            return null;
        }

        var samples = Math.Min(SamplesPerCell, targets.Count);
        Point3D bestTarget = default;
        Point3D bestStand = default;
        var bestDensity = 0;

        for (var s = 0; s < samples; s++)
        {
            var packed = targets[(int)((s + 0.5) * targets.Count / samples)];
            var x = packed & 0xFFFF;
            var y = packed >> 16;

            // Deep-water specials need a sea serpent killed, so idle fishing is shore-only (as in ValidateTarget).
            if (task.Fishing && FishingIdleRules.IsDeepWater(_map, x, y))
            {
                continue;
            }

            var target = new Point3D(x, y, HarvestIdleHandler.TargetZ(task.Definition, _map, x, y));

            if (!IdleStandFinder.TryFind(_map, target, task.Range, null, IsAllowedStand, out var stand))
            {
                continue;
            }

            var density = 0;
            for (var dx = -task.Range; dx <= task.Range; dx++)
            {
                for (var dy = -task.Range; dy <= task.Range; dy++)
                {
                    density += HasBit(task, stand.X + dx, stand.Y + dy) ? 1 : 0;
                }
            }

            if (density > bestDensity)
            {
                bestDensity = density;
                bestTarget = target;
                bestStand = stand;
            }
        }

        if (bestDensity == 0)
        {
            Reject($"{task.Def.Id}: areas with targets but nowhere to stand (open water, mountain interiors)");
            return null;
        }

        return new IdleSpot
        {
            Id = $"{task.Def.Id}-{bestTarget.X}-{bestTarget.Y}",
            TaskId = task.Def.Id,
            Map = _map,
            Location = bestTarget,
            Stand = bestStand,
            Targets = bestDensity
        };
    }

    private static void Space(List<IdleSpot> ordered, List<IdleSpot> accepted, int spacing)
    {
        spacing = Math.Max(1, spacing);
        var grid = new Dictionary<int, List<IdleSpot>>();
        var squared = spacing * spacing;

        foreach (var spot in ordered)
        {
            var p = IdleSpotIndex.StandPoint(spot);
            var gx = p.X / spacing;
            var gy = p.Y / spacing;
            var clear = true;

            for (var x = gx - 1; clear && x <= gx + 1; x++)
            {
                for (var y = gy - 1; clear && y <= gy + 1; y++)
                {
                    if (!grid.TryGetValue((x << 16) | (y & 0xFFFF), out var list))
                    {
                        continue;
                    }

                    foreach (var other in list)
                    {
                        var q = IdleSpotIndex.StandPoint(other);
                        var dx = p.X - q.X;
                        var dy = p.Y - q.Y;

                        if (dx * dx + dy * dy < squared)
                        {
                            clear = false;
                            break;
                        }
                    }
                }
            }

            if (!clear)
            {
                continue;
            }

            var key = (gx << 16) | (gy & 0xFFFF);
            if (!grid.TryGetValue(key, out var cell))
            {
                grid[key] = cell = [];
            }

            cell.Add(spot);
            accepted.Add(spot);
        }
    }

    private IEnumerable<bool> PickSmithySpots()
    {
        BeginPhase("Picking smithy spots");

        var stationTasks = new List<(IdleTaskDefinition Def, IdleTaskHandler Handler)>();
        foreach (var def in IdleSystem.Tasks.Values)
        {
            var handler = IdleTaskHandler.Get(def.Kind);
            if (handler?.UsesStation(def) == true)
            {
                stationTasks.Add((def, handler));
            }
        }

        var seen = new HashSet<Point3D>();
        var candidates = new List<IdleSpot>();

        for (var i = 0; i < _forges.Count; i++)
        {
            var forge = _forges[i];
            if (seen.Add(forge) && stationTasks.Count > 0 && PickSmithy(forge, stationTasks) is { } spot)
            {
                candidates.Add(spot);
            }

            // Each forge runs the station check for up to 25 tiles around it.
            if ((i & 3) == 3)
            {
                yield return true;
            }
        }

        // Spots serving both smelting and smithing first.
        candidates.Sort((a, b) => b.TaskIds.Count.CompareTo(a.TaskIds.Count));
        Space(candidates, _smithies, _settings.SmithySpacing);

        EndPhase($"{seen.Count} distinct forges, {candidates.Count} with a place to stand, {_smithies.Count} spots after spacing");
    }

    private IdleSpot PickSmithy(Point3D forge, List<(IdleTaskDefinition Def, IdleTaskHandler Handler)> stationTasks)
    {
        IdleSpot best = null;

        for (var dx = -SmithyStations.Range; dx <= SmithyStations.Range; dx++)
        {
            for (var dy = -SmithyStations.Range; dy <= SmithyStations.Range; dy++)
            {
                var x = forge.X + dx;
                var y = forge.Y + dy;

                // The floor the forge stands on, not a level above or below it.
                if (IsExcludedArea(x, y) || !_map.CanSpawnMobile(x, y, forge.Z - 8, forge.Z + 12, false, false, out var z))
                {
                    continue;
                }

                var stand = new Point3D(x, y, z);
                var ids = new List<string>();

                foreach (var (def, handler) in stationTasks)
                {
                    if (handler.CheckStation(null, def, _map, stand, out _))
                    {
                        ids.Add(def.Id);
                    }
                }

                if (ids.Count > (best?.TaskIds.Count ?? 0))
                {
                    best = new IdleSpot { Id = $"smithy-{x}-{y}", TaskIds = ids, Map = _map, Location = stand };
                }
            }
        }

        if (best == null)
        {
            Reject("smithy: no standable tile with the forge in reach");
        }
        else
        {
            _smithyForges[best.Id] = forge;
        }

        return best;
    }

    private IEnumerable<bool> WalkReachability()
    {
        BeginPhase("Walking the map from cities, moongates, and teleporters");

        // Setup is split across ticks: the position buffer alone is one byte per map tile.
        _reach = new ReachMap(_map);
        yield return true;

        _reach.SeedFromTravel();
        yield return true;

        _reach.LoadTeleporters();
        yield return true;

        // About 10 µs per position, so 512 keeps one slice near 5 ms.
        while (_reach.Step(512))
        {
            yield return true;
        }

        EndPhase($"{_reach.Visited:N0} walkable positions reached");

        if (_reach.Routes == null)
        {
            yield break;
        }

        BeginPhase("Building the route map");

        var stamps = new List<IdleMapFileStamp>();
        IdleMapFileStamp.Collect(_map, stamps);

        foreach (var step in _reach.Routes.Build(_reach.Floor, stamps))
        {
            yield return step;
        }

        _routeMap = _reach.Routes.Result;
        EndPhase($"{_routeMap.NodeCount:N0} areas, {_routeMap.EdgeCount:N0} links");
    }

    private IdleRouteMap _routeMap;

    private IEnumerable<bool> TagAndValidate()
    {
        BeginPhase("Tagging and validating spots");

        var towns = new List<(string Name, Point3D Center)>();
        foreach (var region in Region.Regions)
        {
            // Top-level towns only; their sub-regions (farm fields, buildings) are not towns to travel to.
            if (region.Map == _map && region is TownRegion && region.Parent == null && !string.IsNullOrEmpty(region.Name))
            {
                towns.Add((region.Name, region.GoLocation != Point3D.Zero ? region.GoLocation : AreaCenter(region)));
            }
        }

        var all = new List<IdleSpot>();
        foreach (var task in _harvest)
        {
            all.AddRange(task.Spots);
        }

        all.AddRange(_smithies);

        var kept = new List<IdleSpot>(all.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < all.Count; i++)
        {
            if (TagAndCheck(all[i], towns))
            {
                if (ids.Add(all[i].Id))
                {
                    kept.Add(all[i]);
                }
                else
                {
                    Reject("same stand as another spot after moving to a reachable tile");
                }
            }

            if ((i & 63) == 63)
            {
                yield return true;
            }
        }

        _final = kept;

        var reachable = 0;
        var unreachable = 0;
        foreach (var spot in kept)
        {
            reachable += spot.Reachable == true ? 1 : 0;
            unreachable += spot.Reachable == false ? 1 : 0;
        }

        EndPhase(
            _reachability
                ? $"{kept.Count:N0} spots kept of {all.Count:N0}; {reachable:N0} reachable ({_movedStands:N0} moved to a " +
                  $"reachable stand), {unreachable:N0} unreachable"
                : $"{kept.Count:N0} spots kept of {all.Count:N0}"
        );
    }

    private List<IdleSpot> _final = [];

    private static Point3D AreaCenter(Region region)
    {
        if (region.Area.Length == 0)
        {
            return Point3D.Zero;
        }

        var r = region.Area[0];
        return new Point3D(r.Start.X + r.Width / 2, r.Start.Y + r.Height / 2, 0);
    }

    private bool TagAndCheck(IdleSpot spot, List<(string Name, Point3D Center)> towns)
    {
        // The nearest stand can be a walled-in nook (behind a forge, a fenced yard); prefer one a player can walk to.
        if (_reach != null && !_reach.IsReached(IdleSpotIndex.StandPoint(spot)) && MoveToReachableStand(spot))
        {
            _movedStands++;
        }

        var stand = IdleSpotIndex.StandPoint(spot);
        var region = Region.Find(stand, _map);

        if (region.IsPartOf<HouseRegion>())
        {
            Reject("inside a house");
            return false;
        }

        if (_settings.ExcludeDungeons && region.IsPartOf<DungeonRegion>())
        {
            Reject("dungeon");
            return false;
        }

        string name = null;
        for (var r = region; r != null; r = r.Parent)
        {
            name ??= string.IsNullOrEmpty(r.Name) ? null : r.Name;

            foreach (var excluded in _settings.ExcludeRegions)
            {
                if (excluded.InsensitiveEquals(r.Name))
                {
                    Reject($"region {r.Name}");
                    return false;
                }
            }
        }

        // The rule a task start applies, so a spot that would be refused is never written.
        foreach (var taskId in spot.AllTaskIds())
        {
            var def = IdleSystem.GetTask(taskId);
            var handler = IdleTaskHandler.Get(def?.Kind);
            var target = new IdleTarget(spot.Id, _map, spot.Location);

            if (handler == null || !handler.ValidateTarget(null, def, target, out _) ||
                !Utility.InRange(stand, spot.Location, handler.GetRange(def)) ||
                !handler.CheckStation(null, def, _map, stand, out _))
            {
                Reject($"{taskId}: failed the server's start checks");
                return false;
            }
        }

        spot.Region = name;
        spot.RegionKind = IdleRegions.KindOf(region);
        spot.HostileSpawners = CountHostileSpawners(stand);
        spot.Reachable = _reach?.IsReached(stand);

        var best = int.MaxValue;
        foreach (var (town, center) in towns)
        {
            var d = IdleSpotIndex.Distance(stand, center);
            if (d < best)
            {
                best = d;
                spot.Town = town;
            }
        }

        spot.TownDistance = spot.Town == null ? 0 : best;

        var what = spot.TaskId != null ? IdleSystem.GetTask(spot.TaskId)?.Name ?? spot.TaskId : "Smithy";
        spot.Name = spot.Town == null ? what
            : spot.TownDistance <= NearTownDistance ? $"{what} near {spot.Town}"
            : $"{what}, {spot.TownDistance} tiles from {spot.Town}";
        return true;
    }

    private int _movedStands;

    /// <summary>Another standing tile within the task's reach that the walk reached. False if there is none.</summary>
    private bool MoveToReachableStand(IdleSpot spot)
    {
        if (spot.TaskId != null)
        {
            HarvestTask task = null;
            foreach (var t in _harvest)
            {
                task = t.Def.Id == spot.TaskId ? t : task;
            }

            if (task == null)
            {
                return false;
            }

            var target = spot.Location;
            foreach (var (dx, dy) in IdleWorkHere.Offsets(task.Range))
            {
                var x = target.X + dx;
                var y = target.Y + dy;

                if (!IsExcludedArea(x, y) && _map.CanSpawnMobile(x, y, target.Z - 40, target.Z + 40, false, false, out var z) &&
                    _reach.IsReached(new Point3D(x, y, z)))
                {
                    spot.Stand = new Point3D(x, y, z);
                    return true;
                }
            }

            return false;
        }

        if (!_smithyForges.TryGetValue(spot.Id, out var forge))
        {
            return false;
        }

        foreach (var (dx, dy) in IdleWorkHere.Offsets(SmithyStations.Range))
        {
            var x = forge.X + dx;
            var y = forge.Y + dy;

            if (IsExcludedArea(x, y) || !_map.CanSpawnMobile(x, y, forge.Z - 8, forge.Z + 12, false, false, out var z))
            {
                continue;
            }

            var stand = new Point3D(x, y, z);
            if (!_reach.IsReached(stand))
            {
                continue;
            }

            var all = true;
            foreach (var taskId in spot.AllTaskIds())
            {
                var def = IdleSystem.GetTask(taskId);
                all &= def != null && IdleTaskHandler.Get(def.Kind)?.CheckStation(null, def, _map, stand, out _) == true;
            }

            if (all)
            {
                spot.Location = stand;
                spot.Id = $"smithy-{x}-{y}";
                return true;
            }
        }

        return false;
    }

    private int CountHostileSpawners(Point3D p)
    {
        var count = 0;

        foreach (var spawner in _map.GetItemsInRange<BaseSpawner>(p, _settings.HostileSpawnerRange))
        {
            if (spawner.Spawned == null)
            {
                continue;
            }

            foreach (var spawned in spawner.Spawned.Keys)
            {
                if (spawned is BaseCreature { Deleted: false, Controlled: false } bc and not BaseVendor &&
                    bc.FightMode is FightMode.Closest or FightMode.Strongest or FightMode.Weakest or FightMode.Evil)
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private void WriteResults()
    {
        BeginPhase("Writing");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var spot in _final)
        {
            foreach (var id in spot.AllTaskIds())
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
            }
        }

        using (var sb = ValueStringBuilder.Create())
        {
            foreach (var (id, count) in counts)
            {
                sb.Append($"{id} {count:N0}, ");
            }

            sb.Append($"total {_final.Count:N0} spots");
            Report(sb.ToString());
        }

        if (_rejected.Count > 0)
        {
            using var sb = ValueStringBuilder.Create();
            sb.Append("Rejected: ");
            foreach (var (reason, count) in _rejected)
            {
                sb.Append($"{reason} {count:N0}; ");
            }

            Report(sb.ToString());
        }

        var elapsed = Stopwatch.GetElapsedTime(_started);
        var summary =
            $"{elapsed.TotalSeconds:N0} s elapsed, {_loopMilliseconds / 1000:N1} s of loop time, longest tick " +
            $"{_longestTickMilliseconds:N0} ms ({_longestTickPhase}). Working set " +
            $"{Mb(Environment.WorkingSet)} (+{Mb(Environment.WorkingSet - _startWorkingSet)}), peak {Mb(_peakWorkingSet)}.";

        if (_dryRun)
        {
            Report($"Dry run finished: nothing written. {summary}");
            return;
        }

        var file = new IdleSpotFile { Generated = Core.Now };

        // Spots for other maps from an earlier run stay.
        try
        {
            if (File.Exists(IdleSpotLoader.GeneratedPath) &&
                JsonConfig.Deserialize<IdleSpotFile>(IdleSpotLoader.GeneratedPath) is { } existing)
            {
                foreach (var spot in existing.Spots)
                {
                    if (spot.Map != _map)
                    {
                        file.Spots.Add(spot);
                    }
                }

                foreach (var stamp in existing.MapFiles)
                {
                    if (stamp.Map != _map.Name)
                    {
                        file.MapFiles.Add(stamp);
                    }
                }
            }
        }
        catch (Exception e)
        {
            logger.Warning(e, "Could not read the existing {Path}; it will be replaced", IdleSpotLoader.GeneratedPath);
        }

        IdleMapFileStamp.Collect(_map, file.MapFiles);

        _final.Sort(
            (a, b) =>
            {
                var c = string.CompareOrdinal(a.TaskId ?? "smithy", b.TaskId ?? "smithy");
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            }
        );
        file.Spots.AddRange(_final);

        var options = JsonConfig.GetOptions();
        options.WriteIndented = false;

        Directory.CreateDirectory(Path.GetDirectoryName(IdleSpotLoader.GeneratedPath)!);
        File.WriteAllText(IdleSpotLoader.GeneratedPath, JsonSerializer.Serialize(file, options));

        if (_routeMap != null)
        {
            var routePath = IdleRoutes.PathFor(_map);
            _routeMap.Write(routePath);
            Report($"Wrote the route map ({new FileInfo(routePath).Length / 1024:N0} KB) to Data/Idle/{Path.GetFileName(routePath)}.");
        }

        Report(
            $"Wrote {file.Spots.Count:N0} spots ({new FileInfo(IdleSpotLoader.GeneratedPath).Length / 1024:N0} KB) to " +
            $"Data/Idle/spots.generated.json. {summary} Restart to load them and free the tile cache."
        );
    }

    /// <summary>
    /// Flood fill over walkable positions with the server's own movement check, seeded at the starting cities and
    /// moongates and following teleporters. Positions are keyed by tile; a second floor over a visited tile is tracked
    /// separately so upper levels of buildings and caves are not lost. Every step also feeds the route map.
    /// </summary>
    private sealed class ReachMap
    {
        // Heights are stored offset by 128 so a fresh (zeroed) buffer means unseen without a fill pass.
        private const byte Unseen = 0;

        // Same tile and within this height: treated as the same floor.
        private const int SameFloor = 10;

        private static readonly Direction[] _directions = [Direction.North, Direction.East, Direction.South, Direction.West];

        private readonly Map _map;
        private readonly int _width;
        private readonly int _height;
        private readonly byte[] _floor;
        private readonly Dictionary<long, int> _upperFloors = [];
        private readonly Queue<long> _queue = new();
        private readonly Dictionary<int, List<Point3D>> _teleports = [];
        private readonly IdleReachProbe _probe = new();

        public long Visited { get; private set; }

        /// <summary>The route map collected during the walk; null for maps too large to pack a position in 25 bits.</summary>
        public IdleRouteBuilder Routes { get; }

        public byte[] Floor => _floor;

        public ReachMap(Map map)
        {
            _map = map;
            _width = map.Width;
            _height = map.Height;
            _floor = new byte[_width * _height];
            Routes = (long)_width * _height < 1L << 25 ? new IdleRouteBuilder(map) : null;
        }

        public void SeedFromTravel()
        {
            var cities = _map == Map.Trammel ? CharacterCreation.TrammelStartingCities : CharacterCreation.FeluccaStartingCities;
            foreach (var city in cities)
            {
                if (city.Map == _map)
                {
                    Visit(city.Location.X, city.Location.Y, city.Location.Z, -1, default, false);
                }
            }

            foreach (var list in (ReadOnlySpan<PMList>)[PMList.Trammel, PMList.Felucca, PMList.Ilshenar, PMList.Malas])
            {
                if (list.Map != _map)
                {
                    continue;
                }

                foreach (var entry in list.Entries)
                {
                    Visit(entry.Location.X, entry.Location.Y, entry.Location.Z, -1, default, false);
                }
            }
        }

        public void LoadTeleporters()
        {
            var path = Path.Combine(Core.BaseDirectory, "Data/teleporters.json");
            if (!File.Exists(path))
            {
                return;
            }

            var entries = JsonConfig.Deserialize<List<TeleporterEntry>>(path);
            foreach (var e in entries ?? [])
            {
                if (e.Src?.Loc is not { Length: 3 } src || e.Dst?.Loc is not { Length: 3 } dst)
                {
                    continue;
                }

                if (e.Src.Map == _map && e.Dst.Map == _map)
                {
                    AddTeleport(src, dst);
                    if (e.Back)
                    {
                        AddTeleport(dst, src);
                    }
                }
            }
        }

        private void AddTeleport(int[] from, int[] to)
        {
            if (!InBounds(from[0], from[1]))
            {
                return;
            }

            var key = from[1] * _width + from[0];
            if (!_teleports.TryGetValue(key, out var list))
            {
                _teleports[key] = list = [];
            }

            list.Add(new Point3D(to[0], to[1], to[2]));
        }

        private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height;

        /// <summary>
        /// Marks a position reached from <paramref name="from"/> (area <paramref name="fromNode"/>, -1 for a starting
        /// point) and queues it if it is new; either way the route map learns the step.
        /// </summary>
        private void Visit(int x, int y, int z, int fromNode, Point3D from, bool teleport)
        {
            if (!InBounds(x, y))
            {
                return;
            }

            var index = y * _width + x;
            var floor = _floor[index];
            int node;

            if (floor == Unseen)
            {
                _floor[index] = (byte)(Math.Clamp(z, -127, 127) + 128);
                node = Routes?.Claim(x, y, z, fromNode, from, teleport) ?? 0;
                Routes?.SetGround(index, node);
            }
            else if (Math.Abs(floor - 128 - z) <= SameFloor)
            {
                Routes?.Link(fromNode, Routes.GroundNode(index, x, y), from, new Point3D(x, y, z), teleport);
                return;
            }
            else
            {
                var key = UpperKey(index, z);
                if (_upperFloors.TryGetValue(key, out var existing))
                {
                    Routes?.Link(fromNode, existing, from, new Point3D(x, y, z), teleport);
                    return;
                }

                node = Routes?.Claim(x, y, z, fromNode, from, teleport) ?? 0;
                _upperFloors[key] = node;
            }

            Routes?.Add(node, x, y, z);
            Visited++;
            _queue.Enqueue(((long)node << 33) | ((long)index << 8) | (byte)(sbyte)Math.Clamp(z, -128, 127));
        }

        private static long UpperKey(int index, int z) => ((long)index << 5) | (uint)((z + 128) >> 3);

        /// <summary>Expands up to <paramref name="nodes"/> positions; false when the walk is done.</summary>
        public bool Step(int nodes)
        {
            for (var n = 0; n < nodes && _queue.Count > 0; n++)
            {
                var packed = _queue.Dequeue();
                var node = (int)(packed >> 33);
                var index = (int)((packed >> 8) & 0x1FFFFFF);
                var z = (sbyte)(byte)(packed & 0xFF);
                var x = index % _width;
                var y = index / _width;
                var loc = new Point3D(x, y, z);

                foreach (var d in _directions)
                {
                    if (MovementRules.CheckMovement(_probe, _map, loc, d, out var newZ))
                    {
                        var nx = x;
                        var ny = y;
                        MovementRules.Offset(d, ref nx, ref ny);
                        Visit(nx, ny, newZ, node, loc, false);
                    }
                }

                if (_teleports.TryGetValue(index, out var destinations))
                {
                    foreach (var dst in destinations)
                    {
                        Visit(dst.X, dst.Y, dst.Z, node, loc, true);
                    }
                }
            }

            return _queue.Count > 0;
        }

        public bool IsReached(Point3D p)
        {
            if (!InBounds(p.X, p.Y))
            {
                return false;
            }

            var index = p.Y * _width + p.X;
            var floor = _floor[index];

            if (floor == Unseen)
            {
                return false;
            }

            if (Math.Abs(floor - 128 - p.Z) <= SameFloor + 6)
            {
                return true;
            }

            for (var z = p.Z - 16; z <= p.Z + 16; z += 8)
            {
                if (_upperFloors.ContainsKey(UpperKey(index, z)))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class TeleporterEntry
    {
        [JsonPropertyName("src")]
        public TeleporterEnd Src { get; set; }

        [JsonPropertyName("dst")]
        public TeleporterEnd Dst { get; set; }

        [JsonPropertyName("back")]
        public bool Back { get; set; }
    }

    private sealed class TeleporterEnd
    {
        [JsonPropertyName("map")]
        public Map Map { get; set; }

        [JsonPropertyName("loc")]
        public int[] Loc { get; set; }
    }
}
