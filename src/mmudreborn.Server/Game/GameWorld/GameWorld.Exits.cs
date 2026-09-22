using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

// Outcome of a bash attempt at a door/gate exit, so the caller can render the correct stock response:
// a real ATTEMPT (Bashed/Failed) prints its self-line, broadcasts a "You see %s [attempt to] bash …"
// observer line, and eats the action delay; AlreadyOpen/NotBashable are pre-roll rejections that do none
// of that (they only print their own line).
public enum BashExitOutcome
{
    NotBashable,
    AlreadyOpen,
    Failed,
    Bashed,
}

public partial class GameWorld
{
    public static string DirectionFromIndex(int index)
    {
        if (index < 0 || index >= ExitDirections.Length)
            return "";

        return ExitDirections[index];
    }

    public string NormalizeDirection(string direction)
    {
        if (DirectionAliases.TryGetValue(direction.Trim(), out var normalized))
            return normalized;

        return direction.Trim().ToLowerInvariant();
    }

    public RoomExitDefinition? FindMovementExit(Player player, Room room, string direction)
    {
        var normalizedDirection = NormalizeDirection(direction);
        var exit = room.GetExit(normalizedDirection);
        if (exit == null || !exit.HasDestination)
            return null;

        if (exit.IsRemoteAction || exit.IsTextCommandExit)
            return null;

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (exit.IsHiddenExit && !state.IsRevealed && !exit.IsPassableHiddenExit)
            return null;

        return exit;
    }

    private RoomExitState GetExitState(RoomExitDefinition exit)
    {
        return _roomExitStates.GetOrAdd((exit.MapNumber, exit.RoomNumber, exit.Direction), _ => new RoomExitState
        {
            IsRevealed = exit.StartsVisible,
            Access = GetAccessState(exit),
            HiddenActionBits = exit.IsHiddenExit ? exit.Para1 : 0,
            TrapDisarmed = !exit.TrapStartsArmed,
        });
    }

    private ExitAccessState GetAccessState(RoomExitDefinition exit)
    {
        string accessKey = GetExitAccessKey(exit);
        return _roomExitAccessStates.GetOrAdd(accessKey, _ =>
        {
            bool startsLocked = StartsLockedByDefault(exit);
            return new ExitAccessState
            {
                IsUnlocked = !exit.IsBarrierExit || !startsLocked,
                IsOpen = false,
            };
        });
    }

    private string GetExitAccessKey(RoomExitDefinition exit)
    {
        string sideKey = $"{exit.MapNumber}:{exit.RoomNumber}:{exit.Direction}";
        var reverseExit = FindReverseExit(exit);
        if (reverseExit == null)
            return sideKey;

        string reverseKey = $"{reverseExit.MapNumber}:{reverseExit.RoomNumber}:{reverseExit.Direction}";
        return string.CompareOrdinal(sideKey, reverseKey) <= 0
            ? $"{sideKey}|{reverseKey}"
            : $"{reverseKey}|{sideKey}";
    }

