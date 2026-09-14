using System.Linq;

namespace mmudreborn.Data.Models;

public class Race
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public int MinInt { get; set; }
    public int MinWil { get; set; }
    public int MinStr { get; set; }
    public int MinHea { get; set; }
    public int MinAgl { get; set; }
    public int MinChm { get; set; }
    public int MaxInt { get; set; }
    public int MaxWil { get; set; }
    public int MaxStr { get; set; }
    public int MaxHea { get; set; }
    public int MaxAgl { get; set; }
    public int MaxChm { get; set; }
    public int HPPerLvl { get; set; }
    public int ExpTable { get; set; }
    public int BaseCP { get; set; }
    public Dictionary<int, int> Abilities { get; set; } = [];

    public bool HasStealth => Abilities.ContainsKey(102); // Race Stealth (ability 102)
    public bool HasNightVision => Abilities.ContainsKey(2); // NV
}

public class CharacterClass
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public int MinHits { get; set; }
    public int MaxHits { get; set; }
    public int ExpTable { get; set; }
    public int MageryType { get; set; }
    public int MageryLvl { get; set; }
    public int WeaponType { get; set; }
    public int ArmourType { get; set; }
    public int CombatLvl { get; set; }
    public int TitleText { get; set; }
    public List<string> LevelTitles { get; set; } = [];
    public Dictionary<int, int> Abilities { get; set; } = [];

    public string? GetRawTitleForLevel(int level)
    {
        if (LevelTitles.Count == 0)
            return null;

        int index = Math.Clamp(level, 1, LevelTitles.Count) - 1;
        return LevelTitles[index];
    }
}

// Values match the stock exit-type switch. Names verified against the
// stock cases plus the live game_data.RoomExits distribution. A few legacy names are kept for
// stability but are inaccurate — see the inline notes. Any type CanTraverseExit does not gate
// falls through to normal passage (correct for the addon/dead types in our data set).
public enum RoomExitType
{
    Normal = 0,
    Spell = 1,        // portal: passable only via follow/cast-teleport, never a plain walk (dead in
                      // data — 0 rows — but faithfully gated in CanTraverseExit, per stock type 1)
    Key = 2,
    Item = 3,
    Toll = 4,
    Action = 5,       // type 5: Para1!=0 → a room-entry permission check, else normal
    Hidden = 6,       // search/remote-action reveal; Para1 is a runtime slot bitfield (see GameWorld.Exits)
    Door = 7,
    ChangeMap = 8,    // MISLABEL: type 8 = addon/purchase gate ("your sysop must purchase…").
                      // No addon system here, so these pass through — intended for our content.
    Trap = 9,
    Text = 10,
    Gate = 11,
    RemoteAction = 12, // speech-triggered trigger exit; NOT walkable (type 12 blocks with "no exit")
    Class = 13,
    Race = 14,
    Level = 15,
    Timed = 16,       // MISLABEL: type 16 = sealed exit w/ denial message, but only when Para3!=0.
                      // Both live rows have Para3=0 → stock passes them through, same as us.
    Ticket = 17,
    UserCount = 18,   // dead in data (0 rows); not gated at move-time in stock
    BlockGuard = 19,  // guards aggro but never bar the exit — intentionally non-blocking (see memory)
    Alignment = 20,
    Delay = 21,       // dead in data (0 rows)
    Cast = 22,        // type 22: a room cast of Para1 on traverse, then pass
    Ability = 23,
    SpellTrap = 24,
}

[Flags]
public enum RoomAttributes
{
    None = 0,
    Protected = 1 << 0,
    Patrol = 1 << 1,
    Ownable = 1 << 2,
    Bit4 = 1 << 3,
    Clear = 1 << 4,
    Bit6 = 1 << 5,
    GangHouse = 1 << 6,
    Bit8 = 1 << 7,
}

public sealed class RoomMessage
{
    public int Number { get; set; }
    public string Line1 { get; set; } = "";
    public string Line2 { get; set; } = "";
    public string Line3 { get; set; } = "";

    public IEnumerable<string> GetNonEmptyLines()
    {
        if (!string.IsNullOrWhiteSpace(Line1)) yield return Line1.Trim();
        if (!string.IsNullOrWhiteSpace(Line2)) yield return Line2.Trim();
        if (!string.IsNullOrWhiteSpace(Line3)) yield return Line3.Trim();
    }
}

public sealed class SocialAction
{
    public string Name { get; set; } = "";
    public int? DisplayOrder { get; set; }
    public string SingleToUser { get; set; } = "";
    public string SingleToRoom { get; set; } = "";
    public string UserToUser { get; set; } = "";
    public string UserToOtherUser { get; set; } = "";
    public string UserToRoom { get; set; } = "";
    public string MonsterToUser { get; set; } = "";
    public string MonsterToRoom { get; set; } = "";
    public string InventoryToUser { get; set; } = "";
    public string InventoryToRoom { get; set; } = "";
    public string FloorItemToUser { get; set; } = "";
    public string FloorItemToRoom { get; set; } = "";

