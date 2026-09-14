using mmudreborn.Data.Models;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Regression for the "Greater Hellion vanishes mid-fight" bug: a death-summoned monster (Champion of
/// Blood → Greater Hellion) is placed into a room whose Group/MonsterType it does not match. The 30s
/// invalid-room sweep must NOT remove such a force-placed monster, or it disappears from the room (and
/// `look`) while the player keeps fighting the combat engine's stale reference to it.
/// </summary>
public sealed class MonsterInvalidRoomCleanupTests
{
    private const int Map = 1;
    private const int RoomNum = 10;
    private const int RoomGroup = 7;       // the room only "naturally" holds Group 7 monsters
    private const int HellionId = 9001;
    private const int HellionGroup = 99;   // a summoned reward monster, NOT in the room's group

    private static GameWorld BuildWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(Map, RoomNum)] = new Room
        {
            MapNumber = Map,
            RoomNumber = RoomNum,
            MonsterType = RoomGroup,
        };
        db.Monsters[HellionId] = new Monster
        {
            Number = HellionId,
            Name = "greater hellion",
            HP = 500,
            Group = HellionGroup,
        };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void Force_placed_monster_in_incompatible_room_survives_the_invalid_room_sweep()
    {
        var world = BuildWorld();

        Assert.True(world.TrySpawnMonsterInRoom(Map, RoomNum, HellionId, ignoreRoomRestrictions: true, out var hellion, out _));
        Assert.NotNull(hellion);
        Assert.True(hellion!.WasForcePlaced);

        world.RunInvalidRoomCleanupForTests();

        var here = world.GetMonstersInRoom(Map, RoomNum);
        Assert.Contains(hellion, here);
    }

    [Fact]
    public void Engaged_monster_in_incompatible_room_survives_even_without_the_force_placed_flag()
    {
        var world = BuildWorld();

        Assert.True(world.TrySpawnMonsterInRoom(Map, RoomNum, HellionId, ignoreRoomRestrictions: true, out var hellion, out _));
        // Simulate a non-force-placed monster (e.g. a group mismatch arising another way) that a player
        // is actively fighting: the active-fight guard alone must keep it in the room.
        hellion!.WasForcePlaced = false;
        hellion.MarkPlayerEngaged("Tank");

        world.RunInvalidRoomCleanupForTests();

        Assert.Contains(hellion, world.GetMonstersInRoom(Map, RoomNum));
    }

    [Fact]
    public void Unbound_idle_monster_in_incompatible_room_is_still_swept()
    {
        var world = BuildWorld();

        Assert.True(world.TrySpawnMonsterInRoom(Map, RoomNum, HellionId, ignoreRoomRestrictions: true, out var stray, out _));
        // Strip the exemptions so this looks like a stray wanderer that ended up where it doesn't belong:
        // the sweep must still clear it (the fix narrows the sweep, it does not disable it).
        stray!.WasForcePlaced = false;

        world.RunInvalidRoomCleanupForTests();

        Assert.DoesNotContain(stray, world.GetMonstersInRoom(Map, RoomNum));
    }
}
