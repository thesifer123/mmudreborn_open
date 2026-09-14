using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Ambient-wander plus group-formation (anchor + drag) parity.
// Type: 0=Solo, 1=Leader, 2=Follower, 3=Stationary. Ambient-wander eligibility keys on the GROUP field
// (0/2 never wander); Stationary never relocates; a Follower/subordinate-Leader anchors to a same-group
// (GroupMatch) leader in its room and is dragged when that leader moves.
public sealed class MonsterGroupFormationTests
{
    private const int Group = 7;   // a normal (non-0/2/5) wandering group; rooms below accept it

    private static GameWorld BuildWorld()
    {
        var db = new InMemoryGameDatabase();
        var roomA = new Room { MapNumber = 1, RoomNumber = 10, MonsterType = Group };
        roomA.SetExit(new RoomExitDefinition
        {
            MapNumber = 1, RoomNumber = 10, Direction = "north",
            TargetMap = 1, TargetRoom = 11, ExitType = RoomExitType.Normal,
        });
        db.Rooms[(1, 10)] = roomA;
        db.Rooms[(1, 11)] = new Room { MapNumber = 1, RoomNumber = 11, MonsterType = Group }; // dead-end
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static void Register(GameWorld world, int id, string name, int type, int group,
        int groupMatch = 0, int maxFollowers = 0, double expMulti = 1, int follow = 0)
    {
        ((InMemoryGameDatabase)world.Database).Monsters[id] = new Monster
        {
            Number = id, Name = name, HP = 100, Type = type, Group = group,
            GroupMatch = groupMatch, MaxFollowers = maxFollowers, ExpMulti = expMulti, FollowPercent = follow,
        };
    }

    private static MonsterInstance Spawn(GameWorld world, int id)
    {
        Assert.True(world.TrySpawnMonsterInRoom(1, 10, id, ignoreRoomRestrictions: true, out var inst, out _));
        return inst!;
    }

    // ---- Anchor logic (deterministic) ----

    [Fact]
    public void Follower_anchors_to_a_same_group_leader_in_the_room()
    {
        var world = BuildWorld();
        Register(world, 1, "leader", type: 1, group: Group, groupMatch: 5, maxFollowers: 5, expMulti: 10);
        Register(world, 2, "follower", type: 2, group: Group, groupMatch: 5);
        var leader = Spawn(world, 1);
        var follower = Spawn(world, 2);

        Assert.True(world.IsAnchoredToGroupLeaderForTests(follower));
        Assert.False(world.IsAnchoredToGroupLeaderForTests(leader));   // no stronger leader present
    }

    [Fact]
    public void Follower_of_a_different_group_is_not_anchored()
    {
        var world = BuildWorld();
        Register(world, 1, "leader", type: 1, group: Group, groupMatch: 5, maxFollowers: 5, expMulti: 10);
        Register(world, 2, "outsider", type: 2, group: Group, groupMatch: 6);   // different GroupMatch
        Spawn(world, 1);
        var outsider = Spawn(world, 2);

        Assert.False(world.IsAnchoredToGroupLeaderForTests(outsider));
    }

    [Fact]
    public void Weaker_leader_anchors_to_a_stronger_same_group_leader()
    {
        var world = BuildWorld();
        Register(world, 1, "strong", type: 1, group: Group, groupMatch: 5, maxFollowers: 5, expMulti: 20);
        Register(world, 2, "weak", type: 1, group: Group, groupMatch: 5, maxFollowers: 5, expMulti: 10);
        var strong = Spawn(world, 1);
        var weak = Spawn(world, 2);

        Assert.True(world.IsAnchoredToGroupLeaderForTests(weak));
        Assert.False(world.IsAnchoredToGroupLeaderForTests(strong));
    }

    // ---- Wander eligibility (probabilistic; FollowPercent 0 => ~50% chance, single mob within budget) ----

    [Fact]
    public void Solo_monster_wanders()
    {
        var world = BuildWorld();
        Register(world, 1, "solo", type: 0, group: Group);   // Solo, low aggression = high wander
        var solo = Spawn(world, 1);

        bool moved = false;
        for (int i = 0; i < 80 && !moved; i++)
        {
            world.RunMonsterWanderForTests();
            moved = solo.RoomNumber == 11;
        }
        Assert.True(moved, "a Solo monster should be able to ambient-wander");
    }

    [Fact]
    public void Stationary_monster_never_moves()
    {
        var world = BuildWorld();
        Register(world, 1, "statue", type: 3, group: Group);   // Stationary
        var statue = Spawn(world, 1);

        for (int i = 0; i < 80; i++)
            world.RunMonsterWanderForTests();

        Assert.Equal(10, statue.RoomNumber);
    }

    [Fact]
    public void Passive_group_zero_never_wanders()
    {
        var world = BuildWorld();
        Register(world, 1, "townsfolk", type: 0, group: 0);   // Group 0 = never wanders
        var mob = Spawn(world, 1);

        for (int i = 0; i < 80; i++)
            world.RunMonsterWanderForTests();

        Assert.Equal(10, mob.RoomNumber);
    }

    // ---- Drag ----

    [Fact]
    public void Leader_drags_its_follower_when_it_wanders()
    {
        var world = BuildWorld();
        Register(world, 1, "leader", type: 1, group: Group, groupMatch: 5, maxFollowers: 5, expMulti: 10);
        Register(world, 2, "follower", type: 2, group: Group, groupMatch: 5);
        var leader = Spawn(world, 1);
        var follower = Spawn(world, 2);

        bool leaderMoved = false;
        for (int i = 0; i < 80 && !leaderMoved; i++)
        {
            world.RunMonsterWanderForTests();
            leaderMoved = leader.RoomNumber == 11;
        }

        Assert.True(leaderMoved, "the leader should wander");
        // The follower never wanders on its own (anchored), but is dragged along with the leader.
        Assert.Equal(leader.RoomNumber, follower.RoomNumber);
    }
}
