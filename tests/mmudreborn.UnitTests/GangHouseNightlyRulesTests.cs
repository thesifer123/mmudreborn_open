using System;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The stock nightly cleanup's gang-house rules. Ownership is re-derived every night from
// who carries each deed; the carrier pays the tax from their own bankbook; a house's items leave anyone
// outside its owning gang; an unpaid house leaves its carrier and gang a login notice; anyone saved inside
// a closed house is put out to the Temple (or the Earthen Tomb from 40 evil points); a closed house's
// floors are swept and an owned house's floors lose items tagged to a closed house; and the deed-sale
// paper-work lockout ends for everyone.
public sealed class GangHouseNightlyRulesTests
{
    private const int Blue = 6;
    private const int White = 10;
    private const int BlueDeedId = 1012;
    private const int WhiteDeedId = 1017;
    private const int BlueKeyId = 1032;
    private const int SapphireEmblemId = 1034;
    private const int WhiteKeyId = 1044;
    private const int TorchId = 5;
    private const int BlueTaxGold = 1000;
    private const string Ashgrove = "The Ashgrove";
    private const string Crows = "The Hollow Crows";
    private const string EmblemSlot = "worn-16";
    private static readonly (int Map, int Room) BlueHall = (15, 900);
    private static readonly (int Map, int Room) WhiteHall = (15, 960);

    private static (GameWorld World, InMemoryPlayerRepository Repo, ShopItem BlueDeedSlot) CreateWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Square", Description = "x" };
        db.Rooms[(1, GameWorld.DefaultDeathRespawnRoomNumber)] = new Room { MapNumber = 1, RoomNumber = GameWorld.DefaultDeathRespawnRoomNumber, Name = "Temple", Description = "x" };
        db.Rooms[(1, GameWorld.EvilDeathRespawnRoomNumber)] = new Room { MapNumber = 1, RoomNumber = GameWorld.EvilDeathRespawnRoomNumber, Name = "Earthen Tomb", Description = "x" };
        db.Rooms[BlueHall] = new Room { MapNumber = BlueHall.Map, RoomNumber = BlueHall.Room, Name = "Blue Hall", Description = "x", GangHouseId = Blue, Attributes = (int)RoomAttributes.GangHouse };
        db.Rooms[WhiteHall] = new Room { MapNumber = WhiteHall.Map, RoomNumber = WhiteHall.Room, Name = "White Hall", Description = "x", GangHouseId = White, Attributes = (int)RoomAttributes.GangHouse };

        db.Items[BlueDeedId] = new Item { Number = BlueDeedId, Name = "blue parchment deed", Gettable = true, Abilities = { [181] = Blue, [182] = BlueTaxGold, [183] = Blue, [184] = Blue } };
        db.Items[WhiteDeedId] = new Item { Number = WhiteDeedId, Name = "white parchment deed", Gettable = true, Abilities = { [181] = White, [182] = 2000, [183] = White, [184] = White } };
        db.Items[BlueKeyId] = new Item { Number = BlueKeyId, Name = "blue key", Gettable = true, Abilities = { [183] = Blue } };
        db.Items[SapphireEmblemId] = new Item { Number = SapphireEmblemId, Name = "sapphire emblem", Worn = 16, Gettable = true, Abilities = { [183] = Blue } };
        db.Items[WhiteKeyId] = new Item { Number = WhiteKeyId, Name = "white key", Gettable = true, Abilities = { [183] = White } };
        db.Items[TorchId] = new Item { Number = TorchId, Name = "torch", Gettable = true };

        var blueSlot = new ShopItem { ItemId = BlueDeedId, Max = 1 };
        db.Shops[124] = new Shop
        {
            Number = 124,
            Name = "Realm Deed Shop",
            ShopType = GameWorld.DeedShopType,
            Items = { blueSlot, new ShopItem { ItemId = WhiteDeedId, Max = 1 } },
        };

