using System.Diagnostics;
using mmudreborn.Game;

namespace mmudreborn.Server;

// ── Stock's monster sweeps and the host poll clock ──────────────────────────────────────────────
// Stock does not update every monster on each 1s/3s/30s tick. Its active monsters sit in a fixed buffer
// of MONSBUF (2000) slots, and each update kind (fast / medium / slow) walks that buffer ONE SLOT AT A
// TIME, empty slots included. The ticks only re-arm a sweep (when the last one has finished) and step it
// once; the walking itself is done by the game's per-player polling routine, which the BBS host calls
// once per pass of its main loop for every player in the game. Every second poll call steps each sweep
// one slot. The stock BBS host serves one event per channel per pass and
// then sleeps 10 ms, so a pass is ~10 ms (we model a host whose 10 ms sleep is exact).
//
// So the time for a sweep to reach every monster depends on how many players are in the game:
// 2000 slots / (50 slots/s per player) = 40 s / players. With one player each monster gets a medium
// update (spell upkeep, wander) about every 42 s and a slow update (regen, poison) about every 60 s;
// only with 14+ players does every monster update on every 3 s tick.
//
// The same polling routine drives stock's slow background save: one buffered record every
// (players × POLLRATE) poll calls — ~7.5 s whatever the player count — walking rooms, then this monster
// buffer, then the rest (~5000 records, ~10.6 h a round). A monster's "dirty" byte is cleared only when
// that save reaches its slot (or at nightly cleanup), and a dirty guard moves past the wander budget.
public partial class GameWorld
{
    private const int MonsterBufferSlots = 2000;        // MONSBUF stock default
    private const int HostPassMilliseconds = 10;        // host main loop: Sleep(10) per pass
    private const int PollPumpIntervalMilliseconds = 100;
    private const int MaxPollPumpCatchUpMilliseconds = 1000;
    private const int InitialPollRate = 750;            // POLLRATE stock default
    private const int MaxPollRate = 10000;
    private const int SaveBufferSlowTicks = 10;         // SECBUFF 300 s = 10 slow (30 s) ticks
    private const int WanderBudgetPerMediumTick = 3;

    // The save walks each buffer from slot 0 to its size INCLUSIVE, in this order: rooms (ROOMBUF 1500),
    // active monsters (MONSBUF), known monsters (300), spells (150), classes (16), races (12), shops (50),
    // bankbooks (32), messages (250), gangs (10), items (ITEMBUF 750). Only the monster buffer is modelled;
    // the rest just spend their steps. Index 1 is the monster buffer, sized from the live slot table.
    private static readonly int[] SaveSweepBufferSizes = [1500, MonsterBufferSlots, 300, 150, 16, 12, 50, 32, 250, 10, 750];
    private const int SaveSweepMonsterBuffer = 1;

    private enum MonsterSweepKind { Fast, Medium, Slow }

    private sealed class MonsterSweep
    {
        public int Cursor;
        public int Pending;
    }

    private MonsterInstance?[] _monsterBufferSlots = new MonsterInstance?[MonsterBufferSlots];
    private int _nextMonsterBufferId;
    private readonly MonsterSweep _fastMonsterSweep = new();
    private readonly MonsterSweep _mediumMonsterSweep = new();
    private readonly MonsterSweep _slowMonsterSweep = new();
    private int _slowUpdateCycle;
    private int _wanderBudget;
    private int _pollCallCounter;
    private int _pollRate = InitialPollRate;
    private int _saveSweepPending;
    private int _saveSweepBuffer;
    private int _saveSweepIndex;
    private int _saveSweepWrites;
    private Timer? _pollPumpTimer;
    private long _pollPumpLastTimestamp;
    private long _pollPumpCarryMilliseconds;

    // What a batch of sweep steps produced, acted on once the monster lock is released.
    private readonly List<MonsterMovementAnnouncement> _sweepMovementAnnouncements = [];
    private readonly List<MonsterInstance> _sweepKills = [];

    // Passed as a tick callback's state by the Advance*TicksForTests hooks: the tick then runs its whole
    // monster sweep at once, as a board busy enough to finish every sweep inside one tick would. Real
    // timers pass null and get the paced sweep.
    private static readonly object CompleteMonsterSweepForTests = new();

