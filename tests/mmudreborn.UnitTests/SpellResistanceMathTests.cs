using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class SpellResistanceMathTests
{
    [Fact]
    public void Damage_minus_mr_increases_damage_below_fifty_mr()
    {
        int adjusted = SpellResistanceMath.ApplyDamageMinusMagicResistance(10, 30, targetHasAntiMagic: false);

        Assert.Equal(12, adjusted);
    }

    [Fact]
    public void Damage_minus_mr_uses_percentage_reduction_above_fifty_mr()
    {
        int adjusted = SpellResistanceMath.ApplyDamageMinusMagicResistance(20, 70, targetHasAntiMagic: false);

        Assert.Equal(18, adjusted);
    }

    [Fact]
    public void Damage_minus_mr_caps_standard_reduction_at_fifty_percent()
    {
        int adjusted = SpellResistanceMath.ApplyDamageMinusMagicResistance(20, 200, targetHasAntiMagic: false);

        Assert.Equal(10, adjusted);
    }

    [Fact]
    public void Damage_minus_mr_uses_anti_magic_curve_when_target_has_anti_magic()
    {
        int adjusted = SpellResistanceMath.ApplyDamageMinusMagicResistance(20, 70, targetHasAntiMagic: true);

        Assert.Equal(13, adjusted);
    }

    [Fact]
    public void Type_two_resist_uses_half_mr_as_resist_chance()
    {
        Assert.True(SpellResistanceMath.IsSpellResisted(2, 70, targetHasAntiMagic: false, roll: 35));
        Assert.False(SpellResistanceMath.IsSpellResisted(2, 70, targetHasAntiMagic: false, roll: 36));
    }

    [Fact]
    public void Type_one_resist_requires_anti_magic()
    {
        Assert.False(SpellResistanceMath.IsSpellResisted(1, 70, targetHasAntiMagic: false, roll: 1));
        Assert.True(SpellResistanceMath.IsSpellResisted(1, 70, targetHasAntiMagic: true, roll: 35));
    }
}