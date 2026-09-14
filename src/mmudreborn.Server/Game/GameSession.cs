using mmudreborn.Game;

namespace mmudreborn.Server;

public class GameSession : IGameSession
{
    private const string HostedAppId = "mmudreborn";
    private const string HostedWorldId = "default";
    private static readonly TimeSpan MudMenuMeditationDelay = TimeSpan.FromMilliseconds(5400);
    private static readonly TimeSpan MudMenuMeditationDotInterval = TimeSpan.FromMilliseconds(450);
    private const int MudMenuMeditationDotCount = 12;

    private readonly IGameClient _client;
    private readonly GameWorld _world;
    private bool _promptAlreadyBuffered;
    private CommandParser? _commandParser;
    private bool _returnToMudMenuRequested;

    public GameSession(IGameClient client, GameWorld world)
    {
        _client = client;
        _world = world;
    }

    public bool ReturnToMudMenu => _returnToMudMenuRequested;

    public async Task<bool> RunForPlayerAsync(Player player, CancellationToken ct)
    {
        _returnToMudMenuRequested = false;
        _client.SetCommandHistoryEnabled(true);
        _client.CurrentHostedAppId = HostedAppId;
        _client.CurrentHostedWorldId = HostedWorldId;

        try
        {
            _world.DisconnectOnlineCharacter(
                player.Name,
                $"{MudAnsi.BrightRed}You have been disconnected because this character logged in from another connection.{MudAnsi.Reset}");

            _client.Player = player;
            player.Client = _client;
            _world.AddPlayer(player);

            _world.BroadcastToRealm(
                $"{MudAnsi.White}{player.Name} just entered the Realm.{MudAnsi.Reset}",
                except: _client,
                reprompt: true);

            // The login sequence reads the drop-carrier flag and tells the player what happened last time,
            // then zeroes the word. This is the only moment they ever hear about the drop-carrier penalty —
            // when it was applied there was nobody on the socket to tell.
            if (player.DisconnectedWhilePlaying)
            {
                player.DisconnectedWhilePlaying = false;
                await _client.SendLineAsync();
                await _client.SendLineAsync("Last time you were on, you disconnected while playing.");
                await _client.SendLineAsync("The gods have punished you appropriately.");
                _world.PlayerRepo.SavePlayer(player);
            }

            var cmdParser = new CommandParser(_client, _world, player);
            _commandParser = cmdParser;
            await cmdParser.ShowCompletedBugReviewPromptsAsync();
            // On-entry room display honors brief/verbose (stock-faithful), unlike explicit `look` which is
            // always full. Brief players get the short room on login; Verbose (incl. brand-new chars) get full.
            await cmdParser.ShowRoomOnEntryAsync();

            // Re-arm monster aggro on entry. The incoming-attacker list and NextMonsterAttackAtUtc are
            // in-memory only, so a fresh login always starts with an empty list and a MinValue deadline.
            // A conscious player recovers on their first command (CheckEncounters runs after every one),
            // but a mortally wounded player can issue none — so without this they lie in a room full of
            // monsters, untouched, forever.
            //
            // Held under the world gate for the instant of mutation, like move-prep in GameLoop: this
            // writes shared combat state (the incoming-attacker list, NextMonsterAttackAtUtc, and a
            // monster's out-of-combat energy refill) that the combat beat reads under the same gate.
            //
            // Worth knowing: the rest of this entry sequence — AddPlayer, NotifyPlayerEnteredRoom (which
            // spawns the room's NPC primary and fires its CreateSpell), ShowRoomOnEntryAsync — all runs
            // UNGATED today. Gating the whole sequence is the real fix and a separate piece of work;
            // this at least doesn't add to the pile.
            await _world.WorldStateGate.WaitAsync(ct);
            long entryAggroTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            mmudreborn.Game.GameDiagnostics.MarkGateAcquired("session:entry-aggro");
            try
            {
                await cmdParser.CheckEncounters();
            }
            finally
            {
                mmudreborn.Game.GameDiagnostics.RecordGateHold("session:entry-aggro", entryAggroTimestamp);
                mmudreborn.Game.GameDiagnostics.MarkGateReleased();
                _world.WorldStateGate.Release();
            }

            await GameLoop(player, cmdParser, ct);

            return (cmdParser.ReturningToMenu || _returnToMudMenuRequested) && _client.Connected;
        }
        finally
        {
            _client.SetCommandHistoryEnabled(false);
        }
    }

