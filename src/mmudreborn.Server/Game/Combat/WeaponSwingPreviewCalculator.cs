using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Game.Combat;

// EnergyUse exposes the effective EU (post min-200 floor) used for the round budget.
// RawEnergyUse exposes the pre-clamp value used by Quick & Deadly detection.
public readonly record struct WeaponSwingPreview(int EnergyUse, double RawSwings, int QuickAndDeadlyBonus, int EncumbrancePercent, int RawEnergyUse);
public readonly record struct WeaponSwingRound(int Swings, int NextEnergyRemainder);

public static class WeaponSwingPreviewCalculator
{
    // The attack loops run up to 6 swings per round.
    public const int MaxWeaponSwingsPerRound = 6;

    // Energy use is clamped to a floor of 200 before swing budgeting.
    // Quick & Deadly detection still uses the pre-clamp EU.
    public const int MinimumEffectiveEnergyUse = 200;

    // The energy update refills the pool by staminaCap per fast tick.
    // Stock player stamina cap is 1000.
    public const int DefaultPlayerMaxStamina = 1000;

    public static WeaponSwingPreview Calculate(Player player, CharacterClass cls, Item weapon, int speedModifierPercent = 100, bool isBashing = false)
    {
        int encumbrancePercent = CalculateEncumbrancePercent(player.Encumbrance, player.MaxEncumbrance);
        return Calculate(
            cls.CombatLvl,
            player.Level,
            weapon.Speed,
            player.Agility,
            player.Strength,
            encumbrancePercent,
            weapon.StrReq,
            speedModifierPercent,
            isBashing,
            player.GetEffectiveMaxStamina());
    }

    public static WeaponSwingPreview Calculate(
        int combatLevel,
        int level,
        int weaponSpeed,
        int agility,
        int strength,
        int encumbrancePercent,
        int itemStrengthRequirement,
        int speedModifierPercent = 100,
        bool isBashing = false,
        int maxStamina = DefaultPlayerMaxStamina)
    {
        int safeWeaponSpeed = Math.Max(1, weaponSpeed);
        int safeSpeedModifierPercent = speedModifierPercent > 0 ? speedModifierPercent : 100;
        int safeEncumbrancePercent = Math.Max(0, encumbrancePercent);

        // Energy use: Level*classSpeed + 45, where classSpeed is
        // the SAME class field used as classCombat in the AV formula (= CombatLvl). No +2.
        int denominator = Math.Max(1, ((level * combatLevel) + 45) * (agility + 150) * 1500 / 9000);
        int energyUse = safeWeaponSpeed * 1000 / denominator;

        if (strength < itemStrengthRequirement)
            energyUse = (((itemStrengthRequirement - strength) * 3) + 200) * energyUse / 200;

        energyUse = energyUse * ((safeEncumbrancePercent / 2) + 75) / 100;
        energyUse = Math.Max(1, energyUse * safeSpeedModifierPercent / 100);

        if (isBashing)
            energyUse = Math.Max(1, energyUse * 2);

        // Stock uses the pre-clamp EU when deciding Quick & Deadly eligibility.
        int rawEnergyUse = energyUse;
        int quickAndDeadlyBonus = CalculateQuickAndDeadlyBonus(agility, rawEnergyUse, safeEncumbrancePercent, strength >= itemStrengthRequirement);

        // Effective EU drives the per-round swing budget. The fighter marshal clamps it to
        // [200, maxStamina]: floor 200 AND ceil maxStamina (
        // `if (maxStamina < EU) EU = maxStamina`). The ceiling is what lets a bash/smash (EU ×2 / =
        // full stamina) still fire once from a full pool instead of pricing itself out of range.
        int safeMaxStamina = Math.Max(MinimumEffectiveEnergyUse, maxStamina);
        int effectiveEnergyUse = Math.Clamp(rawEnergyUse, MinimumEffectiveEnergyUse, safeMaxStamina);

        double rawSwings = Math.Round(1000.0 / effectiveEnergyUse, 4, MidpointRounding.AwayFromZero);
        return new WeaponSwingPreview(effectiveEnergyUse, rawSwings, quickAndDeadlyBonus, safeEncumbrancePercent, rawEnergyUse);
    }

    public static int CalculateEncumbrancePercent(int currentEncumbrance, int maxEncumbrance)
    {
        int safeMaxEncumbrance = Math.Max(1, maxEncumbrance);
        return Math.Max(0, currentEncumbrance * 100 / safeMaxEncumbrance);
    }

