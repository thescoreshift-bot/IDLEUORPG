using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Server.Custom.Idle;

/// <summary>
/// Operator knobs, loaded from <c>Configuration/idle.json</c>. Missing keys keep these defaults.
/// </summary>
public class IdleSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Offline work speed as a fraction of online speed (0.6 = 60%).</summary>
    public double OfflineRate { get; set; } = 0.6;

    /// <summary>Speed of live work while the character is in the world (1.0 = the normal online pace).</summary>
    public double OnlineRate { get; set; } = 1.0;

    /// <summary>
    /// After this many hours of live work without logging out, live work quietly continues at the offline rate.
    /// 0 = never.
    /// </summary>
    public double OnlineFullRateHours { get; set; } = 4;

    /// <summary>On login, the task picks up again as live work if the character is still at the spot.</summary>
    public bool ResumeOnLogin { get; set; } = true;

    /// <summary>Focus ([Focus]): a short burst of faster work, live or offline.</summary>
    public bool FocusEnabled { get; set; } = true;

    public double FocusDurationSeconds { get; set; } = 120;

    /// <summary>Extra work speed while focused (0.15 = 15% faster).</summary>
    public double FocusSpeedBonus { get; set; } = 0.15;

    /// <summary>Wait after a Focus ends before the next can start.</summary>
    public double FocusCooldownSeconds { get; set; } = 300;

    /// <summary>Time away beyond this is not counted.</summary>
    public TimeSpan MaxOfflineTime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Absences shorter than this are ignored (short reconnects, crashes).</summary>
    public TimeSpan MinOfflineTime { get; set; } = TimeSpan.FromMinutes(1);

    public bool ShowSummaryGump { get; set; } = true;

    /// <summary>Stones of carrying capacity idle deliveries leave free; the rest goes to the bank.</summary>
    public int KeepFreeStones { get; set; } = 20;

    public IdleSkillGainSettings SkillGain { get; set; } = new();

    public IdleSpotGeneratorSettings SpotGenerator { get; set; } = new();

    /// <summary>"Work here" ([Idle here, IdleApi.WorkHere): how far around the character to look for a target (max 24).</summary>
    public int WorkHereRadius { get; set; } = 12;

    public IdleTravelSettings Travel { get; set; } = new();
}

/// <summary>Going to a spot on foot ([Idle route, and the walker).</summary>
public class IdleTravelSettings
{
    /// <summary>
    /// Extra tiles on foot worth walking for a safer spot: one inside guard zones, without hostile spawners nearby, or
    /// reached through fewer unguarded tiles. 0 = always the nearest on foot.
    /// </summary>
    public int SafeDetourTiles { get; set; } = 40;
}

/// <summary>[IdleGenSpots: how the world scan picks spots.</summary>
public class IdleSpotGeneratorSettings
{
    /// <summary>Minimum tiles between two generated spots of the same harvest task.</summary>
    public int MinSpacing { get; set; } = 24;

    /// <summary>Minimum tiles between two generated smithy spots.</summary>
    public int SmithySpacing { get; set; } = 6;

    /// <summary>Walks the map from starting cities, moongates, and teleporters to tag spots nobody can walk to.</summary>
    public bool Reachability { get; set; } = true;

    public bool ExcludeDungeons { get; set; } = true;

    /// <summary>Region names whose spots are dropped (any map).</summary>
    public List<string> ExcludeRegions { get; set; } = ["Jail", "Green Acres"];

    /// <summary>Areas whose spots are dropped.</summary>
    public List<IdleExcludeArea> ExcludeAreas { get; set; } =
    [
        new() { Name = "Dungeons and staff areas east of x 5120", Map = "Felucca", X = 5120, Y = 0, Width = 2048, Height = 4096 }
    ];

    /// <summary>Spawners of hostile creatures within this many tiles count toward a spot's danger.</summary>
    public int HostileSpawnerRange { get; set; } = 24;

    /// <summary>Game-loop time the scan may use per tick; it pauses between ticks.</summary>
    public double BudgetMilliseconds { get; set; } = 25;
}

public class IdleExcludeArea
{
    public string Name { get; set; }
    public string Map { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool Contains(Map map, int x, int y) =>
        map?.Name.InsensitiveEquals(Map) == true && x >= X && y >= Y && x < X + Width && y < Y + Height;
}

public class IdleSkillGainSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>Scales the normal UO skill gain chance while offline (1.0 = classic).</summary>
    public double GainChanceMultiplier { get; set; } = 1.0;

    /// <summary>Allows the classic once-per-interval stat gains while offline.</summary>
    public bool StatGains { get; set; } = true;
}

/// <summary>
/// Task and location catalog, loaded from <c>Data/Idle/tasks.json</c>.
/// New skills are added here as data; <see cref="IdleTaskDefinition.Kind"/> selects the code that runs them.
/// </summary>
public class IdleTaskCatalog
{
    public List<IdleTaskDefinition> Tasks { get; set; } = new();

    /// <summary>Named spots a task can target without a click (e.g. from a web API).</summary>
    public List<IdleLocation> Locations { get; set; } = new();
}

public class IdleTaskDefinition
{
    public string Id { get; set; }
    public string Name { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Handler that runs this task, e.g. "Harvest".</summary>
    public string Kind { get; set; }

    /// <summary>Per-task speed multiplier, applied on top of <see cref="IdleSettings.OfflineRate"/>.</summary>
    public double Rate { get; set; } = 1.0;

    /// <summary>Per-task multiplier on the skill gain chance, applied on top of the global one.</summary>
    public double SkillGainMultiplier { get; set; } = 1.0;

    /// <summary>Online seconds per attempt (one swing, cast, etc.).</summary>
    public double SecondsPerAttempt { get; set; } = 2.0;

    /// <summary>Seconds lost walking to a fresh spot each time one runs dry. 0 = spots never run dry.</summary>
    public double RelocateSeconds { get; set; }

    /// <summary>Harvest kind: harvest system name ("Mining", "Lumberjacking", "Fishing").</summary>
    public string HarvestSystem { get; set; }

    /// <summary>Harvest kind: definition within the system ("OreAndStone", "Sand", "Definition").</summary>
    public string HarvestDefinition { get; set; }

    /// <summary>
    /// Harvest kind: Average = the world's vein mix (the character roams between spots as they run dry);
    /// Target = only the vein under the chosen spot.
    /// </summary>
    public IdleVeinMode VeinMode { get; set; } = IdleVeinMode.Average;

    /// <summary>
    /// Item type names the character throws away instead of keeping (e.g. old boots from fishing), so junk cannot
    /// fill the 125-item bank box. Discards are still listed in the summary.
    /// </summary>
    public List<string> DiscardItems { get; set; } = new();

    [JsonIgnore]
    public HashSet<Type> DiscardTypes { get; } = new();

    /// <summary>Craft kind: craft system name ("Blacksmithy").</summary>
    public string CraftSystem { get; set; }
}

public enum IdleVeinMode
{
    Average,
    Target
}

public class IdleLocation
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string TaskId { get; set; }

    /// <summary>Further tasks this spot supports (e.g. a smithy serves both smelting and blacksmithing).</summary>
    public List<string> TaskIds { get; set; } = new();

    public Map Map { get; set; }
    public Point3D Location { get; set; }

    public IEnumerable<string> AllTaskIds()
    {
        if (!string.IsNullOrEmpty(TaskId))
        {
            yield return TaskId;
        }

        foreach (var id in TaskIds)
        {
            if (!string.IsNullOrEmpty(id) && !id.InsensitiveEquals(TaskId))
            {
                yield return id;
            }
        }
    }
}