    public bool SupportsSingleTarget =>
        !string.IsNullOrWhiteSpace(SingleToUser) ||
        !string.IsNullOrWhiteSpace(SingleToRoom);

    public bool SupportsUserTarget =>
        !string.IsNullOrWhiteSpace(UserToUser) ||
        !string.IsNullOrWhiteSpace(UserToOtherUser) ||
        !string.IsNullOrWhiteSpace(UserToRoom);

    public bool SupportsMonsterTarget =>
        !string.IsNullOrWhiteSpace(MonsterToUser) ||
        !string.IsNullOrWhiteSpace(MonsterToRoom);

    public bool SupportsInventoryTarget =>
        !string.IsNullOrWhiteSpace(InventoryToUser) ||
        !string.IsNullOrWhiteSpace(InventoryToRoom);

    public bool SupportsFloorItemTarget =>
        !string.IsNullOrWhiteSpace(FloorItemToUser) ||
        !string.IsNullOrWhiteSpace(FloorItemToRoom);
}

public sealed class RoomExitDefinition
{
    public int MapNumber { get; set; }
    public int RoomNumber { get; set; }
    public int DirectionIndex { get; set; }
    public string Direction { get; set; } = "";
    public int TargetMap { get; set; }
    public int TargetRoom { get; set; }
    public RoomExitType ExitType { get; set; }
    public int Para1 { get; set; }
    public int Para2 { get; set; }
    public int Para3 { get; set; }
    public int Para4 { get; set; }
    public bool IsLegacySpecial { get; set; }
    public List<string> LegacyCommands { get; set; } = [];

    public bool HasDestination => TargetMap > 0 && TargetRoom > 0;
    public bool IsDoorLike => ExitType is RoomExitType.Door or RoomExitType.Gate;
    public bool IsBarrierExit => ExitType is RoomExitType.Key or RoomExitType.Door or RoomExitType.Gate;
    // In the LOOK direction switch, only a closed Key (type 2) or Door (type 7) blocks looking
    // through to the room beyond ("The door is closed in that direction!"). A Gate (case 11) is NOT in that
    // switch, so it falls through to the room description — you SEE the room beyond a gate whether it is open
    // or closed. So look-opacity is strictly narrower than movement-blocking (IsBarrierExit, which DOES
    // include Gate): you can peer through a closed jail-cell gate but not a closed door.
    public bool BlocksLookThroughWhenClosed => ExitType is RoomExitType.Key or RoomExitType.Door;
    public bool IsTextCommandExit => ExitType == RoomExitType.Text || IsLegacySpecial;
    public bool IsRemoteAction => ExitType == RoomExitType.RemoteAction;
    public bool IsToll => ExitType == RoomExitType.Toll;
    public bool IsItemExit => ExitType is RoomExitType.Item or RoomExitType.Ticket;
    public bool IsTicketExit => ExitType == RoomExitType.Ticket;
    public bool IsHiddenExit => ExitType == RoomExitType.Hidden;
    public bool IsSearchableHiddenExit => ExitType == RoomExitType.Hidden && (Para1 & 2) != 0;
    public bool IsPassableHiddenExit => ExitType == RoomExitType.Hidden && (Para1 & 1) != 0;
    // Hidden-exit Para1 is a DAT bitfield; level-gated exits use their own exit types.
    public int RequiredLevel => 0;
    public bool NeedsRemoteActions => ExitType == RoomExitType.Hidden && (Para1 >= 16 || (Para1 & 8) != 0 || Para2 != 0);
    public int RequiredItemId => IsItemExit ? Para1 : 0;
    public int FailureMessageNumber => IsItemExit ? Para2 : 0;
    public int SuccessMessageNumber => IsItemExit ? Para3 : 0;
    public bool StartsVisible => ExitType switch
    {
        RoomExitType.Hidden => (Para1 & 4) != 0 || (Para1 & 32768) != 0,
        RoomExitType.Text => false,
        RoomExitType.RemoteAction => false,
        _ => true,
    };

    // Barrier exits store an explicit lock-state field separate from key id and difficulty.
    public int LockStateRaw => ExitType switch
    {
        RoomExitType.Key => Para2,
        RoomExitType.Door or RoomExitType.Gate => Para1,
        _ => 0,
    };

    public bool StartsLocked => LockStateRaw > 0;

    public int RequiredKeyItemId => ExitType switch
    {
        RoomExitType.Key => Para1,
        RoomExitType.Door or RoomExitType.Gate => Para4,
        _ => 0,
    };

    public int OpenDurationBlocks => ExitType switch
    {
        RoomExitType.Key => Para4,
        RoomExitType.Door or RoomExitType.Gate => Para3,
        _ => 0,
    };

    public int PickDifficultyRaw => ExitType switch
    {
        RoomExitType.Key => Para3,
        RoomExitType.Door or RoomExitType.Gate => Para2,
        _ => 0,
    };

