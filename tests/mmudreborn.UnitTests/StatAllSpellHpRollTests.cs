using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// `stat all` Spells table: the Min/Max/Avg-per-round columns describe an HP roll. They are meaningful
// only for spells that actually change HP — damage (harm abilities 1/8/17/19/95) or healing (ability
// 18). Utility/buff spells store an unrelated magnitude in MinBase/MaxBase (blur's AC bonus,
// illuminate's item ref), so those columns must be blank rather than print a misleading number.
public sealed class StatAllSpellHpRollTests
{
    private static GameSpell SpellWith(params int[] abilityIds)
    {
        var s = new GameSpell { MinBase = 5, MaxBase = 5 };
        foreach (var id in abilityIds)
            s.Abilities[id] = 0;
        return s;
    }

    [Fact]
    public void Damage_spell_has_hp_roll()
    {
        // hellstorm (hsto) carries elemental harm ability 17.
        Assert.True(CommandParser.SpellHasHpRoll(SpellWith(17)));
        // drain (8), damage (1), poison (19), smite (95) also count.
        Assert.True(CommandParser.SpellHasHpRoll(SpellWith(1)));
        Assert.True(CommandParser.SpellHasHpRoll(SpellWith(8, 95)));
    }

    [Fact]
    public void Healing_spell_has_hp_roll()
    {
        // mend / major healing / godheal all carry ability 18 (Alter HP).
        Assert.True(CommandParser.SpellHasHpRoll(SpellWith(18)));
    }

    [Fact]
    public void Blur_and_illuminate_have_no_hp_roll()
    {
        // blur: protective shield (10) + message (115) + effect flags (122). No harm, no heal.
        Assert.False(CommandParser.SpellHasHpRoll(SpellWith(10, 115, 122)));
        // illuminate: ambient/area ability 148 only.
        Assert.False(CommandParser.SpellHasHpRoll(SpellWith(148)));
    }
}
