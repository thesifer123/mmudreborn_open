using mmudreborn.Data.Models;
using mmudreborn.Game.Combat;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading;

namespace mmudreborn.Game;

public enum PlayerCombatRoundAction
{
    None = 0,
    Attack,
    Backstab,
    Punch,
    Kick,
    Jumpkick,
    Bash,
    Smash,
}

public enum KnockdownKind
{
    None = 0,
    Smash,
    Spell,
}

// One entry in a character's recent-death log. NON-STOCK: the original keeps no death history at all, so
// this whole feature sits behind SYSOP CONFIGURE DEATHLOG (default OFF) and is rendered only on the
// (already non-stock) `stat all` sheet.
public sealed class DeathRecord
{
    public DateTime WhenUtc { get; set; }
    public int MapNumber { get; set; }
    public int RoomNumber { get; set; }
    // Snapshotted at death rather than resolved on display: a room can be renamed by a data reseed, and
    // "where I died" should read the way it read at the time.
    public string RoomName { get; set; } = string.Empty;
    // The monster or player that landed the killing blow, or a cause ("poison", "a trap") when nothing
    // took credit. Never empty — see Player.RecordDeath.
    public string Killer { get; set; } = string.Empty;
}

// One active spell/buff slot on a character (parallel arrays: id, magnitude, remaining
// duration). Casting refreshes an existing slot rather than stacking a duplicate.
public sealed class ActiveSpell
{
    public int SpellId { get; set; }
    // The rolled effect magnitude captured at cast time (min..max, level-scaled), applied
    // as an ability's value when the spell's own value for that ability is 0 (e.g. speed/slow EU%).
    // Named "CastLevel" historically — it is NOT the caster level; kept as the persisted JSON name.
    public int CastLevel { get; set; }
    public int RemainingDuration { get; set; }
}

public class Player
{
    // Live BBS connection bound to this in-realm session (set when the player enters the realm,
    // cleared on removal). Lets GameWorld route output to a player without the BBS host needing to
    // know about rooms/realms — the host owns the socket, the world owns the routing.
    [JsonIgnore]
    public mmudreborn.Server.IGameClient? Client { get; set; }

    public const int KaiMageryType = 5;
    public const int LearnedSpellbookAbilityBase = 1_000_000;
    public const int MaxActiveSpells = 10;   // active-spell slots per player
    private const int AbilityMaxSentinel = -32000;   // "=max" init for AV/regen abilities
    // These ability ids aggregate by MAX across sources, not sum
    // (22, 71, 72, 101, 105, 106).
    private static readonly HashSet<int> MaxAggregatedAbilities = [22, 71, 72, 101, 105, 106];
    // Ability 87 (haste/slow EU scaler): sources fold together by a
    // running pairwise average (first source seeds it, each later one averages in), not max/sum.
    private const int HasteSlowAbilityId = 87;

