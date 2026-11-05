using ModernUO.Serialization;

namespace Server.Custom.Idle;

/// <summary>
/// Stand-in walker the spot generator passes to the server's movement check. Never added to the world or saved; the
/// serializer exists only because every <see cref="Mobile"/> type must have one. Reported as not alive so closed
/// doors do not block the walk, as a player would open them.
/// </summary>
[SerializationGenerator(0)]
internal sealed partial class IdleReachProbe : Mobile
{
    public IdleReachProbe() : base(Serial.MinusOne)
    {
    }

    public override bool Alive => false;
}
