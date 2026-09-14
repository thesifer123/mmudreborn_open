using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class CurrencyHelperTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(0, 0, 0, 0, 7, 7)]
    [InlineData(0, 0, 0, 1, 0, 10)]
    [InlineData(0, 0, 1, 0, 0, 100)]
    [InlineData(0, 1, 0, 0, 0, 10_000)]
    [InlineData(1, 0, 0, 0, 0, 1_000_000)]
    [InlineData(2, 3, 4, 5, 6, 2_030_456)]
    public void ToCopper_sums_denominations_by_conversion_constants(long runic, long platinum, long gold, long silver, long copper, long expected)
    {
        Assert.Equal(expected, CurrencyHelper.ToCopper(runic, platinum, gold, silver, copper));
    }

    [Fact]
    public void SetFromCopper_splits_total_into_highest_denominations_first()
    {
        var player = new Player();

        CurrencyHelper.SetFromCopper(player, 2_030_456);

        Assert.Equal(2, player.Runic);
        Assert.Equal(3, player.Platinum);
        Assert.Equal(4, player.Gold);
        Assert.Equal(5, player.Silver);
        Assert.Equal(6, player.Copper);
    }

    [Fact]
    public void SetFromCopper_zero_clears_all_denominations()
    {
        var player = new Player { Runic = 9, Platinum = 9, Gold = 9, Silver = 9, Copper = 9 };

        CurrencyHelper.SetFromCopper(player, 0);

        Assert.Equal(0, player.Runic);
        Assert.Equal(0, player.Platinum);
        Assert.Equal(0, player.Gold);
        Assert.Equal(0, player.Silver);
        Assert.Equal(0, player.Copper);
    }

    [Fact]
    public void SetFromCopper_clamps_negative_input_to_zero()
    {
        var player = new Player { Copper = 99 };

        CurrencyHelper.SetFromCopper(player, -500);

        Assert.Equal(0, player.Runic);
        Assert.Equal(0, player.Platinum);
        Assert.Equal(0, player.Gold);
        Assert.Equal(0, player.Silver);
        Assert.Equal(0, player.Copper);
    }

    [Fact]
    public void Normalize_collapses_overflow_into_higher_denominations()
    {
        var player = new Player
        {
            Copper = 25,
            Silver = 12,
            Gold = 0,
            Platinum = 0,
            Runic = 0,
        };

        CurrencyHelper.Normalize(player);

        // 25 cp + 120 cp (12 silver) = 145 cp → 1 gold, 4 silver, 5 copper
        Assert.Equal(1, player.Gold);
        Assert.Equal(4, player.Silver);
        Assert.Equal(5, player.Copper);
        Assert.Equal(0, player.Platinum);
        Assert.Equal(0, player.Runic);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    [InlineData(123_456_789L)]
    public void SetFromCopper_then_ToCopper_round_trips(long totalCopper)
    {
        var player = new Player();

        CurrencyHelper.SetFromCopper(player, totalCopper);

        Assert.Equal(totalCopper, CurrencyHelper.ToCopper(player));
    }
}
