using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class WeaponSwingPreviewCalculatorTests
{
    [Fact]
    public void Calculate_matches_quarterstaff_reference_and_speed_multipliers()
    {
        var fast = WeaponSwingPreviewCalculator.Calculate(
            combatLevel: 1,
            level: 1,
            weaponSpeed: 1200,
            agility: 30,
            strength: 65,
            encumbrancePercent: 6,
            itemStrengthRequirement: 30,
            speedModifierPercent: 85);
        var normal = WeaponSwingPreviewCalculator.Calculate(
            combatLevel: 1,
            level: 1,
            weaponSpeed: 1200,
            agility: 30,
            strength: 65,
            encumbrancePercent: 6,
            itemStrengthRequirement: 30,
            speedModifierPercent: 100);
        var slow = WeaponSwingPreviewCalculator.Calculate(
            combatLevel: 1,
            level: 1,
            weaponSpeed: 1200,
            agility: 30,
            strength: 65,
            encumbrancePercent: 6,
            itemStrengthRequirement: 30,
            speedModifierPercent: 125);
        var bash = WeaponSwingPreviewCalculator.Calculate(
            combatLevel: 1,
            level: 1,
            weaponSpeed: 1200,
            agility: 30,
            strength: 65,
            encumbrancePercent: 6,
            itemStrengthRequirement: 30,
            speedModifierPercent: 100,
            isBashing: true);

        // Energy use is Level*CombatLvl+45 (no +2): denom = (1*1+45)*(30+150)*1500/9000
        // = 1380; base EU = 1200*1000/1380 = 869; ×(6/2+75)%=78% → 677 at speedMod 100.
        Assert.Equal(575, fast.EnergyUse);     // ×85%
        Assert.Equal(1.7391, fast.RawSwings, 4);
        Assert.Equal(0, fast.QuickAndDeadlyBonus);

        Assert.Equal(677, normal.EnergyUse);
        Assert.Equal(1.4771, normal.RawSwings, 4);
        Assert.Equal(0, normal.QuickAndDeadlyBonus);

        Assert.Equal(846, slow.EnergyUse);     // ×125%
        Assert.Equal(1.1820, slow.RawSwings, 4);
        Assert.Equal(0, slow.QuickAndDeadlyBonus);

        // Bash doubles EU (677×2 = 1354) but stock ceils EU to maxStamina (1000), so a bash
        // still costs exactly one full pool and lands one swing per round.
        Assert.Equal(1000, bash.EnergyUse);
        Assert.Equal(1.0000, bash.RawSwings, 4);
        Assert.Equal(0, bash.QuickAndDeadlyBonus);
    }

    [Fact]
    public void CalculateQuickAndDeadlyBonus_uses_agility_and_encumbrance_thresholds()
    {
        Assert.Equal(20, WeaponSwingPreviewCalculator.CalculateQuickAndDeadlyBonus(agility: 90, energyUse: 150, encumbrancePercent: 20));
        Assert.Equal(10, WeaponSwingPreviewCalculator.CalculateQuickAndDeadlyBonus(agility: 90, energyUse: 150, encumbrancePercent: 33));
        Assert.Equal(0, WeaponSwingPreviewCalculator.CalculateQuickAndDeadlyBonus(agility: 90, energyUse: 200, encumbrancePercent: 20));
        Assert.Equal(0, WeaponSwingPreviewCalculator.CalculateQuickAndDeadlyBonus(agility: 90, energyUse: 150, encumbrancePercent: 67));
        Assert.Equal(0, WeaponSwingPreviewCalculator.CalculateQuickAndDeadlyBonus(agility: 90, energyUse: 150, encumbrancePercent: 20, meetsStrengthRequirement: false));
    }

    [Fact]
    public void CalculateRound_matches_stock_reference_swing_sequences()
    {
        int remainder = 0;
        int[] quarterstaffAttack = new int[10];
        for (int index = 0; index < quarterstaffAttack.Length; index++)
        {
            var round = WeaponSwingPreviewCalculator.CalculateRound(649, remainder);
            quarterstaffAttack[index] = round.Swings;
            remainder = round.NextEnergyRemainder;
        }

        remainder = 0;
        int[] quarterstaffBash = new int[10];
        for (int index = 0; index < quarterstaffBash.Length; index++)
        {
            var round = WeaponSwingPreviewCalculator.CalculateRound(1298, remainder);
            quarterstaffBash[index] = round.Swings;
            remainder = round.NextEnergyRemainder;
        }

        remainder = 0;
        int[] azureAttack = new int[10];
        for (int index = 0; index < azureAttack.Length; index++)
        {
            var round = WeaponSwingPreviewCalculator.CalculateRound(94, remainder);
            azureAttack[index] = round.Swings;
            remainder = round.NextEnergyRemainder;
        }

        Assert.Equal([1, 2, 1, 2, 1, 2, 1, 2, 1, 2], quarterstaffAttack);
        Assert.Equal([0, 1, 1, 1, 0, 1, 1, 1, 0, 1], quarterstaffBash);
        Assert.Equal([5, 5, 5, 5, 5, 5, 5, 5, 5, 5], azureAttack);
    }

    [Fact]
    public void CalculateAverageRoundSwings_caps_fast_weapon_sequences_at_five_real_swings()
    {
        Assert.Equal(5.0, WeaponSwingPreviewCalculator.CalculateAverageRoundSwings(94), 4);
        Assert.Equal(5.0, WeaponSwingPreviewCalculator.CalculateAverageRoundSwings(188), 4);
    }

    [Fact]
    public void Calculate_bashing_preserves_quick_and_deadly_when_final_energy_is_still_fast_enough()
    {
        var bashPreview = WeaponSwingPreviewCalculator.Calculate(
            combatLevel: 1,
            level: 10,
            weaponSpeed: 250,
            agility: 90,
            strength: 90,
            encumbrancePercent: 0,
            itemStrengthRequirement: 0,
            speedModifierPercent: 100,
            isBashing: true);

        // Quick & Deadly is decided from the raw (pre-clamp) EU; the budget-side EU is
        // clamped to MinimumEffectiveEnergyUse.
        Assert.True(bashPreview.RawEnergyUse < WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse);
        Assert.Equal(WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse, bashPreview.EnergyUse);
        Assert.Equal(20, bashPreview.QuickAndDeadlyBonus);
    }

    [Fact]
    public void Calculate_clamps_effective_energy_use_to_minimum_floor()
    {
        // Very high-level, high-agility character with a fast weapon would otherwise compute
        // an EU well below 200; the energy calculation clamps to 200 before swing budgeting.
        var preview = WeaponSwingPreviewCalculator.Calculate(
            combatLevel: 1,
            level: 30,
            weaponSpeed: 250,
            agility: 120,
            strength: 90,
            encumbrancePercent: 0,
            itemStrengthRequirement: 0,
            speedModifierPercent: 100);

        Assert.True(preview.RawEnergyUse < WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse);
        Assert.Equal(WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse, preview.EnergyUse);
    }

    [Fact]
    public void CalculateRound_caps_swings_at_six_for_extremely_fast_attackers()
    {
        // With a huge leftover and a fast weapon at the EU floor (200), the raw budget would
        // produce more than 6 swings; the stock attack loop caps at 6.
        var round = WeaponSwingPreviewCalculator.CalculateRound(
            energyUse: 50,             // pre-clamp; method floors to 200 internally
            currentEnergyRemainder: 500,
            maxStamina: 1000);

        Assert.Equal(WeaponSwingPreviewCalculator.MaxWeaponSwingsPerRound, round.Swings);
        Assert.Equal(6, WeaponSwingPreviewCalculator.MaxWeaponSwingsPerRound);
    }

    [Fact]
    public void CalculateRound_respects_custom_max_stamina_cap()
    {
        // A monster (or buffed player) with a larger stamina pool gets more swings per round.
        var round = WeaponSwingPreviewCalculator.CalculateRound(
            energyUse: 200,
            currentEnergyRemainder: 0,
            maxStamina: 1200);

        // 1200 / 200 = 6 swings, exactly at the cap.
        Assert.Equal(6, round.Swings);
        Assert.Equal(0, round.NextEnergyRemainder);
    }

    [Fact]
    public void ConsumeSwingRound_spends_swings_from_the_absolute_pool_without_regen()
    {
        // Live combat: the pool is regenerated separately (PrepareCombatRound). ConsumeSwingRound
        // only spends. A full 1000 pool at EU 200 yields 5 swings and leaves nothing.
        var round = WeaponSwingPreviewCalculator.ConsumeSwingRound(energyUse: 200, currentEnergy: 1000);

        Assert.Equal(5, round.Swings);
        Assert.Equal(0, round.NextEnergyRemainder);
    }

    [Fact]
    public void ConsumeSwingRound_caps_at_six_and_keeps_overshoot_in_the_pool()
    {
        // An overshot pool (e.g. fresh combatant after PrepareCombatRound) can burst to 6 swings;
        // the unused remainder stays in the pool (stock keeps the overshoot — no modulo discard).
        var round = WeaponSwingPreviewCalculator.ConsumeSwingRound(energyUse: 200, currentEnergy: 2000);

        Assert.Equal(6, round.Swings);          // capped
        Assert.Equal(800, round.NextEnergyRemainder); // 2000 - 6*200 retained
    }

    [Fact]
    public void ConsumeSwingRound_floors_energy_use_to_200_and_yields_no_swings_on_empty_pool()
    {
        // The EU floor means a 50-EU weapon still costs 200/swing.
        var floored = WeaponSwingPreviewCalculator.ConsumeSwingRound(energyUse: 50, currentEnergy: 1000);
        Assert.Equal(5, floored.Swings); // 1000 / 200, not 1000 / 50

        // An empty pool produces no swings (regen must happen first).
        var empty = WeaponSwingPreviewCalculator.ConsumeSwingRound(energyUse: 200, currentEnergy: 0);
        Assert.Equal(0, empty.Swings);
        Assert.Equal(0, empty.NextEnergyRemainder);
    }
}