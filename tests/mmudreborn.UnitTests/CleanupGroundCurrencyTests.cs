using System;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Nightly cleanup hides a room's dropped coins coin for coin. Stock moves each denomination from the
// visible pile to the hidden one exactly as it lies and never exchanges it. We used to add
// the pile up as copper and re-mint it as the fewest, largest coins, so a room where 150 platinum's worth of
// gold was dropped in a day was found holding a runic.
public sealed class CleanupGroundCurrencyTests
{
    private static readonly (int Map, int Room) Floor = (1, 1);
    private static readonly (int Map, int Room) PlacedRoom = (1, 2);

    private static GameWorld CreateWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[Floor] = new Room { MapNumber = Floor.Map, RoomNumber = Floor.Room, Name = "Path", Description = "x" };
        // 87 copper of static placed currency, seeded as 8 silver + 7 copper.
        db.Rooms[PlacedRoom] = new Room { MapNumber = PlacedRoom.Map, RoomNumber = PlacedRoom.Room, Name = "Shrine", Description = "x", GroundCurrency = 87 };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static long Hidden(GameWorld world, (int Map, int Room) room, long denomination)
        => world.GetHiddenGroundCurrencyDenominationCount(room.Map, room.Room, denomination);

    private static long Visible(GameWorld world, (int Map, int Room) room, long denomination)
        => world.GetVisibleGroundCurrencyDenominationCount(room.Map, room.Room, denomination);

    [Fact]
    public void Dropped_coins_are_hidden_as_they_lie_never_re_minted_into_bigger_coins()
    {
        var world = CreateWorld();
        world.DropCurrencyInRoom(Floor.Map, Floor.Room, runic: 0, platinum: 3, gold: 15000, silver: 12, copper: 30);

        world.RunDailyCleanup(DateTime.UtcNow);

        Assert.Equal(0, world.GetVisibleGroundCurrency(Floor.Map, Floor.Room));
        Assert.Equal(0, Hidden(world, Floor, CurrencyHelper.CopperPerRunic));   // was 1 runic, 50 platinum, ...
        Assert.Equal(3, Hidden(world, Floor, CurrencyHelper.CopperPerPlatinum));
        Assert.Equal(15000, Hidden(world, Floor, CurrencyHelper.CopperPerGold));
        Assert.Equal(12, Hidden(world, Floor, CurrencyHelper.CopperPerSilver));
        Assert.Equal(30, Hidden(world, Floor, 1));
    }

    [Fact]
    public void A_rooms_placed_coins_stay_visible_and_only_the_dropped_ones_are_hidden()
    {
        var world = CreateWorld();
        Assert.Equal(87, world.GetVisibleGroundCurrency(PlacedRoom.Map, PlacedRoom.Room));   // seeds the placement
        world.DropCurrencyInRoom(PlacedRoom.Map, PlacedRoom.Room, runic: 0, platinum: 0, gold: 3, silver: 2, copper: 0);

        world.RunDailyCleanup(DateTime.UtcNow);

        Assert.Equal(8, Visible(world, PlacedRoom, CurrencyHelper.CopperPerSilver));
        Assert.Equal(7, Visible(world, PlacedRoom, 1));
        Assert.Equal(0, Visible(world, PlacedRoom, CurrencyHelper.CopperPerGold));
        Assert.Equal(3, Hidden(world, PlacedRoom, CurrencyHelper.CopperPerGold));
        Assert.Equal(2, Hidden(world, PlacedRoom, CurrencyHelper.CopperPerSilver));
        Assert.Equal(0, Hidden(world, PlacedRoom, 1));
    }
}
