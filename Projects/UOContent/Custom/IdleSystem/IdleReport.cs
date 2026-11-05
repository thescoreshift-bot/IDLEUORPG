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

    /// <summary>Carried weight as the status bar shows it (body weight included), before and after delivery.</summary>
    public int WeightBefore { get; set; }
    public int WeightAfter { get; set; }
    public int WeightLimit { get; set; }

    /// <summary>Configured free-weight reserve, and the weight idle delivery fills the pack up to.</summary>
    public int KeepFreeStones { get; set; }
    public int PackLimit { get; set; }

    /// <summary>Stacks the weight safety pass moved from the backpack to the bank.</summary>
    public List<IdleMoveLine> MovedToBank { get; } = new();

    /// <summary>Materials, ore, and tool uses spent (crafting-style tasks).</summary>
    public List<IdleUsedLine> Used { get; } = new();

    /// <summary>Order progress, e.g. "7 of 10 made, 3 left". Null for tasks without an order.</summary>
    public string OrderStatus { get; set; }

    /// <summary>Non-stackable items this settlement placed, so the safety pass never moves the player's own copies.</summary>
    public HashSet<Item> PlacedItems { get; } = new();

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
    public Type Type { get; init; }
    public double UnitWeight { get; init; }
    public int ItemId { get; init; }
    public int Hue { get; init; }
    public string Name { get; init; }
    public int Amount { get; set; }

    /// <summary>How many of <see cref="Amount"/> came out exceptional (crafted items).</summary>
    public int Exceptional { get; set; }
    public int ToPack { get; set; }
    public int ToBank { get; set; }

    /// <summary>Thrown away on purpose (task's DiscardItems), so it is neither delivered nor counted as lost.</summary>
    public bool Discarded { get; set; }

    public int Lost => Discarded ? 0 : Amount - ToPack - ToBank;
}

public record IdleSkillLine(string Name, double From, double To);

public record IdleStatLine(string Name, int From, int To);

public record IdleMoveLine(string Name, int Amount, int Weight);

public class IdleUsedLine
{
    public string Name { get; init; }
    public int Amount { get; set; }
}