    public int BashDifficultyRaw => ExitType is RoomExitType.Door or RoomExitType.Gate ? Para2 : 0;
    // PICKLOCK attempts any locked barrier of the right type; the difficulty value is
    // NOT a yes/no gate, it is an additive term in the success roll (see PassesExitSkillCheck). A Key
    // (type 2) exit has no difficulty!=0 gate at all — Para3 may legitimately be 0 (skill-only pick).
    public bool CanPicklock => ExitType switch
    {
        RoomExitType.Key => LockStateRaw >= 2,
        RoomExitType.Door or RoomExitType.Gate => LockStateRaw >= 2,
        _ => false,
    };
    public bool CanBash => ExitType is RoomExitType.Door or RoomExitType.Gate && LockStateRaw >= 2;
    public string DoorNoun => ExitType == RoomExitType.Gate ? "gate" : "door";

    // Trap exits (type 9): Para1=damage, Para2=armed state (0/3 armed, 1 disarmed),
    // Para4=trap message id. Moving through an armed trap deals a roll over [dmg/2, dmg].
    public bool IsTrapExit => ExitType == RoomExitType.Trap;
    public int TrapDamage => IsTrapExit ? Para1 : 0;
    public int TrapMessageId => IsTrapExit ? Para4 : 0;
    // Spell-trap (type 24): casts Para1's spell on traverse (Para4 = "trap fires" room message).
    // DISARM handles BOTH type 9 and type 24; both disarm + re-arm identically.
    public bool IsSpellTrapExit => ExitType == RoomExitType.SpellTrap;
    public int SpellTrapSpellId => IsSpellTrapExit ? Para1 : 0;
    // Either kind is disarmable via `disarm trap` and shares the re-arm timer.
    public bool IsDisarmableTrapExit => IsTrapExit || IsSpellTrapExit;
    public bool TrapStartsArmed => IsDisarmableTrapExit && Para2 != 1;

    public IEnumerable<string> GetCommandPhrases(IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        if (LegacyCommands.Count > 0)
            return LegacyCommands;

        if (!IsTextCommandExit && !IsRemoteAction)
            return Array.Empty<string>();

        if (messages == null || !messages.TryGetValue(Para1, out var message))
            return Array.Empty<string>();

        return message.GetNonEmptyLines()
            .Select(line => line.Trim().ToLowerInvariant())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
    }

    public RoomMessage? GetPrimaryMessage(IReadOnlyDictionary<int, RoomMessage>? messages, int messageNumber)
    {
        if (messageNumber <= 0 || messages == null)
            return null;

        messages.TryGetValue(messageNumber, out var message);
        return message;
    }
}

