using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Ability 178 (Shadowform): the user-description path reads the ability value
// and, when nonzero, replaces the whole look (name header + appearance + equipment) with textblock
// #value — hiding a PVP target's identity and gear. The value is a TEXTBLOCK id, not a magnitude, so
// ApplyAbility ASSIGNS (last-wins) rather than sums: two ids must never add into a garbage third id.
// Death shroud item #1579 carries 178 = 9652; shadowform spell #130 carries 178 = 4157.
public sealed class ShadowformTests
{
    private static (Player player, InMemoryGameDatabase db, Race race, CharacterClass cls) Setup()
    {
        var db = new InMemoryGameDatabase();
        var race = new Race { Number = 1 };
        var cls = new CharacterClass { Number = 1 };
        db.Races[1] = race;
        db.Classes[1] = cls;
        var player = new Player { RaceId = 1, ClassId = 1 };
        return (player, db, race, cls);
    }

    [Fact]
    public void Worn_death_shroud_sets_shadowform_textblock()
    {
        var (player, db, race, cls) = Setup();
        db.Items[1579] = new Item
        {
            Number = 1579,
            Name = "death shroud",
            Worn = 11,
            Abilities = new Dictionary<int, int> { [178] = 9652 },
        };
        player.Equipment["torso"] = 1579;

        player.RecalculateStats(race, cls, db);

        Assert.Equal(9652, player.ShadowformTextblock);
    }

    [Fact]
    public void Active_shadowform_spell_sets_textblock()
    {
        var (player, db, race, cls) = Setup();
        db.Spells[130] = new GameSpell { Number = 130, Abilities = new Dictionary<int, int> { [178] = 4157 } };
        player.AddOrRefreshActiveSpell(130, castLevel: 40, duration: 20);

        player.RecalculateStats(race, cls, db);

        Assert.Equal(4157, player.ShadowformTextblock);
    }

    [Fact]
    public void Spell_wins_over_item_when_both_present_no_summing()
    {
        var (player, db, race, cls) = Setup();
        db.Items[1579] = new Item
        {
            Number = 1579,
            Worn = 11,
            Abilities = new Dictionary<int, int> { [178] = 9652 },
        };
        db.Spells[130] = new GameSpell { Number = 130, Abilities = new Dictionary<int, int> { [178] = 4157 } };
        player.Equipment["torso"] = 1579;
        player.AddOrRefreshActiveSpell(130, castLevel: 40, duration: 20);

        player.RecalculateStats(race, cls, db);

        // Items apply before active spells in RecalculateStats, so the spell's id wins — and it is a
        // single valid textblock, NOT the meaningless sum 9652 + 4157.
        Assert.Equal(4157, player.ShadowformTextblock);
        Assert.NotEqual(9652 + 4157, player.ShadowformTextblock);
    }

    [Fact]
    public void Removing_all_sources_clears_the_textblock()
    {
        var (player, db, race, cls) = Setup();
        db.Spells[130] = new GameSpell { Number = 130, Abilities = new Dictionary<int, int> { [178] = 4157 } };
        player.AddOrRefreshActiveSpell(130, castLevel: 40, duration: 20);
        player.RecalculateStats(race, cls, db);
        Assert.Equal(4157, player.ShadowformTextblock);

        player.RemoveActiveSpell(130);
        player.RecalculateStats(race, cls, db);

        Assert.Equal(0, player.ShadowformTextblock);   // re-derived every recalc, so it drops to 0
    }
}
