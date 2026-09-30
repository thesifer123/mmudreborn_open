using System;
using System.Linq;
using System.Threading.Tasks;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Buying and selling at the Realm Deed Shop (shop type 12), as stock's buy_item / sell_item gate it.
// Selling a deed back arms the "outstanding paper-work" lockout until the next cleanup (the deed returns
// to the shelf only then); buying needs the gang leader, no deed already carried and enough gang
// experience; a house somebody owns is just out of stock. A purchase prints the ordinary line and no more.
public sealed class GangHouseDeedShopTests
{
    private const int Blue = 6;
    private const int White = 10;
    private const int BlueDeedId = 1012;
    private const int WhiteDeedId = 1017;
    private const string Ashgrove = "The Ashgrove";
    private static readonly (int Map, int Room) DeedShopRoom = (1, 500);

    private sealed record Setup(GameWorld World, InMemoryPlayerRepository Repo, RecordingGameClient Client, Player Player, CommandParser Parser, ShopItem BlueSlot, ShopItem WhiteSlot);

    private static Setup CreateSetup(string name = "Rook", params int[] inventory)
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[DeedShopRoom] = new Room { MapNumber = DeedShopRoom.Map, RoomNumber = DeedShopRoom.Room, Name = "Realm Deed Shop", Description = "x", RoomType = Room.ShopRoomType, Shop = 124 };
        db.Items[BlueDeedId] = new Item { Number = BlueDeedId, Name = "blue parchment deed", Gettable = true, Price = 10, Abilities = { [181] = Blue, [182] = 1000, [183] = Blue, [184] = Blue } };
        db.Items[WhiteDeedId] = new Item { Number = WhiteDeedId, Name = "white parchment deed", Gettable = true, Price = 10, Abilities = { [181] = White, [182] = 2000, [183] = White, [184] = White } };
        // One of each deed on the shelf, as a boot seeds it (Current = Max).
        var blueSlot = new ShopItem { ItemId = BlueDeedId, Max = 1, Current = 1 };
        var whiteSlot = new ShopItem { ItemId = WhiteDeedId, Max = 1, Current = 1 };
        db.Shops[124] = new Shop { Number = 124, Name = "Realm Deed Shop", ShopType = GameWorld.DeedShopType, Items = { blueSlot, whiteSlot } };

        var repo = new InMemoryPlayerRepository();
        repo.CreateGangRecord(Ashgrove, 9, "Rook");
        var world = new GameWorld(db, repo);

        var client = new RecordingGameClient();
        var player = new Player
        {
            Name = name,
            Gang = Ashgrove,
            CurrentMapNumber = DeedShopRoom.Map,
            CurrentRoomNumber = DeedShopRoom.Room,
            Copper = 1000,
            Strength = 100,   // room in the pack for the deed
            Client = client,
            Inventory = inventory.ToList(),
        };
        client.Player = player;
        repo.SavePlayer(player);
        world.AddPlayer(player);
        return new Setup(world, repo, client, player, new CommandParser(client, world, player), blueSlot, whiteSlot);
    }

    [Fact]
    public async Task Selling_a_deed_back_locks_out_another_purchase_until_the_next_cleanup()
    {
        var s = CreateSetup("Rook", BlueDeedId);
        s.World.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        await s.Parser.ProcessCommand("sell blue");

        Assert.DoesNotContain(BlueDeedId, s.Player.Inventory);
        Assert.Equal(0, s.BlueSlot.Current);   // back on the shelf only at cleanup
        Assert.NotEqual(0, s.Player.GangHouseFlags & Player.GangHouseDeedSoldFlag);

        s.Client.Lines.Clear();
        await s.Parser.ProcessCommand("buy white");
        Assert.Contains(s.Client.Lines, l => l.Contains("Due to outstanding paper-work we are unable to provide you with another"));
        Assert.Contains(s.Client.Lines, l => l.Contains("property today. Please call back tomorrow!"));
        Assert.DoesNotContain(WhiteDeedId, s.Player.Inventory);

        s.World.ProcessGangHouseTax(DateTime.UtcNow);
        Assert.Equal(1, s.BlueSlot.Current);
        Assert.False(s.World.IsGangHouseOwned(Blue));

        s.Client.Lines.Clear();
        await s.Parser.ProcessCommand("buy white");
        Assert.Contains(WhiteDeedId, s.Player.Inventory);
        Assert.Equal(0, s.WhiteSlot.Current);
        var line = Assert.Single(s.Client.Lines, l => !string.IsNullOrWhiteSpace(l));
        Assert.StartsWith("You just bought white parchment deed for", line);   // and nothing more
        Assert.Equal(Ashgrove, s.World.GetGangHouse(White)?.OwnerGang);
    }

    [Fact]
    public async Task Only_the_gang_leader_may_buy_a_deed()
    {
        var s = CreateSetup("Birch");

        await s.Parser.ProcessCommand("buy white");

        Assert.Contains(s.Client.Lines, l => l.Contains("You must be a gang leader to purchase a gang house deed."));
        Assert.Empty(s.Player.Inventory);
    }

    [Fact]
    public async Task A_leader_already_carrying_a_deed_is_refused()
    {
        var s = CreateSetup("Rook", BlueDeedId);

        await s.Parser.ProcessCommand("buy white");

        Assert.Contains(s.Client.Lines, l => l.Contains("You are already the owner of a gang house."));
        Assert.Equal([BlueDeedId], s.Player.Inventory);
    }

    [Fact]
    public async Task A_second_house_is_allowed_while_a_gang_mate_carries_the_first_deed()
    {
        var s = CreateSetup("Rook");
        s.Repo.SavePlayer(new Player { Name = "Birch", Gang = Ashgrove, Inventory = [BlueDeedId] });
        s.World.AssignGangHouse(Blue, Ashgrove, "Rook", DateTime.UtcNow);

        await s.Parser.ProcessCommand("buy white");

        Assert.Contains(WhiteDeedId, s.Player.Inventory);
    }

    [Fact]
    public async Task The_gangs_experience_gates_the_purchase()
    {
        var s = CreateSetup("Rook");
        s.World.SetGangHouseMinimumExperience(5000);
        s.Player.GangExperience = 3000;
        s.Repo.SavePlayer(new Player { Name = "Birch", Gang = Ashgrove, GangExperience = 1000 });

        await s.Parser.ProcessCommand("buy white");
        Assert.Contains(s.Client.Lines, l => l.Contains("Your gang does not have enough experience for you to purchase a gang house now."));
        Assert.Empty(s.Player.Inventory);

        s.Repo.LoadPlayerByName("Birch")!.GangExperience = 2000;   // the gang now has 5000 between them
        await s.Parser.ProcessCommand("buy white");
        Assert.Contains(WhiteDeedId, s.Player.Inventory);
    }

    [Fact]
    public async Task A_house_somebody_owns_is_simply_out_of_stock()
    {
        var s = CreateSetup("Rook");
        s.World.AssignGangHouse(Blue, "The Hollow Crows", "Flint", DateTime.UtcNow);

        await s.Parser.ProcessCommand("buy blue");

        Assert.Contains(s.Client.Lines, l => l.Contains("You cannot buy blue parchment deed here!"));
        Assert.Empty(s.Player.Inventory);
    }
}
