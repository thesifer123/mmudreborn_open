using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class GameWorldCombatPulseTests
{
    [Fact]
    public void Next_combat_pulse_aligns_staggered_times_to_same_shared_deadline()
    {
        DateTime epochUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime firstActionAt = epochUtc.AddMilliseconds(100);
        DateTime secondActionAt = epochUtc.AddMilliseconds(3100);
        DateTime expectedPulseAt = epochUtc.AddSeconds(5);

        Assert.Equal(expectedPulseAt, GameWorld.GetNextCombatPulseUtc(epochUtc, firstActionAt));
        Assert.Equal(expectedPulseAt, GameWorld.GetNextCombatPulseUtc(epochUtc, secondActionAt));
    }

    [Fact]
    public void Next_combat_pulse_moves_boundary_actions_to_following_round()
    {
        DateTime epochUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime actionAtBoundary = epochUtc.AddSeconds(5);

        Assert.Equal(epochUtc.AddSeconds(10), GameWorld.GetNextCombatPulseUtc(epochUtc, actionAtBoundary));
    }
}