using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class ElementalDamageTests
{
    // The element -> resistance ability map.
    [Theory]
    [InlineData(0, 3)]    // cold
    [InlineData(1, 5)]    // hot/fire
    [InlineData(2, 65)]
    [InlineData(3, 66)]
    [InlineData(5, 147)]
    [InlineData(6, 21)]   // poison
    [InlineData(4, 0)]    // no elemental resistance
    [InlineData(7, 0)]    // unmapped -> none
    public void GetElementResistAbilityId_matches_the_stock_map(int attType, int expectedAbility)
    {
        Assert.Equal(expectedAbility, SpellResistanceMath.GetElementResistAbilityId(attType));
    }

    [Fact]
    public void Negative_resistance_increases_damage()
    {
        // Nekojin cold ability = -10 -> (100 - (-10)) * 100 / 100 = 110.
        Assert.Equal(110, SpellResistanceMath.ApplyElementalDamage(spellType: 0, attType: 0, baseDamage: 100, resistValue: -10));
    }

    [Fact]
    public void Positive_resistance_reduces_damage()
    {
        // Nekojin hot ability = +10 -> (100 - 10) * 100 / 100 = 90.
        Assert.Equal(90, SpellResistanceMath.ApplyElementalDamage(spellType: 0, attType: 1, baseDamage: 100, resistValue: 10));
    }

    [Fact]
    public void Spell_type_three_or_higher_bypasses_elemental_resistance()
    {
        Assert.Equal(100, SpellResistanceMath.ApplyElementalDamage(spellType: 3, attType: 0, baseDamage: 100, resistValue: 50));
    }

    [Fact]
    public void Unmapped_element_leaves_damage_unchanged()
    {
        Assert.Equal(100, SpellResistanceMath.ApplyElementalDamage(spellType: 0, attType: 4, baseDamage: 100, resistValue: 50));
    }
}