    private void StartPollPump()
    {
        _pollPumpLastTimestamp = Stopwatch.GetTimestamp();
        _pollPumpTimer = new Timer(PollPump, null, PollPumpIntervalMilliseconds, PollPumpIntervalMilliseconds);
    }

    private void PollPump(object? state) => RunGatedTickBody("tick:poll", PollPumpImpl);

    private void PollPumpImpl()
    {
        long now = Stopwatch.GetTimestamp();
        long elapsedMs = (now - _pollPumpLastTimestamp) * 1000 / Stopwatch.Frequency;
        _pollPumpLastTimestamp = now;
        // A stalled pump (debugger, a long gate hold) catches up at most a second — the host would have
        // been stalled too, not running polls in the meantime.
        long budgetMs = Math.Min(MaxPollPumpCatchUpMilliseconds, elapsedMs + _pollPumpCarryMilliseconds);
        long passes = budgetMs / HostPassMilliseconds;
        _pollPumpCarryMilliseconds = budgetMs % HostPassMilliseconds;

        int playersInGame = _onlinePlayers.Count;
        if (passes <= 0 || playersInGame == 0)
            return;

        long pollCalls = passes * playersInGame;
        lock (_monsterLock)
        {
            RefreshMonsterBufferSlots();
            for (long call = 0; call < pollCalls; call++)
                RunPollCall(playersInGame);
            ResolveSweepKills();
        }
        AnnounceSweepMovements();
    }

    // One call of the game's polling routine: every second call steps the slow, fast and medium sweeps one
    // slot each, and every (players × POLLRATE) calls steps the background save.
    private void RunPollCall(int playersInGame)
    {
        if ((_pollCallCounter & 1) != 0)
        {
            StepMonsterSweep(MonsterSweepKind.Slow);
            StepMonsterSweep(MonsterSweepKind.Fast);
            StepMonsterSweep(MonsterSweepKind.Medium);
        }

        _pollCallCounter++;
        if (_pollCallCounter >= Math.Max(1, playersInGame) * _pollRate)
        {
            StepSaveSweep();
            _pollCallCounter = 0;
        }
    }

    // The tick side of a sweep: re-arm it if the last one has finished, then step it once (or, from a
    // test hook, run the whole sweep). The medium tick also resets the realm-wide wander budget; the slow
    // tick starts a new slow-update cycle. Caller holds _monsterLock.
    private void KickMonsterSweep(MonsterSweepKind kind, bool completeSweep)
    {
        if (kind == MonsterSweepKind.Slow)
            _slowUpdateCycle++;
        if (kind == MonsterSweepKind.Medium)
            _wanderBudget = 0;

        var sweep = GetMonsterSweep(kind);
        RefreshMonsterBufferSlots();

        if (completeSweep)
        {
            sweep.Cursor = 0;
            sweep.Pending = 1;
            while (sweep.Pending > 0)
                StepMonsterSweep(kind);
            return;
        }

        if (sweep.Pending < 1)
            sweep.Pending++;
        StepMonsterSweep(kind);
    }

    private MonsterSweep GetMonsterSweep(MonsterSweepKind kind) => kind switch
    {
        MonsterSweepKind.Fast => _fastMonsterSweep,
        MonsterSweepKind.Medium => _mediumMonsterSweep,
        _ => _slowMonsterSweep,
    };

    private void StepMonsterSweep(MonsterSweepKind kind)
    {
        var sweep = GetMonsterSweep(kind);
        if (sweep.Pending <= 0)
            return;

        var monster = GetLiveMonsterInSlot(sweep.Cursor);
        if (monster != null)
        {
            switch (kind)
            {
                case MonsterSweepKind.Fast: FastUpdateMonster(monster); break;
                case MonsterSweepKind.Medium: MediumUpdateMonster(monster); break;
                default: SlowUpdateMonster(monster); break;
            }
        }

        sweep.Cursor++;
        if (sweep.Cursor >= _monsterBufferSlots.Length)
        {
            sweep.Cursor = 0;
            sweep.Pending--;
        }
    }

