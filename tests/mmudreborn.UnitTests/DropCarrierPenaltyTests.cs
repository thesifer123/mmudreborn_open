using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The drop-carrier penalty. Stock does NOT leave a hung-up character
// standing in the room to be beaten to death: huprou penalizes the drop and takes the character out of the
// world immediately. These cover the penalty itself and, just as importantly, the cases where it must NOT
// fire — it ships defaulted to NONE, and a session the BOARD replaced never lost carrier at all.
public sealed class DropCarrierPenaltyTests
{
    private const int PlainSwordId = 500;
    private const int LoyalRingId = 501;

    private static (GameWorld World, InMemoryGameDatabase Db) NewWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Items[PlainSwordId] = new Item { Number = PlainSwordId, Name = "a plain sword" };
        db.Items[LoyalRingId] = new Item
        {
            Number = LoyalRingId,
            Name = "a loyal ring",
            Abilities = { [100] = 1 },   // ability 100 = Loyal Item, exempt from huprou's drop loops
        };

        return (new GameWorld(db, new InMemoryPlayerRepository()), db);
    }

    private static Player NewPlayer(string name, int currentHp = 200, int maxHp = 200)
    {
        var player = new Player
        {
            Name = name,
            CurrentMapNumber = 1,
            CurrentRoomNumber = 1,
            MaxHP = maxHp,
            CurrentHP = currentHp,
        };

        player.Inventory.Add(PlainSwordId);
        player.InventoryInstanceIds.Add(1);
        return player;
    }

    [Fact]
    public void Defaults_to_none_so_a_dropped_player_loses_nothing()
    {
        var (world, _) = NewWorld();
        var player = NewPlayer("Idle");
        world.AddPlayer(player);

        Assert.Equal(GameWorld.DisconnectPenaltyNone, world.DisconnectPenaltyLevel);
        Assert.False(world.ApplyDropCarrierPenalty(player));
        Assert.Equal(200, player.CurrentHP);
        Assert.Contains(PlainSwordId, player.Inventory);
        Assert.False(player.DisconnectedWhilePlaying);
    }

    [Fact]
    public void High_costs_hp_drops_gear_and_flags_the_character_for_their_next_login()
    {
        var (world, _) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyHigh);
        world.SetDisconnectPenaltyHpPercents(10, 25);
        world.SetDisconnectPenaltyMaxItemsDropped(3);

        var player = NewPlayer("Dropper");
        world.AddPlayer(player);

        Assert.False(world.ApplyDropCarrierPenalty(player));   // wounded, not killed

        // 10-25% of a 200 MaxHP pool = 20..50 HP.
        Assert.InRange(player.CurrentHP, 150, 180);
        Assert.DoesNotContain(PlainSwordId, player.Inventory);
        Assert.Contains(world.GetVisibleGroundItems(1, 1), entry => entry.ItemId == PlainSwordId);

        // Nobody is on the socket to be told, so stock records it and the login sequence delivers the
        // "gods have punished you appropriately" line.
        Assert.True(player.DisconnectedWhilePlaying);
    }

    [Fact]
    public void A_loyal_item_is_never_dropped()
    {
        var (world, _) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyHigh);
        world.SetDisconnectPenaltyMaxItemsDropped(5);

        var player = NewPlayer("Faithful");
        player.Inventory.Add(LoyalRingId);
        player.InventoryInstanceIds.Add(2);
        world.AddPlayer(player);

        world.ApplyDropCarrierPenalty(player);

        Assert.Contains(LoyalRingId, player.Inventory);
        Assert.DoesNotContain(PlainSwordId, player.Inventory);
        Assert.DoesNotContain(world.GetVisibleGroundItems(1, 1), entry => entry.ItemId == LoyalRingId);
    }

    [Fact]
    public void The_item_budget_is_respected()
    {
        var (world, db) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyHigh);
        world.SetDisconnectPenaltyMaxItemsDropped(2);

        var player = NewPlayer("Hoarder");
        for (int extra = 0; extra < 4; extra++)
        {
            player.Inventory.Add(PlainSwordId);
            player.InventoryInstanceIds.Add(10 + extra);
        }
        world.AddPlayer(player);

        world.ApplyDropCarrierPenalty(player);

        Assert.Equal(3, player.Inventory.Count);   // started with 5, budget of 2 spent
        Assert.Equal(player.Inventory.Count, player.InventoryInstanceIds.Count);
    }

    // MEDIUM is the inside-autocombat or being-attacked gate. A player whose link dies standing
    // in a safe room loses nothing; the same player mid-fight pays.
    [Fact]
    public void Medium_spares_a_player_who_was_not_fighting()
    {
        var (world, _) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyMedium);

        var bystander = NewPlayer("Safe");
        world.AddPlayer(bystander);
        Assert.False(world.ApplyDropCarrierPenalty(bystander));
        Assert.Equal(200, bystander.CurrentHP);
        Assert.False(bystander.DisconnectedWhilePlaying);

        var fighter = NewPlayer("Fighting");
        fighter.InCombat = true;
        world.AddPlayer(fighter);
        world.ApplyDropCarrierPenalty(fighter);
        Assert.True(fighter.CurrentHP < 200);
        Assert.True(fighter.DisconnectedWhilePlaying);
    }

    // huprou adds the (negative) death floor to an already-unconscious player's HP rather than taking a
    // percentage, then lets the kill check finish them: hanging up mid-bleed-out is not an escape.
    [Fact]
    public void Dropping_while_bleeding_out_is_fatal()
    {
        var (world, _) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyHigh);

        var player = NewPlayer("Bleeding", currentHp: -3);
        world.AddPlayer(player);

        Assert.True(world.ApplyDropCarrierPenalty(player));
        Assert.True(player.CurrentHP <= Player.DeathHP);
    }

    // The distinction the whole thing turns on. A duplicate login, a sysop kick and a board shutdown all
    // arrive at HandleDroppedConnection, but the board closed those sockets — no carrier was lost, so no
    // penalty. Without this, the login-time stale-session kick that clears a reconnecting player's ghost
    // would itself fine them for reconnecting.
    [Fact]
    public void A_connection_the_board_closed_is_not_a_carrier_drop()
    {
        var (world, _) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyHigh);

        var player = NewPlayer("Replaced");
        world.AddPlayer(player);

        var client = new RecordingGameClient { Player = player, DisconnectedByHost = true };
        client.AppData = client;
        player.Client = client;

        world.HandleDroppedConnection(client);

        Assert.False(player.DisconnectedWhilePlaying);
        Assert.Equal(200, player.CurrentHP);
        Assert.Contains(PlainSwordId, player.Inventory);
        Assert.Null(world.FindOnlinePlayer("Replaced"));   // still removed from the world, just unpunished
    }

    [Fact]
    public void A_peer_that_vanished_is_a_carrier_drop()
    {
        var (world, _) = NewWorld();
        world.SetDisconnectPenaltyLevel(GameWorld.DisconnectPenaltyHigh);

        var player = NewPlayer("Vanished");
        world.AddPlayer(player);

        var client = new RecordingGameClient { Player = player };   // DisconnectedByHost stays false
        client.AppData = client;
        player.Client = client;

        world.HandleDroppedConnection(client);

        Assert.True(player.DisconnectedWhilePlaying);
        Assert.True(player.CurrentHP < 200);
        Assert.Null(world.FindOnlinePlayer("Vanished"));
    }
}
