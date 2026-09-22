using System;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Gang-shop owner-managed stock engine (STOCK/UNSTOCK/MARKUP + buy revenue). A shop
// has ten slots, twenty of each item; the deed (ability 184 == house id) is the controller; stock and
// markup persist; buying deposits to the owning gang leader's bank; eviction empties the shop.
public sealed class GangShopStockTests
{
    private const int ShopId = 126;
    private const int HouseId = 2;
    private const int SwordId = 100;
    private const int RingId = 200;
    private const int DeedId = 1009;

    private static (GameWorld World, InMemoryPlayerRepository Repo, InMemoryGameDatabase Db) CreateWorld(
        InMemoryPlayerRepository? sharedRepo = null)
    {
        var db = new InMemoryGameDatabase();
        db.Shops[ShopId] = new Shop { Number = ShopId, Name = "Gang Shop #126", ShopType = GameWorld.GangShopType };
        db.Rooms[(15, 875)] = new Room { MapNumber = 15, RoomNumber = 875, Shop = ShopId, GangHouseId = HouseId };
        db.Items[SwordId] = new Item { Number = SwordId, Name = "iron sword", Price = 50, Currency = 0 };
        db.Items[RingId] = new Item { Number = RingId, Name = "gold ring", Price = 10, Currency = 2 };
        db.Items[DeedId] = new Item { Number = DeedId, Name = "orange parchment deed", Abilities = { [184] = HouseId } };

        var repo = sharedRepo ?? new InMemoryPlayerRepository();
        return (new GameWorld(db, repo), repo, db);
    }

