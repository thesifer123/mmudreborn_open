using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Golden lock-in for the two error-prone pure formulas inside the attack resolver,
// extracted from CalculateAttack so they can be pinned deterministically (the live path rolls a
// static RNG). Values are hand-computed straight from the stock integer math (verified against
// stock for scaling); a regression in any constant (the /140, the
// (AC+dodge)² term, the [10,99] clamp, the DG·10 / (AV/8) dodge ratio, the 95 cap, the backstab /5)
// flips one of these exact numbers.
public sealed class CombatHitChanceFormulaTests
{
    [Theory]
    // Normal/quadratic branch: temp = (AV+mod)²/140; hit = 100 − (AC+dodgeSkill)²/temp; clamp [10,99].
    [InlineData(100, 0, 20, 10, 88)]    // temp=71, 30²/71=12 → 88
    [InlineData(50, 0, 40, 30, 10)]     // temp=17, 70²/17=288 → -188 → clamp 10
    [InlineData(5, 0, 0, 0, 10)]        // temp=0 → stock forces 5 → clamp 10
    [InlineData(100, -15, 20, 10, 83)]  // bash mod: accTotal=85, temp=51, 30²/51=17 → 83
    [InlineData(200, 0, 0, 0, 99)]      // huge AV, no defence → 100 → clamp 99
    public void Normal_hit_chance_matches_stock(int av, int accMod, int ac, int dodgeSkill, int expected)
        => Assert.Equal(expected, CombatEngine.ComputeHitChance(CombatEngine.AttackType.Normal, av, accMod, ac, dodgeSkill));

    [Theory]
    // Backstab branch is the simple AV − AC line (accMod and dodgeSkill are IGNORED), clamped [10,99].
    [InlineData(100, 20, 80)]
    [InlineData(200, 20, 99)]   // 180 → clamp 99
    [InlineData(15, 20, 10)]    // -5 → clamp 10
    public void Backstab_hit_chance_is_av_minus_ac(int av, int ac, int expected)
        => Assert.Equal(expected, CombatEngine.ComputeHitChance(CombatEngine.AttackType.Backstab, av, accuracyMod: 999, ac, defenderDodgeSkill: 999));

    [Theory]
    // Post-hit dodge gate: AV<9 → 0; else (DG·10)/(AV/8), cap 95, backstab /5.
    [InlineData(CombatEngine.AttackType.Normal, 80, 20, 20)]    // 80/8=10 → 200/10=20
    [InlineData(CombatEngine.AttackType.Normal, 80, 50, 50)]    // 500/10=50
    [InlineData(CombatEngine.AttackType.Normal, 16, 20, 95)]    // 16/8=2 → 200/2=100 → cap 95
    [InlineData(CombatEngine.AttackType.Normal, 8, 50, 0)]      // AV<9 → 0 (also guards div-by-zero)
    [InlineData(CombatEngine.AttackType.Backstab, 80, 20, 4)]   // 20 then /5
    [InlineData(CombatEngine.AttackType.Backstab, 16, 20, 19)]  // 100 → cap 95 → /5 = 19
    public void Dodge_chance_matches_stock(CombatEngine.AttackType type, int av, int dg, int expected)
        => Assert.Equal(expected, CombatEngine.ComputeDodgeChance(type, av, dg));
}
