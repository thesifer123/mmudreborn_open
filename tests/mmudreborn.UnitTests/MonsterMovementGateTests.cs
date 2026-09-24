using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The single mover is behind every monster relocation — ambient wander
// (the medium pass), pursuit of a locked target (the fast pass) and a Leader's drag — so its
// destination precondition and exit-type switch bind all three. These pin both halves directly; the
// engine paths only compose them.
public class MonsterMovementGateTests
{
    private static RoomExitDefinition Exit(RoomExitType type, int para1 = 0, int para2 = 0) => new()
    {
        Direction = "north",
        TargetMap = 1,
        TargetRoom = 2,
        ExitType = type,
        Para1 = para1,
        Para2 = para2,
    };

    // The types whose case in the mover's switch simply refuses — the monster does not move.
    [Theory]
    [InlineData(RoomExitType.Spell)]         // case 1
    [InlineData(RoomExitType.Item)]          // case 3
    [InlineData(RoomExitType.Toll)]          // case 4
    [InlineData(RoomExitType.Hidden)]        // case 6
    [InlineData(RoomExitType.ChangeMap)]     // case 8
    [InlineData(RoomExitType.RemoteAction)]  // case 0xc
    public void Exit_types_stock_hard_blocks_are_impassable_to_monsters(RoomExitType type)
    {
        Assert.False(GameWorld.CanMonsterTraverseExitType(new Monster { Group = 20 }, Exit(type)));
    }

    // Bug #234: the switch never reads the Para1 reveal bitfield, so a searchable exit stays impassable
    // after a player finds it — a chase cannot leak through one behind him.
    [Theory]
    [InlineData(0)]
    [InlineData(2)]      // searchable
    [InlineData(4)]      // starts visible
    [InlineData(16)]     // remote-action reveal
    public void Hidden_exit_is_impassable_whatever_its_reveal_bits_say(int para1)
    {
        Assert.False(GameWorld.CanMonsterTraverseExitType(
            new Monster { Group = 20 }, Exit(RoomExitType.Hidden, para1)));
    }

    // Types with no case at all fall through to the move. Class/race/level/alignment/toll denials are
    // room-entry checks on the PLAYER path and are never consulted for a monster, and type
    // 22 (Cast) casts only on a user.
    [Theory]
    [InlineData(RoomExitType.Normal)]
    [InlineData(RoomExitType.Action)]
    [InlineData(RoomExitType.Trap)]
    [InlineData(RoomExitType.Text)]
    [InlineData(RoomExitType.Class)]
    [InlineData(RoomExitType.Race)]
    [InlineData(RoomExitType.Level)]
    [InlineData(RoomExitType.BlockGuard)]
    [InlineData(RoomExitType.Alignment)]
    [InlineData(RoomExitType.Cast)]
    [InlineData(RoomExitType.Ability)]
    [InlineData(RoomExitType.SpellTrap)]
    public void Exit_types_with_no_case_fall_through_to_the_move(RoomExitType type)
    {
        Assert.True(GameWorld.CanMonsterTraverseExitType(new Monster { Group = 20 }, Exit(type)));
    }