    private RoomExitDefinition? FindReverseExit(RoomExitDefinition exit)
    {
        var targetRoom = GetRoom(exit.TargetMap, exit.TargetRoom);
        if (targetRoom == null)
            return null;

        string expectedDirection = GetOppositeDirection(exit.Direction);
        var candidates = targetRoom.GetExitDefinitions().Values
            .Where(candidate => candidate.HasDestination
                && candidate.TargetMap == exit.MapNumber
                && candidate.TargetRoom == exit.RoomNumber)
            .ToList();

        return candidates.FirstOrDefault(candidate => candidate.Direction.Equals(expectedDirection, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault();
    }

    private bool StartsLockedByDefault(RoomExitDefinition exit)
    {
        var reverseExit = FindReverseExit(exit);
        return exit.StartsLocked || reverseExit?.StartsLocked == true;
    }

    private bool RefreshTimedExitState(RoomExitDefinition exit, RoomExitState state)
    {
        return RefreshTimedExitState(exit, state, DateTime.UtcNow);
    }

    private bool RefreshTimedExitState(RoomExitDefinition exit, RoomExitState state, DateTime now)
    {
        if (!state.Access.AccessUntilUtc.HasValue || state.Access.AccessUntilUtc.Value > now)
            return false;

        state.Access.IsOpen = false;
        state.Access.AccessUntilUtc = null;
        state.Access.IsUnlocked = !exit.IsBarrierExit || !StartsLockedByDefault(exit);
        SyncLinkedVisibility(exit, state.Access.IsOpen);
        return true;
    }

    private static DateTime? GetTimedAccessExpiry(RoomExitDefinition exit)
    {
        // OPEN / PICKLOCK / item-use floor a door/gate's open-duration to a minimum of
        // one block when it is opened (`iVar = (field < 2) ? 1 : field`), then schedule the relock via
        // then schedule the relock. It fires 300 × that value fast-ticks later, and
        // the fast pass runs every real-time second — i.e. 300 s = 5 min per block.
        // So EVERY opened barrier relocks after ≥5 min; a Para3 of 0 means 5 min, NOT "open forever".
        int blocks = exit.IsBarrierExit ? Math.Max(1, exit.OpenDurationBlocks) : exit.OpenDurationBlocks;
        return blocks > 0
            ? DateTime.UtcNow.AddMinutes(blocks * 5)
            : null;
    }

    private static void StartTimedAccess(RoomExitDefinition exit, RoomExitState state)
    {
        if (!state.Access.AccessUntilUtc.HasValue)
            state.Access.AccessUntilUtc = GetTimedAccessExpiry(exit);
    }

    // A successfully disarmed trap (exit state 0→1 / 3→4) schedules
    // a one-block timer. The exit update for types 9/24 then re-arms it
    // (state 1→0) when the timer fires, with NO room broadcast. One block = 300
    // fast-ticks = 5 minutes (the same per-block cadence as door relock).
    private static readonly TimeSpan TrapReArmDelay = TimeSpan.FromMinutes(5);

    // Silently re-arm a disarmed trap once its scheduled delay elapses. Returns true if it re-armed.
    private static bool RefreshTrapReArm(RoomExitState state, DateTime now)
    {
        if (!state.TrapDisarmed || !state.TrapReArmAtUtc.HasValue || state.TrapReArmAtUtc.Value > now)
            return false;

        state.TrapDisarmed = false;
        state.TrapReArmAtUtc = null;
        return true;
    }

    private void BroadcastTimedExitRelock(RoomExitDefinition exit)
    {
        string message = $"The {exit.DoorNoun} to the {exit.Direction} just locked!";
        BroadcastToRoom(exit.MapNumber, exit.RoomNumber, message);

        var reverseExit = FindReverseExit(exit);
        if (reverseExit == null)
            return;

        if (reverseExit.MapNumber == exit.MapNumber && reverseExit.RoomNumber == exit.RoomNumber)
            return;

        string reverseMessage = $"The {reverseExit.DoorNoun} to the {reverseExit.Direction} just locked!";
        BroadcastToRoom(reverseExit.MapNumber, reverseExit.RoomNumber, reverseMessage);
    }

    private void SyncLinkedVisibility(RoomExitDefinition exit, bool isOpen)
    {
        var reverseExit = FindReverseExit(exit);
        if (reverseExit == null)
            return;

        if (reverseExit.IsHiddenExit)
        {
            var reverseState = GetExitState(reverseExit);
            reverseState.IsRevealed = isOpen;
        }
    }

    private void SyncExitStateAfterChange(RoomExitDefinition exit, RoomExitState state)
    {
        SyncLinkedVisibility(exit, state.Access.IsOpen);
    }

    // BASH:     a roll of 0..99 < (Strength + bashDifficulty)
    // PICKLOCK: a roll of 0..99 < (Picklocks + pickDifficulty)
    // The difficulty is a SIGNED ADDITIVE term, NOT a yes/no gate: a positive value makes the lock
    // EASIER (it adds to the stat), a negative value makes it HARDER, and 0 means a pure stat-vs-roll.
    // We previously short-circuited `difficulty >= 0 => always succeed`, which made every easy/medium
    // door (Para>=0) auto-open regardless of skill/strength and only ever rolled the negative ones —
    // collapsing the stock difficulty gradient. Now roll the same single formula for all values.
    private bool PassesExitSkillCheck(int statValue, int rawDifficulty)
    {
        int chance = Math.Max(0, statValue) + rawDifficulty;
        if (chance <= 0)
            return false;

        // genrdn(0,100) is exclusive on the max (a + rand()%(b-a)) → it rolls 0..99, never 100. Model it
        // as Next(0,100), NOT Next(0,101): the latter can roll 100, which makes a guaranteed (chance>=100)
        // pick/bash spuriously fail ~1% of the time. See the (0,(max-min)+1) / (dmg/2,dmg+1) roll
        // idioms throughout stock, which only round-trip with an exclusive max.
        return _rng.Next(0, 100) < chance;
    }

    public IReadOnlyDictionary<string, RoomExitDefinition> GetVisibleExits(Player player, Room room)
    {
        var visible = new Dictionary<string, RoomExitDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var exit in room.GetExitDefinitions().Values.OrderBy(def => def.DirectionIndex))
        {
            if (!exit.HasDestination)
                continue;

            if (exit.IsRemoteAction || exit.IsTextCommandExit)
                continue;

            var state = GetExitState(exit);
            RefreshTimedExitState(exit, state);

            if (exit.IsHiddenExit && !state.IsRevealed)
                continue;

            visible[exit.Direction] = exit;
        }

        return visible;
    }

    // Stock default labels for revealed hidden exits with no custom message.
    // Stock carries per-direction strings; we reproduce them exactly.
    private static readonly Dictionary<string, string> DefaultSecretExitLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = "Secret passage north",
        ["south"] = "secret passage south",
        ["east"] = "secret passage East",
        ["west"] = "secret passage west",
        ["northeast"] = "secret passage northeast",
        ["northwest"] = "secret passage northwest",
        ["southeast"] = "secret passage southeast",
        ["southwest"] = "secret passage southwest",
        ["up"] = "secret passage above",
        ["down"] = "secret passage below",
    };

    public string GetVisibleExitLabel(Player player, Room room, RoomExitDefinition exit)
    {
        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (exit.IsBarrierExit)
            return $"{(state.Access.IsOpen ? "open" : "closed")} {exit.DoorNoun} {exit.Direction}";

        if (exit.IsHiddenExit && state.IsRevealed)
        {
            if (exit.Para4 > 0 && Database.Messages.TryGetValue(exit.Para4, out var lookMessage))
            {
                var template = lookMessage.Line1;
                if (!string.IsNullOrWhiteSpace(template))
                    return FormatMessageTemplate(template, exit.Direction);
            }

            if (DefaultSecretExitLabels.TryGetValue(exit.Direction, out var defaultLabel))
                return defaultLabel;
        }

        return exit.Direction;
    }

    public string GetVisibleExitString(Player player, Room room)
    {
        var exits = GetVisibleExits(player, room);
        if (exits.Count == 0)
            return "none";

        return string.Join(", ", exits.Values.Select(exit => GetVisibleExitLabel(player, room, exit)));
    }

    public RoomExitDefinition? FindVisibleExit(Player player, Room room, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
            return null;

        var visibleExits = GetVisibleExits(player, room);
        var lowered = selector.Trim().ToLowerInvariant();
        var normalizedDirection = NormalizeDirection(lowered);

        if (visibleExits.TryGetValue(normalizedDirection, out var directExit))
            return directExit;

        foreach (var exit in visibleExits.Values)
        {
            var label = GetVisibleExitLabel(player, room, exit).ToLowerInvariant();
            if (label == lowered || label.Contains(lowered) || lowered.Contains(label))
                return exit;
        }

        return null;
    }

    public bool TryOpenExit(Player player, Room room, string selector, out string message,
        out RoomExitDefinition? openedExit, out bool doorPresent)
    {
        doorPresent = false;
        openedExit = null;
        var exit = FindVisibleExit(player, room, selector);
        if (exit == null || !exit.IsBarrierExit)
        {
            message = "You don't see anything like that to open.";
            return false;
        }

        // A real door is here — stock clears sneak/hide from this point on, even if it turns out to be
        // already open or locked (the open is ATTEMPTED on a real door). Only "no door that way" is free.
        doorPresent = true;

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);
        if (state.Access.IsOpen)
        {
            // Informational, not an error: OPEN prints "The door was already open."
            // / "The %s was already open." in the default text color (White
            // on palettes 0/1) with NO bright-red error prefix — unlike "is closed", which stock
            // prepends with the red escape. So the message carries its own White; the bash
            // call site must not re-wrap it in error red.
            // Past tense is OPEN's wording. "The %s is already open." is a DIFFERENT
            // string that belongs to BASH alone — see TryBashExit below.
            message = $"{MudAnsi.White}The {exit.DoorNoun} was already open.{MudAnsi.Reset}";
            return false;
        }

