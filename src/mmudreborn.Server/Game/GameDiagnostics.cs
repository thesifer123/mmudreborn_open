using System.Diagnostics;

namespace mmudreborn.Game;

public sealed record WorldTickDiagnosticSample(
    DateTime AtUtc,
    int Tick,
    int TotalMs,
    int TimedExitMs,
    int MonsterLockMs,
    int MonsterBuffMs,
    int DeadMonsterMs,
    int MovementMs,
    int MovementAnnounceMs,
    int CleanupMs,
    int LairMs,
    int AmbientMs,
    int PersistMs,
    int PlayerUpkeepMs,
    int MovementCount,
    int ActiveTrueLairRooms,
    int TrueLairRoomsScanned,
    int TrueLairRoomsProcessed,
    int PlayerRooms,
    int AdjacentRoomsChecked,
    int SpawnAttempts,
    int MonstersSpawned);

public sealed record WorldTickDiagnosticsSnapshot(
    bool Enabled,
    bool ConsoleLoggingEnabled,
    int SlowThresholdMs,
    long TotalTicks,
    long SlowTicks,
    long SkippedTicks,
    long LastTickMs,
    long MaxTickMs,
    WorldTickDiagnosticSample[] RecentSlowTicks);

public sealed record RoomSpellTickDiagnosticSample(
    DateTime AtUtc,
    long Sequence,
    int TotalMs,
    int PlayersProcessed,
    int ActiveSpellRooms,
    int PulsesTriggered,
    int ConcurrentTicks);

public sealed record RoomSpellTickDiagnosticsSnapshot(
    bool Enabled,
    bool ConsoleLoggingEnabled,
    int SlowThresholdMs,
    long TotalTicks,
    long SlowTicks,
    long OverlapTicks,
    long LastTickMs,
    long MaxTickMs,
    RoomSpellTickDiagnosticSample[] RecentSlowTicks);

// One recorded room-spell-tick overlap: a room-spell timer fired while a previous holder still had the
// world lock, so the two serialized (nothing dropped — the gate queues them). Cause = what held the gate
// at that instant ("combat" / "tick:slow" / a command label), which is the actual thing worth looking at.
public sealed record RoomSpellOverlapSample(
    DateTime AtUtc,
    int ConcurrentTicks,
    string Cause,
    double CauseHeldMs);

// One recorded instance of the global WorldStateGate being held longer than the slow threshold, tagged
// with where it was held ("command" / "combat" / "persist-flush"). The whole point of write-behind and
// the off-gate read commands is to keep these holds tiny; a slow sample here is the smoking gun for a
// blocking call that sneaked back under the gate.
public sealed record GateHoldSample(
    DateTime AtUtc,
    string Source,
    double Milliseconds);

public sealed record GateHoldDiagnosticsSnapshot(
    bool Enabled,
    int SlowThresholdMs,
    long TotalHolds,
    long SlowHolds,
    double AverageMs,
    double MaxMs,
    GateHoldSample[] RecentSlowHolds);

// One swallowed background exception, kept with its full stack so a fault that would otherwise vanish
// (a Timer callback or off-loop command throwing) can be diagnosed after the fact via SYSOP DIAG.
public sealed record BackgroundExceptionSample(
    DateTime AtUtc,
    string Source,
    string ExceptionType,
    string Message,
    string StackTrace);

/// <summary>
/// Surfaces exceptions that the server would otherwise swallow silently:
///   - the per-connection command loop (GameSession.GameLoop) — exceptions there propagate to the
///     host connection handler, which swallows them to keep the server alive, so a throwing command
///     (e.g. a concurrency fault mid-move) just makes the command vanish with no trace;
///   - the System.Threading.Timer callbacks (GameWorld.WorldTick / RoomSpellTick) — a Timer drops any
///     exception its callback throws.
/// Tests can set <see cref="RethrowBackgroundExceptions"/> to fail loudly, or read
/// <see cref="LastBackgroundException"/> after exercising a scenario. Production just records the last one.
/// </summary>
public static class GameDiagnostics
{
    private const int MaxWorldTickSamples = 32;
    private const int MaxRoomSpellTickSamples = 32;
    private static readonly object WorldTickDiagnosticsLock = new();
    private static readonly Queue<WorldTickDiagnosticSample> WorldTickSamples = new();
    private static readonly object RoomSpellTickDiagnosticsLock = new();
    private static readonly Queue<RoomSpellTickDiagnosticSample> RoomSpellTickSamples = new();
    private static volatile bool _worldTickProfilingEnabled = true;
    private static volatile bool _worldTickConsoleLoggingEnabled;
    private static volatile int _worldTickSlowThresholdMs = 150;
    private static long _worldTickTotalCount;
    private static long _worldTickSlowCount;
    private static long _worldTickSkippedCount;
    private static long _worldTickLastElapsedMs;
    private static long _worldTickMaxElapsedMs;
    private static long _roomSpellTickTotalCount;
    private static long _roomSpellTickSlowCount;
    private static long _roomSpellTickOverlapCount;
    private static long _roomSpellTickLastElapsedMs;
    private static long _roomSpellTickMaxElapsedMs;

