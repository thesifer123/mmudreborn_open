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
    private sealed record SysopActionFieldDefinition(
        string CanonicalName,
        string Description,
        Func<SocialAction, string> GetValue,
        Action<SocialAction, string> SetValue,
        params string[] Aliases);

    private readonly record struct TriggeredSpellResult(bool Teleported, bool StopProcessing);
    private const int SilverRiverRoomSpellId = 753;
    private const int SilverRiverEntryMessageId = 2665;
    private const int LearnedScrollSpellAbilityBase = Player.LearnedSpellbookAbilityBase;

    private const int BridgeJumpSpellId = 923;
    private readonly IGameClient _client;
    private readonly GameWorld _world;
    private readonly Player _player;
    private int _pendingCombatSpellId;
    private GameBugReportDraft? _pendingBugReportDraft;
    private bool _pendingRoomCommandTeleportRoomDisplay;

    // Silvermere Town Square fixtures in v1.11p data.
    private const int TownSquareMap = 1;
    private const int TownSquareRoom = 224;
    private const int TownSquareLargeSignItemId = 796;
    private const int TownSquareSmallSignItemId = 29;
    private const int NewhavenHealerRoom = 2190;
    private const int HealerSmallSignItemId = 513;
    private const int NewbieManualItemId = 1098;
    private static readonly TimeSpan CommunicationBurstWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CommunicationThrottleInterval = TimeSpan.FromSeconds(1);
    // The fast-tick period (1s) — the master movement/energy clock.
    private static readonly TimeSpan FastTick = TimeSpan.FromSeconds(1);
    // Move delays are integer fast-ticks: base 1; encumbrance > 66% adds
    // +1; > 100% blocks (HandleMovement). One encumbrance breakpoint at 66% — not the
    // paramud none/light/medium/heavy 4-tier. Full model in ApplyMovementDelayAsync: dragging doubles,
    // slow (68) ×2 / haste (67) ÷2, and the rapid-move counter adds +1 on the 3rd+ move.
    private const int MovementHasteAbilityId = 67;  // halves the move delay
    private const int MovementSlowAbilityId = 68;   // doubles the move delay
    // Movement-block line shown when a smash-knocked-down player tries to MOVE.
    // NOTE: the smash knockdown does NOT block attacking/casting in stock — only movement.
    // ("You are smashed to the ground!" is the on-land message, emitted by CombatEngine, not here.)
    private const string SmashKnockdownMoveBlockMessage = "You are too stunned to move anywhere!";
    // HoldPerson root (ability 74) movement-block line.
    private const string HoldPersonMoveBlockMessage = "You can't seem to move anywhere!";
    // Door/skill actions add a plain fast-tick delay (no encumbrance/haste modifiers):
    private const int OpenCloseDoorFastTicks = 1;  // OPEN / CLOSE
    private const int BashPicklockFastTicks = 2;   // BASHDOOR / PICKLOCK
    private const int CommunicationBurstLimit = 6;
    private static string? _cachedTownSquareMapAnsi;
    private static readonly Regex ValidSysopActionNameRegex = new("^[a-z][a-z0-9_-]{0,28}$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // Accept both the legacy {Ansi.NAME} form and the current {MudAnsi.NAME} form. Commit 79cae0c renamed
    // the Ansi facade to MudAnsi and migrated the documented token syntax (sysop help) to {MudAnsi.}, but
    // left this grammar as {Ansi.} — so {MudAnsi.} tokens (the documented form) never resolved. Matching
    // both keeps any stored {Ansi.} templates working while honoring the new convention.
    private static readonly Regex AnsiTokenRegex = new(@"\{(?:Mud)?Ansi\.(?<name>[A-Za-z0-9_]+)\}", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Dictionary<string, string> AnsiTokenValues = BuildAnsiTokenValues();
    private static readonly SysopActionFieldDefinition[] SysopActionFields =
    [
        new("self", "Actor sees with no target", static action => action.SingleToUser, static (action, value) => action.SingleToUser = value, "single", "single-user", "actor"),
        new("room", "Room sees with no target", static action => action.SingleToRoom, static (action, value) => action.SingleToRoom = value, "single-room"),
        new("player-self", "Actor sees when targeting a player", static action => action.UserToUser, static (action, value) => action.UserToUser = value, "user-user"),
        new("player-target", "Target player sees", static action => action.UserToOtherUser, static (action, value) => action.UserToOtherUser = value, "user-other", "user-to-other", "target-user"),
        new("player-room", "Room sees player-targeted action", static action => action.UserToRoom, static (action, value) => action.UserToRoom = value, "user-room"),
        new("monster-self", "Actor sees when targeting a monster", static action => action.MonsterToUser, static (action, value) => action.MonsterToUser = value, "monster-user"),
        new("monster-room", "Room sees monster-targeted action", static action => action.MonsterToRoom, static (action, value) => action.MonsterToRoom = value),
        new("inventory-self", "Actor sees when targeting an inventory item", static action => action.InventoryToUser, static (action, value) => action.InventoryToUser = value, "inventory-user", "item-self", "item-user"),
        new("inventory-room", "Room sees inventory-item action", static action => action.InventoryToRoom, static (action, value) => action.InventoryToRoom = value, "item-room"),
        new("floor-self", "Actor sees when targeting a floor item", static action => action.FloorItemToUser, static (action, value) => action.FloorItemToUser = value, "floor-user", "ground-self", "ground-user"),
        new("floor-room", "Room sees floor-item action", static action => action.FloorItemToRoom, static (action, value) => action.FloorItemToRoom = value, "ground-room"),
    ];
    private static readonly Dictionary<string, SysopActionFieldDefinition> SysopActionFieldAliases = BuildSysopActionFieldAliases();

    // Only MOVEMENT is blocked by a status — by the smash-knockdown flag (
    // "You are too stunned to move anywhere!") or the HoldPerson root (
    // "You can't seem to move anywhere!"). Neither blocks attacking or casting; the only thing that
    // interrupts an action is confusion (a per-action roll, handled separately).
    private bool IsMovementBlockedByStatus
        => (_player.IsKnockedDown && _player.KnockdownKind == KnockdownKind.Smash) || _player.IsRooted;

    private string GetMovementBlockMessage()
    {
        if (_player.IsKnockedDown && _player.KnockdownKind == KnockdownKind.Smash)
            return SmashKnockdownMoveBlockMessage;
        if (_player.IsRooted)
            return HoldPersonMoveBlockMessage;
        return string.Empty;
    }

    // Direction aliases — canonical table lives in MudDirections (shared with GameWorld).
    private static readonly IReadOnlyDictionary<string, string> DirectionAliases = MudDirections.Aliases;

    // A directional move (n/s/e/.../up/down) — the commands routed to HandleMovement at the top of
    // ProcessCommand. Used by GameSession to decide whether to pay the stock delay-before-move: the move's
    // delay is waited OFF the gate BEFORE relocating (ComputeMoveExposureDelay), so you stand in the
    // origin room taking combat-round hits while you "leave" and can't instant-bounce out of a monster's
    // room. go/enter/text-exits are NOT classified here — they resolve instantly (rare one-off crossings).
    public static bool IsMovementCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;
        var trimmed = input.Trim();
        if (trimmed.Length > 0 && (trimmed[0] is '/' or '\'' or '-' || "\".>".Contains(trimmed[0])))
            return false;
        var cmd = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return DirectionAliases.ContainsKey(cmd);
    }

    public CommandParser(IGameClient client, GameWorld world, Player player)
    {
        _client = client;
        _world = world;
        _player = player;
        _pendingCombatSpellId = player.PendingCombatSpellId;
    }

    public bool ReturningToMenu { get; private set; }

    private static Dictionary<string, string> BuildAnsiTokenValues()
    {
        var values = typeof(MudAnsi)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(string) && property.GetIndexParameters().Length == 0)
            .ToDictionary(
                property => property.Name,
                property => (string)(property.GetValue(null) ?? string.Empty),
                StringComparer.OrdinalIgnoreCase);

        foreach (var field in typeof(MudAnsi)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(string)))
        {
            values[field.Name] = (string)(field.GetValue(null) ?? string.Empty);
        }

        return values;
    }

    // Lookup map for SYSOP ACTION field editing: canonical name + each declared synonym → the field.
    // The SysopActionFields array is the single source of truth; this just indexes it by every name.
    private static Dictionary<string, SysopActionFieldDefinition> BuildSysopActionFieldAliases()
    {
        var aliases = new Dictionary<string, SysopActionFieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in SysopActionFields)
        {
            aliases[field.CanonicalName] = field;
            foreach (string alias in field.Aliases)
                aliases[alias] = field;
        }

        return aliases;
    }

    // Info commands that read the world but mutate nothing: top/topten (incl. `top gang`) and hall of
    // fame are pure DB reads, who is an atomic online-player snapshot. None touch live mutable game
    // state, so GameSession runs them OFF the global WorldStateGate — a leaderboard DB round-trip
    // (top) no longer freezes the whole world, and who/top no longer queue behind a combat round.
    // (Resolved canonical verbs: who/scan→"who", hall/halloffame→"hall", top, topten.)
    //
    // webwho (the non-stock `web-who` roster) belongs here for the SAME reason and MORE so: it does a
    // synchronous PlayerRepo.LoadPlayerByName DB read for every web-present name not currently in the
    // realm. Left on-gate it held the world gate across those round-trips, so the command right AFTER
    // web-who queued behind the backed-up combat round (seen as a one-shot `command:webwho` gate hold
    // in SYSOP DIAG). It mutates nothing — reads web presence + loads throwaway Players purely to
    // render — so it is safe off-gate, exactly like stock `who`.
    //
    // `map` renders the area's WCCMAPxx.ANS then BLOCKS on a "Hit any key to continue..." keypress. It
    // mutates nothing (reads a static asset file + the player's own map number, then re-shows the room),
    // so it MUST be off-gate: on-gate the keypress wait held the world gate for the reader's entire pause
    // (seen as multi-hundred-ms `command:map` holds in SYSOP DIAG that freeze every other player).
    private static readonly HashSet<string> ReadOnlyOffGateCommands = new(StringComparer.Ordinal)
    {
        "who", "hall", "top", "topten", "webwho", "map",
    };

    // The bug-report subsystem lives entirely in its OWN database table (PlayerRepo's bug methods);
    // it never touches live mutable world state and never starts combat. So every bug verb — the
    // report wizard, the listings, AND the moderation writes (complete/verify/still/delete) — runs
    // OFF the world gate. Those writes mutate only bug rows, not the combat-shared world, so they
    // no longer queue behind (or freeze) a combat round. (Canonical verbs after CommandRegistry
    // resolves abbreviations/aliases: listbugs→"bugs", removebug→"deletebug", etc.)
    private static readonly HashSet<string> BugOffGateCommands = new(StringComparer.Ordinal)
    {
        "bug", "bugs", "showbug", "completebug", "notabug", "verifybug", "stillbug", "deletebug",
    };

    // SYSOP sub-commands that only READ snapshots — GetAllOnlinePlayers().ToList(), _monsterLock'd
    // per-room snapshots, GameDiagnostics statics, or pure PlayerRepo DB reads. Each was verified to
    // never iterate a live collection, so they bypass the world gate. Every state-mutating sub-command
    // (spawn/god/heal/reset/configure/grantability/restart/...) is deliberately ABSENT and keeps
    // serializing on the gate, preserving the stock single-thread guarantee for anything that mutates.
    private static readonly HashSet<string> SysopReadOnlySubCommands = new(StringComparer.Ordinal)
    {
        "buffers", "status", "stat", "diag", "diagnostics", "report", "list", "map",
    };

    // Pure-delivery chat that only buffers output to recipients — none of these break sneak/hide, so
    // none mutate combat-visible state. Recipients are reached over the broadcast bus, which iterates
    // the ConcurrentDictionary of online players (enumeration is a moving snapshot — thread-safe, never
    // throws). The per-message rate-limit touches only THIS player's own throttle fields, and a single
    // session's GameLoop is the lone writer of those, so they're safe off the gate. "broadcast"/"br"
    // (and the '/'-/' prefixes) route to HandleApostropheSpeech, which delivers on the joined channel.
    // NOTE: say/yell (and the '"' yell / '.' say prefixes) are intentionally NOT here — they DO break
    // sneak/hide, so they keep serializing on the gate. GOSSIP/AUCTION are handled separately: bare
    // "gossip"/"auction" toggles a persisted receive flag (gated) while "<channel> <msg>" only
    // delivers (off-gate). (Canonical verbs: telepath/tell → "whisper"; br → "broadcast".)
    private static readonly HashSet<string> ChatOffGateCommands = new(StringComparer.Ordinal)
    {
        "whisper", "broadcast",
    };

    // True when `input` may bypass the global world gate: read-only info commands, the whole bug
    // subsystem, and the read-only SYSOP sub-commands. Mirrors ProcessCommand's verb parsing/
    // resolution so abbreviations and aliases are honoured; any speech/targeting prefix
    // ('/ . " - >') is never an off-gate command. NOTE: an in-progress bug-report wizard is handled
    // separately by GameSession via IsAwaitingBugReportInput (its later lines aren't bug verbs).
    public static bool IsOffGateCommand(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var trimmed = input.Trim();

        // Prefixes that DON'T break sneak are pure delivery and go off-gate even though they're
        // "prefixed": '/' telepath (private), and '\''/'-' broadcast (the join-channel range — same as
        // the br/broadcast verb; HandleApostropheSpeech delivers on the joined channel).
        if (trimmed[0] is '/' or '\'' or '-')
            return true;

        // Sneak-breaking / targeting prefixes stay gated: '"' yell and '.' say break sneak; '>' orders
        // a pet. All must keep serializing against the combat round.
        if ("\".>".Contains(trimmed[0]))
            return false;

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return false;

        var cmd = CommandRegistry.Resolve(parts[0].ToLowerInvariant());

        if (ReadOnlyOffGateCommands.Contains(cmd) ||
            BugOffGateCommands.Contains(cmd) ||
            ChatOffGateCommands.Contains(cmd))
            return true;

        // GOSSIP/AUCTION are dual-mode: bare "gossip"/"auction" toggles a persisted receive flag
        // (stays gated); "<channel> <message>" only delivers to the realm channel, so the message
        // form goes off-gate.
        if (cmd is "gossip" or "auction")
            return parts.Length > 1;

        // TRAIN is dual-mode: "train" (level up) is a synchronous self-mutation that stays GATED, but
        // "train stats" opens the interactive stat-allocation editor — a screen that blocks on user
        // keystrokes for as long as the player takes to spend their CP (seen as 44-second
        // source=command:train gate holds in SYSOP DIAG, freezing the entire world). The editor only
        // reads-then-edits LOCAL arrays; it touches no shared world state until the final commit, which
        // re-acquires the gate itself (see CharacterCreation.TrainAsync). So route the editor off-gate.
        if (cmd == "train")
            return parts.Length > 1 && parts[1].Trim().Equals("stats", StringComparison.OrdinalIgnoreCase);

        // SYSOP is a single verb with many sub-commands; only the read-only ones go off-gate. A bare
        // "sysop" just prints the help menu (read-only), so it qualifies too.
        if (cmd == "sysop")
        {
            if (parts.Length < 2)
                return true;

            var subParts = parts[1].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (subParts.Length == 0)
                return true;

            return SysopReadOnlySubCommands.Contains(subParts[0].ToLowerInvariant());
        }

        return false;
    }

    // True while a multi-line bug-report wizard is collecting input from this session. Those follow-up
    // lines (title/brief/description) aren't bug verbs, so GameSession can't recognise them via
    // IsOffGateCommand — but they only ever build a draft and, on submit, write a single bug row, so
    // the whole wizard runs off the world gate.
    public bool IsAwaitingBugReportInput => _pendingBugReportDraft != null;

    // A short, bounded label naming which command held the world gate, for the gate-hold diagnostic's
    // "Recent slow gate holds" list — so a slow hold reads "command:use" instead of a generic "command",
    // pinpointing the culprit. Resolves abbreviations to the canonical verb; never echoes args/free text.
    public static string GateHoldVerbLabel(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return "command";

        var trimmed = input.TrimStart();
        switch (trimmed[0])
        {
            case '/': return "command:telepath";
            case '\'':
            case '-': return "command:broadcast";
            case '"': return "command:yell";
            case '.': return "command:say";
            case '>': return "command:order";
        }

        var verb = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
        return "command:" + CommandRegistry.Resolve(verb);
    }

    // Commands that never fumble under confusion. Stock only bypasses the confusion roll
    // for the sysop path, so an admin keeps control of moderation tools while debuffed. Prefix action
    // commands (telepath '/', say '.', yell '"', broadcast '\''/'-', order '>') are NOT exempt — they are
    // real actions stock confuses too.
    private static bool IsConfusionExemptCommand(string trimmedInput)
    {
        if (trimmedInput.Length == 0)
            return true;

        char first = trimmedInput[0];
        if (first is '/' or '\'' or '-' or '"' or '.' or '>')
            return false;

        string verb = trimmedInput.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        return CommandRegistry.Resolve(verb.ToLowerInvariant()) == "sysop";
    }

    public async Task<bool> ProcessCommand(string input)
    {
        if (_pendingBugReportDraft != null)
        {
            await HandlePendingBugReportAsync(input ?? string.Empty);
            return true;
        }

        // A blank line redisplays the room. CONFIRMED AGAINST A LIVE STOCK BOARD: pressing Enter
        // repeatedly on stock reprints the room name, ground currency, "Also here:" and the
        // exits every time. Do NOT "fix" this to a bare prompt on the strength of the dispatcher
        // returning early on an empty line — an Enter from the client does not reach the door
        // as an empty argv, so that branch is not the blank-line path.
        if (string.IsNullOrWhiteSpace(input))
        {
            await ShowRoom(brief: _player.BriefMode);
            return true;
        }


        var trimmed = input.Trim();

        // Stock rolls the confusion check on EVERY typed command before dispatching it:
        // a confused player has a genrdn(0,100) < magnitude chance to fumble — the command is consumed
        // ("You look around stupidly and do nothing!") and never runs. So confusion interferes with ALL
        // actions (attacking, moving, casting, getting, gossiping, telepathing, …), not just movement —
        // each attempt rolls independently, so retrying "a <monster>" usually lands eventually. The
        // per-round auto-combat swing/cast has its OWN roll in ExecuteAutoPlayerCombatRound (the stock
        // separate round-resolve check). Sysop commands are exempt (stock sysop bypass).
        if (!IsConfusionExemptCommand(trimmed) && await ConfusedActionConsumedAsync())
            return true;

        // Slash-prefix = telepath (player-to-player message)
        if (trimmed.StartsWith('/'))
        {
            await HandleTelepath(trimmed[1..]);
            return true;
        }

        // Apostrophe-prefix = broadcast to joined channel, or say if no channel is joined.
        if (trimmed.StartsWith('\'') || trimmed.StartsWith('-'))
        {
            await HandleApostropheSpeech(trimmed[1..]);
            return true;
        }

        // Quote-prefix = yell to the room and adjacent rooms.
        if (trimmed.StartsWith('"'))
        {
            await HandleYell(trimmed[1..]);
            return true;
        }

        // Dot-prefix = say (always, regardless of talk mode)
        if (trimmed.StartsWith('.'))
        {
            var msg = trimmed.Substring(1);
            await HandleSay(msg);
            return true;
        }

        // ">" prefix = direct message (TELL). The "/" prefix is already handled above
        // (telepath); stock has no separate same-room whisper, so there is no second "/" path.
        if (trimmed.StartsWith('>') && trimmed.Length > 1)
        {
            await HandleDirectMessage(trimmed.Substring(1));
            return true;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : "";

        if (cmd == "action")
        {
            await HandleActionCommand(args);
            return true;
        }

        // "set" commands
        if (cmd == "set")
        {
            await HandleSet(args);
            return true;
        }

        // Check for movement
        if (DirectionAliases.TryGetValue(cmd, out var direction))
        {
            await HandleMovement(direction);
            return true;
        }

        if (cmd == "help" && args.Trim().Equals("exp", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelpExp();
            return true;
        }

        if (cmd == "help" && args.Trim().Equals("experience", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelpExp();
            return true;
        }

        // Accept the plural too. The renderer has always been reachable as "help bug", but the whole
        // command family it documents is plural-heavy (BUGS / LISTBUGS / SHOWBUG), so "help bugs" is
        // the form people actually reach for — and it used to land on the unknown-topic path.
        //
        // Gated on BugTrackerEnabled like every BUG verb in the switch below: the tracker is non-stock
        // tooling, so with SYSOP CONFIGURE BUGTRACKER off the help topic must not exist either. Falling
        // through here lands on the ordinary unknown-topic path, which is exactly what a stock realm
        // would answer — advertising commands that would then be refused is worse than saying nothing.
        if (cmd == "help" && IsBugHelpArgument(args) && _world.BugTrackerEnabled)
        {
            await RenderBugHelpAsync();
            return true;
        }

        if (cmd == "?" && await TryHandleQuestionMarkUtility(args))
        {
            return true;
        }

        if (cmd == "status")
        {
            await HandleStats(null);
            return true;
        }

        if (cmd == "stat")
        {
            await HandleStats(args);
            return true;
        }

        // Normalize abbreviations and aliases to a canonical command word using the stock-faithful
        // minimum-abbreviation table. Any verb the table doesn't
        // recognize passes through unchanged, so unknown input still falls to the room-exit fallback.
        // Keep the verb the player actually typed: several verbs collapse onto one canonical command
        // but still behave differently when given no argument (see HandleEquip — bare `equip` lists
        // your gear, bare `wear`/`wield`/`ready`/`arm` print their own Syntax line).
        string typedVerb = cmd;
        cmd = CommandRegistry.Resolve(cmd);

        switch (cmd)
        {
            case "look":
                await HandleLook(args);
                break;

            case "map":
                await HandleMapAsync();
                break;

            case "read":
                await HandleRead(args);
                break;

            // The bug tracker is non-stock tooling behind its own switch (SYSOP CONFIGURE BUGTRACKER). When
            // disabled, every bug verb falls through to the stock unknown-command path. (resolver maps
            // listbugs→"bugs", stillpresentbug→"stillbug", removebug→"deletebug".)
            case "bug":
            case "bugs":
            case "showbug":
            case "completebug":
            case "notabug":
            case "verifybug":
            case "stillbug":
            case "deletebug":
                if (!_world.BugTrackerEnabled)
                    goto default;
                switch (cmd)
                {
                    case "bug": await HandleBug(args); break;
                    case "bugs": await HandleListBugs(args); break;
                    case "showbug": await HandleShowBug(args); break;
                    case "completebug": await HandleCompleteBug(args); break;
                    case "notabug": await HandleNotABug(args); break;
                    case "verifybug": await HandleVerifyBug(args); break;
                    case "stillbug": await HandleStillBug(args); break;
                    case "deletebug": await HandleDeleteBug(args); break;
                }
                break;

            case "attack":
                await HandleAttack(args);
                break;

            case "backstab":
                await HandleBackstab(args);
                break;

            case "rob":
                await HandleRob(args);
                break;

            case "sneak":
                await HandleSneak();
                break;

            case "rest":
                await HandleRest();
                break;

            case "meditate":
                await HandleMeditate();
                break;

            case "aid":
                await HandleAid(args);
                break;

            case "break":
                await HandleBreak();
                break;

            case "search":
                await HandleSearch(args);
                break;

            case "appraise":
                // APPRAISE returns without printing (no argument, or no shop number on the room):
                // the line is simply not an appraisal, so it takes the unhandled path like any other.
                if (!await HandleAppraise(args))
                    goto default;
                break;

            case "train":
                await HandleTrain(args);
                break;

            case "track":
                await HandleTrack(args);
                break;

            // st/sta/stat/stats all → status (the stat sheet); only stas/stash → stash (hide).
            case "st":
            case "sta":
            case "stat":
                await HandleStats(args);
                break;

            case "inventory":
                // Stock-faithful: the resolver maps "i" and "inve".."inventory" here ("in"/"inv" are
                // dead). Inventory is no-arg-only — "i <text>" falls through to say like stock.
                if (string.IsNullOrEmpty(args))
                    await HandleInventory();
                else
                    goto default;
                break;

            case "keys":
                await HandleKeys();
                break;

            // resolver maps bal/balance/bank → "bankbook"
            case "bankbook":
                await HandleBankBook();
                break;

            case "deposit":
                await HandleDeposit(args);
                break;

            case "withdraw":
                await HandleWithdraw(args);
                break;

            // resolver maps g/take → "get"
            case "get":
                await HandleGet(args);
                break;

            case "drop":
                await HandleDrop(args);
                break;

            case "eat":
                await HandleEatOrDrinkAsync(args, isDrink: false);
                break;

            case "drink":
                await HandleEatOrDrinkAsync(args, isDrink: true);
                break;

            case "give":
                await HandleGive(args);
                break;

            case "share":
                await HandleShare(args);
                break;

            // resolver maps halloffame → "hall"
            case "hall":
                // Non-stock QOL command — when disabled it falls through to the stock unknown-command path.
                if (!_world.IsQolEnabled(QolFeature.Hall))
                    goto default;
                await HandleHallOfFame(args);
                break;

            case "forgive":
                await HandleForgive(args);
                break;

            case "light":
                // A LIGHT that names no carried light source is "not handled" in stock and falls through
                // exactly like an unknown command.
                if (!await HandleLight(args))
                    await HandleUnhandledCommandAsync(trimmed, cmd, args);
                break;

            // resolver maps eq/arm/wield/wear/ready → "equip"
            case "equip":
                await HandleEquip(args, typedVerb);
                break;

            case "remove":
                await HandleRemove(args);
                break;

            case "extinguish":
                await HandleExtinguish(args);
                break;

            // resolver maps hid/stash → "hide"
            case "hide":
                await HandleHide(args);
                break;

            // resolver maps sc → "who"
            case "who":
                await HandleWho();
                break;

            // Non-stock QOL command — the web-presence roster (telepath-eligible). Falls through to the
            // stock unknown-command path when disabled.
            case "webwho":
                if (!_world.IsQolEnabled(QolFeature.WhoWeb))
                    goto default;
                await HandleWhoWeb();
                break;

            case "top":
            case "topten":
                // Stock TOP handles the gang leaderboard itself: `top gang [count|all]` (and `top [count]
                // [player|gang|<class>]`). The non-stock one-word `topgangs`/`gangtop` aliases were removed.
                await HandleTop(args, cmd == "topten");
                break;

            case "wealth":
                await HandleWealth();
                break;

            case "exits":
                await HandleExits();
                break;

            case "room":
                // Non-stock QOL command — when disabled it falls through to the stock unknown-command path.
                if (!_world.IsQolEnabled(QolFeature.Room))
                    goto default;
                await HandleRoom();
                break;

            case "home":
                // Non-stock QOL command — when disabled it falls through to the stock unknown-command path.
                if (!_world.IsQolEnabled(QolFeature.Home))
                    goto default;
                await HandleHome(args);
                break;

            case "profile":
                await HandleProfile();
                break;

            case "say":
                await HandleSay(args);
                break;

            // resolver maps telepath → "whisper"; both route to the single telepath handler (stock has
            // no separate same-room whisper command).
            case "whisper":
                await HandleTelepath(args);
                break;

            case "greet":
                await HandleGreet(args);
                break;

            // resolver maps br..broadcast → "broadcast" (the stock minimum is "br"); '/- handled pre-switch.
            case "broadcast":
                await HandleApostropheSpeech(args);
                break;

            case "yell":
                await HandleYell(args);
                break;

            case "gossip":
                await HandleGossip(args);
                break;

            // resolver maps auc → "auction"; "auctions" (plural) kept explicitly
            case "auction":
                await HandleAuction(args);
                break;

            case "ignore":
            case "forget":
                await HandleIgnore(args);
                break;

            // resolver maps bg/gb → "broadgang"
            case "broadgang":
                await HandleBroadgang(args);
                break;

            case "join":
                await HandleJoin(args);
                break;

            case "invite":
                await HandleInvite(args);
                break;

            case "uninvite":
                await HandleUninvite(args);
                break;

            case "party":
                await HandleParty();
                break;

            case "leave":
                await HandleLeaveParty();
                break;

            case "frontrank":
                await HandlePartyRank(GameWorld.PartyRank.Front);
                break;

            case "backrank":
                await HandlePartyRank(GameWorld.PartyRank.Back);
                break;

            case "midrank":
                await HandlePartyRank(GameWorld.PartyRank.Middle);
                break;

            case "promote":
                await HandlePromote(args);
                break;

            case "demote":
                await HandleDemote(args);
                break;

            case "create":
                await HandleCreate(args);
                break;

            case "disband":
                await HandleDisband(args);
                break;

            case "cast":
                await HandleCast(args);
                break;

            case "invoke":
                await HandleInvoke(args);
                break;

            case "spells":
                await HandleSpells();
                break;

            case "powers":
                await HandlePowers();
                break;

            case "abilities":
                // Non-stock QOL command — when disabled it falls through to the stock unknown-command path.
                if (!_world.IsQolEnabled(QolFeature.Abilities))
                    goto default;
                await HandleAbilities();
                break;

            case "purge":
                await HandlePurge(args);
                break;

            case "disarm":
                await HandleDisarm(args);
                break;

            case "punch":
                await HandleMysticAttack(args, "punch", trimmed);
                break;

            case "kick":
                await HandleMysticAttack(args, "kick", trimmed);
                break;

            case "jumpkick":
                await HandleMysticAttack(args, "jumpkick", trimmed);
                break;

            // resolver maps bas/aa → "bash"
            case "bash":
                await HandleBash(args);
                break;

            case "smash":
                await HandleSmash(args);
                break;

            case "use":
                await HandleUse(args);
                break;

            case "lock":
                await HandleLock(args);
                break;

            case "picklock":
                await HandlePicklock(args);
                break;

            case "open":
                if (!await HandleOpen(args))
                    await HandleUnhandledCommandAsync(trimmed, cmd, args);
                break;

            case "close":
                await HandleClose(args);
                break;

            case "experience":
                await HandleExperience();
                break;

            case "suicide":
                await HandleSuicide();
                break;

            case "reroll":
                await HandleReroll();
                return !ReturningToMenu;

            case "health":
                await HandleHealth();
                break;

            case "list":
                await HandleShopList();
                break;

            case "buy":
                await HandleBuy(args);
                break;

            case "sell":
                await HandleSell(args);
                break;

            case "stock":
                await HandleStock(args);
                break;

            case "unstock":
                await HandleUnstock(args);
                break;

            case "markup":
                await HandleMarkup(args);
                break;

            case "verbose":
                _player.BriefMode = false;
                await _client.SendLineAsync($"{MudAnsi.White}Verbose mode set{MudAnsi.Reset}");
                break;

            case "brief":
                _player.BriefMode = true;
                await _client.SendLineAsync($"{MudAnsi.White}Quiet mode set{MudAnsi.Reset}");
                break;

            case "version":
                // Prints the version banner, in the stock banner's format.
                await _client.SendLineAsync($"{MudAnsi.White}MMUDREBORN v0.1.13 (Dec 30 2025 14:20:26){MudAnsi.Reset}");
                break;

            case "statline":
                // Standalone STATLINE ON/OFF/FULL (+ CUSTOM) — same
                // handler as SET STATLINE.
                await ApplyStatlineSettingAsync(args);
                break;

            case "go":
            case "enter":
                await HandleGoEnter(string.IsNullOrEmpty(args) ? cmd : $"{cmd} {args}");
                break;

            case "borrow":
            case "row":
            case "climb":
            case "crawl":
                await HandleGoEnter(string.IsNullOrEmpty(args) ? cmd : $"{cmd} {args}");
                break;

            case "follow":
                await HandleFollow(args);
                break;

            case "drag":
                await HandleDrag(args);
                break;

            case "ask":
                await HandleAsk(args);
                break;

            case "sysop":
                await HandleSysop(args);
                // Honor a session-ending sysop command (BOARD RESET sets ReturningToMenu): returning false
                // stops the loop before the post-command save, so a wiped character isn't re-persisted.
                return !ReturningToMenu;

            case "help":
            case "?":
                await HandleHelp(args);
                break;

            case "quit":
                await HandleQuit();
                return false;

            default:
                await HandleUnhandledCommandAsync(trimmed, cmd, args);
                break;
        }

        return true;
    }

    // The path a command takes when nothing handled it — an unknown verb, or a stock command that returned
    // 0 ("not handled"): room actions, then a direct spell command, then a social, then the no-op line.
    private async Task HandleUnhandledCommandAsync(string trimmed, string cmd, string args)
    {
        if (await TryHandleRoomAction(trimmed))
            return;

        if (await TryHandleDirectSpellCommand(trimmed))
            return;

        if (await TryHandleSocialAction(cmd, args))
            return;

        // Fast talk mode (SET TALK FAST): unrecognized input is spoken aloud. It echoes "You say ..."
        // even when the player is alone in the room (speakEvenWithoutAudience) — the whole point of
        // fast-talk is that what you typed gets said. Slow talk mode keeps the stock no-op message.
        if (_player.TalkMode == 1)
        {
            await HandleSay(trimmed, speakEvenWithoutAudience: true);
        }
        else
        {
            // "Your command had no effect."
            await _client.SendLineAsync("Your command had no effect.");
        }
    }

}
