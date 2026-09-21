using CWGaming.Shared;
using mmudreborn.Data;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace mmudreborn.Server;

public partial class GameWorld : IBbsDoorContext
{
    public const int SysopTestingRoomMapNumber = 999;
    public const int SysopTestingRoomNumber = 1;
    public const int SysopTestingItemsRoomNumber = 2;
    public const int SysopTestingGroupRoomNumber = 3;
    public const int DefaultDeathRespawnMapNumber = 1;
    public const int DefaultDeathRespawnRoomNumber = 2189;
    public const int EvilDeathRespawnRoomNumber = 142;
    // Room type 5 is the combat arena / collision room.
    public const int ArenaRoomType = 5;
    // Room type 2 is a quest/level-capped room — entry
    // is blocked when the room's MaxIndex level cap is below the player's level.
    public const int QuestRoomType = 2;
    // Stock makes the level-ahead exp gate configurable in [0,16]. We add 0 = OFF.
    public const int MaxLevelAheadCap = 16;
    public const int MaxBroadcastChannelNumber = 999_999;

    private sealed record SysopTestingRoomDefinition(int RoomNumber, string Name, string Description, string[] Aliases);

    private static readonly SysopTestingRoomDefinition[] SysopTestingRooms =
    [
        new(
            SysopTestingRoomNumber,
            "Sysop Testing Chamber",
            "A quiet chamber reserved for sysop regression and combat timing tests. Use SYSOP SPAWN here to stage deterministic monster encounters.",
            ["testing", "test"]),
        new(
            SysopTestingItemsRoomNumber,
            "Sysop Item Testing Chamber",
            "A quiet chamber reserved for ground-item, stash, and currency-output regressions where ambient monster chatter would contaminate captures.",
            ["testing items", "testing item", "test items", "test item", "items", "item"]),
        new(
            SysopTestingGroupRoomNumber,
            "Sysop Group Testing Chamber",
            "A wide chamber reserved for party, observer, and grouped-monster regressions that need extra staging space.",
            ["testing group", "testing groups", "test group", "test groups", "group", "groups"]),
    ];

    private static readonly HashSet<int> SysopTestingRoomNumbers = SysopTestingRooms
        .Select(definition => definition.RoomNumber)
        .ToHashSet();

    private static readonly Dictionary<string, int> SysopTestingRoomAliases = SysopTestingRooms
        .SelectMany(definition => definition.Aliases.Select(alias => new KeyValuePair<string, int>(alias, definition.RoomNumber)))
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    public enum PartyRank
    {
        Front,
        Middle,
        Back,
    }

    public sealed class PartyMemberSnapshot
    {
        public string Name { get; init; } = "";
        public string LastName { get; init; } = "";
        public string ClassName { get; init; } = "";
        public bool HasMana { get; init; }
        // Party mana label: 'K' for the Kai-magery class (MageryType 5),
        // 'M' otherwise. Drives the "[K:..%]" vs "[M:..%]" bracket.
        public bool UsesKai { get; init; }
        public int ManaPercent { get; init; }
        public int HitsPercent { get; init; }
        public PartyRank Rank { get; init; }
        public bool IsInvited { get; init; }
        // The party display prints two status chars per member: 'P' when poisoned
        // (PoisonLevel != 0) and 'R' resting / 'M' meditating (else a space for each).
        public bool IsPoisoned { get; init; }
        public bool IsResting { get; init; }
        public bool IsMeditating { get; init; }
    }

    public sealed class PartySnapshot
    {
        public string LeaderName { get; init; } = "";
        public bool ViewerIsLeader { get; init; }
        public List<PartyMemberSnapshot> Members { get; init; } = [];
    }

    public sealed class PartyTravelCleanupResult
    {
        public bool DisbandedParty { get; init; }
        public string SelfMessage { get; init; } = "";
        public string? LeaderName { get; init; }
        public List<string> AffectedMembers { get; init; } = [];
    }

