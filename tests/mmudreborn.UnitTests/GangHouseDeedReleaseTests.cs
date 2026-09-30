using System;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #243: selling the gang-house deed must give the house up at the next cleanup. Stock never
// stores an owner — the nightly cleanup walks every player record and a house is owned only while a
// player in a gang carries its deed (ability 181). A house nobody carries the deed to is unowned: it
// is not taxed, every player loses that house's keys/emblems/keyrings (ability 183), and the deed goes
// back on the Realm Deed Shop shelf. Closing a house that way arms no re-buy lockout.
public sealed class GangHouseDeedReleaseTests
{
    private const int HouseId = 6;              // Blue
    private const int BlueDeedId = 1012;
    private const int BlueKeyId = 1032;
    private const int SapphireEmblemId = 1034;
    private const int BlueKeyringId = 2025;
    private const int DeedShopId = 124;
    private const int TaxGold = 1000;
    private const string Gang = "The Ashgrove";
    private const string EmblemSlot = "worn-16";

    private static (GameWorld World, InMemoryPlayerRepository Repo, ShopItem DeedSlot) CreateWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Square", Description = "x" };
        db.Items[BlueDeedId] = new Item
        {
            Number = BlueDeedId,
            Name = "blue parchment deed",
            Abilities = { [181] = HouseId, [182] = TaxGold, [183] = HouseId, [184] = HouseId },
        };
        db.Items[BlueKeyId] = new Item { Number = BlueKeyId, Name = "blue key", Abilities = { [183] = HouseId } };
        db.Items[SapphireEmblemId] = new Item { Number = SapphireEmblemId, Name = "sapphire emblem", Worn = 16, Abilities = { [183] = HouseId } };
        db.Items[BlueKeyringId] = new Item { Number = BlueKeyringId, Name = "blue keyring", Abilities = { [183] = HouseId } };

        var deedSlot = new ShopItem { ItemId = BlueDeedId, Max = 1 };
        db.Shops[DeedShopId] = new Shop { Number = DeedShopId, Name = "Realm Deed Shop", ShopType = GameWorld.DeedShopType, Items = { deedSlot } };

