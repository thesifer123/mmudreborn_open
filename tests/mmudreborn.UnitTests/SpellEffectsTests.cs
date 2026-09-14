using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// Pure spell-effect primitives (the stock cast-effect cases).
public sealed class SpellEffectsTests
{
    [Fact]
    public void Heal_caps_at_max_hp_and_returns_actual_delta()
    {
        var p = new Player { MaxHP = 100, CurrentHP = 90 };
        Assert.Equal(10, SpellEffects.ApplyHeal(p, 25));   // only 10 fits under the cap
        Assert.Equal(100, p.CurrentHP);
    }

    [Fact]
    public void Heal_applies_full_amount_below_cap()
    {
        var p = new Player { MaxHP = 100, CurrentHP = 40 };
        Assert.Equal(30, SpellEffects.ApplyHeal(p, 30));
        Assert.Equal(70, p.CurrentHP);
    }

    [Fact]
    public void Mana_restore_caps_at_max()
    {
        var p = new Player { MaxMana = 50, CurrentMana = 45 };
        Assert.Equal(5, SpellEffects.ApplyMana(p, 20));    // only 5 fits under the cap
        Assert.Equal(50, p.CurrentMana);
    }

    [Fact]
    public void Mana_restore_applies_full_amount_below_cap()
    {
        var p = new Player { MaxMana = 100, CurrentMana = 20 };
        Assert.Equal(35, SpellEffects.ApplyMana(p, 35));
        Assert.Equal(55, p.CurrentMana);
    }

    [Fact]
    public void Mana_drain_floors_at_zero()
    {
        var p = new Player { MaxMana = 50, CurrentMana = 10 };
        Assert.Equal(-10, SpellEffects.ApplyMana(p, -30));  // only 10 left to drain
        Assert.Equal(0, p.CurrentMana);
    }

    [Fact]
    public void Dispel_matches_source_carrying_the_ability()
    {
        var carrier = new GameSpell { Abilities = { [19] = 0 } };
        var other = new GameSpell { Abilities = { [22] = 5 } };
        Assert.True(SpellEffects.ShouldDispel(carrier, 19));
        Assert.False(SpellEffects.ShouldDispel(other, 19));
    }

    [Fact]
    public void Dispel_all_sentinel_matches_any_source()
    {
        var any = new GameSpell { Abilities = { [22] = 5 } };
        Assert.True(SpellEffects.ShouldDispel(any, -1));
        Assert.True(SpellEffects.ShouldDispel(any, 65535));
    }

    [Fact]
    public void Reduce_poison_lowers_level_and_returns_actual_reduction()
    {
        var p = new Player { PoisonLevel = 12 };
        Assert.Equal(5, SpellEffects.ReducePoison(p, 5));
        Assert.Equal(7, p.PoisonLevel);
    }

    [Fact]
    public void Reduce_poison_floors_at_zero()
    {
        var p = new Player { PoisonLevel = 4 };
        Assert.Equal(4, SpellEffects.ReducePoison(p, 10));   // only 4 to remove
        Assert.Equal(0, p.PoisonLevel);
    }

    // Bug #112: dispelling/curing a poison spell must reverse the poison accumulator it deposited (the
    // monster-bite case stores the rolled magnitude as the active spell's CastLevel and uses ability
    // value 0, so the reversal falls back to that magnitude).
    [Fact]
    public void Reverse_terminated_poison_uses_cast_magnitude_when_ability_value_is_zero()
    {
        var p = new Player { PoisonLevel = 12 };
        var bite = new GameSpell { Number = 80, Abilities = { [19] = 0 } }; // poison, value 0
        Assert.Equal(12, SpellEffects.ReverseTerminatedPoison(p, bite, castMagnitude: 12));
        Assert.Equal(0, p.PoisonLevel);
    }

