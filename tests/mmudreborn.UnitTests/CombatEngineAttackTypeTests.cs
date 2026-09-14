using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Verifies CombatEngine matches the stock attack-resolver switch:
// attack-type accuracy/damage-pct/multiplier tables,
// crit gating, and damage-resist-before-multiplier ordering.
public sealed class CombatEngineAttackTypeTests
{
    [Theory]
    [InlineData(CombatEngine.AttackType.Punch, 0, 0, 1)]
    [InlineData(CombatEngine.AttackType.Kick, 0, 33, 1)]
    [InlineData(CombatEngine.AttackType.Jumpkick, 0, 66, 1)]
    [InlineData(CombatEngine.AttackType.Backstab, 0, 0, 1)]
    [InlineData(CombatEngine.AttackType.Normal, 0, 0, 1)]
    [InlineData(CombatEngine.AttackType.Bash, -15, 10, 3)]
    [InlineData(CombatEngine.AttackType.Smash, -25, 20, 5)]
    [InlineData(CombatEngine.AttackType.Surprise, -75, 125, 1)]
    public void Attack_type_modifier_tables_match_stock(CombatEngine.AttackType type, int expectedAccuracyMod, int expectedDamagePct, int expectedMultiplier)
    {
        Assert.Equal(expectedAccuracyMod, CombatEngine.GetAttackTypeAccuracyMod(type));
        Assert.Equal(expectedDamagePct, CombatEngine.GetAttackTypeDamagePct(type));
        Assert.Equal(expectedMultiplier, CombatEngine.GetAttackTypeDamageMultiplier(type));
    }

    [Theory]
    // weapon min==max==10 fixes the roll; bash/smash never crit so damage is deterministic.
    // Stock order: pre-roll dmg% on the weapon bound, then damage = (roll - DR/10) * multiplier.
    // DR raw 50 => /10 => 5.
    [InlineData(CombatEngine.AttackType.Normal, 5)]  // 10 +0%  => (10-5)*1
    [InlineData(CombatEngine.AttackType.Bash, 18)]   // 10 +10% => 11; (11-5)*3  (NOT x5)
    [InlineData(CombatEngine.AttackType.Smash, 35)]  // 10 +20% => 12; (12-5)*5
    public void Damage_resist_is_subtracted_before_the_multiplier(CombatEngine.AttackType type, int expectedDamage)
    {
        var calc = HitWith(type, weaponMin: 10, weaponMax: 10, defenderDamageResist: 50);

        Assert.False(calc.Missed);
        Assert.False(calc.Glanced);
        Assert.Equal(expectedDamage, calc.Damage);
    }

    [Fact]
    public void Blow_fully_absorbed_by_armour_glances_for_zero_damage()
    {
        // roll 3, DR/10 = 5 => 3 - 5 < 1 => glance (result type 1).
        var calc = HitWith(CombatEngine.AttackType.Normal, weaponMin: 3, weaponMax: 3, defenderDamageResist: 50);

        Assert.False(calc.Missed);
        Assert.True(calc.Glanced);
        Assert.Equal(0, calc.Damage);
    }

    [Fact]
    public void Bash_and_smash_never_crit()
    {
        for (int i = 0; i < 200; i++)
        {
            var bash = HitWith(CombatEngine.AttackType.Bash, 5, 5, critChance: 100);
            var smash = HitWith(CombatEngine.AttackType.Smash, 5, 5, critChance: 100);
            Assert.False(bash.IsCrit);
            Assert.False(smash.IsCrit);
        }
    }

    private static CombatCalcResult HitWith(CombatEngine.AttackType type, int weaponMin, int weaponMax, int defenderDamageResist = 0, int critChance = 0)
    {
        // Huge accuracy + zero defence guarantees the hit roll lands (loop guards the ~2% top-roll miss).
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var calc = CombatEngine.CalculateAttack(
                attackerAccuracy: 100000,
                defenderAC: 0,
                defenderDodge: 0,
                weaponMin: weaponMin,
                weaponMax: weaponMax,
                critChance: critChance,
                attackType: type,
                defenderDamageResist: defenderDamageResist);

            if (!calc.Missed)
                return calc;
        }

        throw new Xunit.Sdk.XunitException("Expected a guaranteed hit within the retry window.");
    }
}
