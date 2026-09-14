using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Healing rain (#145) is Targets=13 — the party-area beneficial type: caster plus every party member
/// in the room, one shared roll, full magnitude to each (Targets 13 skips the divide-by-target-count
/// that 3/5/9/10 carry). Stock scaling is MinBase 4 +lvl/3, MaxBase 10 +lvl/2, capped at level 25.
/// </summary>
public class HealingRainMagnitudeTests
{
    private static GameSpell HealingRain() => new()
    {
        Number = 145,
        Name = "healing rain",
        Targets = 13,
        MinBase = 4,
        MaxBase = 10,
        MinInc = 1,
        MinIncLvls = 3,
        MaxInc = 1,
        MaxIncLvls = 2,
        Cap = 25,
    };

    [Theory]
    [InlineData(1, 4, 10)]     // 4 + 1/3 = 4,   10 + 1/2 = 10
    [InlineData(12, 8, 16)]    // 4 + 4   = 8,   10 + 6   = 16
    [InlineData(25, 12, 22)]   // 4 + 8   = 12,  10 + 12  = 22   (at the cap)
    [InlineData(60, 12, 22)]   // capped: level 60 still bands as level 25
    public void Band_matches_the_stock_scaling(int level, int expectedMin, int expectedMax)
    {
        var (min, max) = CommandParser.ComputeSpellMagnitudeBand(HealingRain(), level);
        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
    }

    [Fact]
    public void Cap_prevents_unbounded_growth_at_high_level()
    {
        var atCap = CommandParser.ComputeSpellMagnitudeBand(HealingRain(), 25);
        var wayPast = CommandParser.ComputeSpellMagnitudeBand(HealingRain(), 200);
        Assert.Equal(atCap, wayPast);
    }

    [Fact]
    public void Rolls_stay_inside_the_band()
    {
        var spell = HealingRain();
        var (min, max) = CommandParser.ComputeSpellMagnitudeBand(spell, 25);
        for (int i = 0; i < 20_000; i++)
            Assert.InRange(CommandParser.RollSpellMagnitude(spell, 25), min, max);
    }

    [Fact]
    public void Party_heal_is_never_stronger_per_head_than_a_single_target_heal()
    {
        // Sanity against the "hitting harder than expected" report: at cap, greater healing (#89,
        // single target, 5 +lvl, 10 +2*lvl, cap 30) must out-heal a rain tick per recipient.
        var greaterHealing = new GameSpell
        {
            Number = 89, Name = "greater healing", Targets = 2,
            MinBase = 5, MaxBase = 10, MinInc = 1, MinIncLvls = 1, MaxInc = 2, MaxIncLvls = 1, Cap = 30,
        };

        var rain = CommandParser.ComputeSpellMagnitudeBand(HealingRain(), 30);
        var single = CommandParser.ComputeSpellMagnitudeBand(greaterHealing, 30);

        Assert.True(single.Max > rain.Max, $"single-target {single.Max} should exceed rain {rain.Max}");
        Assert.True(single.Min > rain.Min, $"single-target {single.Min} should exceed rain {rain.Min}");
    }
}