    private sealed class PartyState
    {
        public string LeaderName { get; init; } = "";
        public List<string> Members { get; } = [];
        public Dictionary<string, PartyRank> Ranks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Invited { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ExitAccessState
    {
        public bool IsUnlocked { get; set; }
        public bool IsOpen { get; set; }
        public DateTime? AccessUntilUtc { get; set; }
    }

    public sealed class RemoteActionResult
    {
        public string? PlayerSpeechMessage { get; set; }
        public string? RoomSpeechMessage { get; set; }
        public string? RevealPlayerMessage { get; set; }
        public string? RevealRoomMessage { get; set; }

        // Para4 — the item the trigger REQUIRED, when it had one. Stock spends one of its charges as
        // part of firing the action (the charge is deducted before running
        // it), so the caller must consume it. 0 when the trigger carried no item requirement.
        public int ConsumedItemId { get; set; }
    }

    private sealed class RoomExitState
    {
        public bool IsRevealed { get; set; }
        public ExitAccessState Access { get; set; } = new();
        // A hidden exit's Para1 doubles as a runtime bitfield. Bits
        // 4..13 are up to ten remote-action "slots"; each fired action clears its
        // slot, and the exit reveals once every slot is clear. Initialized from Para1 in
        // GetExitState (action bits = Para1 & HiddenActionBitMask). See AdvanceHiddenActionBits.
        public int HiddenActionBits { get; set; }
        public bool TrapDisarmed { get; set; }
        // A disarmed trap re-arms silently when its scheduled timer fires. Null = armed or
        // permanently-disarmed (never re-armed). See RefreshTrapReArm.
        public DateTime? TrapReArmAtUtc { get; set; }
        // Exit type 6: a hidden exit
        // revealed by SEARCH re-hides silently one block (5 min) later. Set only on a search reveal;
        // null means "no scheduled re-hide" (start-visible or remote-action reveals stay revealed).
        // See RefreshHiddenExitRehide.
        public DateTime? RehideAtUtc { get; set; }
    }

    private sealed class GroundCurrencySnapshot
    {
        public long Runic { get; set; }
        public long Platinum { get; set; }
        public long Gold { get; set; }
        public long Silver { get; set; }
        public long Copper { get; set; }
    }

    private readonly record struct LairCandidateKey(int MonsterType, int MinIndex, int MaxIndex, int ByNumber);

    private readonly record struct SpawnedMonsterAnnouncement(int Map, int Room, MonsterInstance Instance);

    private readonly record struct MonsterMovementAnnouncement(
        int FromMap,
        int FromRoom,
        int ToMap,
        int ToRoom,
        string Direction,
        string Name,
        string? ExitMessage,
        string? EntranceMessage);

    // CarriesMovementNoise: the entry-movement display switches on the exit type and only emits
    // for 0/3/4/7/9/11 (Normal, Item, Toll, Door, Trap, Gate). Every other type — Spell, Key, Action,
    // Hidden, ChangeMap, Text, RemoteAction, Class/Race/Level/Timed — is silent, so a hidden passage or
    // a keyed door does not leak movement to the room behind it. Carried per-entry rather than filtered
    // out of the index because GetAdjacentRooms shares this index and wants EVERY exit.
    // The switch cases are: 0, 3, 4, 7, 9, 11.
    private static readonly HashSet<RoomExitType> MovementNoiseExitTypes =
    [
        RoomExitType.Normal, RoomExitType.Item, RoomExitType.Toll,
        RoomExitType.Door, RoomExitType.Trap, RoomExitType.Gate,
    ];

    private readonly record struct IncomingMovementExit(int Map, int Room, string Direction, bool CarriesMovementNoise);

    private readonly record struct BackgroundGenerationStats(
        int ActiveTrueLairRooms,
        int TrueLairRoomsScanned,
        int TrueLairRoomsProcessed,
        int PlayerRooms,
        int AdjacentRoomsChecked,
        int SpawnAttempts,
        int MonstersSpawned);

    private readonly record struct RoomSpellTickStats(
        int PlayersProcessed,
        int ActiveSpellRooms,
        int PulsesTriggered);


    private sealed class GroundItemEntry
    {
        public int ItemId { get; init; }
        public bool IsHidden { get; init; }
        public long InstanceId { get; init; }
        public bool IsStaticSeeded { get; init; }
    }

    public sealed class ItemRuntimeState
    {
        public DateTime ActiveLightUntilUtc { get; set; } = DateTime.MinValue;
        public TimeSpan StoredLightRemaining { get; set; } = TimeSpan.Zero;
        public DateTime LightRechargeReadyAtUtc { get; set; } = DateTime.MinValue;
        public int? RemainingCharges { get; set; }
    }

    private sealed class PlayerRuntimeItemStateSnapshot
    {
        public List<(int ItemId, long InstanceId)> Inventory { get; init; } = [];
        public Dictionary<string, (int ItemId, long InstanceId)> Equipment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class PlayerRoomSpellState
    {
        public int SpellId { get; set; }
        public DateTime NextPulseAtUtc { get; set; }
        public int PulseCount { get; set; }
    }

    internal enum RoomSpellCastOutcome
    {
        None,
        Reprompt,
        StopProcessing,
    }

    private readonly record struct GroundCurrencyStacks(long Runic, long Platinum, long Gold, long Silver, long Copper)
    {
        public static GroundCurrencyStacks Empty => new(0, 0, 0, 0, 0);

        public bool IsEmpty => Runic <= 0 && Platinum <= 0 && Gold <= 0 && Silver <= 0 && Copper <= 0;

        public long TotalCopper => CurrencyHelper.ToCopper(Runic, Platinum, Gold, Silver, Copper);

        public static GroundCurrencyStacks FromCopperNormalized(long copperAmount)
        {
            long remaining = Math.Max(0, copperAmount);

            long runic = remaining / CurrencyHelper.CopperPerRunic;
            remaining %= CurrencyHelper.CopperPerRunic;

            long platinum = remaining / CurrencyHelper.CopperPerPlatinum;
            remaining %= CurrencyHelper.CopperPerPlatinum;

            long gold = remaining / CurrencyHelper.CopperPerGold;
            remaining %= CurrencyHelper.CopperPerGold;

            long silver = remaining / CurrencyHelper.CopperPerSilver;
            long copper = remaining % CurrencyHelper.CopperPerSilver;

            return new(runic, platinum, gold, silver, copper);
        }

        public static GroundCurrencyStacks FromDenomination(long denominationMultiplier, long count)
        {
            if (count <= 0)
                return Empty;

            return denominationMultiplier switch
            {
                CurrencyHelper.CopperPerRunic => new(count, 0, 0, 0, 0),
                CurrencyHelper.CopperPerPlatinum => new(0, count, 0, 0, 0),
                CurrencyHelper.CopperPerGold => new(0, 0, count, 0, 0),
                CurrencyHelper.CopperPerSilver => new(0, 0, 0, count, 0),
                1 => new(0, 0, 0, 0, count),
                _ => Empty,
            };
        }

        public static GroundCurrencyStacks FromSnapshot(GroundCurrencySnapshot? snapshot)
        {
            if (snapshot == null)
                return Empty;

            return new(
                Math.Max(0, snapshot.Runic),
                Math.Max(0, snapshot.Platinum),
                Math.Max(0, snapshot.Gold),
                Math.Max(0, snapshot.Silver),
                Math.Max(0, snapshot.Copper));
        }

        public GroundCurrencySnapshot ToSnapshot()
        {
            return new GroundCurrencySnapshot
            {
                Runic = Runic,
                Platinum = Platinum,
                Gold = Gold,
                Silver = Silver,
                Copper = Copper,
            };
        }

        public GroundCurrencyStacks Add(GroundCurrencyStacks other)
        {
            return new(
                Runic + other.Runic,
                Platinum + other.Platinum,
                Gold + other.Gold,
                Silver + other.Silver,
                Copper + other.Copper);
        }

        public long GetDenominationCount(long denominationMultiplier)
        {
            return denominationMultiplier switch
            {
                CurrencyHelper.CopperPerRunic => Runic,
                CurrencyHelper.CopperPerPlatinum => Platinum,
                CurrencyHelper.CopperPerGold => Gold,
                CurrencyHelper.CopperPerSilver => Silver,
                1 => Copper,
                _ => 0,
            };
        }

        public GroundCurrencyStacks RemoveDenomination(long denominationMultiplier, long count, out GroundCurrencyStacks removed)
        {
            if (count <= 0)
            {
                removed = Empty;
                return this;
            }

            long taken;
            switch (denominationMultiplier)
            {
                case CurrencyHelper.CopperPerRunic:
                    taken = Math.Min(count, Runic);
                    removed = new GroundCurrencyStacks(taken, 0, 0, 0, 0);
                    return new GroundCurrencyStacks(Runic - taken, Platinum, Gold, Silver, Copper);
                case CurrencyHelper.CopperPerPlatinum:
                    taken = Math.Min(count, Platinum);
                    removed = new GroundCurrencyStacks(0, taken, 0, 0, 0);
                    return new GroundCurrencyStacks(Runic, Platinum - taken, Gold, Silver, Copper);
                case CurrencyHelper.CopperPerGold:
                    taken = Math.Min(count, Gold);
                    removed = new GroundCurrencyStacks(0, 0, taken, 0, 0);
                    return new GroundCurrencyStacks(Runic, Platinum, Gold - taken, Silver, Copper);
                case CurrencyHelper.CopperPerSilver:
                    taken = Math.Min(count, Silver);
                    removed = new GroundCurrencyStacks(0, 0, 0, taken, 0);
                    return new GroundCurrencyStacks(Runic, Platinum, Gold, Silver - taken, Copper);
                case 1:
                    taken = Math.Min(count, Copper);
                    removed = new GroundCurrencyStacks(0, 0, 0, 0, taken);
                    return new GroundCurrencyStacks(Runic, Platinum, Gold, Silver, Copper - taken);
                default:
                    removed = Empty;
                    return this;
            }
        }

        public List<string> ToDisplayParts()
        {
            var parts = new List<string>();

            if (Runic > 0) parts.Add($"{Runic} runic {(Runic == 1 ? "coin" : "coins")}");
            if (Platinum > 0) parts.Add($"{Platinum} platinum {(Platinum == 1 ? "piece" : "pieces")}");
            if (Gold > 0) parts.Add($"{Gold} gold {(Gold == 1 ? "crown" : "crowns")}");
            if (Silver > 0) parts.Add($"{Silver} silver {(Silver == 1 ? "noble" : "nobles")}");
            if (Copper > 0) parts.Add($"{Copper} copper {(Copper == 1 ? "farthing" : "farthings")}");

            return parts;
        }
    }

    private static readonly string[] ExitDirections =
    {
        "north", "south", "east", "west", "northeast", "northwest", "southeast", "southwest", "up", "down"
    };

    // Canonical alias table lives in MudDirections (shared with CommandParser).
    private static readonly IReadOnlyDictionary<string, string> DirectionAliases = MudDirections.Aliases;

    // Swappable so a sysop `reloaddata` can atomically replace the in-memory game data without a
    // process restart. Reference assignment is atomic; readers see the old or the new fully-loaded
    // database, never a partial one. Backed by a volatile field for cross-thread visibility (timers
    // run on threadpool threads).
    private volatile IGameDatabase _database;
    public IGameDatabase Database => _database;
    // Set by the host so ReloadGameData can build a fresh, fully-loaded database (e.g. a new
    // GameDatabase + LoadAll). Null in contexts without a reloader (tests) — reloaddata is then a
    // no-op that asks the operator to use `sys restart`.
    public Func<IGameDatabase>? GameDataReloader { get; set; }
    // Set by the host: invoked with (exitCode, reason) when a sysop requests a restart/shutdown. The
    // host performs the graceful save + process exit. Null in tests (Request* then no-ops safely).
    public Action<int, string>? OnShutdownRequested { get; set; }
    public IPlayerRepository PlayerRepo { get; }
    public IBbsUserRepository? BbsUserRepo { get; }
    public IBbsCommandDispatcher BbsCommandDispatcher { get; }
    public bool ArenaCombatMode { get; private set; }
    public int PvpLevelRange { get; private set; }
    public int DeathSetting { get; private set; }
    public int MonsterExperienceRate { get; private set; }
    public int RerollKeepExperiencePercent { get; private set; }
    // "Monster Generation Rate" (configure GENRATE, in seconds): how often the
    // background spawn driver runs. This is a board-configurable value loaded from the
    // server config at boot — stock ships NO canonical default, so there is no single "stock" number;
    // every board set its own. We default low (5s) for a lively, stock-feeling world. NOTE: our pass is
    // gated on the 3s medium tick via (GENRATE / 3), so any value 3–5 collapses to ~3s effective cadence.
    public int MonsterGenerationRateSeconds { get; private set; } = 5;
    // "Minutes before regenerating" (configure MINWAIT, in minutes): the default
    // per-room respawn delay used when a room's own Delay field is 0. Default 5 (stock).
    public int LairRegenDefaultMinutes { get; private set; } = 5;
    // Radius (in room-graph hops) of the per-player spawn bubble: the BFS that keeps lairs within this
    // many rooms of a player on the scheduled background-regen list. NOT a stock value (stock has no such
    // pre-warming concept). Default 10 — small enough that the BFS is cheap and the global 4-spawns/tick
    // scheduled budget concentrates on a tight neighborhood, large enough to keep a player's surroundings
    // alive. A moving player's arrival room is filled by the at-player/adjacent pass regardless of this.
    // Configurable via SYSOP CONFIGURE BUBRADIUS.
    public int ActiveLairSpawnRoomRadius { get; private set; } = 10;
    public int LevelAheadCap { get; private set; }
    public int GangCreateRunicCost { get; private set; }
    public int GangCreateMinimumExperience { get; private set; }
    public int GangHouseMinimumExperience { get; private set; }
    public int LimitedItemsMode { get; private set; }
    public int MaxEvilPointsForgivenPerDay { get; private set; }
    public int EvilPointForgivenessCycleMinutes { get; private set; }
    public int EvilPointForgivenessAmount { get; private set; }

    // Drop-carrier penalty — the hangup routine. Stock reads the
    // level from a config token over HIGH/MEDIUM/LOW/NONE, so the values here are the stock
    // 1-based token indices. Stock DEFAULTS TO HIGH (tokopt returns 0 when the option is absent and the
    // stock forces it to 1); we default to NONE instead, deliberately, so the mechanism ships inert and the
    // sysop opts in — see SYSOP CONFIGURE DISCONNECTPENALTY.
    public const int DisconnectPenaltyHigh = 1;    // every disconnect
    public const int DisconnectPenaltyMedium = 2;  // only while in autocombat or being attacked
    public const int DisconnectPenaltyLow = 3;     // only while in PvP combat
    public const int DisconnectPenaltyNone = 4;    // never
    public int DisconnectPenaltyLevel { get; private set; } = DisconnectPenaltyNone;

    // The penalty itself: lose a random slice of MaxHP between these two percentages (two config
    // values, both range-checked 0-100), and drop up to this many items on the floor. The
    // stock numbers live in the .MCV config file, which we do not have, so these defaults are OURS — with
    // the level defaulting to NONE they are inert until a sysop turns the penalty on and tunes them.
    public int DisconnectPenaltyMinHpPercent { get; private set; } = 10;
    public int DisconnectPenaltyMaxHpPercent { get; private set; } = 25;
    public int DisconnectPenaltyMaxItemsDropped { get; private set; } = 3;
    // Stock: once a player is over the EP cap (Player.EvilPointGainCap),
    // attacking an innocent (align 0/4) is BLOCKED, not merely denied the EP gain — the same return that
    // gates the swing. Newer MUDs dropped that block, so it's a server toggle. ON = stock (block the
    // action). OFF = modern (action proceeds; EP gain is still refused past the cap). Default ON.
    public bool EvilCapBlocksActions { get; private set; } = true;
    // Governs ONLY the non-backstab-weapon surprise (a sneaking player attacking with `backstab` while
    // holding a weapon that lacks the backstab ability). A REAL backstab (backstab-capable weapon or
    // unarmed) is unaffected — it is always a full, silent surprise round.
    //   ON  = the non-backstab weapon DOES get a surprise round: full backstab DAMAGE + a silent surprise
    //         (the victim gets no "moves to attack" warning and learns of it when the hit lands).
    //   OFF = no surprise round: the swing is a plain NORMAL attack — normal damage and the victim IS
    //         warned ("moves to attack you"), exactly like any other normal attack.
    // Default OFF. (Faithful stock is actually backstab-damage + silent, i.e. ON; OFF is the
    // community-patched "treat it as a normal attack" behavior. Sysop's choice.)
    public bool SurpriseRoundEnabled { get; private set; }
    // QUESTALLPARTY: a quality-of-life toggle for main-quest items that a monster DROPS to the ground on
    // death (the "kill → item drops → pick up → turn in" pattern). In stock only ONE copy drops per kill, so
    // a party of N must kill the monster N times (one turn-in each). ON = hand one copy to EACH engaged
    // player who is on that quest step and still needs it, instead of a single ground drop (nobody needing it
    // = the stock ground drop). OFF = stock. Default OFF. (Only the curated
    // CommandParser.QuestPartyDropNeeds set is affected; normal loot is unchanged.)
    public bool QuestDropToAllParty { get; private set; }
    // Deepest the alignment scale goes (mudinfo evil/legal spec: "-220 <= Saint < -200"). EP forgiveness
    // drifts a clean player DOWN toward this floor — i.e. all the way to Saint — never stopping at 0.
    public const float EvilPointForgivenessFloor = -220f;

    // NON-STOCK (SYSOP CONFIGURE MINEPS, default OFF): lets a player pin a minimum evil-point standing
    // with `set mineps <amount>`, below which the passive forgiveness drift will not carry them. The
    // long-standing complaint it answers: script the game overnight, drift Outlaw → Seedy, and the gear
    // your old band allowed is stripped off you. It never grants evil points
    // — the standing is still earned — and it clamps ONLY the forgiveness tick, not `forgive`, quest
    // absolution or sysop changes. Off by default because none of it is stock.
    public bool MinEvilPointsEnabled { get; private set; }
    public TimeOnly DailyCleanupTime { get; private set; } = new(4, 0);
    private DateTime _nextCleanupAtUtc;
    private DateTime _lastCleanupAtUtc;

    private readonly ConcurrentDictionary<(int Map, int Room), List<MonsterInstance>> _roomMonsters = new();
    private readonly ConcurrentDictionary<string, Player> _onlinePlayers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stock evil-timer relationship list: the directed (aggressor → victim)
    /// PvP edges that drive the self-defence evil-points waiver, the "Also here:" name marker, and the
    /// retaliation travel gate. See <see cref="EvilTimerRegistry"/>.
    /// </summary>
    public EvilTimerRegistry EvilTimers { get; } = new();

    private readonly object _monsterLock = new();
    // Not readonly: rebuilt from the swapped database by ReloadGameData.
    private Dictionary<int, HashSet<(int Map, int Room)>> _boundNpcRoomsByMonsterNumber;
    private readonly Random _rng = new();
    private IBbsHost? _host;

    private readonly ConcurrentDictionary<int, int> _globalMonsterCounts = new();
    // Spawn regen-timer gate: a unique monster (GameLimit==1) with a
    // RegenTime (in HOURS) records its last-death stamp (written on
    // the kill) and may not respawn until RegenTime hours (± a re-rolled ~12.5% jitter)
    // have elapsed. Keyed by monster number; persisted so the long timers survive restarts.
    private readonly ConcurrentDictionary<int, DateTime> _monsterRegenLastDeathUtc = new();
    private const string MonsterRegenStateSettingKey = "MonsterRegenDeaths";
    private readonly ConcurrentDictionary<(int Map, int Room), DateTime> _roomNextLairSpawnAtUtc = new();
    private readonly object _activeSpawnRoomsLock = new();
    private readonly Dictionary<string, HashSet<(int Map, int Room)>> _activeSpawnRoomsByPlayer = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(int Map, int Room)> _activeSpawnRooms = [];
    private readonly HashSet<(int Map, int Room)> _activeTrueLairSpawnRooms = [];
    private readonly object _lairCandidateCacheLock = new();
    private readonly Dictionary<LairCandidateKey, IReadOnlyList<Monster>> _lairCandidateCache = [];
    // Not readonly: rebuilt from the swapped database by ReloadGameData.
    private Dictionary<(int Map, int Room), IReadOnlyList<IncomingMovementExit>> _incomingMovementExitsByTargetRoom;
    private int _activeTrueLairSpawnCursor;

    private readonly ConcurrentDictionary<(int Map, int Room), List<GroundItemEntry>> _roomGroundItems = new();
    private readonly ConcurrentDictionary<(int Map, int Room), byte> _initializedStaticGroundItemRooms = new();
    // "Logical room item" removal overlay: a
    // non-gettable visible-placed item (e.g. the giant apparatus 819) is drawn straight from the room's
    // static Placed field, so it has no dynamic ground entry to delete. clearitem records its removal
    // here; the renderer and presence checks subtract it. In-memory only — it re-seeds on restart/reset
    // so the quest prop respawns, matching the stock room-reset cycle.
    private readonly ConcurrentDictionary<(int Map, int Room), HashSet<int>> _removedPlacedItems = new();
    private readonly object _groundItemLock = new();
    private readonly ConcurrentDictionary<long, ItemRuntimeState> _itemRuntimeStates = new();
    private readonly ConcurrentDictionary<string, PlayerRuntimeItemStateSnapshot> _offlinePlayerItemStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PlayerRoomSpellState> _playerRoomSpellStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _playerNextEvilPointForgivenessTick = new(StringComparer.OrdinalIgnoreCase);
    private long _nextItemInstanceId;

    private readonly ConcurrentDictionary<(int Map, int Room), (GroundCurrencyStacks Visible, GroundCurrencyStacks Hidden)> _roomGroundCurrency = new();
    private readonly ConcurrentDictionary<(int Map, int Room), byte> _initializedStaticGroundCurrencyRooms = new();
    private readonly ConcurrentDictionary<(int Map, int Room, string Direction), RoomExitState> _roomExitStates = new();
    private readonly ConcurrentDictionary<string, ExitAccessState> _roomExitAccessStates = new();
    private readonly object _partyLock = new();
    private readonly Dictionary<string, PartyState> _parties = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _partyLeaderByMember = new(StringComparer.OrdinalIgnoreCase);
    // Pending party (follow) invites, keyed by invitee -> the set of leaders who have invited them. A
    // player can hold invites from MULTIPLE leaders at once: stock keeps each
    // invitee in the inviting leader's own group slots and never blocks a second leader from also
    // inviting (the conflict is resolved at FOLLOW time). A single-valued map here was the cause of
    // bug #161 ("cannot invite someone already invited by somebody else").
    private readonly Dictionary<string, HashSet<string>> _partyInvitesByInvitee = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _dragLock = new();
    private readonly Dictionary<string, string> _dragTargetByDragger = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _draggerByTarget = new(StringComparer.OrdinalIgnoreCase);

    // Three independent timers mirroring the stock self-rescheduling callbacks
    // (the fast / medium / slow background passes). The
    // single bundled WorldTick was retired in favor of the stock fast/medium/slow cadence.
    private Timer? _fastTimer;
    private Timer? _mediumTimer;
    private Timer? _slowTimer;
    private Timer? _roomSpellTimer;
    private Timer? _combatTimer;

    // The single game-state mutex — our faithful stand-in for the stock one cooperative thread.
    // The combat round and user-command processing never overlap in stock
    // the original because there is only one thread. We reproduce that exactly: the combat beat holds this
    // for the WHOLE round (every player swing + every monster swing + finalize) and each command holds
    // it around its in-memory processing. So a monster attack is never dropped on contention and a
    // player's combat state is never mutated by a command and the round at the same instant. Heavy I/O
    // (the buffered command output's socket flush) is done outside it; see GameSession.
    public SemaphoreSlim WorldStateGate { get; } = new(1, 1);

    // _mediumTickCount inherits the old 3s WorldTick period and drives the existing %5 (monster
    // movement) / %10 (cleanup, ambient) sub-gates, so those effective cadences are unchanged.
    private int _mediumTickCount;
    private int _slowTickCount;
    private int _fastTickInProgress;
    private int _mediumTickInProgress;
    private int _slowTickInProgress;
    private int _roomSpellTickInProgress;
    private bool _useManualRoomSpellTicksForTests;
    private DateTime? _roomSpellTestNowUtc;
    private readonly DateTime _combatPulseEpochUtc;

    private const int MaxMonstersPerRoom = 15;
    // A roll of 1..99 < 6 per (player-room × exit) per tick. We use the
    // same outer gate; the inner ShouldSpawnPressureRoom then applies the RoomType pressure %.
    private const int AdjacentSpawnChancePercent = 5;
    private const int AmbientPressureSpawnRoomType = 0;
    private const int InstantPressureSpawnRoomType = 2;
    private const int TrueLairSpawnRoomType = 3;
    private const int AmbientPressureSpawnChancePercent = 5;
    private const int InstantPressureSpawnChancePercent = 90;
    private const int OvercrowdedPressureSpawnChancePercent = 1;
    private const int BackgroundSpawnCycleCap = 9;
    private const int MaxScheduledTrueLairRoomsPerTick = 4;
    private const int MaxScheduledTrueLairRoomScansPerTick = 64;
    private const int RoomSpellTriggerTextBlockAbility = 148;
    private const int TeleportRoomAbilityId = 140;
    private const int TeleportMapAbilityId = 141;
    private const int SilverRiverSpellId = 753;
    private const int SilverRiverBashMessageId = 2096;
    private const int SilverRiverLogRaftItemId = 690;
    private const int SilverRiverWoodenSkiffItemId = 691;
    private const int SilverRiverSilverbarkCanoeItemId = 1181;
    private static readonly TimeSpan RoomSpellTimerInterval = TimeSpan.FromSeconds(1);
    // Stock tick periods read from the shipped binary,
    // driving the fast / medium / slow background passes respectively.
    private static readonly TimeSpan FastTickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MediumTickInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SlowTickInterval = TimeSpan.FromSeconds(30);
    // Fast-tick counters: rest HP regen fires when the rest counter > 20
    // ⇒ ≈ every 20 fast (1s) ticks; meditate mana regen fires when its counter > 14 ⇒ ≈ every 15.
    private const int RestRegenFastTicks = 20;
    private const int MeditateRegenFastTicks = 14;
    // Flush ground items/currency to PG every N slow (30s) ticks so an ungraceful kill/crash loses at
    // most ~this many minutes of dropped loot. 8 × 30s = 4 minutes.
    private const int GroundStatePersistSlowTicks = 8;
    // Combat round = one background-energy tick (the autocombat pass runs once per
    // background-energy tick). That pass re-kicks every N scheduler ticks, and
    // one scheduler tick = the fast-pass period (the scheduler unit is
    // seconds). Read from the shipped binary: N=5, fast=1s ⇒ 5×1 = 5s. (Cross-checked
    // by the same read giving medium=3s/slow=30s, matching the verified tick scheduler.) Was 4s on
    // unverified "stock lore"; corrected to the faithful 5s.
    private static readonly TimeSpan CombatPulseInterval = TimeSpan.FromSeconds(5);
    // The room cast runs from the medium character update on every other medium tick (a
    // global toggle flag flips once per medium pass),
    // so the effective rate is 2 × medium tick. The stock binary's default medium-tick period
    // is 3s (verified from the shipped binary: slow=30s, medium=3s, fast=1s), so room
    // spells fire every 6s — matching observed stock (~5-6s between bashes). Applied
    // uniformly: there is no per-room interval in stock.
    private static readonly TimeSpan RoomSpellPulseInterval = TimeSpan.FromSeconds(6);
    private const int NoLairSpawnDelayRoomType = InstantPressureSpawnRoomType;
    private const int MaxPartyMembers = 6;
    // Evil-point forgiveness (a mmudreborn feature) runs on the 30s slow tick, aligned with the
    // stock evil-timer decrement on the slow pass. 2 slow ticks = 60s = one cycle minute.
    private const int SlowTicksPerMinute = 2;
    // The slow monster update regenerates a monster's HP by its per-monster rate
    // (HPRegen) once per global "slow" cycle, NOT every world tick. The
    // slow updater round-robins exactly one monster slot per slow tick, so every
    // monster is visited once per full pass over the slot table — a fixed, world-wide cadence
    // independent of the individual monster (only the HPRegen *amount* varies). Megamud surfaces
    // this cadence as e.g. "Regens: 90 HPs every 90 seconds [18 rounds]" (chimera, HPRegen=90).
    // Monster regen runs on the medium tick (3s), so 90s = 30 medium ticks. Applying HPRegen every
    // tick (the prior behavior) healed ~30x too fast; we gate it to one application per interval.
    private const int MonsterHpRegenIntervalTicks = 30;

    public GameWorld(IGameDatabase database, IPlayerRepository playerRepo, IBbsUserRepository? bbsUserRepo = null, IBbsCommandDispatcher? bbsCommandDispatcher = null)
    {
        _combatPulseEpochUtc = DateTime.UtcNow;
        _database = database;
        PlayerRepo = playerRepo;
        BbsUserRepo = bbsUserRepo;
        BbsCommandDispatcher = bbsCommandDispatcher ?? NoOpBbsCommandDispatcher.Instance;
        _incomingMovementExitsByTargetRoom = BuildIncomingMovementExitIndex(database);
        EnsureSysopTestingRooms();
        _boundNpcRoomsByMonsterNumber = database.Rooms.Values
            .Where(room => room.NPC > 0)
            .GroupBy(room => room.NPC)
            .ToDictionary(
                group => group.Key,
                group => group.Select(room => (room.MapNumber, room.RoomNumber)).ToHashSet());
        PvpLevelRange = Math.Max(0, PlayerRepo.GetServerSettingInt("PvpLevelRange", 9));
        DeathSetting = Player.NormalizeDeathHP(PlayerRepo.GetServerSettingInt("DeathSetting", Player.DefaultDeathHP));
        Player.ConfigureDeathHP(DeathSetting);
        MonsterExperienceRate = Math.Max(1, PlayerRepo.GetServerSettingInt("MonsterExperienceRate", 1));
        RerollKeepExperiencePercent = Math.Clamp(PlayerRepo.GetServerSettingInt("RerollKeepExperiencePercent", 0), 0, 100);
        MonsterGenerationRateSeconds = Math.Clamp(PlayerRepo.GetServerSettingInt("MonsterGenRateSeconds", 5), 1, 3600);
        LairRegenDefaultMinutes = Math.Clamp(PlayerRepo.GetServerSettingInt("LairRegenMinutes", 5), 0, 1440);
        ActiveLairSpawnRoomRadius = Math.Clamp(PlayerRepo.GetServerSettingInt("SpawnBubbleRadius", 10), 1, 60);
        LevelAheadCap = Math.Clamp(PlayerRepo.GetServerSettingInt("LevelAheadCap", 0), 0, MaxLevelAheadCap);
        GangCreateRunicCost = Math.Max(0, PlayerRepo.GetServerSettingInt("GangCreateRunicCost", 0));
        GangCreateMinimumExperience = Math.Max(0, PlayerRepo.GetServerSettingInt("GangCreateMinimumExperience", 100000));
        GangHouseMinimumExperience = Math.Max(0, PlayerRepo.GetServerSettingInt("GangHouseMinimumExperience", 0));
        LimitedItemsMode = Math.Clamp(PlayerRepo.GetServerSettingInt("LimitedItemsMode", 0), 0, 1);
        // Default ON = stock (17 visible / 15 hidden per room).
        GroundItemLimitEnabled = PlayerRepo.GetServerSettingInt("GroundItemLimitEnabled", 1) != 0;
        EvilCapBlocksActions = PlayerRepo.GetServerSettingInt("EVILCAPBLOCK", 1) != 0;
        SurpriseRoundEnabled = PlayerRepo.GetServerSettingInt("SURPRISEROUND", 0) != 0;
        QuestDropToAllParty = PlayerRepo.GetServerSettingInt("QUESTALLPARTY", 0) != 0;
        LoadQolSettings();
        LoadBugTrackerSetting();
        MinEvilPointsEnabled = PlayerRepo.GetServerSettingInt("MINEPS", 0) != 0;
        MaxEvilPointsForgivenPerDay = Math.Max(0, PlayerRepo.GetServerSettingInt("MAXEPDAY", 0));
        EvilPointForgivenessCycleMinutes = Math.Max(0, PlayerRepo.GetServerSettingInt("EPCYCLE", 0));
        EvilPointForgivenessAmount = Math.Max(0, PlayerRepo.GetServerSettingInt("EPAMOUNT", 0));
        LoadDisconnectPenaltySettings();
        LoadDeathLogSetting();
        string cleanupTimeSetting = PlayerRepo.GetServerSettingText("CleanupTime", "04:00");
        if (TimeOnly.TryParse(cleanupTimeSetting, out var parsedCleanupTime))
            DailyCleanupTime = parsedCleanupTime;
        _nextCleanupAtUtc = CalculateNextCleanupUtc(DailyCleanupTime);
        string lastCleanupSetting = PlayerRepo.GetServerSettingText("LastCleanupUtc", string.Empty);
        if (DateTime.TryParse(lastCleanupSetting, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsedLastCleanup))
            _lastCleanupAtUtc = parsedLastCleanup;
        LoadPersistedRoomGroundState();
        LoadPersistedMonsterRegenState();
        LoadGangHouses();
        LoadGangShops();
        // Shops seed Current=Max on database load; overlay any persisted depletion so purchases survive a
        // restart. After gang/deed shop setup so those managed shops aren't touched.
        LoadPersistedShopStock();
    }

    private static DateTime CalculateNextCleanupUtc(TimeOnly cleanupTime)
    {
        var now = DateTime.UtcNow;
        var todayCleanup = now.Date.Add(cleanupTime.ToTimeSpan());
        return todayCleanup > now ? todayCleanup : todayCleanup.AddDays(1);
    }

    public void SetDailyCleanupTime(TimeOnly time)
    {
        DailyCleanupTime = time;
        _nextCleanupAtUtc = CalculateNextCleanupUtc(time);
        PlayerRepo.SetServerSettingText("CleanupTime", time.ToString("HH:mm"));
    }

    private static Dictionary<(int Map, int Room), IReadOnlyList<IncomingMovementExit>> BuildIncomingMovementExitIndex(IGameDatabase database)
    {
        var incomingByTarget = new Dictionary<(int Map, int Room), List<IncomingMovementExit>>();

        foreach (var room in database.Rooms.Values)
        {
            var exitTypesByDirection = room.GetExitDefinitions().Values
                .GroupBy(definition => definition.Direction, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ExitType, StringComparer.OrdinalIgnoreCase);

            foreach (var (direction, exit) in room.GetExits())
            {
                var target = (exit.Map, exit.Room);
                if (!incomingByTarget.TryGetValue(target, out var incoming))
                {
                    incoming = [];
                    incomingByTarget[target] = incoming;
                }

                incoming.Add(new IncomingMovementExit(
                    room.MapNumber,
                    room.RoomNumber,
                    direction,
                    CarriesMovementNoise: MovementNoiseExitTypes.Contains(exitTypesByDirection.GetValueOrDefault(direction, RoomExitType.Normal))));
            }
        }

        return incomingByTarget.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<IncomingMovementExit>)pair.Value);
    }

