using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace mmudreborn.Server;

public partial class CommandParser
{
    private static bool TryGetDirectionOffset(string dir, out int dx, out int dy)
    {
        dx = 0;
        dy = 0;
        switch (dir)
        {
            case "north": dy = 1; return true;
            case "south": dy = -1; return true;
            case "east": dx = 1; return true;
            case "west": dx = -1; return true;
            case "northeast": dx = 1; dy = 1; return true;
            case "northwest": dx = -1; dy = 1; return true;
            case "southeast": dx = 1; dy = -1; return true;
            case "southwest": dx = -1; dy = -1; return true;
            default: return false;
        }
    }

    private Task SendMortallyWoundedMovementMessageAsync()
        => _client.SendLineAsync(MudAnsi.Error("You may not do that while you are mortally wounded!"));

    // The player-move access-control gate, run on the
    // DESTINATION room as a player moves in. Returns true when entry is permitted. Blocks (with the
    // matching stock message) on: a quest/level-capped room (type 2) whose MaxIndex level cap is below the
    // player's level; entering a protected/safe room while in autocombat or during a PvP retaliation
    // window; entering an arena (type 5) below half max HP or during retaliation. Leaving an arena
    // (source type 5 → non-arena) clears the mover's autocombat as a side effect but still allows the
    // move. Spawn side-effects of the stock gate (auto-NPC, gang fill type 3) are handled by
    // the spawn system, not here. The caller prints the generic "You are not permitted in that room!".
    private Task<bool> IsUserAllowedInRoomAsync(Room destRoom, int sourceRoomType)
        => IsRoomEntryAllowedForAsync(_player, _client, destRoom, sourceRoomType);

    // The protected/quest/arena entry gate. On a move the gate
    // is run against the MOVING user's own state — and crucially a party
    // FOLLOWER is moved through this same gate, keyed on the
    // FOLLOWER's own combat/level/HP state, not the leader's. Hence the per-mover parameterization.
    private async Task<bool> IsRoomEntryAllowedForAsync(Player mover, IGameClient moverClient, Room destRoom, int sourceRoomType)
    {
        // "Inside autocombat" is true only when the player has ISSUED an attack / offensive
        // cast — the engage happens on the PvP attack and the player-attacks-monster paths
        // but NOT when a monster hits the player. So a player
        // who is merely BEING attacked (and hasn't fought back) is NOT "in autocombat" and is NOT barred —
        // e.g. a party leader an aggressive mob aggroed can still walk into a safe room. `InCombat`
        // is set on exactly those player-initiated attack/cast paths, matching stock.
        bool inAutocombat = mover.InCombat;
        bool inRetaliation = _world.EvilTimers.IsInRetaliation(mover.Name);

        var verdict = RoomAccessGate.Evaluate(
            destRoom.RoomType, destRoom.MaxIndex, destRoom.IsProtected,
            sourceRoomType, mover.Level, mover.CurrentHP, mover.MaxHP,
            inAutocombat, inRetaliation, out bool clearAutocombatOnLeave);

        // Arena-leave side effect (drop both sides of autocombat). Only on an allowed move.
        if (clearAutocombatOnLeave)
            mover.ClearCombatState();

        string? blockMessage = verdict switch
        {
            RoomEntryVerdict.QuestLevelCap => "You have progressed too far for this room.",
            RoomEntryVerdict.ProtectedInCombat => "You may not enter that room while in combat.",
            RoomEntryVerdict.ProtectedInRetaliation => "You may not enter that room during a retaliation time-period.",
            RoomEntryVerdict.ArenaUnhealthy => "You are not healthy enough to enter that room.",
            RoomEntryVerdict.ArenaInRetaliation => "You may not enter that room during a retaliation time-period.",
            _ => null,
        };

        if (blockMessage == null)
            return true;

        await moverClient.SendLineAsync(blockMessage);
        return false;
    }

    // The follow loop: after the leader moves, each follower is
    // moved the same way; a follower whose move fails (blocked exit, locked door,
    // the entry gate, etc.) OR who is unconscious (HP<1) has their follow link cleared
    // — dropped from the party PER-FOLLOWER, never a whole-party disband.
    // This upholds the party-in-room invariant: a member left behind for ANY reason must leave the party
    // (there is no valid state where a party spans two rooms). The leader and followers who DID make the
    // move stay together. `failureMessage` is the per-mover reason (the room gate sends its own line, so
    // pass null there); we always tell the dropped follower they're no longer following.
    private void DropFollowerFromLeaderParty(Player follower, string? failureMessage)
    {
        if (!string.IsNullOrWhiteSpace(failureMessage))
            _world.SendToPlayer(follower.Name, failureMessage!);

        if (_world.TryLeaveParty(follower, out var selfMessage, out _))
            _world.SendToPlayer(follower.Name, GameAnsi.PartyNotice(selfMessage));
    }

    private async Task HandleMovement(string direction)
    {
        if (_player.IsUnconscious)
        {
            await SendMortallyWoundedMovementMessageAsync();
            return;
        }

        if (IsMovementBlockedByStatus)
        {
            await _client.SendLineAsync(GetMovementBlockMessage());
            return;
        }

        // Confusion is rolled once per typed command at the dispatcher (ProcessCommand);
        // a confused move already fumbles there. Stock has NO confusion check inside the move itself, so there
        // is intentionally no second roll here (and follow/auto-movement is not confusion-gated).
        if (IsOverEncumberedForMovement())
        {
            await _client.SendLineAsync("You are too heavy to move!");
            return;
        }

        var currentRoom = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (currentRoom == null)
        {
            await _client.SendLineAsync(MudAnsi.Error("You are in a void!"));
            return;
        }

        Player? draggedPlayer = GetDraggedPlayerForMovement(currentRoom);
        string dragSuffix = draggedPlayer == null ? string.Empty : $" (Dragging {draggedPlayer.Name})";
        string movementDisplayName = $"{_player.Name}{dragSuffix}";

        var destination = _world.FindMovementExit(_player, currentRoom, direction);
        if (destination == null)
        {
            await _client.SendLineAsync("There is no exit in that direction!");
            if (_player.IsSneaking)
            {
                _player.IsSneaking = false;
                _player.IsHidden = false;
                await _client.SendLineAsync("You are no longer sneaking.");
            }
            return;
        }

        if (!_world.CanTraverseExit(_player, currentRoom, destination, out var failureMessage, out var successMessage))
        {
            if (destination.IsItemExit)
            {
                if (destination.FailureMessageNumber > 0 && _world.Database.Messages.TryGetValue(destination.FailureMessageNumber, out var itemFailureMessage) && !string.IsNullOrWhiteSpace(itemFailureMessage.Line2))
                {
                    _world.BroadcastToRoom(
                        _player.CurrentMapNumber,
                        _player.CurrentRoomNumber,
                        itemFailureMessage.Line2.Replace("%s", _player.Name, StringComparison.OrdinalIgnoreCase),
                        _client);
                }

                await _client.SendLineAsync($"{MudAnsi.BrightYellow}{failureMessage ?? "You do not have a necessary item!"}{MudAnsi.Reset}");
                return;
            }

            await _client.SendLineAsync(failureMessage ?? "There is no exit in that direction!");
            return;
        }

        if (!string.IsNullOrWhiteSpace(successMessage))
            await _client.SendLineAsync(successMessage);

        // Exit type 9: an armed trap on this exit fires as the player passes through it.
        var trapMessage = _world.MaybeTriggerTrapOnTraverse(_player, destination);
        if (trapMessage != null)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{trapMessage}{MudAnsi.Reset}");
            if (_player.CurrentHP <= Player.DeathHP)
            {
                await HandlePlayerDeath();
                return;
            }
        }

        if (destination.ExitType == RoomExitType.Cast)
        {
            var preMoveSpellResult = await ExecuteTriggeredSpellByIdAsync(destination.Para1, showRoomAfterTeleport: true,
                TriggeredCastAnnounce.RoomCast);
            if (preMoveSpellResult.StopProcessing || preMoveSpellResult.Teleported)
                return;
        }

        // Item (3) AND Ticket (17) exits both spend a charge — exit type 3 deducts
        // a charge on traverse. This used to fire only for type 17, so every room-ticket inn
        // stairwell (all type 3) let one ticket work forever. See ConsumeItemChargeAsync (bug #211).
        if (destination.IsItemExit)
            await ConsumeItemChargeAsync(destination.RequiredItemId);

        var destRoom = _world.GetRoom(destination.TargetMap, destination.TargetRoom);
        if (destRoom == null)
        {
            await _client.SendLineAsync("That path leads nowhere...");
            return;
        }

        // The move's action delay (including the dragging doubling) is computed and
        // waited OFF the world gate BEFORE this handler runs (GameSession via ComputeMoveExposureDelay),
        // so by the time we relocate the delay is already spent in the origin room. Nothing to stamp here.

        // The destination access-control gate runs before the free attack.
        // On a block it prints the specific reason (inside the gate) AND the mover's generic
        // "You are not permitted in that room!", then aborts the move (the free attack is
        // never reached). Faithful to the stock ordering.
        if (!await IsUserAllowedInRoomAsync(destRoom, currentRoom.RoomType))
        {
            await _client.SendLineAsync("You are not permitted in that room!");
            return;
        }