    [Fact]
    public void Stocking_creates_a_slot_then_grows_it()
    {
        var (world, _, _) = CreateWorld();

        Assert.Equal(0, world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: false, price: 0, currencyGiven: -1));
        Assert.Equal(0, world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: false, price: 0, currencyGiven: -1));

        var slots = world.SnapshotGangShopSlots(ShopId);
        var slot = Assert.Single(slots);
        Assert.Equal(SwordId, slot.ItemId);
        Assert.Equal(2, slot.Quantity);
        Assert.Equal(50, slot.Price);     // item default price
        Assert.Equal(0, slot.Currency);
    }

    [Fact]
    public void Explicit_price_and_currency_override_the_item_defaults_each_stock()
    {
        var (world, _, _) = CreateWorld();

        world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: true, price: 500, currencyGiven: 2);
        var slot = Assert.Single(world.SnapshotGangShopSlots(ShopId));
        Assert.Equal(500, slot.Price);
        Assert.Equal(2, slot.Currency);
    }

    [Fact]
    public void Slot_caps_at_twenty_then_spills_into_a_second_slot()
    {
        var (world, _, _) = CreateWorld();

        for (int i = 0; i < GameWorld.GangShopSlotMaxQuantity + 1; i++)
            world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: false, price: 0, currencyGiven: -1);

        var slots = world.SnapshotGangShopSlots(ShopId);
        Assert.Equal(2, slots.Count);
        Assert.Equal(GameWorld.GangShopSlotMaxQuantity, slots[0].Quantity);
        Assert.Equal(1, slots[1].Quantity);
    }

    [Fact]
    public void All_ten_slots_full_of_distinct_items_refuses_the_eleventh()
    {
        var (world, _, db) = CreateWorld();
        for (int i = 0; i < GameWorld.GangShopSlotCount; i++)
        {
            int id = 300 + i;
            db.Items[id] = new Item { Number = id, Name = $"trinket {i}", Price = 1, Currency = 0 };
            Assert.True(world.StockGangShopItem(ShopId, id, 1, 0, false, 0, -1) >= 0);
        }

        db.Items[999] = new Item { Number = 999, Name = "one too many", Price = 1, Currency = 0 };
        Assert.Equal(-1, world.StockGangShopItem(ShopId, 999, 1, 0, false, 0, -1));
    }

    [Fact]
    public void Unstock_returns_one_and_clears_the_slot_at_zero()
    {
        var (world, _, _) = CreateWorld();
        world.StockGangShopItem(ShopId, SwordId, 50, 0, false, 0, -1);

        Assert.Equal(SwordId, world.ResolveGangShopItemByName(ShopId, "iron", out _));
        Assert.True(world.UnstockGangShopItem(ShopId, SwordId));
        Assert.Empty(world.SnapshotGangShopSlots(ShopId));
        Assert.Equal(0, world.ResolveGangShopItemByName(ShopId, "iron", out _)); // nothing left
        Assert.False(world.UnstockGangShopItem(ShopId, SwordId));
    }

    // Stock shop-item lookup: a word-prefix match (never a mid-word substring), an exact name wins
    // outright, and two different loose matches are ambiguous.
    [Fact]
    public void Resolve_by_name_is_word_prefix_exact_wins_and_reports_ambiguity()
    {
        var (world, _, db) = CreateWorld();
        db.Items[401] = new Item { Number = 401, Name = "topaz stone", Price = 1, Currency = 0 };
        db.Items[402] = new Item { Number = 402, Name = "hematite stone", Price = 1, Currency = 0 };
        db.Items[403] = new Item { Number = 403, Name = "stone", Price = 1, Currency = 0 };
        world.StockGangShopItem(ShopId, 402, 1, 0, false, 0, -1);
        world.StockGangShopItem(ShopId, 401, 1, 0, false, 0, -1);

        Assert.Equal(401, world.ResolveGangShopItemByName(ShopId, "t", out var none));
        Assert.Null(none);

        Assert.Equal(0, world.ResolveGangShopItemByName(ShopId, "st", out var ambiguous));
        Assert.Equal(new[] { "hematite stone", "topaz stone" }, ambiguous);

        world.StockGangShopItem(ShopId, 403, 1, 0, false, 0, -1);
        Assert.Equal(403, world.ResolveGangShopItemByName(ShopId, "stone", out var exactNone));
        Assert.Null(exactNone);
    }

    [Fact]
    public void UnstockAll_flattens_and_empties_every_slot()
    {
        var (world, _, _) = CreateWorld();
        world.StockGangShopItem(ShopId, SwordId, 50, 0, false, 0, -1);
        world.StockGangShopItem(ShopId, SwordId, 50, 0, false, 0, -1);
        world.StockGangShopItem(ShopId, RingId, 10, 2, false, 0, -1);

        var dropped = world.UnstockAllGangShopItems(ShopId);

        Assert.Equal(3, dropped.Count);
        Assert.Equal(2, dropped.Count(id => id == SwordId));
        Assert.Equal(1, dropped.Count(id => id == RingId));
        Assert.Empty(world.SnapshotGangShopSlots(ShopId));
    }

    [Fact]
    public void Markup_round_trips_and_clamps_to_the_stock_range()
    {
        var (world, _, _) = CreateWorld();

        world.SetGangShopMarkup(ShopId, 250);
        Assert.Equal(250, world.GetGangShopMarkup(ShopId));

        world.SetGangShopMarkup(ShopId, 99999);
        Assert.Equal(GameWorld.GangShopMaxMarkupPercent, world.GetGangShopMarkup(ShopId));

        world.SetGangShopMarkup(ShopId, -5);
        Assert.Equal(0, world.GetGangShopMarkup(ShopId));
    }

    [Fact]
    public void Controller_check_requires_the_matching_deed_in_inventory()
    {
        var (world, _, _) = CreateWorld();

        var withDeed = new Player();
        withDeed.Inventory.Add(DeedId);
        Assert.True(world.PlayerControlsGangShop(withDeed, HouseId));
        Assert.False(world.PlayerControlsGangShop(withDeed, 3)); // wrong house

        var withoutDeed = new Player();
        Assert.False(world.PlayerControlsGangShop(withoutDeed, HouseId));
    }

    [Fact]
    public void Shop_to_house_map_is_built_from_the_rooms()
    {
        var (world, _, _) = CreateWorld();
        Assert.Equal(HouseId, world.GetGangShopHouseId(ShopId));
        Assert.Equal(0, world.GetGangShopHouseId(99999));
    }

    [Fact]
    public void Stock_and_markup_persist_across_a_reload()
    {
        var repo = new InMemoryPlayerRepository();
        var (world, _, _) = CreateWorld(repo);
        world.SetGangShopMarkup(ShopId, 150);
        world.StockGangShopItem(ShopId, SwordId, 50, 0, priceGiven: true, price: 75, currencyGiven: 1);
        world.PersistGangShop(ShopId);

        // Fresh world, same repo — the persisted state must come back.
        var (reloaded, _, _) = CreateWorld(repo);
        Assert.Equal(150, reloaded.GetGangShopMarkup(ShopId));
        var slot = Assert.Single(reloaded.SnapshotGangShopSlots(ShopId));
        Assert.Equal(SwordId, slot.ItemId);
        Assert.Equal(1, slot.Quantity);
        Assert.Equal(75, slot.Price);
        Assert.Equal(1, slot.Currency);
    }

    [Fact]
    public void Emptying_a_shop_deletes_its_persisted_row()
    {
        var repo = new InMemoryPlayerRepository();
        var (world, _, _) = CreateWorld(repo);
        world.StockGangShopItem(ShopId, SwordId, 50, 0, false, 0, -1);
        world.PersistGangShop(ShopId);
        Assert.Single(repo.LoadGangShops());

        world.UnstockGangShopItem(ShopId, SwordId);
        world.PersistGangShop(ShopId);

        Assert.Empty(repo.LoadGangShops());
    }

    [Fact]
    public void ClearGangShopsForHouse_empties_and_deletes_the_houses_shops()
    {
        var repo = new InMemoryPlayerRepository();
        var (world, _, _) = CreateWorld(repo);
        world.StockGangShopItem(ShopId, SwordId, 50, 0, false, 0, -1);
        world.PersistGangShop(ShopId);

        world.ClearGangShopsForHouse(HouseId);

        Assert.Empty(world.SnapshotGangShopSlots(ShopId));
        Assert.Empty(repo.LoadGangShops());
    }

    [Fact]
    public void Buy_revenue_is_deposited_to_the_offline_gang_leaders_bank()
    {
        var repo = new InMemoryPlayerRepository();
        var leader = new Player { Name = "Boss" };
        repo.SavePlayer(leader);
        repo.CreateGangRecord("Reds", 9, "Boss");

        var (world, _, _) = CreateWorld(repo);
        world.AssignGangHouse(HouseId, "Reds", "Boss", DateTime.UtcNow);

        world.DepositGangShopRevenue(ShopId, 1234);

        var reloadedLeader = repo.LoadPlayerByName("Boss");
        Assert.NotNull(reloadedLeader);
        Assert.Equal(1234, reloadedLeader!.BankBalances.GetValueOrDefault(GameWorld.GangLeaderBankNumber));
    }

    [Fact]
    public void Deposit_is_a_noop_when_the_house_is_unowned()
    {
        var repo = new InMemoryPlayerRepository();
        var leader = new Player { Name = "Boss" };
        repo.SavePlayer(leader);
        repo.CreateGangRecord("Reds", 9, "Boss");

        var (world, _, _) = CreateWorld(repo);
        // No AssignGangHouse — the house is unowned, so takings go nowhere.
        world.DepositGangShopRevenue(ShopId, 1234);

        Assert.Equal(0, repo.LoadPlayerByName("Boss")!.BankBalances.GetValueOrDefault(GameWorld.GangLeaderBankNumber));
    }
}
