using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #59: mihe/mahe/grhe all healed for the same low amount because ApplyHealEffect was rolling
// just MinBase..MaxBase with no level scaling. The stock cast formula
// grows min and max independently from
// their own per-level slopes, capped by spell.Cap. These tests pin the band math against the live
// game_data values for the three healing spells so a regression in either slope or the cap
// behavior is caught immediately.
public sealed class SpellMagnitudeBandTests
{
    [Fact]
    public void Minor_healing_at_level_1_matches_base_band()
    {
        // (MinInc/MinIncLvls)*1 = 0; (MaxInc/MaxIncLvls)*1 = 0 — band is the raw base.
        var band = CommandParser.ComputeSpellMagnitudeBand(MinorHealing(), casterLevel: 1);
        Assert.Equal((2, 8), band);
    }

    [Fact]
    public void Minor_healing_caps_at_cap_level_not_caster_level()
    {
        // Cap=10. At level 60, effLvl is clamped to 10; min += (10/3)*1=3, max += (10/3)*2=6.
        var band = CommandParser.ComputeSpellMagnitudeBand(MinorHealing(), casterLevel: 60);
        Assert.Equal((5, 14), band);
    }

    [Fact]
    public void Major_healing_max_grows_every_level_min_every_three_levels()
    {
        // MaxInc=1/MaxIncLvls=1, MinInc=1/MinIncLvls=3. Cap=30 clamps level 60 to 30.
        // min = 6 + 30/3 = 16; max = 10 + 30/1 = 40. mahe outscales mihe by ~3x in average.
        var band = CommandParser.ComputeSpellMagnitudeBand(MajorHealing(), casterLevel: 60);
        Assert.Equal((16, 40), band);
    }

    [Fact]
    public void Greater_healing_outscales_major_at_cap()
    {
        // grhe slopes: MinInc=1/MinIncLvls=1, MaxInc=2/MaxIncLvls=1. Cap=30.
        // min = 5 + 30 = 35; max = 10 + 60 = 70. The reported "all heal the same" pre-fix lived
        // here — without scaling this rolled 5..10 at any level, identical to mahe's 6..10 band.
        var band = CommandParser.ComputeSpellMagnitudeBand(GreaterHealing(), casterLevel: 60);
        Assert.Equal((35, 70), band);
    }

    [Fact]
    public void Healing_average_strictly_increases_with_tier_at_high_level()
    {
        // The user-visible repro: pre-fix, mihe/mahe/grhe all rolled ~5..10 at any level, so their
        // averages were indistinguishable. With scaling, mean(mihe) < mean(mahe) < mean(grhe).
        // (The bands themselves can overlap at the seams — grhe min 35 < mahe max 40 — what matters
        // is that average healing per cast strictly grows with the tier.)
        static double Avg((int Min, int Max) band) => (band.Min + band.Max) / 2.0;

        double minor = Avg(CommandParser.ComputeSpellMagnitudeBand(MinorHealing(), casterLevel: 60));
        double major = Avg(CommandParser.ComputeSpellMagnitudeBand(MajorHealing(), casterLevel: 60));
        double greater = Avg(CommandParser.ComputeSpellMagnitudeBand(GreaterHealing(), casterLevel: 60));

        Assert.True(major > minor, $"mean(mahe)={major} should exceed mean(mihe)={minor}");
        Assert.True(greater > major, $"mean(grhe)={greater} should exceed mean(mahe)={major}");
    }

    [Fact]
    public void Min_clamps_to_max_when_min_slope_outpaces_max_slope()
    {
        // Defensive case: contrived spell where MinInc grows faster than MaxInc — the stock formula
        // explicitly clamps min to max.
        var spell = new GameSpell { MinBase = 0, MaxBase = 1, MinInc = 5, MinIncLvls = 1, MaxInc = 1, MaxIncLvls = 1, Cap = 0 };
        var (min, max) = CommandParser.ComputeSpellMagnitudeBand(spell, casterLevel: 10);
        Assert.Equal(max, min);
    }

    [Theory]
    // Reference (spell editor / user-supplied): meteor swarm is "20 to level+10, max 35 at
    // level 25, 45 at 35+". min = MinBase(20) fixed; max = MaxBase(10) + min(level, Cap=35).
    [InlineData(25, 20, 35)]   // at req level: 20..35
    [InlineData(35, 20, 45)]   // at the cap level: 20..45 (matches "Min/Avg/Max 20/32/45 @lvl 35")
    [InlineData(60, 20, 45)]   // above the cap: damage frozen at the level-35 band
    public void Meteor_swarm_band_matches_reference(int level, int expectedMin, int expectedMax)
    {
        var band = CommandParser.ComputeSpellMagnitudeBand(MeteorSwarm(), level);
        Assert.Equal((expectedMin, expectedMax), band);
    }

    [Theory]
    // Bug #142: barkskin (#34) — Min/MaxBase 10, Inc 10/10 per 10 levels, Cap=20. The DR magnitude
    // (ability 7, value 0 ⇒ rolled magnitude) must clamp at level 20, giving a flat 30 (= +3 DR) from
    // then on. Pre-fix the buff path used a levels-above-req formula that ignored Cap and grew without
    // bound (40+ at level ~40, the reported "40 AC / 0 DR"). Min==Max here, so the roll is deterministic.
    [InlineData(7, 17, 17)]    // at req level: 10 + (10*7/10) = 17
    [InlineData(20, 30, 30)]   // at the cap level: 10 + (10*20/10) = 30
    [InlineData(60, 30, 30)]   // above the cap: frozen at the level-20 band, NOT climbing
    public void Barkskin_dr_magnitude_caps_at_level_20(int level, int expectedMin, int expectedMax)
    {
        var band = CommandParser.ComputeSpellMagnitudeBand(Barkskin(), level);
        Assert.Equal((expectedMin, expectedMax), band);
    }

