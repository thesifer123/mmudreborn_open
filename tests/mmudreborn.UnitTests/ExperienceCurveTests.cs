using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// XP curve: seed × per-level ratio product with integer
// truncation, NOT the old baseExp·Σi² quadratic.
public sealed class ExperienceCurveTests
{
    [Fact]
    public void Total_exp_matches_the_stock_ratio_product()
    {
        // factor 0 → seed 1000; multiply by the per-level ratio table with truncation:
        // L2=1000, L3=1000·(40/20)=2000, L4=floor(2000·44/24)=3666, L5=floor(3666·44/24)=6721.
        Assert.Equal(0, Player.GetTotalExpForLevel(1, 0, 0));
        Assert.Equal(1000, Player.GetTotalExpForLevel(2, 0, 0));
        Assert.Equal(2000, Player.GetTotalExpForLevel(3, 0, 0));
        Assert.Equal(3666, Player.GetTotalExpForLevel(4, 0, 0));
        Assert.Equal(6721, Player.GetTotalExpForLevel(5, 0, 0));
        // The old quadratic curve gave 5000 at L3 — guard against regressing to it.
        Assert.NotEqual(5000, Player.GetTotalExpForLevel(3, 0, 0));
    }

    [Fact]
    public void Seed_scales_with_race_plus_class_factor()
    {
        // seed = (race+class)·10 + 1000; L2 == seed (first ratio is 1/1).
        Assert.Equal(1500, Player.GetTotalExpForLevel(2, 30, 20));
    }

    [Fact]
    public void Increases_monotonically_into_the_two_part_range()
    {
        long prev = -1;
        for (int lvl = 1; lvl <= 80; lvl++)
        {
            long v = Player.GetTotalExpForLevel(lvl, 0, 0);
            Assert.True(v > prev, $"level {lvl} exp {v} should exceed previous {prev}");
            prev = v;
        }
        // Crosses past int32 (L71 is the first), exercising the two-part high*1e9+low path.
        Assert.True(Player.GetTotalExpForLevel(71, 0, 0) > int.MaxValue);
    }

    [Fact]
    public void High_part_survives_the_stock_32bit_cliff()
    {
        // Stock computed high*num*1e6 in 32 bits; for a Human Warrior (factor 0) the
        // product wraps at L101, collapsing per-level needed exp from ~4.5e9 to ~2e8 (the exp
        // chart "cliff": L101 read 42,112,732,404). We use the community-patched 64-bit form.
        Assert.Equal(41_907_129_400L, Player.GetTotalExpForLevel(100, 0, 0));
        Assert.Equal(46_407_699_700L, Player.GetTotalExpForLevel(101, 0, 0));
        Assert.Equal(51_408_315_600L, Player.GetTotalExpForLevel(102, 0, 0));
        Assert.NotEqual(42_112_732_404L, Player.GetTotalExpForLevel(101, 0, 0));

        // Growth must never collapse past the cliff: each "needed" step stays >= the prior one.
        long prevTotal = Player.GetTotalExpForLevel(90, 0, 0);
        long prevNeeded = 0;
        for (int lvl = 91; lvl <= 130; lvl++)
        {
            long total = Player.GetTotalExpForLevel(lvl, 0, 0);
            long needed = total - prevTotal;
            Assert.True(needed >= prevNeeded,
                $"level {lvl} needed {needed} regressed below previous {prevNeeded}");
            prevTotal = total;
            prevNeeded = needed;
        }
    }
}
