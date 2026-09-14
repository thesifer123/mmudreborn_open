using mmudreborn.Data.Models;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Port verification for the per-slot shop restock decision:
// when a due slot is below Max, genrdn(1,100) < Percent adds Amount, clamped to Max.
public sealed class ShopRestockTests
{
    private static ShopItem Slot(int current, int max, int amount, int percent) => new()
    {
        ItemId = 100,
        Current = current,
        Max = max,
        Amount = amount,
        Percent = percent,
    };

    [Fact]
    public void Roll_below_percent_adds_the_restock_amount()
    {
        var item = Slot(current: 0, max: 10, amount: 3, percent: 100);
        GameWorld.ApplyDueShopRestock(item, roll: 50);
        Assert.Equal(3, item.Current);
    }

    [Fact]
    public void Roll_at_or_above_percent_does_not_restock()
    {
        var item = Slot(current: 2, max: 10, amount: 3, percent: 40);
        GameWorld.ApplyDueShopRestock(item, roll: 40);   // genrdn(1,100) < Percent is strict
        Assert.Equal(2, item.Current);
        GameWorld.ApplyDueShopRestock(item, roll: 99);
        Assert.Equal(2, item.Current);
    }

    [Fact]
    public void Restock_clamps_to_max()
    {
        var item = Slot(current: 9, max: 10, amount: 5, percent: 100);
        GameWorld.ApplyDueShopRestock(item, roll: 1);
        Assert.Equal(10, item.Current);   // 9 + 5 clamped to 10
    }

    [Fact]
    public void A_full_slot_is_left_untouched()
    {
        var item = Slot(current: 10, max: 10, amount: 5, percent: 100);
        GameWorld.ApplyDueShopRestock(item, roll: 1);
        Assert.Equal(10, item.Current);
    }

    private static (GameWorld World, ShopItem Slot) BootShopWorld(InMemoryPlayerRepository repo, int shopId, int itemId, int current, int max)
    {
        var db = new InMemoryGameDatabase();
        var slot = new ShopItem { ItemId = itemId, Current = current, Max = max, Amount = 1, Percent = 50, Time = 60 };
        db.Shops[shopId] = new Shop { Number = shopId, Name = "Test Shop", ShopType = 0, Items = { slot } };
        return (new GameWorld(db, repo), slot);
    }

    [Fact]
    public void Depleted_shop_stock_survives_a_restart()
    {
        const int shopId = 200, itemId = 555;
        var repo = new InMemoryPlayerRepository();

        // Boot full (Current=Max=10), a player buys 4, then a graceful save.
        var (world1, slot1) = BootShopWorld(repo, shopId, itemId, current: 10, max: 10);
        for (int i = 0; i < 4; i++)
            world1.ConsumeShopStock(slot1);
        Assert.Equal(6, slot1.Current);
        world1.PersistShopStock();

        // Fresh boot: its DB seeds the slot full again — the constructor's LoadPersistedShopStock (via the
        // shared repo's ServerSettings) must restore the depletion rather than leave it refilled to 10.
        var (_, slot2) = BootShopWorld(repo, shopId, itemId, current: 10, max: 10);
        Assert.Equal(6, slot2.Current);
    }

    [Fact]
    public void A_reordered_or_reidentified_slot_is_not_mis_restored()
    {
        const int shopId = 201;
        var repo = new InMemoryPlayerRepository();

        // Deplete item 555 and persist.
        var (world1, slot1) = BootShopWorld(repo, shopId, itemId: 555, current: 10, max: 10);
        for (int i = 0; i < 3; i++)
            world1.ConsumeShopStock(slot1);
        world1.PersistShopStock();

        // The shop was edited so that slot now holds a DIFFERENT item — the guard skips it, leaving the
        // new item at its full-boot stock instead of applying 555's saved quantity.
        var (_, slot2) = BootShopWorld(repo, shopId, itemId: 999, current: 10, max: 10);
        Assert.Equal(10, slot2.Current);
    }
}
