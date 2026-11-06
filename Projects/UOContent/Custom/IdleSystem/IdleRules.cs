using Server.Mobiles;

namespace Server.Custom.Idle;

/// <summary>
/// Character-state rules every idle activity obeys: live work, walking to work, and the preflight checklist. Each caller
/// words its own message; the rule itself lives here so the activities cannot drift apart.
/// </summary>
public static class IdleRules
{
    /// <summary>The first rule that keeps the character from working right now, or <see cref="IdleLogoutResult.None"/>.</summary>
    public static IdleLogoutResult CheckCharacter(PlayerMobile pm)
    {
        if (!pm.Alive)
        {
            return IdleLogoutResult.Dead;
        }

        if (pm.Criminal)
        {
            return IdleLogoutResult.Criminal;
        }

        if (pm.Warmode || IdleSystem.IsInCombat(pm))
        {
            return IdleLogoutResult.InCombat;
        }

        return IdleLogoutResult.None;
    }
}