    public bool IsSysopTestingRoom(int mapNumber, int roomNumber)
    {
        return mapNumber == SysopTestingRoomMapNumber && SysopTestingRoomNumbers.Contains(roomNumber);
    }

    public static bool TryResolveSysopTestingRoomAlias(string input, out int roomNumber)
    {
        roomNumber = 0;
        return SysopTestingRoomAliases.TryGetValue(input.Trim(), out roomNumber);
    }

    public DateTime GetNextCombatPulseUtc(DateTime? afterUtc = null)
    {
        return GetNextCombatPulseUtc(_combatPulseEpochUtc, afterUtc ?? DateTime.UtcNow);
    }

    public static DateTime GetNextCombatPulseUtc(DateTime combatPulseEpochUtc, DateTime afterUtc)
    {
        if (afterUtc < combatPulseEpochUtc)
            return combatPulseEpochUtc;

        long elapsedTicks = afterUtc.Ticks - combatPulseEpochUtc.Ticks;
        long pulseCount = (elapsedTicks / CombatPulseInterval.Ticks) + 1;
        return combatPulseEpochUtc.AddTicks(pulseCount * CombatPulseInterval.Ticks);
    }

    private void EnsureSysopTestingRooms()
    {
        foreach (var definition in SysopTestingRooms)
        {
            var key = (SysopTestingRoomMapNumber, definition.RoomNumber);
            if (Database.Rooms.ContainsKey(key))
                continue;

            Database.Rooms[key] = new Room
            {
                MapNumber = SysopTestingRoomMapNumber,
                RoomNumber = definition.RoomNumber,
                Name = definition.Name,
                Description = definition.Description,
                Light = 0,
                Shop = 0,
                NPC = 0,
                CMD = 0,
                Spell = 0,
                MaxRegen = 0,
                MonsterType = 0,
                MinIndex = 0,
                MaxIndex = 0,
                Delay = 0,
                Placed = "",
                HiddenItems = "",
                GroundCurrency = 0,
                DeathRoom = 0,
                N = "0",
                S = "0",
                E = "0",
                W = "0",
                NE = "0",
                NW = "0",
                SE = "0",
                SW = "0",
                U = "0",
                D = "0",
            };
        }
    }

    // `configure GENRATE <seconds>`: how often the background spawn driver runs.
    public void SetMonsterGenerationRateSeconds(int value)
    {
        MonsterGenerationRateSeconds = Math.Clamp(value, 1, 3600);
        PlayerRepo.SetServerSettingInt("MonsterGenRateSeconds", MonsterGenerationRateSeconds);
    }

