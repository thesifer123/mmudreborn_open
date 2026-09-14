using System.Linq;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The proactive health monitor: BuildHealthAlertReasons turns diagnostics deltas into operator-facing
// alert lines, and RunHealthMonitor baselines on its first call so a fresh server never false-alarms.
public sealed class HealthMonitorTests
{
    [Fact]
    public void No_reasons_when_everything_is_healthy()
    {
        var reasons = GameWorld.BuildHealthAlertReasons(
            backgroundErrorDelta: 0, lastErrorText: "unknown",
            worldSkipDelta: 0,
            roomSpellOverlapDelta: 0, overlapCause: "",
            worstGateStallMs: 0, gateStallSource: "",
            saveBacklog: 0);

        Assert.Empty(reasons);
    }

    [Fact]
    public void Reports_each_tripped_condition()
    {
        var reasons = GameWorld.BuildHealthAlertReasons(
            backgroundErrorDelta: 2, lastErrorText: "NullReferenceException: boom",
            worldSkipDelta: 3,
            roomSpellOverlapDelta: 10, overlapCause: "combat",
            worstGateStallMs: 250, gateStallSource: "command",
            saveBacklog: 150);

        Assert.Equal(5, reasons.Count);
        Assert.Contains(reasons, r => r.Contains("2 background error(s)") && r.Contains("boom"));
        Assert.Contains(reasons, r => r.Contains("overloaded") && r.Contains("3 world tick(s) dropped"));
        Assert.Contains(reasons, r => r.Contains("world lock contended") && r.Contains("10 room-spell tick overlaps") && r.Contains("combat"));
        Assert.Contains(reasons, r => r.Contains("lock stalled 250ms") && r.Contains("command"));
        Assert.Contains(reasons, r => r.Contains("save backlog is 150"));
    }

    [Fact]
    public void Single_world_tick_skip_alerts_but_a_few_overlaps_do_not()
    {
        // A dropped world tick is real overload — alert immediately. A handful of room-spell overlaps is
        // benign serialization — must NOT trip (this is the cry-wolf case we fixed).
        var skip = GameWorld.BuildHealthAlertReasons(
            backgroundErrorDelta: 0, lastErrorText: "unknown",
            worldSkipDelta: 1,
            roomSpellOverlapDelta: 0, overlapCause: "",
            worstGateStallMs: 0, gateStallSource: "",
            saveBacklog: 0);
        Assert.Single(skip);
        Assert.Contains(skip, r => r.Contains("1 world tick(s) dropped"));

        var fewOverlaps = GameWorld.BuildHealthAlertReasons(
            backgroundErrorDelta: 0, lastErrorText: "unknown",
            worldSkipDelta: 0,
            roomSpellOverlapDelta: 4, overlapCause: "combat", // below the sustained-burst threshold
            worstGateStallMs: 0, gateStallSource: "",
            saveBacklog: 0);
        Assert.Empty(fewOverlaps);
    }

    [Fact]
    public void Gate_stall_under_threshold_does_not_trip()
    {
        var reasons = GameWorld.BuildHealthAlertReasons(
            backgroundErrorDelta: 0, lastErrorText: "unknown",
            worldSkipDelta: 0,
            roomSpellOverlapDelta: 0, overlapCause: "",
            worstGateStallMs: 40, gateStallSource: "combat", // below the 100ms felt-stall threshold
            saveBacklog: 0);

        Assert.Empty(reasons);
    }

    [Fact]
    public void Run_health_monitor_is_safe_with_no_host_and_baselines_on_first_call()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { HealthAlertsEnabled = true };

        // First call only establishes the baseline; later calls deliver via the host (null here, so a
        // tripped alert is a safe no-op rather than a crash). Both passes must complete without throwing.
        var first = Record.Exception(() => world.RunHealthMonitor(System.DateTime.UtcNow));
        var second = Record.Exception(() => world.RunHealthMonitor(System.DateTime.UtcNow.AddSeconds(30)));

        Assert.Null(first);
        Assert.Null(second);
    }
}
