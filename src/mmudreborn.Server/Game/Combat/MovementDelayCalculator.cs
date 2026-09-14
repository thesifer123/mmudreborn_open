namespace mmudreborn.Game.Combat;

/// <summary>
/// Pure movement-delay rules, factored out so they are unit-testable
/// independent of the async command path. The delay is measured in fast-ticks (1s);
/// the caller multiplies by the fast-tick period and applies it as the gate before the next move.
/// </summary>
public static class MovementDelayCalculator
{
    public const int HeavyEncumbrancePercent = 66;          // above this, +1 tick
    public const int MaxMovementEncumbrancePercent = 100;   // above this, move blocked
    public const int RapidMoveFatigueThreshold = 2;         // above 2 → +1 tick on the 3rd+ move

    /// <summary>Encumbrance &gt; 100% hard-blocks movement ("You are too heavy to move!").</summary>
    public static bool IsOverEncumbered(int encumbrancePercent) => encumbrancePercent > MaxMovementEncumbrancePercent;

    /// <summary>
    /// The fast-tick cost: base 1 (2 while dragging); encumbrance &gt; 66% adds +1 (+2 dragging);
    /// the slow state (ability 68) doubles, the haste state (ability 67) halves; floored at 1; then
    /// the rapid-move counter adds +1 once it exceeds 2. Heavy players never accumulate
    /// fatigue (stock resets the counter each fast tick when enc &gt; 66), so fatigue only applies when light.
    /// </summary>
    public static int ComputeDelayTicks(int encumbrancePercent, bool isDragging, bool isHasted, bool isSlowed, int recentMoveCount)
    {
        bool heavy = encumbrancePercent > HeavyEncumbrancePercent;

        int ticks = isDragging ? 2 : 1;
        if (heavy)
            ticks += isDragging ? 2 : 1;
        if (isSlowed)
            ticks *= 2;
        if (isHasted)
            ticks /= 2;
        if (ticks < 1)
            ticks = 1;

        if (!heavy && recentMoveCount > RapidMoveFatigueThreshold)
            ticks += 1;

        return ticks;
    }
}