    // `configure MINWAIT <minutes>`: default per-room respawn delay when the room's Delay is 0.
    public void SetLairRegenDefaultMinutes(int value)
    {
        LairRegenDefaultMinutes = Math.Clamp(value, 0, 1440);
        PlayerRepo.SetServerSettingInt("LairRegenMinutes", LairRegenDefaultMinutes);
    }

    // Spawn-bubble BFS radius (rooms). A live change immediately rebuilds every online player's bubble so
    // the new coverage takes effect at once (the force path runs the BFS synchronously; radius changes are
    // a rare admin op, so the brief gate-free rebuild is fine).
    public void SetActiveLairSpawnRoomRadius(int value)
    {
        ActiveLairSpawnRoomRadius = Math.Clamp(value, 1, 60);
        PlayerRepo.SetServerSettingInt("SpawnBubbleRadius", ActiveLairSpawnRoomRadius);
        foreach (var player in _onlinePlayers.Values)
            RefreshPlayerSpawnBubbleIfNeeded(player, force: true);
    }

    public void SetGangCreateRunicCost(int value)
    {
        GangCreateRunicCost = Math.Max(0, value);
        PlayerRepo.SetServerSettingInt("GangCreateRunicCost", GangCreateRunicCost);
    }

    public void SetGangCreateMinimumExperience(int value)
    {
        GangCreateMinimumExperience = Math.Max(0, value);
        PlayerRepo.SetServerSettingInt("GangCreateMinimumExperience", GangCreateMinimumExperience);
    }

    public void SetGangHouseMinimumExperience(int value)
    {
        GangHouseMinimumExperience = Math.Max(0, value);
        PlayerRepo.SetServerSettingInt("GangHouseMinimumExperience", GangHouseMinimumExperience);
    }

    public void SetHost(IBbsHost host)
    {
        _host = host;
    }

    public const int RestartExitCode = 42;
    public const int ShutdownExitCode = 0;

    // Sysop-triggered process restart (exit 42): the supervisor relaunches the latest build, so a
    // committed code change goes live. The host's callback performs the graceful save before exit.
    public void RequestRestart(string reason)
    {
        OnShutdownRequested?.Invoke(RestartExitCode, reason);
    }

    // Sysop-triggered full stop (exit 0): the supervisor does not relaunch.
    public void RequestShutdown(string reason)
    {
        OnShutdownRequested?.Invoke(ShutdownExitCode, reason);
    }

    // In-process game-data reload (sysop `reloaddata`): swap the in-memory database for a freshly
    // loaded one and rebuild the caches derived from it, with no player disconnects. Covers the common
    // case (message/item/spell/monster edits — plain dictionary lookups) and room-exit indexes.
    // Returns false when no reloader is wired (use `sys restart`). For structural room-set changes
    // (new static room items in already-visited rooms), prefer `sys restart` for a clean rebuild.
    public bool ReloadGameData()
    {
        var reloader = GameDataReloader;
        if (reloader == null)
            return false;

        IGameDatabase fresh = reloader();

        // Rebuild constructor-derived caches against the fresh database, then publish it. Reference
        // assignments are atomic; the brief window where an index and _database differ is acceptable
        // for a rare admin action.
        var incoming = BuildIncomingMovementExitIndex(fresh);
        var boundNpcRooms = fresh.Rooms.Values
            .Where(room => room.NPC > 0)
            .GroupBy(room => room.NPC)
            .ToDictionary(
                group => group.Key,
                group => group.Select(room => (room.MapNumber, room.RoomNumber)).ToHashSet());

        _incomingMovementExitsByTargetRoom = incoming;
        _boundNpcRoomsByMonsterNumber = boundNpcRooms;
        _database = fresh;
        EnsureSysopTestingRooms();
        return true;
    }

    public IGameClient? GetClientForPlayer(string playerName)
    {
        return _onlinePlayers.TryGetValue(playerName, out var player) ? player.Client : null;
    }

    public IReadOnlyList<BbsPresenceSnapshot> GetBbsPresenceSnapshots()
    {
        return _host?.GetBbsPresenceSnapshots() ?? [];
    }

    /// <summary>
    /// Stock "warp escape" — applied when a player LEAVES the game. The Warped Asylum (map 9),
    /// Mirrored Hall (map 3) and the other disorienting maze areas tag every maze room with a
    /// non-zero <see cref="Room.ExitRoom"/>. Stock rewrites the player's saved current-room to that
    /// ExitRoom (same area) on the way out — both on hang-up/disconnect
    /// and on a graceful quit — so the player reappears at the maze's
    /// designated exit on their next login and can walk back out. This is the documented "stuck in the
    /// maze? log off and back on" escape. Call it immediately before persisting the character on leave.
    /// No-op for ordinary rooms (ExitRoom == 0). Idempotent: the destination room's own ExitRoom is 0,
    /// so repeated calls (multiple leave chokepoints) settle after the first.
    /// </summary>
    public void ApplyWarpExitRoomOnLeave(Player player)
    {
        var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (room is null || room.ExitRoom <= 0 || room.ExitRoom == player.CurrentRoomNumber)
            return;

        player.CurrentRoomNumber = room.ExitRoom;
    }

    public void AddPlayer(Player player)
    {
        // Recompute derived stats and clamp CurrentHP/Mana to their maxima on login so a
        // character whose stored HP drifted above MaxHP (e.g. a sysop race/class swap while
        // offline) is corrected on relog instead of showing "161/153". MaxHP itself is NOT
        // re-rolled here (RecalculatePlayerStats only clamps current values to the stored max).
        RecalculatePlayerStats(player);
        RestorePlayerItemRuntimeState(player);
        RechargePlayerItemsIfNeeded(player);
        _onlinePlayers[player.Name] = player;
        // Seed the movement trail with the spawn room (stock records it as the newest slot on placement)
        // so a tracker can resolve the direction of this player's very first move out of it.
        player.RecordMovementTrail(player.CurrentMapNumber, player.CurrentRoomNumber);
        ResetPlayerEvilPointForgivenessTimer(player.Name);
        NotifyPlayerEnteredRoom(player);

        if (player.BroadcastChannel > 0)
            AnnounceBroadcastChannelJoin(player, player.BroadcastChannel);

        PublishOnlinePresence();
    }

    public void RenameOnlinePlayer(string oldName, Player player)
    {
        _onlinePlayers.TryRemove(oldName, out _);
        _playerRoomSpellStates.TryRemove(oldName, out _);
        _playerNextEvilPointForgivenessTick.TryRemove(oldName, out _);
        RemovePlayerSpawnBubble(oldName);
        _onlinePlayers[player.Name] = player;
        ResetPlayerEvilPointForgivenessTimer(player.Name);
        NotifyPlayerEnteredRoom(player);
        PublishOnlinePresence();
    }

    public enum RenamePlayerOutcome { Success, NotFound, NameTaken, InvalidName }

    private const int MinRenameNameLength = 2;
    private const int MaxRenameNameLength = 15;

    // Rename a character everywhere — ONLINE or OFFLINE — and persist it. Renames the Players row + every
    // DB reference (PlayerRepo.RenamePlayer), re-points the BBS account link so login still resolves the
    // (renamed) character on next logon — this does NOT change the BBS account's own login name — and
    // migrates the transient in-memory state if the player is online (online roster + party/drag maps).
    public RenamePlayerOutcome RenamePlayer(string oldName, string newName)
    {
        oldName = Player.NormalizeNamePart(oldName);
        newName = Player.NormalizeNamePart(newName);

        if (string.IsNullOrWhiteSpace(newName)
            || newName.Length is < MinRenameNameLength or > MaxRenameNameLength
            || NameEquals(newName, oldName))
            return RenamePlayerOutcome.InvalidName;

        var online = FindOnlinePlayer(oldName);
        if (online == null && !PlayerRepo.PlayerExists(oldName))
            return RenamePlayerOutcome.NotFound;
        if (PlayerRepo.PlayerExists(newName))
            return RenamePlayerOutcome.NameTaken;

        if (!PlayerRepo.RenamePlayer(oldName, newName))
            return RenamePlayerOutcome.NameTaken;   // lost a race for the name

        // The account→character link needs no update on rename: it lives on the player row as BbsUserId,
        // which RenamePlayer never touches, so the renamed character still resolves for its owner at next
        // login. (Previously the link was a player NAME stored BBS-side, which had to be re-pointed here.)
        RenamePartyAndDragReferences(oldName, newName);

        if (online != null)
        {
            online.Name = newName;
            RenameOnlinePlayer(oldName, online);
            PlayerRepo.SavePlayer(online);
        }

        return RenamePlayerOutcome.Success;
    }