    // Medium update: active-spell upkeep, then (when not locked onto a player) the ambient wander.
    private void MediumUpdateMonster(MonsterInstance monster)
    {
        if (monster.ActiveSpells.Count > 0)
        {
            // Apply over-time spell effects FIRST, then age the slots (mirrors the player order in
            // ProcessActiveSpellUpkeep). A drain death ends this monster's update.
            if (monster.TickSpellUpkeepEffects(Database))
            {
                _sweepKills.Add(monster);
                return;
            }
            monster.TickActiveSpells(Database);
        }

        TryWanderMonster(monster, _sweepMovementAnnouncements);
    }

    // Slow update, at most once per slow cycle: forget the last move direction (so the next wander may
    // repeat it), drain poison, then regenerate HP — death is judged after both.
    private void SlowUpdateMonster(MonsterInstance monster)
    {
        if (monster.SlowUpdateStamp == _slowUpdateCycle)
            return;

        monster.SlowUpdateStamp = _slowUpdateCycle;
        monster.LastMoveDirection = "";
        monster.ApplySlowUpdate();
        if (monster.IsDead)
            _sweepKills.Add(monster);
    }

    // Fast update: only for a monster locked onto a player or owned by one. A smash-dazed one counts its
    // daze down; a hostile lock counts a miss while it can't reach its player, and lapses past the limit.
    private void FastUpdateMonster(MonsterInstance monster)
    {
        if (!monster.HasLockedTarget && !monster.HasPlayerOwner)
            return;

        ProcessMonsterKnockdownTick(monster);
        if (monster.HasLockedTarget && !monster.HasPlayerOwner)
            ProcessHostileLockMiss(monster);
    }

    // The background save, one record per step. Reaching a monster's slot saves its record, which is
    // what clears its dirty byte.
    private void StepSaveSweep()
    {
        if (_saveSweepPending <= 0)
            return;

        int bufferSize = _saveSweepBuffer == SaveSweepMonsterBuffer
            ? _monsterBufferSlots.Length
            : SaveSweepBufferSizes[_saveSweepBuffer];

        if (_saveSweepBuffer == SaveSweepMonsterBuffer && _saveSweepIndex < _monsterBufferSlots.Length)
        {
            var monster = _monsterBufferSlots[_saveSweepIndex];
            if (monster is { IsDirty: true })
            {
                monster.IsDirty = false;
                _saveSweepWrites++;
            }
        }

        _saveSweepIndex++;
        if (_saveSweepIndex > bufferSize)
        {
            _saveSweepIndex = 0;
            _saveSweepBuffer++;
            if (_saveSweepBuffer >= SaveSweepBufferSizes.Length)
            {
                _saveSweepBuffer = 0;
                _saveSweepPending--;
            }
        }
    }

    // SECBUFF (every 300 s): start a save round if none is running, and step it once.
    private void KickSaveSweep()
    {
        if (_saveSweepPending < 1)
            _saveSweepPending++;
        StepSaveSweep();
    }

    // Each slow tick stock nudges POLLRATE by how many records the save wrote: up 50 past 50 writes, down
    // 50 (never below its configured start) under 10. One save step per ~7.5 s can't write 50 records in
    // 30 s, so at stock settings it stays at 750.
    private void AdjustPollRateFromSaveWrites()
    {
        if (_saveSweepWrites < 51)
        {
            if (_saveSweepWrites < 10 && _pollRate > InitialPollRate)
                _pollRate -= 50;
        }
        else if (_pollRate < MaxPollRate)
        {
            _pollRate += 50;
        }
        _saveSweepWrites = 0;
    }

    // Nightly cleanup saves every buffer at once, clearing every dirty byte.
    private void ClearAllMonsterDirtyFlags()
    {
        lock (_monsterLock)
        {
            foreach (var list in _roomMonsters.Values)
                foreach (var monster in list)
                    monster.IsDirty = false;
        }
    }