        var repo = new InMemoryPlayerRepository();
        repo.CreateGangRecord(Gang, 9, "Rook");
        return (new GameWorld(db, repo), repo, deedSlot);
    }

    private static Player SaveOffline(InMemoryPlayerRepository repo, string name, string gang, params int[] inventory)
    {
        var player = new Player { Name = name, Gang = gang, Inventory = inventory.ToList() };
        repo.SavePlayer(player);
        return player;
    }

    [Fact]
    public void Sold_deed_closes_the_house_at_cleanup_and_sweeps_its_items_from_every_player()
    {
        var (world, repo, deedSlot) = CreateWorld();
        // Rook bought the Blue house, then sold the deed back: he still has the key and keyring, and a
        // gang-mate still wears the emblem. He has plenty banked, so this is not a tax eviction.
        var rook = SaveOffline(repo, "Rook", Gang, BlueKeyId, BlueKeyringId);
        rook.BankBalances[8] = 10_000_000;
        var birch = SaveOffline(repo, "Birch", Gang, BlueKeyId);
        birch.Equipment[EmblemSlot] = SapphireEmblemId;
        world.AssignGangHouse(HouseId, Gang, "Rook", DateTime.UtcNow);
        Assert.Equal(0, deedSlot.Current);   // deed off the shelf while owned

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.False(world.IsGangHouseOwned(HouseId));
        Assert.Empty(repo.LoadGangHouses());
        Assert.Equal(10_000_000, repo.LoadPlayerByName("Rook")!.BankBalances[8]);   // no tax on a house nobody holds
        Assert.Empty(repo.LoadPlayerByName("Rook")!.Inventory);
        Assert.Empty(repo.LoadPlayerByName("Birch")!.Inventory);
        Assert.False(repo.LoadPlayerByName("Birch")!.Equipment.ContainsKey(EmblemSlot));
        Assert.Equal(1, deedSlot.Current);   // deed back on the Realm Deed Shop shelf
        Assert.Equal(0, repo.LoadPlayerByName("Rook")!.GangHouseFlags & Player.GangHouseDeedSoldFlag);   // closing arms no lockout
    }

    [Fact]
    public void House_whose_deed_is_still_carried_stays_owned_and_is_taxed()
    {
        var (world, repo, deedSlot) = CreateWorld();
        var rook = SaveOffline(repo, "Rook", Gang, BlueDeedId, BlueKeyId);
        rook.BankBalances[8] = 10_000_000;
        world.AssignGangHouse(HouseId, Gang, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.True(world.IsGangHouseOwned(HouseId));
        Assert.Equal(10_000_000 - TaxGold * 100L, repo.LoadPlayerByName("Rook")!.BankBalances[8]);
        Assert.Equal([BlueDeedId, BlueKeyId], repo.LoadPlayerByName("Rook")!.Inventory);
        Assert.Equal(0, deedSlot.Current);
    }

    [Fact]
    public void A_gang_mate_carrying_the_deed_keeps_the_house_owned_and_pays_the_tax()
    {
        var (world, repo, _) = CreateWorld();
        var rook = SaveOffline(repo, "Rook", Gang, BlueKeyId);
        rook.BankBalances[8] = 10_000_000;
        var birch = SaveOffline(repo, "Birch", Gang, BlueDeedId);
        birch.BankBalances[8] = 500_000;
        world.AssignGangHouse(HouseId, Gang, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.True(world.IsGangHouseOwned(HouseId));
        Assert.Equal("Birch", world.GetGangHouse(HouseId)!.OwnerPlayer);   // the carrier is the owner
        Assert.Equal(500_000 - TaxGold * 100L, repo.LoadPlayerByName("Birch")!.BankBalances[8]);
        Assert.Equal(10_000_000, repo.LoadPlayerByName("Rook")!.BankBalances[8]);   // the leader pays nothing
        Assert.Equal([BlueKeyId], repo.LoadPlayerByName("Rook")!.Inventory);
    }

    [Fact]
    public void A_deed_carried_by_a_player_outside_any_gang_does_not_keep_the_house()
    {
        var (world, repo, _) = CreateWorld();
        var rook = SaveOffline(repo, "Rook", Gang, BlueKeyId);
        rook.BankBalances[8] = 10_000_000;
        SaveOffline(repo, "Drifter", "", BlueDeedId);
        world.AssignGangHouse(HouseId, Gang, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.False(world.IsGangHouseOwned(HouseId));
        Assert.Empty(repo.LoadPlayerByName("Rook")!.Inventory);
        Assert.Empty(repo.LoadPlayerByName("Drifter")!.Inventory);   // the deed is a house item too
    }

    [Fact]
    public void Online_holders_lose_the_items_live_and_hear_no_tax_notice()
    {
        var (world, repo, _) = CreateWorld();
        var client = new RecordingGameClient();
        var rook = new Player
        {
            Name = "Rook",
            Gang = Gang,
            CurrentMapNumber = 1,
            CurrentRoomNumber = 1,
            Client = client,
            Inventory = [BlueKeyId, BlueKeyringId],
        };
        client.Player = rook;
        rook.Equipment[EmblemSlot] = SapphireEmblemId;
        rook.BankBalances[8] = 10_000_000;
        repo.SavePlayer(rook);
        world.AddPlayer(rook);
        world.AssignGangHouse(HouseId, Gang, "Rook", DateTime.UtcNow);

        world.ProcessGangHouseTax(DateTime.UtcNow);

        Assert.False(world.IsGangHouseOwned(HouseId));
        Assert.Empty(rook.Inventory);
        Assert.False(rook.Equipment.ContainsKey(EmblemSlot));
        Assert.DoesNotContain(client.Lines, l => l.Contains("failed to pay the tax"));
    }
}