    private async Task GameLoop(Player player, CommandParser cmdParser, CancellationToken ct)
    {
        bool suppressInitialBlankCommands = true;
        int suppressedInitialBlankCommandCount = 0;
        DateTime suppressInitialBlankCommandsUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        bool pendingMudMenuExit = false;
        DateTime pendingMudMenuExitAtUtc = DateTime.MinValue;
        DateTime nextMudMenuExitDotAtUtc = DateTime.MinValue;
        int mudMenuExitDotsPrinted = 0;
        // Snapshot of player.CombatOutputSeq taken when the silent-meditation "x" exit begins. Combat
        // is now resolved by the world combat coordinator (GameWorld.Combat.cs), not this loop, so we
        // detect "a real hit landed → interrupt the exit" by watching that counter advance instead of
        // the old per-session ProcessRealtimeCombatTick printedCombat return.
        long combatOutputSeqAtExitStart = 0;

        while (!ct.IsCancellationRequested && _client.Connected)
        {
            player.SuppressBroadcastReprompt = false;
            // GMCP turn-state feed (vitals + status + room occupants) for opted-in clients.
            // The top-of-loop prompt is the turn boundary where the server is idle awaiting
            // input — exactly when the client may safely act. No-op for legacy clients.
            await GmcpEmitter.SendTurnStateAsync(_client, _world, player);
            if (!pendingMudMenuExit && !_promptAlreadyBuffered)
                await _client.SendAsync(GameAnsi.Prompt(player));
            _promptAlreadyBuffered = false;

            string? input2;
            bool completedMudMenuExit = false;
            while (true)
            {
                input2 = await _client.ReadLineEchoAsync(250, true, ct);

                if (input2 == null)
                    break;

                if (!input2.Equals(GameTransportConstants.ReadTimeoutSentinel, StringComparison.Ordinal))
                    break;

                // Combat is resolved by the world combat coordinator (GameWorld.Combat.cs), which sends
                // its own output and reprompts the player; this poll only handles the silent-meditation
                // "x" exit (gossip/auction/say broadcasts never flow through here).
                if (pendingMudMenuExit)
                {
                    // A real combat hit (mob attacking, PvP retaliation, etc.) breaks meditation. The
                    // coordinator bumps player.CombatOutputSeq whenever it actually sends combat output
                    // to this player; an advance since the exit began means a hit landed → interrupt.
                    if (System.Threading.Interlocked.Read(ref player.CombatOutputSeq) != combatOutputSeqAtExitStart)
                    {
                        pendingMudMenuExit = false;
                        player.SuppressBroadcastReprompt = false;
                        if (!_client.IsAtLineStart)
                            await _client.SendLineAsync();
                        await _client.SendLineAsync($"{MudAnsi.BrightRed}Your meditation is interrupted!{MudAnsi.Reset}");
                        await _client.SendAsync(GameAnsi.Prompt(player));
                        _promptAlreadyBuffered = true;
                        continue;
                    }

                    DateTime now = DateTime.UtcNow;
                    while (mudMenuExitDotsPrinted < MudMenuMeditationDotCount && now >= nextMudMenuExitDotAtUtc)
                    {
                        await _client.SendAsync(".");
                        mudMenuExitDotsPrinted++;
                        nextMudMenuExitDotAtUtc += MudMenuMeditationDotInterval;
                    }

                    if (now >= pendingMudMenuExitAtUtc)
                    {
                        if (!_client.IsAtLineStart)
                            await _client.SendLineAsync();

                        ReturnPlayerToMudMenu(player);
                        completedMudMenuExit = true;
                        break;
                    }

                    continue;
                }
            }

            if (completedMudMenuExit)
                break;

            if (input2 == null)
                break;

            string trimmedInput = input2.Trim();
            if (pendingMudMenuExit)
            {
                // The QUIT silent-meditation exit: while
                // waiting to exit, ONLY the `break` command aborts it — the dispatcher routes `break`
                // to cancelling the exit. A bare Enter OR any other command is refused with
                // "You may not perform any commands while waiting to exit!" and the meditation
                // continues; the player still exits when the delay elapses. (Real combat hits interrupt
                // it via the CombatOutputSeq branch in the sentinel-tick loop above.)
                if (!trimmedInput.Equals("break", StringComparison.OrdinalIgnoreCase))
                {
                    if (!_client.IsAtLineStart)
                        await _client.SendLineAsync();
                    await _client.SendLineAsync("You may not perform any commands while waiting to exit!");
                    continue;
                }

                // `break` cancels the pending exit and returns to the game prompt.
                pendingMudMenuExit = false;
                player.SuppressBroadcastReprompt = false;
                if (!_client.IsAtLineStart)
                    await _client.SendLineAsync();
                await _client.SendLineAsync("Your meditation has been interrupted - you may not exit now!");
                await _client.SendAsync(GameAnsi.Prompt(player));
                _promptAlreadyBuffered = true;
                continue;
            }

            if (suppressInitialBlankCommands)
            {
                if (string.IsNullOrEmpty(trimmedInput) &&
                    suppressedInitialBlankCommandCount < 4 &&
                    DateTime.UtcNow <= suppressInitialBlankCommandsUntilUtc)
                {
                    suppressedInitialBlankCommandCount++;
                    _promptAlreadyBuffered = true;
                    continue;
                }

                if (!string.IsNullOrEmpty(trimmedInput) || DateTime.UtcNow > suppressInitialBlankCommandsUntilUtc)
                    suppressInitialBlankCommands = false;
            }

            if (await _world.BbsCommandDispatcher.TryDispatchAsync(trimmedInput, _client, _world, _client.CurrentBbsUserName))
                continue;

            if (trimmedInput.Equals("x", StringComparison.OrdinalIgnoreCase))
            {
                pendingMudMenuExit = true;
                pendingMudMenuExitAtUtc = DateTime.UtcNow + MudMenuMeditationDelay;
                nextMudMenuExitDotAtUtc = DateTime.UtcNow;
                mudMenuExitDotsPrinted = 0;
                combatOutputSeqAtExitStart = System.Threading.Interlocked.Read(ref player.CombatOutputSeq);
                // Suppress reprompts from incoming broadcasts (gossip/auction/realm chatter) so the
                // meditation line of dots isn't trampled by a redrawn prompt. The broadcast TEXT
                // still prints; only the trailing prompt redraw is muted. Cleared on cancel/exit.
                player.SuppressBroadcastReprompt = true;
                await _client.SendLineAsync("You will exit after a period of silent meditation.");
                continue;
            }

            player.SuppressBroadcastReprompt = true;
            bool continueLoop = true;
            if (cmdParser.IsAwaitingBugReportInput || CommandParser.IsOffGateCommand(trimmedInput))
            {
                // Commands that never touch live mutable world state run entirely OFF the global
                // game-state gate: read-only info (who/top/hall), the whole bug subsystem
                // (its own DB table — including the active report wizard), and the read-only SYSOP
                // sub-commands (status/list/report/buffers/diag/map — snapshot reads only). This stops a
                // DB round-trip from freezing the whole world and keeps these instant instead of queueing
                // behind a combat round. No SavePlayer / CheckEncounters: nothing changed and none start
                // combat. Stock fidelity is preserved — every state-mutating command still serializes below.
                _client.BeginBuffering();
                await _client.FlushDeferredBroadcastLinesAsync();
                try
                {
                    continueLoop = await cmdParser.ProcessCommand(trimmedInput);
                }
                catch (Exception ex)
                {
                    mmudreborn.Game.GameDiagnostics.RecordBackgroundException(ex, CommandParser.GateHoldVerbLabel(trimmedInput));
                    if (mmudreborn.Game.GameDiagnostics.RethrowBackgroundExceptions) throw;
                    continueLoop = true;
                }
            }
            else
            {
            // The per-player action-delay counter: a pending delay defers THIS
            // player's next command — it does NOT freeze the world. Stock decrements the counter on the
            // player's own tick and queues their input while it is non-zero; every other player and the
            // combat round keep running. We reproduce that by sleeping the player's OWN session loop OFF
            // the global gate, then acquiring the gate only for the instant of actual mutation.
            //
            // NextActionAllowedAtUtc = the door/bash/skill delay; gates the NEXT command of ANY kind.
            TimeSpan pendingActionDelay = player.NextActionAllowedAtUtc - DateTime.UtcNow;
            if (pendingActionDelay > TimeSpan.Zero)
                await Task.Delay(pendingActionDelay, ct);

            // The stock delay-before-move (the move adds the delay; relocation happens only AFTER it drains).
            // For a directional move we pay the move delay HERE, off the gate, while still standing in the
            // origin room — so the combat round keeps swinging at us and we can't instant-bounce out of a
            // room before its monster engages (fixes "running away too easy"). The delay is computed under
            // a brief gate (it reads shared room/occupant state); then we wait it off-gate and re-check:
            // if a combat pulse killed or teleported us during the wait, the queued move is void.
            bool runCommand = true;
            if (CommandParser.IsMovementCommand(trimmedInput))
            {
                await _world.WorldStateGate.WaitAsync(ct);
                long prepTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                mmudreborn.Game.GameDiagnostics.MarkGateAcquired("command:move-prep");
                TimeSpan moveExposure;
                try { moveExposure = cmdParser.ComputeMoveExposureDelay(trimmedInput); }
                finally
                {
                    mmudreborn.Game.GameDiagnostics.RecordGateHold("command:move-prep", prepTimestamp);
                    mmudreborn.Game.GameDiagnostics.MarkGateReleased();
                    _world.WorldStateGate.Release();
                }

                if (moveExposure > TimeSpan.Zero)
                {
                    int exposureMap = player.CurrentMapNumber, exposureRoom = player.CurrentRoomNumber;
                    await Task.Delay(moveExposure, ct);
                    if (player.CurrentMapNumber != exposureMap || player.CurrentRoomNumber != exposureRoom)
                    {
                        // A combat pulse relocated/killed us mid-move (stock re-checks after the
                        // delay and aborts). Drop the queued move; loop on to the prompt.
                        runCommand = false;
                        continueLoop = true;
                    }
                }
            }

            if (runCommand)
            {
            // Serialize this command against the world combat round on the single global game-state
            // gate (GameWorld.WorldStateGate), reproducing the stock one cooperative thread: stock
            // The original never runs a user command and the combat round at the same
            // time, for ANY player. Holding the global gate here means a round in progress finishes
            // before this command mutates state, and a command in progress finishes before the round
            // runs — so a monster swing is never dropped because someone was mid-command. The player's
            // persistence is marked here but the database round-trip happens off-gate (write-behind); the
            // buffered output's socket flush also happens after release (below).
            await _world.WorldStateGate.WaitAsync(ct);
            long gateAcquiredTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            string gateHoldLabel = CommandParser.GateHoldVerbLabel(trimmedInput);
            mmudreborn.Game.GameDiagnostics.MarkGateAcquired(gateHoldLabel);
            try
            {
                _client.BeginBuffering();
                await _client.FlushDeferredBroadcastLinesAsync();
                try
                {
                    continueLoop = await cmdParser.ProcessCommand(trimmedInput);
                }
                catch (Exception ex)
                {
                    // The host swallows per-connection exceptions to stay up, so a throwing command (e.g. a
                    // concurrency fault mid-move) was vanishing silently. Record it (rethrow in tests).
                    mmudreborn.Game.GameDiagnostics.RecordBackgroundException(ex, CommandParser.GateHoldVerbLabel(trimmedInput));
                    if (mmudreborn.Game.GameDiagnostics.RethrowBackgroundExceptions) throw;
                    continueLoop = true;
                }

                if (continueLoop)
                {
                    // Write-behind: mark the player to be persisted off-gate by the world's flush loop
                    // instead of doing the database round-trip here under the gate (which froze the whole
                    // world for the duration). Falls back to a synchronous save when write-behind is off.
                    _world.MarkPlayerDirty(player);
                    await cmdParser.CheckEncounters();
                }
            }
            finally
            {
                mmudreborn.Game.GameDiagnostics.RecordGateHold(gateHoldLabel, gateAcquiredTimestamp);
                mmudreborn.Game.GameDiagnostics.MarkGateReleased();
                _world.WorldStateGate.Release();
            }
            }
            }

            if (!continueLoop)
            {
                _client.FlushOutput();
                break;
            }

            player.SuppressBroadcastReprompt = false;
            await _client.SendAsync(GameAnsi.Prompt(player));
            _promptAlreadyBuffered = true;
            _client.FlushOutput();
        }

        // Skip the teardown save when this character has already been removed from the world (e.g. a sysop
        // BOARD RESET wiped everyone, or any op that RemovePlayer'd them). Re-saving here would resurrect a
        // deliberately-deleted row. A normal logout/disconnect leaves the player online at this point, so
        // it still persists as before.
        //
        // Identity, not presence — the same hazard the write-behind flusher guards (GameWorld
        // .PlayerPersistence, and see HandleDroppedConnection, which words it as "stops a stale socket from
        // evicting a NEW session that has since logged in under the same name"). Asking only "is this name
        // online?" answered YES after a reconnect, because the NEW session is online under that name, so a
        // hung session unwinding here wrote its own stale snapshot over the row the live session is using —
        // silently rolling back HP, room, inventory and active spells to whatever they were when the hang
        // began. That is the ordinary reconnect ordering, not a rare race: DisconnectOnlineCharacter closes
        // the old socket, which is exactly what unblocks this loop.
        if (!cmdParser.ReturningToMenu && ReferenceEquals(_world.FindOnlinePlayer(player.Name), player))
        {
            // Warp-maze escape (the hang-up path): make this final session save land at the maze's
            // ExitRoom, so the relocation persists even before the host's teardown RemovePlayer runs.
            _world.ApplyWarpExitRoomOnLeave(player);
            _world.PlayerRepo.SavePlayer(player);
        }
    }

