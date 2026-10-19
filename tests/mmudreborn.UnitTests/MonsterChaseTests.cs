using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Stock chases on two occasions. When a player walks out, every monster in that room locked onto them
// rolls its FollowPercent and follows through the same exit. One that didn't make it keeps trying on its
// fast updates: if the player is elsewhere on the map, visible to it and not mid-step, it rolls again and
// moves one room along the player's own trail; anything else is a miss, and 16 misses end the lock.
public sealed class MonsterChaseTests
{
    private const int Group = 7;
    private const int HunterId = 500;
    private const string PlayerName = "Runner";

    [Fact]
    public void A_locked_monster_left_behind_catches_up_along_the_players_trail_one_room_per_fast_update()
    {
        var (world, player) = BuildCorridor();
        var hunter = SpawnHunter(world, room: 1, lockedOn: true);
        WalkTo(player, 2);
        WalkTo(player, 3);
        player.MovedThisPulse = false;

        world.AdvanceFastTicksForTests(1);
        Assert.Equal(2, hunter.RoomNumber);

        world.AdvanceFastTicksForTests(1);
        Assert.Equal(3, hunter.RoomNumber);

        // In the player's room now: further fast updates neither move it nor count misses.
        world.AdvanceFastTicksForTests(20);
        Assert.Equal(3, hunter.RoomNumber);
        Assert.True(hunter.IsLockedOnTarget(PlayerName));
    }

    [Fact]
    public void A_player_who_has_only_just_stepped_is_not_chased_on_that_update()
    {
        var (world, player) = BuildCorridor();
        var hunter = SpawnHunter(world, room: 1, lockedOn: true);
        WalkTo(player, 2);   // MovedThisPulse stays set

        world.AdvanceFastTicksForTests(1);

        Assert.Equal(1, hunter.RoomNumber);
        Assert.Equal(1, hunter.FollowAbandonTicks);   // a miss
    }

    [Fact]
    public void A_hidden_player_is_not_chased_by_a_monster_that_cannot_see_hidden()
    {
        var (world, player) = BuildCorridor();
        var hunter = SpawnHunter(world, room: 1, lockedOn: true);
        WalkTo(player, 2);
        player.MovedThisPulse = false;
        player.IsHidden = true;

        world.AdvanceFastTicksForTests(1);

        Assert.Equal(1, hunter.RoomNumber);
        Assert.Equal(1, hunter.FollowAbandonTicks);
    }

    [Fact]
    public void A_player_whose_trail_never_passed_here_cannot_be_chased()
    {
        var (world, player) = BuildCorridor();
        var hunter = SpawnHunter(world, room: 2, lockedOn: true);
        WalkTo(player, 3);   // straight from 1 to 3 as far as the trail knows (a teleport, say)
        player.MovedThisPulse = false;

        world.AdvanceFastTicksForTests(1);

        Assert.Equal(2, hunter.RoomNumber);
        Assert.Equal(1, hunter.FollowAbandonTicks);
    }

    [Fact]
    public void The_immediate_follow_takes_monsters_locked_onto_the_player_and_not_unlocked_ones()
    {
        var (world, player) = BuildCorridor();
        var locked = SpawnHunter(world, room: 1, lockedOn: true);
        var unlocked = SpawnHunter(world, room: 1, lockedOn: false);
        var fromRoom = world.GetRoom(1, 1)!;
        var exit = fromRoom.GetExitDefinitions().Values.Single(e => e.Direction == "north");
        WalkTo(player, 2);

        var followed = world.ResolveMonsterPursuit(player, fromRoom, exit);

        Assert.Equal([locked], followed);
        Assert.Equal(2, locked.RoomNumber);
        Assert.Equal(1, unlocked.RoomNumber);
    }

    // Rooms 1 - 2 - 3 in a line (north / south), all open to the hunter's group, and the player in room 1.
    private static (GameWorld World, Player Player) BuildCorridor()
    {
        var db = new InMemoryGameDatabase();
        db.Monsters[HunterId] = new Monster { Number = HunterId, Name = "hunter", HP = 100, Type = 0, Group = Group, FollowPercent = 100 };
        for (int room = 1; room <= 3; room++)
        {
            var r = new Room { MapNumber = 1, RoomNumber = room, MonsterType = Group };
            if (room < 3)
                r.SetExit(new RoomExitDefinition { MapNumber = 1, RoomNumber = room, Direction = "north", TargetMap = 1, TargetRoom = room + 1, ExitType = RoomExitType.Normal });
            if (room > 1)
                r.SetExit(new RoomExitDefinition { MapNumber = 1, RoomNumber = room, Direction = "south", TargetMap = 1, TargetRoom = room - 1, ExitType = RoomExitType.Normal });
            db.Rooms[(1, room)] = r;
        }

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var player = new Player { Name = PlayerName, CurrentMapNumber = 1, CurrentRoomNumber = 1 };
        world.AddPlayer(player);
        return (world, player);
    }

    private static MonsterInstance SpawnHunter(GameWorld world, int room, bool lockedOn)
    {
        Assert.True(world.TrySpawnMonsterInRoom(1, room, HunterId, ignoreRoomRestrictions: true, out var hunter, out _));
        if (lockedOn)
            hunter!.SetLockedTarget(PlayerName);
        return hunter!;
    }

    private static void WalkTo(Player player, int room)
    {
        player.CurrentRoomNumber = room;
        player.RecordMove(1, room);
    }
}