        if (!state.Access.IsUnlocked)
        {
            message = $"The {exit.DoorNoun} is locked.";
            return false;
        }

        state.Access.IsOpen = true;
        StartTimedAccess(exit, state);
        SyncExitStateAfterChange(exit, state);
        openedExit = exit;
        // OPEN success line: "The door is now open." / "The %s is now open."
        // The actor is NOT the subject — "You open the ..." appears nowhere in stock.
        message = $"The {exit.DoorNoun} is now open.";
        return true;
    }

    public bool TryUnlockExit(Player player, Room room, string selector, out string message)
    {
        var exit = FindVisibleExit(player, room, selector);
        if (exit == null || !exit.IsBarrierExit)
        {
            message = "You don't see anything like that to unlock.";
            return false;
        }

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (state.Access.IsUnlocked)
        {
            message = $"The {exit.DoorNoun} was not locked.";
            return false;
        }

        if (exit.RequiredKeyItemId <= 0 || !PlayerHasItem(player, exit.RequiredKeyItemId))
        {
            message = "You don't have the right key.";
            return false;
        }

        state.Access.IsUnlocked = true;
        StartTimedAccess(exit, state);
        // A key unlock and a picklock share ONE success line in stock: the item-use path
        // prints "You successfully unlocked the door." / "You successfully unlocked the
        // %s.", the same wording PICKLOCK uses. "You unlock the ..."
        // is not a stock string.
        message = $"You successfully unlocked the {exit.DoorNoun}.";
        return true;
    }

    public bool TryLockExit(Player player, Room room, string selector, out string message, out bool doorPresent)
    {
        doorPresent = false;
        var exit = FindVisibleExit(player, room, selector);
        if (exit == null || !exit.IsBarrierExit)
        {
            message = "There is no benefit to locking in that direction.";
            return false;
        }

        doorPresent = true;

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (state.Access.IsOpen)
        {
            message = $"You must close the {exit.DoorNoun} before you may lock it.";
            return false;
        }

        if (!state.Access.IsUnlocked)
        {
            message = $"The {exit.DoorNoun} is already locked.";
            return false;
        }

        state.Access.IsUnlocked = false;
        state.Access.AccessUntilUtc = null;
        SyncExitStateAfterChange(exit, state);
        message = $"The {exit.DoorNoun} is now locked.";
        return true;
    }

    public bool TryPicklockExit(Player player, Room room, string selector, out string message, out bool doorPresent)
    {
        doorPresent = false;
        var exit = FindVisibleExit(player, room, selector);
        if (exit == null || !exit.IsBarrierExit)
        {
            message = "Your skill fails you this time.";
            return false;
        }

        doorPresent = true;
        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (state.Access.IsUnlocked)
        {
            message = $"The {exit.DoorNoun} was not locked.";
            return false;
        }

        if (!exit.CanPicklock)
        {
            message = "Your skill fails you this time.";
            return false;
        }

        if (!PassesExitSkillCheck(player.Picklocks, exit.PickDifficultyRaw))
        {
            message = "Your skill fails you this time.";
            return false;
        }

        state.Access.IsUnlocked = true;
        StartTimedAccess(exit, state);
        message = $"You successfully unlocked the {exit.DoorNoun}.";
        return true;
    }

    public BashExitOutcome TryBashExit(Player player, Room room, string selector, out string message)
    {
        var exit = FindVisibleExit(player, room, selector);
        if (exit == null || !exit.IsBarrierExit)
        {
            message = "Your command had no effect.";
            return BashExitOutcome.NotBashable;
        }

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (state.Access.IsOpen)
        {
            // Informational, not an error: stock prints "The %s is already open." in the default
            // text color (White on palettes 0/1) with NO bright-red error prefix — unlike "is
            // closed"/"is locked", which stock prepends with the red escape. So the
            // message carries its own White; the bash call site must not re-wrap it in error red.
            message = $"{MudAnsi.White}The {exit.DoorNoun} is already open.{MudAnsi.Reset}";
            return BashExitOutcome.AlreadyOpen;
        }

        if (!exit.CanBash)
        {
            message = $"You can't bash the {exit.DoorNoun} open.";
            return BashExitOutcome.NotBashable;
        }

        if (!PassesExitSkillCheck(player.Strength, exit.BashDifficultyRaw))
        {
            // A failed bash ROLL prints the fixed string "Your attempts to
            // bash through fail!" — NOT a door-noun/direction sentence. Bug #193: we had
            // invented "You fail to bash open the <door>.", which no stock client ever sends.
            message = "Your attempts to bash through fail!";
            return BashExitOutcome.Failed;
        }

        state.Access.IsUnlocked = true;
        state.Access.IsOpen = true;
        StartTimedAccess(exit, state);
        SyncExitStateAfterChange(exit, state);
        message = $"You bashed the {exit.DoorNoun} open.";
        return BashExitOutcome.Bashed;
    }

    public bool TryDisarmExit(Player player, Room room, string selector, out string message, out RoomExitDefinition? triggeredSpellTrap)
    {
        triggeredSpellTrap = null;
        message = string.Empty;

        // DISARM parses the direction first; a token that is not one of the ten
        // compass/vertical directions never enters the trap logic and produces no output.
        if (!MudDirections.Aliases.TryGetValue(selector.Trim(), out var directionName))
            return false;

        // Stock never reveals whether a trap is actually there: a missing exit, a non-trap exit, or an
        // already-disarmed trap all report the SAME failed disarm to the parsed direction (BrightRed,
        // rather than leaking the trap's presence.
        string failedMessage = $"{MudAnsi.BrightRed}You failed to disarm any trap to the {directionName}.{MudAnsi.Reset}";

        // Look up the trap at the EXACT parsed direction -- stock indexes the exit slot by the
        // direction index, so "disarm trap north" must not fuzzy-match a northwest trap the way
        // FindVisibleExit's label fallback would.
        GetVisibleExits(player, room).TryGetValue(directionName, out var exit);
        if (exit == null || !exit.IsDisarmableTrapExit)
        {
            message = failedMessage;
            return false;
        }

        var state = GetExitState(exit);
        RefreshTrapReArm(state, DateTime.UtcNow);
        if (state.TrapDisarmed)
        {
            message = failedMessage;
            return false;
        }

        int roll = _rng.Next(0, 100);   // DISARM: a roll of 0..99 < DisarmTraps (the max is exclusive)
        if (roll < player.DisarmTraps)
        {
            state.TrapDisarmed = true;
            // Stock schedules the silent re-arm — 5 min later. Both type 9 and
            // type 24 re-arm identically.
            state.TrapReArmAtUtc = DateTime.UtcNow + TrapReArmDelay;
            message = $"You successfully disarmed the trap to the {exit.Direction}.";
            return true;
        }

        if (roll - 10 < player.DisarmTraps)
        {
            message = failedMessage;
            return false;
        }

        // Botched the disarm — the trap triggers on you. A spell-trap (type 24) casts its spell (the
        // caller fires it via the async pipeline); a regular trap (type 9) deals its damage here.
        if (exit.IsSpellTrapExit)
        {
            triggeredSpellTrap = exit;
            message = $"You set off the trap to the {exit.Direction}!";
            return false;
        }

        int damage = ApplyTrapDamage(player, exit);
        message = damage > 0
            ? $"You set off the trap to the {exit.Direction}! You take {damage} damage!"
            : $"You set off the trap to the {exit.Direction}!";
        return false;
    }

    public string? MaybeTriggerTrapOnTraverse(Player player, RoomExitDefinition exit)
    {
        if (!exit.IsTrapExit)
            return null;

        var state = GetExitState(exit);
        RefreshTrapReArm(state, DateTime.UtcNow);
        if (state.TrapDisarmed)
            return null;

        int damage = ApplyTrapDamage(player, exit);
        return damage > 0 ? $"You spring a trap and take {damage} damage!" : null;
    }

    // True when the (spell-)trap on this exit is currently disarmed (re-arm timer refreshed first).
    // The spell-trap (type 24) traverse path queries this to skip casting; type-9 traps gate inside
    // MaybeTriggerTrapOnTraverse.
    public bool IsTrapCurrentlyDisarmed(RoomExitDefinition exit)
    {
        if (!exit.IsDisarmableTrapExit)
            return false;

        var state = GetExitState(exit);
        RefreshTrapReArm(state, DateTime.UtcNow);
        return state.TrapDisarmed;
    }

    private int ApplyTrapDamage(Player player, RoomExitDefinition exit)
    {
        int damage = exit.TrapDamage;
        if (damage <= 0)
            return 0;

        int rolledDamage = _rng.Next(damage / 2, damage + 1);
        player.CurrentHP -= rolledDamage;
        player.RecordDamageSource("a trap");   // non-stock death log; nothing else takes credit for a trap
        return rolledDamage;
    }

    // The UTC instant a currently-open barrier is scheduled to auto-relock, or null if no relock is
    // pending. Used by tests to assert that opening a door/gate scheduled its relock.
    public DateTime? GetScheduledRelockUtcForTests(int mapNumber, int roomNumber, string direction)
    {
        var exit = GetRoom(mapNumber, roomNumber)?.GetExit(direction);
        return exit == null ? null : GetExitState(exit).Access.AccessUntilUtc;
    }

    public void ExpireExitAccessForTests(int mapNumber, int roomNumber, string direction)
    {
        var room = GetRoom(mapNumber, roomNumber)
            ?? throw new InvalidOperationException($"Room {mapNumber}/{roomNumber} was not found.");

        var exit = room.GetExit(direction)
            ?? throw new InvalidOperationException($"Exit '{direction}' was not found for room {mapNumber}/{roomNumber}.");

        var state = GetExitState(exit);
        state.Access.AccessUntilUtc = DateTime.UtcNow.AddSeconds(-1);
    }

    public void ExpireTrapReArmForTests(int mapNumber, int roomNumber, string direction)
    {
        var room = GetRoom(mapNumber, roomNumber)
            ?? throw new InvalidOperationException($"Room {mapNumber}/{roomNumber} was not found.");

        var exit = room.GetExit(direction)
            ?? throw new InvalidOperationException($"Exit '{direction}' was not found for room {mapNumber}/{roomNumber}.");

        var state = GetExitState(exit);
        if (state.TrapReArmAtUtc.HasValue)
            state.TrapReArmAtUtc = DateTime.UtcNow.AddSeconds(-1);
    }

    public void ExpireHiddenExitRehideForTests(int mapNumber, int roomNumber, string direction)
    {
        var room = GetRoom(mapNumber, roomNumber)
            ?? throw new InvalidOperationException($"Room {mapNumber}/{roomNumber} was not found.");

        var exit = room.GetExit(direction)
            ?? throw new InvalidOperationException($"Exit '{direction}' was not found for room {mapNumber}/{roomNumber}.");

        var state = GetExitState(exit);
        if (state.RehideAtUtc.HasValue)
            state.RehideAtUtc = DateTime.UtcNow.AddSeconds(-1);
    }

    public int SetExitTypeForTests(int mapNumber, int roomNumber, string direction, int exitType)
    {
        var room = GetRoom(mapNumber, roomNumber)
            ?? throw new InvalidOperationException($"Room {mapNumber}/{roomNumber} was not found.");

        var exit = room.GetExit(NormalizeDirection(direction))
            ?? throw new InvalidOperationException($"Exit '{direction}' was not found for room {mapNumber}/{roomNumber}.");

        int previous = (int)exit.ExitType;
        exit.ExitType = (RoomExitType)exitType;
        return previous;
    }

    public bool IsHiddenExitRevealedForTests(int mapNumber, int roomNumber, string direction)
    {
        var room = GetRoom(mapNumber, roomNumber)
            ?? throw new InvalidOperationException($"Room {mapNumber}/{roomNumber} was not found.");

        var exit = room.GetExit(direction)
            ?? throw new InvalidOperationException($"Exit '{direction}' was not found for room {mapNumber}/{roomNumber}.");

        return GetExitState(exit).IsRevealed;
    }

    public enum CloseExitOutcome
    {
        Closed,
        NotADoor,
        NotOpen,
    }

    /// <summary>The paired exit on the far side of a door, so callers can address that room.</summary>
    public RoomExitDefinition? GetReverseExit(RoomExitDefinition exit) => FindReverseExit(exit);

    /// <summary>
    /// CLOSE, once the combat gate has passed and the argument has resolved to a
    /// direction index. The door/gate branch (types 7/11) and the keyed branch (type 2) print the
    /// same four sentences — the type-2 branch just hardcodes "door" where the other substitutes the
    /// noun — so DoorNoun collapses them into one implementation:
    ///     user      "The %s is now closed."
    ///     near room "You see %s close the %s to the %s."
    ///     far room  "The %s to the %s just closed."
    ///     not open  "That %s is not open. Closing it will do nothing!"
    /// `exitPresent` reports whether ANY exit exists in that direction, which is what gates the
    /// sneak/hide clear: stock zeroes both flags as soon as the direction has an exit,
    /// BEFORE testing the exit type — so closing at a plain passage still gives you away, while
    /// closing at a direction with no exit at all is free.
    /// Closing needs no far-side write here: paired exits share one ExitAccessState via
    /// GetExitAccessKey, so the stock second store into the far room's door array is already implied.
    /// </summary>
    public CloseExitOutcome TryCloseExit(Player player, Room room, string directionName, out string message, out RoomExitDefinition? exit, out bool exitPresent)
    {
        GetVisibleExits(player, room).TryGetValue(directionName, out exit);
        exitPresent = exit != null;

        if (exit == null || !exit.IsBarrierExit)
        {
            message = "That is not a door or a gate!";
            return CloseExitOutcome.NotADoor;
        }

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);
        if (!state.Access.IsOpen)
        {
            message = $"That {exit.DoorNoun} is not open. Closing it will do nothing!";
            return CloseExitOutcome.NotOpen;
        }

        state.Access.IsOpen = false;
        SyncExitStateAfterChange(exit, state);
        message = $"The {exit.DoorNoun} is now closed.";
        return CloseExitOutcome.Closed;
    }

    public bool CanLookThroughExit(Player player, Room room, RoomExitDefinition exit, out string? failureMessage)
    {
        failureMessage = null;

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (exit.IsHiddenExit && !state.IsRevealed)
        {
            failureMessage = "You don't see anything special that way.";
            return false;
        }

        // A closed Key/Door blocks the peek, but a Gate is transparent (not in the
        // switch — see BlocksLookThroughWhenClosed). So a closed jail-cell gate still lets you see the room
        // beyond; a closed door does not.
        if (exit.BlocksLookThroughWhenClosed && !state.Access.IsOpen)
        {
            failureMessage = $"The {exit.DoorNoun} is closed in that direction!";
            return false;
        }

        return true;
    }

    public bool CanTraverseExit(Player player, Room room, RoomExitDefinition exit, out string? failureMessage, out string? successMessage, bool isFollowMove = false)
    {
        failureMessage = null;
        successMessage = null;

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);

        if (exit.IsHiddenExit && !state.IsRevealed && !exit.IsPassableHiddenExit)
        {
            failureMessage = "There is no exit in that direction!";
            return false;
        }

        // Exit type 1: a Spell/portal exit is passable only by FOLLOWING someone through, or
        // by being cast/teleported through (a spell-teleport bypasses this gate entirely by setting the
        // destination directly — moveType 4). A normal walk is refused; a player dragging someone gets a
        // distinct refusal. (Dead in stock data — zero live rows — but faithfully gated so it works if used.)
        if (exit.ExitType == RoomExitType.Spell && !isFollowMove)
        {
            failureMessage = TryGetDraggedPlayer(player, out _)
                ? "You may not drag anyone through this exit."
                : "You need to cast a spell to go that way!";
            return false;
        }

        // Bug #209. The Class/Race/Level/Alignment denials below are FIXED strings in stock, taken
        // verbatim from the shipped binary (the exit switch, types 13/14/15/20; string
        // bytes read out of the binary directly). None of these cases looks up
        // a message at all — Para3 is never read on any of them, only Para1
        // and Para2. We were doing two wrong things at once: inventing fallback text
        // that appears NOWHERE in stock ("Only members of the X class may go that way.") and honouring
        // a Para3 message override stock ignores. That combination is what made 1/1377 west the odd one
        // out — it is the only class exit in the quest halls whose Para3 (8570) resolves to real text
        // ("An invisible barrier blocks your entry!"), so it printed that while its 49 siblings, whose
        // Para3 is the blank sentinel #1, fell through to the invented line. Stock prints one and the
        // same sentence for every one of them.
        if (exit.ExitType == RoomExitType.Level)
        {
            int minimumLevel = Math.Max(0, exit.Para1);
            int maximumLevel = exit.Para2 is > 0 and < 999 ? exit.Para2 : 0;

            if (minimumLevel > 0 && player.Level < minimumLevel)
            {
                failureMessage = "You have not progressed far enough to go through this exit!";
                return false;
            }

            if (maximumLevel > 0 && player.Level > maximumLevel)
            {
                failureMessage = "You have progressed too far to go through this exit!";
                return false;
            }
        }

        // Cases 0xd/0xe have TWO gate shapes, selected by Para1:
        //   Para1 != 0            -> INCLUSIVE: only that class/race passes.
        //   Para1 == 0, Para2 !=0 -> EXCLUSIVE: that class/race is the only one barred ("all except").
        //     Stock allows when Para2 is 0 or does not match the player — so a match on Para2 denies.
        //   Para1 == 0, Para2 ==0 -> open to everyone.
        // We previously implemented only the inclusive shape, so an exclusive gate let the one class it
        // was built to bar walk straight through. No stock rows use it today, but the data can express
        // it and content can, so honour both shapes.
        if (exit.ExitType == RoomExitType.Class && IsClassRaceGateBlocked(exit, player.ClassId))
        {
            failureMessage = "You may not go through this exit!";
            return false;
        }

        if (exit.ExitType == RoomExitType.Race && IsClassRaceGateBlocked(exit, player.RaceId))
        {
            failureMessage = "You may not go through this exit!";
            return false;
        }

        // Exit type 23 (Ability) is NOT gated at move time. The exit switch has cases for
        // types 1-16, 20 and 22 only; everything else — including 23 — falls to the
        // default, i.e. straight through to the room-entry gate. We used to block on
        // Para2/Para3 ability bounds and print "A strange power holds you back!", a string that (like the
        // bug #209 denials) exists nowhere in stock — it was cribbed from Messages #3154, which is
        // Para3 data on unrelated LEVEL exits. That barred players from 5 exits stock lets everyone walk.
        // GetExitAbilityValue is kept: the ability-value lookup is still used by textblock gate verbs.

        if (exit.ExitType == RoomExitType.Alignment)
        {
            // Alignment exit: Para1 = inclusive minimum, Para2 = inclusive maximum, both on the
            // stock alignment scale (positive = evil, negative
            // = good). Para1 ≤ -999 is the legacy "no lower bound" sentinel; Para2 ≥ 999 or == 0
            // is the legacy "no upper bound" sentinel. Sourcing this from player.EvilPoints (now
            // the canonical alignment field) is what makes a Saint-EP character actually clear
            // gates like 1/525 (Para1=-999, Para2=39) — the previous read of player.Alignment
            // missed because that legacy field used an inverted positive-good scale that gameplay
            // never updated.
            int minimumAlignment = exit.Para1 <= -999 ? int.MinValue : exit.Para1;
            int maximumAlignment = exit.Para2 >= 999 || exit.Para2 == 0 ? int.MaxValue : exit.Para2;
            int alignment = (int)Math.Round(player.EvilPoints);

            // Exit type 20 splits the denial by WHICH bound was missed — too far below the minimum is
            // "too good", above the maximum is "too evil" — where we printed one invented line for both.
            // Only the wording changes here; the bounds/sentinel logic above is untouched, so nobody's
            // access to a gate changes.
            if (alignment < minimumAlignment)
            {
                failureMessage = "You are too good to go through this exit!";
                return false;
            }

            if (alignment > maximumAlignment)
            {
                failureMessage = "You are too evil to go through this exit!";
                return false;
            }
        }

        if (exit.IsItemExit && exit.RequiredItemId > 0 && !PlayerHasItem(player, exit.RequiredItemId))
        {
            if (exit.FailureMessageNumber > 0 && Database.Messages.TryGetValue(exit.FailureMessageNumber, out var itemFailureMessage) && !string.IsNullOrWhiteSpace(itemFailureMessage.Line1))
                failureMessage = itemFailureMessage.Line1;
            else
                failureMessage = "You do not have a necessary item!";

            return false;
        }

        if (exit.IsBarrierExit && !state.Access.IsOpen)
        {
            failureMessage = $"The {exit.DoorNoun} is closed.";
            return false;
        }

        if (exit.IsToll)
        {
            long totalCopper = CurrencyHelper.ToCopper(player);
            long tollCopper = (long)exit.Para1 * CurrencyHelper.CopperPerGold;
            if (totalCopper < tollCopper)
            {
                failureMessage = $"You do not have enough to cover the toll of {exit.Para1} gold crowns.";
                return false;
            }

            CurrencyHelper.SetFromCopper(player, totalCopper - tollCopper);
            player.RecalculateEquipment(Database);
            successMessage = $"You just paid {exit.Para1} gold crowns in toll charges.";
        }

        return true;
    }

    public (int MapNumber, int RoomNumber) ResolveDeathRespawn(Player player, int deathMap, int deathRoom)
    {
        var deathRoomDefinition = GetRoom(deathMap, deathRoom);
        if (deathRoomDefinition?.DeathRoom > 0)
            return (deathMap, deathRoomDefinition.DeathRoom);

        return ShouldRespawnAtEvilDeathRoom(player)
            ? (DefaultDeathRespawnMapNumber, EvilDeathRespawnRoomNumber)
            : (DefaultDeathRespawnMapNumber, DefaultDeathRespawnRoomNumber);
    }

    private static bool ShouldRespawnAtEvilDeathRoom(Player player)
    {
        // Stock death/suicide respawn: EvilPoints < 40 → Temple, else Earthen Tomb.
        // The cut is Outlaw+ (>= 40), NOT the IsEvil band — Seedy (30-39) respawns at the Temple.
        return player.EvilPoints >= 40;
    }

    /// <summary>
    /// Exit types 13 (class) and 14 (race) — identical shape:
    ///     if (Para1 == 0) { if (Para2 == 0 || Para2 != value) allow; else deny; }
    ///     else            { if (Para1 == value) allow; else deny; }
    /// i.e. Para1 names the ONE class/race admitted, or — when Para1 is 0 — Para2 names the one barred.
    /// </summary>
    private static bool IsClassRaceGateBlocked(RoomExitDefinition exit, int playerValue)
    {
        if (exit.Para1 > 0)
            return playerValue != exit.Para1;

        return exit.Para2 > 0 && playerValue == exit.Para2;
    }

    private string ResolveExitFailureMessage(int messageNumber, string fallback)
    {
        if (messageNumber > 0
            && Database.Messages.TryGetValue(messageNumber, out var message)
            && !string.IsNullOrWhiteSpace(message.Line1))
        {
            return message.Line1;
        }

        return fallback;
    }

    private static int GetExitAbilityValue(Player player, int abilityId)
    {
        return abilityId switch
        {
            9 or 27 or 102 or 103 => player.Stealth,
            13 => player.Illumination,
            14 => player.RoomIllumination,
            29 => player.HasPunch ? 1 : 0,
            30 => player.HasKick ? 1 : 0,
            31 => player.HasBash ? 1 : 0,
            35 => player.HasJumpkick ? 1 : 0,
            57 => player.HasSeeHidden ? 1 : 0,
            77 => player.Perception,
            186 => player.HasPerfectStealth ? 1 : 0,
            _ => player.GetQuestAbilityValue(abilityId),
        };
    }

    private bool HasExternalRemoteActionTargeting(RoomExitDefinition exit)
    {
        foreach (var room in Database.Rooms.Values)
        {
            foreach (var candidate in room.GetExitDefinitions().Values.Where(def => def.IsRemoteAction))
            {
                if (candidate.MapNumber == exit.MapNumber && candidate.RoomNumber == exit.RoomNumber)
                    continue;

                if (candidate.TargetMap != exit.MapNumber || candidate.TargetRoom != exit.RoomNumber)
                    continue;

                string targetDirection = DirectionFromIndex(Math.Abs(candidate.Para2) % 10);
                if (string.IsNullOrEmpty(targetDirection))
                    continue;

                if (targetDirection.Equals(exit.Direction, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    public bool TryRevealHiddenExit(Room room, string direction, out RoomExitDefinition? exit)
    {
        exit = room.GetExit(NormalizeDirection(direction));
        // Only a SEARCHABLE hidden exit (runtime state bit 2) yields to a search. An
        // action-slot puzzle exit (slot bits set, searchable bit clear) is opened solely by its
        // remoteaction verb sequence — search must never reveal it. Callers already gate on this, but
        // enforce the invariant here so the helper can't be misused to bypass a puzzle.
        if (exit == null || !exit.IsSearchableHiddenExit)
            return false;

        var state = GetExitState(exit);
        state.IsRevealed = true;
        // A search reveal schedules a silent re-hide one block
        // (5 min) out. Re-searching before it fires refreshes the window.
        state.RehideAtUtc = DateTime.UtcNow + HiddenExitRehideDelay;
        return true;
    }

    // One block for a searched hidden exit = 5 min (300s,
    // the same per-block cadence as the door relock / trap re-arm).
    private static readonly TimeSpan HiddenExitRehideDelay = TimeSpan.FromMinutes(5);

    // Exit type 6: when a search-revealed hidden exit's timer fires it is set
    // back to hidden with NO room broadcast — the secret silently fades and must
    // be searched again. Returns true when a re-hide actually happened this pass.
    private static bool RefreshHiddenExitRehide(RoomExitDefinition exit, RoomExitState state, DateTime now)
    {
        if (!exit.IsHiddenExit || !state.RehideAtUtc.HasValue || state.RehideAtUtc.Value > now)
            return false;

        state.RehideAtUtc = null;
        state.IsRevealed = false;
        // Exit type 6: when a puzzle-revealed exit re-hides it also
        // RE-ARMS the action slots from the exit's slot bitfield (Para1) so the sequence must be solved
        // again. For a pure search-revealed exit Para1 carries no slot bits, so this is a no-op there.
        state.HiddenActionBits = exit.IsHiddenExit ? exit.Para1 : 0;
        return true;
    }

    public IEnumerable<RoomExitDefinition> GetTextCommandExits(Room room)
    {
        return room.GetExitDefinitions().Values.Where(exit => exit.HasDestination && exit.IsTextCommandExit);
    }

    // Action slots live in Para1 bits 4..13 (up to ten ordered remote actions). See RoomExitState.
    private const int HiddenActionBitMask = 0x3FF0;

    // Apply one fired remote action (identified by its order
    // index) to a hidden exit's runtime slot bitfield and report whether it just became revealed.
    //   - actionOrder <= 0  (remote Para2 < 10, "single action"): clear every slot at once.
    //   - actionOrder k      clears slot bit (1 << (k+3)). For an ORDERED exit (Para2 > 0) the
    //                        slot only clears while the next-higher slot is still set, so the engine
    //                        demands the highest order index first, descending to 1. An UNORDERED
    //                        exit (Para2 < 0) drops that guard and clears the slot unconditionally.
    // The exit reveals the instant all slots are clear. Wrong-order actions are no-ops, never a
    // reset — matching stock (it simply leaves the guarded slot untouched).
    private static bool AdvanceHiddenActionBits(RoomExitState state, RoomExitDefinition hidden, int actionOrder)
    {
        bool unordered = hidden.Para2 < 0;
        int bits = state.HiddenActionBits;

        if (actionOrder <= 0)
        {
            bits &= ~HiddenActionBitMask;
        }
        else if (actionOrder <= 10)
        {
            int slot = 16 << (actionOrder - 1);
            int nextSlot = slot << 1;
            if (unordered || (bits & nextSlot) == 0)
                bits &= ~slot;
        }

        state.HiddenActionBits = bits;

        if ((bits & HiddenActionBitMask) == 0 && !state.IsRevealed)
        {
            state.IsRevealed = true;
            // Clearing the final action slot reveals the exit AND schedules
            // its silent re-hide one block (5 min) out — the same cadence as
            // a searched secret. Without this a puzzle gate (e.g. Palace Gates 3/661 SE) stays open forever.
            state.RehideAtUtc = DateTime.UtcNow + HiddenExitRehideDelay;
            return true;
        }

        return false;
    }

    // Reveal block: when a hidden exit opens, the whole room (actor included) is
    // told. Use the hidden exit's own Para3 line1 if set, else the stock default. "%s" expands to
    // the revealed direction's name.
    private void SetHiddenRevealMessages(RemoteActionResult result, RoomExitDefinition hidden)
    {
        string direction = DirectionFromIndex(hidden.DirectionIndex);
        if (string.IsNullOrEmpty(direction))
            direction = hidden.Direction;

        string? text = null;
        if (hidden.Para3 > 0
            && Database.Messages.TryGetValue(hidden.Para3, out var message)
            && !string.IsNullOrWhiteSpace(message.Line1))
        {
            text = message.Line1.Trim();
        }

        text ??= $"A concealed passage opens to the {direction}.";
        text = text.Replace("%s", direction);

        result.RevealPlayerMessage = text;
        result.RevealRoomMessage = text;
    }

    public bool TryCompleteRemoteActionForExit(int mapNumber, int roomNumber, int directionIndex, int actionOrder, out bool changed)
    {
        changed = false;
        string direction = DirectionFromIndex(directionIndex);
        if (string.IsNullOrEmpty(direction))
            return false;

        var targetRoom = GetRoom(mapNumber, roomNumber);
        var targetExit = targetRoom?.GetExit(direction);
        if (targetExit == null)
            return false;

        var state = GetExitState(targetExit);

        if (targetExit.IsHiddenExit)
        {
            changed = AdvanceHiddenActionBits(state, targetExit, actionOrder);
            return true;
        }

        if (targetExit.IsDoorLike)
        {
            bool opening = !state.Access.IsOpen;
            state.Access.IsOpen = opening;
            if (opening)
            {
                state.Access.IsUnlocked = true;
                // A scripted action that opens a door/gate schedules its auto-relock
                // The textblock `remoteaction` op routes here, so it must too —
                // otherwise the gate (e.g. the Golden Spire book-stand gate) stays open forever instead
                // of relocking after the open-duration. Matches TryTriggerRemoteAction.
                StartTimedAccess(targetExit, state);
            }
            else
            {
                state.Access.AccessUntilUtc = null;
            }
            SyncExitStateAfterChange(targetExit, state);
            changed = true;
            return true;
        }

        return false;
    }

    public RemoteActionResult? TryTriggerRemoteAction(Player player, Room room, string spokenText)
    {
        string normalizedSpeech = spokenText.Trim().ToLowerInvariant();
        var remoteExits = room.GetExitDefinitions().Values.Where(def => def.IsRemoteAction).ToList();

        foreach (var exit in remoteExits)
        {
            // Stock fires a remote action only when the input equals one of the exit's command phrases
            // (an exact match against its three message lines) — there is no "any verb naming a room object" shortcut.
            var commands = exit.GetCommandPhrases(Database.Messages).ToList();
            if (!commands.Any(command => PhraseMatches(command, normalizedSpeech)))
                continue;

            // The remote-action branch: Para4 names an item the speaker must be
            // CARRYING. Without it the phrase does nothing at all — no message, the trigger simply does
            // not fire. With it, stock finds the item in inventory and deducts a charge BEFORE
            // running the action, so firing the trigger SPENDS a charge; a single-use item is destroyed
            // and prints its DestructMsg. The caller does the spending (it owns the inventory + client).
            // Stock looks the required item up BY NAME (the carried-item lookup with the item's full name,
            // any carried item, worn or not): an exact name wins, else the first carried item whose name
            // starts with it — and the charge is spent from whichever item that lookup found.
            int consumedItemId = 0;
            if (exit.Para4 > 0)
            {
                consumedItemId = FindCarriedItemByRequiredName(player, exit.Para4);
                if (consumedItemId == 0)
                    return null;
            }

            var result = new RemoteActionResult { ConsumedItemId = consumedItemId };
            if (Database.Messages.TryGetValue(exit.Para3, out var speechMessage))
            {
                // Standard message convention: Line1 = first-person to the ACTOR ("You pull the large
                // iron lever."), Line2 = third-person to the ROOM ("%s pulls the large iron lever.",
                // %s = the actor's name). Substitute %s here — the room broadcast was leaking a literal
                // "%s pulls the large iron lever." to everyone else.
                result.PlayerSpeechMessage = speechMessage.Line1?.Replace("%s", player.Name, StringComparison.OrdinalIgnoreCase);
                result.RoomSpeechMessage = speechMessage.Line2?.Replace("%s", player.Name, StringComparison.OrdinalIgnoreCase);
            }

            string targetDirection = DirectionFromIndex(Math.Abs(exit.Para2) % 10);
            if (string.IsNullOrEmpty(targetDirection))
                return result;

            var targetRoom = GetRoom(exit.TargetMap, exit.TargetRoom) ?? room;
            var targetExit = targetRoom.GetExit(targetDirection);
            if (targetExit == null)
                return result;

            // Remote actions can target hidden exits (reveal them) or barrier exits (open/close them).
            if (targetExit.IsHiddenExit)
            {
                if (targetExit.RequiredLevel > 0 && player.Level < targetExit.RequiredLevel && !HasExternalRemoteActionTargeting(targetExit))
                    continue;

                var targetState = GetExitState(targetExit);
                // Remote Para2 = targetDirection + 10*order; <10 means "single action" (order 0).
                int actionOrder = Math.Abs(exit.Para2) > 9 ? Math.Abs(exit.Para2) / 10 : 0;

                if (AdvanceHiddenActionBits(targetState, targetExit, actionOrder))
                    SetHiddenRevealMessages(result, targetExit);
            }
            else if (targetExit.IsDoorLike)
            {
                var targetState = GetExitState(targetExit);
                bool opening = !targetState.Access.IsOpen;
                targetState.Access.IsOpen = opening;
                if (opening)
                    targetState.Access.IsUnlocked = true;
                StartTimedAccess(targetExit, targetState);
                SyncExitStateAfterChange(targetExit, targetState);
            }
            else
            {
                return result;
            }

            return result;
        }

        return null;
    }

    // The remote-action item lookup: the required item's name run through the stock carried-item match.
    private int FindCarriedItemByRequiredName(Player player, int requiredItemId)
    {
        if (!Database.Items.TryGetValue(requiredItemId, out var required))
            return PlayerHasItem(player, requiredItemId) ? requiredItemId : 0;

        var carried = player.Inventory.Concat(player.Equipment.Values)
            .Where(id => Database.Items.ContainsKey(id));
        var matches = TargetNameMatcher.NarrowToExactOrAllMatches(carried, id => Database.Items[id].Name, required.Name);
        return matches.Count > 0 ? matches[0] : 0;
    }

    private static string FormatMessageTemplate(string template, string direction)
    {
        return template.Replace("%s", direction, StringComparison.OrdinalIgnoreCase).Trim();
    }

    // Stock compares the RAW typed line to each command phrase (case-insensitive equality) — no
    // stripping of a "say"/"speak" verb or quotes on either side. The data spells speech out literally
    // ("say darkness", "speak faith", "say 'midnight reveals secret knowledge'"), and `say`/`speak` are
    // not stock commands, so the typed line reaches this check whole. Speech itself (".x", fast-talk)
    // never fires an action.
    private static bool PhraseMatches(string command, string typedLine)
        => string.Equals(command.Trim(), typedLine.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool PlayerHasItem(Player player, int itemId)
    {
        return player.Inventory.Contains(itemId) || player.Equipment.Values.Contains(itemId);
    }

    private static bool PlayerHasSilverRiverFloatation(Player player)
    {
        return PlayerHasItem(player, SilverRiverLogRaftItemId)
            || PlayerHasItem(player, SilverRiverWoodenSkiffItemId)
            || PlayerHasItem(player, SilverRiverSilverbarkCanoeItemId);
    }
}