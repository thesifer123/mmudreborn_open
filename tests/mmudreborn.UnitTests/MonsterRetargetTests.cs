using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Item C stage 3b — the "attack last" re-target roll. A player's swing may re-point the monster's
// lines 26036-26044). When a player's swing engages a monster, the monster re-points its single locked
// locked target onto that attacker on a roll of 1..99 < FollowPercent, OR unconditionally when
// the monster is passive-aligned (0/3/4). A type-5 monster only grabs when currently untargeted. The
// summoned/charmed-pet exclusions are applied by the caller, not this pure decision.
public sealed class MonsterRetargetTests
{
    // Non-passive align (e.g. 2 = evil hostile): re-targets only when the roll beats FollowPercent.
    [Theory]
    [InlineData(2, 50, 49, true)]   // roll 49 < 50 -> grab
    [InlineData(2, 50, 50, false)]  // roll 50 is NOT < 50 -> miss (strict <, genrdn(1,100))
    [InlineData(2, 50, 75, false)]  // roll above FollowPercent -> miss
    [InlineData(1, 0, 1, false)]    // FollowPercent 0 -> never grabs on the roll
    public void Aggressive_monster_retargets_only_when_roll_beats_follow_percent(
        int align, int followPercent, int roll, bool expected)
    {
        Assert.Equal(expected,
            CombatEngine.ShouldRetargetToAttacker(align, monsterType: 1, followPercent, currentlyTargeted: false, roll));
    }

    // Passive-aligned monsters (0 townsfolk, 3 peaceful, 4 lawful) always grab the most-recent
    // attacker, regardless of the roll vs FollowPercent.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    public void Passive_aligned_monster_always_retargets_to_the_attacker(int align)
    {
        // FollowPercent 0 and a max roll would fail the aggressive path, but passive always grabs.
        Assert.True(
            CombatEngine.ShouldRetargetToAttacker(align, monsterType: 1, monsterFollowPercent: 0, currentlyTargeted: false, roll: 100));
    }

    [Fact]
    public void Type5_monster_never_steals_off_an_existing_lock_even_on_a_winning_roll()
    {
        // Already targeted + a roll that would otherwise win (1 < 99) -> type-5 still refuses to steal.
        Assert.False(
            CombatEngine.ShouldRetargetToAttacker(monsterAlign: 2, monsterType: 5, monsterFollowPercent: 99, currentlyTargeted: true, roll: 1));
    }

    [Fact]
    public void Type5_monster_grabs_when_currently_untargeted_and_the_roll_wins()
    {
        Assert.True(
            CombatEngine.ShouldRetargetToAttacker(monsterAlign: 2, monsterType: 5, monsterFollowPercent: 99, currentlyTargeted: false, roll: 1));
    }

    [Fact]
    public void Passive_type5_monster_still_grabs_only_when_untargeted()
    {
        // Passive align would always grab, but the type-5 untargeted gate takes precedence.
        Assert.False(
            CombatEngine.ShouldRetargetToAttacker(monsterAlign: 0, monsterType: 5, monsterFollowPercent: 0, currentlyTargeted: true, roll: 100));
    }
}

// Item C stage 3c — the cross-player target spread and the post-swing lock roll.
// Both decisions are pure so they are deterministically testable here.
public sealed class MonsterTargetSpreadTests
{
    // A rng that returns a fixed script of genrdn(0,100) values, one per eligible candidate.
    private static Func<int> Rolls(params int[] values)
    {
        int i = 0;
        return () => values[i++];
    }

    [Fact]
    public void First_candidate_whose_roll_beats_threshold_wins()
    {
        // hits all 0 -> threshold 50. Candidate 0 rolls 60 (fail), candidate 1 rolls 10 (pass) -> index 1.
        int idx = CombatEngine.ChooseUnengagedAttackTarget(
            hitsThisTick: [0, 0, 0], eligibleForRoll: [true, true, true], Rolls(60, 10, 99));
        Assert.Equal(1, idx);
    }

    [Fact]
    public void When_no_roll_passes_the_last_eligible_candidate_is_the_fallback()
    {
        // All eligible, every roll fails (>= threshold) -> fall back to the LAST eligible (index 2).
        int idx = CombatEngine.ChooseUnengagedAttackTarget(
            hitsThisTick: [0, 0, 0], eligibleForRoll: [true, true, true], Rolls(99, 99, 99));
        Assert.Equal(2, idx);
    }

    [Fact]
    public void Hits_this_tick_lower_the_threshold_and_spread_damage()
    {
        // Candidate 0 already took 10 hits -> threshold 50-50=0, no roll 0..100 is < 0, always fails.
        // Candidate 1 fresh -> threshold 50, roll 5 passes -> index 1. (Damage spreads off the hit one.)
        int idx = CombatEngine.ChooseUnengagedAttackTarget(
            hitsThisTick: [10, 0], eligibleForRoll: [true, true], Rolls(0, 5));
        Assert.Equal(1, idx);
    }

    [Fact]
    public void Ineligible_candidates_are_skipped_for_both_the_roll_and_the_fallback()
    {
        // Only candidate 1 is eligible and its roll fails -> fallback is index 1, never the ineligible 0/2.
        int idx = CombatEngine.ChooseUnengagedAttackTarget(
            hitsThisTick: [0, 0, 0], eligibleForRoll: [false, true, false], Rolls(99));
        Assert.Equal(1, idx);
    }

    [Fact]
    public void No_eligible_candidate_yields_minus_one()
    {
        int idx = CombatEngine.ChooseUnengagedAttackTarget(
            hitsThisTick: [0, 0], eligibleForRoll: [false, false], Rolls());
        Assert.Equal(-1, idx);
    }

    [Theory]
    [InlineData(2, 60, 50, CombatEngine.MonsterLockUpdate.Lock)]    // aggressive, roll 50 < FP 60 -> focus
    [InlineData(2, 60, 70, CombatEngine.MonsterLockUpdate.Clear)]   // aggressive, roll fails -> drop & re-spread
    [InlineData(4, 60, 70, CombatEngine.MonsterLockUpdate.Keep)]    // passive guard, roll fails -> keep current lock
    [InlineData(4, 60, 10, CombatEngine.MonsterLockUpdate.Lock)]    // passive, roll passes -> focus
    public void Post_attack_lock_focuses_or_respreads(int align, int followPercent, int roll, CombatEngine.MonsterLockUpdate expected)
    {
        Assert.Equal(expected,
            CombatEngine.ResolvePostAttackLock(align, monsterType: 1, followPercent, currentlyTargeted: false, roll));
    }

    [Fact]
    public void Summoned_creature_never_manages_its_lock_post_attack()
    {
        // Type 37 (summoned): even a winning roll leaves the lock untouched.
        Assert.Equal(CombatEngine.MonsterLockUpdate.Keep,
            CombatEngine.ResolvePostAttackLock(monsterAlign: 2, monsterType: 37, monsterFollowPercent: 99, currentlyTargeted: false, roll: 0));
    }

    [Fact]
    public void Type5_monster_keeps_an_existing_lock_post_attack()
    {
        Assert.Equal(CombatEngine.MonsterLockUpdate.Keep,
            CombatEngine.ResolvePostAttackLock(monsterAlign: 2, monsterType: 5, monsterFollowPercent: 99, currentlyTargeted: true, roll: 0));
    }
}
