using System;
using System.Collections.Generic;

namespace Server.Custom.Idle;

/// <summary>What one settlement produced; feeds the "while you were away" summary.</summary>
public class IdleReport
{
    public string TaskName { get; init; }
    public IdleSettleMode Mode { get; init; }
    public bool Simulated => Mode != IdleSettleMode.Login;
    public TimeSpan Away { get; init; }
    public TimeSpan Counted { get; init; }
    public bool Capped => Away > Counted;

    public int Attempts { get; set; }
    public int Successes { get; set; }

    /// <summary>Why work stopped before the counted time ran out, if it did.</summary>
    public string StopReason { get; set; }

    public List<IdleItemLine> Items { get; } = new();
    public List<IdleSkillLine> Skills { get; } = new();
    public List<IdleStatLine> Stats { get; } = new();

    /// <summary>Which rules applied and the exact numbers, for staff simulations. Empty on real logins.</summary>
    public List<string> Notes { get; } = new();
}

public enum IdleSettleMode
{
    /// <summary>A real login after offline time.</summary>
    Login,

    /// <summary>Staff simulation that applies results.</summary>
    Simulate,

    /// <summary>Staff simulation that only reports; skills, stats, tools, and items are left untouched.</summary>
    Preview
}

public class IdleItemLine
{
    public int ItemId { get; init; }
    public int Hue { get; init; }
    public string Name { get; init; }
    public int Amount { get; init; }
    public int ToPack { get; set; }
    public int ToBank { get; set; }
    public int Lost => Amount - ToPack - ToBank;
}

public record IdleSkillLine(string Name, double From, double To);

public record IdleStatLine(string Name, int From, int To);