        // As a non-sneaking player departs, an eligible room monster gets one free
        // swing. A swing that kills or holds/stuns the player cancels
        // the move; a plain hit does not. Runs before the room change so the hit lands in this room.
        if (await TryDepartingMonsterFreeAttackAsync(currentRoom))
            return;

        // Check for Action exits (ExitType=5) with Para1 message — e.g. skiff crossings.
        // These are directionally traversable but have custom self/depart/arrive messages.
        RoomMessage? actionMessage = null;
        bool useActionMessages = destination.ExitType == RoomExitType.Action &&
            destination.Para1 > 0 && _world.Database.Messages.TryGetValue(destination.Para1, out actionMessage);

        // Also check text-annotated exits for stock-derived travel messaging.
        var textExits = _world.GetTextCommandExits(currentRoom)
            .ToDictionary(exit => exit.Direction, StringComparer.OrdinalIgnoreCase);
        textExits.TryGetValue(direction, out var textExit);

        RoomMessage? itemSuccessMessage = null;
        bool useItemMessages = destination.IsItemExit &&
            destination.SuccessMessageNumber > 0 &&
            _world.Database.Messages.TryGetValue(destination.SuccessMessageNumber, out itemSuccessMessage);

        var departingMonsterAttackers = SnapshotDepartingMonsterAttackers(_player, currentRoom.MapNumber, currentRoom.RoomNumber);

        // Stock movement format: "%s%s moves into the room from the %s."
        string departDir = direction;
        string arriveDir = GetOppositeDirection(direction);

        // The sneak state is snapshotted up front, then the per-move
        // stealth check. The SNAPSHOT drives this move's messaging (a breaking sneak still renders with
        // stealthy per-observer notices); a failed roll clears the live sneak flag so it's dead from the
        // next move on. Capture the snapshot before resolving the roll.
        bool movingAsSneaker = _player.IsSneaking;
        if (movingAsSneaker)
        {
            await _client.SendLineAsync($"{MudAnsi.White}Sneaking...{MudAnsi.Reset}");
            await ResolveSneakMoveBreakAsync(currentRoom);
        }

        // Fully invisible sysops produce no departure/arrival messages at all.
        bool sysopSilentMove = _player.IsSysopInvisible;

        // Departure broadcast
        RoomMessage? hiddenTraversalMessage = null;
        bool useHiddenTraversalMessages = destination.IsHiddenExit && destination.IsPassableHiddenExit &&
            destination.Para3 > 0 && _world.Database.Messages.TryGetValue(destination.Para3, out hiddenTraversalMessage);