    // Keep the slot table in step with the world: free the slots of monsters that died or left it, and
    // give new monsters slots in the order they spawned. Stock numbers each new monster one past the
    // last and takes the first free slot from (number % MONSBUF) onward. Past MONSBUF live monsters stock
    // would start evicting; we grow the table instead so no monster drops out of its updates.
    private void RefreshMonsterBufferSlots()
    {
        for (int slot = 0; slot < _monsterBufferSlots.Length; slot++)
        {
            var monster = _monsterBufferSlots[slot];
            if (monster != null && (monster.IsDead || !IsMonsterInWorld(monster)))
            {
                _monsterBufferSlots[slot] = null;
                monster.BufferSlot = -1;
            }
        }

        List<MonsterInstance>? unslotted = null;
        foreach (var list in _roomMonsters.Values)
            foreach (var monster in list)
                if (monster.BufferSlot < 0 && !monster.IsDead)
                    (unslotted ??= []).Add(monster);

        if (unslotted == null)
            return;

        foreach (var monster in unslotted.OrderBy(m => m.SpawnSequence))
            AssignMonsterBufferSlot(monster);
    }

    private void AssignMonsterBufferSlot(MonsterInstance monster)
    {
        int length = _monsterBufferSlots.Length;
        int start = ++_nextMonsterBufferId % length;
        for (int probe = 0; probe < length; probe++)
        {
            int slot = (start + probe) % length;
            if (_monsterBufferSlots[slot] == null)
            {
                _monsterBufferSlots[slot] = monster;
                monster.BufferSlot = slot;
                return;
            }
        }

        Array.Resize(ref _monsterBufferSlots, length * 2);
        _monsterBufferSlots[length] = monster;
        monster.BufferSlot = length;
    }

    private MonsterInstance? GetLiveMonsterInSlot(int slot)
    {
        if (slot >= _monsterBufferSlots.Length)
            return null;

        var monster = _monsterBufferSlots[slot];
        return monster == null || monster.IsDead || !IsMonsterInWorld(monster) ? null : monster;
    }

    private bool IsMonsterInWorld(MonsterInstance monster)
        => _roomMonsters.TryGetValue((monster.MapNumber, monster.RoomNumber), out var list) && list.Contains(monster);

    // Kills from spell upkeep or poison found during a batch of steps. Caller holds _monsterLock.
    private void ResolveSweepKills()
    {
        if (_sweepKills.Count == 0)
            return;

        var kills = _sweepKills.ToList();
        _sweepKills.Clear();
        foreach (var monster in kills)
            KillMonsterFromUpkeep(monster);
    }

    // Movement lines from a batch of steps, sent once the monster lock is released.
    private List<MonsterMovementAnnouncement> TakeSweepMovementAnnouncements()
    {
        var announcements = _sweepMovementAnnouncements.ToList();
        _sweepMovementAnnouncements.Clear();
        return announcements;
    }

    private void AnnounceSweepMovements()
    {
        List<MonsterMovementAnnouncement> announcements;
        lock (_monsterLock)
            announcements = TakeSweepMovementAnnouncements();
        if (announcements.Count > 0)
            AnnounceMonsterMovements(announcements);
    }

    // Test seam: the buffer slot a monster has been given (refreshing the table first).
    internal int GetMonsterBufferSlotForTests(MonsterInstance monster)
    {
        lock (_monsterLock)
        {
            RefreshMonsterBufferSlots();
            return monster.BufferSlot;
        }
    }

    // Test seam: drive the host poll clock by a number of main-loop passes, as the pump would.
    internal void RunHostPollPassesForTests(int passes)
    {
        int playersInGame = _onlinePlayers.Count;
        lock (_monsterLock)
        {
            RefreshMonsterBufferSlots();
            for (long call = 0; call < (long)passes * playersInGame; call++)
                RunPollCall(playersInGame);
            ResolveSweepKills();
        }
        AnnounceSweepMovements();
    }

    // Test seam: tick-side kicks without completing the sweep (the paced, production behaviour).
    internal void KickMediumMonsterSweepForTests()
    {
        lock (_monsterLock)
        {
            KickMonsterSweep(MonsterSweepKind.Medium, completeSweep: false);
            ResolveSweepKills();
        }
        AnnounceSweepMovements();
    }

    // Test seam: run the save sweep a number of steps (arming it first).
    internal void RunSaveSweepStepsForTests(int steps)
    {
        lock (_monsterLock)
        {
            RefreshMonsterBufferSlots();
            if (_saveSweepPending < 1)
                _saveSweepPending++;
            for (int i = 0; i < steps; i++)
                StepSaveSweep();
        }
    }
}
