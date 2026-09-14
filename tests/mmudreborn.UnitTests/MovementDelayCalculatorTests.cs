using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class MovementDelayCalculatorTests
{
    // Enc > 100% blocks; <= 100% is allowed.
    [Theory]
    [InlineData(0, false)]
    [InlineData(66, false)]
    [InlineData(100, false)]
    [InlineData(101, true)]
    [InlineData(250, true)]
    public void IsOverEncumbered_blocks_only_above_100_percent(int encPct, bool expectedBlocked)
    {
        Assert.Equal(expectedBlocked, MovementDelayCalculator.IsOverEncumbered(encPct));
    }

    // <= 66% costs 1 fast-tick, 67-100% costs 2 (the heavy breakpoint).
    [Theory]
    [InlineData(0, 1)]
    [InlineData(33, 1)]
    [InlineData(66, 1)]
    [InlineData(67, 2)]
    [InlineData(100, 2)]
    public void Encumbrance_breakpoint_is_66_percent(int encPct, int expectedTicks)
    {
        int ticks = MovementDelayCalculator.ComputeDelayTicks(encPct, isDragging: false, isHasted: false, isSlowed: false, recentMoveCount: 0);
        Assert.Equal(expectedTicks, ticks);
    }

    [Fact]
    public void Dragging_doubles_base_and_heavy_increments()
    {
        // base 2 dragging; heavy adds +2 dragging => 4.
        Assert.Equal(2, MovementDelayCalculator.ComputeDelayTicks(0, isDragging: true, isHasted: false, isSlowed: false, recentMoveCount: 0));
        Assert.Equal(4, MovementDelayCalculator.ComputeDelayTicks(80, isDragging: true, isHasted: false, isSlowed: false, recentMoveCount: 0));
    }

    [Fact]
    public void Slow_doubles_and_haste_halves()
    {
        // slow x2: light 1 -> 2.
        Assert.Equal(2, MovementDelayCalculator.ComputeDelayTicks(0, isDragging: false, isHasted: false, isSlowed: true, recentMoveCount: 0));
        // haste /2 on a heavy 2 -> 1 (haste cancels the heavy penalty).
        Assert.Equal(1, MovementDelayCalculator.ComputeDelayTicks(80, isDragging: false, isHasted: true, isSlowed: false, recentMoveCount: 0));
        // haste on a light 1 floors at 1 (1/2 = 0 -> 1).
        Assert.Equal(1, MovementDelayCalculator.ComputeDelayTicks(0, isDragging: false, isHasted: true, isSlowed: false, recentMoveCount: 0));
        // slow then haste cancel: light 1 *2 /2 = 1.
        Assert.Equal(1, MovementDelayCalculator.ComputeDelayTicks(0, isDragging: false, isHasted: true, isSlowed: true, recentMoveCount: 0));
    }

    [Theory]
    [InlineData(0, 1)]   // 1st move
    [InlineData(2, 1)]   // 2nd move (counter == threshold, no fatigue yet)
    [InlineData(3, 2)]   // 3rd move exceeds the threshold -> +1
    [InlineData(9, 2)]   // stays +1 (single increment, not cumulative)
    public void Rapid_move_fatigue_adds_one_tick_after_the_second_move_when_light(int recentMoveCount, int expectedTicks)
    {
        int ticks = MovementDelayCalculator.ComputeDelayTicks(0, isDragging: false, isHasted: false, isSlowed: false, recentMoveCount);
        Assert.Equal(expectedTicks, ticks);
    }

    [Fact]
    public void Heavy_players_never_accumulate_rapid_move_fatigue()
    {
        // Heavy = 2 ticks; a high recent-move count must NOT add fatigue (stock resets the counter per fast tick when heavy).
        int ticks = MovementDelayCalculator.ComputeDelayTicks(80, isDragging: false, isHasted: false, isSlowed: false, recentMoveCount: 9);
        Assert.Equal(2, ticks);
    }

    [Fact]
    public void Result_is_always_at_least_one_tick()
    {
        // Every combination floors at 1 (haste can't drop a move below a single tick).
        for (int enc = 0; enc <= 100; enc += 10)
            foreach (bool drag in new[] { false, true })
                foreach (bool haste in new[] { false, true })
                    foreach (bool slow in new[] { false, true })
                        Assert.True(MovementDelayCalculator.ComputeDelayTicks(enc, drag, haste, slow, 0) >= 1);
    }
}