public class Room
{
    public int MapNumber { get; set; }
    public int RoomNumber { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Attributes { get; set; }
    public int Light { get; set; }
    public int Shop { get; set; }
    public int NPC { get; set; }
    public int CMD { get; set; }
    public int Spell { get; set; }
    public int RoomType { get; set; }

    /// <summary>
    /// The room class (the map legend's 1 S(hop), 2 A(rena), 3 L(air), …), tested
    /// against 1 before ANY shop verb will look at the room's shop number: LIST,
    /// BUY, SELL, DEPOSIT, WITHDRAW, TRAIN, STOCK/UNSTOCK/MARKUP and
    /// the shop-item display all refuse outright when this isn't 1.
    ///
    /// The Shop field alone is NOT the test: 1,458 rooms in the shipped data carry a shop number without
    /// being shop rooms (Gold Mine Tunnel → 108, the gang-house entrances → the Realm Deed Shop #124),
    /// which is why stock answers "You cannot LIST if you are not in a shop!" in the Gold House entrance
    /// even though a shop id is sitting on the room.
    ///
    /// The one documented exception is APPRAISE, which reads the shop number with no room-class
    /// test at all — appraising works anywhere a shop number happens to be attached.
    /// </summary>
    public bool IsShopRoom => RoomType == ShopRoomType;

    public const int ShopRoomType = 1;
    /// <summary>Gang-house id 1..10. 0 = not a gang-house room.
    /// Set for the 134 colour-house rooms; distinct from the GangHouse attribute bit, which a few
    /// non-house rooms also carry.</summary>
    public int GangHouseId { get; set; }
    public int MaxRegen { get; set; }
    public int MonsterType { get; set; }
    public int MinIndex { get; set; }
    public int MaxIndex { get; set; }
    // The room "by Number" field: when non-zero, the spawn places THIS specific monster #
    // in the room and skips the group/index random pick (stock only runs the group scan when this is
    // 0). Seeded by EnsureRoomByNumberImported; consulted by the lair spawner ahead of MonsterType.
    public int ByNumber { get; set; }
    public int Delay { get; set; }

    // Control room: the room that anchors this room's "area" cluster — the
    // shared monster-population budget lives there. 0 = this room is not part of a control-room cluster.
    public int ControlRoom { get; set; }

    // Area-wide monster cap ("Area Max"). Only meaningful on a control room;
    // controlRoom.MaxArea is the population ceiling for the whole cluster. 0 = no area cap.
    public int MaxArea { get; set; }
    public string Placed { get; set; } = "";
    public string HiddenItems { get; set; } = "";
    public int GroundCurrency { get; set; }

    // Death room: when a player dies in this room, they respawn at this room number (same map)
    // 0 = use default death room for the map
    public int DeathRoom { get; set; }

    // Exit room (the field immediately after DeathRoom in the DAT record): when a
    // player LEAVES the game (disconnect/quit) while standing in this room, their saved room is rewritten
    // to this room number (same map), so on their next login they reappear here. This is how the original lets
    // a player escape a disorienting warp maze — the Warped Asylum (map 9 → 1180) and Mirrored Hall
    // (map 3 → 564) tag every maze room with the same bail-out room. 0 = no relocation (normal room).
    // Applied via GameWorld.ApplyWarpExitRoomOnLeave. NB: distinct from the warp itself (random teleport
    // on traverse via type-22 Cast exits); this is just the log-out escape hatch.
    public int ExitRoom { get; set; }

    // Exits stored as "MapNum/RoomNum" or "MapNum/RoomNum (Door)" or "0"
    public string N { get; set; } = "0";
    public string S { get; set; } = "0";
    public string E { get; set; } = "0";
    public string W { get; set; } = "0";
    public string NE { get; set; } = "0";
    public string NW { get; set; } = "0";
    public string SE { get; set; } = "0";
    public string SW { get; set; } = "0";
    public string U { get; set; } = "0";
    public string D { get; set; } = "0";

    private readonly Dictionary<string, RoomExitDefinition> _typedExits = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (int Index, string Direction, Func<Room, string> Getter)[] ExitSlots =
    {
        (0, "north", room => room.N),
        (1, "south", room => room.S),
        (2, "east", room => room.E),
        (3, "west", room => room.W),
        (4, "northeast", room => room.NE),
        (5, "northwest", room => room.NW),
        (6, "southeast", room => room.SE),
        (7, "southwest", room => room.SW),
        (8, "up", room => room.U),
        (9, "down", room => room.D),
    };

    public RoomAttributes AttributeFlags => (RoomAttributes)Attributes;
    public bool HasAttribute(RoomAttributes attribute) => (AttributeFlags & attribute) == attribute;
    public bool IsProtected => HasAttribute(RoomAttributes.Protected);
    public bool IsPatrollable => HasAttribute(RoomAttributes.Patrol);
    public bool IsOwnable => HasAttribute(RoomAttributes.Ownable);
    public bool IsClearAtCleanup => HasAttribute(RoomAttributes.Clear);
    public bool IsGangHouse => HasAttribute(RoomAttributes.GangHouse);
    public bool HasBit4 => HasAttribute(RoomAttributes.Bit4);
    public bool HasBit6 => HasAttribute(RoomAttributes.Bit6);
    public bool HasBit8 => HasAttribute(RoomAttributes.Bit8);

    public bool IsLit => Light >= 0;

    /// <summary>Returns a light level description string, or null if room is fully visible.</summary>
    public string? GetLightDescription() => GetLightDescription(Light);

    public static string? GetLightDescription(int effectiveLight)
    {
        if (effectiveLight >= 0) return null;
        if (effectiveLight >= -150) return "The room is dimly lit";
        if (effectiveLight > -200) return "The room is very dark - you can't see anything";
        return "The room is pitch black - you can't see anything";
    }

    /// <summary>Whether the room is too dark to see contents (below -150).</summary>
    public bool IsTooBlind => IsTooBlindForLight(Light);

    public static bool IsTooBlindForLight(int effectiveLight) => effectiveLight < -150;

    /// <summary>Get placed item IDs from the comma-separated Placed field.</summary>
    public List<int> GetPlacedItemIds()
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(Placed)) return ids;
        foreach (var part in Placed.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int id) && id > 0)
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>Get hidden item IDs from the comma-separated HiddenItems field.</summary>
    public List<int> GetHiddenItemIds()
    {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(HiddenItems)) return ids;
        foreach (var part in HiddenItems.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int id) && id > 0)
                ids.Add(id);
        }
        return ids;
    }

    public void SetExit(RoomExitDefinition exit)
    {
        _typedExits[exit.Direction] = exit;
    }

    public bool HasTypedExits => _typedExits.Count > 0;

    public IReadOnlyDictionary<string, RoomExitDefinition> GetExitDefinitions()
    {
        if (_typedExits.Count > 0)
            return _typedExits;

        var legacy = new Dictionary<string, RoomExitDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, direction, getter) in ExitSlots)
        {
            var exit = ParseLegacyExit(index, direction, getter(this));
            if (exit != null)
                legacy[direction] = exit;
        }

        return legacy;
    }

    public RoomExitDefinition? GetExit(string direction)
    {
        GetExitDefinitions().TryGetValue(direction, out var exit);
        return exit;
    }

    public Dictionary<string, (int Map, int Room, List<string> Commands)> GetTextCommandExits(IReadOnlyDictionary<int, RoomMessage>? messages = null)
    {
        return GetExitDefinitions()
            .Values
            .Where(exit => exit.IsTextCommandExit && exit.HasDestination)
            .ToDictionary(
                exit => exit.Direction,
                exit => (exit.TargetMap, exit.TargetRoom, exit.GetCommandPhrases(messages).ToList()),
                StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, (int Map, int Room)> GetHiddenExits()
    {
        return GetExitDefinitions()
            .Values
            .Where(exit => exit.IsHiddenExit && exit.HasDestination)
            .ToDictionary(
                exit => exit.Direction,
                exit => (exit.TargetMap, exit.TargetRoom),
                StringComparer.OrdinalIgnoreCase);
    }

    public Dictionary<string, (int Map, int Room, bool Door)> GetExits()
    {
        return GetExitDefinitions()
            .Values
            .Where(exit => exit.HasDestination && !exit.IsHiddenExit && !exit.IsTextCommandExit && !exit.IsRemoteAction)
            .ToDictionary(
                exit => exit.Direction,
                exit => (exit.TargetMap, exit.TargetRoom, exit.IsBarrierExit),
                StringComparer.OrdinalIgnoreCase);
    }

    public string GetExitString()
    {
        var exits = GetExits();
        if (exits.Count == 0) return "none";
        return string.Join(", ", exits.Keys);
    }

    private RoomExitDefinition? ParseLegacyExit(int directionIndex, string direction, string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue) || rawValue == "0")
            return null;

        var match = System.Text.RegularExpressions.Regex.Match(rawValue, @"(?<map>\d+)\/(?<room>\d+)");
        if (!match.Success)
            return null;

        int map = int.Parse(match.Groups["map"].Value);
        int room = int.Parse(match.Groups["room"].Value);

        var exit = new RoomExitDefinition
        {
            MapNumber = MapNumber,
            RoomNumber = RoomNumber,
            DirectionIndex = directionIndex,
            Direction = direction,
            TargetMap = map,
            TargetRoom = room,
            ExitType = RoomExitType.Normal,
        };

        if (rawValue.Contains("(Door", StringComparison.OrdinalIgnoreCase)
            || rawValue.Contains("(Gate", StringComparison.OrdinalIgnoreCase))
        {
            exit.ExitType = rawValue.Contains("(Gate", StringComparison.OrdinalIgnoreCase)
                ? RoomExitType.Gate
                : RoomExitType.Door;
        }

        if (rawValue.Contains("(Hidden", StringComparison.OrdinalIgnoreCase))
        {
            exit.ExitType = RoomExitType.Hidden;
            if (rawValue.Contains("Searchable", StringComparison.OrdinalIgnoreCase))
                exit.Para1 = 2;
            else
                exit.Para1 = 1;
        }

        var textMatch = System.Text.RegularExpressions.Regex.Match(rawValue, @"\(Text:\s*([^)]+)\)");
        if (textMatch.Success)
        {
            exit.ExitType = RoomExitType.Text;
            exit.LegacyCommands = textMatch.Groups[1].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(command => command.Trim().ToLowerInvariant())
                .Where(command => !string.IsNullOrWhiteSpace(command))
                .ToList();
        }

        if (rawValue.Contains("(Special)", StringComparison.OrdinalIgnoreCase))
            exit.IsLegacySpecial = true;

        return exit;
    }
}