    [Fact]
    public void Reverse_terminated_poison_prefers_explicit_ability_value()
    {
        var p = new Player { PoisonLevel = 12 };
        var spell = new GameSpell { Abilities = { [19] = 10 } }; // explicit poison magnitude 10
        Assert.Equal(10, SpellEffects.ReverseTerminatedPoison(p, spell, castMagnitude: 99));
        Assert.Equal(2, p.PoisonLevel);
    }

    [Fact]
    public void Reverse_terminated_poison_floors_at_zero()
    {
        var p = new Player { PoisonLevel = 5 };
        var bite = new GameSpell { Abilities = { [19] = 0 } };
        Assert.Equal(5, SpellEffects.ReverseTerminatedPoison(p, bite, castMagnitude: 12)); // only 5 to remove
        Assert.Equal(0, p.PoisonLevel);
    }

    [Fact]
    public void Reverse_terminated_poison_ignores_non_poison_spells()
    {
        var p = new Player { PoisonLevel = 8 };
        var buff = new GameSpell { Abilities = { [22] = 5 } }; // not a poison spell
        Assert.Equal(0, SpellEffects.ReverseTerminatedPoison(p, buff, castMagnitude: 12));
        Assert.Equal(8, p.PoisonLevel); // untouched
    }

    [Fact]
    public void Poison_or_disease_detects_status_markers()
    {
        Assert.True(SpellEffects.IsPoisonOrDisease(new GameSpell { Abilities = { [74] = 0 } }));
        Assert.True(SpellEffects.IsPoisonOrDisease(new GameSpell { Abilities = { [75] = 1 } }));
        Assert.False(SpellEffects.IsPoisonOrDisease(new GameSpell { Abilities = { [18] = 0 } }));
    }

    [Fact]
    public void Restore_energy_adds_to_the_pool_and_returns_delta()
    {
        var p = new Player { CurrentEnergy = 100 };
        Assert.Equal(50, SpellEffects.RestoreEnergy(p, 50));
        Assert.Equal(150, p.CurrentEnergy);
    }

    [Fact]
    public void Restore_energy_of_zero_is_a_noop()
    {
        var p = new Player { CurrentEnergy = 80 };
        Assert.Equal(0, SpellEffects.RestoreEnergy(p, 0));
        Assert.Equal(80, p.CurrentEnergy);
    }

    [Fact]
    public void Teach_spell_learns_an_unknown_spell_once()
    {
        var p = new Player();
        int abilityId = Player.GetLearnedSpellbookAbilityId(42);

        Assert.True(SpellEffects.TeachSpell(p, 42));
        Assert.Equal(1, p.GetQuestAbilityValue(abilityId));

        // Already known ⇒ no-op, returns false.
        Assert.False(SpellEffects.TeachSpell(p, 42));
        Assert.Equal(1, p.GetQuestAbilityValue(abilityId));
    }

    [Fact]
    public void Teach_spell_rejects_invalid_spell_number()
    {
        var p = new Player();
        Assert.False(SpellEffects.TeachSpell(p, 0));
        Assert.False(SpellEffects.TeachSpell(p, -3));
    }

    [Theory]
    [InlineData(100, -15, 116)]   // HP>=0: (100 - (-15)) + 1 ⇒ leaves HP at -16 (a kill)
    [InlineData(0, -15, 16)]      // exactly 0 still routes through the >=0 branch
    [InlineData(-5, -15, 15)]     // HP<0: damage = -deathHp
    public void Smite_vs_player_drops_victim_past_the_death_threshold(int currentHp, int deathHp, int expectedDamage)
    {
        int damage = SpellEffects.SmiteDamageVsPlayer(currentHp, deathHp);
        Assert.Equal(expectedDamage, damage);
        if (currentHp >= 0)
            Assert.Equal(deathHp - 1, currentHp - damage);   // resulting HP is one past the floor
    }

    [Fact]
    public void Smite_vs_monster_is_an_instant_kill()
    {
        Assert.Equal(52, SpellEffects.SmiteDamageVsMonster(50));   // HP + 2 ⇒ resulting HP -2
        Assert.Equal(2, SpellEffects.SmiteDamageVsMonster(0));
    }
}
