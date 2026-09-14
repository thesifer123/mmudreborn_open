using System;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Port verification for the stock evil-timer relationship list (the edge query, star display,
// retaliation test and timer add).
public sealed class EvilTimerRegistryTests
{
    [Fact]
    public void Already_evil_is_directional()
    {
        var reg = new EvilTimerRegistry();
        Assert.Equal(0, reg.AlreadyEvil("Alice", "Bob"));

        reg.AddEvilTimer("Alice", "Bob", chargedEvilPoints: 10);

        // The aggressor → victim edge is code 1 (plain, bit0 clear); the reverse edge stays 0.
        Assert.Equal(1, reg.AlreadyEvil("Alice", "Bob"));
        Assert.Equal(0, reg.AlreadyEvil("Bob", "Alice"));
    }

    [Fact]
    public void Already_evil_is_case_insensitive()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);
        Assert.Equal(1, reg.AlreadyEvil("alice", "BOB"));
    }

    [Fact]
    public void Display_evil_star_true_for_a_plain_timer()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);
        // No bit1 suppression on a normal PvP open → the marker shows.
        Assert.True(reg.DisplayEvilStar("Alice", "Bob"));
        Assert.True(reg.DisplayEvilStar("Bob", "Alice")); // no entry → still shows (returns 1)
    }

    [Fact]
    public void In_retaliation_only_for_aggressor_with_nonzero_amount()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", chargedEvilPoints: 10);

        Assert.True(reg.IsInRetaliation("Alice"));   // aggressor with a charge → travel-gated
        Assert.False(reg.IsInRetaliation("Bob"));    // victim is not in retaliation
    }

    [Fact]
    public void Zero_charge_records_relationship_without_retaliation_gate()
    {
        // Striking an already-evil target charges 0 EP but still records the edge (the timer opens
        // after the EP block): the name marker shows, but retaliation stays false (the amount is 0).
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", chargedEvilPoints: 0);

        Assert.Equal(1, reg.AlreadyEvil("Alice", "Bob"));
        Assert.False(reg.IsInRetaliation("Alice"));
    }

    [Fact]
    public void Refresh_with_zero_keeps_the_original_charge()
    {
        // A continued strike re-opens the window with amount 0; the original charge (and thus the
        // travel gate) must survive.
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", chargedEvilPoints: 10);
        reg.AddEvilTimer("Alice", "Bob", chargedEvilPoints: 0);

        Assert.True(reg.IsInRetaliation("Alice"));
    }

    [Fact]
    public void Entries_expire_after_the_window()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var reg = new EvilTimerRegistry();
        reg.SetClockForTests(() => now);

        reg.AddEvilTimer("Alice", "Bob", 10);
        Assert.Equal(1, reg.AlreadyEvil("Alice", "Bob"));

        now += EvilTimerRegistry.EvilTimerWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(0, reg.AlreadyEvil("Alice", "Bob"));
        Assert.False(reg.IsInRetaliation("Alice"));
    }

    [Fact]
    public void Remove_single_timer_drops_only_that_edge()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);
        reg.AddEvilTimer("Bob", "Alice", 10);

        reg.RemoveSingleTimer("Alice", "Bob");

        Assert.Equal(0, reg.AlreadyEvil("Alice", "Bob"));
        Assert.Equal(1, reg.AlreadyEvil("Bob", "Alice"));
    }

    [Fact]
    public void Noticed_rob_edge_shows_the_star_hidden_rob_edge_suppresses_it()
    {
        var reg = new EvilTimerRegistry();

        // A *noticed* rob (caught/bump) opens a bit-0-only edge: code 2, marker shows.
        reg.AddEvilTimer("Alice", "Bob", 1, EvilTimerRegistry.EvilTimerKind.RobNoticed);
        Assert.Equal(2, reg.AlreadyEvil("Alice", "Bob"));
        Assert.True(reg.DisplayEvilStar("Alice", "Bob"));

        // A *hidden* rob (quiet fail / silent success) opens bit-0|bit-1: code 3, marker suppressed.
        reg.AddEvilTimer("Carol", "Dave", 1, EvilTimerRegistry.EvilTimerKind.RobHidden);
        Assert.Equal(3, reg.AlreadyEvil("Carol", "Dave"));
        Assert.False(reg.DisplayEvilStar("Carol", "Dave"));
    }

    [Fact]
    public void Rob_open_overwrites_a_prior_pvp_edge_flags()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);   // plain PvP edge (code 1, star shows)
        Assert.Equal(1, reg.AlreadyEvil("Alice", "Bob"));

        // A subsequent hidden rob on the same victim overwrites the flags (remove + re-add).
        reg.AddEvilTimer("Alice", "Bob", 1, EvilTimerRegistry.EvilTimerKind.RobHidden);
        Assert.Equal(3, reg.AlreadyEvil("Alice", "Bob"));
        Assert.False(reg.DisplayEvilStar("Alice", "Bob"));
    }

    [Fact]
    public void Try_forgive_clears_the_inbound_edge_and_returns_the_charge()
    {
        // Forgiveness (attacker=Alice, victim=Bob): Bob forgives Alice; the Alice→Bob edge is
        // cleared and its charged EP handed back so the caller can refund it off Alice's evil points.
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", chargedEvilPoints: 25);

        Assert.True(reg.TryForgive("Alice", "Bob", out float refunded));
        Assert.Equal(25f, refunded);
        Assert.Equal(0, reg.AlreadyEvil("Alice", "Bob"));   // edge removed
    }

    [Fact]
    public void Try_forgive_fails_when_no_matching_edge()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 25);

        // Wrong direction (Bob never struck Alice) and an unrelated pair both fail.
        Assert.False(reg.TryForgive("Bob", "Alice", out float r1));
        Assert.Equal(0f, r1);
        Assert.False(reg.TryForgive("Carol", "Bob", out _));

        // The real edge is untouched by the failed attempts.
        Assert.Equal(1, reg.AlreadyEvil("Alice", "Bob"));
    }

    [Fact]
    public void Try_forgive_removes_only_one_edge_and_leaves_others()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);
        reg.AddEvilTimer("Carol", "Bob", 10);

        Assert.True(reg.TryForgive("Alice", "Bob", out _));

        Assert.Equal(0, reg.AlreadyEvil("Alice", "Bob"));
        Assert.Equal(1, reg.AlreadyEvil("Carol", "Bob")); // a different attacker's grudge survives
    }

    // The should-give-evil rule — the four verdicts the evil-point path branches on.
    [Fact]
    public void Should_give_evil_charges_fresh_aggression()
    {
        var reg = new EvilTimerRegistry();
        Assert.Equal(2, reg.ShouldGiveEvil("Alice", "Bob"));
    }

    [Fact]
    public void Should_give_evil_waives_self_defence()
    {
        // Bob struck first (Bob → Alice edge): Alice's counter-attack is free. The target-to-actor
        // actor) is tested BEFORE the actor's own edge, so it wins even in a mutual feud.
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Bob", "Alice", 10);
        Assert.Equal(0, reg.ShouldGiveEvil("Alice", "Bob"));

        reg.AddEvilTimer("Alice", "Bob", 10);
        Assert.Equal(0, reg.ShouldGiveEvil("Alice", "Bob"));
    }

    [Fact]
    public void Should_give_evil_waives_a_continued_strike_on_our_own_plain_edge()
    {
        // An actor → target code of 1: the opening EP is paid once per window, not per swing.
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);
        Assert.Equal(0, reg.ShouldGiveEvil("Alice", "Bob"));
    }

    [Theory]
    [InlineData(EvilTimerRegistry.EvilTimerKind.RobNoticed)]   // edge code 2
    [InlineData(EvilTimerRegistry.EvilTimerKind.RobHidden)]    // edge code 3
    public void Should_give_evil_still_charges_when_our_own_edge_came_from_robbing(EvilTimerRegistry.EvilTimerKind kind)
    {
        // Robbing someone does NOT pre-pay an attack on them: the verdict is 1, which charges in full
        // and drops the rob edge so the replacement is a plain one.
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 1, kind);
        Assert.Equal(1, reg.ShouldGiveEvil("Alice", "Bob"));

        // ...while the same rob edge makes ALICE fair game for BOB, for free.
        Assert.Equal(0, reg.ShouldGiveEvil("Bob", "Alice"));
    }

    [Fact]
    public void Remove_all_for_drops_both_directions()
    {
        var reg = new EvilTimerRegistry();
        reg.AddEvilTimer("Alice", "Bob", 10);
        reg.AddEvilTimer("Carol", "Alice", 10);
        reg.AddEvilTimer("Bob", "Carol", 10);

        reg.RemoveAllFor("Alice");

        Assert.Equal(0, reg.AlreadyEvil("Alice", "Bob"));
        Assert.Equal(0, reg.AlreadyEvil("Carol", "Alice"));
        Assert.Equal(1, reg.AlreadyEvil("Bob", "Carol")); // unrelated edge survives
    }
}
