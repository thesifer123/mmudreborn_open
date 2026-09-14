using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// Shares the process-global GameDiagnostics statics with GateHoldDiagnosticsTests — see the collection
// definition there for why these two must not run in parallel.
[Collection(GameDiagnosticsCollection.Name)]
public sealed class GameDiagnosticsTests
{
    [Fact]
    public void RecordBackgroundException_keeps_latest_exception_for_diagnosis()
    {
        bool previousRethrow = GameDiagnostics.RethrowBackgroundExceptions;
        try
        {
            GameDiagnostics.RethrowBackgroundExceptions = false;
            GameDiagnostics.Reset();
            var exception = new InvalidOperationException("boom");

            GameDiagnostics.RecordBackgroundException(exception);

            Assert.Same(exception, GameDiagnostics.LastBackgroundException);
        }
        finally
        {
            GameDiagnostics.RethrowBackgroundExceptions = previousRethrow;
            GameDiagnostics.Reset();
        }
    }

    [Fact]
    public void RecordBackgroundException_keeps_a_ring_buffer_with_source_type_message_and_stack()
    {
        bool previousRethrow = GameDiagnostics.RethrowBackgroundExceptions;
        try
        {
            GameDiagnostics.RethrowBackgroundExceptions = false;
            GameDiagnostics.Reset();

            Exception captured;
            try { throw new InvalidOperationException("Collection was modified"); }
            catch (Exception ex) { captured = ex; }

            GameDiagnostics.RecordBackgroundException(captured, "tick:medium");

            // GameDiagnostics is process-global static and other test classes run in parallel, so match
            // on this test's unique entry rather than exact snapshot size / global count.
            var snapshot = GameDiagnostics.GetBackgroundExceptionsSnapshot();
            var mine = Assert.Single(snapshot, e => e.Source == "tick:medium" && e.Message == "Collection was modified");
            Assert.Equal("InvalidOperationException", mine.ExceptionType);
            Assert.Contains("RecordBackgroundException_keeps_a_ring_buffer", mine.StackTrace);
        }
        finally
        {
            GameDiagnostics.RethrowBackgroundExceptions = previousRethrow;
            GameDiagnostics.Reset();
        }
    }

    [Fact]
    public void Background_exception_ring_buffer_is_capped_and_cleared_by_reset()
    {
        bool previousRethrow = GameDiagnostics.RethrowBackgroundExceptions;
        try
        {
            GameDiagnostics.RethrowBackgroundExceptions = false;
            GameDiagnostics.Reset();

            // Unique "capbuf" prefix so parallel pollution from other classes can't match these asserts.
            for (int i = 0; i < 40; i++)
                GameDiagnostics.RecordBackgroundException(new InvalidOperationException($"capbuf {i}"), "tick:fast");

            var snapshot = GameDiagnostics.GetBackgroundExceptionsSnapshot();
            Assert.True(snapshot.Length <= 24, $"ring buffer should cap at 24, was {snapshot.Length}");
            Assert.Contains(snapshot, e => e.Message == "capbuf 39");      // newest kept
            Assert.DoesNotContain(snapshot, e => e.Message == "capbuf 0"); // oldest dropped (40 > cap 24)

            GameDiagnostics.ResetWorldTickDiagnostics(); // the SYSOP DIAG CLEAR path clears the buffer
            Assert.DoesNotContain(GameDiagnostics.GetBackgroundExceptionsSnapshot(), e => e.Message.StartsWith("capbuf"));
        }
        finally
        {
            GameDiagnostics.RethrowBackgroundExceptions = previousRethrow;
            GameDiagnostics.Reset();
        }
    }

