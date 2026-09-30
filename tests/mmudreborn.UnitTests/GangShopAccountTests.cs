using System;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Gang-shop takings follow the shop's account: STOCK writes the stocker's name into the shop record, and
// the deposit pays that name's bankbook 8 — not the gang leader's. The account is saved
// with the shop, so it survives a restart.
public sealed class GangShopAccountTests
{
    private const int ShopId = 126;
    private const int HouseId = 2;
    private const int SwordId = 100;

    private static (GameWorld World, InMemoryPlayerRepository Repo) CreateWorld(InMemoryPlayerRepository repo)
    {
        var db = new InMemoryGameDatabase();
        db.Shops[ShopId] = new Shop { Number = ShopId, Name = "Gang Shop #126", ShopType = GameWorld.GangShopType };
        db.Rooms[(15, 875)] = new Room { MapNumber = 15, RoomNumber = 875, Shop = ShopId, GangHouseId = HouseId };
        db.Items[SwordId] = new Item { Number = SwordId, Name = "iron sword", Price = 50, Currency = 0 };
        return (new GameWorld(db, repo), repo);
    }

    private static InMemoryPlayerRepository RepoWithGang()
    {
        var repo = new InMemoryPlayerRepository();
        repo.SavePlayer(new Player { Name = "Boss" });
        repo.SavePlayer(new Player { Name = "Stocker" });
        repo.CreateGangRecord("Reds", 9, "Boss");
        return repo;
    }

    private static long Bank8(InMemoryPlayerRepository repo, string name)
        => repo.LoadPlayerByName(name)!.BankBalances.GetValueOrDefault(GameWorld.GangLeaderBankNumber);

    [Fact]
    public void Takings_go_to_whoever_last_stocked_the_shop()
    {
        var (world, repo) = CreateWorld(RepoWithGang());
        world.AssignGangHouse(HouseId, "Reds", "Boss", DateTime.UtcNow);
        world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: false, price: 0, currencyGiven: -1);
        world.SetGangShopAccount(ShopId, "Stocker");

        world.DepositGangShopRevenue(ShopId, 1234);

        Assert.Equal(1234, Bank8(repo, "Stocker"));
        Assert.Equal(0, Bank8(repo, "Boss"));
    }

    [Fact]
    public void The_stockers_account_survives_a_restart()
    {
        var repo = RepoWithGang();
        var (world, _) = CreateWorld(repo);
        world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: false, price: 0, currencyGiven: -1);
        world.SetGangShopAccount(ShopId, "Stocker");
        world.PersistGangShop(ShopId);

        var (restarted, _) = CreateWorld(repo);
        restarted.DepositGangShopRevenue(ShopId, 500);

        Assert.Equal(500, Bank8(repo, "Stocker"));
    }
}
