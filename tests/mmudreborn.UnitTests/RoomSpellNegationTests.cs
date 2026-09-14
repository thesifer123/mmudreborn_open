using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// When a room spell is about to land, stock scans every worn item's
// Negate Spells list (10 entries). Any match short-circuits the cast silently.
// Field-confirmed from the Nightmare Redux item editor screenshot: magma amulet (item 487)
// carries [526, 218] in its Negate Spells slots, blocking magma heat and temple-of-fire fire
// — phoenix feather (item 1000) has the same list. The DAT-export tool extracts these slots
// for every item that has them; here we pin the runtime gate that consumes the data.
public sealed class RoomSpellNegationTests
{
    [Fact]
    public void Player_wearing_negating_item_blocks_the_spell()
    {
        var world = CreateWorld(magmaAmulet: true);
        Assert.True(world.PlayerHasItemNegatingSpell(world.Player, spellId: 526));
    }

    [Fact]
    public void Negation_is_scoped_to_the_listed_spell_numbers()
    {
        // Magma amulet negates 526 + 218 but NOT magma drip (#115) or unrelated spells.
        var world = CreateWorld(magmaAmulet: true);
        Assert.True(world.PlayerHasItemNegatingSpell(world.Player, spellId: 218));
        Assert.False(world.PlayerHasItemNegatingSpell(world.Player, spellId: 115));
        Assert.False(world.PlayerHasItemNegatingSpell(world.Player, spellId: 999));
    }

    [Fact]
    public void Player_without_the_item_takes_the_spell()
    {
        var world = CreateWorld(magmaAmulet: false);
        Assert.False(world.PlayerHasItemNegatingSpell(world.Player, spellId: 526));
    }

    [Fact]
    public void Carried_but_unequipped_item_does_not_negate()
    {
        // The negate gate only iterates the worn-equipment slots.
        // A magma amulet sitting in inventory must NOT block the spell.
        var world = CreateWorld(magmaAmulet: false);
        world.Player.Inventory.Add(MagmaAmuletItemId);
        Assert.False(world.PlayerHasItemNegatingSpell(world.Player, spellId: 526));
    }

    [Fact]
    public void Zero_or_negative_spell_id_never_negates()
    {
        var world = CreateWorld(magmaAmulet: true);
        Assert.False(world.PlayerHasItemNegatingSpell(world.Player, spellId: 0));
        Assert.False(world.PlayerHasItemNegatingSpell(world.Player, spellId: -1));
    }

    private const int MagmaAmuletItemId = 487;

    private sealed record NegationWorld(GameWorld World, Player Player)
    {
        public bool PlayerHasItemNegatingSpell(Player player, int spellId)
            => World.PlayerHasItemNegatingSpell(player, spellId);
    }

    private static NegationWorld CreateWorld(bool magmaAmulet)
    {
        var database = new InMemoryGameDatabase();
        database.Items[MagmaAmuletItemId] = new Item
        {
            Number = MagmaAmuletItemId,
            Name = "magma amulet",
            NegatedSpellNumbers = { 526, 218 },
        };

        var player = new Player { Name = "TestSubject" };
        if (magmaAmulet)
            player.Equipment["neck"] = MagmaAmuletItemId;

        var world = new GameWorld(database, new InMemoryPlayerRepository());
        return new NegationWorld(world, player);
    }
}