    [Theory]
    // Ticket #1 spell — turn undead (#18). User-supplied spec: "LVL Cap 20, Damage 32 to 75, Min
    // 12+(1*lvl), Max 15+(3*lvl)". Both slopes are per-1-level, so the band grows each level until the
    // level-20 cap freezes it at 32..75. Every cast path (single/area/monster) now rolls THIS band.
    [InlineData(3, 15, 24)]    // req level 3:  12+3   .. 15+9
    [InlineData(20, 32, 75)]   // cap level 20: 12+20  .. 15+60   (the "32 to 75" cap)
    [InlineData(50, 32, 75)]   // above the cap: frozen at the level-20 band
    public void Turn_undead_band_caps_at_level_20(int level, int expectedMin, int expectedMax)
    {
        var band = CommandParser.ComputeSpellMagnitudeBand(TurnUndead(), level);
        Assert.Equal((expectedMin, expectedMax), band);
    }

    [Theory]
    // Live bug (Azrandimon #1030): its spell-attacks (AtkType 2) cast at the attack's AtkMax level = 50.
    // Monster casts used a made-up "base roll + (castLevel-ReqLevel)/MaxIncLvls*MaxInc" formula that scaled
    // ONLY the max side, so when ReqLevel met the cast level (inferno ReqLevel 50, cast 50) it added nothing
    // and returned the raw MinBase..MaxBase band. For inferno that band is -15..20 → "A pillar of flame
    // appears and scorches you for -6 damage!" (negative = a heal). The stock area cast
    // scales BOTH min and max by level, exactly like ComputeSpellMagnitudeBand — so
    // these three Azrandimon damage spells land on the MME/NMR "@lvl 50" numbers and never go negative.
    [InlineData(1048, 135, 620)]   // inferno   @50: (-15 + 3*50) .. (20 + 12*50)
    [InlineData(1247, 230, 575)]   // hellstorm @50: (30 + 4*50) .. (75 + 10*50)
    [InlineData(1214, 250, 500)]   // hellfire  @50: (0 + 5*50) .. (0 + 10*50)
    public void Azrandimon_damage_spells_match_mme_band_at_cast_level_50(int number, int expectedMin, int expectedMax)
    {
        var spell = number switch
        {
            1048 => Inferno(),
            1247 => Hellstorm(),
            _ => Hellfire(),
        };
        var band = CommandParser.ComputeSpellMagnitudeBand(spell, casterLevel: 50);
        Assert.Equal((expectedMin, expectedMax), band);
        Assert.True(band.Min >= 0, $"spell {number} min {band.Min} must not be negative at its intended cast level");
    }

    // game_data spell 1048 (inferno): the swingy -15..20 base that made the pre-fix formula heal the target.
    private static GameSpell Inferno() => new()
    {
        Number = 1048, ReqLevel = 50, MinBase = -15, MaxBase = 20, Cap = 65,
        MinIncLvls = 1, MinInc = 3, MaxIncLvls = 1, MaxInc = 12,
    };

    private static GameSpell Hellstorm() => new()
    {
        Number = 1247, ReqLevel = 50, MinBase = 30, MaxBase = 75, Cap = 65,
        MinIncLvls = 1, MinInc = 4, MaxIncLvls = 1, MaxInc = 10,
    };

    private static GameSpell Hellfire() => new()
    {
        Number = 1214, ReqLevel = 0, MinBase = 0, MaxBase = 0, Cap = 0,
        MinIncLvls = 1, MinInc = 5, MaxIncLvls = 1, MaxInc = 10,
    };

    // game_data spell 18 (turn undead): ticket #1 spell — undead-only (ability 23), level-capped at 20.
    private static GameSpell TurnUndead() => new()
    {
        Number = 18, ReqLevel = 3, MinBase = 12, MaxBase = 15, Cap = 20,
        MinIncLvls = 1, MinInc = 1, MaxIncLvls = 1, MaxInc = 3,
    };

    // game_data spell 34 (barkskin): the DR magnitude band that ability 7 draws from.
    private static GameSpell Barkskin() => new()
    {
        Number = 34, MinBase = 10, MaxBase = 10, Cap = 20,
        MaxIncLvls = 10, MaxInc = 10, MinIncLvls = 10, MinInc = 10,
    };

    // game_data values for spells 13/17/89, copied here so the test isn't dependent on a live DB
    // and the band math is pinned even if the data is later retuned.
    private static GameSpell MinorHealing() => new()
    {
        Number = 13, MinBase = 2, MaxBase = 8, Cap = 10,
        MaxIncLvls = 3, MaxInc = 2, MinIncLvls = 3, MinInc = 1,
    };

    private static GameSpell MajorHealing() => new()
    {
        Number = 17, MinBase = 6, MaxBase = 10, Cap = 30,
        MaxIncLvls = 1, MaxInc = 1, MinIncLvls = 3, MinInc = 1,
    };

    private static GameSpell GreaterHealing() => new()
    {
        Number = 89, MinBase = 5, MaxBase = 10, Cap = 30,
        MaxIncLvls = 1, MaxInc = 2, MinIncLvls = 1, MinInc = 1,
    };

    // game_data spell 285 (meteor swarm): MinBase 20 > MaxBase 10 (the max band only overtakes the min
    // once the per-level slope kicks in), MaxInc=1/MaxIncLvls=1, no min slope, Cap=35.
    private static GameSpell MeteorSwarm() => new()
    {
        Number = 285, MinBase = 20, MaxBase = 10, Cap = 35,
        MaxIncLvls = 1, MaxInc = 1, MinIncLvls = 0, MinInc = 0,
    };
}
