using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// The movement ring: newest entry at logical index 0, older entries
// shift down, oldest falls off at capacity. TRACK relies on slot[i-1] being the room entered one
// step after slot[i].
public sealed class MovementTrailTests
{
    [Fact]
    public void Record_keeps_newest_first()
    {
        var trail = new MovementTrail(MovementTrail.PlayerCapacity);
        trail.Record(1, 10);
        trail.Record(1, 11);
        trail.Record(2, 12);

        Assert.Equal(3, trail.Count);
        Assert.Equal((2, 12), trail[0]); // newest
        Assert.Equal((1, 11), trail[1]);
        Assert.Equal((1, 10), trail[2]); // oldest
    }

    [Fact]
    public void Record_caps_at_capacity_and_drops_oldest()
    {
        var trail = new MovementTrail(MovementTrail.MonsterCapacity); // 10

        for (int room = 0; room < 15; room++)
            trail.Record(1, room);

        Assert.Equal(MovementTrail.MonsterCapacity, trail.Count);
        Assert.Equal((1, 14), trail[0]);                              // last recorded is newest
        Assert.Equal((1, 5), trail[MovementTrail.MonsterCapacity - 1]); // rooms 0..4 fell off
    }
}
