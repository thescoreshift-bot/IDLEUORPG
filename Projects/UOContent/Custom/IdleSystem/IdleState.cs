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
    NoSupplies
}

/// <summary>
/// One character's idle task. Supplies are not copied here: they stay in the backpack, which the world
/// save already persists, and are counted when the offline time is settled.
/// </summary>
public class IdleState
{
    private const int Version = 0;

    public PlayerMobile Player { get; }
    public string TaskId { get; set; }
    public IdleTarget Target { get; set; }
    public DateTime StartedAt { get; set; }

    /// <summary>When the character left the world. Only meaningful while <see cref="LogoutResult"/> is not None.</summary>
    public DateTime LogoutTime { get; set; }

    public IdleLogoutResult LogoutResult { get; set; }

    public IdleState(PlayerMobile player) => Player = player;

    public void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.Write(TaskId);
        Target.Serialize(writer);
        writer.Write(StartedAt);
        writer.Write(LogoutTime);
        writer.Write((byte)LogoutResult);
    }

    public void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version
        TaskId = reader.ReadString();
        Target = IdleTarget.Deserialize(reader);
        StartedAt = reader.ReadDateTime();
        LogoutTime = reader.ReadDateTime();
        LogoutResult = (IdleLogoutResult)reader.ReadByte();
    }
}