    private static bool NameEquals(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Migrate a renamed player's name through the transient in-memory party/drag maps so an ONLINE rename
    // doesn't orphan their membership. Offline players are not present in these maps, so this is a no-op.
    private void RenamePartyAndDragReferences(string oldName, string newName)
    {
        lock (_partyLock)
        {
            // PartyState.LeaderName is init-only — if oldName led a party, rebuild it under newName.
            if (_parties.Remove(oldName, out var led))
            {
                var rebuilt = new PartyState { LeaderName = newName };
                foreach (var m in led.Members) rebuilt.Members.Add(NameEquals(m, oldName) ? newName : m);
                foreach (var kv in led.Ranks) rebuilt.Ranks[NameEquals(kv.Key, oldName) ? newName : kv.Key] = kv.Value;
                foreach (var inv in led.Invited) rebuilt.Invited.Add(NameEquals(inv, oldName) ? newName : inv);
                _parties[newName] = rebuilt;
            }

            foreach (var party in _parties.Values)
            {
                for (int i = 0; i < party.Members.Count; i++)
                    if (NameEquals(party.Members[i], oldName)) party.Members[i] = newName;
                if (party.Ranks.Remove(oldName, out var rank)) party.Ranks[newName] = rank;
                if (party.Invited.Remove(oldName)) party.Invited.Add(newName);
            }

            RenameMapKeyAndValues(_partyLeaderByMember, oldName, newName);
            RenamePartyInviteMap(_partyInvitesByInvitee, oldName, newName);
        }

        lock (_dragLock)
        {
            RenameMapKeyAndValues(_dragTargetByDragger, oldName, newName);
            RenameMapKeyAndValues(_draggerByTarget, oldName, newName);
        }
    }

    private static void RenameMapKeyAndValues(Dictionary<string, string> map, string oldName, string newName)
    {
        if (map.Remove(oldName, out var value))
            map[newName] = NameEquals(value, oldName) ? newName : value;
        foreach (var key in map.Keys.ToList())
            if (NameEquals(map[key], oldName)) map[key] = newName;
    }

    // Rename helper for the invitee -> {leaders} party-invite multi-map: rename the invitee key AND
    // rename the old name anywhere it appears as an inviting leader inside the value sets.
    private static void RenamePartyInviteMap(Dictionary<string, HashSet<string>> map, string oldName, string newName)
    {
        if (map.Remove(oldName, out var leaders))
            map[newName] = leaders;
        foreach (var set in map.Values)
            if (set.Remove(oldName)) set.Add(newName);
    }

    public List<Player> GetAllOnlinePlayers()
    {
        return _onlinePlayers.Values.ToList();
    }

    // True while this character is a live, in-realm session. Used by off-gate interactive commands (the
    // TRAIN STATS editor) to confirm — under the world gate, before committing — that the player wasn't
    // removed during the off-gate input wait (permadeath on the last life, or a dropped connection). The
    // write-behind flusher makes the same membership check before persisting (see FlushPendingPlayerSaves).
    public bool IsPlayerOnline(string name) => _onlinePlayers.ContainsKey(name);

    // When a gang LEADER's character is destroyed (reroll, or permadeath on the last life), the whole
    // gang dissolves — every member is cleared and the gang settings/invites are deleted, mirroring a
    // leader-issued DISBAND. A non-leader's removal leaves the gang intact (the caller just drops that
    // one member). Returns the disbanded gang's name, or null when the player was not a gang leader.
    // (Gang-house deeds are not modelled yet — there is no ganghouse ownership/deed pool to return to.)
    public string? DisbandGangIfLeader(Player player)
    {
        if (string.IsNullOrWhiteSpace(player.Gang) || !PlayerRepo.IsGangLeader(player.Name, player.Gang))
            return null;

        string gangName = player.Gang;
        PlayerRepo.DisbandGang(gangName);

        foreach (var member in GetAllOnlinePlayers()
                     .Where(p => p.Gang.Equals(gangName, StringComparison.OrdinalIgnoreCase)))
        {
            member.Gang = string.Empty;
            member.GangExperience = 0;
            member.IsGangLieutenant = false;
            if (!member.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
                SendToPlayer(member.Name, $"The gang {gangName} has now been disbanded.");
        }

        player.Gang = string.Empty;
        player.GangExperience = 0;
        player.IsGangLieutenant = false;
        return gangName;
    }

    public Player? FindOnlinePlayer(string nameOrPrefix)
        => ResolvePlayerByNameOrPrefix(_onlinePlayers.Values, nameOrPrefix);

    // Resolve a player from a candidate set by name, preferring an EXACT (case-insensitive) name over a
    // prefix match — otherwise typing "duhh" could resolve to "Duhhsucks" (a prefix hit) instead of the
    // player literally named "Duhh". Only with no exact match do we fall back to prefix matching, choosing
    // the shortest (closest) candidate, so "duhh" → Duhh while "duhhs" (not a prefix of Duhh) → Duhhsucks.
    public static Player? ResolvePlayerByNameOrPrefix(IEnumerable<Player> candidates, string nameOrPrefix)
    {
        var list = candidates as IReadOnlyCollection<Player> ?? candidates.ToList();

        var exact = list.FirstOrDefault(p => p.Name.Equals(nameOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        return list
            .Where(p => p.Name.StartsWith(nameOrPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name.Length)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public bool SendChannelMessage(string senderName, string recipientName, string message, bool reprompt = true, bool prependLineBreak = true)
    {
        if (!_onlinePlayers.TryGetValue(recipientName, out var recipient))
            return false;

        if (recipient.IgnoredPlayerNames.Contains(senderName))
            return false;

        DeliverToClient(recipient, message, reprompt, prependLineBreak);
        return true;
    }

    public bool IsValidBroadcastChannel(int channel)
    {
        return channel >= 0 && channel <= MaxBroadcastChannelNumber;
    }

    public List<Player> GetPlayersOnBroadcastChannel(int channel)
    {
        if (channel <= 0)
            return [];

        return _onlinePlayers.Values
            .Where(player => player.BroadcastChannel == channel)
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<string> GetBroadcastChannelMemberNames(int channel)
    {
        return GetPlayersOnBroadcastChannel(channel)
            .Select(player => player.Name)
            .ToList();
    }

    public void BroadcastToPlayerChannel(int channel, string senderName, string message, bool reprompt = true, bool prependLineBreak = true)
    {
        if (channel <= 0)
            return;

        foreach (var recipient in _onlinePlayers.Values)
        {
            if (recipient.BroadcastChannel != channel)
                continue;

            if (recipient.Name.Equals(senderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (recipient.IgnoredPlayerNames.Contains(senderName))
                continue;

            DeliverToClient(recipient, message, reprompt, prependLineBreak);
        }
    }

    public void AnnounceBroadcastChannelJoin(Player player, int channel)
    {
        if (channel <= 0)
            return;

        BroadcastToPlayerChannel(
            channel,
            player.Name,
            $"{MudAnsi.BrightYellow}{player.Name} just joined your channel ({channel}){MudAnsi.Reset}",
            reprompt: true,
            prependLineBreak: true);
    }

    public void AnnounceBroadcastChannelLeave(Player player, int channel)
    {
        if (channel <= 0)
            return;

        BroadcastToPlayerChannel(
            channel,
            player.Name,
            $"{MudAnsi.BrightYellow}{player.Name} just left your channel ({channel}){MudAnsi.Reset}",
            reprompt: true,
            prependLineBreak: true);
    }

    public void BroadcastChannelToRealm(string senderName, string message, IGameClient? except = null, bool reprompt = false, bool prependLineBreak = true, Func<Player, bool>? canReceive = null, bool includeSender = false)
    {
        foreach (var recipient in _onlinePlayers.Values)
        {
            // Native gossip excludes the sender (their telnet client echoes locally). An externally
            // injected broadcast (from the web) has no local echo, so includeSender delivers it to the
            // sender's own online character too — otherwise a solo web-gossiper sees nothing in-game.
            if (!includeSender && recipient.Name.Equals(senderName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (except?.Player != null && recipient.Name.Equals(except.Player.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (recipient.IgnoredPlayerNames.Contains(senderName))
                continue;

            if (canReceive != null && !canReceive(recipient))
                continue;

            DeliverToClient(recipient, message, reprompt, prependLineBreak);
        }
    }

    /// <summary>
    /// Walk every terminal
    /// and, for anyone whose autocombat is targeting <paramref name="victim"/>, stop the loop for
    /// them (they also get the "*Combat Off*" break line).
    ///
    /// The death path calls this the moment a player dies, which is what stops the rest of the room
    /// swinging at a corpse. Without it an attacker keeps a stale target pointer and lands a full extra
    /// round on the victim AFTER they have already respawned — including inside the protected
    /// death-recall room, where no blow should ever land.
    /// </summary>
    public void KillAutocombatAgainstPlayer(Player victim)
    {
        foreach (var other in _onlinePlayers.Values)
        {
            if (ReferenceEquals(other, victim) || !ReferenceEquals(other.PlayerCombatTarget, victim))
                continue;

            other.PlayerCombatTarget = null;
            other.StopCombatLoop();
            // The break line is emitted: the attacker is told their
            // combat ended rather than silently losing their target.
            SendToPlayer(other.Name, GameAnsi.CombatOff("*Combat Off*"));
        }
    }

    public void RemovePlayer(Player player, bool save = true)
    {
        // Warp-maze escape: rewrite the saved room to the maze's ExitRoom on the
        // way out, before the persist below. No-op outside the Asylum/Mirrored Hall/etc.
        ApplyWarpExitRoomOnLeave(player);
        CaptureOfflinePlayerItemRuntimeState(player);
        StopDraggingForPlayer(player);
        RemovePlayerFromParty(player);
        // Summoned "angels" vanish when their
        // owner leaves the realm; charmed creatures revert to wild.
        DismissPlayerPets(player.Name);

        if (player.BroadcastChannel > 0)
            AnnounceBroadcastChannelLeave(player, player.BroadcastChannel);

        _onlinePlayers.TryRemove(player.Name, out _);
        _playerRoomSpellStates.TryRemove(player.Name, out _);
        _playerNextEvilPointForgivenessTick.TryRemove(player.Name, out _);
        RemovePlayerSpawnBubble(player.Name);
        // Drop any pending write-behind mark: the synchronous save below is the authoritative final state,
        // and (since the player is now offline) the flusher would skip them regardless.
        ClearPendingPlayerSave(player.Name);

        if (save)
            PlayerRepo.SavePlayer(player);

        // Unbind the connection last: the player is now offline, so routing must no longer find it.
        player.Client = null;

        PublishOnlinePresence();
    }

    public void NotifyPlayerEnteredRoom(Player player)
    {
        // Hidden ground contents are re-hidden the moment you change rooms: a reveal earned by
        // searching only lasts for the current visit, so leaving (or returning) drops what you saw.
        player.ClearRevealedHidden();

        var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (room == null)
        {
            _playerRoomSpellStates.TryRemove(player.Name, out _);
            return;
        }

        EnsureLargeTombQuestRoomState(room);
        EnsureRoomNpcPresent(room, player);
        FillTrueLairOnEnter(room, player);
        RefreshPlayerSpawnBubbleIfNeeded(player);

        // Room-spell pulse schedule. Stock casts the CURRENT room's spell every medium tick
        // with NO per-player accumulation timer, so movement can
        // NEVER dodge it — a player swimming the Silver River is bashed against the rocks exactly like
        // one sitting still. We mirror that by NOT resetting the pulse clock on movement: only
        // (re)initialise it when the player has no schedule yet or genuinely crosses into a DIFFERENT
        // room spell. A no-spell room PAUSES the schedule (it resumes on re-entry) rather than clearing
        // it — clearing here is what let a mover reset the 6s timer every step and never get hit (and
        // would still let one dodge by hopping onto the bank for a single step).
        if (room.Spell <= 0)
            return;

        DateTime now = GetCurrentRoomSpellTimeUtc();
        TimeSpan pulseInterval = GetRoomSpellPulseInterval(room);

        bool crossedIntoNewSpell = false;
        _playerRoomSpellStates.AddOrUpdate(
            player.Name,
            _ =>
            {
                crossedIntoNewSpell = true;
                return new PlayerRoomSpellState
                {
                    SpellId = room.Spell,
                    NextPulseAtUtc = now.Add(pulseInterval),
                    PulseCount = 0,
                };
            },
            (_, state) =>
            {
                if (state.SpellId != room.Spell)
                {
                    crossedIntoNewSpell = true;
                    state.SpellId = room.Spell;
                    state.PulseCount = 0;
                    state.NextPulseAtUtc = now.Add(pulseInterval);
                }

                return state;
            });

        // A destination room with the cast-on-entry attribute bit casts
        // its room Spell IMMEDIATELY on arrival when it differs from the previous room's spell (331
        // stock rooms) — the pulse schedule then continues from here. Our paused schedule holds the
        // LAST spell-room's id, so the only divergence is re-entering the same spell room via a
        // spell-less detour: stock re-casts on arrival, we resume the pending pulse — invisible, since
        // the active-spell slot persists across the detour.
        if (crossedIntoNewSpell && room.HasAttribute(RoomAttributes.Bit6))
            CastRoomSpellOnPlayer(player, room.Spell);
    }

    private static bool IsLargeTombRoom(Room room)
        => room.Name.Equals("Large Tomb", StringComparison.OrdinalIgnoreCase);

    private bool TryGetLargeTombQuestWeaponId(Room room, out int itemId)
    {
        itemId = 0;
        if (!IsLargeTombRoom(room))
            return false;

        var candidateIds = room.GetPlacedItemIds()
            .Where(candidateId =>
                Database.Items.TryGetValue(candidateId, out var item) &&
                item.Gettable &&
                item.Limit == 1 &&
                item.ClassRestrictions.Any(classId => classId > 0))
            .Distinct()
            .ToList();

        if (candidateIds.Count != 1)
            return false;

        itemId = candidateIds[0];
        return true;
    }

    private void EnsureLargeTombQuestRoomState(Room room)
    {
        if (!TryGetLargeTombQuestWeaponId(room, out var questWeaponItemId))
            return;

        EnsureSingleLargeTombQuestWeapon(room, questWeaponItemId);
    }

    private void EnsureSingleLargeTombQuestWeapon(Room room, int itemId)
    {
        EnsureStaticGroundItemsInitialized(room.MapNumber, room.RoomNumber);

        var key = (room.MapNumber, room.RoomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items))
            {
                items = [];
                _roomGroundItems[key] = items;
            }

            var matchingVisibleIndexes = items
                .Select((entry, index) => (entry, index))
                .Where(candidate => !candidate.entry.IsHidden && candidate.entry.ItemId == itemId)
                .ToList();

            if (matchingVisibleIndexes.Count == 0)
            {
                items.Insert(0, new GroundItemEntry
                {
                    ItemId = itemId,
                    IsHidden = false,
                    InstanceId = CreateItemInstance(itemId),
                    IsStaticSeeded = true,
                });
                return;
            }

            for (int matchIndex = matchingVisibleIndexes.Count - 1; matchIndex >= 1; matchIndex--)
            {
                var duplicate = matchingVisibleIndexes[matchIndex];
                items.RemoveAt(duplicate.index);
                _itemRuntimeStates.TryRemove(duplicate.entry.InstanceId, out _);
            }
        }
    }

    /// <summary>
    /// Faithful spawn-on-enter for a room's primary NPC (Room.NPC).
    /// The entry gate spawns this monster on every room entry when the
    /// room's "primary present" latch is clear, and the spawn sets the
    /// latch; the kill clears it on the primary's death. Here the latch is simply
    /// "is a live instance of room.NPC present?", so this never spawns a second one, never fires
    /// while a player merely sits in the room (only entry calls it), and re-spawns only after the
    /// primary has been killed and a player re-enters. Independent of the lair/pressure spawn path.
    ///
    /// The spawn tail also fires the spawned monster's CreateSpell — for
    /// the dark-elf queen this is spell #456 "summon weaponmaster", which conjures her escort #342.
    /// We fire it here (stock spawns this primary on player entry, so this is the faithful trigger),
    /// outside the lock like TrySpawnMonsterInRoom, and gate it with a once-per-life latch so the
    /// escort is summoned the first time a player is present with a freshly (re)spawned primary —
    /// regardless of whether the lazy room-init path created the primary first — and never re-fires
    /// while it stays alive.
    /// </summary>
    private void EnsureRoomNpcPresent(Room room, Player enteringPlayer)
    {
        if (room.NPC <= 0 || !Database.Monsters.TryGetValue(room.NPC, out var npcTemplate))
            return;

        var key = (room.MapNumber, room.RoomNumber);
        MonsterInstance? primaryToFire = null;
        // Set ONLY when the primary actually (re)appears — a live primary that merely needs its
        // CreateSpell fired must not re-announce an arrival that already happened.
        MonsterInstance? spawnedPrimary = null;

        lock (_monsterLock)
        {
            if (!_roomMonsters.TryGetValue(key, out var monsters))
            {
                monsters = [];
                _roomMonsters[key] = monsters;
            }

            // Latch is set: a live primary is already here — never spawn a second. But if it was
            // created by a path that didn't fire its CreateSpell (lazy room init / lair tick), fire
            // it now so the escort summon isn't lost.
            var livePrimary = monsters.FirstOrDefault(monster => !monster.IsDead && monster.Template.Number == npcTemplate.Number);
            if (livePrimary != null)
            {
                if (!livePrimary.CreateSpellFired)
                    primaryToFire = livePrimary;
            }
            // Unique timer mobs (chimera, basilisk, …) honor their RegenTime: once killed they stay
            // dead until RegenTime hours have elapsed, even across re-entries. RegenTime==0 / non-
            // unique primaries (slime beast, cave worm, …) are not gated and revive immediately.
            else if (IsRegenTimerElapsed(npcTemplate))
            {
                // Revive the dead primary in place if its instance is still in the room (preserves
                // identity, rerolls carried treasure), matching a fresh spawn.
                var deadNpc = monsters.FirstOrDefault(monster => monster.IsPermanentNPC && monster.Template.Number == npcTemplate.Number);
                if (deadNpc != null)
                {
                    deadNpc.CurrentHP = deadNpc.MaxHP;
                    // A revive means it has already died once (last-death stamp set), so the guarantee
                    // is off and treasure rerolls normally — ShouldGuaranteeFirstDrop returns false here.
                    deadNpc.RollCarriedTreasure(ShouldGuaranteeFirstDrop(deadNpc.Template));
                    deadNpc.ResetEnergy();
                    deadNpc.ResetDeathProcessing(); // clears CreateSpellFired so the escort re-summons
                    deadNpc.RespawnAtUtc = null;
                    deadNpc.RespawnTimer = 0;
                    AdjustGlobalCount(deadNpc.Template.Number, 1);
                    primaryToFire = deadNpc;
                    spawnedPrimary = deadNpc;
                }
                // Otherwise spawn a fresh primary (first entry, or the dead instance was cleaned up).
                else if (CanSpawnMonster(npcTemplate))
                {
                    var npc = MonsterInstance.Create(npcTemplate, room.MapNumber, room.RoomNumber, isPermanentNPC: true, guaranteeAllDrops: ShouldGuaranteeFirstDrop(npcTemplate));
                    monsters.Add(npc);
                    AdjustGlobalCount(npcTemplate.Number, 1);
                    primaryToFire = npc;
                    spawnedPrimary = npc;
                }
            }
        }

        // Stock reaches this same spawn through the entry gate
        // (spawn when Room.NPC is set and the primary-present latch is clear), and the spawn
        // ALWAYS broadcasts the arrival line before firing the CreateSpell — so a Room.NPC primary
        // announces exactly like a lair or summoned spawn. We were silent here, which is why a minotaur
        // chieftain (#114, MoveMsg 248 = "A minotaur chieftain stomps in from %s!") could materialise
        // with no line at all.
        //
        // The entering player is EXCLUDED: stock spawns in the entry gate, before the mover joins the
        // room, so they are never a broadcast recipient — they simply see the primary in the room
        // description that follows. Only players ALREADY standing here witness the arrival. (Our
        // NotifyPlayerEnteredRoom runs after the move commits, so without this exclusion the mover would
        // get a line stock never sends them.) Announce before the CreateSpell, matching the spawn
        // order — the escort a CreateSpell summons must not be announced ahead of its summoner.
        if (spawnedPrimary != null)
        {
            string? spawnMsg = FormatMonsterSpawnMessage(spawnedPrimary.Template, spawnedPrimary.DisplayName);
            if (spawnMsg != null)
            {
                BroadcastToRoom(room.MapNumber, room.RoomNumber,
                    $"{spawnMsg}{MudAnsi.Reset}",
                    except: enteringPlayer.Client,
                    reprompt: true,
                    prependLineBreak: true);
            }
        }

        // Fired outside the lock (matches TrySpawnMonsterInRoom). Latch first so a CreateSpell that has
        // no world-clean effect (Targets 0/8) still counts as "fired" and isn't retried on every entry.
        if (primaryToFire != null)
        {
            primaryToFire.CreateSpellFired = true;
            FireMonsterCreateSpell(primaryToFire, 0);
        }
    }

    public Room? GetRoom(int mapNumber, int roomNumber)
    {
        return Database.Rooms.GetValueOrDefault((mapNumber, roomNumber));
    }

    public List<MonsterInstance> GetMonstersInRoom(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        MonsterInstance? lazySpawnedPrimary = null;
        if (!_roomMonsters.ContainsKey(key))
        {
            lock (_monsterLock)
            {
                // Double-check under lock to prevent race condition
                if (!_roomMonsters.ContainsKey(key))
                    lazySpawnedPrimary = SpawnRoomMonsters(mapNumber, roomNumber);
            }
        }

        // Broadcast outside the lock. Stock has NO silent spawn: every spawn closes
        // with a room broadcast of the template's MoveMsg, so a primary NPC placed
        // by this lazy path owes the room an arrival line exactly like one placed on entry.
        // AnnounceSpawnedMonsters no-ops when the room holds no players (the common case here, and the
        // state the stock boot-time preload always runs in), and
        // FormatMonsterSpawnMessage still returns null for the blank-sentinel sneaks.
        if (lazySpawnedPrimary != null)
            AnnounceSpawnedMonsters([new SpawnedMonsterAnnouncement(mapNumber, roomNumber, lazySpawnedPrimary)]);

        if (_roomMonsters.TryGetValue(key, out var monsters))
        {
            lock (_monsterLock)
            {
                return monsters.Where(m => !m.IsDead).ToList();
            }
        }

        return [];
    }

    /// <summary>
    /// Lazily populate a room's monster list on first access. Returns the primary NPC it placed (or
    /// null), which the caller announces once it has dropped _monsterLock — no spawn is ever silent.
    /// </summary>
    private MonsterInstance? SpawnRoomMonsters(int mapNumber, int roomNumber, bool bypassOccupancyGate = false)
    {
        var room = GetRoom(mapNumber, roomNumber);
        if (room == null) return null;

        var key = (mapNumber, roomNumber);
        if (_roomMonsters.ContainsKey(key)) return null;

        var monsters = new List<MonsterInstance>();
        var spawnedPrimary = EnsurePermanentNpcInRoom(room, key, monsters, bypassOccupancyGate);

        if (monsters.Count > 0)
        {
            _roomMonsters[key] = monsters;
        }

        return spawnedPrimary;
    }

    /// <summary>Check if a monster can be spawned based on GameLimit.</summary>
    private bool CanSpawnMonster(Monster template)
    {
        if (template.GameLimit <= 0) return true; // 0 = unlimited
        int currentCount = _globalMonsterCounts.GetValueOrDefault(template.Number, 0);
        return currentCount < template.GameLimit;
    }

    /// <summary>Adjust global spawn count for a monster template.</summary>
    private void AdjustGlobalCount(int monsterNumber, int delta)
    {
        _globalMonsterCounts.AddOrUpdate(monsterNumber,
            delta > 0 ? delta : 0,
            (_, current) => Math.Max(0, current + delta));
    }

    public void SpawnMonsterInRoom(int mapNumber, int roomNumber, int monsterId)
    {
        TrySpawnMonsterInRoom(mapNumber, roomNumber, monsterId, ignoreRoomRestrictions: false, out _, out _);
    }

    public bool TrySpawnMonsterInRoom(int mapNumber, int roomNumber, int monsterId, bool ignoreRoomRestrictions, out MonsterInstance? instance, out string errorMessage, int createSpellDepth = 0, bool respectRegenTimer = true)
    {
        instance = null;
        errorMessage = string.Empty;

        if (!Database.Monsters.TryGetValue(monsterId, out var template))
        {
            errorMessage = $"Monster {monsterId} was not found.";
            return false;
        }

        if (!CanSpawnMonster(template))
        {
            errorMessage = $"The {template.Name} cannot be spawned right now because its game limit has been reached.";
            return false;
        }

        // The unique-boss RegenTime gate applies to EVERY spawn path,
        // including a textblock/room-spell `summon`. Without it, a room-spell boss trigger (e.g. the
        // fortress trigger 12/2375-2377 → summon Angelic Hunter #1012, GameLimit 1 / RegenTime 1h) re-spawns
        // the boss on the very next pulse the room is empty — so it can be farmed every few seconds instead
        // of once an hour. IsRegenTimerElapsed is a no-op for anything that isn't a GameLimit-1 timer unique,
        // so reinforcements/escorts/pets are unaffected. Sysop spawns pass respectRegenTimer:false to override.
        if (respectRegenTimer && !IsRegenTimerElapsed(template))
        {
            errorMessage = $"The {template.Name} has not finished regenerating.";
            return false;
        }

        var room = GetRoom(mapNumber, roomNumber);
        if (room == null)
        {
            errorMessage = $"Room {mapNumber}/{roomNumber} was not found.";
            return false;
        }

        if (!ignoreRoomRestrictions && !IsMonsterAllowedInRoom(template, room))
        {
            errorMessage = $"The {template.Name} cannot be spawned in this room.";
            return false;
        }

        lock (_monsterLock)
        {
            var key = (mapNumber, roomNumber);
            if (!_roomMonsters.ContainsKey(key))
                _roomMonsters[key] = [];

            instance = MonsterInstance.Create(template, mapNumber, roomNumber, guaranteeAllDrops: ShouldGuaranteeFirstDrop(template));
            // Spawned past the room's Group/MonsterType gate → exempt from the invalid-room sweep so it
            // isn't removed from under an in-progress fight (death-summons, reinforcements, quest adds, pets).
            instance.WasForcePlaced = ignoreRoomRestrictions;
            _roomMonsters[key].Add(instance);
            AdjustGlobalCount(template.Number, 1);
        }

        // The spawn line self-colours from BrightYellow, inserting Green only after
        // a substituted name — so do NOT wrap it in a Green base here. A null message is the blank
        // silence-sentinel MoveMsg (e.g. summoned "dying master assassin") → spawn with no arrival line.
        string? spawnMsg = FormatMonsterSpawnMessage(template, instance.DisplayName);
        if (spawnMsg != null)
        {
            BroadcastToRoom(mapNumber, roomNumber,
                $"{spawnMsg}{MudAnsi.Reset}",
                reprompt: true,
                prependLineBreak: true);
        }

        FireMonsterCreateSpell(instance, createSpellDepth);
        return true;
    }

    // On spawn a monster casts its CreateSpell through the same dispatcher
    // as its DeathSpell
    // as DeathSpell — monster as caster, no player-target arg. Spawning is a world event with no player
    // session, so only the world-clean subset fires here: summons (ability 12 → spawn escorts) and
    // self/ally duration buffs on the spawning monster. Single-target types (Targets 0/8) are no-ops as
    // in stock; area offensive-on-player create spells (a couple of exotic mobs: dragonfear, globe of
    // darkness) are not applied — there is no killer/room session to target. Depth-guarded so a summon
    // whose own CreateSpell summons again can't cascade without bound (game limits also gate spawns).
    private const int MaxCreateSpellDepth = 3;
    private const int ScriptedCommandTextBlockAbility = 148;   // run a textblock as a script

    private void FireMonsterCreateSpell(MonsterInstance caster, int depth)
    {
        if (caster.Template.CreateSpell <= 0
            || depth >= MaxCreateSpellDepth
            || !Database.Spells.TryGetValue(caster.Template.CreateSpell, out var spell))
        {
            return;
        }

        bool isSelfAlly = spell.Targets is 1 or 2 or 4;
        bool isArea = spell.Targets is 3 or 5 or 6 or 7 or 9 or 10 or 11 or 12 or 13;
        if (!isSelfAlly && !isArea)
            return; // Targets 0/8: no player target ⇒ no-op.

        bool fired = false;

        // Summon (ability 12): spawn the escorts, owned by the caster. Stock walks ALL TEN ability
        // slots and spawns once per summon slot, so ONE create spell can bring a whole
        // retinue: the hydra's #90 lists the head (590) six times, which is how stock puts a full set of
        // six heads in the pit the moment the hydra spawns — with a single summon here the pit started
        // with one head and the hydra's 50%-per-round mid-spell then trickled replacements in forever,
        // which is not the same fight. The template id is each slot's AbilVal, or the rolled
        // MinBase..MaxBase when that slot is 0 (the dragon-summon-tapestry convention) — shared with every
        // other summon path via ResolveSummonTemplateIds. Each spawn recurses through TrySpawnMonsterInRoom
        // so an escort fires its own CreateSpell (depth-guarded), and each is gated by the summoned
        // template's own GameLimit (hydra head = 6), which is what actually caps the retinue.
        var summonTemplateIds = CommandParser.ResolveSummonTemplateIds(spell, _rng);
        if (summonTemplateIds.Count > 0)
        {
            foreach (int summonTemplateId in summonTemplateIds)
            {
                // Re-counted per summon so a multi-slot spell cannot overshoot the child-list size.
                int existingChildren = GetMonstersInRoom(caster.MapNumber, caster.RoomNumber)
                    .Count(m => ReferenceEquals(m.Owner, caster) && !m.IsDead);
                if (existingChildren >= 10)
                    break;

                if (TrySpawnMonsterInRoom(caster.MapNumber, caster.RoomNumber, summonTemplateId, ignoreRoomRestrictions: true, out var summoned, out _, depth + 1)
                    && summoned != null)
                {
                    summoned.Owner = caster;
                    fired = true;
                }
            }
        }
        // Self/ally duration buff on the spawning monster (armour, haste, etc.).
        else if (isSelfAlly && spell.Duration > 0 && spell.Abilities.Count > 0)
        {
            // Capped band at the triggered-cast level (stock passes 1 for CreateSpell/DeathSpell),
            // consistent with every other monster-cast magnitude: flat MinBase..MaxBase for a no-inc buff,
            // plus the level-1 inc for any that scale.
            var (bandMin, bandMax) = CommandParser.ComputeSpellMagnitudeBand(spell, 1);
            int magnitude = _rng.Next(bandMin, bandMax + 1);
            caster.AddOrRefreshActiveSpell(Database, spell.Number, magnitude, spell.Duration);
            fired = true;
        }
        // Area duration debuff on the players already in the room (e.g. aged earth dragon → "globe of
        // darkness" #440, a 40-tick darkness/freedom-strip). Applied directly at world level (sync) — the
        // active-spell aggregation carries its abilities on recalc. NOTE: an area CreateSpell that instead
        // triggers an ability-148 textblock script (ancient sand dragon → "dragonfear" #531 → textblock 1048's
        // level-gated fear) is NOT handled here: the textblock interpreter is async/per-session and the
        // spawn path is synchronous — that single case stays deferred.
        else if (isArea && spell.Duration > 0 && spell.Abilities.Count > 0
            && !spell.Abilities.ContainsKey(ScriptedCommandTextBlockAbility))
        {
            // Capped band at the triggered-cast level (1), as above — consistent monster-cast magnitude.
            var (bandMin, bandMax) = CommandParser.ComputeSpellMagnitudeBand(spell, 1);
            int magnitude = _rng.Next(bandMin, bandMax + 1);
            foreach (var roomPlayer in GetPlayersInRoom(caster.MapNumber, caster.RoomNumber)
                .Where(p => p.CurrentHP > Player.DeathHP))
            {
                if (roomPlayer.AddOrRefreshActiveSpell(spell.Number, magnitude, spell.Duration))
                    RecalculatePlayerStats(roomPlayer);
                fired = true;
            }
        }

        if (fired)
        {
            // Stock shows the spell's CastMsgB room line, NOT a fabricated "{name} casts {spell}." — for a
            // summon/create spell that line is the empty "66" sentinel, so the cast is SILENT (the new
            // monster's spawn message announces its arrival). See FormatMonsterTriggeredCastAnnounce.
            string? announce = FormatMonsterTriggeredCastAnnounce(spell, caster);
            if (announce != null)
                BroadcastToRoom(caster.MapNumber, caster.RoomNumber,
                    GameAnsi.SpellHostile(announce), reprompt: true, prependLineBreak: true);
        }
    }

    // A monster's triggered cast (DeathSpell /
    // CreateSpell) shows the spell's CastMsgB ROOM-view line (message Line2, with %s = the
    // caster's name) — never a fabricated "{name} casts {spell}." A CastMsgB of 0, or one pointing at a
    // missing row (the empty "66" sentinel that every summon/create/death spell uses), prints NOTHING.
    // Returns the room-view line (no color), or null when the cast is silent.
    public string? FormatMonsterTriggeredCastAnnounce(GameSpell spell, MonsterInstance caster)
    {
        if (spell.CastMessageB <= 0 || !Database.Messages.TryGetValue(spell.CastMessageB, out var message))
            return null;
        string name = caster.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) name = caster.Template.Name;
        if (!name.StartsWith("the ", StringComparison.OrdinalIgnoreCase)) name = $"The {name}";
        string line = (message.Line2 ?? string.Empty).Replace("%s", name, StringComparison.Ordinal).Trim();
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }

    public MonsterInstance? FindMonsterInRoom(int mapNumber, int roomNumber, string name)
    {
        var monsters = GetMonstersInRoom(mapNumber, roomNumber);
        return monsters.FirstOrDefault(m =>
            TargetNameMatcher.MatchesWordPrefix(m.DisplayName, name) ||
            TargetNameMatcher.MatchesWordPrefix(m.Name, name));
    }

    // Global by-name monster lookup for `track`: when the named target is
    // not in the tracker's room, search every spawned monster so we can walk its trail.
    public MonsterInstance? FindMonsterByNameAnywhere(string name)
    {
        foreach (var list in _roomMonsters.Values)
        {
            var match = list.FirstOrDefault(m => !m.IsDead
                && (TargetNameMatcher.MatchesWordPrefix(m.DisplayName, name)
                    || TargetNameMatcher.MatchesWordPrefix(m.Name, name)));
            if (match != null)
                return match;
        }
        return null;
    }

    /// <summary>
    /// Bug #212. An EXACT name match wins outright over any prefix match, however the room enumerates.
    /// In the player branch of target resolution, the terminal scan accepts
    /// a candidate on a loose match, then tests full-name equality — and RETURNS
    /// THAT PLAYER IMMEDIATELY, only falling through to the ambiguity list for a merely-loose hit. We
    /// took the first boolean match instead, so with "Blue" and "Blueberry" both present "aid blue" /
    /// "mend blue" / "drag blue" landed on whichever the dictionary yielded first — and _onlinePlayers is
    /// a ConcurrentDictionary, so that order isn't even stable between runs.
    /// </summary>
    public Player? FindPlayerInRoom(int mapNumber, int roomNumber, string name, Player? except = null)
        => SelectBestNameMatch(GetPlayersInRoom(mapNumber, roomNumber, except), name);

    /// <summary>
    /// The selection half of <see cref="FindPlayerInRoom"/>, kept pure and order-explicit so it can be
    /// pinned against an adverse candidate order — an end-to-end test of the old bug passes or fails by
    /// luck, since the room enumeration comes out of a ConcurrentDictionary.
    ///
    /// Only Exact is promoted, matching the stock two-way exact/loose split: it draws no distinction
    /// between PrefixFromStart and WordPrefix, so neither do we, and among loose-only candidates the
    /// previous first-match behaviour is preserved. (The stock loose pick is the LAST match in terminal
    /// order — it overwrites on every hit — but our enumeration has no terminal ordering
    /// to be faithful to, so replicating that would be noise rather than fidelity.)
    /// </summary>
    internal static Player? SelectBestNameMatch(IReadOnlyList<Player> candidates, string name)
    {
        Player? looseMatch = null;
        foreach (var player in candidates)
        {
            var rank = TargetNameMatcher.GetMatchRank(player.Name, name);
            if (rank == TargetNameMatcher.MatchRank.Exact)
                return player;

            if (rank != TargetNameMatcher.MatchRank.None)
                looseMatch ??= player;
        }

        return looseMatch;
    }

    public void RemoveDeadMonster(MonsterInstance monster)
    {
        // A monster that dies while smashed to the floor must not keep its knockdown: a dead Room.NPC
        // primary stays in the room list (below), and its expiring timer used to broadcast "Slightly dazed
        // the <monster> rises from the floor." over its corpse — or it revived already knocked down.
        monster.ClearKnockdown();

        var key = (monster.MapNumber, monster.RoomNumber);
        if (_roomMonsters.TryGetValue(key, out var list))
        {
            // Decrement global count
            AdjustGlobalCount(monster.Template.Number, -1);

            // Stamp the last-death time for any limited monster. This one stamp
            // gates BOTH the unique RegenTime respawn cooldown AND the guaranteed-first-drop override
            // (ShouldGuaranteeFirstDrop) — so once a boss dies here, its later spawns roll drops normally.
            RecordLimitedMonsterDeath(monster.Template);

            if (monster.IsPermanentNPC)
            {
                // Faithful to stock (the kill clears the primary-present latch): the
                // primary NPC clears its "present" latch on death and respawns ONLY when a player
                // next ENTERS the room (EnsureRoomNpcPresent) — never on a wall-clock timer and
                // never while a player merely sits here. The dead instance stays in the list and is
                // revived in place on the next entry.
                monster.RespawnAtUtc = null;
                monster.RespawnTimer = 0;
            }
            else
            {
                ScheduleLairRespawnAfterVacancy(monster);
                list.Remove(monster);
                if (list.Count == 0)
                    _roomMonsters.TryRemove(key, out _);
            }
        }
    }

    public bool TryBeginMonsterDeathProcessing(MonsterInstance monster)
    {
        lock (_monsterLock)
        {
            return monster.IsDead && monster.TryBeginDeathProcessing();
        }
    }

    // Deliver one line to a single online player's connection, honouring the same broadcast
    // buffering/suppression/reprompt contract the BBS host used to apply. The host owns the socket;
    // the world owns the routing (it knows who is where). Mirrors the old TelnetServerHost loop body.
    private static void DeliverToClient(Player player, string message, bool reprompt, bool prependLineBreak)
    {
        var client = player.Client;
        if (client == null || player.SuppressBroadcastOutput)
            return;
        if (client.TryDeferBroadcastLine(message, reprompt, prependLineBreak))
            return;

        bool suppress = player.SuppressBroadcastReprompt;
        if (prependLineBreak)
            _ = client.PrepareForBroadcastAsync(suppress);
        _ = client.SendLineAsync(message);
        if (reprompt && !suppress)
            _ = client.SendAsync(client.CurrentPrompt);
    }

    public List<Player> GetPlayersInRoom(int mapNumber, int roomNumber, Player? except = null)
    {
        var players = new List<Player>();
        foreach (var player in _onlinePlayers.Values)
        {
            if (ReferenceEquals(player, except))
                continue;
            if (player.CurrentMapNumber == mapNumber && player.CurrentRoomNumber == roomNumber)
                players.Add(player);
        }

        return players;
    }

    public void BroadcastToRoom(int mapNumber, int roomNumber, string message, IGameClient? except = null, bool reprompt = true, bool prependLineBreak = true)
    {
        foreach (var player in _onlinePlayers.Values)
        {
            if (ReferenceEquals(player.Client, except))
                continue;
            if (player.CurrentMapNumber == mapNumber && player.CurrentRoomNumber == roomNumber)
                DeliverToClient(player, message, reprompt, prependLineBreak);
        }
    }

    public void BroadcastToAdjacentRooms(int mapNumber, int roomNumber, string message, IGameClient? except = null, bool reprompt = true, bool prependLineBreak = true)
    {
        foreach (var adjacentRoom in GetAdjacentRooms(mapNumber, roomNumber))
            BroadcastToRoom(adjacentRoom.Map, adjacentRoom.Room, message, except, reprompt, prependLineBreak);
    }

    public void BroadcastToRoomSequence(int mapNumber, int roomNumber, IReadOnlyList<string> messages, IGameClient? except = null, bool prependLineBreak = true)
    {
        if (messages.Count == 0)
            return;

        // EVERY line carries its own prompt, not just the last one of the sequence. Captured from
        // a stock server with the terminal completely idle: a single
        // 205-byte push carrying two monster-attack lines went out as
        //   ESC[79D ESC[K …[HP=306…]:  ESC[79D ESC[K "The giant bat snaps at you…" CRLF
        //   ESC[79D ESC[K …[HP=306…]:  ESC[79D ESC[K "The zombie swings at you…"  CRLF
        //   ESC[79D ESC[K …[HP=306…]:
        // i.e. line/prompt/line/prompt — 31 lines and 30 prompts across the capture. Each intermediate
        // prompt is erased by the next line's ESC[79D ESC[K before the terminal repaints, so this is
        // visually identical to a single trailing prompt; Megamud parses the byte stream and treats the
        // prompt as its record boundary. Reprompting only on the final message collapsed a whole
        // multi-line event (e.g. "X just left." / "monster just left." / "*Combat Off*") into one
        // record, which is what left it convinced combat was still running.
        for (int index = 0; index < messages.Count; index++)
        {
            BroadcastToRoom(
                mapNumber,
                roomNumber,
                messages[index],
                except,
                reprompt: true,
                prependLineBreak: prependLineBreak);
        }
    }

    public void BroadcastToRealm(string message, IGameClient? except = null, bool reprompt = false, bool prependLineBreak = true)
    {
        foreach (var player in _onlinePlayers.Values)
        {
            if (ReferenceEquals(player.Client, except))
                continue;
            DeliverToClient(player, message, reprompt, prependLineBreak);
        }
    }

    public void SendToPlayer(string playerName, string message, bool reprompt = true, bool prependLineBreak = true)
    {
        if (_onlinePlayers.TryGetValue(playerName, out var player))
            DeliverToClient(player, message, reprompt, prependLineBreak);
    }

    public void RepromptPlayer(string playerName)
    {
        if (!_onlinePlayers.TryGetValue(playerName, out var player))
            return;

        var client = player.Client;
        if (client == null || player.SuppressBroadcastOutput || player.SuppressBroadcastReprompt)
            return;

        // Redraw the whole prompt line in place and restore any half-typed input, rather than
        // blasting the prompt inline — a rest/meditate/room-spell/poison refresh must not interrupt
        // a command the player is mid-way through typing.
        _ = client.RedrawPromptWithInputAsync(client.CurrentPrompt);
    }

    // Forcibly remove an online character and tear down its connection. Used by the duplicate-login
    // guard and sysop tooling; the "just disconnected" realm announcement is a MUD message, so it
    // lives here in the world, not in the BBS host.
    public bool DisconnectOnlineCharacter(string playerName, string reason)
    {
        if (!_onlinePlayers.TryGetValue(playerName, out var player))
            return false;

        var client = player.Client;
        if (client != null)
        {
            _ = client.EnsureNewLineAsync();
            _ = client.SendLineAsync(reason);
        }

        AnnounceDisconnectAndRemove(player, client, carrierLost: false);
        return true;
    }

    public bool EmergencyDisconnectOnlineCharacter(string playerName)
    {
        if (!_onlinePlayers.TryGetValue(playerName, out var player))
            return false;

        AnnounceDisconnectAndRemove(player, player.Client, carrierLost: false);
        return true;
    }

    // A connection dropped underneath us (socket closed / read loop ended). The BBS host calls this
    // from its client cleanup so the MUD-specific "just disconnected" message and player removal stay
    // in the world. The host hands us the generic connection; we recover the attached realm character
    // from the door's session object (no character attached at the BBS login / menu => nothing to do).
    public void HandleDroppedConnection(IBbsConnection connection)
    {
        if ((connection.AppData as IGameClient)?.Player is not { } player)
            return;

        // Only tear down a character that is STILL the online instance under this name. A character
        // already removed from the world — sysop BOARD RESET, a forced disconnect, return-to-menu —
        // must not be torn down again here: AnnounceDisconnectAndRemove saves by default, which would
        // re-INSERT the row a BOARD RESET just deleted (the wipe kicks sockets, and this teardown then
        // ran afterwards and resurrected every kicked character). The reference check also stops a
        // stale socket from evicting a NEW session that has since logged in under the same name.
        if (!_onlinePlayers.TryGetValue(player.Name, out var online) || !ReferenceEquals(online, player))
            return;

        // Carrier loss vs an administrative close. Both arrive here, but only the first is the stock hangup
        // case: a session the BOARD replaced (a duplicate login, a sysop kick, a shutdown) must not be
        // punished for a drop that never happened. IBbsConnection.DisconnectedByHost is the transport
        // telling us which it was — and note a genuine reconnect after a real drop still counts as carrier
        // loss, because it is the stale socket, not the board, that failed.
        AnnounceDisconnectAndRemove(player, player.Client, carrierLost: !connection.DisconnectedByHost);
    }

    // The door owns the account→character link (Players.BbsUserId). The host's ;users / ;account listings
    // ask us for the character a BBS account owns, by its BBS user id; we merge in the player's sysop/test
    // flags. Surfaced as a door-agnostic record so the host never references Player. Resolves offline
    // characters too (it's a player-store read), so ;users names every account's character, not just online ones.
    public BbsLinkedPlayerInfo? LookupPlayerByBbsUser(string bbsUserName)
    {
        if (string.IsNullOrWhiteSpace(bbsUserName))
            return null;

        var player = PlayerRepo.LoadPlayerByBbsUserId(bbsUserName);
        return player == null
            ? null
            : new BbsLinkedPlayerInfo(player.Name, player.IsSysop, player.IsTestAccount, player.IsTesterSysop);
    }

    private void AnnounceDisconnectAndRemove(Player player, IGameClient? client, bool carrierLost)
    {
        // Stock hangup order: the penalty lands FIRST, then the realm is told, then the kill check
        // decides whether the character it just wounded is now dead. Off unless a sysop enabled it.
        bool killedByPenalty = carrierLost && ApplyDropCarrierPenalty(player);

        // Stock color is bright white (\x1b[1;37m, confirmed). NOTE: getting MegaMud to
        // file this line under its Conversation window (the way "just left the Realm" already does) is
        // still open — the markers in the stock data segment are internal formatting codes, not the
        // on-wire bytes, so reproducing them verbatim just renders as "|]" garbage. Needs a raw wire
        // capture of a reference MUD's disconnect line to match what MegaMud actually parses.
        BroadcastToRealm(
            $"{MudAnsi.BrightWhite}{player.Name} just disconnected!!!{MudAnsi.Reset}",
            except: client,
            reprompt: true);
        // The kill check, run straight after the broadcast: the penalty can take a
        // wounded player under the death floor, and hanging up mid-bleed-out never rescues them. Death
        // handling owns the removal in the permadeath case, so only remove here when they survived.
        if (killedByPenalty)
            ProcessWorldTickDeath(player, $"{MudAnsi.BrightRed}{MudAnsi.BgBlack}You have been killed.{MudAnsi.Reset}");

        if (_onlinePlayers.TryGetValue(player.Name, out var stillOnline) && ReferenceEquals(stillOnline, player))
            RemovePlayer(player);

        if (client != null)
        {
            client.Player = null;
            client.Disconnect();
        }
    }

    private HashSet<(int Map, int Room)> GetAdjacentRooms(int mapNumber, int roomNumber)
    {
        var adjacentRooms = new HashSet<(int Map, int Room)>();

        var sourceRoom = GetRoom(mapNumber, roomNumber);
        if (sourceRoom != null)
        {
            foreach (var exit in sourceRoom.GetExits().Values)
                adjacentRooms.Add((exit.Map, exit.Room));
        }

        if (_incomingMovementExitsByTargetRoom.TryGetValue((mapNumber, roomNumber), out var incomingExits))
        {
            foreach (var incoming in incomingExits)
                adjacentRooms.Add((incoming.Map, incoming.Room));
        }

        adjacentRooms.Remove((mapNumber, roomNumber));
        return adjacentRooms;
    }

    public void Stop()
    {
        _fastTimer?.Dispose();
        _mediumTimer?.Dispose();
        _slowTimer?.Dispose();
        _roomSpellTimer?.Dispose();
        _combatTimer?.Dispose();
        // Stop the write-behind loop before the final save-all so it can't race us; the loop only ever
        // wrote dirty ONLINE players, all of whom the loop below now persists synchronously anyway.
        StopPlayerWriteBehind();
        StopSpawnBubbleRefresh();
        StopRealmBus();
        _pendingSpawnBubbleRefresh.Clear();
        _dirtyPlayers.Clear();
        PersistRoomGroundState();
        PersistShopStock();

        // Save all online players
        foreach (var player in _onlinePlayers.Values)
        {
            PlayerRepo.SavePlayer(player);
        }
    }

    public (int ActiveRooms, int ActiveMonsters, int GroundItemRooms, int GroundCurrencyRooms) GetBufferStats()
    {
        int activeMonsters = 0;
        foreach (var kvp in _roomMonsters)
            activeMonsters += kvp.Value.Count(m => !m.IsDead);

        return (
            ActiveRooms: _roomMonsters.Count,
            ActiveMonsters: activeMonsters,
            GroundItemRooms: _roomGroundItems.Count,
            GroundCurrencyRooms: _roomGroundCurrency.Count
        );
    }

    public int ResetMonsterGenerationRoom(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        MonsterInstance? restoredPrimary;
        int aliveAfterReset;

        lock (_monsterLock)
        {
            if (_roomMonsters.TryRemove(key, out var list))
            {
                foreach (var monster in list)
                    AdjustGlobalCount(monster.Template.Number, -1);
            }

            // A manual room reset (sysop `reset`, test setup) should restore the room to its pristine
            // state, including bringing back a unique timer mob regardless of when it last died — and
            // regardless of who is standing here, which is why it bypasses the entry-only gate that
            // binds organic primary spawns.
            var room = GetRoom(mapNumber, roomNumber);
            if (room?.NPC > 0)
                _monsterRegenLastDeathUtc.TryRemove(room.NPC, out _);

            restoredPrimary = SpawnRoomMonsters(mapNumber, roomNumber, bypassOccupancyGate: true);

            aliveAfterReset = _roomMonsters.TryGetValue(key, out var spawned)
                ? spawned.Count(m => !m.IsDead)
                : 0;
        }

        // Unlike the organic paths this one CAN restore a primary into an occupied room, so it owes the
        // room the arrival line — no spawn a player can witness is ever silent.
        if (restoredPrimary != null)
            AnnounceSpawnedMonsters([new SpawnedMonsterAnnouncement(mapNumber, roomNumber, restoredPrimary)]);

        return aliveAfterReset;
    }

    public int ResetMonsterGenerationArea(int mapNumber)
    {
        int affected = 0;
        lock (_monsterLock)
        {
            var roomKeys = _roomMonsters.Keys.Where(k => k.Map == mapNumber).ToList();
            foreach (var key in roomKeys)
            {
                if (_roomMonsters.TryRemove(key, out var list))
                {
                    foreach (var monster in list)
                        AdjustGlobalCount(monster.Template.Number, -1);
                    affected++;
                }
            }
        }

        return affected;
    }

    public void ReinitializeBuffers()
    {
        lock (_monsterLock)
        {
            _roomMonsters.Clear();
            _globalMonsterCounts.Clear();
        }
        _monsterRegenLastDeathUtc.Clear();
        _roomNextLairSpawnAtUtc.Clear();
        _roomExitStates.Clear();
        _roomExitAccessStates.Clear();
        lock (_groundItemLock)
        {
            _roomGroundItems.Clear();
        }
        _initializedStaticGroundItemRooms.Clear();
        _roomGroundCurrency.Clear();
        _initializedStaticGroundCurrencyRooms.Clear();
        _itemRuntimeStates.Clear();
        _offlinePlayerItemStates.Clear();
        _playerRoomSpellStates.Clear();
        lock (_activeSpawnRoomsLock)
        {
            _activeSpawnRoomsByPlayer.Clear();
            _activeSpawnRooms.Clear();
        }
        _roomSpellTestNowUtc = _useManualRoomSpellTicksForTests ? DateTime.UtcNow : null;
        SeedBackgroundLairSpawnDeadlines();
        foreach (var player in _onlinePlayers.Values)
            RefreshPlayerSpawnBubbleIfNeeded(player, force: true);
    }

    // Full realm wipe for SYSOP BOARD RESET. Removes every online character from the world WITHOUT saving
    // (they are being deleted), wipes all persisted realm state, and rebuilds in-memory buffers so the world
    // is a fresh start: monsters respawn from scratch on entry, no ground loot, no housing, no gangs. Must be
    // called under the world gate (it mutates shared state); the caller handles player messaging/disconnect.
    public void ResetRealmToFreshState()
    {
        // Detach every online character without persisting, and drop their write-behind marks so the
        // background flusher can't re-insert a row we are about to delete (it also skips offline players).
        foreach (var player in GetAllOnlinePlayers())
            RemovePlayer(player, save: false);
        _dirtyPlayers.Clear();

        // Wipe all persisted realm state (players, rerolls, gangs, houses, shops, ground drops, hall of fame).
        PlayerRepo.ResetRealmPersistence();

        // Clear the gang-house eviction lockouts (a ServerSettings CSV) so no stale gang name lingers.
        PlayerRepo.SetServerSettingText(GangHouseLockoutSettingKey, string.Empty);

        // Rebuild in-memory buffers (spawned monsters, ground drops, room spells, spawn bubbles, unique
        // regen timers), then persist the now-empty unique-monster regen timers.
        ReinitializeBuffers();
        PersistMonsterRegenState();

        // Reload the now-empty gang house/shop ownership and refill finite shop stock (deed items included).
        LoadGangHouses();
        LoadGangShops();
        ResetShopStock();
    }

    public bool AnyPlayersInArenaRooms()
    {
        foreach (var player in _onlinePlayers.Values)
        {
            var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
            if (room != null && room.Name.Contains("arena", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void SetArenaCombatMode(bool combatMode)
    {
        ArenaCombatMode = combatMode;
    }

    public void SetMaxEvilPointsForgivenPerDay(int amount)
    {
        MaxEvilPointsForgivenPerDay = Math.Max(0, amount);
        PlayerRepo.SetServerSettingInt("MAXEPDAY", MaxEvilPointsForgivenPerDay);
        ResetAllEvilPointForgivenessTimers();
    }

    // SYSOP CONFIGURE EVILCAPBLOCK <0|1>: whether a maxed-evil player is also blocked from attacking
    // innocents (stock) vs merely denied the EP gain (modern).
    public void SetEvilCapBlocksActions(bool enabled)
    {
        EvilCapBlocksActions = enabled;
        PlayerRepo.SetServerSettingInt("EVILCAPBLOCK", enabled ? 1 : 0);
    }

    // SYSOP CONFIGURE SURPRISEROUND <0|1>: ON = a non-backstab weapon gets a full backstab-damage surprise
    // round (silent); OFF = it is a plain normal attack (normal damage, victim warned). See SurpriseRoundEnabled.
    public void SetSurpriseRoundEnabled(bool enabled)
    {
        SurpriseRoundEnabled = enabled;
        PlayerRepo.SetServerSettingInt("SURPRISEROUND", enabled ? 1 : 0);
    }

    // SYSOP CONFIGURE QUESTALLPARTY <0|1>: hand a main-quest ground-drop item to every engaged party member
    // instead of dropping a single copy (so a party doesn't have to re-kill the monster once per member).
    public void SetQuestDropToAllParty(bool enabled)
    {
        QuestDropToAllParty = enabled;
        PlayerRepo.SetServerSettingInt("QUESTALLPARTY", enabled ? 1 : 0);
    }

    public void SetMinEvilPointsEnabled(bool enabled)
    {
        MinEvilPointsEnabled = enabled;
        PlayerRepo.SetServerSettingInt("MINEPS", enabled ? 1 : 0);
    }

    /// <summary>
    /// The lowest EvilPoints the forgiveness tick may carry this player to: the alignment scale's floor,
    /// raised to their own `set mineps` value while the realm has MINEPS enabled. Math.Max also means a
    /// stale stored value below the scale can never widen the drift.
    /// </summary>
    public float GetEvilPointForgivenessFloorFor(Player player)
        => MinEvilPointsEnabled
            ? Math.Max(EvilPointForgivenessFloor, player.MinEvilPoints)
            : EvilPointForgivenessFloor;

    public void SetEvilPointForgivenessCycleMinutes(int minutes)
    {
        EvilPointForgivenessCycleMinutes = Math.Max(0, minutes);
        PlayerRepo.SetServerSettingInt("EPCYCLE", EvilPointForgivenessCycleMinutes);
        ResetAllEvilPointForgivenessTimers();
    }

    public void SetEvilPointForgivenessAmount(int amount)
    {
        EvilPointForgivenessAmount = Math.Max(0, amount);
        PlayerRepo.SetServerSettingInt("EPAMOUNT", EvilPointForgivenessAmount);
        ResetAllEvilPointForgivenessTimers();
    }

    public void SetPvpLevelRange(int levels)
    {
        PvpLevelRange = Math.Max(0, levels);
        PlayerRepo.SetServerSettingInt("PvpLevelRange", PvpLevelRange);
    }

    public void SetDeathSetting(int deathHp)
    {
        DeathSetting = Player.NormalizeDeathHP(deathHp);
        Player.ConfigureDeathHP(DeathSetting);
        PlayerRepo.SetServerSettingInt("DeathSetting", DeathSetting);
    }

    public void SetMonsterExperienceRate(int multiplier)
    {
        MonsterExperienceRate = Math.Max(1, multiplier);
        PlayerRepo.SetServerSettingInt("MonsterExperienceRate", MonsterExperienceRate);
    }

    public long ScaleMonsterExperienceAward(long baseExperience)
    {
        if (baseExperience <= 0)
            return 0;

        long rate = MonsterExperienceRate;
        return baseExperience > long.MaxValue / rate
            ? long.MaxValue
            : baseExperience * rate;
    }

    public void SetRerollKeepExperiencePercent(int percent)
    {
        RerollKeepExperiencePercent = Math.Clamp(percent, 0, 100);
        PlayerRepo.SetServerSettingInt("RerollKeepExperiencePercent", RerollKeepExperiencePercent);
    }

    public void SetLevelAheadCap(int levelsAhead)
    {
        LevelAheadCap = Math.Clamp(levelsAhead, 0, MaxLevelAheadCap);
        PlayerRepo.SetServerSettingInt("LevelAheadCap", LevelAheadCap);
    }

    // A positive exp gain is refused outright when the
    // player already holds at least the total needed for currentLevel + the
    // configurable level-ahead gate). We expose that gate as the sysop LevelAheadCap; 0 = disabled so
    // the gate never fires (our addition — stock's smallest configured value still gates at the next
    // level). When enabled, a player may bank exp up to but not reaching the requirement for
    // (currentLevel + cap + 1), i.e. at most `cap` levels ahead of the next train.
    public bool IsBlockedByLevelAheadCap(Player player)
    {
        if (LevelAheadCap <= 0)
            return false;
        if (!Database.Races.TryGetValue(player.RaceId, out var race)
            || !Database.Classes.TryGetValue(player.ClassId, out var cls))
            return false;

        long capExp = Player.GetTotalExpForLevel(player.Level + LevelAheadCap + 1, race.ExpTable, cls.ExpTable);
        return capExp > 0 && player.Experience >= capExp;
    }

    // Arena/collision branch: a death in a type-5 arena room when arena
    // combat-mode is OFF ("death does not count") is a no-op recall — no life lost, no loot dropped.
    // When ArenaCombatMode is ON the death counts (life lost / permadeath), but type-5 rooms still
    // never drop loot (stock skips the drop block for type-5 with arena on).
    public bool IsArenaRoom(int mapNumber, int roomNumber)
    {
        var room = GetRoom(mapNumber, roomNumber);
        return room != null && room.RoomType == ArenaRoomType;
    }

    public bool IsArenaDeathExempt(int mapNumber, int roomNumber)
        => !ArenaCombatMode && IsArenaRoom(mapNumber, roomNumber);

    public void SetLimitedItemsMode(int mode)
    {
        LimitedItemsMode = Math.Clamp(mode, 0, 1);
        PlayerRepo.SetServerSettingInt("LimitedItemsMode", LimitedItemsMode);
    }

    public void SetGroundItemLimitEnabled(bool enabled)
    {
        GroundItemLimitEnabled = enabled;
        PlayerRepo.SetServerSettingInt("GroundItemLimitEnabled", enabled ? 1 : 0);
    }
}