    public static int CalculateQuickAndDeadlyBonus(int agility, int energyUse, int encumbrancePercent, bool meetsStrengthRequirement = true)
    {
        if (!meetsStrengthRequirement || energyUse >= 200 || encumbrancePercent > 66)
            return 0;

        int bonus = Math.Min(20, 200 - energyUse + ((agility - 50) / 10));
        if (encumbrancePercent >= 33)
            bonus /= 2;

        return Math.Max(0, bonus);
    }

    public static WeaponSwingRound CalculateRound(int energyUse, int currentEnergyRemainder)
    {
        return CalculateRound(energyUse, currentEnergyRemainder, DefaultPlayerMaxStamina);
    }

    /// <summary>
    /// The per-round swing budget. Mirrors the energy-use calculation plus the attack loop:
    ///   - EU is clamped to a floor of <see cref="MinimumEffectiveEnergyUse"/> (200).
    ///   - Budget = leftover stamina + per-round regen (`maxStamina`).
    ///   - Swing count is capped at <see cref="MaxWeaponSwingsPerRound"/> (6).
    /// </summary>
    public static WeaponSwingRound CalculateRound(int energyUse, int currentEnergyRemainder, int maxStamina)
    {
        int safeEnergyUse = Math.Max(MinimumEffectiveEnergyUse, Math.Max(1, energyUse));
        int safeEnergyRemainder = Math.Max(0, currentEnergyRemainder);
        int safeMaxStamina = maxStamina > 0 ? maxStamina : DefaultPlayerMaxStamina;
        int energyBudget = safeEnergyRemainder + safeMaxStamina;
        int swings = Math.Min(MaxWeaponSwingsPerRound, energyBudget / safeEnergyUse);
        int nextEnergyRemainder = energyBudget % safeEnergyUse;
        return new WeaponSwingRound(swings, nextEnergyRemainder);
    }

    /// <summary>
    /// The live swing loop: consume swings from the attacker's ABSOLUTE stamina pool.
    /// Unlike <see cref="CalculateRound"/> — a stateless preview that re-adds the 1000
    /// cap every round — this does NOT regenerate; the per-round refill lives in
    /// Player/MonsterInstance.PrepareCombatRound. It only consumes:
    ///   swings = min(6, pool / EU);  remaining = pool - swings*EU.
    /// EU is clamped to [200, maxStamina]. There is NO opening-round burst: PrepareCombatRound only
    /// adds the cap when the pool is below it (the stock energy guard), so a combatant engaging at a
    /// full pool starts with exactly one cap, not two. Carried remainder (&lt; EU ≤ cap) is topped up
    /// each subsequent round. <see cref="WeaponSwingRound.NextEnergyRemainder"/> is the remaining pool.
    /// </summary>
    public static WeaponSwingRound ConsumeSwingRound(int energyUse, int currentEnergy)
    {
        int safeEnergyUse = Math.Max(MinimumEffectiveEnergyUse, Math.Max(1, energyUse));
        int pool = Math.Max(0, currentEnergy);
        int swings = Math.Min(MaxWeaponSwingsPerRound, pool / safeEnergyUse);
        int remainingEnergy = pool - (swings * safeEnergyUse);
        return new WeaponSwingRound(swings, remainingEnergy);
    }

    public static IReadOnlyList<int> CalculateSwingCycle(int energyUse, int currentEnergyRemainder = 0)
    {
        int safeEnergyUse = Math.Max(1, energyUse);
        int remainder = Math.Max(0, currentEnergyRemainder);
        var swings = new List<int>();
        var seenRemainders = new HashSet<int>();

        while (seenRemainders.Add(remainder))
        {
            var round = CalculateRound(safeEnergyUse, remainder);
            swings.Add(round.Swings);
            remainder = round.NextEnergyRemainder;
        }

        return swings;
    }

    public static double CalculateAverageRoundSwings(int energyUse, int currentEnergyRemainder = 0)
    {
        var cycle = CalculateSwingCycle(energyUse, currentEnergyRemainder);
        if (cycle.Count == 0)
            return 0;

        long totalSwings = 0;
        foreach (int swings in cycle)
            totalSwings += swings;

        return Math.Round(totalSwings / (double)cycle.Count, 4, MidpointRounding.AwayFromZero);
    }
}