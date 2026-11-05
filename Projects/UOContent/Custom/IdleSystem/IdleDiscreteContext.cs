using System;
using System.Collections.Generic;
using Server.Items;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// What a <see cref="IdleDiscreteTaskRun"/> works with: the time budget, the real skill-gain roll for each skill
/// check, and delivery of finished items under the same weight, reserve, and bank rules as gathered items.
/// </summary>
public sealed class IdleDiscreteContext
{
    private readonly bool _canGain;
    private readonly double _gainMultiplier;

    // A preview places nothing, so room is tracked as if the items had been placed.
    private int _previewPackWeight;
    private int _previewPackItems;
    private int _previewBankItems;

    internal IdleDiscreteContext(
        PlayerMobile pm, IdleTaskDefinition def, IdleSettleMode mode, IdleReport report, List<string> notes,
        double seconds, bool canGain, double gainMultiplier
    )
    {
        Player = pm;
        Definition = def;
        Mode = mode;
        Report = report;
        Notes = notes;
        SecondsLeft = seconds;
        _canGain = canGain;
        _gainMultiplier = gainMultiplier;
    }

    public PlayerMobile Player { get; }
    public IdleTaskDefinition Definition { get; }
    public IdleSettleMode Mode { get; }
    public bool Preview => Mode == IdleSettleMode.Preview;

    /// <summary>Live work: the player sees each result as it happens.</summary>
    public bool Online => Mode == IdleSettleMode.Online;
    public IdleReport Report { get; }

    /// <summary>Breakdown lines for staff simulations; null on real logins.</summary>
    public List<string> Notes { get; }

    /// <summary>Online-equivalent seconds of work left.</summary>
    public double SecondsLeft { get; private set; }

    public int Attempts { get; set; }
    public int Successes { get; set; }

    internal double StatRolls { get; private set; }
    internal int Gains { get; private set; }

    /// <summary>Takes the time for one attempt, or returns false if not enough is left.</summary>
    public bool TrySpend(double seconds)
    {
        if (seconds <= 0 || SecondsLeft + 1e-9 < seconds)
        {
            return false;
        }

        SecondsLeft -= seconds;
        return true;
    }

    /// <summary>
    /// One online CheckSkill(skill, min, max) call: the same gain chance, applied through the real gain routine.
    /// Checks that are too hard or trivial give nothing, as online.
    /// </summary>
    public void CheckSkill(SkillName name, double minSkill, double maxSkill)
    {
        var skill = Player.Skills[name];
        if (skill == null || maxSkill <= minSkill)
        {
            return;
        }

        var chance = (skill.Value - minSkill) / (maxSkill - minSkill);
        if (chance < 0.0 || chance >= 1.0)
        {
            return;
        }

        StatRolls += chance;

        if (_canGain && Utility.RandomDouble() < IdleSettlement.GainChance(Player, skill, chance) * _gainMultiplier &&
            IdleSettlement.TryGain(Player, skill))
        {
            Gains++;
        }
    }

    /// <summary>Whether one more item like <paramref name="sample"/> would fit in the backpack or the bank.</summary>
    public bool HasRoomFor(Item sample) => FitsPack(sample) || FitsBank(sample);

    /// <summary>
    /// Places a finished item, backpack first (within the pack limit), then the bank box, and counts it on
    /// <paramref name="line"/>. A preview counts it and deletes it. Returns false (item deleted) if there was no room.
    /// </summary>
    public bool Deliver(Item item, IdleItemLine line)
    {
        var placed = 0;

        if (FitsPack(item))
        {
            placed = 1;
        }
        else if (FitsBank(item))
        {
            placed = 2;
        }

        if (Preview)
        {
            if (placed == 1)
            {
                _previewPackWeight += item.PileWeight + item.TotalWeight;
                _previewPackItems += 1 + item.TotalItems;
            }
            else if (placed == 2)
            {
                _previewBankItems += 1 + item.TotalItems;
            }
        }
        else if (placed == 1 && !Player.Backpack.TryDropItem(Player, item, false))
        {
            placed = 0;
        }
        else if (placed == 2 && !Player.BankBox.TryDropItem(Player, item, false))
        {
            placed = 0;
        }

        if (placed == 0 || Preview)
        {
            item.Delete();
        }
        else
        {
            Report.PlacedItems.Add(item);
        }

        if (placed == 1)
        {
            line.ToPack++;
        }
        else if (placed == 2)
        {
            line.ToBank++;
        }

        return placed != 0;
    }

    /// <summary>Tells the working player about a notable event (exceptional item, broken tool). Live work only.</summary>
    public void Notify(string message)
    {
        if (Online && Player.NetState != null)
        {
            Player.SendMessage(0x59, message);
        }
    }

    /// <summary>Adds a delivered stack to the summary, merging it into an earlier line of the same item.</summary>
    public void MergeLine(IdleItemLine line)
    {
        if (line == null)
        {
            return;
        }

        foreach (var existing in Report.Items)
        {
            if (existing.Type == line.Type)
            {
                existing.Amount += line.Amount;
                existing.ToPack += line.ToPack;
                existing.ToBank += line.ToBank;
                return;
            }
        }

        Report.Items.Add(line);
    }

    /// <summary>The summary line for an item type, created on first use.</summary>
    public IdleItemLine GetLine(Item sample)
    {
        var type = sample.GetType();
        foreach (var line in Report.Items)
        {
            if (line.Type == type)
            {
                return line;
            }
        }

        var created = new IdleItemLine
        {
            Type = type,
            UnitWeight = sample.Weight,
            ItemId = sample.ItemID,
            Hue = sample.Hue,
            Name = IdleSettlement.GetName(sample)
        };

        Report.Items.Add(created);
        return created;
    }

    /// <summary>Counts materials (or ore) used up, for the summary.</summary>
    public void AddUsed(string name, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        foreach (var line in Report.Used)
        {
            if (line.Name == name)
            {
                line.Amount += amount;
                return;
            }
        }

        Report.Used.Add(new IdleUsedLine { Name = name, Amount = amount });
    }

    private bool FitsPack(Item item)
    {
        var pack = Player.Backpack;
        if (pack == null)
        {
            return false;
        }

        if (!Preview)
        {
            return IdleSettlement.Fits(pack, Player, item, Report.PackLimit);
        }

        return IdleSettlement.CarriedWeight(Player) + _previewPackWeight + item.PileWeight + item.TotalWeight <= Report.PackLimit &&
               pack.TotalItems + _previewPackItems + 1 + item.TotalItems <= pack.MaxItems;
    }

    private bool FitsBank(Item item)
    {
        var bank = Player.BankBox;
        if (bank == null)
        {
            return false;
        }

        return Preview
            ? bank.TotalItems + _previewBankItems + 1 + item.TotalItems <= bank.MaxItems
            : IdleSettlement.Fits(bank, Player, item, Report.PackLimit);
    }
}