    private const int MaxGateHoldSamples = 32;
    private static readonly object GateHoldLock = new();
    private static readonly Queue<GateHoldSample> GateHoldSamples = new();
    private static volatile int _gateHoldSlowThresholdMs = 5;
    private static long _gateHoldCount;
    private static long _gateHoldSlowCount;
    private static long _gateHoldTotalMicros;
    private static long _gateHoldMaxMicros;

    // The single WorldStateGate is a mutex (SemaphoreSlim(1,1)), so exactly one holder exists at a time.
    // We track that holder — set right after acquire, cleared right before release — so a room-spell tick
    // that overlaps can name what it queued behind, turning "an overlap happened" into "an overlap behind
    // combat that held the lock 9ms". Writes only ever happen from the current holder; the diagnostic
    // readers (overlap recorder, SYSOP DIAG) read it lock-free.
    private static volatile string? _gateCurrentHolder;
    private static long _gateCurrentHolderSinceTimestamp;

    private const int MaxRoomSpellOverlapSamples = 16;
    private static readonly object RoomSpellOverlapLock = new();
    private static readonly Queue<RoomSpellOverlapSample> RoomSpellOverlapSamples = new();

    /// <summary>When true, recorded background exceptions are rethrown so the failure isn't hidden.</summary>
    public static bool RethrowBackgroundExceptions { get; set; }

    /// <summary>The most recent swallowed background exception (for test assertions / diagnosis).</summary>
    public static volatile Exception? LastBackgroundException;

    private static long _backgroundExceptionCount;

    private const int MaxBackgroundExceptionSamples = 24;
    private static readonly object BackgroundExceptionLock = new();
    private static readonly Queue<BackgroundExceptionSample> BackgroundExceptionSamples = new();

    /// <summary>Total background exceptions recorded since the last <see cref="Reset"/> — read by the health monitor.</summary>
    public static long BackgroundExceptionCount => Interlocked.Read(ref _backgroundExceptionCount);

    public static bool WorldTickProfilingEnabled => _worldTickProfilingEnabled;
    public static bool WorldTickConsoleLoggingEnabled => _worldTickConsoleLoggingEnabled;
    public static int WorldTickSlowThresholdMs => _worldTickSlowThresholdMs;

    public static void Reset()
    {
        LastBackgroundException = null;
        Interlocked.Exchange(ref _backgroundExceptionCount, 0);
        lock (BackgroundExceptionLock)
            BackgroundExceptionSamples.Clear();
        ResetWorldTickDiagnostics();
    }

    // source identifies WHERE the fault was swallowed (e.g. "tick:medium", "tick:room-spell", "combat",
    // "command:<verb>") so SYSOP DIAG can point at the culprit subsystem; the full stack is retained too.
    public static void RecordBackgroundException(Exception ex, string source = "unknown")
    {
        LastBackgroundException = ex;
        Interlocked.Increment(ref _backgroundExceptionCount);

        var sample = new BackgroundExceptionSample(
            DateTime.UtcNow,
            source,
            ex.GetType().Name,
            ex.Message,
            ex.ToString());
        lock (BackgroundExceptionLock)
        {
            while (BackgroundExceptionSamples.Count >= MaxBackgroundExceptionSamples)
                BackgroundExceptionSamples.Dequeue();
            BackgroundExceptionSamples.Enqueue(sample);
        }

        if (_worldTickConsoleLoggingEnabled)
            Console.WriteLine($"[MMUD ERROR] source={source} {ex.GetType().Name}: {ex.Message}");

        if (RethrowBackgroundExceptions)
            throw new InvalidOperationException(
                $"Background exception surfaced for diagnosis: {ex.GetType().Name}: {ex.Message}", ex);
    }

    public static BackgroundExceptionSample[] GetBackgroundExceptionsSnapshot()
    {
        lock (BackgroundExceptionLock)
            return BackgroundExceptionSamples.ToArray();
    }

