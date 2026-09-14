using System.Collections.Generic;
using mmudreborn.Data.Models;
using Xunit;

namespace mmudreborn.UnitTests;

// Opening a container (ItemType 8) rolls coins straight into the
// opener's purse — silently, AFTER the loot spell — as `player.<denom> += lngrnd(0, item.<max>)` for
// each of runic/platinum/gold/silver/copper. The maxes live at item record bytes 944/948/952/956/960.
// The roll has an EXCLUSIVE top (the monster-drop path adds +1 to include max; OPEN does
// not), so the roll is Random.Next(0, max) → range [0, max-1], and a max of 0 yields 0.
public sealed class OpenChestCoinsTests
{
    [Fact]
    public void RollOpenCoins_only_rolls_non_zero_denominations()
    {
        // Alder chest (#974) maxes.
        var chest = new Item { OpenRunic = 0, OpenPlatinum = 5, OpenGold = 750, OpenSilver = 2500, OpenCopper = 0 };

        var rolledBounds = new List<int>();
        // Fake RNG returns max-1 (the top of the exclusive [0, max) range) and records the bounds it
        // was asked to roll, so we can assert it is NEVER invoked for a zero-max denomination.
        var coins = chest.RollOpenCoins(max => { rolledBounds.Add(max); return max - 1; });

        Assert.Equal((0, 4, 749, 2499, 0), coins);
        // Only the three non-zero maxes were rolled (no roll for runic=0 or copper=0).
        Assert.Equal(new[] { 5, 750, 2500 }, rolledBounds);
    }

    [Fact]
    public void RollOpenCoins_with_all_zero_maxes_yields_nothing_and_never_rolls()
    {
        var plainBox = new Item();
        Assert.False(plainBox.HasOpenCoins);

        bool rolled = false;
        var coins = plainBox.RollOpenCoins(_ => { rolled = true; return 99; });

        Assert.Equal((0, 0, 0, 0, 0), coins);
        Assert.False(rolled);
    }

    [Fact]
    public void RollOpenCoins_respects_exclusive_lower_and_upper_bounds()
    {
        var chest = new Item { OpenGold = 750, OpenSilver = 2500 };

        // Bottom of the range.
        Assert.Equal((0, 0, 0, 0, 0), chest.RollOpenCoins(_ => 0));
        // A representative interior roll matching the live alder-chest capture (gold 226, silver 1051):
        // both are within [0, max-1], i.e. <= 749 and <= 2499 respectively.
        var sampled = chest.RollOpenCoins(max => max == 750 ? 226 : 1051);
        Assert.Equal((0, 0, 226, 1051, 0), sampled);
    }

    [Fact]
    public void HasOpenCoins_true_when_any_denomination_non_zero()
    {
        Assert.True(new Item { OpenCopper = 1 }.HasOpenCoins);
        Assert.True(new Item { OpenRunic = 1 }.HasOpenCoins);
        Assert.False(new Item { OpenGold = 0 }.HasOpenCoins);
    }

}
