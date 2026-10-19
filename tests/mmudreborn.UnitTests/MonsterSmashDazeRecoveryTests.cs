using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A monster smashed to the floor is dazed for a count of 15. Stock counts that down in four places, not on
// a timer: once per attack round the monster takes part in (its own, or a player's against it), on each fast
// update while it is locked onto a player or owned, and on each wander attempt that reaches the mover while
// it is not locked. Only the first two announce "Slightly dazed the X rises from the floor."
public sealed class MonsterSmashDazeRecoveryTests
{
    private const int GuardGroup = 5;
    private const int GuardId = 638;

    [Fact]
    public void A_dazed_monster_locked_onto_a_player_counts_down_on_its_fast_update()
    {
        var (world, db) = BuildWorld();
        AddGuardRoom(db, 1, 101);
        var monster = Spawn(world, 1);
        monster.SetLockedTarget("Somebody");
        monster.ApplyKnockdown(KnockdownKind.Smash, 2);

        world.AdvanceFastTicksForTests(1);
        Assert.True(monster.IsKnockedDown);

        world.AdvanceFastTicksForTests(1);
        Assert.False(monster.IsKnockedDown);
    }

    [Fact]
    public void An_unlocked_dazed_monster_counts_down_on_its_wander_attempts_instead_of_moving()
    {
        var (world, db) = BuildWorld();
        AddGuardRoom(db, 1, 101);
        var monster = Spawn(world, 1);
        monster.ApplyKnockdown(KnockdownKind.Smash, 2);

        world.RunMonsterWanderForTests();
        Assert.True(monster.IsKnockedDown);
        Assert.Equal(1, monster.RoomNumber);

        world.RunMonsterWanderForTests();
        Assert.False(monster.IsKnockedDown);
        Assert.Equal(1, monster.RoomNumber);   // the attempt that brought it up still did not move it

        world.RunMonsterWanderForTests();
        Assert.Equal(101, monster.RoomNumber);
    }

    [Fact]
    public void A_stationary_monster_still_makes_wander_attempts_that_count_its_daze_down()
    {
        var (world, db) = BuildWorld();
        db.Monsters[GuardId].Type = 3;   // Stationary: the mover never relocates it
        AddGuardRoom(db, 1, 101);
        var monster = Spawn(world, 1);
        monster.ApplyKnockdown(KnockdownKind.Smash, 1);

        world.RunMonsterWanderForTests();

        Assert.False(monster.IsKnockedDown);
        Assert.Equal(1, monster.RoomNumber);
    }

    [Fact]
    public void The_daze_counts_down_once_per_monster_attack_round_and_the_rise_is_announced()
    {
        var player = new Player { Name = "Target", CurrentHP = 5000, MaxHP = 5000 };
        var monster = new MonsterInstance
        {
            Template = new Monster { Name = "rat", Energy = 1000, Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1 }] },
            DisplayName = "rat",
            CurrentHP = 1000,
            MaxHP = 1000,
        };
        monster.ResetEnergy();
        monster.ApplyKnockdown(KnockdownKind.Smash, 2);

        monster.PrepareCombatRound();
        var first = CombatEngine.MonsterAttack(monster, player);
        Assert.True(monster.IsKnockedDown);
        Assert.DoesNotContain(first.Messages, line => line.Contains("rises from the floor", StringComparison.Ordinal));

        monster.PrepareCombatRound();
        var second = CombatEngine.MonsterAttack(monster, player);
        Assert.False(monster.IsKnockedDown);
        Assert.Contains(second.Messages, line => line.Contains("Slightly dazed the rat rises from the floor.", StringComparison.Ordinal));
        Assert.Contains(second.RoomMessages, line => line.Contains("Slightly dazed the rat rises from the floor.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dazed_monster_does_not_recover_on_the_medium_tick_by_itself()
    {
        var (world, db) = BuildWorld();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, MonsterType = 9 };
        db.Monsters[700] = new Monster { Number = 700, Name = "statue", HP = 50, Type = 3, Group = 0 };   // never attempts a wander
        var statue = Spawn(world, 1, 700);
        statue.ApplyKnockdown(KnockdownKind.Smash, 1);

        world.AdvanceMediumTicksForTests(5);

        Assert.True(statue.IsKnockedDown);
    }

    private static (GameWorld World, InMemoryGameDatabase Db) BuildWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Monsters[GuardId] = new Monster
        {
            Number = GuardId, Name = "storm giant", HP = 100, Type = 0, Group = GuardGroup, GroupIndex = 35,
        };
        return (new GameWorld(db, new InMemoryPlayerRepository()), db);
    }

    private static MonsterInstance Spawn(GameWorld world, int room, int monsterId = GuardId)
    {
        Assert.True(world.TrySpawnMonsterInRoom(1, room, monsterId, ignoreRoomRestrictions: true, out var monster, out _));
        return monster!;
    }

    // A guard room with one plain exit north into an empty guard room.
    private static void AddGuardRoom(InMemoryGameDatabase db, int room, int target)
    {
        var source = new Room { MapNumber = 1, RoomNumber = room, MonsterType = GuardGroup };
        source.SetExit(new RoomExitDefinition
        {
            MapNumber = 1, RoomNumber = room, Direction = "north",
            TargetMap = 1, TargetRoom = target, ExitType = RoomExitType.Normal,
        });
        db.Rooms[(1, room)] = source;
        db.Rooms.TryAdd((1, target), new Room { MapNumber = 1, RoomNumber = target, MonsterType = GuardGroup });
    }
}
