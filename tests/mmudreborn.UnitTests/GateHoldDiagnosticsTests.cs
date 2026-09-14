using System.Diagnostics;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// GameDiagnostics is process-global STATIC state — one set of counters, one config, one sample ring for
// the whole test assembly. xUnit runs separate test classes in parallel, so GameDiagnosticsTests calling
// ResetWorldTickDiagnostics() can land between this class's Reset and its snapshot and zero the counters
// out from under it ("expected 2 holds, got 0"). Both classes join this collection so they serialize
// against each other. Assertions are unchanged — this is isolation, not a loosened expectation.
[CollectionDefinition(GameDiagnosticsCollection.Name, DisableParallelization = true)]
public sealed class GameDiagnosticsCollection
{
    public const string Name = "GameDiagnostics static state";
}

// The gate-hold meter is how we keep "snappy" honest: it records how long the global WorldStateGate was
// held per acquisition so a blocking call that sneaks back under the gate shows up as a slow sample.
[Collection(GameDiagnosticsCollection.Name)]
public sealed class GateHoldDiagnosticsTests
{
    private static long TimestampAgo(double millisecondsAgo)
        => Stopwatch.GetTimestamp() - (long)(millisecondsAgo / 1000.0 * Stopwatch.Frequency);

    [Fact]
    public void Records_count_and_max_for_each_hold()
    {
        GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: true);
        GameDiagnostics.ResetWorldTickDiagnostics();

        GameDiagnostics.RecordGateHold("command", TimestampAgo(1.0));
        GameDiagnostics.RecordGateHold("combat", TimestampAgo(2.0));

        var snapshot = GameDiagnostics.GetGateHoldDiagnosticsSnapshot();
        Assert.Equal(2, snapshot.TotalHolds);
        Assert.True(snapshot.MaxMs >= 1.5, $"expected max ~2ms, got {snapshot.MaxMs}");
        Assert.True(snapshot.AverageMs > 0);
    }

    [Fact]
    public void Holds_over_the_slow_threshold_are_captured_as_samples()
    {
        GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: true);
        GameDiagnostics.ResetWorldTickDiagnostics();

        GameDiagnostics.RecordGateHold("command", TimestampAgo(0.1));            // fast: under 5ms threshold
        GameDiagnostics.RecordGateHold("persist-flush", TimestampAgo(25.0));     // slow: retained sample

        var snapshot = GameDiagnostics.GetGateHoldDiagnosticsSnapshot();
        Assert.Equal(2, snapshot.TotalHolds);
        Assert.Equal(1, snapshot.SlowHolds);
        Assert.Single(snapshot.RecentSlowHolds);
        Assert.Equal("persist-flush", snapshot.RecentSlowHolds[0].Source);
    }

    [Fact]
    public void Disabled_profiling_records_nothing()
    {
        GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: false);
        GameDiagnostics.ResetWorldTickDiagnostics();

        GameDiagnostics.RecordGateHold("command", TimestampAgo(50.0));

        var snapshot = GameDiagnostics.GetGateHoldDiagnosticsSnapshot();
        Assert.Equal(0, snapshot.TotalHolds);

        GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: true); // restore for sibling tests
    }
}
