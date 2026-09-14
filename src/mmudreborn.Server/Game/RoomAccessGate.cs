using mmudreborn.Server;

namespace mmudreborn.Game;

/// <summary>
/// The outcome of the room-entry access-control gate (the player-move
/// player-move path), split out as a pure decision so it can be unit-tested. The caller maps each
/// blocked verdict to its stock message and (on <see cref="Allowed"/>) honours
/// <c>ClearAutocombatOnLeave</c>.
/// </summary>
public enum RoomEntryVerdict
{
    Allowed,
    QuestLevelCap,          // "You have progressed too far for this room."
    ProtectedInCombat,      // "You may not enter that room while in combat."
    ProtectedInRetaliation, // "You may not enter that room during a retaliation time-period."
    ArenaUnhealthy,         // "You are not healthy enough to enter that room."
    ArenaInRetaliation,     // "You may not enter that room during a retaliation time-period."
}

public static class RoomAccessGate
{
    /// <summary>
    /// Faithful port of the room-entry gate order:
    /// quest-room (type 2) level cap → protected-room (Attributes bit0) autocombat/retaliation block →
    /// arena-leave autocombat clear (side effect, still allowed) → arena-enter (type 5) half-HP /
    /// retaliation block. The stock spawn side-effects (auto-NPC, gang fill type 3) are not
    /// modelled here — they belong to the spawn system.
    /// </summary>
    /// <param name="clearAutocombatOnLeave">Set true when the move leaves an arena (source type 5) for a
    /// non-arena room — the caller drops the mover's autocombat. Only meaningful when
    /// the result is <see cref="RoomEntryVerdict.Allowed"/>.</param>
    public static RoomEntryVerdict Evaluate(
        int destRoomType, int destMaxIndex, bool destProtected,
        int sourceRoomType, int playerLevel, int currentHp, int maxHp,
        bool inAutocombat, bool inRetaliation, out bool clearAutocombatOnLeave)
    {
        clearAutocombatOnLeave = false;

        // Quest room (type 2): blocked when the room's level cap (MaxIndex) is below the player.
        if (destRoomType == GameWorld.QuestRoomType && destMaxIndex < playerLevel)
            return RoomEntryVerdict.QuestLevelCap;

        // Protected/safe room (Attributes bit0): can't walk in mid-fight or during a retaliation window.
        // Evaluated BEFORE the arena-leave clear, so it sees the pre-clear autocombat state (stock order).
        if (destProtected)
        {
            if (inAutocombat)
                return RoomEntryVerdict.ProtectedInCombat;
            if (inRetaliation)
                return RoomEntryVerdict.ProtectedInRetaliation;
        }

        // Leaving an arena (source type 5) for a non-arena room: drop autocombat. Move still proceeds.
        if (sourceRoomType == GameWorld.ArenaRoomType && destRoomType != GameWorld.ArenaRoomType)
            clearAutocombatOnLeave = true;

        // Entering an arena (dest type 5) from a non-arena room: need >= half max HP, and not retaliating.
        if (destRoomType == GameWorld.ArenaRoomType && sourceRoomType != GameWorld.ArenaRoomType)
        {
            if (currentHp < maxHp / 2)   // current HP below half of max
                return RoomEntryVerdict.ArenaUnhealthy;
            if (inRetaliation)
                return RoomEntryVerdict.ArenaInRetaliation;
        }

        return RoomEntryVerdict.Allowed;
    }
}
