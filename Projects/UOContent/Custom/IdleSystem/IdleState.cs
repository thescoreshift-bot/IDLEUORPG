using System;
using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Where a task runs, as plain data so it can be set by a click, a named location, or an external API.
/// </summary>
public readonly record struct IdleTarget(string LocationId, Map Map, Point3D Location)
{
    /// <summary>LocationId used when the target came from clicking a spot in-game.</summary>
    public const string Clicked = "clicked";

    /// <summary>LocationId used when the target is wherever the character stands (crafting at a station).</summary>
    public const string Here = "here";

    public void Serialize(IGenericWriter writer)
    {
        writer.Write(LocationId);
        writer.Write(Map);
        writer.Write(Location);
    }

    public static IdleTarget Deserialize(IGenericReader reader) =>
        new(reader.ReadString(), reader.ReadMap(), reader.ReadPoint3D());
}

public enum IdleLogoutResult : byte
{
    None,
    Eligible,
    TaskUnavailable,
    NotAtTarget,
    Dead,
    Criminal,
    InCombat,
    NoSupplies,
    NoStation
}

/// <summary>
/// What a crafting-style task should make, as plain data (an external API can fill it in). One order per character;
/// <see cref="Remaining"/> carries over between logouts until it reaches 0.
/// </summary>
public class IdleOrder
{
    /// <summary>Recipe id from the live craft table (the item's type name, e.g. "PlateChest"). Null for smelting.</summary>
    public string RecipeId { get; set; }

    /// <summary>Material type name (e.g. "IronIngot", "DullCopperIngot"). Null = the recipe's default.</summary>
    public string Material { get; set; }

    public int Requested { get; set; }
    public int Remaining { get; set; }

    public void Serialize(IGenericWriter writer)
    {
        writer.Write(RecipeId);
        writer.Write(Material);
        writer.WriteEncodedInt(Requested);
        writer.WriteEncodedInt(Remaining);
    }

    public static IdleOrder Deserialize(IGenericReader reader) =>
        new()
        {
            RecipeId = reader.ReadString(),
            Material = reader.ReadString(),
            Requested = reader.ReadEncodedInt(),
            Remaining = reader.ReadEncodedInt()
        };
}

/// <summary>
/// One character's idle task. Supplies are not copied here: they stay in the backpack, which the world
/// save already persists, and are counted when the offline time is settled.
/// </summary>
public class IdleState
{
    private const int Version = 1;

    public PlayerMobile Player { get; }
    public string TaskId { get; set; }
    public IdleTarget Target { get; set; }
    public DateTime StartedAt { get; set; }

    /// <summary>When the character left the world. Only meaningful while <see cref="LogoutResult"/> is not None.</summary>
    public DateTime LogoutTime { get; set; }

    public IdleLogoutResult LogoutResult { get; set; }

    /// <summary>Crafting-style tasks only.</summary>
    public IdleOrder Order { get; set; }

    /// <summary>Start of live work since the character last left the world; drives the full-rate window. Not saved.</summary>
    public DateTime OnlineSince { get; set; }

    /// <summary>A stop rule broke during the logout delay, so no offline work follows. Not saved.</summary>
    public IdleLogoutResult Interrupted { get; set; }

    public IdleState(PlayerMobile player) => Player = player;

    public void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.Write(TaskId);
        Target.Serialize(writer);
        writer.Write(StartedAt);
        writer.Write(LogoutTime);
        writer.Write((byte)LogoutResult);

        writer.Write(Order != null);
        Order?.Serialize(writer);
    }

    public void Deserialize(IGenericReader reader)
    {
        var version = reader.ReadEncodedInt();
        TaskId = reader.ReadString();
        Target = IdleTarget.Deserialize(reader);
        StartedAt = reader.ReadDateTime();
        LogoutTime = reader.ReadDateTime();
        LogoutResult = (IdleLogoutResult)reader.ReadByte();

        if (version >= 1 && reader.ReadBool())
        {
            Order = IdleOrder.Deserialize(reader);
        }
    }
}
