using mmudreborn.Data.Models;

namespace mmudreborn.Game;

/// <summary>
/// Pure spell-effect primitives — the immediate per-ability effects applied by the cast
/// functions. Each mutates the target and returns the
/// actual applied delta (after clamping) for the cast-success message. Kept static/pure so they are
/// unit-testable and reusable across the beneficial, self, offensive, and item cast paths.
/// </summary>
public static class SpellEffects
{
    /// <summary>
    /// Ability 18 (heal): add <paramref name="amount"/> to HP, capped at MaxHP. The caller
    /// supplies a non-negative rolled amount.
    /// </summary>
    public static int ApplyHeal(Player target, int amount)
    {
        int previous = target.CurrentHP;
        target.CurrentHP = System.Math.Min(target.MaxHP, target.CurrentHP + amount);
        return target.CurrentHP - previous;
    }

    /// <summary>
    /// Ability 150 (restore/burn mana): add <paramref name="amount"/> to mana, clamped to
    /// [0, MaxMana]. A negative amount drains it (the offensive form, e.g. magebind). Matches
    /// clamped between 0 and the target's maximum.
    /// </summary>
    public static int ApplyMana(Player target, int amount)
    {
        int previous = target.CurrentMana;
        target.CurrentMana = System.Math.Clamp(target.CurrentMana + amount, 0, target.MaxMana);
        return target.CurrentMana - previous;
    }

    /// <summary>
    /// Ability 20 (reduce/cure poison): subtract <paramref name="amount"/> from the poison
    /// level, floored at 0. Returns the actual reduction. This — not ability 81 — is the real
    /// cure-poison effect; the poison level is the scalar drained per slow tick.
    /// </summary>
    public static int ReducePoison(Player target, int amount)
    {
        int previous = target.PoisonLevel;
        target.PoisonLevel = System.Math.Max(0, target.PoisonLevel - amount);
        return previous - target.PoisonLevel;
    }

    /// <summary>Ability 19 — the poison ability id (the per-tick HP drain).</summary>
    public const int PoisonAbilityId = 19;

    /// <summary>
    /// On spell termination, ability 19: when a poison spell LEAVES a
    /// target — by expiry, dispel, cure, or remove-by-number — subtract the magnitude it deposited back
    /// off the poison level (floored at 0) so the poison "runs its course" no matter HOW it was removed.
    /// Without this the dispel/cure paths strip the active spell but leave the poison accumulator behind,
    /// so antidote (reduce-poison 20 + dispel 73) only "sort of" cured — the active spell vanished
    /// (no status line) yet the target kept taking poison damage (bug #112). Magnitude = the spell's own
    /// poison ability value, falling back to the rolled cast magnitude when that value is 0 (the
    /// monster-bite case, where the rolled amount is stored as the active spell's CastLevel). Returns the
    /// actual reduction.
    /// </summary>
    public static int ReverseTerminatedPoison(Player target, GameSpell source, int castMagnitude)
    {
        if (source is null || !source.Abilities.TryGetValue(PoisonAbilityId, out int poisonAbilityValue))
            return 0;

        int magnitude = poisonAbilityValue != 0 ? poisonAbilityValue : castMagnitude;
        int previous = target.PoisonLevel;
        target.PoisonLevel = System.Math.Max(0, target.PoisonLevel - magnitude);
        return previous - target.PoisonLevel;
    }

    /// <summary>
    /// Dispel (ability 73): true when an active buff sourced from <paramref name="source"/>
    /// should be stripped — i.e. <paramref name="abilityToStrip"/> is the "all" sentinel
    /// or the source spell carries that ability id. Used to filter a target's active-spell slots.
    /// </summary>
    public static bool ShouldDispel(GameSpell source, int abilityToStrip)
        => abilityToStrip is -1 or 65535 || source.Abilities.ContainsKey(abilityToStrip);

    /// <summary>
    /// Cure status (ability 81): a buff sourced from a spell carrying ability 74 or 75
    /// (the paralysis/disease status markers) is stripped. Distinct from the poison level.
    /// </summary>
    public static bool IsPoisonOrDisease(GameSpell source)
        => source.Abilities.ContainsKey(74) || source.Abilities.ContainsKey(75);

    /// <summary>
    /// Ability 11 (restore energy/stamina): add <paramref name="amount"/> to the energy pool
    /// (the C# WeaponSwingEnergyRemainder). Stock does a raw add with no
    /// clamp here — the pool is bounded by the regen/refill cadence elsewhere — so this mirrors that.
    /// NB: dead in stock data (no spell carries ability 11); implemented for completeness. Returns the delta.
    /// </summary>
    public static int RestoreEnergy(Player target, int amount)
    {
        if (amount == 0)
            return 0;

        target.CurrentEnergy += amount;
        return amount;
    }

    /// <summary>
    /// Ability 160 (teach spell): add spell number <paramref name="spellNumber"/> to the target's
    /// spellbook. Mirrors LearnSpell — sets the learned-spellbook quest
    /// ability for that spell. Returns true if newly learned (false if already known or invalid).
    /// </summary>
    public static bool TeachSpell(Player target, int spellNumber)
    {
        if (spellNumber <= 0)
            return false;

        int abilityId = Player.GetLearnedSpellbookAbilityId(spellNumber);
        if (target.GetQuestAbilityValue(abilityId) > 0)
            return false;

        target.SetQuestAbilityValue(abilityId, 1);
        return true;
    }

    /// <summary>
    /// Ability 95 (smite) vs a player target: the damage that drops the victim to just past the
    /// death threshold. Stock: damage = -deathSetting when HP&lt;0,
    /// else (HP - deathSetting) + 1, leaving HP = deathSetting - 1 (a guaranteed kill). deathHp is the
    /// signed death-threshold (Player.DeathHP).
    /// </summary>
    public static int SmiteDamageVsPlayer(int currentHp, int deathHp)
        => currentHp < 0 ? -deathHp : (currentHp - deathHp) + 1;

    /// <summary>
    /// Ability 95 (smite) vs a monster target: instant kill — damage = HP + 2
    /// pushing the monster below 0.
    /// </summary>
    public static int SmiteDamageVsMonster(int currentHp)
        => currentHp + 2;
}
