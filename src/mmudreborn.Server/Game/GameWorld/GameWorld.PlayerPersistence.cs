using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using mmudreborn.Data;
using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class GameWorld
{
    // Write-behind player persistence. Stock kept live state in RAM and persisted lazily; we do the
    // same so the per-command save's database round-trip no longer runs under WorldStateGate (which
    // froze the whole world for the duration of every command's save). A command marks its player dirty
    // — a cheap in-memory set insert under the gate it already holds — and a background loop later takes
    // a CONSISTENT snapshot under a brief gate hold (CPU-only: field reads + JSON) and executes the
    // upsert OFF the gate. Coalescing is automatic: a player dirtied N times between flushes is written
    // once, with its latest state.
    //
    // Durability: a crash loses at most one flush interval of NON-critical progress. Every durability-
    // critical save (logout/RemovePlayer, death, reroll, sysop edits, shutdown) still calls SavePlayer
    // synchronously and is persisted immediately; logout/shutdown also drop the pending dirty mark so a
    // late flush can't roll an authoritative save back to an older snapshot.
    private readonly ConcurrentDictionary<string, Player> _dirtyPlayers = new();
    private CancellationTokenSource? _persistenceCts;
    private Task? _persistenceLoopTask;

    private static readonly TimeSpan PlayerPersistenceFlushInterval = TimeSpan.FromSeconds(1);

    // OFF by default so the unit/integration test harnesses keep the original synchronous-save behavior
    // (the DB reflects each command the instant it returns — deterministic). The production host turns it
    // on (CWGamingServ/Program.cs) right after constructing the world.
    public bool PlayerWriteBehindEnabled { get; set; }

    // Marks the acting player for write-behind persistence. Called from GameSession under the world gate
    // after a command mutates state. When write-behind is disabled this falls back to the exact prior
    // behavior — a synchronous save — so nothing changes for callers that never start the loop.
    public void MarkPlayerDirty(Player player)
    {
        if (!PlayerWriteBehindEnabled)
        {
            PlayerRepo.SavePlayer(player);
            return;
        }

        _dirtyPlayers[player.Name] = player;
    }

    // Drops a pending dirty mark — used when a player is persisted synchronously (logout/death/etc.) so a
    // later flush can't overwrite that authoritative save with an older snapshot.
    private void ClearPendingPlayerSave(string playerName)
        => _dirtyPlayers.TryRemove(playerName, out _);

    private void StartPlayerWriteBehind()
    {
        if (!PlayerWriteBehindEnabled)
            return;

        _persistenceCts = new CancellationTokenSource();
        _persistenceLoopTask = Task.Run(() => RunPlayerPersistenceLoopAsync(_persistenceCts.Token));
    }

    private void StopPlayerWriteBehind()
    {
        _persistenceCts?.Cancel();
        try
        {
            _persistenceLoopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces as a faulted/cancelled task on Wait — expected on shutdown.
        }

        _persistenceCts?.Dispose();
        _persistenceCts = null;
        _persistenceLoopTask = null;
    }

    private async Task RunPlayerPersistenceLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PlayerPersistenceFlushInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                    break;
                await FlushPendingPlayerSavesAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                GameDiagnostics.RecordBackgroundException(ex, "persistence");
                if (GameDiagnostics.RethrowBackgroundExceptions) throw;
            }
        }
    }

    // Persists every currently-dirty online player: snapshot under a brief gate hold (consistent, no
    // I/O), then execute the upsert off-gate. Public so shutdown and tests can force a deterministic
    // drain. A player who left since being marked is skipped — RemovePlayer already saved them.
    public async Task FlushPendingPlayerSavesAsync()
    {
        if (_dirtyPlayers.IsEmpty)
            return;

        foreach (var name in _dirtyPlayers.Keys.ToList())
        {
            if (!_dirtyPlayers.TryRemove(name, out var player))
                continue;
            // Identity, not just presence. _dirtyPlayers is keyed by NAME but holds a live Player
            // reference, so asking only "is this name online?" let a mark left behind by a session that
            // has since gone away be written on top of the NEW session logged in under the same name —
            // silently reverting everything that changed since (HP, room, inventory, and any spell the
            // reverted snapshot still had active). Reachable when a hung connection's command loop
            // unblocks and marks dirty AFTER DisconnectOnlineCharacter has already swapped in the
            // reconnecting session. HandleDroppedConnection guards the same hazard the same way — it
            // "stops a stale socket from evicting a NEW session that has since logged in under the same
            // name" — the flusher just didn't.
            if (!_onlinePlayers.TryGetValue(name, out var online) || !ReferenceEquals(online, player))
                continue; // left the realm, or was replaced: the synchronous logout save is authoritative

            Action saveAction;
            await WorldStateGate.WaitAsync();
            long gateAcquiredTimestamp = Stopwatch.GetTimestamp();
            GameDiagnostics.MarkGateAcquired("persist-flush");
            try
            {
                saveAction = PlayerRepo.CapturePlayerSave(player);
            }
            finally
            {
                GameDiagnostics.RecordGateHold("persist-flush", gateAcquiredTimestamp);
                GameDiagnostics.MarkGateReleased();
                WorldStateGate.Release();
            }

            try
            {
                saveAction();
            }
            catch (Exception ex)
            {
                GameDiagnostics.RecordBackgroundException(ex, "persistence");
                // A failed write (e.g. the database briefly unavailable) must not lose the player's progress:
                // the mark was already removed above, so put it back and the next flush retries with the
                // then-latest state. Only while this exact session is still online — after a logout the
                // synchronous logout save is authoritative — and TryAdd, so a fresher mark is left alone.
                if (_onlinePlayers.TryGetValue(name, out var stillOnline) && ReferenceEquals(stillOnline, player))
                    _dirtyPlayers.TryAdd(name, player);
                if (GameDiagnostics.RethrowBackgroundExceptions) throw;
            }
        }
    }
}