    public static void ConfigureWorldTickDiagnostics(bool? enabled = null, bool? consoleLoggingEnabled = null, int? slowThresholdMs = null)
    {
        if (enabled.HasValue)
            _worldTickProfilingEnabled = enabled.Value;

        if (consoleLoggingEnabled.HasValue)
            _worldTickConsoleLoggingEnabled = consoleLoggingEnabled.Value;

        if (slowThresholdMs.HasValue)
            _worldTickSlowThresholdMs = Math.Clamp(slowThresholdMs.Value, 1, 10_000);
    }

    public static void ResetWorldTickDiagnostics()
    {
        Interlocked.Exchange(ref _worldTickTotalCount, 0);
        Interlocked.Exchange(ref _worldTickSlowCount, 0);
        Interlocked.Exchange(ref _worldTickSkippedCount, 0);
        Interlocked.Exchange(ref _worldTickLastElapsedMs, 0);
        Interlocked.Exchange(ref _worldTickMaxElapsedMs, 0);
        lock (WorldTickDiagnosticsLock)
            WorldTickSamples.Clear();

        Interlocked.Exchange(ref _roomSpellTickTotalCount, 0);
        Interlocked.Exchange(ref _roomSpellTickSlowCount, 0);
        Interlocked.Exchange(ref _roomSpellTickOverlapCount, 0);
        Interlocked.Exchange(ref _roomSpellTickLastElapsedMs, 0);
        Interlocked.Exchange(ref _roomSpellTickMaxElapsedMs, 0);
        lock (RoomSpellTickDiagnosticsLock)
            RoomSpellTickSamples.Clear();
        lock (RoomSpellOverlapLock)
            RoomSpellOverlapSamples.Clear();

        Interlocked.Exchange(ref _gateHoldCount, 0);
        Interlocked.Exchange(ref _gateHoldSlowCount, 0);
        Interlocked.Exchange(ref _gateHoldTotalMicros, 0);
        Interlocked.Exchange(ref _gateHoldMaxMicros, 0);
        lock (GateHoldLock)
            GateHoldSamples.Clear();

        // SYSOP DIAG CLEAR also resets the recorded background faults + counter so the [REALM HEALTH]
        // delta and the "since last clear" error list start fresh.
        Interlocked.Exchange(ref _backgroundExceptionCount, 0);
        LastBackgroundException = null;
        lock (BackgroundExceptionLock)
            BackgroundExceptionSamples.Clear();
    }

    public static void RecordWorldTickSkipped()
    {
        if (!_worldTickProfilingEnabled)
            return;

        Interlocked.Increment(ref _worldTickSkippedCount);
        if (_worldTickConsoleLoggingEnabled)
            Console.WriteLine("[MMUD PERF] world tick skipped because the previous tick is still running");
    }

    public static void RecordRoomSpellTickOverlap(int concurrentTicks)
    {
        if (!_worldTickProfilingEnabled)
            return;

        Interlocked.Increment(ref _roomSpellTickOverlapCount);

        // Capture WHO holds the gate at the instant we overlap — that holder is why this room-spell tick
        // had to queue. This is the diagnostic the operator actually wants.
        var (cause, heldMs) = GetCurrentGateHolder();
        string causeText = cause ?? "unknown";
        var sample = new RoomSpellOverlapSample(DateTime.UtcNow, concurrentTicks, causeText, heldMs);
        lock (RoomSpellOverlapLock)
        {
            while (RoomSpellOverlapSamples.Count >= MaxRoomSpellOverlapSamples)
                RoomSpellOverlapSamples.Dequeue();
            RoomSpellOverlapSamples.Enqueue(sample);
        }

        if (_worldTickConsoleLoggingEnabled)
            Console.WriteLine($"[MMUD PERF] room spell tick overlap concurrent={concurrentTicks} held-by={causeText} {heldMs:F2}ms");
    }

    // Set by whoever just acquired the WorldStateGate; cleared just before they release it. Cheap volatile
    // writes on the gate path — no lock, since only the single current holder ever writes.
    public static void MarkGateAcquired(string source)
    {
        Volatile.Write(ref _gateCurrentHolderSinceTimestamp, Stopwatch.GetTimestamp());
        _gateCurrentHolder = source;
    }

    public static void MarkGateReleased()
    {
        // Clear BEFORE the actual Release() so we never clobber the next holder's mark.
        _gateCurrentHolder = null;
    }

