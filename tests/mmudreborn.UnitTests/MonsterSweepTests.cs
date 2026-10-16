using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Stock's monster sweeps on the host poll clock (GameWorld.MonsterSweeps). Monsters sit in a 2000-slot
// buffer; each update kind walks it one slot per two calls of the game's polling routine, which the host
// calls once per ~10 ms main-loop pass for each player in the game. So how soon a monster is updated
// depends on its slot and on how many players are in the game. The background save, one record per
// step, is what clears a monster's dirty byte, and a dirty guard patrols past the wander budget.
public sealed class MonsterSweepTests
{
    private const int GuardGroup = 5;
    private const int GuardId = 638;
    private const int StatueId = 700;
    private const int BuffSpellId = 900;

    [Fact]
    public void Monsters_take_buffer_slots_in_the_order_they_spawn()
    {
        var (world, db) = BuildWorld();
        AddGuardRoom(db, 1, 100);

        var first = Spawn(world, 1, GuardId);
        var second = Spawn(world, 1, GuardId);
        var third = Spawn(world, 1, GuardId);

        Assert.Equal(1, world.GetMonsterBufferSlotForTests(first));
        Assert.Equal(2, world.GetMonsterBufferSlotForTests(second));
        Assert.Equal(3, world.GetMonsterBufferSlotForTests(third));
    }

    [Fact]
    public void A_monster_gets_its_medium_update_only_when_the_paced_sweep_reaches_its_slot()
    {
        var (world, _) = BuildWorldWithStatues(statues: 40);
        AddPlayersInGame(world, 1);
        var last = StatueInSlot(world, 40);
        Assert.True(last.AddOrRefreshActiveSpell(world.Database, BuffSpellId, 1, 10));

        // The tick re-arms the sweep and steps slot 0. With one player in the game every second poll
        // call (= every second host pass) steps one slot, so slots 1..39 take 78 passes.
        world.KickMediumMonsterSweepForTests();
        world.RunHostPollPassesForTests(78);
        Assert.Equal(10, last.ActiveSpells[0].RemainingDuration);

        world.RunHostPollPassesForTests(2);
        Assert.Equal(9, last.ActiveSpells[0].RemainingDuration);
    }

    [Fact]
    public void Each_extra_player_in_the_game_speeds_the_sweep_up()
    {
        var (world, _) = BuildWorldWithStatues(statues: 40);
        AddPlayersInGame(world, 2);
        var last = StatueInSlot(world, 40);
        Assert.True(last.AddOrRefreshActiveSpell(world.Database, BuffSpellId, 1, 10));

        // Two players: two poll calls per pass, so the same 80 calls take 40 passes.
        world.KickMediumMonsterSweepForTests();
        world.RunHostPollPassesForTests(39);
        Assert.Equal(10, last.ActiveSpells[0].RemainingDuration);

        world.RunHostPollPassesForTests(1);
        Assert.Equal(9, last.ActiveSpells[0].RemainingDuration);
    }

    [Fact]
    public void A_dirty_guard_patrols_past_the_spent_wander_budget_and_a_clean_one_does_not()
    {
        var (world, db) = BuildWorld();
        // Five guard rooms, each with a way north into an empty guard room.
        var guards = new List<MonsterInstance>();
        for (int room = 1; room <= 5; room++)
        {
            AddGuardRoom(db, room, room + 100);
            guards.Add(Spawn(world, room, GuardId));
        }

        // The first three spend the realm's 3-move budget; the fourth is clean, the fifth dirty.
        guards[4].IsDirty = true;

        world.RunMonsterWanderForTests();

        Assert.Equal(4, guards[3].RoomNumber);     // held by the spent budget
        Assert.Equal(105, guards[4].RoomNumber);   // dirty: moves anyway
    }

    [Fact]
    public void A_monster_spawns_clean_and_moving_makes_it_dirty()
    {
        var (world, db) = BuildWorld();
        AddGuardRoom(db, 1, 101);
        var guard = Spawn(world, 1, GuardId);
        Assert.False(guard.IsDirty);

        world.RelocateMonsterForTests(guard, 1, 101);

        Assert.True(guard.IsDirty);
    }

