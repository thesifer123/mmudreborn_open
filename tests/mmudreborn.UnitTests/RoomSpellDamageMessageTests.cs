using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug followup to #room-spell-direct-cast: the damage announcement used to lead with the spell
// name ("magma heat strikes you for 31 damage!") which Megamud parsed as a monster hit and ended
// up adding "magma heat" to its monster DB. The line must lead with "You" / a player-perspective
// phrase. FormatRoomSpellDamageLine is the chooser: DescMsg.Line1 with %d → DescMsg.Line3 → stock
// per-AttType template → generic. Pinning the layered fallback so a regression in one slot
// doesn't silently land back on the monster-shaped string.
public sealed class RoomSpellDamageMessageTests
{
    [Fact]
    public void DescMsg_Line1_with_percent_d_is_substituted()
    {
        // Stock-shaped row: Line1 is the damage template stock would print.
        var (world, _) = CreateWorld(message: (66, "You are seared by the flames for %d damage!", "", ""));
        var spell = FireSpell(descMsgId: 66);

        Assert.Equal("You are seared by the flames for 31 damage!", world.FormatRoomSpellDamageLine(spell, 31));
    }

    [Fact]
    public void DescMsg_falls_back_to_Line3_wrapper_when_no_damage_template()
    {
        // ice-storm-style row: DescMsg.Line3 is a status phrase with no %d. Wrap with "(N damage)".
        var (world, _) = CreateWorld(message: (84, "You are no longer freezing.", "", "You are freezing!"));
        var spell = IceSpell(descMsgId: 84);

        Assert.Equal("You are freezing! (37 damage)", world.FormatRoomSpellDamageLine(spell, 37));
    }

    [Fact]
    public void Cast_message_b_is_the_primary_source_for_the_damage_line()
    {
        // Stock magma heat (#526) carries CastMsgB=1553 = "You are seared by the flames for %d
        // damage!" — the authoritative player-facing line. It must win over the (absent) DescMsg 66
        // and the synthetic per-AttType template, sourced straight from the real message data.
        var (world, _) = CreateWorld(message: (1553, "You are seared by the flames for %d damage!", "You are seared by the flames for %d damage!", ""));
        var spell = new GameSpell
        {
            Number = 526,
            Name = "magma heat",
            AttType = 1,
            CastMessageB = 1553,
            Abilities = { [1] = 0, [115] = 66 },
        };

        Assert.Equal("You are seared by the flames for 37 damage!", world.FormatRoomSpellDamageLine(spell, 37));
    }

    [Fact]
    public void Fire_attack_type_uses_seared_template_when_descmsg_missing()
    {
        // magma heat (#526): DescMsg=66 isn't populated in live game_data — the per-AttType stock
        // template handles this case so the line still flavors as fire damage.
        var (world, _) = CreateWorld();
        var spell = FireSpell(descMsgId: 66);

        Assert.Equal("You are seared by the flames for 45 damage!", world.FormatRoomSpellDamageLine(spell, 45));
    }

    [Fact]
    public void Cold_attack_type_uses_chilled_template()
    {
        var (world, _) = CreateWorld();
        var spell = new GameSpell { Number = 999, Name = "freezing water", AttType = 0, Abilities = { [1] = 0 } };

        Assert.Equal("You are chilled to the bone for 8 damage!", world.FormatRoomSpellDamageLine(spell, 8));
    }

    [Fact]
    public void Unknown_attack_type_uses_generic_player_perspective_line()
    {
        // Unmapped AttType — must still lead with "You" so Megamud doesn't pattern-match a monster
        // template. Spell name is intentionally omitted.
        var (world, _) = CreateWorld();
        var spell = new GameSpell { Number = 999, Name = "weird thing", AttType = 99, Abilities = { [1] = 0 } };

        string line = world.FormatRoomSpellDamageLine(spell, 5);

        Assert.StartsWith("You", line);
        Assert.DoesNotContain("weird thing", line);
    }

    private static (GameWorld World, Player Player) CreateWorld((int Number, string Line1, string Line2, string Line3)? message = null)
    {
        var database = new InMemoryGameDatabase();
        if (message.HasValue)
        {
            database.Messages[message.Value.Number] = new RoomMessage
            {
                Number = message.Value.Number,
                Line1 = message.Value.Line1,
                Line2 = message.Value.Line2,
                Line3 = message.Value.Line3,
            };
        }

        var world = new GameWorld(database, new InMemoryPlayerRepository());
        return (world, new Player { Name = "Tester" });
    }

    private static GameSpell FireSpell(int descMsgId) => new()
    {
        Number = 526,
        Name = "magma heat",
        AttType = 1,
        Abilities = { [1] = 0, [115] = descMsgId },
    };

    private static GameSpell IceSpell(int descMsgId) => new()
    {
        Number = 135,
        Name = "ice storm",
        AttType = 0,
        Abilities = { [1] = 0, [115] = descMsgId },
    };
}
