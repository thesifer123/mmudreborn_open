using System;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// "home <monster>" QOL lookup: reports spawn-origin room, live location, and respawn timing for unique
// timer-regen bosses (GameLimit==1 && RegenTime>0). These pin the GameWorld.GetMonsterHomeReports core.
public sealed class MonsterHomeReportTests
{
    private const int DragonId = 851;
    private const int RegularRatId = 200; // a non-unique mob that must never appear in a home report
    private const int RegenHours = 2;

    [Fact]
    public void Reports_home_room_for_a_matching_timer_regen_boss()
    {
        var world = BuildWorld();

        var report = Assert.Single(world.GetMonsterHomeReports("midnight"));

        Assert.Equal(DragonId, report.Number);
        Assert.Equal(RegenHours, report.RegenTimeHours);
        var home = Assert.Single(report.HomeRooms);
        Assert.Equal((1, 1), (home.Map, home.Room));
        Assert.Equal("Bone Room", home.RoomName);
        Assert.True(home.EnterSpawn); // Room.NPC primary
    }

    [Fact]
    public void Non_unique_monsters_are_excluded()
    {
        var world = BuildWorld();

        Assert.Empty(world.GetMonsterHomeReports("rat"));
    }

    [Fact]
    public void Reports_active_when_the_boss_is_alive_in_its_home_room()
    {
        var world = BuildWorld();
        EnterRoom(world, 1, 1); // spawns the Room.NPC primary

        var report = Assert.Single(world.GetMonsterHomeReports("midnight"));

        Assert.True(report.IsActive);
        Assert.True(report.InHomeRoom);
        Assert.Null(report.RespawnReadyAtUtc);
    }

    [Fact]
    public void Reports_current_room_when_the_boss_is_away_from_home()
    {
        var world = BuildWorld();
        Assert.True(world.TrySpawnMonsterInRoom(1, 2, DragonId, ignoreRoomRestrictions: true, out _, out _));

        var report = Assert.Single(world.GetMonsterHomeReports("midnight"));

        Assert.True(report.IsActive);
        Assert.False(report.InHomeRoom);
        Assert.Equal((1, 2), (report.CurrentMap, report.CurrentRoom));
        Assert.Equal("Far Cavern", report.CurrentRoomName);
    }

    [Fact]
    public void Reports_respawn_timer_after_the_boss_is_killed()
    {
        var world = BuildWorld();
        EnterRoom(world, 1, 1);
        var dragon = world.GetMonstersInRoom(1, 1).Single(m => m.Template.Number == DragonId);

        dragon.CurrentHP = 0;
        world.RemoveDeadMonster(dragon);

        var before = DateTime.UtcNow;
        var report = Assert.Single(world.GetMonsterHomeReports("midnight"));

        Assert.False(report.IsActive);
        Assert.NotNull(report.DiedAtUtc);
        Assert.NotNull(report.RespawnReadyAtUtc);
        // Nominal eligibility is RegenTime hours after death.
        Assert.InRange(
            report.RespawnReadyAtUtc!.Value,
            before.AddHours(RegenHours).AddMinutes(-1),
            before.AddHours(RegenHours).AddMinutes(1));
    }

    private static void EnterRoom(GameWorld world, int map, int room)
        => world.NotifyPlayerEnteredRoom(new Player { CurrentMapNumber = map, CurrentRoomNumber = room });

    private static GameWorld BuildWorld()
    {
        var database = new InMemoryGameDatabase();

        database.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Bone Room", NPC = DragonId };
        database.Rooms[(1, 2)] = new Room { MapNumber = 1, RoomNumber = 2, Name = "Far Cavern" };

        database.Monsters[DragonId] = new Monster
        {
            Number = DragonId,
            Name = "colossal midnight dragon",
            HP = 100,
            Type = 1,
            GameLimit = 1,
            RegenTime = RegenHours,
        };

        // Ordinary fauna: GameLimit 0 → never a "home" subject even though the name would match a query.
        database.Monsters[RegularRatId] = new Monster
        {
            Number = RegularRatId,
            Name = "giant rat",
            HP = 5,
            Type = 1,
            GameLimit = 0,
            RegenTime = 0,
        };

        return new GameWorld(database, new InMemoryPlayerRepository());
    }
}