    // Type 2 tests the Key exit's lock field (Para2); types 7/11 test the Door/Gate one
    // (Para1). Opening writes 1 over 2 and the relock writes 2 back, so an unlocked barrier is
    // still non-zero — a monster never follows a player through a lock it just watched him pick. Only a
    // never-lockable exit (field 0) passes, and no live row has one.
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]   // unlocked, but still non-zero
    [InlineData(2, false)]   // locked
    public void Key_exit_passes_only_while_its_lock_field_is_zero(int lockState, bool expected)
    {
        Assert.Equal(expected, GameWorld.CanMonsterTraverseExitType(
            new Monster { Group = 20 }, Exit(RoomExitType.Key, para2: lockState)));
    }

    [Theory]
    [InlineData(RoomExitType.Door, 0, true)]
    [InlineData(RoomExitType.Door, 1, false)]
    [InlineData(RoomExitType.Door, 2, false)]
    [InlineData(RoomExitType.Gate, 0, true)]
    [InlineData(RoomExitType.Gate, 2, false)]
    public void Door_and_gate_pass_only_while_their_lock_field_is_zero(RoomExitType type, int lockState, bool expected)
    {
        Assert.Equal(expected, GameWorld.CanMonsterTraverseExitType(
            new Monster { Group = 20 }, Exit(type, para1: lockState)));
    }

    // The field the mover reads is the LIVE one: OPEN writes 0 into it, CLOSE 1, a lock or the
    // relock timer 2. Every imported door row carries 2, so a door a player has opened must pass on its
    // live 0 — not stay shut on the imported 2 (bug #240: nothing could follow you through an open door).
    [Theory]
    [InlineData(RoomExitType.Door, 0, true)]    // open right now
    [InlineData(RoomExitType.Door, 1, false)]   // closed
    [InlineData(RoomExitType.Door, 2, false)]   // locked
    [InlineData(RoomExitType.Gate, 0, true)]
    public void Door_and_gate_pass_on_their_live_lock_field_not_the_imported_one(RoomExitType type, int liveLockState, bool expected)
    {
        Assert.Equal(expected, GameWorld.CanMonsterTraverseExitType(
            new Monster { Group = 20 }, Exit(type, para1: 2), liveLockState));
    }

    // Guards (Group 5) are exempt from the door/gate lock, but NOT from the key lock; Group 38 is exempt
    // from all three.
    [Theory]
    [InlineData(5, RoomExitType.Door, true)]
    [InlineData(5, RoomExitType.Gate, true)]
    [InlineData(5, RoomExitType.Key, false)]
    [InlineData(38, RoomExitType.Door, true)]
    [InlineData(38, RoomExitType.Gate, true)]
    [InlineData(38, RoomExitType.Key, true)]
    [InlineData(20, RoomExitType.Door, false)]
    public void Guard_and_roaming_groups_are_exempt_from_the_barrier_locks(int group, RoomExitType type, bool expected)
    {
        var exit = type == RoomExitType.Key ? Exit(type, para2: 2) : Exit(type, para1: 2);
        Assert.Equal(expected, GameWorld.CanMonsterTraverseExitType(new Monster { Group = group }, exit));
    }

    // The destination precondition guarding the whole switch. Note there is NO "MonsterType > 0" guard:
    // a Group-0 monster may enter a Group-0 room. That test belongs to SPAWNING, not to the mover.
    [Theory]
    [InlineData(20, 20, true)]
    [InlineData(20, 17, false)]
    [InlineData(0, 0, true)]
    public void Destination_accepts_a_monster_of_its_own_group(int group, int roomMonsterType, bool expected)
    {
        Assert.Equal(expected, GameWorld.IsMovementDestinationCompatible(
            new Monster { Group = group },
            new Room { MonsterType = roomMonsterType },
            Exit(RoomExitType.Normal)));
    }

    [Fact]
    public void Roaming_group_38_enters_any_room()
    {
        Assert.True(GameWorld.IsMovementDestinationCompatible(
            new Monster { Group = 38 },
            new Room { MonsterType = 17, Attributes = 1, RoomType = 5 },
            Exit(RoomExitType.Normal)));
    }

    // Group 37 (summoned) goes anywhere with NO room flags set that is not an Arena (RoomType 5).
    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 0, false)]   // any flag set
    [InlineData(0, 5, false)]   // arena
    public void Summoned_group_37_enters_only_unflagged_non_arena_rooms(int attributes, int roomType, bool expected)
    {
        Assert.Equal(expected, GameWorld.IsMovementDestinationCompatible(
            new Monster { Group = 37 },
            new Room { MonsterType = 17, Attributes = attributes, RoomType = roomType },
            Exit(RoomExitType.Normal)));
    }

    // The PATROL room flag is guard pathing and nothing else: a Guard steps outside its own MonsterType
    // only into PATROL rooms, and never through a BlockGuard (type 19) exit.
    [Theory]
    [InlineData(2, RoomExitType.Normal, true)]        // PATROL
    [InlineData(0, RoomExitType.Normal, false)]       // not PATROL
    [InlineData(2, RoomExitType.BlockGuard, false)]   // PATROL, but a BlockGuard exit
    public void Guard_patrols_only_into_patrol_flagged_rooms(int attributes, RoomExitType type, bool expected)
    {
        Assert.Equal(expected, GameWorld.IsMovementDestinationCompatible(
            new Monster { Group = 5 },
            new Room { MonsterType = 17, Attributes = attributes },
            Exit(type)));
    }
}