    // (source, how-long-held-so-far) of whoever holds the gate right now, or (null, 0) when idle.
    public static (string? Source, double HeldMs) GetCurrentGateHolder()
    {
        string? holder = _gateCurrentHolder;
        if (holder == null)
            return (null, 0);

        long since = Volatile.Read(ref _gateCurrentHolderSinceTimestamp);
        double heldMs = (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;
        return (holder, heldMs);
    }

    public static RoomSpellOverlapSample[] GetRecentRoomSpellOverlaps()
    {
        lock (RoomSpellOverlapLock)
            return RoomSpellOverlapSamples.ToArray();
    }

    public static void RecordWorldTickMeasurement(
        DateTime atUtc,
        int tick,
        int totalMs,
        int timedExitMs,
        int monsterLockMs,
        int monsterBuffMs,
        int deadMonsterMs,
        int movementMs,
        int movementAnnounceMs,
        int cleanupMs,
        int lairMs,
        int ambientMs,
        int persistMs,
        int playerUpkeepMs,
        int movementCount,
        int activeTrueLairRooms,
        int trueLairRoomsScanned,
        int trueLairRoomsProcessed,
        int playerRooms,
        int adjacentRoomsChecked,
        int spawnAttempts,
        int monstersSpawned)
    {
        if (!_worldTickProfilingEnabled)
            return;

        Interlocked.Increment(ref _worldTickTotalCount);
        Interlocked.Exchange(ref _worldTickLastElapsedMs, totalMs);
        UpdateMaximum(ref _worldTickMaxElapsedMs, totalMs);

        if (totalMs < _worldTickSlowThresholdMs)
            return;

        Interlocked.Increment(ref _worldTickSlowCount);
        var sample = new WorldTickDiagnosticSample(
            atUtc,
            tick,
            totalMs,
            timedExitMs,
            monsterLockMs,
            monsterBuffMs,
            deadMonsterMs,
            movementMs,
            movementAnnounceMs,
            cleanupMs,
            lairMs,
            ambientMs,
            persistMs,
            playerUpkeepMs,
            movementCount,
            activeTrueLairRooms,
            trueLairRoomsScanned,
            trueLairRoomsProcessed,
            playerRooms,
            adjacentRoomsChecked,
            spawnAttempts,
            monstersSpawned);

        lock (WorldTickDiagnosticsLock)
        {
            while (WorldTickSamples.Count >= MaxWorldTickSamples)
                WorldTickSamples.Dequeue();
            WorldTickSamples.Enqueue(sample);
        }

        if (_worldTickConsoleLoggingEnabled)
            Console.WriteLine($"[MMUD PERF] {FormatWorldTickSample(sample)}");
    }

    public static void RecordRoomSpellTickMeasurement(
        DateTime atUtc,
        int totalMs,
        int playersProcessed,
        int activeSpellRooms,
        int pulsesTriggered,
        int concurrentTicks)
    {
        if (!_worldTickProfilingEnabled)
            return;

        long sequence = Interlocked.Increment(ref _roomSpellTickTotalCount);
        Interlocked.Exchange(ref _roomSpellTickLastElapsedMs, totalMs);
        UpdateMaximum(ref _roomSpellTickMaxElapsedMs, totalMs);

        if (totalMs < _worldTickSlowThresholdMs)
            return;

        Interlocked.Increment(ref _roomSpellTickSlowCount);
        var sample = new RoomSpellTickDiagnosticSample(
            atUtc,
            sequence,
            totalMs,
            playersProcessed,
            activeSpellRooms,
            pulsesTriggered,
            concurrentTicks);

        lock (RoomSpellTickDiagnosticsLock)
        {
            while (RoomSpellTickSamples.Count >= MaxRoomSpellTickSamples)
                RoomSpellTickSamples.Dequeue();
            RoomSpellTickSamples.Enqueue(sample);
        }

        if (_worldTickConsoleLoggingEnabled)
            Console.WriteLine($"[MMUD PERF] {FormatRoomSpellTickSample(sample)}");
    }

    // Records how long the global WorldStateGate was held for one acquisition. `gateAcquiredTimestamp`
    // is a Stopwatch.GetTimestamp() captured immediately after the gate was taken; the caller passes it
    // again at release. Cheap on the hot path (a subtraction + a few interlocked adds); only a hold that
    // exceeds the slow threshold allocates a retained sample.
    public static void RecordGateHold(string source, long gateAcquiredTimestamp)
    {
        if (!_worldTickProfilingEnabled)
            return;

        double elapsedMs = (Stopwatch.GetTimestamp() - gateAcquiredTimestamp) * 1000.0 / Stopwatch.Frequency;
        long micros = (long)(elapsedMs * 1000.0);

        Interlocked.Increment(ref _gateHoldCount);
        Interlocked.Add(ref _gateHoldTotalMicros, micros);
        UpdateMaximum(ref _gateHoldMaxMicros, micros);

        if (elapsedMs < _gateHoldSlowThresholdMs)
            return;

        Interlocked.Increment(ref _gateHoldSlowCount);
        var sample = new GateHoldSample(DateTime.UtcNow, source, elapsedMs);
        lock (GateHoldLock)
        {
            while (GateHoldSamples.Count >= MaxGateHoldSamples)
                GateHoldSamples.Dequeue();
            GateHoldSamples.Enqueue(sample);
        }

        if (_worldTickConsoleLoggingEnabled)
            Console.WriteLine($"[MMUD PERF] gate hold source={source} {elapsedMs:F2}ms");
    }

    public static GateHoldDiagnosticsSnapshot GetGateHoldDiagnosticsSnapshot()
    {
        GateHoldSample[] samples;
        lock (GateHoldLock)
            samples = GateHoldSamples.ToArray();

        long totalHolds = Interlocked.Read(ref _gateHoldCount);
        long totalMicros = Interlocked.Read(ref _gateHoldTotalMicros);
        return new GateHoldDiagnosticsSnapshot(
            _worldTickProfilingEnabled,
            _gateHoldSlowThresholdMs,
            totalHolds,
            Interlocked.Read(ref _gateHoldSlowCount),
            totalHolds == 0 ? 0 : totalMicros / 1000.0 / totalHolds,
            Interlocked.Read(ref _gateHoldMaxMicros) / 1000.0,
            samples);
    }

    public static WorldTickDiagnosticsSnapshot GetWorldTickDiagnosticsSnapshot()
    {
        WorldTickDiagnosticSample[] samples;
        lock (WorldTickDiagnosticsLock)
            samples = WorldTickSamples.ToArray();

        return new WorldTickDiagnosticsSnapshot(
            _worldTickProfilingEnabled,
            _worldTickConsoleLoggingEnabled,
            _worldTickSlowThresholdMs,
            Interlocked.Read(ref _worldTickTotalCount),
            Interlocked.Read(ref _worldTickSlowCount),
            Interlocked.Read(ref _worldTickSkippedCount),
            Interlocked.Read(ref _worldTickLastElapsedMs),
            Interlocked.Read(ref _worldTickMaxElapsedMs),
            samples);
    }

    public static RoomSpellTickDiagnosticsSnapshot GetRoomSpellTickDiagnosticsSnapshot()
    {
        RoomSpellTickDiagnosticSample[] samples;
        lock (RoomSpellTickDiagnosticsLock)
            samples = RoomSpellTickSamples.ToArray();

        return new RoomSpellTickDiagnosticsSnapshot(
            _worldTickProfilingEnabled,
            _worldTickConsoleLoggingEnabled,
            _worldTickSlowThresholdMs,
            Interlocked.Read(ref _roomSpellTickTotalCount),
            Interlocked.Read(ref _roomSpellTickSlowCount),
            Interlocked.Read(ref _roomSpellTickOverlapCount),
            Interlocked.Read(ref _roomSpellTickLastElapsedMs),
            Interlocked.Read(ref _roomSpellTickMaxElapsedMs),
            samples);
    }

    public static string FormatWorldTickSample(WorldTickDiagnosticSample sample)
    {
        return $"world tick {sample.Tick} total={sample.TotalMs}ms "
            + $"lock={sample.MonsterLockMs}ms buffs={sample.MonsterBuffMs}ms dead={sample.DeadMonsterMs}ms "
            + $"move={sample.MovementMs}ms moveOut={sample.MovementAnnounceMs}ms cleanup={sample.CleanupMs}ms "
            + $"lair={sample.LairMs}ms ambient={sample.AmbientMs}ms player={sample.PlayerUpkeepMs}ms "
            + $"moves={sample.MovementCount} activeLairs={sample.ActiveTrueLairRooms} "
            + $"lairScan={sample.TrueLairRoomsScanned}/{sample.TrueLairRoomsProcessed} "
            + $"playerRooms={sample.PlayerRooms} adj={sample.AdjacentRoomsChecked} "
            + $"spawnCalls={sample.SpawnAttempts} spawned={sample.MonstersSpawned}";
    }

    public static string FormatRoomSpellTickSample(RoomSpellTickDiagnosticSample sample)
    {
        return $"room spell tick #{sample.Sequence} total={sample.TotalMs}ms "
            + $"players={sample.PlayersProcessed} spellRooms={sample.ActiveSpellRooms} "
            + $"pulses={sample.PulsesTriggered} concurrent={sample.ConcurrentTicks}";
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref target);
            if (current >= value)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }
}