public class Monster
{
    private static readonly string[] FemaleDescriptionClues =
    [
        "she", "her", "hers", "herself", "woman", "girl", "female", "queen", "lady",
        "mother", "daughter", "wife", "sister", "priestess"
    ];

    private static readonly string[] MaleDescriptionClues =
    [
        "he", "him", "his", "himself", "man", "boy", "male", "father", "son",
        "husband", "brother", "priest"
    ];

    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Gender { get; set; }
    public int Weapon { get; set; }
    public int ArmourClass { get; set; }
    public int DamageResist { get; set; }
    public int FollowPercent { get; set; }
    // The per-monster free-attack chance rolled
    // when a non-sneaking player leaves the room (a roll of 0..99 <= Active).
    public int Active { get; set; }
    public int MagicRes { get; set; }
    public int BSDefense { get; set; }
    public double EXP { get; set; }
    public double ExpMulti { get; set; }
    public int HP { get; set; }
    public int Energy { get; set; }
    public double AvgDmg { get; set; }
    public int GreetTXT { get; set; }
    public int HPRegen { get; set; }
    public int CharmLVL { get; set; }
    // Solo/Leader/Follower/Stationary: 0=Solo, 1=Leader, 2=Follower, 3=Stationary.
    public int Type { get; set; }
    // Leader/Follower group-formation (the anchor + drag rules). GroupMatch is the
    // cohesion id — a Follower/subordinate-Leader anchors to a same-room Leader with the SAME GroupMatch;
    // MaxFollowers caps how many same-group members a moving Leader drags along. The
    // leader-dominance tiebreak among Leaders is ExpMulti.
    public int GroupMatch { get; set; }
    public int MaxFollowers { get; set; }
    public bool Undead { get; set; }
    public int Align { get; set; }
    public int RegenTime { get; set; }
    public int GameLimit { get; set; }

