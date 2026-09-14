namespace mmudreborn.Game.Combat;

internal static class SpellResistanceMath
{
    public static bool IsSpellResisted(int typeOfResists, int magicResistance, bool targetHasAntiMagic, int roll)
    {
        if (roll < 1 || roll > 100)
            throw new ArgumentOutOfRangeException(nameof(roll));

        bool canResist = (typeOfResists == 1 && targetHasAntiMagic) || typeOfResists == 2;
        if (!canResist)
            return false;

        int cappedMagicResistance = Math.Min(magicResistance, 196);
        return roll <= cappedMagicResistance / 2;
    }

    // Spell element (AttType) -> the defender's
    // per-element resistance ability. 0=cold(3), 1=hot/fire(5), 2=65, 3=66, 5=147, 6=poison(21).
    // AttType 4 / anything else: no elemental resistance applies.
    public static int GetElementResistAbilityId(int attType) => attType switch
    {
        0 => 3,
        1 => 5,
        2 => 65,
        3 => 66,
        5 => 147,
        6 => 21,
        _ => 0,
    };

    // Gated by spell type < 3; damage = (100 - resist) * base / 100 (signed integer division,
    // verified against stock). A negative resistance (e.g. Nekojin cold = -10) raises damage.
    public static int ApplyElementalDamage(int spellType, int attType, int baseDamage, int resistValue)
    {
        if (spellType >= 3 || GetElementResistAbilityId(attType) == 0)
            return baseDamage;

        return (100 - resistValue) * baseDamage / 100;
    }

    public static int ApplyDamageMinusMagicResistance(int amount, int magicResistance, bool targetHasAntiMagic)
    {
        if (amount <= 0)
            return amount;

        int cappedMagicResistance = Math.Min(magicResistance, 150);
        double percentReduction = targetHasAntiMagic
            ? Math.Round(cappedMagicResistance / 200d, 2, MidpointRounding.ToEven)
            : cappedMagicResistance < 50
                ? Math.Round((cappedMagicResistance - 50) / 100d, 2, MidpointRounding.ToEven)
                : Math.Round((cappedMagicResistance - 50) / 200d, 2, MidpointRounding.ToEven);

        double adjustedAmount = amount - (amount * percentReduction);
        return Math.Max(0, (int)Math.Round(adjustedAmount, 0, MidpointRounding.ToEven));
    }
}