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

    /// <summary>Time away beyond this is not counted.</summary>
    public TimeSpan MaxOfflineTime { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Absences shorter than this are ignored (short reconnects, crashes).</summary>
    public TimeSpan MinOfflineTime { get; set; } = TimeSpan.FromMinutes(1);

    public bool ShowSummaryGump { get; set; } = true;

    /// <summary>Stones of carrying capacity idle deliveries leave free; the rest goes to the bank.</summary>
    public int KeepFreeStones { get; set; } = 20;

    public IdleSkillGainSettings SkillGain { get; set; } = new();
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
    public Map Map { get; set; }
    public Point3D Location { get; set; }
}
