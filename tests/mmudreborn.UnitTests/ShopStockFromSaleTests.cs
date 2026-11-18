using System.Threading.Tasks;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Shops start each slot at stock's shipped quantity (ShopItem.Initial), not full, and a sale puts the sold
// item on the shelf: every slot listing it below its max gains one. Stock ships slots at zero with no
// restock, so the only way such an item is ever for sale is a player selling one there. We used to start every slot full and never take stock back from a sale.
public sealed class ShopStockFromSaleTests
{
    private const int ShopId = 300;
    private const int GreatswordId = 400;

    [Fact]
    public async Task A_zero_stock_item_cannot_be_bought_until_a_player_sells_one_there()
    {
        var (world, parser, player, client, slot) = Build(initial: 0, max: 2);
        player.Inventory.Add(GreatswordId);

        await parser.ProcessCommand("buy greatsword");
        Assert.Contains(client.Lines, line => line.Contains("You cannot buy greatsword here!"));
        Assert.Equal(0, slot.Current);

        await parser.ProcessCommand("sell greatsword");
        Assert.Equal(1, slot.Current);
        Assert.DoesNotContain(GreatswordId, player.Inventory);

        await parser.ProcessCommand("buy greatsword");
        Assert.Contains(GreatswordId, player.Inventory);
        Assert.Equal(0, slot.Current);
    }

    [Fact]
    public async Task Sales_fill_the_shelf_only_up_to_max()
    {
        var (_, parser, player, _, slot) = Build(initial: 2, max: 2);
        player.Inventory.Add(GreatswordId);

        await parser.ProcessCommand("sell greatsword");

        Assert.DoesNotContain(GreatswordId, player.Inventory);   // the sale still goes through
        Assert.Equal(2, slot.Current);
    }

    [Fact]
    public async Task A_sell_only_slot_never_takes_stock()
    {
        var (_, parser, player, _, slot) = Build(initial: 0, max: 0);
        player.Inventory.Add(GreatswordId);

        await parser.ProcessCommand("sell greatsword");

        Assert.DoesNotContain(GreatswordId, player.Inventory);
        Assert.Equal(0, slot.Current);
    }

    [Fact]
    public async Task Appraising_changes_no_stock()
    {
        var (_, parser, player, _, slot) = Build(initial: 0, max: 2);
        player.Inventory.Add(GreatswordId);

        await parser.ProcessCommand("appraise greatsword");

        Assert.Contains(GreatswordId, player.Inventory);
        Assert.Equal(0, slot.Current);
    }

    [Fact]
    public void The_deed_shop_takes_no_stock_from_a_sale()
    {
        var (world, _, _, _, slot) = Build(initial: 0, max: 1, shopType: GameWorld.DeedShopType);

        world.AddShopStockFromSale(world.Database.Shops[ShopId], GreatswordId);

        Assert.Equal(0, slot.Current);
    }

    [Fact]
    public void A_slot_sold_up_to_max_above_its_starting_stock_survives_a_restart()
    {
        var repo = new InMemoryPlayerRepository();
        var (world1, slot1) = BootShop(repo);
        world1.AddShopStockFromSale(world1.Database.Shops[ShopId], GreatswordId);
        world1.AddShopStockFromSale(world1.Database.Shops[ShopId], GreatswordId);
        Assert.Equal(2, slot1.Current);   // full — the old "only persist below max" rule dropped this
        world1.PersistShopStock();

        var (_, slot2) = BootShop(repo);
        Assert.Equal(2, slot2.Current);
    }

    [Fact]
    public void Resetting_shop_stock_returns_each_slot_to_its_starting_stock_not_to_max()
    {
        var (world, _, _, _, slot) = Build(initial: 0, max: 2);
        slot.Current = 2;

        world.ResetShopStock();

        Assert.Equal(0, slot.Current);
    }

    // A fresh boot of a shop whose greatsword slot starts empty (Initial 0, Max 2), sharing `repo`'s
    // ServerSettings so persisted stock carries across "restarts".
    private static (GameWorld World, ShopItem Slot) BootShop(InMemoryPlayerRepository repo)
    {
        var db = new InMemoryGameDatabase();
        var slot = new ShopItem { ItemId = GreatswordId, Max = 2, Initial = 0, Current = 0 };
        db.Shops[ShopId] = new Shop { Number = ShopId, Name = "Weapon Shop", ShopType = 1, Items = { slot } };
        db.Items[GreatswordId] = new Item { Number = GreatswordId, Name = "greatsword", ItemType = 1 };
        return (new GameWorld(db, repo), slot);
    }

    private static (GameWorld World, CommandParser Parser, Player Player, RecordingGameClient Client, ShopItem Slot) Build(
        int initial, int max, int shopType = 1)
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, RoomType = Room.ShopRoomType, Shop = ShopId };
        db.Items[GreatswordId] = new Item { Number = GreatswordId, Name = "greatsword", ItemType = 1, Price = 0 };
        var slot = new ShopItem { ItemId = GreatswordId, Max = max, Initial = initial, Current = initial };
        db.Shops[ShopId] = new Shop { Number = ShopId, Name = "Weapon Shop", ShopType = shopType, Items = { slot } };

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var client = new RecordingGameClient();
        var player = new Player { Name = "Rook", CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = 50, MaxHP = 50, Client = client };
        client.Player = player;
        world.AddPlayer(player);
        return (world, new CommandParser(client, world, player), player, client, slot);
    }
}