    public async Task HandleExternalPlayerDeathAsync()
    {
        if (_commandParser == null)
            return;

        await _client.EnsureNewLineAsync();
        await _commandParser.HandlePlayerDeath();
    }

    public async Task ShowCurrentRoomAsync()
    {
        if (_commandParser == null)
            return;

        await _client.EnsureNewLineAsync();
        await _commandParser.HandleLook();
    }

    private void ReturnPlayerToMudMenu(Player player)
    {
        player.ClearCombatState();
        player.IsResting = false;
        player.IsMeditating = false;
        player.IsSneaking = false;
        player.IsHidden = false;
        player.SuppressBroadcastReprompt = false;

        // Warp-maze escape (the graceful-quit path): apply ExitRoom before this save, since the persist
        // here precedes RemovePlayer (which also applies it, but only in-memory under save: false).
        _world.ApplyWarpExitRoomOnLeave(player);
        _world.PlayerRepo.SavePlayer(player);
        _world.RemovePlayer(player, save: false);
        _client.Player = null;
        _client.CurrentHostedAppId = HostedAppId;
        _client.CurrentHostedWorldId = HostedWorldId;
        _world.BroadcastToRealm(
            $"{MudAnsi.White}{player.Name} just left the Realm.{MudAnsi.Reset}",
            except: _client,
            reprompt: true);
        _returnToMudMenuRequested = true;
    }
}
