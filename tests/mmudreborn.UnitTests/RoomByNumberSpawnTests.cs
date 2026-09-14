using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The spawn "by Number": when non-zero it looks up
// that monster and spawns THAT specific one, short-circuiting the
// group/index random pick (the group scan only runs with no explicit number). When by_number == the
// room's perm NPC the on-enter NPC spawn owns it (stock gates that case), so the
// lair must NOT also force it (no double-spawn). These tests pin that behaviour.
public sealed class RoomByNumberSpawnTests
{
    private static GameWorld BuildWorld()
    {
        var db = new InMemoryGameDatabase();
        // Group 6 (newbie tunnels): several mobs share GroupIndex 1; townsman sits at index 17.
        db.Monsters[5] = new Monster { Number = 5, Name = "acid slime", Group = 6, GroupIndex = 1 };
        db.Monsters[1] = new Monster { Number = 1, Name = "giant rat", Group = 6, GroupIndex = 1 };
        db.Monsters[780] = new Monster { Number = 780, Name = "townsman", Group = 6, GroupIndex = 17 };
        // Group 2 (shopkeepers): Helfgrim at index 3.
        db.Monsters[24] = new Monster { Number = 24, Name = "Helfgrim", Group = 2, GroupIndex = 3 };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void ByNumber_forces_the_specific_monster_and_skips_the_group_pick()
    {
        var world = BuildWorld();
        // Slimy Tunnel #615: group 6, index [0..5] (would randomly pick acid slime OR giant rat),
        // by_number = 5 forces acid slime specifically.
        var room = new Room { MonsterType = 6, MinIndex = 0, MaxIndex = 5, ByNumber = 5, NPC = 0 };

        var candidates = world.GetLairMonsterCandidates(room);

        Assert.Single(candidates);
        Assert.Equal(5, candidates[0].Number);
    }

    [Fact]
    public void ByNumber_equal_to_room_npc_does_not_override_so_the_npc_path_owns_it()
    {
        var world = BuildWorld();
        // by_number == NPC: the lair must behave exactly as if by_number were unset (no double-spawn) —
        // here the group/index pick (group 6, index 1) yields acid slime + giant rat, NOT Helfgrim,
        // proving the by_number override was skipped because it equals the NPC.
        var gated = new Room { MonsterType = 6, MinIndex = 1, MaxIndex = 1, ByNumber = 24, NPC = 24 };
        var unset = new Room { MonsterType = 6, MinIndex = 1, MaxIndex = 1, ByNumber = 0, NPC = 24 };

        var gatedNums = world.GetLairMonsterCandidates(gated).Select(m => m.Number).OrderBy(n => n).ToArray();
        var unsetNums = world.GetLairMonsterCandidates(unset).Select(m => m.Number).OrderBy(n => n).ToArray();

        Assert.Equal(unsetNums, gatedNums);            // identical to no-override
        Assert.DoesNotContain(24, gatedNums);          // did NOT force Helfgrim
        Assert.Equal(new[] { 1, 5 }, gatedNums);       // the group/index pick stands
    }

    [Fact]
    public void ByNumber_zero_uses_the_group_index_pick()
    {
        var world = BuildWorld();
        var room = new Room { MonsterType = 6, MinIndex = 1, MaxIndex = 1, ByNumber = 0, NPC = 0 };

        var nums = world.GetLairMonsterCandidates(room).Select(m => m.Number).OrderBy(n => n).ToArray();

        Assert.Equal(new[] { 1, 5 }, nums);
    }

    [Fact]
    public void CanRoomGenerateLairMonsters_allows_bynumber_with_empty_index_window()
    {
        // Narrow Stone Tunnel (1/2320-2323): MonsterType 6, index [0..0] (empty window), by_number 5.
        // Without ByNumber qualifying the gate these rooms stay permanently empty.
        var withByNumber = new Room { MaxRegen = 3, RoomType = 0, MonsterType = 6, MinIndex = 0, MaxIndex = 0, ByNumber = 5 };
        var withoutByNumber = new Room { MaxRegen = 3, RoomType = 0, MonsterType = 6, MinIndex = 0, MaxIndex = 0, ByNumber = 0 };

        Assert.True(GameWorld.CanRoomGenerateLairMonsters(withByNumber));
        Assert.False(GameWorld.CanRoomGenerateLairMonsters(withoutByNumber));
    }

}
