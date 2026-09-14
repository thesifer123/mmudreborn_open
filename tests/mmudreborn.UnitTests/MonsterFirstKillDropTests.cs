using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The guaranteed-first-drop: a LIMITED monster (MaxSpawn != 0 → our
// GameLimit) whose template has no recorded death yet — its first-ever spawn — bypasses the per-item
// drop-% roll and carries ALL of its loot at 100%. On its first death the kill stamps the
// template last-death date, and every later spawn rolls the percentages normally. This is the
// "first kill, first drop" speed-run reward, always-on for limited monsters (no sysop toggle in stock).
public sealed class MonsterFirstKillDropTests
{
    private const int Map = 1;
    private const int Room = 10;
    private const int BossId = 5000;
    private const int RareItemId = 4000;

    // The rare drop is at 0% so ONLY the first-kill guarantee can ever place it — a normal roll never will.
    private static GameWorld BuildWorld(int gameLimit)
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(Map, Room)] = new Room { MapNumber = Map, RoomNumber = Room };
        db.Items[RareItemId] = new Item { Number = RareItemId, Name = "rare relic", Gettable = true };
        db.Monsters[BossId] = new Monster
        {
            Number = BossId, Name = "ancient boss", HP = 10, GameLimit = gameLimit,
            Drops = [new MonsterDrop { ItemId = RareItemId, Percent = 0 }],
        };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static MonsterInstance Spawn(GameWorld world)
    {
        Assert.True(world.TrySpawnMonsterInRoom(Map, Room, BossId, ignoreRoomRestrictions: true, out var mob, out _));
        return mob!;
    }

    [Fact]
    public void Limited_boss_first_spawn_carries_all_drops_ignoring_percent()
    {
        var world = BuildWorld(gameLimit: 1);
        var boss = Spawn(world);
        Assert.Contains(RareItemId, boss.CarriedDropItemIds);
    }

    [Fact]
    public void Limited_boss_rolls_normally_after_its_first_death()
    {
        var world = BuildWorld(gameLimit: 1);

        var first = Spawn(world);
        Assert.Contains(RareItemId, first.CarriedDropItemIds); // guaranteed on the first-ever spawn

        world.RemoveDeadMonster(first);                        // stamps the last-death ledger

        var second = Spawn(world);
        Assert.DoesNotContain(RareItemId, second.CarriedDropItemIds); // guarantee spent → 0% never drops
    }

    [Fact]
    public void Unlimited_monster_never_guarantees_drops()
    {
        var world = BuildWorld(gameLimit: 0);                  // GameLimit 0 = unlimited → always rolls
        var mob = Spawn(world);
        Assert.DoesNotContain(RareItemId, mob.CarriedDropItemIds);
    }
}
