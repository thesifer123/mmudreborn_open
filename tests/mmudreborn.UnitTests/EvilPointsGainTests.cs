using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Stock ground truth (Bug #56): attacking an innocent monster (alignment 0 = townsfolk, 4 = lawful
// guards / shopkeepers / barmaid) is an evil act worth a flat 10 EP.
// The only progression gate is "too far to the evil side"
// (EP > 300) — there is NO "already Seedy → stop" gate, which is what previously made a single
// barmaid kill block every later guard/townsperson. PvP scales that base 10 by how GOOD the TARGET
// is (Saint ×3, Good ×2), not by the attacker's alignment.
public sealed class EvilPointsGainTests
{
    private static Player PlayerWithEp(float ep) => new() { EvilPoints = ep };

    [Theory]
    [InlineData(0)]  // townsfolk
    [InlineData(4)]  // lawful guards / shopkeepers / the barmaid
    public void Attacking_innocent_monster_grants_flat_ten(int align)
    {
        float ep = CombatEngine.GetEPCostForMonsterAttack(PlayerWithEp(0f), new Monster { Align = align });
        Assert.Equal(10f, ep);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)] // neutral/peaceful animals are fair game — no evil gain
    [InlineData(5)]
    [InlineData(6)]
    public void Attacking_non_innocent_monster_grants_nothing(int align)
    {
        float ep = CombatEngine.GetEPCostForMonsterAttack(PlayerWithEp(0f), new Monster { Align = align });
        Assert.Equal(0f, ep);
    }

    [Fact]
    public void Seedy_player_still_gains_from_attacking_innocents()
    {
        // Regression for Bug #56: once a player hit Seedy (EP >= 30) the old IsEvil gate returned 0,
        // so the barmaid was "the only one who gave EPs". Stock keeps granting until EP > 300.
        float ep = CombatEngine.GetEPCostForMonsterAttack(PlayerWithEp(35f), new Monster { Align = 0 });
        Assert.Equal(10f, ep);
    }

    [Fact]
    public void Player_who_has_progressed_too_far_gains_no_more_evil()
    {
        float ep = CombatEngine.GetEPCostForMonsterAttack(PlayerWithEp(305f), new Monster { Align = 4 });
        Assert.Equal(0f, ep);
    }

    [Fact]
    public void Good_attacker_is_floored_up_to_ep_ten_on_first_evil_act()
    {
        // Floor: a Saint (EP -250) attacking an innocent jumps straight to EP 10, so the gain is
        // 10 - (-250) = 260.
        float ep = CombatEngine.GetEPCostForMonsterAttack(PlayerWithEp(-250f), new Monster { Align = 0 });
        Assert.Equal(260f, ep);
        Assert.Equal(10f, -250f + ep);
    }

    [Fact]
    public void Pvp_against_neutral_target_grants_flat_ten()
    {
        float ep = CombatEngine.GetEPCostForPlayerAttack(PlayerWithEp(0f), PlayerWithEp(0f), recentlyAttackedBy: false);
        Assert.Equal(10f, ep);
    }

    [Fact]
    public void Pvp_against_good_target_doubles_and_saint_triples()
    {
        Assert.Equal(20f, CombatEngine.GetEPCostForPlayerAttack(PlayerWithEp(0f), PlayerWithEp(-100f), recentlyAttackedBy: false));
        Assert.Equal(30f, CombatEngine.GetEPCostForPlayerAttack(PlayerWithEp(0f), PlayerWithEp(-250f), recentlyAttackedBy: false));
    }

    [Fact]
    public void Pvp_against_evil_target_or_in_self_defense_grants_nothing()
    {
        Assert.Equal(0f, CombatEngine.GetEPCostForPlayerAttack(PlayerWithEp(0f), PlayerWithEp(35f), recentlyAttackedBy: false));
        Assert.Equal(0f, CombatEngine.GetEPCostForPlayerAttack(PlayerWithEp(0f), PlayerWithEp(0f), recentlyAttackedBy: true));
    }

    [Fact]
    public void Seedy_attacker_still_gains_evil_in_pvp()
    {
        // The PvP scaling must key off the TARGET's goodness, not the attacker's alignment, and
        // must not be gated by the attacker already being Seedy.
        float ep = CombatEngine.GetEPCostForPlayerAttack(PlayerWithEp(35f), PlayerWithEp(0f), recentlyAttackedBy: false);
        Assert.Equal(10f, ep);
    }

    // ── rob (the evil-point path with a base of 1) ────────────────────

    [Fact]
    public void Rob_against_neutral_target_grants_flat_one()
    {
        Assert.Equal(1f, CombatEngine.GetEPCostForRob(PlayerWithEp(0f), PlayerWithEp(0f)));
    }

    [Fact]
    public void Rob_against_good_target_doubles_and_saint_triples()
    {
        Assert.Equal(2f, CombatEngine.GetEPCostForRob(PlayerWithEp(0f), PlayerWithEp(-100f)));
        Assert.Equal(3f, CombatEngine.GetEPCostForRob(PlayerWithEp(0f), PlayerWithEp(-250f)));
    }

    [Fact]
    public void Rob_against_seedy_or_outlaw_target_grants_nothing()
    {
        // Robbing a Seedy+ victim (EP >= 30) is "free" — stock zeroes the charge in that branch.
        Assert.Equal(0f, CombatEngine.GetEPCostForRob(PlayerWithEp(0f), PlayerWithEp(30f)));
        Assert.Equal(0f, CombatEngine.GetEPCostForRob(PlayerWithEp(0f), PlayerWithEp(50f)));
    }

    [Fact]
    public void Rob_floors_a_good_robber_up_to_ep_ten()
    {
        // A Saint robber (EP -250) robbing a neutral jumps straight to EP 10: gain = 10 - (-250) = 260.
        float ep = CombatEngine.GetEPCostForRob(PlayerWithEp(-250f), PlayerWithEp(0f));
        Assert.Equal(260f, ep);
        Assert.Equal(10f, -250f + ep);
    }

    [Fact]
    public void Rob_by_a_too_far_robber_grants_nothing()
    {
        Assert.Equal(0f, CombatEngine.GetEPCostForRob(PlayerWithEp(305f), PlayerWithEp(0f)));
    }

    // ── EP value cap at the gain chokepoint (refuse positive gain once EP > 300) ──

    [Fact]
    public void Gain_overshoots_past_300_once_then_freezes()
    {
        // At/under the cap the gain applies even if it lands over 300 (gate is strict ">300", checked
        // BEFORE the add, with no clamp) — stock overshoot.
        var p = PlayerWithEp(295f);
        Assert.True(p.TryAddEvilPoints(10f));
        Assert.Equal(305f, p.EvilPoints);

        // Now over the cap: further positive gains are refused and EP stays put (frozen overshoot).
        Assert.False(p.TryAddEvilPoints(10f));
        Assert.Equal(305f, p.EvilPoints);
    }

    [Fact]
    public void Exactly_300_can_still_gain_but_301_cannot()
    {
        var atCap = PlayerWithEp(300f);
        Assert.True(atCap.TryAddEvilPoints(10f)); // 300 is not > 300
        Assert.Equal(310f, atCap.EvilPoints);

        var overCap = PlayerWithEp(301f);
        Assert.False(overCap.TryAddEvilPoints(10f));
        Assert.Equal(301f, overCap.EvilPoints);
    }

    [Fact]
    public void Negative_deltas_apply_even_above_the_cap()
    {
        // Good deeds / forgiveness / victim `forgive` must still pull a maxed-evil player back down.
        var p = PlayerWithEp(350f);
        Assert.True(p.TryAddEvilPoints(-20f));
        Assert.Equal(330f, p.EvilPoints);
    }
}