        if (sysopSilentMove)
        {
            // No departure message
        }
        else if (movingAsSneaker)
        {
            // Per-observer: each player independently checks perception vs stealth
            var departureObservers = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player);
            foreach (var observer in departureObservers)
            {
                if (ObserverNoticesSneaker(observer, _player))
                {
                    _world.SendToPlayer(observer.Name, GetSneakDepartureMessage(_player.Name, departDir));
                }
            }
        }
        else
        {
            if (useHiddenTraversalMessages)
            {
                string departMsg = hiddenTraversalMessage!.Line2.Replace("%s", movementDisplayName, StringComparison.OrdinalIgnoreCase);
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, departMsg, _client);
            }
            else if (useActionMessages && !string.IsNullOrWhiteSpace(actionMessage!.Line2))
            {
                string departMsg = actionMessage.Line2.Replace("%s", movementDisplayName, StringComparison.OrdinalIgnoreCase);
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, departMsg, _client);
            }
            else if (useItemMessages && !string.IsNullOrWhiteSpace(itemSuccessMessage!.Line2))
            {
                string departMsg = itemSuccessMessage.Line2.Replace("%s", movementDisplayName, StringComparison.OrdinalIgnoreCase);
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, departMsg, _client);
            }
            else if (textExit != null)
            {
                string departMsg = GetTextExitDepartureMessage(textExit);
                if (!string.IsNullOrWhiteSpace(departMsg))
                    _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, departMsg, _client);
            }
            else
            {
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                    GameAnsi.RoomMovementDeparture(movementDisplayName, departDir), _client);
            }
        }

        // Moving breaks hiding
        _player.IsHidden = false;

        // Move player
        _player.CurrentMapNumber = destination.TargetMap;
        _player.CurrentRoomNumber = destination.TargetRoom;
        _player.RecordMovementTrail(destination.TargetMap, destination.TargetRoom);
        _player.IsResting = false;
        _player.IsMeditating = false;
        // A move sets the "moved this tick" bit; with sneaking it lets you hide
        // with monsters present until the next upkeep clears it (the could-attack gate A). Only a
        // sneaking move grants it; a normal walk clears any stale value.
        _player.SneakedInThisTick = _player.IsSneaking;

        var triggeredExitSpellResult = await ApplyTriggeredExitSpellEffectsAsync(destination);
        if (triggeredExitSpellResult.StopProcessing)
            return;

        bool suppressStandardArrivalMessaging = triggeredExitSpellResult.Teleported;

        // Announce arrival
        if (sysopSilentMove)
        {
            // No arrival message
        }
        else if (!suppressStandardArrivalMessaging && movingAsSneaker)
        {
            // Per-observer: each player in the arrival room checks perception vs stealth
            var arrivalObservers = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player);
            foreach (var observer in arrivalObservers)
            {
                if (ObserverNoticesSneaker(observer, _player))
                {
                    _world.SendToPlayer(observer.Name, GetSneakArrivalMessage(_player.Name, arriveDir));
                }
            }
        }
        else if (!suppressStandardArrivalMessaging)
        {
            if (useHiddenTraversalMessages)
            {
                string arriveMsg = hiddenTraversalMessage!.Line3.Replace("%s", movementDisplayName, StringComparison.OrdinalIgnoreCase);
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, arriveMsg, _client);
            }
            else if (useActionMessages && !string.IsNullOrWhiteSpace(actionMessage!.Line3))
            {
                string arriveMsg = actionMessage.Line3.Replace("%s", movementDisplayName, StringComparison.OrdinalIgnoreCase);
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, arriveMsg, _client);
            }
            else if (useItemMessages && !string.IsNullOrWhiteSpace(itemSuccessMessage!.Line3))
            {
                string arriveMsg = itemSuccessMessage.Line3.Replace("%s", movementDisplayName, StringComparison.OrdinalIgnoreCase);
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, arriveMsg, _client);
            }
            else if (textExit != null)
            {
                string arriveMsg = GetTextExitArrivalMessage(textExit);
                if (!string.IsNullOrWhiteSpace(arriveMsg))
                    _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, arriveMsg, _client);
            }
            else
            {
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                    GameAnsi.RoomMovementArrival(movementDisplayName, arriveDir), _client);
            }

        }

        // A successful sneak (the stealth roll held, or Perfect Stealth) latches and
        // skips the entire action-exit branch, so the MOVER sees no self-traversal narration either — just
        // the "Sneaking..." line and the room description. Only a BROKEN sneak falls through and prints the
        // line (the same move that reveals you to the room). The room-visible depart/arrive lines are
        // already routed through the per-observer sneak model above, so they never leak the action message.
        bool sneakHeld = movingAsSneaker && _player.IsSneaking;

        // Adjacent rooms hear the move unless the sneak HELD. The call is gated on
        //     (not a party-follow move) && (was not sneaking || the sneak did not hold)
        // — so a sneak that BROKE, silently or loudly, is just
        // as audible next door as walking normally. We previously keyed off "did they attempt a
        // sneak", which silenced every failed attempt. Confirmed live: a silently-failed sneak did
        // produce "You hear movement to the south." for a listener one room over, while a sneak that
        // held produced nothing. The party-follow case is excluded, and our follower moves do
        // not route through here; a teleport is not a walk, and a sysop silent move is our own tool.
        if (!sysopSilentMove && !suppressStandardArrivalMessaging && !sneakHeld)
        {
            _world.BroadcastAdjacentMovementNoise(
                _player.CurrentMapNumber,
                _player.CurrentRoomNumber,
                excludeRooms: new[] { (currentRoom.MapNumber, currentRoom.RoomNumber), (_player.CurrentMapNumber, _player.CurrentRoomNumber) });
        }

        if (!sneakHeld)
        {
            if (!suppressStandardArrivalMessaging && useHiddenTraversalMessages)
            {
                await _client.SendLineAsync($"{MudAnsi.LinePreamble}{GameAnsi.TravelSelfMessageColor(TravelMessageType.HiddenTraversal, _player.PaletteId)}{hiddenTraversalMessage!.Line1}");
            }
            else if (!suppressStandardArrivalMessaging && useActionMessages && !string.IsNullOrWhiteSpace(actionMessage!.Line1))
            {
                await _client.SendLineAsync($"{MudAnsi.LinePreamble}{GameAnsi.TravelSelfMessageColor(TravelMessageType.ActionTraversal, _player.PaletteId)}{actionMessage.Line1}");
            }
            else if (!suppressStandardArrivalMessaging && useItemMessages && !string.IsNullOrWhiteSpace(itemSuccessMessage!.Line1))
            {
                await _client.SendLineAsync($"{MudAnsi.LinePreamble}{MudAnsi.BrightYellow}{itemSuccessMessage.Line1}");
            }
            else if (!suppressStandardArrivalMessaging && textExit != null)
            {
                var (goMsg, travelType) = GetTextExitSelfMessageWithType(textExit);
                if (!string.IsNullOrWhiteSpace(goMsg))
                    await _client.SendLineAsync($"{MudAnsi.LinePreamble}{GameAnsi.TravelSelfMessageColor(travelType, _player.PaletteId)}{goMsg}");
            }
        }

        if (!suppressStandardArrivalMessaging)
            ResolvePostTravelCombatState(_player, currentRoom, destination, departingMonsterAttackers);

        _world.NotifyPlayerEnteredRoom(_player);
        await HandleIndependentPartyTravelCleanupAsync(_player, disbandLeader: false);

        // NOTE: Stock queues the travel text and the room description
        // independently, but that queue lands in the same
        // DataToClient buffer — the sender thread batches everything into one
        // TCP segment.  We rely on GameSession's outer BeginBuffering/FlushOutput
        // to achieve the same single-segment delivery.

        // Look at new room (brief mode when walking)
        await ShowRoom(brief: _player.BriefMode);
        await MoveFollowingPartyMembersAsync(currentRoom, direction);
        await MoveDraggedPlayerAsync(currentRoom, direction, draggedPlayer);
        // Owned pets follow their master (they follow the owner's travel trail).
        _world.MovePlayerPetsToFollow(_player, currentRoom.MapNumber, currentRoom.RoomNumber);

        // Check for random encounters
        await CheckEncounters();
    }

    /// <summary>
    /// Handle "ask" command — NPC dialogue is data-driven through TextBlocks
    /// plus wcctext2.dat LinkTo metadata.
    /// </summary>
    private async Task HandleAsk(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Ask whom?");
            return;
        }

        if (!TryResolveConversationNpc(args, out var npc, out var topic))
        {
            await _client.SendLineAsync("You don't see that person here.");
            return;
        }

        var greetId = npc.Template.GreetTXT;
        if (greetId <= 0 || !_world.Database.TextBlocks.TryGetValue(greetId, out var greetText))
        {
            await _client.SendLineAsync($"The {npc.Name} has nothing to say to you.");
            return;
        }

        var greetBlock = ParseDialogueBlock(greetText);
        var clueKeywords = greetBlock.KeywordLinks.Keys
            .Where(keyword => !keyword.Equals("nothing", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (string.IsNullOrWhiteSpace(topic))
        {
            if (await TryExecuteTextBlockAsync(greetId, null, clueKeywords))
                return;

            await _client.SendLineAsync("Ask about what?");
            return;
        }

        if (TryResolveKeywordTarget(greetBlock.KeywordLinks, topic, out var topicId) &&
            await TryExecuteTextBlockAsync(topicId, null, clueKeywords))
        {
            return;
        }

        if (greetBlock.KeywordLinks.TryGetValue("nothing", out var fallbackId) &&
            await TryExecuteTextBlockAsync(fallbackId, null, clueKeywords))
        {
            return;
        }

        await _client.SendLineAsync($"The {npc.Name} has nothing to say about that.");
    }

    /// <summary>
    /// Handle "go", "enter", "borrow" commands for special exits.
    /// Uses the (Text: ...) annotation on room exits from the MDB data to match commands.
    /// Example: S=1/2335 (Special) (Text: borrow skiff, go skiff, row skiff)
    /// The full typed command (e.g. "borrow skiff", "go manhole") is matched against the Text commands.
    /// </summary>
    private async Task HandleGoEnter(string fullCommand)
    {
        if (string.IsNullOrWhiteSpace(fullCommand))
        {
            await _client.SendLineAsync("Go where?");
            return;
        }

        if (await TryHandleRoomAction(fullCommand))
            return;

        await _client.SendLineAsync("You don't see that here.");
    }

    private async Task<bool> TryHandleTextCommandExitAsync(string fullCommand, bool showMissingMessage)
    {
        if (_player.IsUnconscious)
        {
            await SendMortallyWoundedMovementMessageAsync();
            return true;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
            return false;

        var destination = FindTextCommandExit(room, fullCommand);
        if (destination == null)
        {
            if (showMissingMessage)
                await _client.SendLineAsync("You don't see that here.");

            return false;
        }

        await ExecuteTextCommandExitAsync(room, destination);
        return true;
    }

    private RoomExitDefinition? FindTextCommandExit(Room room, string fullCommand)
    {
        var target = fullCommand.Trim().ToLowerInvariant();

        foreach (var exit in _world.GetTextCommandExits(room))
        {
            foreach (var cmd in exit.GetCommandPhrases(_world.Database.Messages))
            {
                if (cmd == target)
                    return exit;
            }
        }

        return null;
    }

    private async Task ExecuteTextCommandExitAsync(Room room, RoomExitDefinition destination)
    {
        if (IsOverEncumberedForMovement())
        {
            await _client.SendLineAsync("You are too heavy to move!");
            return;
        }

        var destRoom = _world.GetRoom(destination.TargetMap, destination.TargetRoom);
        if (destRoom == null)
        {
            await _client.SendLineAsync("That path leads nowhere...");
            return;
        }

        // Text/special exits (skiff crossings, "go portal") are not directional moves, so the GameSession
        // pre-move exposure delay doesn't cover them; they resolve instantly. These are rare one-off
        // crossings, not spam-walkable corridors, so the lack of a pacing delay here is immaterial.

        if (!_world.CanTraverseExit(_player, room, destination, out var failureMessage, out var successMessage))
        {
            await _client.SendLineAsync(failureMessage ?? "You don't see that here.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(successMessage))
            await _client.SendLineAsync(successMessage);

        // A text-command exit still moves you through the stock mover, so the departing
        // free swing applies exactly as on a compass move: after the entry gate, before you leave.
        if (await TryDepartingMonsterFreeAttackAsync(room))
            return;

        bool wasSneaking = _player.IsSneaking;
        var departingMonsterAttackers = SnapshotDepartingMonsterAttackers(_player, room.MapNumber, room.RoomNumber);

        (string goMsg, TravelMessageType travelType) = GetTextExitSelfMessageWithType(destination);
        if (!string.IsNullOrWhiteSpace(goMsg))
            await _client.SendLineAsync($"{MudAnsi.LinePreamble}{GameAnsi.TravelSelfMessageColor(travelType, _player.PaletteId)}{goMsg}");

        // Broadcast the departure line (message Line2, e.g. "%s jumps into the large fountain.") to the
        // SOURCE room so onlookers see the actor leave — the cardinal HandleMovement path does this for
        // text exits too, but this text-COMMAND ("go fountain") path was dropping it, leaving the room
        // silent. Gated on sneaking, like every other departure broadcast. Emit while still in the
        // source room (before the position change below).
        if (!wasSneaking)
        {
            string departMsg = GetTextExitDepartureMessage(destination);
            if (!string.IsNullOrWhiteSpace(departMsg))
                _world.BroadcastToRoom(room.MapNumber, room.RoomNumber, departMsg, _client);
        }

        // Perfect/Supernatural Stealth (ability 186) never breaks on a move — including text-command
        // exits (the cardinal-move path already honors this via ResolveSneakMoveBreakAsync).
        if (!_player.HasPerfectStealth)
            _player.IsSneaking = false;
        _player.IsHidden = false;
        _player.IsResting = false;
        _player.IsMeditating = false;

        _player.CurrentMapNumber = destination.TargetMap;
        _player.CurrentRoomNumber = destination.TargetRoom;
        _player.RecordMovementTrail(destination.TargetMap, destination.TargetRoom);
        ResolvePostTravelCombatState(_player, room, destination, departingMonsterAttackers);
        _world.NotifyPlayerEnteredRoom(_player);
        await HandleIndependentPartyTravelCleanupAsync(_player, disbandLeader: false);

        string arriveMsg = GetTextExitArrivalMessage(destination);
        if (!string.IsNullOrWhiteSpace(arriveMsg))
        {
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                arriveMsg, _client);
        }

        // Gate on the POST-move sneak state, not the pre-move `wasSneaking` snapshot: stock keys
        // the noise off whether the sneak HELD through this move, so a sneak that broke on the
        // way is audible next door. The snapshot above is still right for the departure broadcast --
        // you were sneaking when you left the source room -- but it would wrongly silence a break.
        if (!_player.IsSneaking)
        {
            _world.BroadcastAdjacentMovementNoise(
                _player.CurrentMapNumber,
                _player.CurrentRoomNumber,
                excludeRooms: [(room.MapNumber, room.RoomNumber), (_player.CurrentMapNumber, _player.CurrentRoomNumber)]);
        }

        await ShowRoom(brief: _player.BriefMode);
        await MoveFollowingPartyMembersThroughTextExitAsync(room, destination);
        _world.MovePlayerPetsToFollow(_player, room.MapNumber, room.RoomNumber);
        await CheckEncounters();
    }

    private async Task MoveFollowingPartyMembersThroughTextExitAsync(Room currentRoom, RoomExitDefinition exit)
    {
        if (!_world.IsPartyLeader(_player.Name))
            return;

        var destinationRoom = _world.GetRoom(exit.TargetMap, exit.TargetRoom);
        if (destinationRoom == null)
            return;

        // Follow loop: gate/move each follower independently; a follower who can't follow
        // (blocked exit, in-combat into a protected room, or unconscious) is dropped from the party while
        // the leader and the followers who DID make the move continue together.
        var followers = _world.GetFollowingPartyMembers(_player.Name)
            .Where(follower =>
                follower.CurrentMapNumber == currentRoom.MapNumber &&
                follower.CurrentRoomNumber == currentRoom.RoomNumber)
            .ToList();

        string followLabel = exit.GetCommandPhrases(_world.Database.Messages).FirstOrDefault() ?? exit.Direction;

        foreach (var follower in followers)
        {
            // Follow loop, HP<1 branch: an unconscious follower cannot be moved — drop them.
            if (follower.IsUnconscious)
            {
                DropFollowerFromLeaderParty(follower, null);
                continue;
            }

            if (!_world.CanTraverseExit(follower, currentRoom, exit, out var failureMessage, out _, isFollowMove: true))
            {
                DropFollowerFromLeaderParty(follower, failureMessage);
                continue;
            }

            var followerClient = _world.GetClientForPlayer(follower.Name);
            if (followerClient == null)
                continue;

            // The entry gate keyed on the FOLLOWER's own state (protected room while in
            // combat / retaliating bars them; the gate sends its own message) → dropped.
            if (!await IsRoomEntryAllowedForAsync(follower, followerClient, destinationRoom, currentRoom.RoomType))
            {
                DropFollowerFromLeaderParty(follower, null);
                continue;
            }

            // The follower's own departing free swing, as on a compass follow (see MoveFollowingPartyMembersAsync).
            if (await new CommandParser(followerClient, _world, follower).TryDepartingMonsterFreeAttackAsync(currentRoom))
            {
                DropFollowerFromLeaderParty(follower, null);
                continue;
            }

            var departingMonsterAttackers = SnapshotDepartingMonsterAttackers(follower, currentRoom.MapNumber, currentRoom.RoomNumber);

            // Each follower sneaks individually: a SNEAKing follower keeps that state through the
            // party move. Moving only breaks HIDE, never SNEAK (mirrors the solo move path).
            bool followerSneaking = follower.IsSneaking;
            if (followerSneaking)
            {
                foreach (var observer in _world.GetPlayersInRoom(currentRoom.MapNumber, currentRoom.RoomNumber, follower))
                {
                    if (ObserverNoticesSneaker(observer, follower))
                        _world.SendToPlayer(observer.Name, GetTextExitDepartureMessage(exit, follower.Name));
                }
            }
            else
            {
                string departMsg = GetTextExitDepartureMessage(exit, follower.Name);
                if (!string.IsNullOrWhiteSpace(departMsg))
                    _world.BroadcastToRoom(currentRoom.MapNumber, currentRoom.RoomNumber, departMsg, followerClient);
            }

            follower.CurrentMapNumber = exit.TargetMap;
            follower.CurrentRoomNumber = exit.TargetRoom;
            follower.RecordMovementTrail(exit.TargetMap, exit.TargetRoom);
            follower.IsResting = false;
            follower.IsMeditating = false;
            follower.IsHidden = false;
            // The "moved this tick" bit: a sneaking move keeps the follower unseen by monsters
            // until the next upkeep clears it (the could-attack gate A).
            follower.SneakedInThisTick = followerSneaking;

            ResolvePostTravelCombatState(follower, currentRoom, exit, departingMonsterAttackers);
            _world.NotifyPlayerEnteredRoom(follower);

            // Exit type 9: the exit's trap fires for EVERY party member that traverses it, each
            // rolling its own damage — not just the leader. The follower exemption (skip the
            // special handler) is present ONLY on case-1 door exits; the trap case (9) has no such guard,
            // so a following member springs the same armed trap independently. Mirror the leader path.
            string? followerTrapMessage = _world.MaybeTriggerTrapOnTraverse(follower, exit);
            bool followerDiedToTrap = followerTrapMessage != null && follower.CurrentHP <= Player.DeathHP;

            follower.SuppressBroadcastReprompt = true;

            if (followerSneaking)
            {
                foreach (var observer in _world.GetPlayersInRoom(follower.CurrentMapNumber, follower.CurrentRoomNumber, follower))
                {
                    if (ObserverNoticesSneaker(observer, follower))
                        _world.SendToPlayer(observer.Name, GetTextExitArrivalMessage(exit, follower.Name));
                }
            }
            else
            {
                string arriveMsg = GetTextExitArrivalMessage(exit, follower.Name);
                if (!string.IsNullOrWhiteSpace(arriveMsg))
                    _world.BroadcastToRoom(follower.CurrentMapNumber, follower.CurrentRoomNumber, arriveMsg, followerClient);
            }

            // This follow render is async output driven by the LEADER's move. If the follower is
            // mid-typing, hold it on their deferred queue (flushed on enter) instead of wiping their
            // input line — matching how broadcasts/combat are held while typing.
            bool followerHolding = followerClient.HasPendingInput;
            if (!followerHolding)
                await followerClient.ClearCurrentLineAsync();
            followerClient.BeginHeldOutput();
            try
            {
                // A sneaking player always sees "Sneaking..." on every move, including a party
                // follow. Without it Megamud (which keys off this line) re-sneaks in every room.
                if (followerSneaking)
                    await followerClient.SendLineAsync($"{MudAnsi.White}Sneaking...{MudAnsi.Reset}");
                await followerClient.SendLineAsync($"{MudAnsi.White} -- Following your Party leader {followLabel} --{MudAnsi.Reset}");
                var followerParser = new CommandParser(followerClient, _world, follower);
                // The trap message (leader-style BrightRed) precedes the room, and a lethal trap routes
                // through the follower's own death handler instead of showing the destination room.
                if (followerTrapMessage != null)
                    await followerClient.SendLineAsync($"{MudAnsi.BrightRed}{followerTrapMessage}{MudAnsi.Reset}");
                if (followerDiedToTrap)
                    await followerParser.HandlePlayerDeath();
                else
                    await followerParser.ShowRoom(brief: follower.BriefMode || follower.FollowModeBlind);
            }
            finally
            {
                followerClient.EndHeldOutput();
            }
            if (!followerHolding)
                await followerClient.SendAsync(MudAnsi.Prompt(follower));

            follower.SuppressBroadcastReprompt = false;

            // A follower the trap killed can no longer follow — drop them from the party (their death
            // handler has already relocated them to the death room).
            if (followerDiedToTrap)
                DropFollowerFromLeaderParty(follower, null);
        }
    }

    private List<MonsterInstance> SnapshotDepartingMonsterAttackers(Player player, int mapNumber, int roomNumber)
    {
        return player.SnapshotIncomingMonsterAttackers()
            .Where(monster => !monster.IsDead && monster.MapNumber == mapNumber && monster.RoomNumber == roomNumber)
            .Distinct()
            .ToList();
    }

    private void ResolvePostTravelCombatState(Player player, Room fromRoom, RoomExitDefinition exit, IReadOnlyList<MonsterInstance> departingMonsterAttackers)
    {
        if (departingMonsterAttackers.Count > 0)
        {
            var followedAttackers = _world.ResolveMonsterPursuit(player, fromRoom, exit, departingMonsterAttackers);

            player.ReplaceIncomingMonsterAttackers(followedAttackers);
            player.CombatTarget = followedAttackers.FirstOrDefault();
        }
        else if (player.CombatTarget != null &&
                 (player.CombatTarget.MapNumber != player.CurrentMapNumber ||
                  player.CombatTarget.RoomNumber != player.CurrentRoomNumber))
        {
            player.CombatTarget = null;
        }

        if (player.PlayerCombatTarget != null &&
            (player.PlayerCombatTarget.CurrentMapNumber != player.CurrentMapNumber ||
             player.PlayerCombatTarget.CurrentRoomNumber != player.CurrentRoomNumber))
        {
            player.PlayerCombatTarget = null;
        }

        // A queued backstab is a from-stealth opener against a SPECIFIC target. Once travel leaves us
        // with no monster combat target (the target didn't pursue), drop the queued action so it can
        // never silently latch onto whatever we fight next — the stock downgrade-to-normal bug
        // family. (When a target DID pursue, CombatTarget is non-null and the backstab can still land
        // on the pursuer.) The beat resolver also guards on a null CombatTarget; this keeps the flag
        // honest regardless of which branch above cleared the target. See CommandParser.Combat.cs
        // PendingCombatRoundAction note (Bug #70).
        if (player.CombatTarget == null && player.PendingCombatRoundAction == PlayerCombatRoundAction.Backstab)
            player.PendingCombatRoundAction = PlayerCombatRoundAction.None;
    }

    private async Task<TriggeredSpellResult> ApplyTriggeredExitSpellEffectsAsync(RoomExitDefinition exit)
    {
        if (exit.ExitType == RoomExitType.Cast)
            return await ExecuteTriggeredSpellByIdAsync(exit.Para2, showRoomAfterTeleport: false,
                TriggeredCastAnnounce.RoomCast);

        if (exit.ExitType == RoomExitType.SpellTrap)
        {
            // A disarmed spell-trap doesn't fire (re-arms after its delay — see RefreshTrapReArm).
            if (_world.IsTrapCurrentlyDisarmed(exit))
                return default;

            await SendTriggeredRoomMessageAsync(exit.Para4);
            return await ExecuteTriggeredSpellByIdAsync(exit.Para1, showRoomAfterTeleport: false,
                TriggeredCastAnnounce.Silent);
        }

        return default;
    }

    // Which entry point cast this spell — and therefore how its result is announced. The two engines
    // format from DIFFERENT CastMsgB lines with different sprintf argument lists, so a call site that
    // picks the wrong one prints the wrong text or shifts every placeholder. There is deliberately NO
    // default: each caller must state which engine it stands in for.
    private enum TriggeredCastAnnounce
    {
        /// The room-cast engine: CastMsgB **Line2**, args ("The room", spell, amount).
        /// Exit type 22 (which is literally a room cast of Para1), and traps.
        RoomCast,

        /// The no-target cast engine: CastMsgB **Line1** to the caster and
        /// **Line3** to the room. The textblock `cast` verb, quest casts, item use, cast-on-ending chains.
        CastSuccess,

        /// The caller emits its own line (spell traps send the exit's own Para4 message instead).
        Silent,
    }

    private async Task<TriggeredSpellResult> ExecuteTriggeredSpellByIdAsync(int spellId, bool showRoomAfterTeleport,
        TriggeredCastAnnounce announce)
    {
        if (spellId <= 0 || !_world.Database.Spells.TryGetValue(spellId, out var spell))
            return default;

        if (TryResolveTriggeredSpellTeleport(spell, out var targetMap, out var targetRoom, out var arrivalMessageId))
        {
            // Casting a spell shows its CastMsgB as it resolves — for a
            // teleport that fires at the ORIGIN room, before ability-140 relocates the caster. This is the
            // systemic counterpart to the player/monster cast paths (which already emit CastMsgB); the
            // triggered-teleport path was the gap, so the whole class teleported silently — jail teleport
            // 584 "Battered and beaten…", duergar 582, sinkhole 714, ship 1089, portals 619-621, fall,
            // tentacles, … The "Starting Message" (ability 120) below is a SEPARATE message; both can fire.
            await EmitTriggeredSpellCastMessageBAsync(spell);

            _player.CurrentMapNumber = targetMap;
            _player.CurrentRoomNumber = targetRoom;
            _player.IsResting = false;
            _player.IsMeditating = false;

            var destinationRoom = _world.GetRoom(targetMap, targetRoom);
            _world.NotifyPlayerEnteredRoom(_player);
            await HandleIndependentPartyTravelCleanupAsync(_player, disbandLeader: true);

            await SendTriggeredRoomMessageAsync(
                arrivalMessageId,
                line1Color: destinationRoom?.Spell == SilverRiverRoomSpellId ? MudAnsi.BrightBlue : null);
            if (spellId == BridgeJumpSpellId)
                await SendTriggeredRoomMessageAsync(SilverRiverEntryMessageId, line1Color: MudAnsi.BrightBlue);
            if (showRoomAfterTeleport)
                await ShowRoom(brief: _player.BriefMode);

            return new TriggeredSpellResult(Teleported: true, StopProcessing: false);
        }

        // Ability 148 (scripted command): the spell carries a text block id whose colon/newline-
        // delimited special commands run on the target. This is
        // the backbone of the ~200 environmental "scripted" spells — chests, boxes, traps, puzzle gates,
        // teleporters — which otherwise fall through and do nothing. The block id is the ability-148 slot value
        // or, when that value is 0, the rolled MinBase..MaxBase magnitude (e.g. pastor box #756, slot 0 /
        // MinBase 9630) — without that fallback a slot-0 scripted spell mis-routed to the damage branch.
        int scriptTextBlockId = ResolveScriptedCommandTextBlock(spell);
        if (scriptTextBlockId > 0)
        {
            int mapBefore = _player.CurrentMapNumber;
            int roomBefore = _player.CurrentRoomNumber;

            if (!await PerformTextBlockAsSpecialCommandAsync(scriptTextBlockId))
                return default;

            bool moved = _player.CurrentMapNumber != mapBefore || _player.CurrentRoomNumber != roomBefore;
            if (moved && showRoomAfterTeleport)
                await ShowRoom(brief: _player.BriefMode);

            return new TriggeredSpellResult(Teleported: moved, StopProcessing: true);
        }

        // Defensive carrier gate (ability 151, no harm): any OTHER triggered context that reaches this damage
        // fall-through with a spell-id POOL (a monster hit-spell / trap that is itself a carrier) must roll
        // a pool member and re-trigger it, not read the pool id as raw damage. Placed AFTER teleport/script
        // so an ability-151 jail-teleport or scripted spell still routes correctly. Item self-use carriers are
        // handled earlier (ApplyItemUseSpellEffectAsync) with full buff/debuff routing; this covers the rest.
        if (TryResolveCarrierSpell(spell, out var triggeredCarrierSub))
        {
            if (triggeredCarrierSub != null && _chainDepth < MaxChainDepth)
            {
                _chainDepth++;
                try { return await ExecuteTriggeredSpellByIdAsync(triggeredCarrierSub.Number, showRoomAfterTeleport, announce); }
                finally { _chainDepth--; }
            }
            return default;
        }

        // Non-damage UTILITY gate. A room cast takes HP only from a harm ability
        // (1/8/17/19/95) — a spell whose body is remove-cast 122 / remove-effect 153 / dispel 73 /
        // cure-status 81 applies that effect and nothing else. Its MinBase is not damage.
        //
        // Surfacing from the Muddy Underwater Passage walks an exit-type-22 (Cast) exit whose Para1 is
        // "exit muddy water" (#681: ability 151 → #682), and "stop mud drown" (#682) carries ability 153 TWICE —
        // terminate "holding breath" (512) and "drowning" (513) — with MinBase/MaxBase 1 and AttType 4
        // as filler. The fall-through below read that filler as damage: it dealt 1 HP, printed the raw
        // internal spell name ("stop mud drown hits you for 1 damage!"), and — worse — never cleared the
        // drowning timers, so the player kept drowning after leaving the water. The monster cast path
        // already gates this exact class (ResolveMonsterAttackSpell / SpellCarriesUtilityAbility); this
        // is the triggered path's missing half. Placed AFTER the carrier gate so a spell that both
        // chains (ability 151) and cures keeps its chain route.
        if (!HasHarmAbility(spell) && SpellCarriesUtilityAbility(spell))
        {
            ApplyImmediateBeneficialEffects(spell, _player);
            return default;
        }

        if (spell.MinBase <= 0 || spell.MaxBase < spell.MinBase || spell.AttType <= 0)
            return default;

        // A room cast takes hit points only through the dispatcher's gate: Targets 0/2/6/8 AND (Duration 0,
        // unless the ability is 17). Outside it stock applies NO instant damage — a Duration>0 spell
        // becomes a timed slot instead. This is what makes "exit muddy water" (#681, Targets 1 /
        // Duration 1) harmless in stock, and it belongs here as well as in the room-spell pulse
        // because exit type 22 is literally a room cast of Para1.
        if (announce == TriggeredCastAnnounce.RoomCast && !GameWorld.IsRoomCastInstantDamageEligible(spell))
        {
            ApplyBuffSpellIfDuration(spell, _player);
            return default;
        }

        int damage = Random.Shared.Next(spell.MinBase, spell.MaxBase + 1);
        damage = Math.Max(1, damage - (_player.MagicResist / 2));
        _player.CurrentHP -= damage;
        _player.RecordDamageSource(spell.Name);   // non-stock death log: a triggered trap-spell names itself

        switch (announce)
        {
            case TriggeredCastAnnounce.CastSuccess:
                await EmitCastSuccessMessagesAsync(spell, damage);
                break;
            case TriggeredCastAnnounce.RoomCast:
                await EmitRoomCastMessageAsync(spell, damage);
                break;
            case TriggeredCastAnnounce.Silent:
                break;
        }

        if (_player.CurrentHP <= 0)
        {
            await HandlePlayerDeath();
            return new TriggeredSpellResult(Teleported: false, StopProcessing: true);
        }

        return default;
    }

    private const int TeleportRoomAbilityId = 140;   // dest room (value, or roll MinBase..MaxBase if 0)
    private const int TeleportMapAbilityId = 141;    // dest map
    private const int ScriptedCommandAbilityId = 148; // run a text block as special commands

    private bool TryResolveTriggeredSpellTeleport(GameSpell spell, out int targetMap, out int targetRoom, out int arrivalMessageId)
    {
        arrivalMessageId = spell.Abilities.GetValueOrDefault(120);
        targetMap = spell.Abilities.GetValueOrDefault(TeleportMapAbilityId);   // ability 141 = dest map
        targetRoom = 0;

        if (targetMap <= 0)
            return false;

        // Teleport effect loop (ability 140 = dest room): the room is the ability's OWN value
        // when nonzero — a fixed destination, e.g. "fall" → room 1123, "upper portal" → room 1291 —
        // otherwise it is rolled over MinBase..MaxBase (per slot: value = slotValue != 0 ? slotValue :
        // a roll over min..max). NB 140=room / 141=map, verified from spell data — an
        // older third-party label had these two backwards.
        int fixedRoom = spell.Abilities.GetValueOrDefault(TeleportRoomAbilityId);
        if (fixedRoom > 0)
        {
            if (_world.GetRoom(targetMap, fixedRoom) == null)
                return false;
            targetRoom = fixedRoom;
            return true;
        }

        if (spell.MinBase <= 0 || spell.MaxBase < spell.MinBase)
            return false;

        var candidateRooms = new List<int>();
        for (int roomNumber = spell.MinBase; roomNumber <= spell.MaxBase; roomNumber++)
        {
            if (_world.GetRoom(targetMap, roomNumber) != null)
                candidateRooms.Add(roomNumber);
        }

        if (candidateRooms.Count == 0)
            return false;

        targetRoom = candidateRooms[Random.Shared.Next(candidateRooms.Count)];
        return true;
    }

    // Emit a triggered spell's CastMsgB (the cast announcement) — Line2 → the
    // caster, Line3 → their current (origin) room. CastMsgB==0 carries no cast message; a CastMsgB that
    // points at a missing message row is the "66" silent sentinel (the dozens of environmental tels whose
    // cast is deliberately quiet), so show nothing. %s resolves to the caster's name.
    // The cast-result messaging EVERY no-target cast effect routes
    // through, and therefore what a textblock `cast <id>` produces. The matched-action `cast` verb
    // runs the ordinary cast engine, with a flag that only
    // skipping the confusion / energy / spell-fail / class-power gates — so a scripted cast is messaged
    // exactly like a player's own cast, NOT by some triggered-spell special case.
    //
    // The dispatcher loads the spell's CastMsgB and prints three lines. The argument
    // list each line is sprintf'd with depends on MsgStyle bit0 — there are TWO whole
    // branches, and picking the wrong one shifts every placeholder by one:
    //
    //   MsgStyle bit0 CLEAR — the "named" style, spell/caster named in the text:
    //     Line1 → CASTER  (spellName, targetName, amount)
    //     Line2 → TARGET  (casterName, spellName, amount)   — skipped when target == caster
    //     Line3 → ROOM    (casterName, spellName, targetName, amount)
    //   MsgStyle bit0 SET — the "anonymous" style; the name arguments are simply NOT passed:
    //     Line1 → CASTER  (targetName, amount)
    //     Line2 → TARGET  (amount)
    //     Line3 → ROOM    (targetName, amount)
    //
    // (Line3's last arg is a damage descriptor, which just sprintf's the number — which is why
    // the stock rows spell it "%s damage" rather than "%d damage".)
    //
    // With no CastMsgB row it falls back to "You cast %s on %s." / "%s cast %s on you!" / "%s cast %s on %s!".
    //
    // There is no other damage announcement: "hits you for" occurs ZERO times in the stock string table.
    // The synthetic "<spell name> hits you for N damage!" line was ours, and it is what leaked the raw
    // internal spell name — "desert damage hits you for 12 damage!" where stock shows spell
    // #712's CastMsgB 2015 Line1, "You suffer in the desert heat... you need water, soon!".
    private async Task EmitCastSuccessMessagesAsync(GameSpell spell, int amount)
    {
        // Line2 (the cross-target line) is unreachable here: a scripted cast has no separate caster —
        // in a no-target cast the caster player IS the target, so caster ==
        // target and the Line2 text is skipped, printing only the caster line and the room broadcast.
        string casterTemplate = "You cast %s on %s.";
        string roomTemplate = "%s cast %s on %s!";

        if (spell.CastMessageB > 0 && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
        {
            casterTemplate = message.Line1 ?? string.Empty;
            roomTemplate = message.Line3 ?? string.Empty;
        }

        // When the spell's Targets is 3 or 9-13 — the area/party types —
        // the target-name argument renders as the EMPTY string. That is why the stock rows read
        // "You cast %s on %sthe room for %d damage!" (#33 swarm) and "on your %sparty" (#152): the %s
        // collapses away. Substituting a real name there would produce "on Bobthe room".
        bool areaTargets = spell.Targets == 3 || (spell.Targets >= 9 && spell.Targets <= 13);
        string targetName = areaTargets ? string.Empty : _player.Name;

        string casterLine = FormatCastSuccessCasterLine(spell, casterTemplate, targetName, amount);
        if (!string.IsNullOrWhiteSpace(casterLine))
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{casterLine}{MudAnsi.Reset}");

        string roomLine = FormatCastSuccessRoomLine(spell, roomTemplate, _player.Name, targetName, amount);
        if (!string.IsNullOrWhiteSpace(roomLine))
        {
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{MudAnsi.BrightRed}{roomLine}{MudAnsi.Reset}", _client);
        }
    }

    // MsgStyle bit0 — the flag both cast-message dispatchers branch on
    // (the no-target cast and the room cast). SET means the
    // template names neither the spell nor the caster, so those arguments are NOT passed to sprintf and
    // the amount lands in the FIRST placeholder. The stock data bears this out: of the room-cast spells
    // (Targets 0/2/6/8) carrying a CastMsgB Line2, the bit-clear rows are overwhelmingly "%s"-bearing
    // ("%s casts a spell on you!") while the bit-set rows are not ("A blast of flame jets out and burns
    // you!", "A flurry of icy blades slice and dice you for %d damage!").
    internal static bool SpellMessageOmitsNames(GameSpell spell) => (spell.MessageStyle & 1) != 0;

    // The three sprintf argument lists, pure so the MsgStyle split is directly testable.
    // Caster line (Line1): bit clear (spellName, targetName, amount) / set (targetName, amount).
    internal static string FormatCastSuccessCasterLine(GameSpell spell, string template, string targetName, int amount)
        => SpellMessageOmitsNames(spell)
            ? FormatLegacyMessage(template, targetName, amount)
            : FormatLegacyMessage(template, spell.Name, targetName, amount);

    // Room line (Line3): bit clear (casterName, spellName, targetName, amount) / set (targetName, amount).
    internal static string FormatCastSuccessRoomLine(GameSpell spell, string template, string casterName, string targetName, int amount)
        => SpellMessageOmitsNames(spell)
            ? FormatLegacyMessage(template, targetName, amount)
            : FormatLegacyMessage(template, casterName, spell.Name, targetName, amount);

    // Room-cast line (CastMsgB Line2): bit clear ("The room", spellName, amount) / set (amount).
    internal const string RoomCastCasterName = "The room";          // stock literal
    internal const string RoomCastFallbackTemplate = "%s cast %s on you for %d damage.";   // stock default
    internal static string FormatRoomCastLine(GameSpell spell, string template, int amount)
        => SpellMessageOmitsNames(spell)
            ? FormatLegacyMessage(template, amount)
            : FormatLegacyMessage(template, RoomCastCasterName, spell.Name, amount);

    // The ROOM-cast announcement, the other half of the messaging story.
    // The room cast (exit type 22, room-spell pulses) never uses the no-target dispatcher;
    // its per-ability dispatcher calls this once per cast instead, latched
    // so a multi-ability spell still speaks a single line.
    //
    // The template is the spell's CastMsgB → **Line2**, NOT Line1 (the call site selects Line2
    // when the message resolved). That is the whole reason the stock data splits the way it does — a
    // room-cast spell like magma heat (#526, Targets 2) carries its player line in Line2 ("You are seared
    // by the flames for %d damage!") while a no-target-cast spell like desert damage (#712, Targets 1)
    // carries its line in Line1 and leaves Line2 empty.
    //
    // Args follow the same MsgStyle bit0 split as the no-target cast, with the caster name fixed to
    // the literal "The room" because a room cast has no player caster:
    //   bit0 clear → ("The room", spellName, amount)
    //   bit0 set   → (amount)
    // With no CastMsgB row the template is the stock default,
    // "%s cast %s on you for %d damage." — which is the authentic default line, and nothing like the
    // "<spell name> hits you for N damage!" we had invented.
    // Finally the spell's ability-115 message Line3 is appended when non-empty.
    private async Task EmitRoomCastMessageAsync(GameSpell spell, int amount)
    {
        string template = RoomCastFallbackTemplate;
        if (spell.CastMessageB > 0 && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
            template = message.Line2 ?? string.Empty;

        string line = FormatRoomCastLine(spell, template, amount);
        if (!string.IsNullOrWhiteSpace(line))
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{line}{MudAnsi.Reset}");

        // The ability-115 message's Line3 rides along after the main line.
        if (spell.Abilities.TryGetValue(RoomCastDescMessageAbilityId, out int descMsgId)
            && descMsgId > 0
            && _world.Database.Messages.TryGetValue(descMsgId, out var descMsg)
            && !string.IsNullOrWhiteSpace(descMsg.Line3))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{descMsg.Line3}{MudAnsi.Reset}");
        }
    }

    private const int RoomCastDescMessageAbilityId = 115;   // per-cast status line

    private async Task EmitTriggeredSpellCastMessageBAsync(GameSpell spell)
    {
        if (spell.CastMessageB <= 0 || !_world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
            return;

        string youLine = (message.Line2 ?? string.Empty).Replace("%s", _player.Name, StringComparison.Ordinal).Trim();
        if (!string.IsNullOrWhiteSpace(youLine))
            await _client.SendLineAsync($"{MudAnsi.White}{youLine}{MudAnsi.Reset}");

        string roomLine = (message.Line3 ?? string.Empty).Replace("%s", _player.Name, StringComparison.Ordinal).Trim();
        if (!string.IsNullOrWhiteSpace(roomLine))
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{MudAnsi.White}{roomLine}{MudAnsi.Reset}", _client);
    }

    private async Task SendTriggeredRoomMessageAsync(int messageId, string? line1Color = null, string? line2Color = null)
    {
        if (messageId <= 0 || !_world.Database.Messages.TryGetValue(messageId, out var message))
            return;

        if (!string.IsNullOrWhiteSpace(message.Line1))
            await _client.SendLineAsync(ApplyTriggeredMessageColor(FormatTriggeredMessageLine(message.Line1), line1Color));

        if (!string.IsNullOrWhiteSpace(message.Line2))
        {
            _world.BroadcastToRoom(
                _player.CurrentMapNumber,
                _player.CurrentRoomNumber,
                ApplyTriggeredMessageColor(FormatTriggeredMessageLine(message.Line2), line2Color),
                _client);
        }
    }

    private static string ApplyTriggeredMessageColor(string line, string? color)
    {
        return string.IsNullOrWhiteSpace(color)
            ? line
            : $"{color}{line}{MudAnsi.Reset}";
    }

    private string FormatTriggeredMessageLine(string line)
    {
        return line.Replace("%s", _player.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDirectionalLookSourceMessage(string direction, string playerName) => direction switch
    {
        "up" => $"{playerName} is looking up.",
        "down" => $"{playerName} is looking down.",
        _ => $"{playerName} is looking to the {direction}.",
    };

    // BASH room broadcast: observers see a distinct line for a landed bash vs a failed attempt,
    //   success: "You see %s bash the %s above you./below you./to the %s."
    //   fail:    "You see %s attempt to bash the %s above you./below you./to the %s."
    // The %s door-noun is "door" (type 7) or "gate" (type 0xb). (The old "%s bashes the %s ... open."
    // wording was invented — bug #193.)
    private static string GetBashExitObserverMessage(RoomExitDefinition exit, string playerName, bool succeeded)
    {
        string verb = succeeded ? "bash" : "attempt to bash";
        string where = exit.Direction.ToLowerInvariant() switch
        {
            "up" => "above you",
            "down" => "below you",
            _ => $"to the {exit.Direction}",
        };
        return $"You see {playerName} {verb} the {exit.DoorNoun} {where}.";
    }

    private static string GetDirectionalLookPeekMessage(string arrivalDirection, string playerName) => arrivalDirection switch
    {
        "above" => $"{playerName} peeks in from above!",
        "below" => $"{playerName} peeks in from below!",
        _ => $"{playerName} peeks in from the {arrivalDirection}!",
    };

    private async Task SendWrappedEntryListAsync(string prefix, IReadOnlyList<string> entries, string separator = ", ", string suffix = "", int width = 79)
    {
        var lines = RoomOutputFormatter.WrapEntryList(prefix, entries, separator, width);
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i];
            if (i == lines.Count - 1 && suffix.Length > 0)
                line += suffix;
            await _client.SendLineAsync(line);
        }
    }

    private async Task SendWrappedNoticeHereAsync(IReadOnlyList<string> entries, int width = 79)
    {
        if (entries.Count == 0)
            return;

        string noticeColor = GameColorPalettes.Resolve(_player.PaletteId).Get(GameColorRole.RoomNotice);

        await SendWrappedEntryListAsync(
            $"{noticeColor}You notice {MudAnsi.Reset}",
            entries,
            $"{MudAnsi.Reset}{noticeColor}, {MudAnsi.Reset}",
            $"{noticeColor} here.{MudAnsi.Reset}",
            width);
    }

    private Player? GetDraggedPlayerForMovement(Room currentRoom)
    {
        if (!_world.TryGetDraggedPlayer(_player, out var draggedPlayer))
            return null;

        if (draggedPlayer.CurrentMapNumber == currentRoom.MapNumber &&
            draggedPlayer.CurrentRoomNumber == currentRoom.RoomNumber &&
            draggedPlayer.IsUnconscious)
        {
            return draggedPlayer;
        }

        _world.StopDraggingForPlayer(draggedPlayer);
        return null;
    }

    private async Task MoveFollowingPartyMembersAsync(Room currentRoom, string direction)
    {
        if (!_world.IsPartyLeader(_player.Name))
            return;

        // Follow loop: iterate EVERY follower in the room (unconscious included — they get
        // dropped, not dragged) and gate/move each independently. A follower who can't follow is dropped
        // from the party; the leader and the followers who DID move continue together.
        var followers = _world.GetFollowingPartyMembers(_player.Name)
            .Where(follower =>
                follower.CurrentMapNumber == currentRoom.MapNumber &&
                follower.CurrentRoomNumber == currentRoom.RoomNumber)
            .ToList();

        foreach (var follower in followers)
        {
            // Follow loop, HP<1 branch: an unconscious follower cannot be moved — drop them.
            if (follower.IsUnconscious)
            {
                DropFollowerFromLeaderParty(follower, null);
                continue;
            }

            var followerExit = _world.FindMovementExit(follower, currentRoom, direction);
            if (followerExit == null)
            {
                DropFollowerFromLeaderParty(follower, "There is no exit in that direction!");
                continue;
            }

            if (!_world.CanTraverseExit(follower, currentRoom, followerExit, out var failureMessage, out _, isFollowMove: true))
            {
                DropFollowerFromLeaderParty(follower, failureMessage);
                continue;
            }

            var followerDestinationRoom = _world.GetRoom(followerExit.TargetMap, followerExit.TargetRoom);
            if (followerDestinationRoom == null)
            {
                DropFollowerFromLeaderParty(follower, "That path leads nowhere...");
                continue;
            }

            var followerClient = _world.GetClientForPlayer(follower.Name);
            if (followerClient == null)
                continue;

            // The entry gate keyed on the FOLLOWER's own state: a follower in combat /
            // retaliating is barred from a protected room (and the gate sends its own message) → dropped.
            if (!await IsRoomEntryAllowedForAsync(follower, followerClient, followerDestinationRoom, currentRoom.RoomType))
            {
                DropFollowerFromLeaderParty(follower, null);
                continue;
            }

            // Each follower goes through the stock mover in its own right, so a follower
            // who isn't sneaking rolls its own departing free swing in the room it is leaving — not only
            // the leader. A swing that kills or holds/stuns it stops it following.
            if (await new CommandParser(followerClient, _world, follower).TryDepartingMonsterFreeAttackAsync(currentRoom))
            {
                DropFollowerFromLeaderParty(follower, null);
                continue;
            }

            string departDir = direction;
            string arriveDir = GetOppositeDirection(direction);
            var departingMonsterAttackers = SnapshotDepartingMonsterAttackers(follower, currentRoom.MapNumber, currentRoom.RoomNumber);

            // Each follower sneaks individually: a SNEAKing follower keeps that state through the
            // party move. Moving only breaks HIDE, never SNEAK (mirrors the solo move path).
            bool followerSneaking = follower.IsSneaking;
            if (followerSneaking)
            {
                // Per-observer sneaking departure: only watchers who beat the stealth roll see it.
                foreach (var observer in _world.GetPlayersInRoom(currentRoom.MapNumber, currentRoom.RoomNumber, follower))
                {
                    if (ObserverNoticesSneaker(observer, follower))
                        _world.SendToPlayer(observer.Name, GetSneakDepartureMessage(follower.Name, departDir));
                }
            }
            else
            {
                _world.BroadcastToRoom(currentRoom.MapNumber, currentRoom.RoomNumber,
                    GameAnsi.RoomMovementDeparture(follower.Name, departDir), followerClient);
            }

            follower.CurrentMapNumber = followerExit.TargetMap;
            follower.CurrentRoomNumber = followerExit.TargetRoom;
            follower.IsResting = false;
            follower.IsMeditating = false;
            follower.IsHidden = false;
            // The "moved this tick" bit: a sneaking move keeps the follower unseen by monsters
            // until the next upkeep clears it (the could-attack gate A).
            follower.SneakedInThisTick = followerSneaking;

            ResolvePostTravelCombatState(follower, currentRoom, followerExit, departingMonsterAttackers);
            _world.NotifyPlayerEnteredRoom(follower);

            // Exit type 9: the exit's trap fires for EVERY party member that traverses it, each
            // rolling its own damage — not just the leader (the follower exemption exists only on case-1
            // door exits, never on the trap case). Spring it on this follower via their own resolved exit.
            string? followerTrapMessage = _world.MaybeTriggerTrapOnTraverse(follower, followerExit);
            bool followerDiedToTrap = followerTrapMessage != null && follower.CurrentHP <= Player.DeathHP;

            // Suppress broadcast reprompts for the follower while being moved
            follower.SuppressBroadcastReprompt = true;

            if (followerSneaking)
            {
                foreach (var observer in _world.GetPlayersInRoom(follower.CurrentMapNumber, follower.CurrentRoomNumber, follower))
                {
                    if (ObserverNoticesSneaker(observer, follower))
                        _world.SendToPlayer(observer.Name, GetSneakArrivalMessage(follower.Name, arriveDir));
                }
            }
            else
            {
                _world.BroadcastToRoom(follower.CurrentMapNumber, follower.CurrentRoomNumber,
                    GameAnsi.RoomMovementArrival(follower.Name, arriveDir), followerClient);
            }

            // Async output driven by the LEADER's move: hold it if the follower is mid-typing (flushed
            // on enter) rather than clearing their input line. See the text-exit follow path above.
            bool followerHolding = followerClient.HasPendingInput;
            // Clear the active prompt instead of pushing it into scrollback (only when not holding).
            if (!followerHolding)
                await followerClient.ClearCurrentLineAsync();
            followerClient.BeginHeldOutput();
            try
            {
                // A sneaking player always sees "Sneaking..." on every move, including a party
                // follow. Without it Megamud (which keys off this line) re-sneaks in every room.
                if (followerSneaking)
                    await followerClient.SendLineAsync($"{MudAnsi.White}Sneaking...{MudAnsi.Reset}");
                await followerClient.SendLineAsync($"{MudAnsi.White} -- Following your Party leader {direction} --{MudAnsi.Reset}");
                var followerParser = new CommandParser(followerClient, _world, follower);
                // The trap message precedes the room; a lethal trap routes through the follower's own
                // death handler instead of showing the destination room.
                if (followerTrapMessage != null)
                    await followerClient.SendLineAsync($"{MudAnsi.BrightRed}{followerTrapMessage}{MudAnsi.Reset}");
                if (followerDiedToTrap)
                    await followerParser.HandlePlayerDeath();
                else
                    await followerParser.ShowRoom(brief: follower.BriefMode || follower.FollowModeBlind);
            }
            finally
            {
                followerClient.EndHeldOutput();
            }
            if (!followerHolding)
                await followerClient.SendAsync(MudAnsi.Prompt(follower));

            // A follower the trap killed can no longer follow — drop them (their death handler already
            // relocated them to the death room).
            if (followerDiedToTrap)
                DropFollowerFromLeaderParty(follower, null);

            follower.SuppressBroadcastReprompt = false;
        }
    }

    private async Task MoveDraggedPlayerAsync(Room currentRoom, string direction, Player? draggedPlayer)
    {
        if (draggedPlayer == null)
            return;

        if (draggedPlayer.CurrentMapNumber != currentRoom.MapNumber ||
            draggedPlayer.CurrentRoomNumber != currentRoom.RoomNumber)
        {
            _world.StopDraggingForPlayer(draggedPlayer);
            return;
        }

        if (!draggedPlayer.IsUnconscious)
        {
            _world.StopDraggingForPlayer(draggedPlayer);
            return;
        }

        var draggedClient = _world.GetClientForPlayer(draggedPlayer.Name);
        if (draggedClient == null)
        {
            _world.StopDraggingForPlayer(draggedPlayer);
            return;
        }

        var destinationRoom = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (destinationRoom == null)
        {
            _world.StopDraggingForPlayer(draggedPlayer);
            return;
        }

        draggedPlayer.CurrentMapNumber = _player.CurrentMapNumber;
        draggedPlayer.CurrentRoomNumber = _player.CurrentRoomNumber;
        draggedPlayer.IsResting = false;
        draggedPlayer.IsMeditating = false;
        draggedPlayer.IsHidden = false;
        if (!draggedPlayer.HasPerfectStealth)
            draggedPlayer.IsSneaking = false;

        _world.NotifyPlayerEnteredRoom(draggedPlayer);

        draggedPlayer.SuppressBroadcastReprompt = true;
        draggedClient.BeginBuffering();
        try
        {
            await draggedClient.ClearCurrentLineAsync();
            await draggedClient.SendLineAsync(GameAnsi.DragNotice($"{_player.Name} is dragging you around."));
            var draggedParser = new CommandParser(draggedClient, _world, draggedPlayer);
            await draggedParser.ShowRoom(brief: draggedPlayer.BriefMode || draggedPlayer.FollowModeBlind);
            await draggedClient.SendAsync(MudAnsi.Prompt(draggedPlayer));
            await _client.SendLineAsync(GameAnsi.DragNotice($"You are dragging {draggedPlayer.Name}."));
        }
        finally
        {
            draggedClient.FlushOutput();
            draggedPlayer.SuppressBroadcastReprompt = false;
        }
    }

    // On a sneaking move the engine re-rolls the per-move
    // stealth check (our CalculateStealthChance). On a FAILED roll the
    // sneak flag clears IMMEDIATELY — sneak is dead from here on (you must re-issue SNEAK) — and
    // only THEN, if the mover is perceptive enough (a roll of 0..99 < Perception), warns them in dark red
    // that they slipped: "You make a sound as you enter the room!" The clear precedes the warning, so
    // the flag is already off by the time the line prints. Perfect-stealth (ability 186)
    // movers auto-hold and skip the whole check. THIS move still renders stealthily via the caller's
    // pre-roll snapshot (the snapshot stays set for the remainder of the move).
    private async Task ResolveSneakMoveBreakAsync(Room currentRoom)
    {
        if (_player.HasPerfectStealth)
            return;

        int chance = Player.CalculateStealthChance(
            _player.Stealth,
            GetMovementEncumbrancePercent(),
            _world.GetPlayersInRoom(currentRoom.MapNumber, currentRoom.RoomNumber, _player).Count(),
            _world.GetMonstersInRoom(currentRoom.MapNumber, currentRoom.RoomNumber).Count(monster => !monster.IsDead),
            _player.RecentlySpotted);

        // Roll 0..100 (top exclusive): sneak holds when roll < chance; a roll >= chance breaks it.
        if (Random.Shared.Next(0, 101) < chance)
            return;

        // Roll failed — sneak ends now (the flag clears before the warning prints).
        _player.IsSneaking = false;

        // genrdn(0,100) < Perception: the more perceptive mover notices their own noise.
        if (Random.Shared.Next(0, 100) < _player.Perception)
            await _client.SendLineAsync($"{MudAnsi.Red}You make a sound as you enter the room!{MudAnsi.Reset}");
    }

    // ── Per-observer sneak visibility, faithful to stock ─────────────────────
    // Monster ability 57 = See Hidden → boolean detect.
    // An observer with See Hidden always sees a sneaker.
    // Movement display: per-observer perception vs stealth.
    // Stock uses a perception threshold of 40 for detection display checks.

    private static bool ObserverNoticesSneaker(Player observer, Player sneaker)
    {
        if (observer.HasSeeHidden) return true;
        // Observer's perception must meaningfully exceed sneaker's stealth.
        // Low perception (~18) vs decent stealth (~25) should almost never detect.
        int chance = Math.Clamp(observer.Perception - sneaker.Stealth, 5, 75);
        return Random.Shared.Next(100) < chance;
    }

    // Stock: "You notice %s sneaking out upwards%s." / "downwards%s." / "to the %s%s."
    private static string GetSneakDepartureMessage(string playerName, string direction) => direction switch
    {
        "up" => $"{MudAnsi.Yellow}You notice {playerName} sneaking out upwards.{MudAnsi.Reset}",
        "down" => $"{MudAnsi.Yellow}You notice {playerName} sneaking out downwards.{MudAnsi.Reset}",
        _ => $"{MudAnsi.Yellow}You notice {playerName} sneaking out to the {direction}.{MudAnsi.Reset}",
    };

    // Stock: "You notice %s sneak in from above%s." / "below%s." / "from the %s%s."
    private static string GetSneakArrivalMessage(string playerName, string arrivalDirection) => arrivalDirection switch
    {
        "above" => $"{MudAnsi.Yellow}You notice {playerName} sneak in from above.{MudAnsi.Reset}",
        "below" => $"{MudAnsi.Yellow}You notice {playerName} sneak in from below.{MudAnsi.Reset}",
        _ => $"{MudAnsi.Yellow}You notice {playerName} sneak in from the {arrivalDirection}.{MudAnsi.Reset}",
    };

}