    // Stock class title ladders by level band:
    // 1-4, 5-9, 10-14, ..., 65-69, 70+
    // Titles containing '|' are male|female variants.
    private static readonly Dictionary<string, string[]> ClassTitleLadders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Warrior"] = new[]
        {
            "Apprentice", "Grunt", "Fighter", "Veteran", "Mercenary", "Duelist", "Dragoon", "Gladiator",
            "Myrmidon", "Lord|Lady", "Hero|Heroine", "Weaponmaster|Weaponmistress", "Warmonger", "Warlord|Warlady", "Dragonslayer"
        },
        ["Witchunter"] = new[]
        {
            "Apprentice", "Persecutor", "Magehunter", "Magebane", "Spellbreaker", "Enforcer", "Disenchanter", "Eradicator",
            "Demonhunter", "Mageslayer", "Annihilator", "Inquisitor", "High Inquisitor", "Master Confessor", "Demonbane"
        },
        ["Paladin"] = new[]
        {
            "Apprentice", "Knave", "Squire", "Gallant", "Defender", "Cavalier", "Avenger", "Knight",
            "Crusader", "Templar", "Champion", "First Knight", "Lord|Lady Justice", "Supreme Justice", "Grand Exemplar"
        },
        ["Cleric"] = new[]
        {
            "Apprentice", "Auxiliary", "Venerator", "Acolyte", "Fighter Priest|Fighter Priestess", "Canon", "Warrior Priest|Warrior Priestess", "Guardian",
            "Chaplain", "Vicar", "Chancellor", "Rector", "High Cleric", "Divine Protector", "Exalted Shield"
        },
        ["Priest"] = new[]
        {
            "Apprentice", "Clergyman|Clergywoman", "Curate", "Pastor", "Reverend", "Parson", "Minister", "Cardinal",
            "Pontiff", "Bishop", "High Priest|High Priestess", "Archbishop", "Chosen", "Prophet", "Voice of God"
        },
        ["Missionary"] = new[]
        {
            "Apprentice", "Initiate", "Witness", "Rogue Priest|Rogue Priestess", "Converter", "Infiltrator", "Oracle", "Evangelist",
            "Diviner", "Faithbringer", "Zealot", "Divine Messenger", "Apostle", "Archangel", "God's Hand"
        },
        ["Ninja"] = new[]
        {
            "Apprentice", "Menace", "Cutthroat", "Stalker", "Killer", "Nightstalker", "Murderer", "Manhunter",
            "Nightblade", "Assassin", "Executioner", "Revenant", "Master Assassin", "Shadow Master|Shadow Mistress", "Death's Hand"
        },
        ["Thief"] = new[]
        {
            "Apprentice", "Rascal", "Footpad", "Pilferer", "Pickpocket", "Cutpurse", "Bandit", "Burglar",
            "Rogue", "Sharper", "Magsman", "Master Rogue", "Rogue Prince|Rogue Princess", "Underlord", "The Hand"
        },
        ["Bard"] = new[]
        {
            "Apprentice", "Jester", "Lyricist", "Entertainer", "Sonnateer", "Skald", "Troubador", "Musician",
            "Minstrel", "Swashbuckler", "Songweaver", "Chanteur|Chanteuse", "Virtuoso", "Artiste", "Maestro"
        },
        ["Gypsy"] = new[]
        {
            "Apprentice", "Scallywag", "Trickster", "Charlatan|Vixen", "Traveler", "Wanderer", "Wayfarer", "Nomad",
            "Voyager", "Arbiter", "Visionary", "Seer|Seeress", "Gypsy Prince|Gypsy Princess", "Lord|Lady of Fortune", "Fortune's Hand"
        },
        ["Warlock"] = new[]
        {
            "Apprentice", "Dabbler", "Spellslinger", "Occultist", "Cabalist", "Warrior Mage", "Erudite", "Rubicant",
            "Evoker", "Diabolist", "Spellbinder", "Swordsmage", "Battlemage", "Warmage", "Grand Magiavant"
        },
        ["Mage"] = new[]
        {
            "Apprentice", "Adept", "Prestidigator", "Illustionist", "Theurgist", "Conjurer", "Magician", "Sorcerer",
            "Arcanist", "Magus", "Wizard", "High Mage", "Archmage", "Lord|Lady Magus", "Supreme Archmagi"
        },
        ["Druid"] = new[]
        {
            "Apprentice", "Naturalist", "Cultivator", "Herbalist", "Elementalist", "Nature's Servant", "Sage", "Savant",
            "Shaman|Shamaness", "Astromancer", "High Druid", "Arch Druid", "Woodland Lord|Woodland Lady", "Lord|Lady of Nature", "Nature's Spirit"
        },
        ["Ranger"] = new[]
        {
            "Apprentice", "Strider", "Excursionist", "Scout", "Explorer", "Guide", "Woodsman|Woodswoman", "Courser",
            "Tracker", "Pathfinder", "Hunter|Huntress", "Ranger Lord|Ranger Lady", "Master Hunter|Huntress", "Lord|Lady of the Hunt", "Nature's Fury"
        },
        ["Mystic"] = new[]
        {
            "Apprentice", "Student", "Disciple", "Seeker", "Monk", "Kai Warrior", "Monk Lord|Monk Lady", "Maharishi",
            "Sensei", "Guru", "Lama", "Master|Mistress of the Way", "Kai Lord|Kai Lady", "Kai Master|Kai Mistress", "Supreme Kai"
        },
    };

    private static readonly string[] GenericTitleLadder =
    {
        "Apprentice", "Adventurer", "Adventurer", "Adventurer", "Adventurer", "Lady|Lord", "Lady|Lord", "Master", "Master",
        "Master", "Master", "Master", "Master", "Master", "Master"
    };

    public static bool UsesKai(CharacterClass cls)
    {
        return cls.MageryType == KaiMageryType;
    }

    public static bool UsesSpellcasting(CharacterClass cls)
    {
        return cls.MageryLvl > 0 && !UsesKai(cls);
    }

    public static string GetMagicResourceLabel(CharacterClass cls)
    {
        return UsesKai(cls) ? "Kai" : "Mana";
    }

    public static int GetLearnedSpellbookAbilityId(int spellId)
    {
        return LearnedSpellbookAbilityBase + spellId;
    }

    public static int CalculateMaxMana(CharacterClass cls, int level)
    {
        if (UsesKai(cls))
            return Math.Max(0, level - 1);

        if (cls.MageryLvl <= 0)
            return 0;

        return 6 + 2 * (cls.MageryLvl * level);
    }

    public static int CalculateSpellCasting(CharacterClass cls, int level, int intellect, int willpower, int charm)
    {
        if (!UsesSpellcasting(cls))
            return 0;

        int statBase = cls.MageryType switch
        {
            1 => ((3 * intellect) + willpower) / 6,
            2 => ((3 * willpower) + intellect) / 6,
            3 => (intellect + willpower) / 3,
            4 => ((3 * charm) + willpower) / 6,
            _ => 0,
        };

        return (2 * level) + statBase + (5 * cls.MageryLvl);
    }

    public int Lives { get; set; } = 9;
    public int Thievery { get; set; } = 0;
    public int Traps { get; set; } = 0;          // Find Traps
    public int DisarmTraps { get; set; } = 0;    // Disarm Traps — separate slot
    public int Picklocks { get; set; } = 0;
    public int Tracking { get; set; } = 0;
    public int MartialArts { get; set; } = 0;
    public int Dodge { get; set; } = 0;

    // Movement trail (room ring / map ring, ×20) read by TRACK. Lazily
    // allocated on first move, in-memory only (never persisted). See MovementTrail.
    private MovementTrail? _movementTrail;
    public MovementTrail? MovementTrail => _movementTrail;
    public void RecordMovementTrail(int mapNumber, int roomNumber)
    {
        _movementTrail ??= new MovementTrail(MovementTrail.PlayerCapacity);
        _movementTrail.Record(mapNumber, roomNumber);
    }
    public int CriticalHitBonus { get; set; } = 0;
    // Names in this set are permanently sysop: access is granted unconditionally, cannot be
    // revoked, and auto-restores on character load even if the persisted bit is 0.
    //
    // Empty by default, and it must stay that way in a public build — any name listed here is a
    // standing back door for whoever registers it. Populate it only in your own deployment, with
    // the operator accounts you actually control.
    public static readonly HashSet<string> IsAllowedNames = new(StringComparer.OrdinalIgnoreCase)
    {
    };

    private bool _isSysop;
    public bool IsSysop
    {
        get => _isSysop || IsAllowedNames.Contains(Name);
        set => _isSysop = value;
    }

    [JsonIgnore]
    public bool IsAllowed => IsAllowedNames.Contains(Name);

    public bool IsTestAccount { get; set; } = false;
    public bool IsToptenDisabled { get; set; } = false;

    // A "tester sysop": a limited operator allowed a small testing subset of SYSOP commands (MAP, GOD
    // with a restricted option set, LIST, GOTO, GIVEITEM, QUEST) but NONE of the full system powers.
    // Independent of IsSysop — a full sysop already outranks a tester and is never gated by the tester
    // checks. Granted via SYSOP ACCESS TESTER <user> ON.
    public bool IsTesterSysop { get; set; } = false;
    private string _name = "";
    private string _lastName = "";

    public string Name
    {
        get => _name;
        set => _name = NormalizeNamePart(value);
    }

    public string LastName
    {
        get => _lastName;
        set => _lastName = NormalizeNamePart(value);
    }

    public string BbsUserId { get; set; } = "";
    public string Gang { get; set; } = "";
    public long GangExperience { get; set; } = 0;
    public bool IsGangLieutenant { get; set; } = false;
    // Options map: Online / All for gang member listing output.
    public bool GangViewOnlineOnly { get; set; } = false;
    public string PasswordHash { get; set; } = "";
    public int RaceId { get; set; }
    public int ClassId { get; set; }
    public int Level { get; set; } = 1;
    public long Experience { get; set; }
    public int CurrentHP { get; set; }
    public int MaxHP { get; set; }
    public int CurrentMana { get; set; }
    public int MaxMana { get; set; }
    public int SpellCasting { get; set; }

    // Stats (effective = base + gear/buff modifiers)
    public int Strength { get; set; }
    public int Agility { get; set; }
    public int Intellect { get; set; }
    public int Willpower { get; set; }
    public int Health { get; set; }
    public int Charm { get; set; }

    // Base stats (natural, from rolling + CP allocation)
    public int BaseStrength { get; set; }
    public int BaseAgility { get; set; }
    public int BaseIntellect { get; set; }
    public int BaseWillpower { get; set; }
    public int BaseHealth { get; set; }
    public int BaseCharm { get; set; }

    // Stat-buff accumulators populated by ApplyAbility cases 44–49. Reset every
    // RecalculateStats, then folded into Strength/Agility/Intellect/Willpower/Health/Charm so
    // derived stats (Perception, MagicResist, MaxEncumbrance, Stealth, etc.) read the boosted
    // value. NOT persisted — recomputed each tick from base + active spells / item bonuses.
    // 24 stock spells carry these buffs (str/int/wis/agi/health/cha enhancement spells).
    public int StrengthBonus { get; set; }
    public int AgilityBonus { get; set; }
    public int IntellectBonus { get; set; }
    public int WillpowerBonus { get; set; }
    public int HealthBonus { get; set; }
    public int CharmBonus { get; set; }

    // Bonus pools for the derived stats that have BOTH a primary-driven formula AND an additive
    // ability bonus (cases 9/27/36/69/70/77). Cases write here; phase 4 of RecalculateStats does
    // `MaxMana = CalculateMaxMana(...) + MaxManaBonus` etc. Splitting these out (rather than
    // having the case write straight to the derived field) is what lets stat-buff abilities
    // affect the formula side: phase 4 computes the formula from buffed primaries, then adds
    // the ability bonus on top.
    public int MaxManaBonus { get; set; }
    public int SpellCastingBonus { get; set; }
    public int PerceptionBonus { get; set; }
    public int MagicResistBonus { get; set; }
    public int StealthBonus { get; set; }

    // Name normalization is a generic BBS concern; the implementation lives in the shared core.
    // Kept here as a forwarder so mmudreborn's many call sites stay unchanged.
    public static string NormalizeNamePart(string? value) => CWGaming.Shared.BbsText.NormalizeNamePart(value);

    // Cosmetics (stock-faithful options)
    public int Gender { get; set; }     // 0=Male, 1=Female
    public int HairLength { get; set; } // 0=None,1=Short,2=Shoulder-Length,3=Long,4=Waist-length,5=Ankle-Length
    public int HairColour { get; set; } // 0=Black,1=White,2=Silver,3=Red,4=Brown,5=Dark-Brown,6=Blonde,7=Green,8=Blue,9=Grey
    public int EyeColour { get; set; }  // 0=Black,1=Crimson,2=Yellow,3=Pale-Blue,...,16=Golden

    public string HeShe => Gender == 1 ? "She" : "He";
    public string HisHer => Gender == 1 ? "Her" : "His";
    public string HimHer => Gender == 1 ? "her" : "him";
    public string HimHer_Lower => Gender == 1 ? "her" : "him";
    public string HeShe_Lower => Gender == 1 ? "she" : "he";
    public string HisHer_Lower => Gender == 1 ? "her" : "his";

    // Derived stats
    // ArmourClass = the fighter Defense Value: the worn-item AC byte sum divided by 10.
    // Storage is the
    // post-divide display value — combat callers and the stat screen use it directly.
    public int ArmourClass { get; set; }
    // DamageResist remains in raw/10× scale: it is summed straight from the item DR bytes
    // (ScaleDamageResist applies the /10 inside the attack calculation).
    public int DamageResist { get; set; }
    public int MagicResist { get; set; }
    public int Perception { get; set; }
    public int Stealth { get; set; }
    // Ability 13 ("Alter User Light"): personal illumination — only this player benefits (racial
    // darkvision, a worn glow item, user-light spells). A viewer-only term in the light level.
    public int Illumination { get; set; }
    // Ability 14 ("Alter Room Light"): room illumination — lights the room for every
    // player present (a Sunsword, an "Alter Room Light" buff). Summed across occupants.
    public int RoomIllumination { get; set; }
    public bool HasSeeHidden { get; set; }
    public bool HasPerfectStealth { get; set; }
    public Dictionary<int, int> QuestAbilities { get; set; } = [];
    // Active spells/buffs (id, cast level, duration; max 10).
    // Persisted with the player; feeds RecalculateStats and is decremented by the upkeep tick.
    public List<ActiveSpell> ActiveSpells { get; set; } = [];

    // Recent deaths, newest first, capped at MaxDeathLogEntries. Persisted; rendered on `stat all` when
    // SYSOP CONFIGURE DEATHLOG is on. Non-stock (see DeathRecord).
    public List<DeathRecord> DeathLog { get; set; } = [];

    public const int MaxDeathLogEntries = 4;

    // The daily cleanup this character has already had applied ("Starting Cleanup" —
    // recharge ability-121 items). The cleanup only walks players who are ONLINE when it fires, so
    // everyone else gets it lazily on their next login; this stamp is what makes that lazy pass run
    // ONCE per cleanup instead of on every single login. Without it a spent charged item (the black
    // flail #349's ten casts, bug #218) refilled every time its owner reconnected.
    public DateTime LastCleanupAppliedUtc { get; set; } = DateTime.MinValue;

    // Who last hurt this character, for the death log's "what killed them". In-memory only and
    // deliberately not persisted: it is a fact about the current fight, and a character who logs back in
    // has no attacker. Set at the damage sites (CombatEngine's monster and PvP paths, traps, the poison
    // and DoT ticks); read once, at death.
    public string? LastDamageSourceName { get; set; }

    public void RecordDamageSource(string? sourceName)
    {
        if (!string.IsNullOrWhiteSpace(sourceName))
            LastDamageSourceName = sourceName.Trim();
    }

    /// <summary>
    /// Push a death onto the log, newest first, trimming to <see cref="MaxDeathLogEntries"/>. The killer
    /// falls back to whatever last damaged this character, then to a generic cause, so an entry always
    /// names something — a blank "died to nothing" line would be worse than an imprecise one.
    /// </summary>
    public void RecordDeath(DateTime whenUtc, int mapNumber, int roomNumber, string roomName, string? killer)
    {
        string attributed = !string.IsNullOrWhiteSpace(killer)
            ? killer.Trim()
            : (!string.IsNullOrWhiteSpace(LastDamageSourceName) ? LastDamageSourceName : "unknown");

        DeathLog.Insert(0, new DeathRecord
        {
            WhenUtc = whenUtc,
            MapNumber = mapNumber,
            RoomNumber = roomNumber,
            RoomName = roomName ?? string.Empty,
            Killer = attributed,
        });

        if (DeathLog.Count > MaxDeathLogEntries)
            DeathLog.RemoveRange(MaxDeathLogEntries, DeathLog.Count - MaxDeathLogEntries);

        LastDamageSourceName = null;
    }
    public int PartyAccuracyModifier { get; set; }
    public int PartyDefenceModifier { get; set; }
    public bool FollowModeBlind { get; set; }
    /// <summary>
    /// When true, BroadcastToRoom skips the reprompt for this player and clears
    /// the active line instead of pushing it into scrollback.  Set for followers
    /// during party movement and for the command issuer during ProcessCommand.
    /// </summary>
    public bool SuppressBroadcastReprompt { get; set; }

    /// <summary>
    /// When true, unsolicited async output such as room, realm, and direct
    /// broadcasts is suppressed for modal full-screen interfaces.
    /// </summary>
    public bool SuppressBroadcastOutput { get; set; }

    // Per-player communication anti-spam state (not persisted).
    public Queue<DateTime> CommunicationBurstTimestampsUtc { get; set; } = [];
    public DateTime CommunicationThrottleUntilUtc { get; set; } = DateTime.MinValue;

    // Runtime light command state (not persisted): active light timers, paused remaining duration, and recharge gates.
    public Dictionary<int, DateTime> ActiveLightUntilUtc { get; set; } = [];
    public Dictionary<int, TimeSpan> StoredLightRemaining { get; set; } = [];
    public Dictionary<int, DateTime> LightRechargeReadyAtUtc { get; set; } = [];

    // Location
    public int CurrentMapNumber { get; set; } = 1;
    public int CurrentRoomNumber { get; set; } = 1;

    // Currency
    public int Runic { get; set; }
    public int Platinum { get; set; }
    public int Gold { get; set; }
    public int Silver { get; set; }
    public int Copper { get; set; }

    // CP
    public int CharacterPoints { get; set; }
    public int SpentCP { get; set; }

    // Inventory
    public List<int> Inventory { get; set; } = [];
    public List<long> InventoryInstanceIds { get; set; } = [];
    public Dictionary<string, int> Equipment { get; set; } = []; // slot -> item number
    public Dictionary<string, long> EquipmentInstanceIds { get; set; } = [];
    public Dictionary<int, long> BankBalances { get; set; } = [];

    // Combat state
    public bool InCombat { get; set; }
    public MonsterInstance? CombatTarget { get; set; }
    public Player? PlayerCombatTarget { get; set; }
    // "Hits taken this combat beat." Incremented each time a monster picks
    // this player as its swing target, and the cross-player target roll is 0..99 < 50 − 5×this
    // (so each additional incoming swing lowers the odds of being chosen again — caps + spreads damage
    // across a party). Reset to 0 at the very start of every beat (ResetIncomingHitsThisTick). Transient
    // per-beat combat state, never persisted.
    [JsonIgnore]
    public int IncomingHitsThisTick { get; private set; }
    public void ResetIncomingHitsThisTick() => IncomingHitsThisTick = 0;
    public void RecordIncomingHitThisTick() => IncomingHitsThisTick++;
    [JsonIgnore]
    public KnockdownKind KnockdownKind { get; private set; }
    [JsonIgnore]
    public int KnockdownTicksRemaining { get; private set; }
    [JsonIgnore]
    public int KnockdownDescriptiveMessageId { get; private set; }
    [JsonIgnore]
    public bool IsKnockedDown => KnockdownTicksRemaining > 0;
    // Cross-thread: a shared monster dying removes itself from EVERY room player's attacker list
    // (CommandParser.Combat) on its killer's thread, while this player's own move thread snapshots the
    // same list. A plain List raced here ("collection modified during enumeration" aborted the move).
    // Guard every access with _incomingAttackersLock and expose only locked methods — there is no
    // lock-free built-in that keeps the ordered RemoveAll(predicate) semantics this needs.
    private readonly List<MonsterInstance> _incomingMonsterAttackers = [];
    private readonly object _incomingAttackersLock = new();

    public List<MonsterInstance> SnapshotIncomingMonsterAttackers()
    {
        lock (_incomingAttackersLock)
            return [.. _incomingMonsterAttackers];
    }

    public int IncomingMonsterAttackerCount
    {
        get { lock (_incomingAttackersLock) return _incomingMonsterAttackers.Count; }
    }

    public bool HasIncomingMonsterAttacker(MonsterInstance monster)
    {
        lock (_incomingAttackersLock)
            return _incomingMonsterAttackers.Contains(monster);
    }

    public void AddIncomingMonsterAttacker(MonsterInstance monster)
    {
        lock (_incomingAttackersLock)
            if (!_incomingMonsterAttackers.Contains(monster))
                _incomingMonsterAttackers.Add(monster);
    }

    public void RemoveIncomingMonsterAttackersWhere(Predicate<MonsterInstance> predicate)
    {
        lock (_incomingAttackersLock)
            _incomingMonsterAttackers.RemoveAll(predicate);
    }

    public void ReplaceIncomingMonsterAttackers(IEnumerable<MonsterInstance> monsters)
    {
        lock (_incomingAttackersLock)
        {
            _incomingMonsterAttackers.Clear();
            _incomingMonsterAttackers.AddRange(monsters);
        }
    }
    // Round gating faithful to stock: one primary attack action per combat round.
    public DateTime NextAttackAllowedAtUtc { get; set; } = DateTime.MinValue;
    // Cast token: one spell cast per combat round (offensive AND beneficial).
    // Token SET = may cast; a cast CONSUMES it, and the background energy pass re-grants
    // it to every terminal after the round resolves. (An earlier comment here claimed a
    // different flag; that one is actually an unrelated PVP combat-interrupt preference.)
    // Modelled as a deadline rather than a flag: a cast blocks until the next 5s pulse, and the combat
    // beat clears it for players whose round resolved, mirroring the post-round grant.
    public DateTime NextSpellAllowedAtUtc { get; set; } = DateTime.MinValue;
    // True once the player has cast a spell this combat round (token consumed). Drives the
    // +10% melee-EU penalty for swinging after acting.
    public bool HasCastThisRound
        => NextSpellAllowedAtUtc != DateTime.MinValue && DateTime.UtcNow < NextSpellAllowedAtUtc;
    // Monster retaliation cadence while engaged.
    public DateTime NextMonsterAttackAtUtc { get; set; } = DateTime.MinValue;

    // Item C — world-coordinated combat tick. Stock resolves every player's combat in one ordered
    // background pass; we mirror that with a single world coordinator
    // (GameWorld.Combat.cs). Command processing and the combat round serialize against each other on
    // the single global GameWorld.WorldStateGate (the one-cooperative-thread invariant), so the
    // unlocked combat fields above are never mutated by a command and the round at the same instant.

    // Bumped (interlocked) whenever combat output is actually sent to this player. The session's
    // mud-menu "silent meditation" exit watches it to detect a real combat hit and interrupt — the
    // signal that the old per-session resolution used to provide via its printedCombat return.
    [JsonIgnore]
    public long CombatOutputSeq;
    // Rapid-move counter: ++ per move, +1 move tick once it exceeds 2 (3rd+
    // move). Reset each slow tick (WorldTick). Runtime-only.
    [JsonIgnore]
    public int RecentMoveCount { get; set; }
    // Per-player action delay for NON-movement actions — door open/close (1 tick),
    // bashdoor/picklock (2), scripted skill delays. Unlike the move gate, this gates the player's NEXT
    // command of ANY kind (stock defers all queued input until the counter drains), so it is
    // waited off-gate for every command in GameSession. Distinct from the directional move delay (which
    // is paid before relocating, in ComputeMoveExposureDelay), so a plain move never delays a follow-up
    // combat/look command. Runtime-only.
    [JsonIgnore]
    public DateTime NextActionAllowedAtUtc { get; set; } = DateTime.MinValue;
    // Absolute stamina pool, aliased as CurrentEnergy. Starts empty; the world tick
    // (RefillStaminaOutOfCombat) clamps it to the cap out of combat, and PrepareCombatRound refills
    // it each combat round, so the first swing always sees a full pool. Drained per swing and cast.
    public int WeaponSwingEnergyRemainder { get; set; }
    // Stamina record: max and current. Drained per swing during combat;
    // refilled on each fast tick. Overshoot allowed while
    // actively engaged (autocombat). Runtime-only — not persisted; the cap is recomputed
    // at login via PlayerStatsService and the pool refills almost immediately.
    public int MaxStamina { get; set; } = WeaponSwingPreviewCalculator.DefaultPlayerMaxStamina;
    public int CurrentEnergy
    {
        get => WeaponSwingEnergyRemainder;
        set => WeaponSwingEnergyRemainder = value;
    }

    public int GetEffectiveMaxStamina()
        => MaxStamina > 0 ? MaxStamina : WeaponSwingPreviewCalculator.DefaultPlayerMaxStamina;

    /// <summary>
    /// Energy refill used by the combat pulse: refills stamina by
    /// the cap with overshoot allowed (player is engaged). Mirrors MonsterInstance.PrepareCombatRound.
    /// Stock only adds the cap when `curEnergy < max` (or the idle bit is clear);
    /// so a player engaging at a FULL pool does NOT overshoot to 2×cap (no opening-round burst).
    /// In sustained combat the carried remainder is always &lt; EU ≤ cap, so this fires every round.
    /// </summary>
    public void PrepareCombatRound()
    {
        int cap = GetEffectiveMaxStamina();
        if (CurrentEnergy < cap)
            CurrentEnergy = Math.Max(0, CurrentEnergy) + cap;
    }

    /// <summary>
    /// Energy refill used by the world (slow) tick for players that
    /// are NOT in combat: refills toward the cap but never overshoots. Mirrors the stock clamp.
    /// </summary>
    public void RefillStaminaOutOfCombat()
    {
        int cap = GetEffectiveMaxStamina();
        if (CurrentEnergy < cap)
            CurrentEnergy = cap;
    }
    public PlayerCombatRoundAction PendingCombatRoundAction { get; set; }
    [JsonIgnore]
    public int PendingCombatSpellId { get; set; }
    public bool IsSneaking { get; set; }
    public bool IsHidden { get; set; }

    // Per-room reveal of hidden ground ITEMS. In stock a stashed item stays hidden until a search
    // uncovers it; only then can you get it (it never moves to visible). The reveal is scoped to one
    // room and is dropped on every room entry (NotifyPlayerEnteredRoom), so leaving and returning
    // re-hides it. NOTE: hidden CURRENCY is deliberately NOT gated here — stock GET pulls stashed
    // coins straight from the hidden pool by exact amount with no search. See [[project-hidden-currency-fidelity-pin]].
    [JsonIgnore] private static readonly IReadOnlySet<long> NoRevealedHiddenItems = new HashSet<long>();
    [JsonIgnore] private readonly HashSet<long> _revealedHiddenItemInstanceIds = new();
    [JsonIgnore] public (int Map, int Room)? RevealedHiddenRoomKey { get; private set; }

    public void ClearRevealedHidden()
    {
        RevealedHiddenRoomKey = null;
        _revealedHiddenItemInstanceIds.Clear();
    }

    // A reveal belongs to exactly one room; recording a sighting in a new room drops the old room's.
    private void EnsureRevealScopedToRoom(int mapNumber, int roomNumber)
    {
        if (RevealedHiddenRoomKey != (mapNumber, roomNumber))
        {
            RevealedHiddenRoomKey = (mapNumber, roomNumber);
            _revealedHiddenItemInstanceIds.Clear();
        }
    }

    public void RevealHiddenItem(int mapNumber, int roomNumber, long instanceId)
    {
        EnsureRevealScopedToRoom(mapNumber, roomNumber);
        _revealedHiddenItemInstanceIds.Add(instanceId);
    }

    // The set of hidden item instances this player has uncovered in the given room (empty otherwise).
    public IReadOnlySet<long> GetRevealedHiddenItems(int mapNumber, int roomNumber)
        => RevealedHiddenRoomKey == (mapNumber, roomNumber) ? _revealedHiddenItemInstanceIds : NoRevealedHiddenItems;

    // Sysop testing modes (runtime-only, not persisted):
    // Full invisible: completely hidden from room display, movement messages, and monster aggro.
    // NoAggro: visible in room but monsters will not initiate combat.
    [JsonIgnore] public bool IsSysopInvisible { get; set; }
    [JsonIgnore] public bool IsSysopNoAggro { get; set; }
    // True while the character has "left the Realm" for the interactive TRAIN STATS editor (faithful:
    // training pulls you out of the world). Auto-set on entry / cleared on exit by RunStatScreen — it is
    // NOT a player-controllable mode. While set, the character is hidden from WHO and room display, can't
    // be aggroed/targeted, and is skipped by the combat beat and the slow world tick (no poison/regen) —
    // i.e. fully inert/out of the world. Runtime-only (never persisted), so a crash can't strand them out.
    [JsonIgnore] public bool IsOutOfRealm { get; set; }
    // Transient "recently spotted" flag (set when found while hidden);
    // reduces the next stealth attempt to 2/3. Decayed each WorldTick. See CalculateStealthChance.
    public bool RecentlySpotted { get; set; }
    // "Moved since last energy tick". With IsSneaking it lets you hide even
    // with monsters present (the window right after sneaking in). Cleared each WorldTick.
    public bool SneakedInThisTick { get; set; }
    public bool IsResting { get; set; }
    public bool IsMeditating { get; set; }
    public DateTime RestStartTime { get; set; }
    // Rest / meditate counters: incremented each fast
    // (1s) tick while resting/meditating and reset when the regen fires (and when the flag clears).
    // Runtime-only — not persisted.
    [JsonIgnore] public int RestRegenTicks { get; set; }
    [JsonIgnore] public int MeditateRegenTicks { get; set; }

    /// <summary>
    /// The instant a monster attacks the player it
    /// clears BOTH the rest and meditate flags, before the swing even
    /// resolves — so you stop resting/meditating the moment you are hit (or dropped by an AoE). The clear
    /// is silent; the combat output and prompt refresh make it visible. Returns true if either was set.
    /// </summary>
    public bool BreakRestAndMeditate()
    {
        bool wasActive = IsResting || IsMeditating;
        IsResting = false;
        IsMeditating = false;
        RestRegenTicks = 0;
        MeditateRegenTicks = 0;
        return wasActive;
    }

    // Unconscious state: HP between 0 and DeathHP means unconscious/bleeding
    // WCCMMHLP.MSG: "Once your HP reaches a certain negative number, usually -15, you will die."
    // Stock string: "Death Setting: %d" — sysop configurable
    public bool IsUnconscious => CurrentHP <= 0 && CurrentHP > DeathHP;

    public static string FormatGoldCrownsFromCopper(long copperAmount)
    {
        long normalized = Math.Max(0, copperAmount);
        long wholeGold = normalized / CurrencyHelper.CopperPerGold;
        long copperRemainder = normalized % CurrencyHelper.CopperPerGold;
        return string.Create(CultureInfo.InvariantCulture, $"{wholeGold:N0}.{copperRemainder:00}");
    }
    public bool IsAided { get; set; } // someone used AID to stop bleeding
    public const int DefaultDeathHP = -15;
    public static int DeathHP { get; private set; } = DefaultDeathHP;

    public static int NormalizeDeathHP(int deathHp)
    {
        if (deathHp > 0)
            return -deathHp;

        return Math.Min(-1, deathHp);
    }

    public static void ConfigureDeathHP(int deathHp)
    {
        DeathHP = NormalizeDeathHP(deathHp);
    }

    public void ClearAidIfRecovered()
    {
        if (IsAided && CurrentHP >= 1)
            IsAided = false;
    }

    public bool ApplyBleedTick()
    {
        if (!IsUnconscious || IsAided)
            return false;

        CurrentHP = Math.Max(DeathHP, CurrentHP - 1);
        if (CurrentHP <= DeathHP)
        {
            IsAided = false;
            return true;
        }

        return false;
    }

    public void ApplyAidRecoveryTick()
    {
        if (!IsAided)
            return;

        if (CurrentHP < 1)
            CurrentHP++;

        if (CurrentHP >= 1)
            IsAided = false;
    }

    // EvilPoints IS the canonical alignment value, matching the single alignment
    // slot in the stock player record). Stock convention is positive = evil, negative = good
    // (a negative delta adds to alignment when the source killed evil,
    // adjusted up/down so a Saint sits around −300 and FIEND sits well above 0). Alignment
    // banding is done by CombatEngine.GetPlayerAlignment(EvilPoints); higher = more evil.
    public float EvilPoints { get; set; }

    // Alignment is a thin alias for the canonical EvilPoints field. The legacy DB column
    // (Alignment int) is preserved for back-compat — PlayerRepository still reads/writes it,
    // but the setter routes into EvilPoints. On the first save after this lands, the legacy
    // column gets rewritten with the canonical scale (positive = evil) and the two diverge no
    // more. New code should read/write EvilPoints directly; this alias exists only so the older
    // sysop output and persistence wiring keep compiling.
    public int Alignment
    {
        get => (int)Math.Round(EvilPoints);
        set => EvilPoints = value;
    }

    // "Lawful" character flag. While set, an evil-point gain refuses any
    // positive delta (prints "To do this action, you must turn unlawful first!"). Set at
    // character creation for paragon classes; toggleable only via sysop (per stock).
    public bool IsLawful { get; set; }

    public float EvilPointsForgivenToday { get; set; }

    /// <summary>
    /// NON-STOCK player-set floor for the passive evil-point forgiveness drift, gated realm-wide by
    /// SYSOP CONFIGURE MINEPS (default OFF). A character who has earned an evil standing loses it while
    /// scripting overnight — forgiveness ticks them Outlaw → Seedy and the worn-item recheck then
    /// strips the gear that band allowed ("Your ... has been removed."). Setting a minimum stops the
    /// forgiveness tick at that value; it never GRANTS evil points, so the standing still has to be
    /// earned the normal way. Only that passive drift is clamped: the stock `forgive` refund, quest
    /// absolution and sysop changes still move a character freely.
    ///
    /// The default is the alignment scale's own bottom (-220, GameWorld.EvilPointForgivenessFloor —
    /// duplicated here because the model layer does not reference the server layer), so "unset" and
    /// "no floor" are the same value and no separate enabled flag is needed.
    /// </summary>
    public float MinEvilPoints { get; set; } = NoEvilPointFloor;

    public const float NoEvilPointFloor = -220f;

    public int LastEvilPointForgivenessDayNumber { get; set; }

    [JsonIgnore]
    public float CurrentEPs
    {
        get => EvilPoints;
        set => EvilPoints = value;
    }

    // A positive evil-point gain is refused once EvilPoints has passed 300
    // ("You have progressed too far to the evil side to do this action."). The gate is checked BEFORE
    // the add and there is NO upper clamp — so a single gain from <=300 lands slightly over (e.g.
    // 295+10 = 305, or up to ~330 from a PvP x3) and then freezes. Stock does NOT clamp to exactly 300.
    public const int EvilPointGainCap = 300;

    /// <summary>
    /// Positive deltas (`delta &gt; 0`) are refused when IsLawful is
    /// set, mirroring the "must turn unlawful first" hard gate, OR once EvilPoints has passed the
    /// <see cref="EvilPointGainCap"/> ("progressed too far"). Negative deltas (good actions / quest
    /// rewards / forgiveness) are always applied. Returns true when EvilPoints actually changed.
    /// Forgiveness drift is handled separately in WorldTick — that path uses a different rule and
    /// does NOT route through here. Sysop EP edits write EvilPoints directly and bypass this gate.
    /// </summary>
    public bool TryAddEvilPoints(float delta)
    {
        if (delta == 0f)
            return false;

        if (delta > 0f && IsLawful)
            return false;

        // Value cap: once over the cap, no further evil can be earned (the gain freezes at the
        // overshoot). Always enforced — the SYSOP EVILCAPBLOCK setting only controls whether the
        // evil ACTION is additionally blocked, not whether the points stop accruing.
        if (delta > 0f && EvilPoints > EvilPointGainCap)
            return false;

        EvilPoints += delta;
        return true;
    }

    public void StopCombatLoop()
    {
        InCombat = false;
        NextAttackAllowedAtUtc = DateTime.MinValue;
        WeaponSwingEnergyRemainder = 0;
        PendingCombatRoundAction = PlayerCombatRoundAction.None;
        PendingCombatSpellId = 0;
    }

    public void RemoveIncomingMonsterAttacker(MonsterInstance monster)
    {
        bool empty;
        lock (_incomingAttackersLock)
        {
            _incomingMonsterAttackers.Remove(monster);
            empty = _incomingMonsterAttackers.Count == 0;
        }
        if (ReferenceEquals(CombatTarget, monster))
            CombatTarget = null;
        if (empty && PlayerCombatTarget == null)
            NextMonsterAttackAtUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Going down mortally wounded ends YOUR fight but not the monsters'. Stops your own loop and drops
    /// your chosen targets, while deliberately KEEPING the incoming-attacker list and the beat schedule
    /// so the monsters standing over you keep swinging — which is how a downed player actually dies.
    ///
    /// ClearCombatState() is the wrong call here: it also empties _incomingMonsterAttackers and resets
    /// NextMonsterAttackAtUtc, which drops the player out of GatherMonstersWithCandidates entirely
    /// (that gate needs IsCombatBeatDue) and leaves them lying there untouched forever. The combat-path
    /// drop already did this correctly by hand; the world-tick drops (poison, bleed, lifeforce drain,
    /// room-spell upkeep) called ClearCombatState and silently made the player unkillable.
    /// </summary>
    public void DropMortallyWoundedKeepingAttackers()
    {
        StopCombatLoop();
        CombatTarget = null;
        PlayerCombatTarget = null;
    }

    public void ClearCombatState()
    {
        StopCombatLoop();
        CombatTarget = null;
        PlayerCombatTarget = null;
        NextMonsterAttackAtUtc = DateTime.MinValue;
        lock (_incomingAttackersLock)
            _incomingMonsterAttackers.Clear();
    }

    public void ApplyKnockdown(KnockdownKind kind, int duration, int descriptiveMessageId = 0)
    {
        if (duration <= 0)
        {
            ClearKnockdown();
            return;
        }

        KnockdownKind = kind;
        KnockdownTicksRemaining = duration;
        KnockdownDescriptiveMessageId = descriptiveMessageId;
    }

    public bool TickKnockdown()
    {
        if (!IsKnockedDown)
            return false;

        KnockdownTicksRemaining--;
        if (KnockdownTicksRemaining > 0)
            return false;

        ClearKnockdown();
        return true;
    }

    public void ClearKnockdown()
    {
        KnockdownKind = KnockdownKind.None;
        KnockdownTicksRemaining = 0;
        KnockdownDescriptiveMessageId = 0;
    }

    // Talk mode: 0 = slow (requires . prefix), 1 = fast (unrecognized = say)
    public int TalkMode { get; set; } = 0;

    // Joined broadcast channel: 0 = none, otherwise a user-selected channel number.
    public int BroadcastChannel { get; set; } = 0;

    // Brief mode: false = show full room descriptions on entry, true = abbreviated
    public bool BriefMode { get; set; } = false;

    // Colour palette: stock palettes start at 0, with future custom palettes registered centrally.
    public int PaletteId { get; set; } = 0;

    // Statline / prompt mode: 0 = OFF (minimal ":" prompt), 1 = ON (standard
    // [HP/MA] prompt — the default), 2 = FULL (standard prompt; a bare Enter re-shows the room),
    // 3 = CUSTOM (render CustomStatline), 4 = FULL CUSTOM (custom prompt + bare-Enter room).
    // Set via SET STATLINE ... or the standalone STATLINE command.
    public int StatlineMode { get; set; } = 1;

    // Custom statline template (≤60 chars) with %-variables — SET STATLINE [FULL] CUSTOM <template>.
    public string CustomStatline { get; set; } = string.Empty;

    // Cached experience threshold for the NEXT level, refreshed in RecalculateStats so the %X custom
    // statline variable can render without a DB lookup at prompt-build time.
    [JsonIgnore]
    public long ExpForNextLevelCached { get; set; }

    // Receive mode: true = other players may give you items and money.
    public bool ReceiveItemsEnabled { get; set; } = true;

    // Safety mode: true = hostile actions that would incur evil points are blocked.
    public bool WarnOnEvilEnabled { get; set; } = false;

    // Realm channels: true = this player receives gossip messages.
    public bool ReceiveGossipEnabled { get; set; } = true;

    // Realm channels: true = this player receives auction messages.
    public bool ReceiveAuctionEnabled { get; set; } = true;

    // Messaging style: false = fantasy, true = technical.
    public bool UseTechnicalStyle { get; set; } = false;

    // Action mode: true = social action commands like GIRN and SPIT are enabled.
    public bool ActionsEnabled { get; set; } = true;

    // Channel ignores: suppresses remote communications like telepath and gossip.
    public HashSet<string> IgnoredPlayerNames { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // Optional local-auth password for dangerous local commands (suicide/reroll).
    public string SuicideRerollPassword { get; set; } = "";

    // SET KEEP — whether this player retains kept experience on reroll (requires sysop also enabled).
    public bool KeepMode { get; set; } = true;

    // Drop-carrier flag, set when the penalty is applied and read
    // on the character's NEXT login to print "Last time you were on, you disconnected while playing." /
    // "The gods have punished you appropriately.", which is where the player finally learns what happened —
    // there was nobody on the socket to tell at the time. Cleared by that login (stock zeroes the whole
    // status word), so it must persist across the logout in between.
    public bool DisconnectedWhilePlaying { get; set; }

    // LOOK style: false = traditional equipment list, true = modern full-slot equipment list.
    public bool UseModernLookStyle { get; set; } = false;

    // Backstab modifiers (from items/abilities)
    public int BSAccuracy { get; set; }
    public int BSMinDamage { get; set; }
    public int BSMaxDamage { get; set; }

    public bool HasRaceStealth { get; set; }
    public bool HasClassStealth { get; set; }

    // Mystic bonus damage (from items/abilities)
    public int PunchDamage { get; set; }
    public int KickDamage { get; set; }
    public int JumpkickDamage { get; set; }
    public int MinDamage { get; set; } // global min dmg bonus
    public int MaxDamage { get; set; } // global max dmg bonus

    // Blur AC fields — both kept in display scale (same as ArmourClass), so
    // GetTotalAC() returns the value combat math is fed.
    // Ability 2 sums the raw value;
    // ApplyAbility mirrors that one-to-one.
    public int ACAbility { get; set; }      // flat AC from abilities/spells (display scale)
    public int ACBlur { get; set; }         // blur AC from ability 10; scaled in GetBlurAC()
    // Blur (ability 10) is divided by the
    // heaviest worn armour weight-class (Item.ArmourType): types 3-6 ÷2, 7/8 ÷3, 9 ÷4,
    // else ÷1. Recomputed each RecalculateStats from worn items; consumed by GetBlurAC/the display path.
    public int BlurArmourDivisor { get; set; } = 1;
    // Prompt resource label: "Kai" (kai class) / "MA" (mana caster) / null
    // (no resource — pure martial). Derived from the current class in RecalculateStats; consumed by the
    // prompt builders to decide whether to show the second resource and which label to use. Not persisted
    // (always recomputed), so a stale MaxMana from a class change can never resurrect a phantom MA tag.
    [JsonIgnore]
    public string? MagicResourceLabel { get; set; }
    public int DRAbility { get; set; }      // flat DR from abilities/spells
    public int DRPercent { get; set; }      // DR percent modifier from ability 99; applied in GetTotalDR()
    public int MaxDamageAbility { get; set; } // max-damage bonus from ability 4; added to max only, both unarmed and weapon paths
    public int AvAbility { get; set; }      // accuracy bonus from abilities 22/105/106 (aggregated by max)
    public int HpRegenBonus { get; set; }   // HP-regen % from ability 123
    public int ManaRegenBonus { get; set; } // mana-regen % from ability 145
    // Shadowform (ability 178): the description path reads the ability value
    // and, when nonzero, prints textblock #value INSTEAD of the name header + appearance + equipment
    // list — hiding the target's identity and gear (PVP). 0 = no shadowform, show normal look. Stores a
    // textblock id, not a magnitude, so ApplyAbility ASSIGNS (last-wins) rather than sums — two textblock
    // ids must never add. Death shroud item #1579 = 9652; shadowform spell #130 = 4157.
    public int ShadowformTextblock { get; set; }
    public int PoisonLevel { get; set; }    // poison: HP lost per upkeep tick until cured
    public int Encumbrance { get; set; }    // current encumbrance
    public int MaxEncumbrance { get; set; } // max encumbrance

    // Ward shadow (anti-backstab from abilities)
    public int Ward { get; set; }
    public int Shadow { get; set; }

    // Martial attack ability gates
    public bool HasPunch { get; set; }
    public bool HasKick { get; set; }
    public bool HasBash { get; set; }
    public bool HasSmash { get; set; }
    public bool HasJumpkick { get; set; }

    // Weapon heavy flag
    public bool IsWeaponHeavy { get; set; }

    // Afraid flag, set from a fear/terror buff carrying ability 60. While afraid
    // the player can't cast or attack ("You are too afraid!"). Derived in RecalculateStats from active
    // buffs; cleared when the fear buff expires.
    public bool IsAfraid { get; set; }
    // HoldPerson root (ability 74). Set from ANY active/worn ability-74
    // source during RecalculateStats (ApplyAbility case 74), mirroring IsAfraid. Stock checks this
    // flag in movement code ONLY ("You can't seem to move anywhere!") — it does NOT block attack/cast.
    // Covers all 41 stock ability-74 spells (hold person, entangle, web, paralyze, …), not just one.
    [JsonIgnore]
    public bool IsRooted { get; set; }

    // Blind flag, set via
    // ability 107. Causes 10 to be subtracted from both
    // AV and the dodge-skill column for the affected fighter, modelling a player who can't see.
    // Derived in RecalculateStats; cleared when the blind buff (e.g. spell 77) expires.
    public bool IsBlinded { get; set; }

    // Mute flag, set via ability 76.
    // While muted the entire say/action handler and the tell path
    // are skipped — communication is suppressed. Derived in RecalculateStats from active buffs
    // (mute #68, song of silence #270, wrathful curse #853); cleared when the buff expires.
    [JsonIgnore]
    public bool IsMuted { get; set; }

    // Second blind trigger: the room light level > 900 — a
    // "blinding-bright" room (the 45 Map-17 celestial rooms at Light 900–1000, where the room-wide light
    // clamps to 900 and the viewer's own light-source ability 0xd pushes the effective level over the
    // top). Same −10 AV / −10 dodge-skill penalty as magical blindness. Transient: recomputed from the
    // player's room each combat marshal (GameWorld.RefreshBrightLightBlindness), never persisted.
    [JsonIgnore]
    public bool IsBlindedByBrightLight { get; set; }

    // The dodge-skill column reads abilities
    // 25 (normal-attack context) and 24 (backstab/alt context). Functionally summed across
    // race/class/items/spells in this model — fewer than a handful of legacy sources carry these,
    // so the additive upper bound is faithful in practice. Populated by ApplyAbility 24/25; reset
    // in RecalculateStats.
    public int DodgeSkillAbility { get; set; }

    // The dodge-skill column gets +10 when the player has ability
    // 9 (ShadowStealth) from any source (presence check, not value sum). Ability 9 also feeds the
    // Stealth stat via ApplyAbility, so we keep this presence flag separately. Derived in
    // RecalculateStats; cleared then set by ApplyAbility case 9.
    public bool HasShadowStealth { get; set; }

    public int GetTotalAC()
    {
        return ArmourClass + GetBlurAC();
    }

    public int GetBlurAC()
    {
        int tempAC = ACAbility;
        // The blur value (ability 10) is
        // divided by the heaviest worn armour weight-class (÷2/÷3/÷4, else ÷1) — NOT scaled by
        // encumbrance. The result lands on the AC/defense (fighter[1]) channel.
        if (ACBlur > 0)
            tempAC += ACBlur / BlurArmourDivisor;
        return tempAC;
    }

    // The AC/DR pair shown on the `stat` screen (the caller
    // displays the return /10 as "Armour Class: AC/DR"):
    //
    //   AC = Σ worn AC bytes + blur×10 + (ability 2 sum)×10   (blur ÷ weight-class first)
    //   DR = Σ worn DR bytes + (ability 7 sum)
    // A single ability lookup returns the ability-7 sum (folded into DR) AND
    // writes the ability-2 sum into its out-param (folded into AC×10). So the display routes ability 2
    // (natural armour) → AC and ability 7 (toughness) → DR — the SAME mapping combat uses (GetTotalAC /
    // GetTotalDR), not a crossed one. It omits only the frail (ability 99) DR% scaling, which is a
    // combat-fighter-only step. Worked example — barkskin at L40 (ability2 +5, ability7 magnitude 30 in
    // tenths) on AC 26/2: AC → 26+5 = 31, DR → (20+30)/10 = 5, i.e. 31/5 (matches stock).
    public int GetDisplayArmourRating(out int displayDr)
    {
        int blurContribution = ACBlur > 0 ? ACBlur / BlurArmourDivisor : 0;
        int displayAc = ArmourClass + ACAbility + blurContribution;   // ability 2 (natural armour) + blur
        if (displayAc < 0)
            displayAc = 0;                                            // the AC return clamps at 0
        displayDr = (DamageResist + DRAbility) / 10;                  // ability 7 (toughness); no blur, no frail%
        return displayAc;
    }

    public int GetTotalDR()
    {
        int dr = DamageResist + DRAbility;
        // Ability 99 ("Alter DR by percent")
        // scales the final DR by ((percent + 100) / 100) — e.g. a "frail" debuff of -50 halves DR.
        if (DRPercent != 0)
            dr = ((DRPercent + 100) * dr) / 100;
        return dr;
    }

    public int GetCarriedCurrencyCoinCount()
    {
        return Math.Max(0, Runic)
            + Math.Max(0, Platinum)
            + Math.Max(0, Gold)
            + Math.Max(0, Silver)
            + Math.Max(0, Copper);
    }

    public int GetCarriedCurrencyWeight()
    {
        return CalculateCurrencyWeight(Runic, Platinum, Gold, Silver, Copper);
    }

    public static int CalculateCurrencyWeight(long runic, long platinum, long gold, long silver, long copper)
    {
        return (int)(Math.Max(0, runic) / 3
            + Math.Max(0, platinum) / 3
            + Math.Max(0, gold) / 3
            + Math.Max(0, silver) / 3
            + Math.Max(0, copper) / 3);
    }

    public static int CalculateBaseMaxEncumbrance(int strength)
    {
        int baseMax = strength <= 100
            ? 48 * strength
            : (84 * strength) - 3600;
        return Math.Max(1, baseMax);
    }

    public static int CalculateMaxEncumbrance(int strength, int alterEncumbrancePercent)
    {
        long baseMax = CalculateBaseMaxEncumbrance(strength);
        long adjusted = baseMax * (100L + alterEncumbrancePercent) / 100L;
        return (int)Math.Clamp(adjusted, 1, int.MaxValue);
    }

    public void RecalculateEquipment(Data.IGameDatabase db)
    {
        if (db.Races.TryGetValue(RaceId, out var race) && db.Classes.TryGetValue(ClassId, out var cls))
            RecalculateStats(race, cls, db);

        int totalAC = 0;
        int totalDR = 0;
        int totalEncumbrance = GetCarriedCurrencyWeight();
        bool weaponHeavy = false;
        // Blur divisor flags — set by the heaviest worn armour weight-class.
        bool blurDiv2 = false, blurDiv3 = false, blurDiv4 = false;

        foreach (var (_, itemId) in Equipment)
        {
            if (!db.Items.TryGetValue(itemId, out var item))
                continue;

            totalAC += item.ArmourClass;
            totalDR += item.DamageResist;
            totalEncumbrance += Math.Max(0, item.Encum);
            if (item.IsWeapon && item.StrReq > Strength)
                weaponHeavy = true;

            // Each worn item's armour-type
            // raises a weight-class flag; the heaviest wins the blur divisor below.
            int at = item.ArmourType;
            if (at >= 3 && at <= 6) blurDiv2 = true;
            else if (at == 7 || at == 8) blurDiv3 = true;
            else if (at == 9) blurDiv4 = true;
        }

        foreach (var itemId in Inventory)
        {
            if (!db.Items.TryGetValue(itemId, out var item))
                continue;

            totalEncumbrance += Math.Max(0, item.Encum);
        }

        // ArmourClass = the raw worn-AC sum / 10, where the sum is
        // taken across all 20 worn slots. The /10 is applied once on the
        // accumulated sum (not per-item) so we drop the same fractional ones-digit stock would.
        ArmourClass = totalAC / 10;
        DamageResist = totalDR;
        Encumbrance = totalEncumbrance;
        IsWeaponHeavy = weaponHeavy;
        // Heaviest worn weight-class wins (÷4 before ÷3 before ÷2; ÷1 if no armour worn).
        BlurArmourDivisor = blurDiv4 ? 4 : blurDiv3 ? 3 : blurDiv2 ? 2 : 1;
    }

    private static int CalculatePerception(int intellect, int willpower, int charm)
    {
        return ((5 * intellect) + (2 * willpower) + charm) / 8;
    }

    private static int CalculateMagicResist(int intellect, int willpower)
    {
        return ((3 * willpower) + intellect) / 4;
    }

    private static int CalculateStealth(int level, int agility, int intellect, int charm, bool hasRaceStealth, bool hasClassStealth)
    {
        if (!hasRaceStealth && !hasClassStealth)
            return 0;

        int stealth = (charm / 6) + (agility / 4) + CalculateStealthLevel(level) + (intellect / 8) + 20;
        if (hasRaceStealth && !hasClassStealth)
            stealth -= 15;
        else if (hasRaceStealth && hasClassStealth)
            stealth += 10;

        return Math.Max(0, stealth);
    }

    private static int CalculatePicklocks(int level, int agility, int intellect)
    {
        return Math.Max(0, (2 * (agility + intellect + (10 * CalculateSkillLevel(level)))) / 7);
    }

    private static int CalculateThievery(int level, int agility, int intellect, int charm)
    {
        return Math.Max(0, (agility + intellect + charm + (24 * CalculateSkillLevel(level))) / 6);
    }

    private static int CalculateFindTraps(int level, int agility, int intellect, int charm)
    {
        return Math.Max(0, (intellect + agility + (2 * charm) + (28 * CalculateSkillLevel(level))) / 7);
    }

    private static int CalculateTracking(int level, int intellect, int willpower, int charm)
    {
        return Math.Max(0, ((2 * intellect) + willpower + charm + (40 * CalculateSkillLevel(level))) / 8);
    }

    private static int CalculateSkillLevel(int level)
    {
        return level <= 15 ? level : 15 + ((level - 15) / 2);
    }

    private static int CalculateStealthLevel(int level)
    {
        return level <= 15 ? level * 2 : level + 15;
    }

    private static int CalculateBaseCritChance(int level, int intellect, int agility, int charm)
    {
        int crits = (level / 10)
            + ((intellect - 50) / 10)
            + ((agility - 50) / 20)
            + ((charm - 50) / 30);
        return Math.Clamp(crits, 1, 75);
    }

    private static int CalculateMartialArts(int level, int agility, int charm, int baseCritChance, int criticalHitBonus, int dodgeBonus, bool hasJumpkick)
    {
        // Ability 34 (dodge) + baseCrit + critBonus + Charm/10
        // + Agility/5 + Level/5. Some readings show this as a 2nd baseCrit
        // term, but stock captures confirm it is the dodge-ability value — e.g. Mystic
        // 25 + 1 + 10 + 4 + 8 = 48, then jumpkick (48+1)*2 = 98 (matches stock); Human Warrior 0 + 1 +
        // 0 + 4 + 8 = 13.
        int martialArts = dodgeBonus
            + baseCritChance
            + criticalHitBonus
            + (charm / 10)
            + (agility / 5)
            + (level / 5);

        return hasJumpkick ? (martialArts + level) * 2 : martialArts;
    }

    private static int CalculateDodge(int level, int agility, int charm, int dodgeBonus)
    {
        return dodgeBonus
            + ((charm - 50) / 5)
            + (level / 5)
            + ((agility - 50) / 3);
    }

    private bool HasAbility(Race? race, CharacterClass? cls, Data.IGameDatabase db, int abilityId)
    {
        return (race?.Abilities.ContainsKey(abilityId) ?? false)
            || (cls?.Abilities.ContainsKey(abilityId) ?? false)
            || QuestAbilities.ContainsKey(abilityId)
            || GetEquippedAbilityBonus(db, abilityId) != 0
            || HasActiveSpellAbility(db, abilityId);
    }

    private bool HasActiveSpellAbility(Data.IGameDatabase db, int abilityId)
    {
        foreach (var active in ActiveSpells)
        {
            if (active.SpellId > 0 && db.Spells.TryGetValue(active.SpellId, out var spell)
                && spell.Abilities.ContainsKey(abilityId))
            {
                return true;
            }
        }

        return false;
    }

    private int GetAbilityBonus(Race? race, CharacterClass? cls, Data.IGameDatabase db, int abilityId)
        => AggregateAbility(race, cls, db, abilityId);

    // Aggregate an ability across sources (innate/active-spells/race/
    // class/worn/wielded/carried). Sum by default; MAX (sentinel-initialised) for the AV/regen set;
    // a buff carrying ability 124 whose value is this id suppresses the result. Carried-inventory
    // items contribute when ItemType != 1 (not a weapon) AND Worn == 0 (not wearable at all) —
    // matches the stock skip flags for weapons and wearables.
    private int AggregateAbility(Race? race, CharacterClass? cls, Data.IGameDatabase db, int abilityId)
    {
        bool useAverage = abilityId == HasteSlowAbilityId;   // ability 87 → pairwise average
        bool useMax = !useAverage && MaxAggregatedAbilities.Contains(abilityId);
        int acc = useMax ? AbilityMaxSentinel : 0;
        bool averageSeeded = false;
        bool suppressed = false;

        void Apply(int value)
        {
            if (useAverage)
            {
                // Ability 87: the first source seeds the accumulator; each later
                // source averages in: acc = (acc + value) / 2.
                acc = averageSeeded ? (acc + value) / 2 : value;
                averageSeeded = true;
            }
            else if (useMax)
            {
                if (value > acc)
                    acc = value;
            }
            else
            {
                acc += value;
            }
        }

        if (QuestAbilities.TryGetValue(abilityId, out var qv))
            Apply(qv);

        foreach (var active in ActiveSpells)
        {
            if (active.SpellId <= 0 || !db.Spells.TryGetValue(active.SpellId, out var spell))
                continue;

            if (spell.Abilities.TryGetValue(abilityId, out var sv))
                Apply(sv != 0 ? sv : active.CastLevel);

            if (spell.Abilities.TryGetValue(124, out var suppressId) && suppressId == abilityId)   // 124 = suppressor
                suppressed = true;
        }

        if (race != null && race.Abilities.TryGetValue(abilityId, out var rv))
            Apply(rv);
        if (cls != null && cls.Abilities.TryGetValue(abilityId, out var cv))
            Apply(cv);

        foreach (var (_, itemId) in Equipment)
        {
            if (db.Items.TryGetValue(itemId, out var item) && item.Abilities.TryGetValue(abilityId, out var iv))
                Apply(iv);
        }

        foreach (int itemId in Inventory)
        {
            if (!db.Items.TryGetValue(itemId, out var item))
                continue;
            if (item.ItemType == 1 || item.Worn != 0)
                continue;
            if (item.Abilities.TryGetValue(abilityId, out var iv))
                Apply(iv);
        }

        if (suppressed)
            return 0;

        return useMax && acc == AbilityMaxSentinel ? 0 : acc;
    }

    private int GetEquippedAbilityBonus(Data.IGameDatabase db, int abilityId)
    {
        int bonus = 0;

        foreach (var (_, itemId) in Equipment)
        {
            if (!db.Items.TryGetValue(itemId, out var item))
                continue;

            bonus += item.Abilities.GetValueOrDefault(abilityId);
        }

        return bonus;
    }

    public bool HasActiveAbility(Data.IGameDatabase db, int abilityId)
    {
        db.Races.TryGetValue(RaceId, out var race);
        db.Classes.TryGetValue(ClassId, out var cls);
        return HasAbility(race, cls, db, abilityId);
    }

    public int GetActiveAbilityValue(Data.IGameDatabase db, int abilityId)
    {
        db.Races.TryGetValue(RaceId, out var race);
        db.Classes.TryGetValue(ClassId, out var cls);
        return AggregateAbility(race, cls, db, abilityId);
    }

    public int GetWeaponMin()
    {
        return Math.Max(1, Strength / 5);
    }

    public int GetWeaponMax()
    {
        return Math.Max(3, Strength / 3);
    }

    // Mystic unarmed attack damage bounds — set by RecalculateUnarmedDamage (called from
    // RecalculateStats, the canonical recompute that runs on load/equip/level/buff), so the accessors
    // always return the current values, exactly like the other derived stats (MartialArts, Dodge, …).
    private int _punchMin, _punchMax, _kickMin, _kickMax, _jumpkickMin, _jumpkickMax;

    public int GetPunchMin() => _punchMin;
    public int GetPunchMax() => _punchMax;
    public int GetKickMin() => _kickMin;
    public int GetKickMax() => _kickMax;
    public int GetJumpkickMin() => _jumpkickMin;
    public int GetJumpkickMax() => _jumpkickMax;

    // Unarmed paths (types 1/2/3). Base table uses the level CAPPED at 20
    // (past 20 it stops scaling) times the grant ability
    // value (Punch 29 / Kick 30 / Jumpkick 35, =1 in stock data):
    //   min = effLvl*grant/8 + 2 ; punch max = (effLvl+3)*grant/4 + 6 ; kick max = effLvl*grant/6 + 7 ;
    //   jumpkick max = effLvl*grant/6 + 8.
    // Then, in stock order: add the Strength bonus and clamp Dmin to Dmax; add the global Max-Damage
    // ability (4) to max; add the per-type damage ability (92/93/94 PunchDmg/KickDmg/JumpKDmg) to both.
    private void RecalculateUnarmedDamage(Race? race, CharacterClass? cls, Data.IGameDatabase db)
    {
        int effLevel = Math.Min(Level, 20);

        int punchGrant = GetAbilityBonus(race, cls, db, 29);
        int kickGrant = GetAbilityBonus(race, cls, db, 30);
        int jumpkickGrant = GetAbilityBonus(race, cls, db, 35);

        // MaxDamageAbility is now the cached ability-4 accumulator populated by ApplyAbility case 4
        // (race/class/items/spells): it is the same value the
        // weapon path adds, so unarmed and weapon paths share it.
        (_punchMin, _punchMax) = ComputeUnarmedBounds(
            (effLevel * punchGrant) / 8 + 2, ((effLevel + 3) * punchGrant) / 4 + 6,
            MaxDamageAbility, GetAbilityBonus(race, cls, db, 92));
        (_kickMin, _kickMax) = ComputeUnarmedBounds(
            (effLevel * kickGrant) / 8 + 2, (effLevel * kickGrant) / 6 + 7,
            MaxDamageAbility, GetAbilityBonus(race, cls, db, 93));
        (_jumpkickMin, _jumpkickMax) = ComputeUnarmedBounds(
            (effLevel * jumpkickGrant) / 8 + 2, (effLevel * jumpkickGrant) / 6 + 8,
            MaxDamageAbility, GetAbilityBonus(race, cls, db, 94));
    }

    private (int Min, int Max) ComputeUnarmedBounds(int baseMin, int baseMax, int maxDmgAbility, int typeDmgAbility)
    {
        // The Strength damage bonus is applied and clamped before the
        // ability layers (max-damage + per-type damage) are added on top.
        var (min, max) = Game.Combat.CombatEngine.ApplyStrengthDamageBonus(baseMin, baseMax, Strength);
        max += maxDmgAbility;           // global Max-Damage ability added to max only
        min += typeDmgAbility;          // per-type damage ability added to both
        max += typeDmgAbility;
        return (min, max);
    }

    /// <summary>
    /// Base accuracy from level, class combat proficiency, and agility.
    /// This is the stat-derived accuracy that gets added to weapon Accy.
    /// </summary>
    public int GetBaseAccuracy(int combatLevel)
    {
        // Accuracy formula:
        //   AV = (Strength-50)/3
        //      + ((combatLvl-1)*floor(sqrt(Level)) + combatLvl*2 + Level/2 - 2) * 2
        //      + (Agility-50)/6
        // The weapon-skill term (halved, then doubled) is approximated by the caller's
        // `+ weapon.Accy`. The flat AV bonus accumulator (abilities 22/105/106) IS folded
        // in here via AvAbility (it is added to AV for all attack types).
        int levelSqrt = 0;
        while ((levelSqrt + 1) * (levelSqrt + 1) <= Level)
            levelSqrt++;

        return ((Strength - 50) / 3)
            + ((((combatLevel - 1) * levelSqrt) + (combatLevel * 2) + (Level / 2) - 2) * 2)
            + ((Agility - 50) / 6)
            + AvAbility
            + CalculateLightEncumbranceAccuracyBonus()
            + CalculateBlindAccuracyPenalty();
    }

    // The fighter's AV gets −10 when EITHER the blind status
    // flag (magical blindness — ability 107 / spell 77) is set OR the player is in a
    // blinding-bright room (light level > 900). Both feed the same penalty. Returns 0 outside the
    // penalty window so it's safe to add unconditionally.
    private int CalculateBlindAccuracyPenalty()
    {
        return IsBlinded || IsBlindedByBrightLight ? -10 : 0;
    }

    // The dodge-skill (to-hit denominator) base
    // value = the ability 24/25 accumulator + 10 when ability 9 (ShadowStealth) is present. Mirrors
    // MonsterInstance.EffectiveDodgeSkill at the player layer so PvP and monster-vs-player paths
    // can feed the SAME calc.
    public int EffectiveDodgeSkill => DodgeSkillAbility + (HasShadowStealth ? 10 : 0);

    // The dodge-skill gets an additional −10 when the player
    // is blinded — magical blindness (ability 107) OR a blinding-bright room
    // (light level > 900, see IsBlindedByBrightLight). Use this from CombatEngine when passing
    // defenderDodgeSkill: for player defenders.
    public int GetCombatDodgeSkill()
    {
        return EffectiveDodgeSkill + (IsBlinded || IsBlindedByBrightLight ? -10 : 0);
    }

    /// <summary>
    /// In the non-backstab
    /// branch, when the player is carrying less than 33% of their max encumbrance AND has positive
    /// HP, the AV gets an `(15 - encum%/10)` bonus from the unburdened weapon-skill term (that term
    /// is incremented by this amount before the AV formula consumes it).
    /// Backstab uses GetBackstabAccuracy which does not call into GetBaseAccuracy, matching the stock
    /// gate. Returns 0 outside the bonus window, so it's safe to add unconditionally.
    /// </summary>
    private int CalculateLightEncumbranceAccuracyBonus()
    {
        if (CurrentHP <= 0 || MaxEncumbrance <= 0)
            return 0;

        int encumbrancePercent = Encumbrance * 100 / MaxEncumbrance;
        if (encumbrancePercent >= 33)
            return 0;

        return 15 - (encumbrancePercent / 10);
    }

    public int GetBackstabAccuracy()
    {
        int tempAcc = (Stealth + Agility) / 2;
        if (HasRaceStealth)
            tempAcc += HasClassStealth ? 5 : -15;

        // Backstab AV: a SECOND Agility/2 term is added on
        // top of the (Stealth+Agility)/2 base. This was
        // missing, slashing backstab AV by Agility/2 (~45 for a 90-agility character) and crushing the
        // hit chance into the clamp floor — the cause of the ~4% backstab rate vs tough monsters.
        tempAcc += Agility / 2;

        tempAcc += BSAccuracy;
        tempAcc += AvAbility;   // the AV bonus is added to backstab AV too
        tempAcc += CalculateBlindAccuracyPenalty(); // the blind penalty applies to all AV reads
        if (IsWeaponHeavy)
            tempAcc -= 10;
        return tempAcc;
    }

    public int GetCrits()
    {
        return Math.Max(1, CalculateBaseCritChance(Level, Intellect, Agility, Charm) + CriticalHitBonus);
    }

    public int GetDodge()
    {
        if (CurrentHP <= 0)
            return -1;

        // In the non-backstab
        // branch, the FighterDodge gets `+ (10 - encum%/10)` when the player carries
        // less than 33% encumbrance and has positive HP. Adding it here applies the bonus to PvP
        // defender-dodge reads as well, matching the in-fighter struct semantics.
        if (MaxEncumbrance <= 0)
            return Dodge;

        int encumbrancePercent = Encumbrance * 100 / MaxEncumbrance;
        if (encumbrancePercent >= 33)
            return Dodge;

        return Dodge + 10 - (encumbrancePercent / 10);
    }

    /// <summary>
    /// Total cumulative exp needed to reach <paramref name="level"/>. Faithful port of the stock
    /// exp curve: seed = factor*10 + 1000 (factor =
    /// race+class exp tables, the percentage modifiers), then multiply by the per-level ratio
    /// table once per level. Stock stores exp two-part in base-1e9; this reproduces its integer
    /// truncation exactly. (The old `baseExp·Σi²` quadratic curve was wrong.)
    /// </summary>
    public static long GetTotalExpForLevel(int level, int raceExpTable, int classExpTable)
    {
        if (level <= 1) return 0;
        var (high, low) = NewCalcExpNeeded(level - 1, raceExpTable + classExpTable);
        return (long)high * 1_000_000_000L + low;
    }

    // Per-level ratio (num/den) table, indices 0..25; index i is applied on the i-th
    // multiply. Index 0 = 1/1 (no-op for the first step). Levels past the table use the stock
    // tail constants: 1.15 (<54), 1.09 (<57), 1.08 (>=57).
    private static readonly (uint Num, uint Den)[] ExpRatioTable =
    {
        (1, 1), (40, 20), (44, 24), (44, 24), (48, 28), (48, 28), (52, 32), (52, 32),
        (56, 36), (56, 36), (60, 40), (60, 40), (65, 45), (65, 45), (70, 50), (70, 50),
        (75, 55), (50, 40), (50, 40), (50, 40), (50, 40), (50, 40), (50, 40), (50, 40),
        (50, 40), (23, 20),
    };

    /// <summary>
    /// Port of the exp-needed calculation — returns the two-part (high, low) total where
    /// value = high*1e9 + low. Replicates the stock 32-bit low-part arithmetic and its overflow
    /// workaround. The high-part step uses the community-patched semantics (64-bit intermediate):
    /// stock computed high*num*1e6 in 32 bits, which wraps once the total passes ~42e9 —
    /// the "exp chart cliff" where per-level needed exp collapses from billions to millions
    /// (Human Warrior: level 101). The patched thunk keeps the full product and carries via the
    /// exact identity q = p*1e6 → q/1e9 == p/1000, q%1e9 == (p%1000)*1e6.
    /// </summary>
    private static (uint High, uint Low) NewCalcExpNeeded(int level, int factor)
    {
        const uint Billion = 1_000_000_000u;
        uint low = (uint)((factor * 1000 + 100000) / 100); // seed = factor*10 + 1000
        uint high = 0;
        for (int i = 0; i < level; i++)
        {
            uint num, den;
            if (i < ExpRatioTable.Length) { num = ExpRatioTable[i].Num; den = ExpRatioTable[i].Den; }
            else if (i < 54) { num = 115; den = 100; }
            else if (i < 57) { num = 109; den = 100; }
            else { num = 108; den = 100; }

            // Low part: floor(low*num/den), reproducing the stock 32-bit overflow handling.
            uint prod = unchecked(low * num);
            uint lowResult;
            if (low <= prod && low == prod / num)
            {
                lowResult = prod / den;
            }
            else
            {
                int shift = 0;
                uint l = low;
                uint p = prod;
                while (!(l <= p && l == p / num))
                {
                    shift++;
                    l /= 100;
                    p = unchecked(l * num);
                }
                uint v = shift <= 1 ? p / den : unchecked(l * num * 100) / den;
                for (int s = shift; s > 0; s--) v = unchecked(v * 100);
                lowResult = v;
            }

            // High part: high*num*1e6 carried into billions, full 64-bit product (patched form).
            ulong hiProd = (ulong)high * num;
            high += (uint)(hiProd / 1000);
            uint e = (uint)(hiProd % 1000) * 1_000_000u; // always < 1e9
            e = unchecked(e + lowResult);
            while (e >= Billion) { e -= Billion; high++; }
            low = e;
        }
        return (high, low);
    }

    /// <summary>
    /// Shared stealth success % for hide and sneak — in the stock
    /// exact order: start at Stealth; encumbrance% bands subtract 5 (&gt;33%) or 10 (&gt;=67%);
    /// ×2/3 when <paramref name="recentlySpotted"/> (the transient spotted flag); clamp to
    /// 100; -1 per other player in the room and -1 per monster present; clamp [0, cap] (cap=95 for
    /// hide/sneak). Perfect stealth (ability 186) bypasses this entirely, handled at the call site.
    /// </summary>
    public static int CalculateStealthChance(int stealth, int encumbrancePercent,
        int otherPlayersInRoom, int monstersInRoom, bool recentlySpotted, int visibilityCap = 95)
    {
        int s = stealth;
        if (encumbrancePercent >= 67) s -= 10;
        else if (encumbrancePercent > 33) s -= 5;
        if (recentlySpotted) s = s * 2 / 3;
        if (s > 100) s = 100;
        s -= Math.Max(0, otherPlayersInRoom);
        s -= Math.Max(0, monstersInRoom);
        if (s > visibilityCap) s = visibilityCap;
        if (s < 0) s = 0;
        return s;
    }

    public long GetExpForNextLevel(int raceExpTable, int classExpTable)
    {
        return GetTotalExpForLevel(Level + 1, raceExpTable, classExpTable);
    }

    public string GetTitle(CharacterClass cls)
    {
        var rawTitle = cls.GetRawTitleForLevel(Level);
        if (!string.IsNullOrWhiteSpace(rawTitle))
            return ResolveGenderedTitle(rawTitle);

        if (Level is > 1 and < 5 &&
            !string.IsNullOrWhiteSpace(cls.Name) &&
            !cls.Name.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return $"{cls.Name} Novice";
        }

        int band = GetTitleBand(Level);

        if (ClassTitleLadders.TryGetValue(cls.Name, out var classLadder) && band < classLadder.Length)
            return ResolveGenderedTitle(classLadder[band]);

        return ResolveGenderedTitle(GenericTitleLadder[Math.Min(band, GenericTitleLadder.Length - 1)]);
    }

    private static int GetTitleBand(int level)
    {
        if (level < 5) return 0;
        if (level >= 70) return 14;
        return ((level - 5) / 5) + 1;
    }

    private string ResolveGenderedTitle(string value)
    {
        int sep = value.IndexOf('|');
        if (sep < 0) return value;

        string left = value.Substring(0, sep);
        string right = value.Substring(sep + 1);
        return Gender == 1 ? right : left;
    }

    public void RecalculateStats(Race race, CharacterClass cls, Data.IGameDatabase db)
    {
        SyncAutomaticKaiPowers(cls, db);

        // Cache the next-level exp threshold for the %X custom-statline variable (DB-free at prompt time).
        ExpForNextLevelCached = GetExpForNextLevel(race.ExpTable, cls.ExpTable);

        // Backward-compat sync, mirrors PlayerRepository.LoadPlayer: an in-memory Player built
        // directly (tests, fresh CharacterCreation, sysop tools) may have Strength set without
        // BaseStrength. Treat the current value as the base so the Strength = BaseStrength +
        // bonus refold below preserves the input.
        if (BaseStrength == 0 && Strength > 0) BaseStrength = Strength;
        if (BaseAgility == 0 && Agility > 0) BaseAgility = Agility;
        if (BaseIntellect == 0 && Intellect > 0) BaseIntellect = Intellect;
        if (BaseWillpower == 0 && Willpower > 0) BaseWillpower = Willpower;
        if (BaseHealth == 0 && Health > 0) BaseHealth = Health;
        if (BaseCharm == 0 && Charm > 0) BaseCharm = Charm;

        // PHASE 1 — Reset every accumulator BEFORE walking ability sources. Includes the new
        // primary-stat-bonus pool (ability 44–49 buffs) so derived stats further down read the
        // boosted Strength/Intellect/Willpower/Agility/Health/Charm. Order is critical: derived
        // stats USED to be computed up here with raw base primaries, which made stat-buff spells
        // silently no-op — they refresh active buffs but never affect Perception/MagicResist/etc.
        Illumination = 0;
        RoomIllumination = 0;
        ACAbility = 0;
        ACBlur = 0;                       // ability 10 blur — re-accumulated each recalc
        DRAbility = 0;
        DRPercent = 0;                    // ability 99 frail — re-accumulated each recalc
        MaxDamageAbility = 0;             // ability 4 — additive across race/class/items/spells
        AvAbility = AbilityMaxSentinel;   // "=max" init; negative-AV buffs (Curse) must win over 0
        HpRegenBonus = 0;
        ManaRegenBonus = 0;
        ShadowformTextblock = 0;          // ability 178 — textblock id, re-derived each recalc
        BSAccuracy = 0;
        BSMinDamage = 0;
        BSMaxDamage = 0;
        StrengthBonus = 0;
        AgilityBonus = 0;
        IntellectBonus = 0;
        WillpowerBonus = 0;
        HealthBonus = 0;
        CharmBonus = 0;
        // Derived-stat bonus pools (cases 36/69/70/77 write here; phase 4 adds them on top of
        // the formula). Keeping these separate from the derived stat itself lets stat-buff
        // abilities affect the formula side and ability bonuses stack additively on top.
        MaxManaBonus = 0;
        SpellCastingBonus = 0;
        PerceptionBonus = 0;
        MagicResistBonus = 0;
        StealthBonus = 0;
        // Reset boolean abilities before re-applying from race/class
        HasSeeHidden = false;
        HasPunch = false;
        HasKick = false;
        HasBash = false;
        HasSmash = false;
        HasJumpkick = false;
        HasPerfectStealth = false;
        IsAfraid = false;
        IsRooted = false;
        IsBlinded = false;
        IsMuted = false;
        DodgeSkillAbility = 0;
        HasShadowStealth = false;

        // PHASE 2 — Walk every ability source. ApplyAbility writes into the accumulators above
        // (including the primary-stat bonus pool).
        foreach (var (abil, val) in race.Abilities)
            ApplyAbility(abil, val);

        foreach (var (abil, val) in cls.Abilities)
            ApplyAbility(abil, val);

        foreach (var (abil, val) in QuestAbilities)
            ApplyAbility(abil, val);

        foreach (var (_, itemId) in Equipment)
        {
            if (!db.Items.TryGetValue(itemId, out var item))
                continue;

            foreach (var (abil, val) in item.Abilities)
            {
                if (abil == 13)
                    continue;

                ApplyAbility(abil, val);
            }
        }

        // Carried inventory (last loop): items in the 100 inventory
        // slots that are NOT weapons (ItemType != 1) AND NOT wearable (Worn == 0) contribute
        // their abilities passively. In stock V1.11p exactly one item triggers this — the "tissue"
        // (#1677) which carries BSAccuracy −10 — but mods rely on the same channel.
        foreach (int itemId in Inventory)
        {
            if (!db.Items.TryGetValue(itemId, out var item))
                continue;
            if (item.ItemType == 1 || item.Worn != 0)
                continue;

            foreach (var (abil, val) in item.Abilities)
            {
                if (abil == 13)
                    continue;

                ApplyAbility(abil, val);
            }
        }

        // Active spells/buffs: each spell's abilities apply at the
        // spell's own value, or the rolled magnitude when that value is 0. Ability 124 is a
        // query-time suppressor (handled by GetActiveAbilityValue), not a stat modifier.
        foreach (var active in ActiveSpells)
        {
            if (active.SpellId <= 0 || !db.Spells.TryGetValue(active.SpellId, out var spell))
                continue;

            foreach (var (abil, val) in spell.Abilities)
            {
                if (abil == 124)   // query-time suppressor, not a stat modifier
                    continue;

                ApplyAbility(abil, val != 0 ? val : active.CastLevel);
            }
        }

        // The AV-bonus accumulator ("=max") normalizes its sentinel to 0 when no source set it.
        if (AvAbility == AbilityMaxSentinel)
            AvAbility = 0;

        // PHASE 3 — Fold the primary-stat bonus pool into the effective Strength/Intellect/etc.
        // Mirrors the stat buffs being written directly before
        // secondary stats are recomputed. Derived stats below now read the post-buff values.
        Strength = BaseStrength + StrengthBonus;
        Agility = BaseAgility + AgilityBonus;
        Intellect = BaseIntellect + IntellectBonus;
        Willpower = BaseWillpower + WillpowerBonus;
        Health = BaseHealth + HealthBonus;
        Charm = BaseCharm + CharmBonus;

        // PHASE 4 — Derived stats. SpellCasting/Perception/MagicResist/MaxEncumbrance/Stealth/
        // Thievery/Traps/DisarmTraps/Picklocks/Tracking/Dodge/MartialArts/Crit now all see the
        // buff-boosted primaries. Derived stats with their own ability bonus pool (cases
        // 36/69/70/77) add it here so a stat-buff and a flat-ability-bonus stack additively.
        MaxMana = CalculateMaxMana(cls, Level) + MaxManaBonus;
        // The prompt's second resource is labelled "Kai" for kai classes
        // (Magery type 5) and "MA" for mana casters; pure-martial classes carry no label and
        // never show the resource. Cached here from the CURRENT class so the prompt is class-correct
        // without a per-render DB lookup.
        MagicResourceLabel = UsesKai(cls) ? "KAI" : UsesSpellcasting(cls) ? "MA" : null;
        // A pure-martial class (Warrior/Thief/Ninja/Witchunter) has no mana/kai pool. Discard any
        // +mana item/buff bonus so it carries no phantom resource — a non-caster can't spend it and
        // must never show an "MA" tag. (Guards against e.g. a +10-mana item equipped by a thief.)
        if (MagicResourceLabel is null)
        {
            MaxMana = 0;
            CurrentMana = 0;
        }
        SpellCasting = CalculateSpellCasting(cls, Level, Intellect, Willpower, Charm) + SpellCastingBonus;
        // Perception =
        // (5·Int+2·Will+Chr)/8 + the ability-77 value — i.e. ability
        // 77 only. The same call also fills an out-param with the ability-27 (Stealth) sum, but that
        // out-param is consumed later by Stealth, NOT by Perception.
        // Some readings render the discarded return as a stale alias of the out-param
        // pointer, which is what made the 2026-06-23 audit (M2) fold ability 27 in here by mistake:
        // that made a plate-armoured character's Perception go deeply NEGATIVE (worn plate carries
        // ability 27 down to -40, and carried boats/rafts as low as -200). The same artifact
        // appears elsewhere, where the return is likewise rendered as a stale local.
        // No clamp: stock floors Stealth/Thievery/Traps/Picklocks/Tracking at 0 but deliberately
        // leaves Perception unclamped.
        Perception = CalculatePerception(Intellect, Willpower, Charm) + PerceptionBonus;
        MagicResist = CalculateMagicResist(Intellect, Willpower) + MagicResistBonus;
        MaxEncumbrance = CalculateMaxEncumbrance(Strength, GetAbilityBonus(race, cls, db, 96));

        HasRaceStealth = HasAbility(race, cls, db, 102);
        HasClassStealth = HasAbility(race, cls, db, 103);
        // The stealth-ability bonus pool (which carries racial
        // penalties as a negative Stealth(27) — e.g. a Half-Ogre's -15) is applied ONLY to characters with
        // race- or class-stealth. A non-stealther is hard-set to 0 and the bonus is NEVER added, so a
        // racial penalty can't drag stealth negative. Stealthers floor at 0 (clamped post-bonus >=0).
        Stealth = (HasRaceStealth || HasClassStealth)
            ? Math.Max(0, CalculateStealth(Level, Agility, Intellect, Charm, HasRaceStealth, HasClassStealth) + StealthBonus)
            : 0;

        Thievery = HasAbility(race, cls, db, 39)
            ? Math.Max(0, CalculateThievery(Level, Agility, Intellect, Charm) + GetAbilityBonus(race, cls, db, 39))
            : 0;
        Traps = HasAbility(race, cls, db, 40)
            ? Math.Max(0, CalculateFindTraps(Level, Agility, Intellect, Charm) + GetAbilityBonus(race, cls, db, 40) + GetAbilityBonus(race, cls, db, 179))
            : 0;
        // Disarm Traps: same base formula as Find, gated by ability40, + ability41.
        DisarmTraps = HasAbility(race, cls, db, 40)
            ? Math.Max(0, CalculateFindTraps(Level, Agility, Intellect, Charm) + GetAbilityBonus(race, cls, db, 41))
            : 0;
        Picklocks = HasAbility(race, cls, db, 37)
            ? Math.Max(0, CalculatePicklocks(Level, Agility, Intellect) + GetAbilityBonus(race, cls, db, 37) + GetAbilityBonus(race, cls, db, 180))
            : 0;
        Tracking = HasAbility(race, cls, db, 38)
            ? Math.Max(0, CalculateTracking(Level, Intellect, Willpower, Charm) + GetAbilityBonus(race, cls, db, 38))
            : 0;

        int baseCritChance = CalculateBaseCritChance(Level, Intellect, Agility, Charm);
        CriticalHitBonus = GetAbilityBonus(race, cls, db, 58);
        int dodgeBonus = GetAbilityBonus(race, cls, db, 34);
        Dodge = CalculateDodge(Level, Agility, Charm, dodgeBonus);
        MartialArts = CalculateMartialArts(Level, Agility, Charm, baseCritChance, CriticalHitBonus, dodgeBonus, HasAbility(race, cls, db, 35));

        // RecalculateUnarmedDamage reads MaxDamageAbility (ability 4) and the now-effective
        // Strength via the unarmed-damage path. Stock builds the fighter min/max from the
        // same ability-4 accumulator.
        RecalculateUnarmedDamage(race, cls, db);

        CurrentMana = Math.Min(CurrentMana, MaxMana);
    }

    private void SyncAutomaticKaiPowers(CharacterClass cls, Data.IGameDatabase db)
    {
        if (!UsesKai(cls))
            return;

        foreach (var spell in db.Spells.Values)
        {
            if (!spell.Learnable || spell.Magery != KaiMageryType)
                continue;

            if (Level < spell.ReqLevel || cls.MageryLvl < spell.MageryLvl)
                continue;

            SetQuestAbilityValue(GetLearnedSpellbookAbilityId(spell.Number), 1);
        }
    }

    public int GetQuestAbilityValue(int abilityId)
    {
        return QuestAbilities.GetValueOrDefault(abilityId);
    }

    public void SetQuestAbilityValue(int abilityId, int value)
    {
        if (value <= 0)
        {
            QuestAbilities.Remove(abilityId);
            return;
        }

        QuestAbilities[abilityId] = value;
    }

    /// <summary>
    /// Grant a quest ability by PRESENCE, storing it even at value 0. Boolean abilities like Perfect
    /// Stealth (#186, the "supernatural stealth" assassin-quest reward) are granted as
    /// <c>giveability 186 0</c>; the entry is stored regardless of value, and
    /// presence is what gets keyed off. This is distinct from <see cref="SetQuestAbilityValue"/>,
    /// whose value&lt;=0 path REMOVES the ability (that path is used to unlearn a scroll spell on
    /// unequip) — routing a value-0 grant through it silently dropped the ability. Removal has its own
    /// command (<see cref="RemoveQuestAbility"/> / quest <c>removeability</c>).
    /// </summary>
    public void GrantQuestAbility(int abilityId, int value)
    {
        QuestAbilities[abilityId] = value < 0 ? 0 : value;
    }

    public void AddQuestAbilityValue(int abilityId, int value)
    {
        if (value == 0)
            return;

        SetQuestAbilityValue(abilityId, GetQuestAbilityValue(abilityId) + value);
    }

    public void RemoveQuestAbility(int abilityId)
    {
        QuestAbilities.Remove(abilityId);
    }

    // Refresh an already-active spell (no stacking) or fill an empty slot
    // (max 10). Returns true if the buff is now active; caller recomputes stats afterward.
    public bool AddOrRefreshActiveSpell(int spellId, int castLevel, int duration)
    {
        if (spellId <= 0 || duration <= 0)
            return false;

        foreach (var active in ActiveSpells)
        {
            if (active.SpellId == spellId)
            {
                // A refreshed slot's magnitude is only ever RAISED
                // magnitude (`if existing < new`), never lowers it — so the slot tracks the strongest
                // application. This is load-bearing for poison: the poison accumulator is also a
                // running max, and ReverseTerminatedPoison subtracts THIS slot's magnitude on expiry.
                // Overwriting with a later weaker roll left the slot below the accumulator, so expiry
                // under-subtracted and a residual poison level lingered forever ("poison never wears off").
                if (castLevel > active.CastLevel)
                    active.CastLevel = castLevel;
                active.RemainingDuration = duration;
                return true;
            }
        }

        if (ActiveSpells.Count >= MaxActiveSpells)
            return false;

        ActiveSpells.Add(new ActiveSpell { SpellId = spellId, CastLevel = castLevel, RemainingDuration = duration });
        return true;
    }

    // The ROOM-spell path, with the refresh flag SET: a slot
    // already holding this spell is refreshed IN PLACE, magnitude overwritten with this cast's roll and
    // duration reset; never a second slot. This differs from the keep-strongest overloads above (which
    // model the flag-clear path): a room spell re-rolls magnitude every
    // 6s pulse — e.g. #412 mana drain rolls -100..-25 fresh each pulse and the NEW roll is taken.
    public bool AddOrRefreshRoomSpell(int spellId, int magnitude, int duration)
    {
        if (spellId <= 0 || duration <= 0)
            return false;

        foreach (var active in ActiveSpells)
        {
            if (active.SpellId == spellId)
            {
                active.CastLevel = magnitude;
                active.RemainingDuration = duration;
                return true;
            }
        }

        if (ActiveSpells.Count >= MaxActiveSpells)
            return false;

        ActiveSpells.Add(new ActiveSpell { SpellId = spellId, CastLevel = magnitude, RemainingDuration = duration });
        return true;
    }

    public bool HasActiveSpell(int spellId)
    {
        foreach (var active in ActiveSpells)
        {
            if (active.SpellId == spellId)
                return true;
        }

        return false;
    }

    public bool RemoveActiveSpell(int spellId) => ActiveSpells.RemoveAll(s => s.SpellId == spellId) > 0;

    private void ApplyAbility(int abilId, int value)
    {
        // Ability IDs from the GreaterMUD mapping
        switch (abilId)
        {
            case 2: ACAbility += value; break; // Natural armour / AC (summed raw)
            case 4: MaxDamageAbility += value; break; // max-damage bonus added to MaxDmg only (any equipped item / race / class / active spell)
            case 7: DRAbility += value; break; // Toughness / damage resist, stored in tenths
            case 10: ACBlur += value; break; // Protective Shield / blur; scaled by free-encumbrance in GetBlurAC()
            case 99: DRPercent += value; break; // Alter DR by percent / frail; applied multiplicatively in GetTotalDR()
            // ShadowStealth(9): adds to Stealth AND flips the presence flag used to
            // grant +10 to the dodge-skill column. Only ability-9 presence is checked
            // (presence), so a single source with non-zero magnitude is enough.
            case 9: StealthBonus += value; HasShadowStealth = true; break;   // added to Stealth in phase 4
            // Dodge-skill column accumulator. 24 and 25 are adjacent slot IDs;
            // one is picked based on attack context. We sum (faithful upper bound in practice).
            case 24: case 25: DodgeSkillAbility += value; break;
            // Two distinct light channels are kept: ability 13 ("Alter User
            // Light") adds to the VIEWER's own light only, while ability 14 ("Alter Room Light")
            // is summed across every player in the room — so ability 14 lights the room for everyone
            // present (Sunsword, starlight #26 / cross of vengeance #448 / angelic halo #1098 / balanced
            // sight #1099). Keep them separate so GetEffectiveRoomLight can aggregate 0xe per-occupant.
            case 13: Illumination += value; break;     // 0xd Illu — personal
            case 14: RoomIllumination += value; break; // Alter Room Light — room-wide
            // Accuracy bonus ("=max" across sources): abilities 22/105/106.
            case 22: case 105: case 106: AvAbility = Math.Max(AvAbility, value); break;
            case 27: StealthBonus += value; break; // Stealth(27) — Stealth only; Perception takes ability 77, never 27
            case 29: HasPunch = true; break; // Punch
            case 30: HasKick = true; break; // Kick
            case 31: HasBash = true; break; // Bash
            case 32: HasSmash = true; break; // Smash (SMASH → attack type 7)
            case 34: break; // Dodge(34) is folded into Dodge and MartialArts during secondary stat recalculation.
            case 35: HasJumpkick = true; break; // Jumpkick
            case 36: MagicResistBonus += value; break; // Magic Resistance — added to the formula in phase 4
            // Abilities 44–49: primary-stat buffs. Add to BaseStrength/etc. in RecalculateStats;
            // every derived stat (Perception, MagicResist, MaxEncumbrance, Stealth, Crit, Dodge,
            // MartialArts) then sees the boosted value. The same stat fields are written,
            // then secondary stats are recomputed
            // — same single-recompute model.
            case 44: StrengthBonus += value; break;      // str enhance / weakness
            case 45: IntellectBonus += value; break;     // int
            case 46: WillpowerBonus += value; break;     // "Wisdom" in stock = our Willpower
            case 47: AgilityBonus += value; break;       // agi
            case 48: HealthBonus += value; break;        // health
            case 49: CharmBonus += value; break;         // chr
            case 57: HasSeeHidden = true; break; // See Hidden
            case 60: IsAfraid = true; break; // Fear: afraid flag — can't cast/attack
            case 74: IsRooted = true; break; // HoldPerson: root flag — blocks MOVEMENT only, not attack/cast
            case 76: IsMuted = true; break; // Mute: blocks speech/tells
            case 107: IsBlinded = true; break; // Blind: sets the blind status → AV/dodge-skill −10
            case 58: break; // Critical hit bonus is folded into Crits and MartialArts during secondary stat recalculation.
            case 69: MaxManaBonus += value; break; // MaxMana(69) — added to the formula in phase 4
            case 70: SpellCastingBonus += value; break; // SpellCasting(70) — added in phase 4
            case 77: PerceptionBonus += value; break; // Perception(77) — added in phase 4
            case 102: break; // RaceStealth(102) is a gate for the stock stealth formula.
            case 103: break; // ClassStealth(103) is a gate for the stock stealth formula.
            case 116: BSAccuracy += value / 2; break; // Backstab accuracy uses half of the ability parameter.
            case 117: BSMinDamage += value; break; // Backstab minimum damage modifier.
            case 118: BSMaxDamage += value; break; // Backstab maximum damage modifier.
            case 123: HpRegenBonus += value; break; // HP-regen % bonus
            case 145: ManaRegenBonus += value; break; // Mana-regen % bonus
            // Shadowform (178): value is the look-description textblock id, NOT a magnitude. Ability
            // lookups sum sources, but summing two textblock ids yields garbage — so
            // ASSIGN (last-wins). Item slots apply before active spells, so the shadowform SPELL (#130,
            // 4157) wins over the death-shroud ITEM (#1579, 9652) when both are present; either hides gear.
            case 178: ShadowformTextblock = value; break;
            case 186: HasPerfectStealth = true; break; // PerfectStealth(186) — quest-granted, auto-succeed sneak
        }
    }

    // ── Stat Descriptors ──────────────────────

    private static readonly string[] StrengthDescriptors =
    {
        "puny", "weak", "slightly built", "moderately built", "well built",
        "muscular", "powerfully built", "heroically proportioned", "Herculean",
        "physically Godlike"
    };

    private static readonly string[] AgilityDescriptors =
    {
        "slowly", "clumsily", "slugishly", "cautiously", "gracefully",
        "very swiftly", "with uncanny speed", "with catlike agility",
        "blindingly fast"
    };

    private static readonly string[] HealthDescriptors =
    {
        "sickly", "frail", "thin", "healthy", "stout",
        "solid", "massive", "gigantic", "colossal"
    };

    private static readonly string[] CharmDescriptors =
    {
        "openly hostile and quite revolting",
        "hostile and rather unappealing",
        "quite unfriendly and aloof",
        "likable in an unassuming sort of way",
        "quite attractive and pleasant to be around",
        "charismatic and outgoing.  You can't help but like {0}",
        "extremely likeable, and fairly radiates charisma",
        "incredibly charismatic.  You are almost overpowered by {0} strong personality",
        "overwhelmingly charismatic.  You almost drop to your knees in wonder at the sight of {0}!"
    };

    private static readonly string[] IntellectDescriptors =
    {
        "utterly moronic", "quite stupid", "slightly dull", "intelligent",
        "bright", "extremely clever", "brilliant", "a genius", "all-knowing"
    };

    private static readonly string[] WillpowerDescriptors =
    {
        "seems selfish and hot-tempered",
        "seems sullen and impulsive",
        "seems a little naive",
        "looks fairly knowledgeable",
        "looks quite experienced and wise",
        "has a worldly air about {0}",
        "seems to possess a wisdom beyond {0} years",
        "seems to be in an enlightened state of mind",
        "looks like {0} is one with the Gods"
    };

    // Thresholds: <30, <40, <50, <60, <70, <80, <90, <100, >=100
    private static int GetStatTier9(int stat)
    {
        if (stat < 30) return 0;
        if (stat < 40) return 1;
        if (stat < 50) return 2;
        if (stat < 60) return 3;
        if (stat < 70) return 4;
        if (stat < 80) return 5;
        if (stat < 90) return 6;
        if (stat < 100) return 7;
        return 8;
    }

    // Strength has 10 tiers
    private static int GetStatTier10(int stat)
    {
        if (stat < 25) return 0;
        if (stat < 35) return 1;
        if (stat < 45) return 2;
        if (stat < 55) return 3;
        if (stat < 65) return 4;
        if (stat < 75) return 5;
        if (stat < 85) return 6;
        if (stat < 95) return 7;
        if (stat < 105) return 8;
        return 9;
    }

    public string GetStrengthDesc() => StrengthDescriptors[GetStatTier10(Strength)];
    public string GetAgilityDesc() => AgilityDescriptors[GetStatTier9(Agility)];
    public string GetHealthDesc() => HealthDescriptors[GetStatTier9(Health)];
    public string GetIntellectDesc() => IntellectDescriptors[GetStatTier9(Intellect)];

    public string GetCharmDesc()
    {
        string desc = CharmDescriptors[GetStatTier9(Charm)];
        return desc.Contains("{0}") ? string.Format(desc, HimHer) : desc;
    }

    public string GetWillpowerDesc()
    {
        string desc = WillpowerDescriptors[GetStatTier9(Willpower)];
        return desc.Contains("{0}") ? string.Format(desc, HimHer_Lower) : desc;
    }

    // Wound status based on HP percentage
    public string GetWoundDesc()
    {
        if (MaxHP <= 0) return "dead";
        if (CurrentHP <= 0) return "mortally wounded";
        int pct = CurrentHP * 100 / MaxHP;
        if (pct < 20) return "critically wounded";
        if (pct < 40) return "severely wounded";
        if (pct < 60) return "moderately wounded";
        if (pct < 80) return "slightly wounded";
        return "unwounded";
    }
}

public class MonsterInstance
{
    private const int DefaultEnergyCap = 1000;
    private static readonly Random _rng = new();
    private readonly ConcurrentDictionary<string, byte> _engagedPlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private int _deathProcessingStarted;
    public Monster Template { get; set; } = null!;
    public int CurrentHP { get; set; }
    public int MaxHP { get; set; }
    public int CurrentEnergy { get; set; }
    public int MaxEnergy { get; set; }
    // Active spell/buff slots (×5): self-cast haste, player debuffs (slow),
    // or any other ability a duration spell confers.
    public List<ActiveSpell> ActiveSpells { get; } = [];
    // Cached effective ability values: template intrinsics merged with active-spell buffs per the
    // monster ability rules. Rebuilt whenever buffs change; read by combat so ANY buffed
    // ability (AV / DR / damage / haste-slow / elemental resist / ...) takes effect, not just haste/slow.
    private readonly Dictionary<int, int> _effectiveAbilities = [];
    // Haste/slow EU scaler (100 = normal; <100 faster, >100 slower), derived from buffs.
    public int HasteSlowPercent
    {
        get
        {
            int value = GetEffectiveAbility(HasteSlowAbilityId);
            return value != 0 ? value : 100;
        }
    }
    /// <summary>Display name with flavor prefix (e.g., "nasty Rat"). Used for all display/combat text.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Base template name for targeting/matching (e.g., "Rat").</summary>
    public string Name => Template.Name;
    public bool IsDead => CurrentHP <= 0;
    public int RespawnTimer { get; set; }
    public DateTime? RespawnAtUtc { get; set; }
    public int MapNumber { get; set; }
    public int RoomNumber { get; set; }
    public int HomeMapNumber { get; set; }
    public int HomeRoomNumber { get; set; }

    // Monster movement trail (room ring, ×10) read by TRACK. Lazily allocated on the
    // first wander/pursuit move — room-bound monsters and NPCs that never relocate allocate nothing.
    private MovementTrail? _movementTrail;
    public MovementTrail? MovementTrail => _movementTrail;
    public void RecordMovementTrail(int mapNumber, int roomNumber)
    {
        _movementTrail ??= new MovementTrail(MovementTrail.MonsterCapacity);
        _movementTrail.Record(mapNumber, roomNumber);
    }
    public bool IsBackgroundSpawn { get; set; }
    public bool IsPermanentNPC { get; set; } // Room.NPC - always present, respawns in place

    // Monster last-move direction: ambient wander never immediately reverses its previous
    // step. Empty until the monster's first wander/pursuit move.
    public string LastMoveDirection { get; set; } = "";
    // Deliberately placed into a room that its Group/MonsterType would normally reject — death-spell
    // summons (e.g. Champion of Blood → Greater Hellion), reinforcement summons, quest adds and pets all
    // spawn via TrySpawnMonsterInRoom(ignoreRoomRestrictions: true). Such a monster must NOT be swept by
    // CleanupMonstersInInvalidRooms, which would otherwise yank it from the room mid-fight (the combat
    // engine keeps its own reference, so the kill still resolves but the monster vanishes from `look`).
    public bool WasForcePlaced { get; set; }
    // A monster's CreateSpell fires once per spawn. For Room.NPC
    // primaries this is set the first time a player is present after the primary (re)spawns, so an escort
    // summon (e.g. dark-elf queen → weaponmaster) never fires twice while the primary stays alive. Reset
    // on revive so a re-spawned primary re-fires it, matching stock re-running the spawn.
    public bool CreateSpellFired { get; set; }
    // Summon owner link (summoned child → its summoner; the summoner tracks its children).
    // Set when a monster casts a summon spell (ability 12); the summoned reinforcement is owned by the caster.
    public MonsterInstance? Owner { get; set; }

    // --- Player-pet model (owner-name string, charm-fresh byte, abandon counter) ---
    // The owning PLAYER's name. Set by a player-cast summon (ability 12) or charm (6); empty for wild
    // monsters. A pet follows its owner room-to-room and never aggros them. (The owner is
    // resolved from that name each fast tick.)
    public string? PlayerOwnerName { get; set; }
    // True for monsters created by a summon spell ("angels", monster Type 37): these are
    // DISMISSED (removed) when the owner leaves/dies or after the follow-abandon limit. Charmed wild
    // monsters (false) instead revert to ownerless wild monsters and are NOT removed.
    public bool IsSummonedCreature { get; set; }
    // Abandon counter: increments each pet-update pass the pet fails to
    // follow its owner; at > FollowAbandonLimit the bond breaks (summoned removed / charmed reverts).
    // Stock keeps ONE such count per monster, so a wild monster uses it for its hostile lock the same
    // way: a pass where the locked player is out of reach counts, a swing zeroes it, and past the limit
    // the lock drops (GameWorld.ProcessHostileLockMisses).
    public int FollowAbandonTicks { get; set; }
    // Combat-pulse gate for a pet's own swing (mirrors Player.NextMonsterAttackAtUtc): a pet attacks
    // a hostile monster at most once per combat round.
    public DateTime PetNextAttackAtUtc { get; set; }
    // Charm-fresh latch (set by a summon or charm, cleared the
    // next time the monster participates in combat resolution). While set, the freshly summoned/charmed
    // monster does not count as "could attack" and is not picked for a departing free swing — it hasn't
    // oriented yet. We clear it on the next pet-update pass (the monster's next chance to act).
    public bool IsCharmFresh { get; set; }
    public bool HasPlayerOwner => !string.IsNullOrEmpty(PlayerOwnerName);
    public bool IsOwnedBy(string playerName)
        => HasPlayerOwner && string.Equals(PlayerOwnerName, playerName, StringComparison.OrdinalIgnoreCase);
    public void ClearPlayerOwnership()
    {
        PlayerOwnerName = null;
        IsSummonedCreature = false;
        FollowAbandonTicks = 0;
    }
    public List<int> CarriedDropItemIds { get; } = [];
    public int CarriedGold { get; set; }
    public int CarriedSilver { get; set; }
    public int CarriedCopper { get; set; }
    public int CarriedPlatinum { get; set; }
    public int CarriedRunic { get; set; }
    public KnockdownKind KnockdownKind { get; private set; }
    public int KnockdownTicksRemaining { get; private set; }
    public int KnockdownDescriptiveMessageId { get; private set; }
    public bool IsKnockedDown => KnockdownTicksRemaining > 0;
    // Counts down world ticks until this monster's next HP-regen cycle (the slow monster update
    // fires once per global slow pass, not every tick — see GameWorld.MonsterHpRegenIntervalTicks).
    // Defaults to 0, so the monster's first processed tick fires the cycle and then seeds the
    // interval — mirroring a freshly initialized regen marker. Monsters spawn
    // at full HP, so that first fire is a no-op for them; thereafter heals land once per interval.
    public int HpRegenCooldownTicks { get; set; }

    /// <summary>
    /// Once every <paramref name="intervalTicks"/> world ticks,
    /// regenerate <see cref="Monster.HPRegen"/> HP (clamped to <see cref="MaxHP"/>). On all other
    /// ticks just advance the countdown so the cadence stays fixed regardless of when damage lands.
    /// </summary>
    public void TickHpRegen(int intervalTicks)
    {
        if (IsDead || Template.HPRegen <= 0)
            return;

        if (--HpRegenCooldownTicks > 0)
            return;

        HpRegenCooldownTicks = intervalTicks;
        if (CurrentHP < MaxHP)
            CurrentHP = Math.Min(MaxHP, CurrentHP + Template.HPRegen);
    }

    // Monster poison: HP drained per slow pass until the monster dies. Applied by a
    // player poison spell (ability 19, max-merged). Stock has no monster cure path, so it runs to death.
    public int PoisonLevel { get; set; }
    public int PoisonCooldownTicks { get; set; }

    // Once per slow pass (same cadence as HP regen) drain the poison
    // level from the monster's HP. Returns true if this drain just killed the monster.
    public bool TickPoison(int intervalTicks)
    {
        if (IsDead || PoisonLevel <= 0)
            return false;

        if (--PoisonCooldownTicks > 0)
            return false;

        PoisonCooldownTicks = intervalTicks;
        CurrentHP -= PoisonLevel;
        return IsDead;
    }

    public void ClearCarriedTreasure()
    {
        CarriedDropItemIds.Clear();
        CarriedGold = 0;
        CarriedSilver = 0;
        CarriedCopper = 0;
        CarriedPlatinum = 0;
        CarriedRunic = 0;
    }

    public void RollCarriedTreasure(bool guaranteeAllDrops = false)
    {
        ClearCarriedTreasure();

        if (Template == null)
            return;

        foreach (var drop in Template.Drops)
        {
            // A LIMITED monster's (MaxSpawn != 0) FIRST-ever spawn
            // (no recorded last-death stamp) bypasses the per-item drop-% roll and carries every
            // loot item at 100%; once it has died the stamp is set and later spawns roll normally. The
            // guaranteeAllDrops decision is made by GameWorld, which owns the last-death ledger.
            if (guaranteeAllDrops || _rng.Next(100) < drop.Percent)
                CarriedDropItemIds.Add(drop.ItemId);
        }

        CarriedGold = Template.Gold > 0 ? _rng.Next(1, Template.Gold + 1) : 0;
        CarriedSilver = Template.Silver > 0 ? _rng.Next(1, Template.Silver + 1) : 0;
        CarriedCopper = Template.Copper > 0 ? _rng.Next(1, Template.Copper + 1) : 0;
        CarriedPlatinum = Template.Platinum > 0 ? _rng.Next(1, Template.Platinum + 1) : 0;
        CarriedRunic = Template.Runic > 0 ? _rng.Next(1, Template.Runic + 1) : 0;
    }

    public bool TryBeginDeathProcessing()
        => System.Threading.Interlocked.CompareExchange(ref _deathProcessingStarted, 1, 0) == 0;

    public void ResetDeathProcessing()
    {
        System.Threading.Volatile.Write(ref _deathProcessingStarted, 0);
        _engagedPlayerNames.Clear();
        LockedTargetName = null;
        CreateSpellFired = false; // a re-spawned primary re-fires its CreateSpell
    }

    // The monster's SINGLE locked combat target (a player name). The engaged
    // branch attacks ONLY this player each beat (not every player it has aggroed); an attack
    // re-points it to the most-recent attacker on a FollowPercent roll (the "attack last" mechanic).
    // Empty/null = unengaged: the monster picks a target across the room via the terminal-order roll.
    // (In stock the same string slot doubles as a pet's owner name; here PlayerOwnerName carries
    // the pet case, so LockedTargetName is the wild-monster combat target only — kept distinct to avoid
    // conflating the two and is never set on an owned pet.)
    public string? LockedTargetName { get; private set; }

    public bool HasLockedTarget => !string.IsNullOrEmpty(LockedTargetName);

    public bool IsLockedOnTarget(string playerName)
        => HasLockedTarget && string.Equals(LockedTargetName, playerName, StringComparison.OrdinalIgnoreCase);

    public void SetLockedTarget(string playerName)
    {
        if (!string.IsNullOrWhiteSpace(playerName))
            LockedTargetName = playerName;
    }

    public void ClearLockedTarget() => LockedTargetName = null;

    public void MarkPlayerEngaged(string playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return;

        _engagedPlayerNames[playerName] = 0;
    }

    public bool HasEngagedPlayer(string playerName)
    {
        return !string.IsNullOrWhiteSpace(playerName)
            && _engagedPlayerNames.ContainsKey(playerName);
    }

    public int GetCombatEnergyCap()
    {
        if (MaxEnergy > 0)
            return MaxEnergy;

        // A monster template with Energy == 0 has NO energy pool BY DESIGN. The energy update
        // uses the Energy field as the per-round cap: when the cap is 0 the
        // monster's current energy can never rise above 0, and any swing whose
        // energy use exceeds the pool. So an Energy-0 monster never attacks AND never retaliates — even with
        // an attack slot defined. This is the whole class of non-combatant NPCs: barmaids, healers, shop
        // keepers, trainers, the practice dummy, and quest figures like Balthazar (#263, Energy 0, 9999 HP).
        // Returning DefaultEnergyCap for these handed them a full 1000-point pool and made them fight back.
        // Only fall back to the default for a synthetic instance with no template at all (test/utility code),
        // never to paper over a real Energy == 0.
        if (Template != null)
            return Template.Energy > 0 ? Template.Energy : 0;

        return DefaultEnergyCap;
    }

    public void ResetEnergy()
    {
        MaxEnergy = GetCombatEnergyCap();
        CurrentEnergy = 0;
        ActiveSpells.Clear();
        _effectiveAbilities.Clear();
        ClearKnockdown();
    }

    public void PrepareCombatRound()
    {
        int energyCap = GetCombatEnergyCap();
        MaxEnergy = energyCap;
        // Monster energy update, exactly:
        //   if      (current <  cap)  current += cap;   // refill — ACCUMULATES above cap, no upward clamp
        //   else if (current >  cap)  current  = cap;   // clamp DOWN to one cap
        //   else (current == cap)     unchanged
        // The accumulate-above-cap is intentional and load-bearing: a monster ending a round with a
        // remainder (e.g. an Adult Red Dragon left with 400 after one 600-EU dragonfire) starts the next
        // round at remainder+cap (1400) — enough to fire TWO 600-EU casts that round. The clamp-DOWN
        // (previously missing here) bounds the surplus: a monster that under-spends its pool can't grow
        // energy unboundedly past one cap, matching the per-round ceiling.
        if (CurrentEnergy < energyCap)
            CurrentEnergy = Math.Max(0, CurrentEnergy) + energyCap;
        else if (CurrentEnergy > energyCap)
            CurrentEnergy = energyCap;
    }

    /// <summary>
    /// Energy refill used when the monster is idle (no engaged players):
    /// refills toward the cap but never overshoots. Clears any stale carry-over from a prior
    /// fight so the next engagement starts at exactly one cap of stamina, not two.
    /// </summary>
    public void RefillEnergyOutOfCombat()
    {
        int cap = GetCombatEnergyCap();
        MaxEnergy = cap;
        if (CurrentEnergy < cap)
            CurrentEnergy = cap;
        else if (CurrentEnergy > cap)
            CurrentEnergy = cap;
    }

    public void ApplyKnockdown(KnockdownKind kind, int duration, int descriptiveMessageId = 0)
    {
        if (duration <= 0)
        {
            ClearKnockdown();
            return;
        }

        KnockdownKind = kind;
        KnockdownTicksRemaining = duration;
        KnockdownDescriptiveMessageId = descriptiveMessageId;
    }

    public bool TickKnockdown()
    {
        if (!IsKnockedDown)
            return false;

        KnockdownTicksRemaining--;
        if (KnockdownTicksRemaining > 0)
            return false;

        ClearKnockdown();
        return true;
    }

    public void ClearKnockdown()
    {
        KnockdownKind = KnockdownKind.None;
        KnockdownTicksRemaining = 0;
        KnockdownDescriptiveMessageId = 0;
    }

    public int EngagedPlayerCount => _engagedPlayerNames.Count;

    private const int HasteSlowAbilityId = 87;   // haste/slow
    private const int SuppressAbilityId = 124;   // zeroes the ability id it names
    private const int MaxMonsterActiveSpells = 5;   // monster buff slots
    // These ability ids aggregate by MAX across sources; all others sum.
    private static readonly HashSet<int> MonsterMaxAbilities =
        [3, 5, 22, 65, 66, 71, 72, 87, 101, 105, 106];

    /// <summary>
    /// Apply (or refresh) a duration spell/buff on the monster — self-cast haste or a player's slow.
    /// <paramref name="magnitude"/> is the rolled effect magnitude, used when the spell's
    /// own ability value is 0. Recomputes the cached haste/slow scaler.
    /// </summary>
    public bool AddOrRefreshActiveSpell(Data.IGameDatabase db, int spellId, int magnitude, int duration)
    {
        if (spellId <= 0 || duration <= 0)
            return false;

        foreach (var active in ActiveSpells)
        {
            if (active.SpellId == spellId)
            {
                // Keep the strongest magnitude on refresh — a slot is only ever raised,
                // never lowered (see the sibling overload for why this matters to poison wear-off).
                if (magnitude > active.CastLevel)
                    active.CastLevel = magnitude;
                active.RemainingDuration = duration;
                RecomputeBuffStats(db);
                return true;
            }
        }

        if (ActiveSpells.Count >= MaxMonsterActiveSpells)
            return false;

        ActiveSpells.Add(new ActiveSpell { SpellId = spellId, CastLevel = magnitude, RemainingDuration = duration });
        RecomputeBuffStats(db);
        return true;
    }

    /// <summary>
    /// Apply the
    /// over-time effects of this monster's active spell slots once per MEDIUM tick — the monster analogue
    /// of the player ProcessActiveSpellUpkeep. Ability 1 disease / 8 drain drains HP, 18
    /// heals, 11 regenerates energy clamped to MaxEnergy, 20 drains poison.
    /// Magnitude = the slot ability value, falling back to the cast level (mirrors the player path). Poison
    /// (19) is NOT here — it drains on the SLOW pass (TickPoison), matching the cadence split.
    /// Returns true if a drain killed the monster (caller defers RemoveDeadMonster).
    /// </summary>
    public bool TickSpellUpkeepEffects(Data.IGameDatabase db)
    {
        if (ActiveSpells.Count == 0)
            return false;

        foreach (var active in ActiveSpells.ToList())
        {
            if (!db.Spells.TryGetValue(active.SpellId, out var upkeepSpell))
                continue;

            foreach (var (abil, abilVal) in upkeepSpell.Abilities)
            {
                int value = abilVal != 0 ? abilVal : active.CastLevel;
                switch (abil)
                {
                    case 1:   // disease/plague
                    case 8:   // drain
                        if (value > 0)
                        {
                            CurrentHP -= value;
                            if (IsDead)
                                return true;
                        }
                        break;
                    case 18:  // heal-over-time
                        if (value > 0)
                            CurrentHP = System.Math.Min(MaxHP, CurrentHP + value);
                        break;
                    case 11:  // energy regen
                        if (value > 0 && MaxEnergy > 0)
                            CurrentEnergy = System.Math.Min(MaxEnergy, CurrentEnergy + value);
                        break;
                    case 20:  // anti-poison drain
                        if (value > 0)
                            PoisonLevel = System.Math.Max(0, PoisonLevel - value);
                        break;
                }
            }
        }

        return false;
    }

    /// <summary>Medium tick: decrement each active spell's duration, drop expired, recompute.</summary>
    public bool TickActiveSpells(Data.IGameDatabase db)
    {
        if (ActiveSpells.Count == 0)
            return false;

        bool changed = false;
        for (int i = ActiveSpells.Count - 1; i >= 0; i--)
        {
            ActiveSpells[i].RemainingDuration--;
            if (ActiveSpells[i].RemainingDuration <= 0)
            {
                ActiveSpells.RemoveAt(i);
                changed = true;
            }
        }

        if (changed)
            RecomputeBuffStats(db);
        return changed;
    }

    /// <summary>
    /// Effective value of a monster ability = template intrinsic merged with active-spell buffs
    /// merged by the monster ability rules. Combat reads this so ANY buffed ability takes effect, not
    /// just haste/slow. Db-free: served from the cache rebuilt on each buff change.
    /// </summary>
    public int GetEffectiveAbility(int abilityId)
    {
        int templateValue = Template?.Abilities?.GetValueOrDefault(abilityId) ?? 0;
        if (ActiveSpells.Count == 0)
            return templateValue;

        return _effectiveAbilities.TryGetValue(abilityId, out var value) ? value : templateValue;
    }

    // The fighter build and spell-resist paths incorporate abilities into the monster's defensive
    // stats (from template intrinsics, equipped items, AND active buffs), so these are not base-only:
    //   DV (defense)  += ability 2
    //   DG (dodge)     = ability 34
    //   MR (resist)   += ability 36
    // Kept additive on the base template field so existing base values are preserved.
    public int EffectiveArmourClass => (Template?.ArmourClass ?? 0) + GetEffectiveAbility(2);
    // DG (the post-hit dodge gate) is ability 34 ONLY
    // — NOT BSDefense. BSDefense is the *backstab* defense and is used
    // only in the backstab-AC formula (GetMonsterBackstabArmourClass), not normal-combat dodge.
    public int EffectiveDodge => GetEffectiveAbility(34);
    // The dodge-skill column: ability 24 (context-selected; 25 in the
    // alternate branch) + 10 when the monster has ability 9. Feeds the to-hit denominator. Stock
    // gates the 24/25 choice on attack context; only 3 monsters carry these abilities, so this
    // additive upper bound is faithful in practice.
    public int EffectiveDodgeSkill => GetEffectiveAbility(24) + (GetEffectiveAbility(9) != 0 ? 10 : 0);
    public int EffectiveMagicResist => (Template?.MagicRes ?? 0) + GetEffectiveAbility(36);

    // Backstab: AC = (instance AC >> 1) + BSDefense*2.
    public int GetMonsterBackstabArmourClass() => (EffectiveArmourClass >> 1) + (Template?.BSDefense ?? 0) * 2;

    /// <summary>
    /// Rebuild the effective-ability cache from template intrinsics + active spells. The
    /// aggregation rules: MAX-set ids take the max, all others sum; a spell contributes
    /// its own value, or its rolled slot magnitude (<see cref="ActiveSpell.CastLevel"/>) when that
    /// value is 0; ability 124 suppresses (zeroes) the id it names. Call after any buff add/expiry.
    /// </summary>
    public void RecomputeBuffStats(Data.IGameDatabase db)
    {
        _effectiveAbilities.Clear();
        if (ActiveSpells.Count == 0)
            return;

        var ids = new HashSet<int>();
        if (Template?.Abilities != null)
            foreach (var id in Template.Abilities.Keys)
                ids.Add(id);
        foreach (var active in ActiveSpells)
            if (db.Spells.TryGetValue(active.SpellId, out var spell))
                foreach (var id in spell.Abilities.Keys)
                    ids.Add(id);

        foreach (var id in ids)
            _effectiveAbilities[id] = AggregateAbility(db, id);

        foreach (var active in ActiveSpells)
            if (db.Spells.TryGetValue(active.SpellId, out var spell)
                && spell.Abilities.TryGetValue(SuppressAbilityId, out var suppressedId))
            {
                _effectiveAbilities[suppressedId] = 0;
            }
    }

    private int AggregateAbility(Data.IGameDatabase db, int abilityId)
    {
        bool useMax = MonsterMaxAbilities.Contains(abilityId);
        int acc = 0;
        bool seen = false;

        void Apply(int value)
        {
            if (useMax)
            {
                if (!seen || value > acc)
                    acc = value;
            }
            else
            {
                acc += value;
            }
            seen = true;
        }

        if (Template?.Abilities != null && Template.Abilities.TryGetValue(abilityId, out var tv))
            Apply(tv);
        foreach (var active in ActiveSpells)
            if (db.Spells.TryGetValue(active.SpellId, out var spell) && spell.Abilities.TryGetValue(abilityId, out var sv))
                Apply(sv != 0 ? sv : active.CastLevel);

        return acc;
    }

    public static MonsterInstance Create(Monster template, int mapNumber, int roomNumber, bool isPermanentNPC = false, bool isBackgroundSpawn = false, bool guaranteeAllDrops = false)
    {
        // Randomly pick a flavor prefix from the monster's DescTxt textblock
        string displayName = template.Name;
        if (template.FlavorTexts.Count > 0)
        {
            var prefix = template.FlavorTexts[_rng.Next(template.FlavorTexts.Count)];
            displayName = string.IsNullOrEmpty(prefix) ? template.Name : $"{prefix} {template.Name}";
        }

        var monster = new MonsterInstance
        {
            Template = template,
            CurrentHP = template.HP,
            MaxHP = template.HP,
            DisplayName = displayName,
            MapNumber = mapNumber,
            RoomNumber = roomNumber,
            HomeMapNumber = mapNumber,
            HomeRoomNumber = roomNumber,
            IsBackgroundSpawn = isBackgroundSpawn,
            IsPermanentNPC = isPermanentNPC,
        };

        monster.RollCarriedTreasure(guaranteeAllDrops);
        monster.ResetEnergy();
        return monster;
    }
}
