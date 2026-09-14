using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class GameWorldSpawnTimingTests
{
    [Fact]
    public void Monster_generation_rate_and_regen_minutes_are_configurable_and_clamped_and_persisted()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo);

        // Our defaults: 5s generation rate (deliberately lowered from 15 in 3ba3d02 for a livelier,
        // stock-feeling world), 5min regen.
        Assert.Equal(5, world.MonsterGenerationRateSeconds);
        Assert.Equal(5, world.LairRegenDefaultMinutes);

        world.SetMonsterGenerationRateSeconds(30);
        Assert.Equal(30, world.MonsterGenerationRateSeconds);
        Assert.Equal(30, repo.GetServerSettingInt("MonsterGenRateSeconds", -1)); // persisted

        world.SetMonsterGenerationRateSeconds(0);           // below floor
        Assert.Equal(1, world.MonsterGenerationRateSeconds); // clamped to >=1

        world.SetLairRegenDefaultMinutes(20);
        Assert.Equal(20, world.LairRegenDefaultMinutes);
        Assert.Equal(20, repo.GetServerSettingInt("LairRegenMinutes", -1)); // persisted

        // A fresh world re-reads persisted settings.
        var reloaded = new GameWorld(new InMemoryGameDatabase(), repo);
        Assert.Equal(1, reloaded.MonsterGenerationRateSeconds);
        Assert.Equal(20, reloaded.LairRegenDefaultMinutes);
    }

    [Fact]
    public void Spawn_bubble_radius_defaults_to_ten_and_is_clamped_and_persisted()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo);

        // Default: 10 rooms (small, cheap BFS; tight scheduled-regen neighborhood).
        Assert.Equal(10, world.ActiveLairSpawnRoomRadius);

        world.SetActiveLairSpawnRoomRadius(25);
        Assert.Equal(25, world.ActiveLairSpawnRoomRadius);
        Assert.Equal(25, repo.GetServerSettingInt("SpawnBubbleRadius", -1)); // persisted

        world.SetActiveLairSpawnRoomRadius(0);              // below floor
        Assert.Equal(1, world.ActiveLairSpawnRoomRadius);   // clamped to >=1
        world.SetActiveLairSpawnRoomRadius(999);            // above ceiling
        Assert.Equal(60, world.ActiveLairSpawnRoomRadius);  // clamped to <=60

        // A fresh world re-reads the persisted value.
        var reloaded = new GameWorld(new InMemoryGameDatabase(), repo);
        Assert.Equal(60, reloaded.ActiveLairSpawnRoomRadius);
    }

    [Fact]
    public void GetRoomSpawnDelay_uses_the_stock_default_when_room_delay_is_zero()
    {
        var world = new GameWorld(new InMemoryGameDatabase(), new InMemoryPlayerRepository());
        var room = new Room { Delay = 0 };

        Assert.Equal(TimeSpan.FromMinutes(5), world.GetRoomSpawnDelay(room));
    }

    [Fact]
    public void GetRoomSpawnDelay_skips_delay_for_dat_room_type_two()
    {
        var world = new GameWorld(new InMemoryGameDatabase(), new InMemoryPlayerRepository());
        var room = new Room { RoomType = 2, Delay = 5 };

        Assert.Equal(TimeSpan.Zero, world.GetRoomSpawnDelay(room));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(120)]
    public void GetRoomSpawnDelay_uses_dat_delay_as_minutes_when_present(int delay)
    {
        var world = new GameWorld(new InMemoryGameDatabase(), new InMemoryPlayerRepository());
        var room = new Room { Delay = delay };

        Assert.Equal(TimeSpan.FromMinutes(delay), world.GetRoomSpawnDelay(room));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void CanRoomGenerateLairMonsters_allows_the_stock_background_spawn_room_types(int roomType)
    {
        var room = new Room
        {
            RoomType = roomType,
            MaxRegen = 1,
            MonsterType = 7,
            MinIndex = 2,
            MaxIndex = 3,
        };

        Assert.True(GameWorld.CanRoomGenerateLairMonsters(room));
    }

    [Theory]
    [InlineData(0, 0, 7, 2, 3)]
    [InlineData(1, 1, 7, 2, 3)]
    [InlineData(5, 1, 7, 2, 3)]
    [InlineData(3, 1, 0, 2, 3)]
    [InlineData(3, 1, 7, 0, 0)]
    public void CanRoomGenerateLairMonsters_blocks_rooms_without_stock_spawn_metadata(
        int roomType,
        int maxRegen,
        int monsterType,
        int minIndex,
        int maxIndex)
    {
        var room = new Room
        {
            RoomType = roomType,
            MaxRegen = maxRegen,
            MonsterType = monsterType,
            MinIndex = minIndex,
            MaxIndex = maxIndex,
        };

        Assert.False(GameWorld.CanRoomGenerateLairMonsters(room));
    }

    [Theory]
    [InlineData(0, 0, 1, 5)]
    [InlineData(2, 0, 1, 90)]
    [InlineData(0, 1, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(0, 2, 1, 0)]
    [InlineData(3, 0, 1, 0)]
    public void GetCurrentRoomPressureSpawnChancePercent_matches_the_stock_room_type_gates(
        int roomType,
        int aliveMonsterCount,
        int playerCount,
        int expectedChance)
    {
        var room = new Room { RoomType = roomType };

        Assert.Equal(
            expectedChance,
            GameWorld.GetCurrentRoomPressureSpawnChancePercent(room, aliveMonsterCount, playerCount));
    }

    [Fact]
    public void IsUnboundMonsterCompatibleWithRoom_allows_zero_spawn_rooms_in_same_monster_group()
    {
        var monster = new Monster { Group = 7 };
        var room = new Room
        {
            MaxRegen = 0,
            MonsterType = 7,
        };

        Assert.True(GameWorld.IsUnboundMonsterCompatibleWithRoom(monster, room));
    }

    [Fact]
    public void IsUnboundMonsterCompatibleWithRoom_blocks_rooms_outside_monster_group()
    {
        var monster = new Monster { Group = 7 };
        var room = new Room
        {
            MaxRegen = 10,
            MonsterType = 6,
        };

        Assert.False(GameWorld.IsUnboundMonsterCompatibleWithRoom(monster, room));
    }

    [Theory]
    [InlineData(0, 0)]    // Zero MaxRegen → no cap enforced (some rooms intentionally uncapped).
    [InlineData(2, 2)]    // Newhaven Arena style: cap honoured at the stock value.
    [InlineData(15, 15)]  // At the engine ceiling.
    [InlineData(50, 15)]  // Clamped down to the engine's MaxMonstersPerRoom safeguard.
    public void GetRoomLairCap_clamps_MaxRegen_to_engine_ceiling(int maxRegen, int expectedCap)
    {
        var room = new Room { MaxRegen = maxRegen };

        Assert.Equal(expectedCap, GameWorld.GetRoomLairCap(room));
    }

    [Theory]
    [InlineData(2, 0, false)]   // 0 of 2 alive — below cap.
    [InlineData(2, 1, false)]   // 1 of 2 — below cap.
    [InlineData(2, 2, true)]    // At cap → wandering/pursuit must skip this room.
    [InlineData(2, 5, true)]    // Already over (legacy overflow) — definitely at cap.
    [InlineData(0, 100, false)] // MaxRegen=0 disables the cap, never blocks.
    public void IsRoomAtLairCap_blocks_when_alive_count_meets_or_exceeds_MaxRegen(int maxRegen, int alive, bool expected)
    {
        // Regression: wandering and pursuit used to ignore MaxRegen, letting Newhaven Arena
        // (MaxRegen=2) accumulate 5+ monsters as compatible adjacent rooms drifted in.
        var room = new Room { MaxRegen = maxRegen };

        Assert.Equal(expected, GameWorld.IsRoomAtLairCap(room, alive));
    }

    [Fact]
    public void Killing_a_lair_monster_sets_the_room_cooldown_to_Delay_minutes()
    {
        // Regression for the full stock-alignment fix: the room's regen timer is anchored to
        // monster REMOVAL (stock writes it on the take/death path), not to
        // spawn attempts. Previously ScheduleRoomLairSpawnDeadline used MIN semantics — a past
        // initial-seed value would survive the death timestamp and the room would read as
        // "ready" the next tick. Now death always sets `now + GetRoomSpawnDelay(room)`.
        // See [[lair-spawn-timer-semantics]].
        var database = new InMemoryGameDatabase();
        var room = new Room
        {
            MapNumber = 1,
            RoomNumber = 1,
            RoomType = 0,           // AmbientPressureSpawnRoomType
            MaxRegen = 2,
            MonsterType = 7,
            MinIndex = 1,
            MaxIndex = 1,
            Delay = 5,              // 5-minute cooldown
        };
        database.Rooms[(1, 1)] = room;
        var template = new Monster
        {
            Number = 100,
            Name = "test rat",
            HP = 10,
            Group = 7,
            GroupIndex = 1,
        };
        database.Monsters[100] = template;

        var world = new GameWorld(database, new InMemoryPlayerRepository());

        // Spawn one lair monster directly, then kill it. Death routes through RemoveDeadMonster
        // → ScheduleLairRespawnAfterVacancy → ScheduleRoomLairSpawnDeadline.
        Assert.True(world.TrySpawnMonsterInRoom(1, 1, monsterId: 100, ignoreRoomRestrictions: false, out var monster, out _));
        Assert.NotNull(monster);
        // Mark as background-spawned so vacancy scheduling kicks in (TrySpawnMonsterInRoom is the
        // sysop/scripted path which doesn't flag IsBackgroundSpawn; we set it here to mirror what
        // the lair-spawn path produces).
        monster!.IsBackgroundSpawn = true;
        monster.HomeMapNumber = 1;
        monster.HomeRoomNumber = 1;

        DateTime before = DateTime.UtcNow;
        monster.CurrentHP = 0;       // dead
        world.RemoveDeadMonster(monster);

        var deadline = world.GetRoomLairSpawnDeadline(1, 1);
        Assert.NotNull(deadline);
        // Cooldown is `Delay` minutes from death. Allow a generous window for clock jitter; the
        // important assertion is that the deadline is in the FUTURE (death created a cooldown).
        Assert.True(deadline > before, $"Expected room deadline > {before:o}, got {deadline:o}.");
        Assert.InRange(
            (deadline.Value - before).TotalMinutes,
            4.5,
            5.5);
    }
}