    [Fact]
    public void World_tick_diagnostics_store_only_slow_samples_and_count_skipped_ticks()
    {
        bool previousEnabled = GameDiagnostics.WorldTickProfilingEnabled;
        bool previousConsole = GameDiagnostics.WorldTickConsoleLoggingEnabled;
        int previousThreshold = GameDiagnostics.WorldTickSlowThresholdMs;

        try
        {
            GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: true, consoleLoggingEnabled: false, slowThresholdMs: 50);
            GameDiagnostics.ResetWorldTickDiagnostics();

            RecordWorldTick(totalMs: 10, tick: 1, movementCount: 0, spawned: 0);
            RecordWorldTick(totalMs: 75, tick: 2, movementCount: 3, spawned: 2);
            GameDiagnostics.RecordWorldTickSkipped();

            var snapshot = GameDiagnostics.GetWorldTickDiagnosticsSnapshot();

            Assert.Equal(2, snapshot.TotalTicks);
            Assert.Equal(1, snapshot.SlowTicks);
            Assert.Equal(1, snapshot.SkippedTicks);
            Assert.Equal(75, snapshot.LastTickMs);
            Assert.Equal(75, snapshot.MaxTickMs);

            var sample = Assert.Single(snapshot.RecentSlowTicks);
            Assert.Equal(2, sample.Tick);
            Assert.Equal(75, sample.TotalMs);
            Assert.Equal(3, sample.MovementCount);
            Assert.Equal(2, sample.MonstersSpawned);
        }
        finally
        {
            GameDiagnostics.ConfigureWorldTickDiagnostics(previousEnabled, previousConsole, previousThreshold);
            GameDiagnostics.ResetWorldTickDiagnostics();
        }
    }

    [Fact]
    public void Room_spell_tick_diagnostics_store_only_slow_samples_and_count_overlaps()
    {
        bool previousEnabled = GameDiagnostics.WorldTickProfilingEnabled;
        bool previousConsole = GameDiagnostics.WorldTickConsoleLoggingEnabled;
        int previousThreshold = GameDiagnostics.WorldTickSlowThresholdMs;

        try
        {
            GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: true, consoleLoggingEnabled: false, slowThresholdMs: 50);
            GameDiagnostics.ResetWorldTickDiagnostics();

            GameDiagnostics.RecordRoomSpellTickMeasurement(DateTime.UtcNow, totalMs: 15, playersProcessed: 3, activeSpellRooms: 1, pulsesTriggered: 0, concurrentTicks: 1);
            GameDiagnostics.RecordRoomSpellTickMeasurement(DateTime.UtcNow, totalMs: 95, playersProcessed: 8, activeSpellRooms: 4, pulsesTriggered: 2, concurrentTicks: 2);
            GameDiagnostics.RecordRoomSpellTickOverlap(concurrentTicks: 2);

            var snapshot = GameDiagnostics.GetRoomSpellTickDiagnosticsSnapshot();

            Assert.Equal(2, snapshot.TotalTicks);
            Assert.Equal(1, snapshot.SlowTicks);
            Assert.Equal(1, snapshot.OverlapTicks);
            Assert.Equal(95, snapshot.LastTickMs);
            Assert.Equal(95, snapshot.MaxTickMs);

            var sample = Assert.Single(snapshot.RecentSlowTicks);
            Assert.Equal(95, sample.TotalMs);
            Assert.Equal(8, sample.PlayersProcessed);
            Assert.Equal(4, sample.ActiveSpellRooms);
            Assert.Equal(2, sample.PulsesTriggered);
            Assert.Equal(2, sample.ConcurrentTicks);
        }
        finally
        {
            GameDiagnostics.ConfigureWorldTickDiagnostics(previousEnabled, previousConsole, previousThreshold);
            GameDiagnostics.ResetWorldTickDiagnostics();
        }
    }

    private static void RecordWorldTick(int totalMs, int tick, int movementCount, int spawned)
    {
        GameDiagnostics.RecordWorldTickMeasurement(
            DateTime.UtcNow,
            tick,
            totalMs,
            timedExitMs: 1,
            monsterLockMs: 2,
            monsterBuffMs: 3,
            deadMonsterMs: 4,
            movementMs: 5,
            movementAnnounceMs: 6,
            cleanupMs: 7,
            lairMs: 8,
            ambientMs: 9,
            persistMs: 10,
            playerUpkeepMs: 11,
            movementCount,
            activeTrueLairRooms: 12,
            trueLairRoomsScanned: 13,
            trueLairRoomsProcessed: 14,
            playerRooms: 15,
            adjacentRoomsChecked: 16,
            spawnAttempts: 17,
            monstersSpawned: spawned);
    }
}
