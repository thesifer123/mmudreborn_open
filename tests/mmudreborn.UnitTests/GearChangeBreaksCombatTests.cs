using System.Threading.Tasks;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Changing gear mid-fight ends autocombat, as in stock: a successful wear, remove, wield or unready
// prints "*Combat Off*" to the player and "<name> breaks off combat." to the rest of the room, then stops
// the swing loop. A refused gear change leaves the fight running. We used to keep swinging.
public sealed class GearChangeBreaksCombatTests
{
    private const int HelmId = 500;
    private const int SwordId = 501;
    private const int TorchId = 502;

    [Fact]
    public async Task Wearing_armour_in_combat_breaks_it_before_the_wear_line()
    {
        var (parser, fighter, client, watcherClient) = Build();
        fighter.Inventory.Add(HelmId);

        await parser.ProcessCommand("wear helm");

        Assert.False(fighter.InCombat);
        Assert.Contains(HelmId, fighter.Equipment.Values);
        AssertInOrder(client, "*Combat Off*", "You are now wearing helm.");
        AssertInOrder(watcherClient, "Rook breaks off combat.", "Rook wears helm!");
    }

    [Fact]
    public async Task Wielding_a_weapon_in_combat_breaks_it_before_the_old_weapon_comes_off()
    {
        var (parser, fighter, client, watcherClient) = Build();
        fighter.Equipment["weapon"] = SwordId;
        fighter.Inventory.Add(SwordId + 10);

        await parser.ProcessCommand("wield axe");

        Assert.False(fighter.InCombat);
        AssertInOrder(client, "*Combat Off*", "You are now holding axe.");
        AssertInOrder(watcherClient, "Rook breaks off combat.", "Rook removes sword.", "Rook wields axe!");
    }

    [Fact]
    public async Task Removing_armour_in_combat_breaks_it_after_the_remove_line()
    {
        var (parser, fighter, client, watcherClient) = Build();
        fighter.Equipment["worn-1"] = HelmId;

        await parser.ProcessCommand("remove helm");

        Assert.False(fighter.InCombat);
        Assert.Contains(HelmId, fighter.Inventory);
        AssertInOrder(client, "You have removed helm.", "*Combat Off*");
        AssertInOrder(watcherClient, "Rook removes helm!", "Rook breaks off combat.");
    }

    [Fact]
    public async Task Removing_the_readied_weapon_in_combat_is_the_stock_unready_and_breaks_it()
    {
        var (parser, fighter, client, watcherClient) = Build();
        fighter.Equipment["weapon"] = SwordId;

        await parser.ProcessCommand("remove sword");

        Assert.False(fighter.InCombat);
        Assert.Contains(SwordId, fighter.Inventory);
        AssertInOrder(client, "You now have no weapon readied.", "*Combat Off*");
        Assert.DoesNotContain(client.Lines, line => line.Contains("You have removed sword."));
        AssertInOrder(watcherClient, "Rook removes sword.", "Rook breaks off combat.");
    }

    [Fact]
    public async Task A_refused_wear_leaves_the_fight_running()
    {
        var (parser, fighter, client, watcherClient) = Build();
        fighter.Inventory.Add(TorchId);

        await parser.ProcessCommand("wear torch");   // a light source "may not be worn!"

        Assert.True(fighter.InCombat);
        Assert.DoesNotContain(client.Lines, line => line.Contains("*Combat Off*"));
        Assert.DoesNotContain(watcherClient.Lines, line => line.Contains("breaks off combat"));
    }

    [Fact]
    public async Task Changing_gear_out_of_combat_says_nothing_about_combat()
    {
        var (parser, fighter, client, watcherClient) = Build();
        fighter.InCombat = false;
        fighter.Inventory.Add(HelmId);

        await parser.ProcessCommand("wear helm");

        Assert.Contains(HelmId, fighter.Equipment.Values);
        Assert.DoesNotContain(client.Lines, line => line.Contains("*Combat Off*"));
        Assert.DoesNotContain(watcherClient.Lines, line => line.Contains("breaks off combat"));
    }

    // A fighter in autocombat and a watcher in the same room. The fighter carries a helm (worn on the
    // head, 1), a sword and an axe (wielded weapons), and a torch.
    private static (CommandParser Parser, Player Fighter, RecordingGameClient Client, RecordingGameClient WatcherClient) Build()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1 };
        db.Classes[1] = new CharacterClass { Number = 1, Name = "Warrior" };
        db.Items[HelmId] = new Item { Number = HelmId, Name = "helm", ItemType = 0, Worn = 1 };
        db.Items[SwordId] = new Item { Number = SwordId, Name = "sword", ItemType = 1 };
        db.Items[SwordId + 10] = new Item { Number = SwordId + 10, Name = "axe", ItemType = 1 };
        db.Items[TorchId] = new Item { Number = TorchId, Name = "torch", ItemType = 0, Abilities = { [54] = 1 } };

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var client = new RecordingGameClient();
        var fighter = new Player
        {
            Name = "Rook", ClassId = 1, CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = 50, MaxHP = 50,
            Client = client, InCombat = true,
        };
        client.Player = fighter;
        world.AddPlayer(fighter);

        var watcherClient = new RecordingGameClient();
        var watcher = new Player
        {
            Name = "Wren", ClassId = 1, CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = 50, MaxHP = 50,
            Client = watcherClient,
        };
        watcherClient.Player = watcher;
        world.AddPlayer(watcher);

        return (new CommandParser(client, world, fighter), fighter, client, watcherClient);
    }

    private static void AssertInOrder(RecordingGameClient client, params string[] fragments)
    {
        int last = -1;
        foreach (var fragment in fragments)
        {
            int index = client.Lines.FindIndex(last + 1, line => line.Contains(fragment));
            Assert.True(index > last, $"'{fragment}' missing or out of order in: {string.Join(" | ", client.Lines)}");
            last = index;
        }
    }
}
