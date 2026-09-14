using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The room-entry gate (player-move path). RoomType: 2 = quest/level-capped,
// 5 = arena; Attributes bit0 = protected. MaxIndex is the quest level cap.
public sealed class RoomAccessGateTests
{
    private const int Quest = GameWorld.QuestRoomType;   // 2
    private const int Arena = GameWorld.ArenaRoomType;   // 5
    private const int Plain = 0;

    private static RoomEntryVerdict Eval(
        int destType = Plain, int destMaxIndex = 0, bool destProtected = false,
        int sourceType = Plain, int level = 10, int hp = 100, int maxHp = 100,
        bool inAutocombat = false, bool inRetaliation = false)
        => RoomAccessGate.Evaluate(destType, destMaxIndex, destProtected, sourceType,
            level, hp, maxHp, inAutocombat, inRetaliation, out _);

    [Fact]
    public void Plain_room_allows_entry()
        => Assert.Equal(RoomEntryVerdict.Allowed, Eval());

    // --- Quest level cap (type 2): MaxIndex < Level blocks ---
    [Theory]
    [InlineData(3, 10, RoomEntryVerdict.QuestLevelCap)]  // cap 3 < level 10 → blocked
    [InlineData(3, 4, RoomEntryVerdict.QuestLevelCap)]   // cap 3 < level 4 → blocked
    [InlineData(3, 3, RoomEntryVerdict.Allowed)]         // cap 3 == level 3 → allowed (cap is inclusive)
    [InlineData(10, 3, RoomEntryVerdict.Allowed)]        // cap 10 > level 3 → allowed
    public void Quest_room_blocks_when_level_exceeds_cap(int maxIndex, int level, RoomEntryVerdict expected)
        => Assert.Equal(expected, Eval(destType: Quest, destMaxIndex: maxIndex, level: level));

    [Fact]
    public void Quest_cap_does_not_apply_to_non_quest_rooms()
        => Assert.Equal(RoomEntryVerdict.Allowed, Eval(destType: Plain, destMaxIndex: 1, level: 99));

    // --- Protected room: blocked while in autocombat / retaliation ---
    [Fact]
    public void Protected_room_blocks_while_in_combat()
        => Assert.Equal(RoomEntryVerdict.ProtectedInCombat, Eval(destProtected: true, inAutocombat: true));

    [Fact]
    public void Protected_room_blocks_during_retaliation()
        => Assert.Equal(RoomEntryVerdict.ProtectedInRetaliation, Eval(destProtected: true, inRetaliation: true));

    [Fact]
    public void Protected_room_combat_check_precedes_retaliation()
        => Assert.Equal(RoomEntryVerdict.ProtectedInCombat,
            Eval(destProtected: true, inAutocombat: true, inRetaliation: true));

    [Fact]
    public void Protected_room_allows_when_not_fighting()
        => Assert.Equal(RoomEntryVerdict.Allowed, Eval(destProtected: true));

    [Fact]
    public void Non_protected_room_ignores_combat_state()
        => Assert.Equal(RoomEntryVerdict.Allowed, Eval(destProtected: false, inAutocombat: true, inRetaliation: true));

    // --- Arena enter (dest type 5 from non-arena): half-HP + retaliation gates ---
    [Theory]
    [InlineData(49, 100, RoomEntryVerdict.ArenaUnhealthy)]  // below half → blocked
    [InlineData(50, 100, RoomEntryVerdict.Allowed)]         // exactly half → allowed
    [InlineData(51, 100, RoomEntryVerdict.Allowed)]
    [InlineData(3, 7, RoomEntryVerdict.Allowed)]            // 3 >= 7/2 (=3, floor) → allowed
    [InlineData(2, 7, RoomEntryVerdict.ArenaUnhealthy)]     // 2 < 3 → blocked
    public void Arena_enter_requires_half_max_hp(int hp, int maxHp, RoomEntryVerdict expected)
        => Assert.Equal(expected, Eval(destType: Arena, sourceType: Plain, hp: hp, maxHp: maxHp));

    [Fact]
    public void Arena_enter_blocks_during_retaliation_when_healthy()
        => Assert.Equal(RoomEntryVerdict.ArenaInRetaliation,
            Eval(destType: Arena, sourceType: Plain, hp: 100, maxHp: 100, inRetaliation: true));

    [Fact]
    public void Arena_enter_unhealthy_check_precedes_retaliation()
        => Assert.Equal(RoomEntryVerdict.ArenaUnhealthy,
            Eval(destType: Arena, sourceType: Plain, hp: 10, maxHp: 100, inRetaliation: true));

    [Fact]
    public void Moving_between_two_arena_rooms_skips_the_enter_gate()
        => Assert.Equal(RoomEntryVerdict.Allowed,
            Eval(destType: Arena, sourceType: Arena, hp: 1, maxHp: 100));

    // --- Arena leave (source type 5 → non-arena): clears autocombat, still allowed ---
    [Fact]
    public void Leaving_arena_clears_autocombat_and_allows()
    {
        var verdict = RoomAccessGate.Evaluate(
            destRoomType: Plain, destMaxIndex: 0, destProtected: false,
            sourceRoomType: Arena, playerLevel: 10, currentHp: 1, maxHp: 100,
            inAutocombat: true, inRetaliation: false, out bool clearOnLeave);

        Assert.Equal(RoomEntryVerdict.Allowed, verdict);
        Assert.True(clearOnLeave);
    }

    [Fact]
    public void Non_arena_move_does_not_signal_autocombat_clear()
    {
        RoomAccessGate.Evaluate(Plain, 0, false, Plain, 10, 100, 100, false, false, out bool clearOnLeave);
        Assert.False(clearOnLeave);
    }

    // Leaving an arena INTO a protected room while fighting is still blocked (protected check runs
    // first, before the leave-clear) — matches the stock gate order.
    [Fact]
    public void Leaving_arena_into_protected_room_while_fighting_is_blocked()
    {
        var verdict = RoomAccessGate.Evaluate(
            destRoomType: Plain, destMaxIndex: 0, destProtected: true,
            sourceRoomType: Arena, playerLevel: 10, currentHp: 100, maxHp: 100,
            inAutocombat: true, inRetaliation: false, out bool clearOnLeave);

        Assert.Equal(RoomEntryVerdict.ProtectedInCombat, verdict);
        Assert.False(clearOnLeave);
    }
}