        var repo = new InMemoryPlayerRepository();
        repo.CreateGangRecord(Ashgrove, 9, "Rook");
        repo.CreateGangRecord(Crows, 9, "Flint");
        return (new GameWorld(db, repo), repo, blueSlot);
    }

    private static Player SaveOffline(InMemoryPlayerRepository repo, string name, string gang, params int[] inventory)
    {
        var player = new Player { Name = name, Gang = gang, Inventory = inventory.ToList(), CurrentMapNumber = 1, CurrentRoomNumber = 1 };
        repo.SavePlayer(player);
        return player;
    }

    private static Player AddOnline(GameWorld world, InMemoryPlayerRepository repo, RecordingGameClient client, string name, string gang, params int[] inventory)
    {
        var player = new Player { Name = name, Gang = gang, Inventory = inventory.ToList(), CurrentMapNumber = 1, CurrentRoomNumber = 1, Client = client };
        client.Player = player;
        repo.SavePlayer(player);
        world.AddPlayer(player);
        return player;
    }

    [Fact]
    public void Ownership_follows_the_deed_to_whichever_gang_carries_it()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Rook", Ashgrove, BlueKeyId).BankBalances[8] = 10_000_000;
        SaveOffline(repo, "Flint", Crows, BlueDeedId).BankBalances[8] = 500_000;
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        var house = world.GetGangHouse(Blue);
        Assert.NotNull(house);
        Assert.Equal(Crows, house!.OwnerGang);
        Assert.Equal("Flint", house.OwnerPlayer);
        Assert.Equal(500_000 - BlueTaxGold * 100L, repo.LoadPlayerByName("Flint")!.BankBalances[8]);
        Assert.Equal(10_000_000, repo.LoadPlayerByName("Rook")!.BankBalances[8]);
        Assert.Empty(repo.LoadPlayerByName("Rook")!.Inventory);   // no longer the owning gang's key to keep
    }

    [Fact]
    public void A_carried_deed_makes_its_house_owned_even_without_a_recorded_purchase()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Flint", Crows, WhiteDeedId).BankBalances[8] = 500_000;

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.Equal(Crows, world.GetGangHouse(White)?.OwnerGang);
    }

    [Fact]
    public void An_owned_houses_items_leave_everyone_outside_the_owning_gang()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Rook", Ashgrove, BlueDeedId).BankBalances[8] = 10_000_000;
        var birch = SaveOffline(repo, "Birch", Ashgrove, BlueKeyId);
        birch.Equipment[EmblemSlot] = SapphireEmblemId;
        SaveOffline(repo, "Flint", Crows, BlueKeyId, TorchId);
        SaveOffline(repo, "Drifter", "").Equipment[EmblemSlot] = SapphireEmblemId;
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.True(world.IsGangHouseOwned(Blue));
        Assert.Equal([BlueKeyId], repo.LoadPlayerByName("Birch")!.Inventory);
        Assert.Equal(SapphireEmblemId, repo.LoadPlayerByName("Birch")!.Equipment[EmblemSlot]);
        Assert.Equal([TorchId], repo.LoadPlayerByName("Flint")!.Inventory);
        Assert.False(repo.LoadPlayerByName("Drifter")!.Equipment.ContainsKey(EmblemSlot));
        Assert.All(new[] { "Birch", "Flint", "Drifter" }, name => Assert.Equal(0, repo.LoadPlayerByName(name)!.GangHouseFlags));
    }

    [Fact]
    public void Unpaid_tax_closes_the_house_and_leaves_login_notices_for_its_carrier_and_gang()
    {
        var (world, repo, blueSlot) = CreateWorld();
        SaveOffline(repo, "Rook", Ashgrove, BlueDeedId, BlueKeyId).BankBalances[8] = 10;   // can't cover 1000 gold
        SaveOffline(repo, "Birch", Ashgrove, BlueKeyId);
        SaveOffline(repo, "Flint", Crows, BlueKeyId);
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.False(world.IsGangHouseOwned(Blue));
        Assert.Equal(1, blueSlot.Current);
        var rook = repo.LoadPlayerByName("Rook")!;
        Assert.Empty(rook.Inventory);
        Assert.Equal(10, rook.BankBalances[8]);
        Assert.Equal(Player.GangHouseClosedNoticeFlag | Player.GangHouseItemsGoneNoticeFlag, rook.GangHouseFlags);
        Assert.Empty(repo.LoadPlayerByName("Birch")!.Inventory);
        Assert.Equal(Player.GangHouseItemsGoneNoticeFlag, repo.LoadPlayerByName("Birch")!.GangHouseFlags);
        Assert.Empty(repo.LoadPlayerByName("Flint")!.Inventory);
        Assert.Equal(0, repo.LoadPlayerByName("Flint")!.GangHouseFlags);   // not the house's gang: no notice
    }

    [Fact]
    public void Unpaid_eviction_notices_reach_an_online_gang_member_straight_away()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Rook", Ashgrove, BlueDeedId).BankBalances[8] = 10;
        var client = new RecordingGameClient();
        var birch = AddOnline(world, repo, client, "Birch", Ashgrove, BlueKeyId);
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.Empty(birch.Inventory);
        Assert.Contains(client.Lines, l => l.Contains("Gang house items have dissappeared from your inventory!"));
        Assert.DoesNotContain(client.Lines, l => l.Contains("Your ganghouse has been closed down!!"));   // not the carrier
        Assert.Equal(0, birch.GangHouseFlags);   // told now, nothing left for the next login
    }

    [Fact]
    public void A_house_nobody_carried_closes_without_notices()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Rook", Ashgrove, BlueKeyId);
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.Empty(repo.LoadPlayerByName("Rook")!.Inventory);
        Assert.Equal(0, repo.LoadPlayerByName("Rook")!.GangHouseFlags);
    }

    [Fact]
    public void Cleanup_ends_the_deed_sale_lockout_for_everyone()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Rook", Ashgrove).GangHouseFlags = Player.GangHouseDeedSoldFlag | Player.GangHouseItemsGoneNoticeFlag;
        var flint = AddOnline(world, repo, new RecordingGameClient(), "Flint", Crows);
        flint.GangHouseFlags = Player.GangHouseDeedSoldFlag;

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.Equal(Player.GangHouseItemsGoneNoticeFlag, repo.LoadPlayerByName("Rook")!.GangHouseFlags);   // an unread notice stays
        Assert.Equal(0, flint.GangHouseFlags);
    }

    [Fact]
    public void Characters_saved_inside_a_closed_house_are_put_out()
    {
        var (world, repo, _) = CreateWorld();
        var rook = SaveOffline(repo, "Rook", Ashgrove);
        (rook.CurrentMapNumber, rook.CurrentRoomNumber) = BlueHall;
        var flint = SaveOffline(repo, "Flint", Crows);
        (flint.CurrentMapNumber, flint.CurrentRoomNumber) = BlueHall;
        flint.EvilPoints = 40;
        var wren = SaveOffline(repo, "Wren", Crows, WhiteDeedId);
        wren.BankBalances[8] = 10_000_000;
        (wren.CurrentMapNumber, wren.CurrentRoomNumber) = WhiteHall;
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.Equal((1, GameWorld.DefaultDeathRespawnRoomNumber), Location(repo, "Rook"));
        Assert.Equal((1, GameWorld.EvilDeathRespawnRoomNumber), Location(repo, "Flint"));
        Assert.Equal(WhiteHall, Location(repo, "Wren"));   // White is still owned
    }

    [Fact]
    public void An_online_player_standing_in_a_closed_house_is_moved_out()
    {
        var (world, repo, _) = CreateWorld();
        var rook = AddOnline(world, repo, new RecordingGameClient(), "Rook", Ashgrove, BlueKeyId);
        (rook.CurrentMapNumber, rook.CurrentRoomNumber) = BlueHall;
        world.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.Equal((1, GameWorld.DefaultDeathRespawnRoomNumber), (rook.CurrentMapNumber, rook.CurrentRoomNumber));
    }

    [Fact]
    public void Closed_house_floors_are_swept_and_owned_house_floors_lose_only_closed_house_items()
    {
        var (world, repo, _) = CreateWorld();
        SaveOffline(repo, "Wren", Crows, WhiteDeedId).BankBalances[8] = 10_000_000;
        world.DropItemInRoom(BlueHall.Map, BlueHall.Room, TorchId);
        world.DropCurrencyInRoom(BlueHall.Map, BlueHall.Room, 500);
        world.DropItemInRoom(WhiteHall.Map, WhiteHall.Room, TorchId);
        world.DropItemInRoom(WhiteHall.Map, WhiteHall.Room, WhiteKeyId);
        world.DropItemInRoom(WhiteHall.Map, WhiteHall.Room, BlueKeyId);
        world.DropCurrencyInRoom(WhiteHall.Map, WhiteHall.Room, 700);
        Assert.Single(world.GetVisibleGroundItems(BlueHall.Map, BlueHall.Room));
        Assert.Equal(500, world.GetVisibleGroundCurrency(BlueHall.Map, BlueHall.Room));
        Assert.Equal(3, world.GetVisibleGroundItems(WhiteHall.Map, WhiteHall.Room).Count);

        world.ProcessGangHouseTax(DateTime.UtcNow);   // Blue: nobody carries the deed. White: owned.

        Assert.Empty(world.GetVisibleGroundItems(BlueHall.Map, BlueHall.Room));
        Assert.Equal(0, world.GetVisibleGroundCurrency(BlueHall.Map, BlueHall.Room));
        var whiteFloor = world.GetVisibleGroundItems(WhiteHall.Map, WhiteHall.Room).Select(i => i.ItemId).OrderBy(id => id).ToList();
        Assert.Equal([TorchId, WhiteKeyId], whiteFloor);
        Assert.Equal(700, world.GetVisibleGroundCurrency(WhiteHall.Map, WhiteHall.Room));
    }

    private static (int, int) Location(InMemoryPlayerRepository repo, string name)
    {
        var player = repo.LoadPlayerByName(name)!;
        return (player.CurrentMapNumber, player.CurrentRoomNumber);
    }
}
