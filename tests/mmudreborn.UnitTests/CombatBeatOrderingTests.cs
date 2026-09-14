using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Item C stage 2 — the backstab pre-pass selection (the background round:
// every queued backstab resolves before any normal attack or monster swing that beat, bug #70).
// GameWorld.Combat.cs factors the "who is in the pre-pass" decision into the pure
// GameWorld.SelectDueBackstabbers so it is testable here without the telnet/session stack. The
// coordinator then runs those players' OwnAttack phase first, excludes them from the player pass
// (fire-once), and resolves the rest under the 60/40 player-swings vs monster-swings coin-flip.
public sealed class CombatBeatOrderingTests
{
    private static readonly DateTime NowUtc = new(2026, 1, 1, 0, 0, 8, DateTimeKind.Utc);
    private static readonly DateTime DueUtc = NowUtc.AddSeconds(-4);   // beat already reached
    private static readonly DateTime NotDueUtc = NowUtc.AddSeconds(4); // beat in the future

    private static Player Combatant(string name, PlayerCombatRoundAction action, DateTime nextRoundUtc)
        => new() { Name = name, PendingCombatRoundAction = action, NextMonsterAttackAtUtc = nextRoundUtc };

    [Fact]
    public void Only_due_backstabbers_are_selected_for_the_pre_pass_in_iteration_order()
    {
        var warrior = Combatant("Warrior", PlayerCombatRoundAction.Attack, DueUtc);
        var rogueA = Combatant("RogueA", PlayerCombatRoundAction.Backstab, DueUtc);
        var basher = Combatant("Basher", PlayerCombatRoundAction.Bash, DueUtc);
        var rogueB = Combatant("RogueB", PlayerCombatRoundAction.Backstab, DueUtc);

        var prePass = GameWorld.SelectDueBackstabbers([warrior, rogueA, basher, rogueB], NowUtc);

        // Backstabbers only, in iteration order; the normal attacker and basher resolve in the main pass.
        Assert.Equal(["RogueA", "RogueB"], prePass.Select(p => p.Name));
    }

    [Fact]
    public void A_backstabber_whose_beat_is_not_yet_due_stays_out_of_the_pre_pass()
    {
        // The backstab is queued but its 4s grid beat has not arrived — it must NOT jump the pre-pass
        // this beat (it leads the pre-pass on the beat it actually becomes due). Preserves
        // bs k -> u -> "Sneaking..." (the swing stays queued, not fired early).
        var pendingRogue = Combatant("Rogue", PlayerCombatRoundAction.Backstab, NotDueUtc);
        var warrior = Combatant("Warrior", PlayerCombatRoundAction.Attack, DueUtc);

        var prePass = GameWorld.SelectDueBackstabbers([pendingRogue, warrior], NowUtc);

        Assert.Empty(prePass);
    }

    [Fact]
    public void An_idle_player_with_no_scheduled_beat_is_never_in_the_pre_pass()
    {
        var idleRogue = Combatant("Rogue", PlayerCombatRoundAction.Backstab, DateTime.MinValue);

        var prePass = GameWorld.SelectDueBackstabbers([idleRogue], NowUtc);

        Assert.Empty(prePass);
    }

    [Fact]
    public void No_backstabbers_yields_an_empty_pre_pass()
    {
        var warrior = Combatant("Warrior", PlayerCombatRoundAction.Attack, DueUtc);
        var basher = Combatant("Basher", PlayerCombatRoundAction.Bash, DueUtc);

        var prePass = GameWorld.SelectDueBackstabbers([warrior, basher], NowUtc);

        Assert.Empty(prePass);
    }
}
