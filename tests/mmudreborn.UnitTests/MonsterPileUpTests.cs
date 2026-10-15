using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #256: 15 storm giants piling up in the Storm Fortress waiting room (16/1183). Two divergences fed
// it: the 3-attempt wander budget was handed out in room-dictionary order (so the last-visited room of a
// closed area never let anyone leave and soaked up every wanderer until its 15 slots filled), and the
// king's/commander's summoned escorts were never counted against any lair, so they arrived on top of full
// lairs and every boss respawn added two more. Stock walks monsters by buffer slot, counts every spawn but
// a room's own primary as that room's offspring, and refuses any spawn into a room with no free slot.
public sealed class MonsterPileUpTests
{
    private const int GuardGroup = 5;
    private const int GuardId = 638;
    private const int BossId = 637;
    private const int EscortSpellId = 784;
    private const int SummonAbility = 12;

    [Fact]
    public void Wander_budget_goes_to_monsters_in_slot_order_whatever_room_they_stand_in()
    {
        var db = new InMemoryGameDatabase();
        db.Monsters[GuardId] = Guard();
        var world = new GameWorld(db, new InMemoryPlayerRepository());

        // The lone guard spawns FIRST, so it holds the lowest slot. Ten other rooms then fill with three
        // guards each — enough to spend the whole budget if the pass visits any of their rooms first.
        AddRoomWithExit(db, 100, 300);
        Assert.True(world.TrySpawnMonsterInRoom(1, 100, GuardId, ignoreRoomRestrictions: false, out var lone, out _));
        for (int room = 1; room <= 10; room++)
        {
            AddRoomWithExit(db, room, room + 200);
            for (int i = 0; i < 3; i++)
                Assert.True(world.TrySpawnMonsterInRoom(1, room, GuardId, ignoreRoomRestrictions: false, out _, out _));
        }

        world.RunMonsterWanderForTests();

        Assert.Equal(300, lone!.RoomNumber);
    }

    [Fact]
    public void A_bosss_summoned_escorts_count_against_its_lair()
    {
        var db = new InMemoryGameDatabase();
        db.Monsters[GuardId] = Guard();
        db.Monsters[BossId] = new Monster
        {
            Number = BossId, Name = "storm giant king", HP = 100, Type = 3, Group = GuardGroup, GroupIndex = 34,
            CreateSpell = EscortSpellId,
        };
        var escortSpell = new GameSpell { Number = EscortSpellId, Name = "summon storm giant", Targets = 1 };
        escortSpell.AbilitySlots.Add(new KeyValuePair<int, int>(SummonAbility, GuardId));
        escortSpell.AbilitySlots.Add(new KeyValuePair<int, int>(SummonAbility, GuardId));
        db.Spells[EscortSpellId] = escortSpell;
        // A Lair (RoomType 3) whose own spawns are the escort species, capped at two.
        db.Rooms[(1, 1)] = new Room
        {
            MapNumber = 1, RoomNumber = 1, NPC = BossId, RoomType = 3, MaxRegen = 2,
            MonsterType = GuardGroup, MinIndex = 35, MaxIndex = 35,
        };
        var world = new GameWorld(db, new InMemoryPlayerRepository());

        // Entry spawns the boss (its CreateSpell summons two escorts) and then fills the lair. The escorts
        // already fill the lair's two places, so the fill adds nothing — four would mean they were ignored.
        world.NotifyPlayerEnteredRoom(new Player { CurrentMapNumber = 1, CurrentRoomNumber = 1 });

        Assert.Equal(2, world.GetMonstersInRoom(1, 1).Count(m => m.Template.Number == GuardId && !m.IsDead));
        Assert.Equal(2, world.CountLivingLairOffspringForTests(1, 1));
    }

    [Fact]
    public void A_summon_into_a_room_with_no_free_slot_is_refused()
    {
        var db = new InMemoryGameDatabase();
        db.Monsters[GuardId] = Guard();
        AddRoomWithExit(db, 1, 2);
        var world = new GameWorld(db, new InMemoryPlayerRepository());
        for (int i = 0; i < 15; i++)
            Assert.True(world.TrySpawnMonsterInRoom(1, 1, GuardId, ignoreRoomRestrictions: true, out _, out _));

        bool spawned = world.TrySpawnMonsterInRoom(1, 1, GuardId, ignoreRoomRestrictions: true, out _, out _);

        Assert.False(spawned);
        Assert.Equal(15, world.GetMonstersInRoom(1, 1).Count(m => !m.IsDead));
    }

    private static Monster Guard() => new()
    {
        Number = GuardId, Name = "storm giant", HP = 100, Type = 0, Group = GuardGroup, GroupIndex = 35,
    };

    // A guard room with one plain exit north into a dead-end guard room.
    private static void AddRoomWithExit(InMemoryGameDatabase db, int room, int target)
    {
        var source = new Room { MapNumber = 1, RoomNumber = room, MonsterType = GuardGroup };
        source.SetExit(new RoomExitDefinition
        {
            MapNumber = 1, RoomNumber = room, Direction = "north",
            TargetMap = 1, TargetRoom = target, ExitType = RoomExitType.Normal,
        });
        db.Rooms[(1, room)] = source;
        db.Rooms[(1, target)] = new Room { MapNumber = 1, RoomNumber = target, MonsterType = GuardGroup };
    }
}
