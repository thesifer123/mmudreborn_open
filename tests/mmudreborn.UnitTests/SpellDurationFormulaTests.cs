using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The cast duration formula:
//   effLvl = min(casterLvl, spell.Cap)            (Cap<=1 ⇒ use casterLvl raw)
//   dur    = spell.Duration
//   if DurIncLvls > 0: dur += (effLvl/DurIncLvls)*DurInc
//   hi     = DurRand * effLvl
//   if hi > dur: dur = genrdn(dur, hi+1)
//   dur    = (casterBonusPct + 100) * dur / 100
public sealed class SpellDurationFormulaTests
{
    [Fact]
    public void Base_duration_only_when_no_per_level_scaling()
    {
        var spell = new GameSpell { Duration = 10 };
        Assert.Equal(10, CommandParser.RollSpellDuration(spell, casterLevel: 5));
    }

    [Fact]
    public void Per_level_step_adds_DurInc_every_DurIncLvls()
    {
        // Every 4 levels add 1; at level 12 that's +3 → base 10 → 13.
        var spell = new GameSpell { Duration = 10, DurIncLvls = 4, DurInc = 1 };
        Assert.Equal(13, CommandParser.RollSpellDuration(spell, casterLevel: 12));
    }

    [Fact]
    public void Cap_clamps_effective_level_before_step_calc()
    {
        // Cap 5 means effLvl = min(20, 5) = 5; (5/4)*1 = 1 → 10+1 = 11.
        var spell = new GameSpell { Duration = 10, DurIncLvls = 4, DurInc = 1, Cap = 5 };
        Assert.Equal(11, CommandParser.RollSpellDuration(spell, casterLevel: 20));
    }

    [Fact]
    public void Cap_of_one_or_less_uses_caster_level_raw()
    {
        // `if (spell.MaxLvl < 1 || casterLevel <= spell.MaxLvl) effLvl = casterLevel`.
        // C# treats Cap <= 0 as "no cap"; the V1.11p DAT never stores Cap=1 distinctly.
        var spell = new GameSpell { Duration = 10, DurIncLvls = 4, DurInc = 1, Cap = 0 };
        Assert.Equal(15, CommandParser.RollSpellDuration(spell, casterLevel: 20));   // (20/4)*1=5
    }

    [Fact]
    public void Caster_duration_bonus_pct_scales_after_step()
    {
        // 50% bonus on a base-10 spell: (50+100)*10/100 = 15.
        var spell = new GameSpell { Duration = 10 };
        Assert.Equal(15, CommandParser.RollSpellDuration(spell, casterLevel: 5, casterDurationBonusPct: 50));
    }

    [Fact]
    public void Negative_bonus_can_shorten_duration()
    {
        // Stock multiplies by (bonus+100)/100 regardless of sign — a hypothetical -50% bonus halves it.
        var spell = new GameSpell { Duration = 20 };
        Assert.Equal(10, CommandParser.RollSpellDuration(spell, casterLevel: 5, casterDurationBonusPct: -50));
    }

    [Fact]
    public void DurRand_zero_means_no_random_spread()
    {
        // The vast majority of stock spells store DurRand=0 — the result must be deterministic and
        // match base + step, so all our pre-DurRand expectations hold.
        var spell = new GameSpell { Duration = 30, DurIncLvls = 5, DurInc = 2, DurRand = 0 };
        for (int trial = 0; trial < 50; trial++)
            Assert.Equal(34, CommandParser.RollSpellDuration(spell, casterLevel: 10));   // 30 + (10/5)*2
    }

    [Fact]
    public void DurRand_at_or_below_stepped_base_is_a_no_op()
    {
        // hi = DurRand*effLvl = 1*10 = 10; base = 30. hi <= base ⇒ no random spread.
        var spell = new GameSpell { Duration = 30, DurRand = 1 };
        for (int trial = 0; trial < 50; trial++)
            Assert.Equal(30, CommandParser.RollSpellDuration(spell, casterLevel: 10));
    }

    [Fact]
    public void DurRand_above_stepped_base_rolls_between_base_and_hi_inclusive()
    {
        // base = 2, hi = 120 * 5 = 600. Stock rolls in [2, 600] inclusive.
        // Sample many times and assert the bounds + that we observe variance.
        var spell = new GameSpell { Duration = 2, DurRand = 120 };
        var observed = new HashSet<int>();
        for (int trial = 0; trial < 500; trial++)
        {
            int dur = CommandParser.RollSpellDuration(spell, casterLevel: 5);
            Assert.InRange(dur, 2, 600);
            observed.Add(dur);
        }
        // With 500 rolls over a 599-wide window, we must have seen at least a handful of distinct
        // values — proves the random spread is actually firing.
        Assert.True(observed.Count > 10, $"expected >10 distinct durations, saw {observed.Count}");
    }
}