    // Currency drops: R=Runic, P=Platinum, G=Gold, S=Silver, C=Copper
    public int Runic { get; set; }
    public int Platinum { get; set; }
    public int Gold { get; set; }
    public int Silver { get; set; }
    public int Copper { get; set; }

    public int DeathSpell { get; set; }
    public int CreateSpell { get; set; }
    // DeathMsg: references a Messages number whose Line3 is this monster's death line
    // (e.g. wyvern → 3184 → "The wyvern lets out a high pitched roar, and crashes to the floor.").
    public int DeathMsg { get; set; }
    // MoveMsg: a Messages number whose Line1/Line2/Line3 are the
    // spawn-entrance / departure / chase text. Stock uses it ONLY on SPAWN;
    // room-to-room movement is always the generic line. 0 ⇒ "just arrived from nowhere" on spawn.
    public int MoveMsg { get; set; }
    public int Group { get; set; }
    public int GroupIndex { get; set; }

    // DescTxt: references a TextBlock number containing B:/N: flavor prefixes
    public int DescTxt { get; set; }
    // Parsed flavor prefixes from the TextBlock (e.g., "nasty", "angry", "" for bare name)
    public List<string> FlavorTexts { get; set; } = [];

    public List<MonsterAttack> Attacks { get; set; } = [];
    public List<MonsterSpell> MidSpells { get; set; } = [];
    public List<MonsterDrop> Drops { get; set; } = [];
    public Dictionary<int, int> Abilities { get; set; } = [];

    // Per-drop uses/charges override for a dropped item (the DropUses entry passed to the
    // 4th arg). 0 = the item's default UseCount. Looked up by item id at death-drop time — DropUses is
    // a static template property, so this equals capturing it at spawn (no stock monster lists the same
    // drop item in two slots with differing uses).
    public int GetDropUses(int itemId)
    {
        foreach (var drop in Drops)
        {
            if (drop.ItemId == itemId)
                return drop.Uses;
        }
        return 0;
    }

    // Stock movement/death messages imported into and loaded from the Messages table.
    // Line1 patterns: "A %s crawls into the room from %s." / "The %s bites you for %d damage!"
    public string? EntranceMessage { get; set; }  // e.g. "A %s crawls into the room from %s."
    public string? ExitMessage { get; set; }       // e.g. "The %s crawls out of the room to %s."
    public string? ChaseMessage { get; set; }      // e.g. "The lashworm crawls into the room after you!"
    public string? DeathMessage { get; set; }      // e.g. "The lashworm falls dead at your feet."

    // The spawn distinguishes two states that both leave EntranceMessage empty, and renders
    // them DIFFERENTLY: a MoveMsg whose Messages row is genuinely ABSENT falls back to "<name> moves into
    // the room from nowhere.", while a row that EXISTS with a blank Line1 (the stock silence sentinels
    // #1/#66/#121/…) prints nothing at all. A nullable EntranceMessage cannot carry that distinction, so
    // LinkMonsterMessages records the lookup failure here. Default false = "resolved", which keeps every
    // hand-built template (tests, fixtures) on the pre-existing blank-means-silent behaviour.
    public bool MoveMsgMissing { get; set; }

    public string GetLookSubjectPronoun()
    {
        return Gender switch
        {
            2 => "She",
            1 => "He",
            _ => InferPronounFromDescription(),
        };
    }

    private string InferPronounFromDescription()
    {
        if (string.IsNullOrWhiteSpace(Description))
            return "It";

        var wordSet = new string(Description
                .Select(ch => char.IsLetter(ch) ? char.ToLowerInvariant(ch) : ' ')
                .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);

        if (FemaleDescriptionClues.Any(wordSet.Contains))
            return "She";

        if (MaleDescriptionClues.Any(wordSet.Contains))
            return "He";

        return "It";
    }
}