    [Fact]
    public void The_background_save_clears_a_monsters_dirty_byte_when_it_reaches_its_slot()
    {
        var (world, db) = BuildWorld();
        AddGuardRoom(db, 1, 101);
        var guard = Spawn(world, 1, GuardId);   // slot 1
        world.RelocateMonsterForTests(guard, 1, 101);
        Assert.Equal(1, world.GetMonsterBufferSlotForTests(guard));

        // The save walks the 1500-room buffer first (slots 0..1500), then the monster buffer from slot 0.
        world.RunSaveSweepStepsForTests(1501 + 1);
        Assert.True(guard.IsDirty);

        world.RunSaveSweepStepsForTests(1);
        Assert.False(guard.IsDirty);
    }

    [Fact]
    public void Nightly_cleanup_clears_every_dirty_byte()
    {
        var (world, db) = BuildWorld();
        AddGuardRoom(db, 1, 101);
        var guard = Spawn(world, 1, GuardId);
        world.RelocateMonsterForTests(guard, 1, 101);
        Assert.True(guard.IsDirty);

        world.RunDailyCleanup();

        Assert.False(guard.IsDirty);
    }

    [Fact]
    public void The_slow_update_drains_poison_then_regenerates_and_forgets_the_last_move_direction()
    {
        var (world, db) = BuildWorld();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, MonsterType = 9 };
        db.Monsters[StatueId] = new Monster { Number = StatueId, Name = "statue", HP = 50, Type = 3, Group = 9, HPRegen = 10 };
        var statue = Spawn(world, 1, StatueId);
        statue.CurrentHP = 5;
        statue.PoisonLevel = 8;
        statue.LastMoveDirection = "north";

        world.AdvanceSlowTicksForTests(1);

        // 5 - 8 + 10: the poison would have killed it on its own, but death is judged after the regen.
        Assert.Equal(7, statue.CurrentHP);
        Assert.False(statue.IsDead);
        Assert.Equal("", statue.LastMoveDirection);
    }

    private static (GameWorld World, InMemoryGameDatabase Db) BuildWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Monsters[GuardId] = new Monster
        {
            Number = GuardId, Name = "storm giant", HP = 100, Type = 0, Group = GuardGroup, GroupIndex = 35,
        };
        db.Spells[BuffSpellId] = new GameSpell { Number = BuffSpellId, Name = "test buff", Targets = 1 };
        // A room far from the monsters for the players in the game to stand in.
        db.Rooms[(1, 900)] = new Room { MapNumber = 1, RoomNumber = 900 };
        return (new GameWorld(db, new InMemoryPlayerRepository()), db);
    }

    // Stationary statues spread over rooms of ten (a room holds 15 at most), spawned in order.
    private static (GameWorld World, InMemoryGameDatabase Db) BuildWorldWithStatues(int statues)
    {
        var (world, db) = BuildWorld();
        db.Monsters[StatueId] = new Monster { Number = StatueId, Name = "statue", HP = 50, Type = 3, Group = 9 };
        for (int i = 0; i < statues; i++)
        {
            int room = 1 + i / 10;
            db.Rooms.TryAdd((1, room), new Room { MapNumber = 1, RoomNumber = room, MonsterType = 9 });
            Spawn(world, room, StatueId);
        }
        return (world, db);
    }

    private static MonsterInstance StatueInSlot(GameWorld world, int slot)
    {
        for (int room = 1; room <= 10; room++)
            foreach (var monster in world.GetMonstersInRoom(1, room))
                if (world.GetMonsterBufferSlotForTests(monster) == slot)
                    return monster;
        throw new InvalidOperationException($"No monster in slot {slot}.");
    }

    private static void AddPlayersInGame(GameWorld world, int count)
    {
        for (int i = 0; i < count; i++)
            world.AddPlayer(new Player { Name = $"Player{i}", CurrentMapNumber = 1, CurrentRoomNumber = 900 });
    }

    private static MonsterInstance Spawn(GameWorld world, int room, int monsterId)
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