public class MonsterAttack
{
    public string Name { get; set; } = "";
    public int SlotIndex { get; set; }
    public int Type { get; set; }
    public int Accuracy { get; set; }
    public int Percent { get; set; }
    public double TruePercent { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public int Energy { get; set; }
    public int HitSpell { get; set; }
    public int HitMessageId { get; set; }
    public int DodgeMessageId { get; set; }
    public int MissMessageId { get; set; }
}

public class MonsterSpell
{
    public int SpellId { get; set; }
    public int Percent { get; set; }
    public int Level { get; set; }
}

public class MonsterDrop
{
    public int ItemId { get; set; }
    public int Percent { get; set; }
    // The death drop passes this per-item uses value along with the item, so
    // the dropped item lands in the room's per-slot uses/charges field with
    // THIS count, overriding the item's default UseCount (e.g. an iron key #1532 defaults to 4 uses but
    // a monster drops it with 10). 0 = use the item default (the stackable path).
    public int Uses { get; set; }
}

public class Item
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public int Limit { get; set; }
    public int Encum { get; set; }
    public int ItemType { get; set; }
    public int UseCount { get; set; }
    // TextBlock shown when the item is `read` (deed/book/note/sign descriptions). 0 = nothing to read.
    public int ReadTextBlock { get; set; }
    public int Price { get; set; }
    public int Currency { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
    public int ArmourClass { get; set; }
    public int DamageResist { get; set; }
    public int WeaponType { get; set; }
    public int ArmourType { get; set; }
    public int Worn { get; set; }
    public int Accy { get; set; }
    public bool Gettable { get; set; }
    public int StrReq { get; set; }
    public int Speed { get; set; }
    public bool NotDroppable { get; set; }
    public bool DestroyOnDeath { get; set; }
    public bool RetainAfterUses { get; set; }
    // The `rob` stealable flag. A rob refuses to steal an item
    // whose flag is 0 ("Your skills fail as you try to rob..."). Non-zero = robbable.
    public bool CanBeRobbed { get; set; }
    public int HitMsg { get; set; }
    public int MissMsg { get; set; }
    // The item DestructMsg field. On a charge deduction,
    // @14227-14245: when an item is consumed (charges hit 0 and the item is not reusable),
    // a non-zero DestructMsg indexes into the Messages table — Line1 prints to the holder and
    // Line2 (when non-empty) broadcasts to the room. DestructMsg == 0 falls back to the stock
    // default "It's uses gone, %s disappears from your inventory!". Most
    // learn-spell scrolls in the stock data carry DestructMsg=97 ("Its magic used, the scroll
    // disintegrates."); some special items have richer messages (e.g. msg 1357 "The scroll
    // explodes with unfathomable force!"). POPULATED: the converter exports the field, the seed
    // carries a DestructMsg column, and PostgresBootstrapper backfills it — 354/1950 stock items have
    // a non-zero value. NB: many consumables (120 potions/scrolls) point at message 8420, which is
    // deliberately BLANK in stock (verified in the NMR editor); on a charge deduction a non-zero
    // DestructMsg whose row is missing/blank prints NOTHING (silent vanish), and the generic
    // "It's uses gone..." fallback fires ONLY when DestructMsg == 0. See SendItemDestructionMessagesAsync.
    public int DestructMsg { get; set; }
    // "Coins on open": when a container (ItemType 8) is opened, the
    // engine rolls `lngrnd(0, max)` of each denomination straight into the opener's purse — silently,
    // separate from the gem/item loot the open-spell delivers. Stored at item record bytes
    // 944/948/952/956/960; seeded by EnsureItemsOpenCoinsImported.
    // That roll has an exclusive top (the monster-drop path adds +1 to include max; OPEN does
    // not), so a max of N yields [0, N-1] and a max of 0 yields 0. See RollOpenCoins.
    public int OpenRunic { get; set; }
    public int OpenPlatinum { get; set; }
    public int OpenGold { get; set; }
    public int OpenSilver { get; set; }
    public int OpenCopper { get; set; }

    public int[] ClassRestrictions { get; set; } = new int[10];
    public int[] RaceRestrictions { get; set; } = new int[10];
    public Dictionary<int, int> Abilities { get; set; } = [];
    // Ordered ability slots preserving DUPLICATES (the Abilities dict keeps only the last value of a
    // repeated ability). E.g. the black tome #764 carries Link-to-Spell (42) twice, one per spell it teaches.
    public List<KeyValuePair<int, int>> AbilitySlots { get; set; } = [];
    // The 10-entry "Negate Spells" list (the Nightmare Redux Race/Class/Negate
    // tab). When the wearer has this item equipped, the negate gate short-circuits any room-cast,
    // monster-cast, or duration spell whose number appears here (the magma amulet's [526, 218]
    // makes magma heat and temple of fire fire no-ops on the bearer). Stored as a HashSet for
    // O(1) `Contains` at the cast site; only non-zero slots are kept.
    public HashSet<int> NegatedSpellNumbers { get; set; } = [];
    // Ability 59 ("Class Item Inclusion" / Nightmare "ClassOk") permits additional class IDs to
    // equip the item on top of ClassRestrictions. Items may carry multiple Abil=59 slots (a dagger
    // has 59:12 AND 59:15), so we collect every occurrence here rather than only the last value
    // that wins in the Abilities dict.
    public List<int> ClassOkClassIds { get; set; } = [];
    // Ability 43 can appear twice: the first occurrence is the manual USE spell (self-buff),
    // the second is the weapon PROC spell (auto-fires on hit). The Abilities dict keeps only
    // the last value, so we shadow the first into UseSpellId and the second into ProcSpellId.
    public int UseSpellId { get; set; }
    public int ProcSpellId { get; set; }
    public bool InGame { get; set; }

    // True when this container has any non-zero coin-on-open field (skip the roll otherwise).
    public bool HasOpenCoins => OpenRunic > 0 || OpenPlatinum > 0 || OpenGold > 0 || OpenSilver > 0 || OpenCopper > 0;

    /// <summary>
    /// The OPEN coin roll: for each denomination with a non-zero max, roll over <c>[0, max)</c>
    /// (exclusive top → range [0, max-1], matching <c>Random.Next(0, max)</c>) and return the amounts.
    /// <paramref name="rollExclusive"/> maps an exclusive upper bound to a value in [0, bound); it is
    /// only invoked for denominations whose max &gt; 0.
    /// </summary>
    public (int Runic, int Platinum, int Gold, int Silver, int Copper) RollOpenCoins(System.Func<int, int> rollExclusive)
    {
        int Roll(int max) => max > 0 ? rollExclusive(max) : 0;
        return (Roll(OpenRunic), Roll(OpenPlatinum), Roll(OpenGold), Roll(OpenSilver), Roll(OpenCopper));
    }

    public int MinimumLevel => Abilities.GetValueOrDefault(135);

    public bool IsWeapon => Min > 0 || Max > 0;
    public bool IsArmour => ArmourClass > 0 && !IsWeapon;

    // EQUIP routes wield-vs-wear by the item TYPE field, NOT by damage:
    // ItemType == 1 is a wielded weapon (the main-hand "weapon" slot); everything else goes
    // through the wear path and is worn by its Worn/wear-location. So a damage-bearing OFF-HAND item — an
    // off-hand parry dagger like the "bleeding main-gauche" (ItemType 0, Worn 12), a thrown weapon
    // (ItemType 3), or a damaging scroll (ItemType 9) — must NOT take the main weapon slot just because
    // it has Min/Max. IsWeapon (damage) still drives combat/damage; IsWieldedWeapon drives equip routing.
    public const int WieldedWeaponItemType = 1;
    public bool IsWieldedWeapon => ItemType == WieldedWeaponItemType;

    public string GetItemTypeName() => ItemType switch
    {
        0 => "Armor",
        1 => "Weapon",
        2 => "Shield",
        3 => "Scroll",
        4 => "Potion",
        5 => "Edged",
        6 => "Light",
        7 => "Key",
        8 => "Container",
        9 => "Food",
        10 => "Drink",
        _ => "Other"
    };
}

public class GameSpell
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public string Short { get; set; } = "";
    public int ReqLevel { get; set; }
    public int EnergyCost { get; set; }
    public int ManaCost { get; set; }
    public int MinBase { get; set; }
    public int MaxBase { get; set; }
    public int Diff { get; set; }
    public int TypeOfResists { get; set; }
    public int Targets { get; set; }
    public int Duration { get; set; }
    public int AttType { get; set; }
    public int Magery { get; set; }
    public int MageryLvl { get; set; }
    public int Cap { get; set; }
    public int MaxIncLvls { get; set; }
    public int MaxInc { get; set; }
    public int MinIncLvls { get; set; }
    public int MinInc { get; set; }
    public int DurIncLvls { get; set; }
    public int DurInc { get; set; }
    // Duration random multiplier per effective level. The cast formula uses
    // `hi = DurRand * effLevel`; if hi > stepped base, duration is a roll over [base, hi].
    public int DurRand { get; set; }
    public bool Learnable { get; set; }
    public int SpellType { get; set; }
    public int CastMessageA { get; set; }
    public int CastMessageB { get; set; }
    public int MessageStyle { get; set; }
    public int ResistAbility { get; set; }
    public Dictionary<int, int> Abilities { get; set; } = [];
    // Ordered ability slots preserving DUPLICATES (the Abilities dict collapses repeats). Stock
    // iterates all 10 ability slots in order; e.g. "burning summon" carries ability 12 twice
    // (two different monster templates), which the dict would lose. Summon iterates this list.
    public List<KeyValuePair<int, int>> AbilitySlots { get; set; } = [];
    // Spell flavor text shown by `look <spell>`. Empty for spells
    // with no description.
    public string Description { get; set; } = "";
}

public class Shop
{
    public int Number { get; set; }
    public string Name { get; set; } = "";
    public int ShopType { get; set; }
    public int MinLvl { get; set; }
    public int MaxLvl { get; set; }
    public int MarkupPercent { get; set; }
    public int ClassRest { get; set; }
    public List<ShopItem> Items { get; set; } = [];
}

public class ShopItem
{
    public int ItemId { get; set; }
    public int Max { get; set; }       // shop slot max qty (restock cap). Max<=0 = unlimited.
    public int Time { get; set; }      // restock interval, in MINUTES.
    public int Amount { get; set; }    // restock qty added per tick.
    public int Percent { get; set; }   // restock probability % per tick (a roll of 1..99 < Percent).

    // --- Runtime stock state (NOT in the static game data; seeded to Max at load, decremented by buy
    // on buy, replenished on restock). Boots full, but DEPLETED slots are
    // persisted to ServerSettings (GameWorld.PersistShopStock) and re-applied after boot, so purchases
    // survive a restart instead of the stock silently refilling. ---
    public int Current { get; set; }
    public DateTime NextRestockUtc { get; set; }
}
