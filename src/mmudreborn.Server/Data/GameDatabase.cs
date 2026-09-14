using System.Data.Common;
using System.Text.Json;
using mmudreborn.Data.Models;
using mmudreborn.Server;
using Npgsql;
using NpgsqlTypes;

namespace mmudreborn.Data;

public class GameDatabase : IGameDatabase
{
    private static readonly string[] LegacyStockActionDisplayOrder =
    [
        "hug", "kiss", "wave", "grin", "bow", "bleed", "nod", "laugh", "shake", "cough", "shrug", "growl",
        "yawn", "wink", "nudge", "smirk", "smooch", "sigh", "highfive", "greet", "cuff", "slap", "jump",
        "snicker", "moan", "dance", "sneeze", "tease", "frown", "giggle", "pinch", "bearhug", "blink",
        "blush", "burp", "caress", "cheer", "chuckle", "clap", "comfort", "curtsy", "egrin", "embrace",
        "gasp", "glare", "groan", "grumble", "handshake", "hum", "pout", "smack", "smile", "sniff", "sob",
        "spit", "squeeze", "whimper", "whistle", "cackle", "elaugh", "flex", "girn", "howl", "tickle",
    ];

    private readonly string _connectionString;
    private readonly string? _helpTopicsPath;
    private Dictionary<string, SocialAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private List<SocialAction> _orderedActions = [];

    public Dictionary<int, Race> Races { get; } = [];
    public Dictionary<int, CharacterClass> Classes { get; } = [];
    public Dictionary<(int Map, int Room), Room> Rooms { get; } = [];
    public Dictionary<(int Map, int Room), string> RoomDescriptions { get; } = [];
    public Dictionary<int, Monster> Monsters { get; } = [];
    public Dictionary<int, Item> Items { get; } = [];
    public Dictionary<int, GameSpell> Spells { get; } = [];
    public Dictionary<int, Shop> Shops { get; } = [];
    public Dictionary<int, RoomMessage> Messages { get; } = [];
    public Dictionary<string, SocialAction> Actions => _actions;
    public List<SocialAction> OrderedActions => _orderedActions;
    public Dictionary<int, string> TextBlocks { get; } = [];
    public Dictionary<int, int> TextBlockLinks { get; } = [];
    public Dictionary<string, string> HelpTopics { get; } = new(StringComparer.OrdinalIgnoreCase);

    public GameDatabase(string connectionString, string? helpTopicsPath = null)
    {
        _connectionString = connectionString;
        _helpTopicsPath = helpTopicsPath;
    }

    public void LoadAll()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        LoadRaces(conn);
        LoadClasses(conn);
        LoadRooms(conn);
        LoadRoomExits(conn);
        LoadRoomDescriptions(conn);
        LoadMonsters(conn);
        LoadItems(conn);
        LoadSpells(conn);
        LoadShops(conn);
        LoadMessages(conn);
        LoadActions(conn);
        LoadTextBlocks(conn);
        LoadTextBlockLinks(conn);
        LoadClassTitles();
        LoadHelpTopics();

        // Parse flavor text prefixes from TextBlocks into Monster.FlavorTexts
        foreach (var mob in Monsters.Values)
        {
            if (mob.DescTxt > 0 && TextBlocks.TryGetValue(mob.DescTxt, out var blockText))
            {
                var lines = blockText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    if (line.StartsWith("B:"))
                    {
                        var prefix = line.Substring(2).Trim();
                        if (!string.IsNullOrEmpty(prefix))
                            mob.FlavorTexts.Add(prefix);
                    }
                    else if (line.StartsWith("N:"))
                    {
                        mob.FlavorTexts.Add(""); // empty = bare monster name
                    }
                }
            }
        }

        // Match monster movement/death messages from the imported Messages table
        // Messages come in pairs: even=entrance/exit/chase, odd=attack/3rdperson/death
        // The chase message (Line3 of entrance) and death message (Line3 of attack) contain
        // the hardcoded monster name, which we use for matching.
        LinkMonsterMessages();
        LinkMonsterDeathMessages();

        Console.WriteLine($"Loaded: {Races.Count} races, {Classes.Count} classes, {Rooms.Count} rooms, " +
                  $"{Monsters.Count} monsters, {Items.Count} items, {Spells.Count} spells, {Shops.Count} shops, {Messages.Count} messages, {Actions.Count} actions, {TextBlocks.Count} textblocks, {HelpTopics.Count} help topics");
    }

    public void ReloadActions()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        LoadActions(conn);
    }

    public void SaveAction(SocialAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        string normalizedName = NormalizeActionName(action.Name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            throw new ArgumentException("Action name is required.", nameof(action));

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        if (!TableExists(conn, "Actions"))
            throw new InvalidOperationException("game_data.\"Actions\" is not available.");

        EnsureActionSchema(conn);

        using var tx = conn.BeginTransaction();

        int? existingDisplayOrder = GetActionDisplayOrder(conn, tx, normalizedName);
        int? targetDisplayOrder = action.DisplayOrder ?? existingDisplayOrder;
        if (!targetDisplayOrder.HasValue)
            targetDisplayOrder = GetNextActionDisplayOrder(conn, tx);

        int normalizedDisplayOrder = MoveActionDisplayOrder(conn, tx, existingDisplayOrder, targetDisplayOrder.Value);

        using var update = conn.CreateCommand();
        update.Transaction = tx;
        update.CommandText = @"
            UPDATE game_data.""Actions""
            SET ""Name"" = @name,
                ""DisplayOrder"" = @displayOrder,
                ""SingleToUser"" = @singleToUser,
                ""SingleToRoom"" = @singleToRoom,
                ""UserToUser"" = @userToUser,
                ""UserToOtherUser"" = @userToOtherUser,
                ""UserToRoom"" = @userToRoom,
                ""MonsterToUser"" = @monsterToUser,
                ""MonsterToRoom"" = @monsterToRoom,
                ""InventoryToUser"" = @inventoryToUser,
                ""InventoryToRoom"" = @inventoryToRoom,
                ""FloorItemToUser"" = @floorItemToUser,
                ""FloorItemToRoom"" = @floorItemToRoom
            WHERE LOWER(""Name"") = LOWER(@name)";
        AddActionParameters(update, normalizedName, normalizedDisplayOrder, action);

        if (update.ExecuteNonQuery() == 0)
        {
            using var insert = conn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = @"
                INSERT INTO game_data.""Actions""
                (
                    ""Name"",
                    ""DisplayOrder"",
                    ""SingleToUser"",
                    ""SingleToRoom"",
                    ""UserToUser"",
                    ""UserToOtherUser"",
                    ""UserToRoom"",
                    ""MonsterToUser"",
                    ""MonsterToRoom"",
                    ""InventoryToUser"",
                    ""InventoryToRoom"",
                    ""FloorItemToUser"",
                    ""FloorItemToRoom""
                )
                VALUES
                (
                    @name,
                    @displayOrder,
                    @singleToUser,
                    @singleToRoom,
                    @userToUser,
                    @userToOtherUser,
                    @userToRoom,
                    @monsterToUser,
                    @monsterToRoom,
                    @inventoryToUser,
                    @inventoryToRoom,
                    @floorItemToUser,
                    @floorItemToRoom
                )";
            AddActionParameters(insert, normalizedName, normalizedDisplayOrder, action);
            insert.ExecuteNonQuery();
        }

        tx.Commit();
        ReloadActions();
    }

    public bool DeleteAction(string name)
    {
        string normalizedName = NormalizeActionName(name);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return false;

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        if (!TableExists(conn, "Actions"))
            return false;

        EnsureActionSchema(conn);

        using var tx = conn.BeginTransaction();
        int? existingDisplayOrder = GetActionDisplayOrder(conn, tx, normalizedName);

        using var delete = conn.CreateCommand();
        delete.Transaction = tx;
        delete.CommandText = @"
            DELETE FROM game_data.""Actions""
            WHERE LOWER(""Name"") = LOWER(@name)";
        delete.Parameters.AddWithValue("@name", normalizedName);

        if (delete.ExecuteNonQuery() == 0)
            return false;

        if (existingDisplayOrder.HasValue)
        {
            using var shift = conn.CreateCommand();
            shift.Transaction = tx;
            shift.CommandText = @"
                UPDATE game_data.""Actions""
                SET ""DisplayOrder"" = ""DisplayOrder"" - 1
                WHERE ""DisplayOrder"" IS NOT NULL
                  AND ""DisplayOrder"" > @displayOrder";
            shift.Parameters.AddWithValue("@displayOrder", existingDisplayOrder.Value);
            shift.ExecuteNonQuery();
        }

        tx.Commit();
        ReloadActions();
        return true;
    }

    /// <summary>
    /// Link each monster to its movement-message text via its dedicated MoveMsg id (template field
    /// The Messages record's Line1/Line2/Line3 are the spawn-entrance / departure / chase text.
    /// Stock uses this ONLY on spawn; room-to-room movement is always the generic
    /// line. (The previous name-in-Line3 heuristic missed every "from the %s" monster, e.g. pyrotrice.)
    /// </summary>
    private void LinkMonsterMessages()
    {
        int linked = 0;
        foreach (var mob in Monsters.Values)
        {
            if (mob.MoveMsg <= 0)
                continue;

            // A MoveMsg pointing at a row that isn't in the table is NOT the same as one pointing at a
            // stock blank sentinel — stock prints a generic arrival line for the former and nothing at
            // all for the latter. Record which one this is so FormatMonsterSpawnMessage can tell them
            // apart (both leave EntranceMessage null).
            if (!Messages.TryGetValue(mob.MoveMsg, out var msg))
            {
                mob.MoveMsgMissing = true;
                continue;
            }

            mob.EntranceMessage = string.IsNullOrEmpty(msg.Line1) ? null : msg.Line1;
            mob.ExitMessage = string.IsNullOrEmpty(msg.Line2) ? null : msg.Line2;
            mob.ChaseMessage = string.IsNullOrEmpty(msg.Line3) ? null : msg.Line3;
            if (mob.EntranceMessage != null)
                linked++;
        }

        if (!RuntimeConfiguration.IsQuietTestLoggingEnabled())
            Console.WriteLine($"  Linked {linked}/{Monsters.Count} monsters to MoveMsg movement messages");
    }

    /// <summary>
    /// Death message = Line3 of the message referenced by the monster's dedicated DeathMsg field
    /// (e.g. giant rat → 27 → "The giant rat falls to the ground with a tortured squeak.", wyvern →
    /// 3184 → "The wyvern lets out a high pitched roar, and crashes to the floor."). It is its OWN
    /// data field — NOT derived from attack hit messages. (The old attack-Line3 heuristic mis-fired:
    /// the wyvern reuses the giant rat's bite message 27 in attack slot 2, so it inherited the rat's
    /// death line.) The death line is a fixed string (no DisplayName interpolation), so flavour-prefixed
    /// instances ("fat giant rat") still print the canonical death line, not a "The fat …" fallback.
    /// A monster with no DeathMsg, or one pointing at a missing/blank message (the stock blank sentinels
    /// 1/66/121 etc., same as item DestructMsg), keeps an empty DeathMessage and — since the kill
    /// has NO generic fallback — dies with NO death line (silent;
    /// the kill still shows the hit + exp). See CombatEngine.GetDeathMessage.
    /// </summary>
    private void LinkMonsterDeathMessages()
    {
        int linked = 0;
        foreach (var mob in Monsters.Values)
        {
            if (mob.DeathMsg > 0
                && Messages.TryGetValue(mob.DeathMsg, out var msg)
                && !string.IsNullOrWhiteSpace(msg.Line3))
            {
                mob.DeathMessage = msg.Line3.Trim();
                linked++;
            }
        }

        if (!RuntimeConfiguration.IsQuietTestLoggingEnabled())
            Console.WriteLine($"  Linked {linked}/{Monsters.Count} monsters to death messages (attack-message Line3)");
    }

    private static bool TableExists(NpgsqlConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = 'game_data'
              AND table_name = @name";
        cmd.Parameters.AddWithValue("@name", tableName);
        return cmd.ExecuteScalar() != null;
    }

    private static string NormalizeActionName(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? string.Empty
            : name.Trim().ToLowerInvariant();
    }

    private static void AddActionParameters(NpgsqlCommand cmd, string normalizedName, int displayOrder, SocialAction action)
    {
        cmd.Parameters.AddWithValue("@name", normalizedName);
        cmd.Parameters.AddWithValue("@displayOrder", displayOrder);
        cmd.Parameters.Add("@singleToUser", NpgsqlDbType.Text).Value = action.SingleToUser ?? string.Empty;
        cmd.Parameters.Add("@singleToRoom", NpgsqlDbType.Text).Value = action.SingleToRoom ?? string.Empty;
        cmd.Parameters.Add("@userToUser", NpgsqlDbType.Text).Value = action.UserToUser ?? string.Empty;
        cmd.Parameters.Add("@userToOtherUser", NpgsqlDbType.Text).Value = action.UserToOtherUser ?? string.Empty;
        cmd.Parameters.Add("@userToRoom", NpgsqlDbType.Text).Value = action.UserToRoom ?? string.Empty;
        cmd.Parameters.Add("@monsterToUser", NpgsqlDbType.Text).Value = action.MonsterToUser ?? string.Empty;
        cmd.Parameters.Add("@monsterToRoom", NpgsqlDbType.Text).Value = action.MonsterToRoom ?? string.Empty;
        cmd.Parameters.Add("@inventoryToUser", NpgsqlDbType.Text).Value = action.InventoryToUser ?? string.Empty;
        cmd.Parameters.Add("@inventoryToRoom", NpgsqlDbType.Text).Value = action.InventoryToRoom ?? string.Empty;
        cmd.Parameters.Add("@floorItemToUser", NpgsqlDbType.Text).Value = action.FloorItemToUser ?? string.Empty;
        cmd.Parameters.Add("@floorItemToRoom", NpgsqlDbType.Text).Value = action.FloorItemToRoom ?? string.Empty;
    }

    private static void EnsureActionSchema(NpgsqlConnection conn)
    {
        using (var addColumn = conn.CreateCommand())
        {
            addColumn.CommandText = @"
                ALTER TABLE game_data.""Actions""
                ADD COLUMN IF NOT EXISTS ""DisplayOrder"" INTEGER NULL";
            addColumn.ExecuteNonQuery();
        }

        using (var index = conn.CreateCommand())
        {
            index.CommandText = @"
                CREATE INDEX IF NOT EXISTS idx_actions_display_order
                ON game_data.""Actions"" (""DisplayOrder"", ""Name"")";
            index.ExecuteNonQuery();
        }

        if (GetVisibleActionCount(conn) > 0)
            return;

        for (int index = 0; index < LegacyStockActionDisplayOrder.Length; index++)
        {
            using var backfill = conn.CreateCommand();
            backfill.CommandText = @"
                UPDATE game_data.""Actions""
                SET ""DisplayOrder"" = @displayOrder
                WHERE LOWER(""Name"") = LOWER(@name)
                  AND ""DisplayOrder"" IS NULL";
            backfill.Parameters.AddWithValue("@displayOrder", index + 1);
            backfill.Parameters.AddWithValue("@name", LegacyStockActionDisplayOrder[index]);
            backfill.ExecuteNonQuery();
        }
    }

    private static int GetVisibleActionCount(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM game_data.""Actions""
            WHERE ""DisplayOrder"" IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static int GetVisibleActionCount(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM game_data.""Actions""
            WHERE ""DisplayOrder"" IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    private static int GetNextActionDisplayOrder(NpgsqlConnection conn, NpgsqlTransaction tx)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT COALESCE(MAX(""DisplayOrder""), 0) + 1
            FROM game_data.""Actions""
            WHERE ""DisplayOrder"" IS NOT NULL";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 1);
    }

    private static int? GetActionDisplayOrder(NpgsqlConnection conn, NpgsqlTransaction tx, string normalizedName)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            SELECT ""DisplayOrder""
            FROM game_data.""Actions""
            WHERE LOWER(""Name"") = LOWER(@name)";
        cmd.Parameters.AddWithValue("@name", normalizedName);

        object? result = cmd.ExecuteScalar();
        if (result == null || result is DBNull)
            return null;

        return Convert.ToInt32(result);
    }

    private static int MoveActionDisplayOrder(NpgsqlConnection conn, NpgsqlTransaction tx, int? existingDisplayOrder, int requestedDisplayOrder)
    {
        int visibleCount = GetVisibleActionCount(conn, tx);
        int maxDisplayOrder = existingDisplayOrder.HasValue
            ? Math.Max(1, visibleCount)
            : visibleCount + 1;
        int desiredDisplayOrder = Math.Clamp(requestedDisplayOrder, 1, Math.Max(1, maxDisplayOrder));

        if (!existingDisplayOrder.HasValue)
        {
            using var shift = conn.CreateCommand();
            shift.Transaction = tx;
            shift.CommandText = @"
                UPDATE game_data.""Actions""
                SET ""DisplayOrder"" = ""DisplayOrder"" + 1
                WHERE ""DisplayOrder"" IS NOT NULL
                  AND ""DisplayOrder"" >= @displayOrder";
            shift.Parameters.AddWithValue("@displayOrder", desiredDisplayOrder);
            shift.ExecuteNonQuery();
            return desiredDisplayOrder;
        }

        if (existingDisplayOrder.Value == desiredDisplayOrder)
            return desiredDisplayOrder;

        using var reorder = conn.CreateCommand();
        reorder.Transaction = tx;

        if (desiredDisplayOrder < existingDisplayOrder.Value)
        {
            reorder.CommandText = @"
                UPDATE game_data.""Actions""
                SET ""DisplayOrder"" = ""DisplayOrder"" + 1
                WHERE ""DisplayOrder"" IS NOT NULL
                  AND ""DisplayOrder"" >= @desired
                  AND ""DisplayOrder"" < @existing";
        }
        else
        {
            reorder.CommandText = @"
                UPDATE game_data.""Actions""
                SET ""DisplayOrder"" = ""DisplayOrder"" - 1
                WHERE ""DisplayOrder"" IS NOT NULL
                  AND ""DisplayOrder"" > @existing
                  AND ""DisplayOrder"" <= @desired";
        }

        reorder.Parameters.AddWithValue("@desired", desiredDisplayOrder);
        reorder.Parameters.AddWithValue("@existing", existingDisplayOrder.Value);
        reorder.ExecuteNonQuery();
        return desiredDisplayOrder;
    }

    private static int? TryGetOrdinal(DbDataReader reader, string columnName)
    {
        for (int index = 0; index < reader.FieldCount; index++)
        {
            if (string.Equals(reader.GetName(index), columnName, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return null;
    }

    private static int? TryGetFirstOrdinal(DbDataReader reader, params string[] columnNames)
    {
        foreach (var columnName in columnNames)
        {
            var ordinal = TryGetOrdinal(reader, columnName);
            if (ordinal.HasValue)
                return ordinal;
        }

        return null;
    }

    private static int GetRequiredFirstOrdinal(DbDataReader reader, params string[] columnNames)
    {
        int? ordinal = TryGetFirstOrdinal(reader, columnNames);
        if (ordinal.HasValue)
            return ordinal.Value;

        throw new InvalidOperationException(
            $"game_data.\"Spells\" is missing required column(s): {string.Join(", ", columnNames)}. " +
            "Re-import game data with tools/dat-import/import_dat.py before starting the game.");
    }

    private static int ReadOptionalInt32(DbDataReader reader, int? ordinal)
    {
        return ordinal.HasValue && !reader.IsDBNull(ordinal.Value)
            ? reader.GetInt32(ordinal.Value)
            : 0;
    }

    private void LoadRaces(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Races\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var race = new Race
            {
                Number = reader.GetInt32(reader.GetOrdinal("Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                MinInt = reader.GetInt32(reader.GetOrdinal("mINT")),
                MinWil = reader.GetInt32(reader.GetOrdinal("mWIL")),
                MinStr = reader.GetInt32(reader.GetOrdinal("mSTR")),
                MinHea = reader.GetInt32(reader.GetOrdinal("mHEA")),
                MinAgl = reader.GetInt32(reader.GetOrdinal("mAGL")),
                MinChm = reader.GetInt32(reader.GetOrdinal("mCHM")),
                MaxInt = reader.GetInt32(reader.GetOrdinal("xINT")),
                MaxWil = reader.GetInt32(reader.GetOrdinal("xWIL")),
                MaxStr = reader.GetInt32(reader.GetOrdinal("xSTR")),
                MaxHea = reader.GetInt32(reader.GetOrdinal("xHEA")),
                MaxAgl = reader.GetInt32(reader.GetOrdinal("xAGL")),
                MaxChm = reader.GetInt32(reader.GetOrdinal("xCHM")),
                HPPerLvl = reader.GetInt32(reader.GetOrdinal("HPPerLVL")),
                ExpTable = reader.GetInt32(reader.GetOrdinal("ExpTable")),
                BaseCP = reader.GetInt32(reader.GetOrdinal("BaseCP")),
            };

            for (int i = 0; i < 10; i++)
            {
                int abil = reader.GetInt32(reader.GetOrdinal($"Abil-{i}"));
                int val = reader.GetInt32(reader.GetOrdinal($"AbilVal-{i}"));
                if (abil != 0) race.Abilities[abil] = val;
            }

            Races[race.Number] = race;
        }
    }

    private void LoadClasses(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Classes\"";
        using var reader = cmd.ExecuteReader();
        int? titleTextOrdinal = TryGetOrdinal(reader, "TitleText");
        while (reader.Read())
        {
            var cls = new CharacterClass
            {
                Number = reader.GetInt32(reader.GetOrdinal("Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                MinHits = reader.GetInt32(reader.GetOrdinal("MinHits")),
                MaxHits = reader.GetInt32(reader.GetOrdinal("MaxHits")),
                ExpTable = reader.GetInt32(reader.GetOrdinal("ExpTable")),
                MageryType = reader.GetInt32(reader.GetOrdinal("MageryType")),
                MageryLvl = reader.GetInt32(reader.GetOrdinal("MageryLVL")),
                WeaponType = reader.GetInt32(reader.GetOrdinal("WeaponType")),
                ArmourType = reader.GetInt32(reader.GetOrdinal("ArmourType")),
                CombatLvl = reader.GetInt32(reader.GetOrdinal("CombatLVL")),
                TitleText = titleTextOrdinal.HasValue && !reader.IsDBNull(titleTextOrdinal.Value)
                    ? reader.GetInt32(titleTextOrdinal.Value)
                    : 0,
            };

            for (int i = 0; i < 10; i++)
            {
                int abil = reader.GetInt32(reader.GetOrdinal($"Abil-{i}"));
                int val = reader.GetInt32(reader.GetOrdinal($"AbilVal-{i}"));
                if (abil != 0) cls.Abilities[abil] = val;
            }

            Classes[cls.Number] = cls;
        }
    }

    private void LoadClassTitles()
    {
        foreach (var cls in Classes.Values)
        {
            cls.LevelTitles.Clear();

            int titleTextId = cls.TitleText;
            if (titleTextId <= 0)
            {
                int inferredTitleTextId = 3000 + cls.Number;
                if (TextBlocks.ContainsKey(inferredTitleTextId))
                    titleTextId = inferredTitleTextId;
            }

            if (titleTextId <= 0 || !TextBlocks.TryGetValue(titleTextId, out var titleText))
                continue;

            cls.TitleText = titleTextId;
            cls.LevelTitles.AddRange(ParseClassTitles(titleText));
        }
    }

    private static IEnumerable<string> ParseClassTitles(string text)
    {
        return text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0);
    }

    private void LoadRooms(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Rooms\"";
        using var reader = cmd.ExecuteReader();
        int? attributesOrdinal = TryGetOrdinal(reader, "Attributes");
        int? roomTypeOrdinal = TryGetOrdinal(reader, "RoomType");
        int? gangHouseOrdinal = TryGetOrdinal(reader, "GangHouse");
        while (reader.Read())
        {
            var room = new Room
            {
                MapNumber = reader.GetInt32(reader.GetOrdinal("Map Number")),
                RoomNumber = reader.GetInt32(reader.GetOrdinal("Room Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Attributes = attributesOrdinal.HasValue && !reader.IsDBNull(attributesOrdinal.Value)
                    ? reader.GetInt32(attributesOrdinal.Value)
                    : 0,
                Light = reader.GetInt32(reader.GetOrdinal("Light")),
                Shop = reader.GetInt32(reader.GetOrdinal("Shop")),
                NPC = reader.GetInt32(reader.GetOrdinal("NPC")),
                CMD = reader.GetInt32(reader.GetOrdinal("CMD")),
                Spell = reader.GetInt32(reader.GetOrdinal("Spell")),
                RoomType = roomTypeOrdinal.HasValue && !reader.IsDBNull(roomTypeOrdinal.Value)
                    ? reader.GetInt32(roomTypeOrdinal.Value)
                    : 0,
                GangHouseId = gangHouseOrdinal.HasValue && !reader.IsDBNull(gangHouseOrdinal.Value)
                    ? (int)reader.GetInt64(gangHouseOrdinal.Value)
                    : 0,
                MaxRegen = reader.GetInt32(reader.GetOrdinal("MaxRegen")),
                ControlRoom = reader.IsDBNull(reader.GetOrdinal("ControlRoom")) ? 0 : reader.GetInt32(reader.GetOrdinal("ControlRoom")),
                MaxArea = reader.IsDBNull(reader.GetOrdinal("MaxArea")) ? 0 : reader.GetInt32(reader.GetOrdinal("MaxArea")),
                MonsterType = reader.GetInt32(reader.GetOrdinal("MonsterType")),
                MinIndex = reader.GetInt32(reader.GetOrdinal("MinIndex")),
                MaxIndex = reader.GetInt32(reader.GetOrdinal("MaxIndex")),
                // ByNumber added by EnsureRoomByNumberImported; TryGetOrdinal keeps pre-migration
                // snapshots loading (default 0 = use the group/index pick).
                ByNumber = ReadOptionalInt32(reader, TryGetOrdinal(reader, "ByNumber")),
                Delay = reader.GetInt32(reader.GetOrdinal("Delay")),
                Placed = reader.GetString(reader.GetOrdinal("Placed")),
                HiddenItems = reader.IsDBNull(reader.GetOrdinal("HiddenItems")) ? "" : reader.GetString(reader.GetOrdinal("HiddenItems")),
                GroundCurrency = reader.IsDBNull(reader.GetOrdinal("GroundCurrency")) ? 0 : reader.GetInt32(reader.GetOrdinal("GroundCurrency")),
                DeathRoom = reader.IsDBNull(reader.GetOrdinal("DeathRoom")) ? 0 : reader.GetInt32(reader.GetOrdinal("DeathRoom")),
                ExitRoom = reader.IsDBNull(reader.GetOrdinal("ExitRoom")) ? 0 : reader.GetInt32(reader.GetOrdinal("ExitRoom")),
                N = reader.GetString(reader.GetOrdinal("N")),
                S = reader.GetString(reader.GetOrdinal("S")),
                E = reader.GetString(reader.GetOrdinal("E")),
                W = reader.GetString(reader.GetOrdinal("W")),
                NE = reader.GetString(reader.GetOrdinal("NE")),
                NW = reader.GetString(reader.GetOrdinal("NW")),
                SE = reader.GetString(reader.GetOrdinal("SE")),
                SW = reader.GetString(reader.GetOrdinal("SW")),
                U = reader.GetString(reader.GetOrdinal("U")),
                D = reader.GetString(reader.GetOrdinal("D")),
            };

            Rooms[(room.MapNumber, room.RoomNumber)] = room;
        }
    }

    private void LoadRoomExits(NpgsqlConnection conn)
    {
        if (!TableExists(conn, "RoomExits"))
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT ""MapNumber"", ""RoomNumber"", ""DirectionIndex"", ""Direction"", ""TargetMap"", ""TargetRoom"", ""ExitType"", ""Para1"", ""Para2"", ""Para3"", ""Para4""
            FROM game_data.""RoomExits""
            ORDER BY ""MapNumber"", ""RoomNumber"", ""DirectionIndex""";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int map = reader.GetInt32(0);
            int roomNumber = reader.GetInt32(1);
            if (!Rooms.TryGetValue((map, roomNumber), out var room))
                continue;

            room.SetExit(new RoomExitDefinition
            {
                MapNumber = map,
                RoomNumber = roomNumber,
                DirectionIndex = reader.GetInt32(2),
                Direction = reader.GetString(3),
                TargetMap = reader.GetInt32(4),
                TargetRoom = reader.GetInt32(5),
                ExitType = (RoomExitType)reader.GetInt32(6),
                Para1 = reader.GetInt32(7),
                Para2 = reader.GetInt32(8),
                Para3 = reader.GetInt32(9),
                Para4 = reader.GetInt32(10),
            });
        }
    }

    private void LoadRoomDescriptions(NpgsqlConnection conn)
    {
        if (!TableExists(conn, "RoomDescriptions"))
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"MapNumber\", \"RoomNumber\", \"Description\" FROM game_data.\"RoomDescriptions\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            int map = reader.GetInt32(0);
            int room = reader.GetInt32(1);
            string desc = reader.GetString(2);
            RoomDescriptions[(map, room)] = desc;

            // Also set it on the Room object if loaded
            if (Rooms.TryGetValue((map, room), out var r))
                r.Description = desc;
        }
        if (!RuntimeConfiguration.IsQuietTestLoggingEnabled())
            Console.WriteLine($"Loaded {RoomDescriptions.Count} room descriptions.");
    }

    private void LoadMonsters(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Monsters\"";
        using var reader = cmd.ExecuteReader();
        var hitMessageOrdinals = Enumerable.Range(0, 5).Select(i => TryGetOrdinal(reader, $"AtkHitMsg-{i}")).ToArray();
        var dodgeMessageOrdinals = Enumerable.Range(0, 5).Select(i => TryGetOrdinal(reader, $"AtkDodgeMsg-{i}")).ToArray();
        var missMessageOrdinals = Enumerable.Range(0, 5).Select(i => TryGetOrdinal(reader, $"AtkMissMsg-{i}")).ToArray();
        // DeathMsg is an enrichment column (added by the bootstrapper); read it defensively so a DB
        // state without it still loads (falls back to 0 → generic death line), matching the message cols.
        int? deathMsgOrdinal = TryGetOrdinal(reader, "DeathMsg");
        int? moveMsgOrdinal = TryGetOrdinal(reader, "MoveMsg");
        int enrichedMonsters = 0;
        while (reader.Read())
        {
            var mob = new Monster
            {
                Number = reader.GetInt32(reader.GetOrdinal("Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description")),
                Gender = reader.IsDBNull(reader.GetOrdinal("Gender")) ? 0 : reader.GetInt32(reader.GetOrdinal("Gender")),
                Weapon = reader.GetInt32(reader.GetOrdinal("Weapon")),
                ArmourClass = reader.GetInt32(reader.GetOrdinal("ArmourClass")),
                DamageResist = reader.GetInt32(reader.GetOrdinal("DamageResist")),
                FollowPercent = reader.GetInt32(reader.GetOrdinal("FollowPercent")),
                Active = reader.GetInt32(reader.GetOrdinal("Active")),
                MagicRes = reader.GetInt32(reader.GetOrdinal("MagicRes")),
                BSDefense = reader.GetInt32(reader.GetOrdinal("BSDefense")),
                EXP = reader.GetDouble(reader.GetOrdinal("EXP")),
                ExpMulti = reader.GetDouble(reader.GetOrdinal("ExpMulti")),
                HP = reader.GetInt32(reader.GetOrdinal("HP")),
                Energy = reader.GetInt32(reader.GetOrdinal("Energy")),
                AvgDmg = reader.GetDouble(reader.GetOrdinal("AvgDmg")),
                GreetTXT = reader.GetInt32(reader.GetOrdinal("GreetTXT")),
                HPRegen = reader.GetInt32(reader.GetOrdinal("HPRegen")),
                CharmLVL = reader.GetInt32(reader.GetOrdinal("CharmLVL")),
                Type = reader.GetInt32(reader.GetOrdinal("Type")),
                Undead = reader.GetInt32(reader.GetOrdinal("Undead")) != 0,
                Align = reader.GetInt32(reader.GetOrdinal("Align")),
                RegenTime = reader.GetInt32(reader.GetOrdinal("RegenTime")),
                GameLimit = reader.GetInt32(reader.GetOrdinal("GameLimit")),
                Runic = reader.GetInt32(reader.GetOrdinal("Runic")),
                Platinum = reader.GetInt32(reader.GetOrdinal("Platinum")),
                Gold = reader.GetInt32(reader.GetOrdinal("Gold")),
                Silver = reader.GetInt32(reader.GetOrdinal("Silver")),
                Copper = reader.GetInt32(reader.GetOrdinal("Copper")),
                DeathSpell = reader.GetInt32(reader.GetOrdinal("DeathSpell")),
                CreateSpell = reader.GetInt32(reader.GetOrdinal("CreateSpell")),
                DeathMsg = ReadOptionalInt32(reader, deathMsgOrdinal),
                MoveMsg = ReadOptionalInt32(reader, moveMsgOrdinal),
                Group = reader.GetInt32(reader.GetOrdinal("Group")),
                GroupIndex = reader.GetInt32(reader.GetOrdinal("GroupIndex")),
                GroupMatch = ReadOptionalInt32(reader, reader.GetOrdinal("GroupMatch")),
                MaxFollowers = ReadOptionalInt32(reader, reader.GetOrdinal("MaxFollowers")),
                DescTxt = reader.GetInt32(reader.GetOrdinal("DescTxt")),
            };

            // Load attacks
            for (int i = 0; i < 5; i++)
            {
                int atkType = reader.GetInt32(reader.GetOrdinal($"AtkType-{i}"));
                int min = reader.GetInt32(reader.GetOrdinal($"AtkMin-{i}"));
                int max = reader.GetInt32(reader.GetOrdinal($"AtkMax-{i}"));
                if (atkType > 0 || min > 0 || max > 0)
                {
                    mob.Attacks.Add(new MonsterAttack
                    {
                        SlotIndex = i,
                        Type = atkType,
                        Accuracy = reader.GetInt32(reader.GetOrdinal($"AtkAcc-{i}")),
                        Percent = reader.GetInt32(reader.GetOrdinal($"AtkPer-{i}")),
                        Min = min,
                        Max = max,
                        HitMessageId = ReadOptionalInt32(reader, hitMessageOrdinals[i]),
                        DodgeMessageId = ReadOptionalInt32(reader, dodgeMessageOrdinals[i]),
                        MissMessageId = ReadOptionalInt32(reader, missMessageOrdinals[i]),
                        Energy = reader.GetInt32(reader.GetOrdinal($"AtkEng-{i}")),
                        HitSpell = reader.GetInt32(reader.GetOrdinal($"AtkHitSpell-{i}")),
                    });
                }
            }

            // Load mid-combat spells
            for (int i = 0; i < 5; i++)
            {
                int spellId = reader.GetInt32(reader.GetOrdinal($"MidSpell-{i}"));
                int pct = reader.GetInt32(reader.GetOrdinal($"MidSpellPer-{i}"));
                if (spellId > 0)
                {
                    mob.MidSpells.Add(new MonsterSpell
                    {
                        SpellId = spellId,
                        Percent = pct,
                        Level = reader.GetInt32(reader.GetOrdinal($"MidSpellLvl-{i}")),
                    });
                }
            }

            // Load drops
            for (int i = 0; i < 10; i++)
            {
                int itemId = reader.GetInt32(reader.GetOrdinal($"DropItem-{i}"));
                int pct = reader.GetInt32(reader.GetOrdinal($"DropPer-{i}"));
                int uses = reader.GetInt32(reader.GetOrdinal($"DropUses-{i}"));
                if (itemId > 0)
                {
                    mob.Drops.Add(new MonsterDrop { ItemId = itemId, Percent = pct, Uses = uses });
                }
            }

            // Load abilities
            for (int i = 0; i < 10; i++)
            {
                int abil = reader.GetInt32(reader.GetOrdinal($"Abil-{i}"));
                int val = reader.GetInt32(reader.GetOrdinal($"AbilVal-{i}"));
                if (abil != 0) mob.Abilities[abil] = val;
            }

            if (mob.Attacks.Any(attack => attack.HitMessageId != 0 || attack.DodgeMessageId != 0 || attack.MissMessageId != 0))
                enrichedMonsters++;

            Monsters[mob.Number] = mob;
        }

        if (!RuntimeConfiguration.IsQuietTestLoggingEnabled())
            Console.WriteLine($"  Loaded monster attack message ids for {enrichedMonsters}/{Monsters.Count} monsters from PostgreSQL");
    }

    private void LoadItems(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Items\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var item = new Item
            {
                Number = reader.GetInt32(reader.GetOrdinal("Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description")),
                Limit = reader.GetInt32(reader.GetOrdinal("Limit")),
                Encum = reader.GetInt32(reader.GetOrdinal("Encum")),
                ItemType = reader.GetInt32(reader.GetOrdinal("ItemType")),
                UseCount = reader.GetInt32(reader.GetOrdinal("UseCount")),
                // ReadTextBlock added by EnsureItemsExtrasImported; TryGetOrdinal keeps older
                // snapshots loading (defaults 0 = not readable).
                ReadTextBlock = TryGetOrdinal(reader, "ReadTextBlock") is int readTbOrdinal && !reader.IsDBNull(readTbOrdinal)
                    ? reader.GetInt32(readTbOrdinal)
                    : 0,
                Price = reader.GetInt32(reader.GetOrdinal("Price")),
                Currency = reader.GetInt32(reader.GetOrdinal("Currency")),
                Min = reader.GetInt32(reader.GetOrdinal("Min")),
                Max = reader.GetInt32(reader.GetOrdinal("Max")),
                ArmourClass = reader.GetInt32(reader.GetOrdinal("ArmourClass")),
                DamageResist = reader.GetInt32(reader.GetOrdinal("DamageResist")),
                WeaponType = reader.GetInt32(reader.GetOrdinal("WeaponType")),
                ArmourType = reader.GetInt32(reader.GetOrdinal("ArmourType")),
                Worn = reader.GetInt32(reader.GetOrdinal("Worn")),
                Accy = reader.GetInt32(reader.GetOrdinal("Accy")),
                Gettable = reader.GetInt32(reader.GetOrdinal("Gettable")) != 0,
                StrReq = reader.GetInt32(reader.GetOrdinal("StrReq")),
                Speed = reader.GetInt32(reader.GetOrdinal("Speed")),
                NotDroppable = reader.GetInt32(reader.GetOrdinal("NotDroppable")) != 0,
                DestroyOnDeath = reader.GetInt32(reader.GetOrdinal("DestroyOnDeath")) != 0,
                RetainAfterUses = reader.GetInt32(reader.GetOrdinal("RetainAfterUses")) != 0,
                // The stealable flag (EnsureItemsExtrasImported adds the "Robable" column on
                // first start; TryGetOrdinal keeps older snapshots loading cleanly → defaults false).
                CanBeRobbed = TryGetOrdinal(reader, "Robable") is int robableOrdinal && !reader.IsDBNull(robableOrdinal)
                    && reader.GetInt32(robableOrdinal) != 0,
                HitMsg = reader.IsDBNull(reader.GetOrdinal("HitMsg")) ? 0 : reader.GetInt32(reader.GetOrdinal("HitMsg")),
                MissMsg = reader.IsDBNull(reader.GetOrdinal("MissMsg")) ? 0 : reader.GetInt32(reader.GetOrdinal("MissMsg")),
                // DestructMsg column may be absent in older seed snapshots (the column is added
                // when the converter is re-run with the destruction-message export enabled).
                // TryGetOrdinal returns null when the column is missing; default to 0 in that case.
                DestructMsg = TryGetOrdinal(reader, "DestructMsg") is int destructOrdinal && !reader.IsDBNull(destructOrdinal)
                    ? reader.GetInt32(destructOrdinal)
                    : 0,
                InGame = reader.GetInt32(reader.GetOrdinal("InGame")) != 0,
                // OpenRunic..OpenCopper added by EnsureItemsOpenCoinsImported (the OPEN
                // "coins on open" maxes). TryGetOrdinal keeps pre-migration snapshots loading
                // (defaults 0 = no coins rolled on open).
                OpenRunic = ReadOptionalInt32(reader, TryGetOrdinal(reader, "OpenRunic")),
                OpenPlatinum = ReadOptionalInt32(reader, TryGetOrdinal(reader, "OpenPlatinum")),
                OpenGold = ReadOptionalInt32(reader, TryGetOrdinal(reader, "OpenGold")),
                OpenSilver = ReadOptionalInt32(reader, TryGetOrdinal(reader, "OpenSilver")),
                OpenCopper = ReadOptionalInt32(reader, TryGetOrdinal(reader, "OpenCopper")),
            };

            for (int i = 0; i < 10; i++)
            {
                item.ClassRestrictions[i] = reader.GetInt32(reader.GetOrdinal($"ClassRest-{i}"));
                item.RaceRestrictions[i] = reader.GetInt32(reader.GetOrdinal($"RaceRest-{i}"));
            }

            // NegateSpell-0..9 columns may be absent on older seed snapshots — the
            // EnsureItemsNegateSpellsImported migration adds them on first start, but if the
            // server is started before the migration we still want to load the rest of the row
            // cleanly. Skip silently when the columns don't yet exist; the empty set falls
            // through as "negates nothing" at the cast site.
            if (TryGetOrdinal(reader, "NegateSpell-0") is int)
            {
                for (int i = 0; i < 10; i++)
                {
                    int slot = reader.GetInt32(reader.GetOrdinal($"NegateSpell-{i}"));
                    if (slot > 0)
                        item.NegatedSpellNumbers.Add(slot);
                }
            }

            bool seenAbility43 = false;
            for (int i = 0; i < 10; i++)
            {
                int abil = reader.GetInt32(reader.GetOrdinal($"Abil-{i}"));
                int val = reader.GetInt32(reader.GetOrdinal($"AbilVal-{i}"));
                if (abil == 0) continue;
                item.Abilities[abil] = val;
                item.AbilitySlots.Add(new KeyValuePair<int, int>(abil, val));
                // Ability 59 (ClassOk) is the one ability that legitimately appears in multiple
                // slots with different values (each value naming an additional permitted class id);
                // the Abilities dict collapses duplicates so we shadow it into a list here.
                if (abil == 59 && val > 0)
                    item.ClassOkClassIds.Add(val);
                // Ability 43 can appear twice: first = manual USE spell, second = weapon PROC spell.
                if (abil == 43 && val > 0)
                {
                    if (!seenAbility43)
                    {
                        item.UseSpellId = val;
                        seenAbility43 = true;
                    }
                    else
                    {
                        item.ProcSpellId = val;
                    }
                }
            }
            // If only one ability-43 slot exists, determine its role from context:
            // weapons with ability 114 (auto-use chance) use it as the proc spell.
            if (item.UseSpellId > 0 && item.ProcSpellId == 0 && item.Abilities.ContainsKey(114))
            {
                item.ProcSpellId = item.UseSpellId;
                item.UseSpellId = 0;
            }

            Items[item.Number] = item;
        }
    }

    private void LoadSpells(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Spells\"";
        using var reader = cmd.ExecuteReader();
        int spellTypeOrdinal = GetRequiredFirstOrdinal(reader, "SpellType");
        int castMessageAOrdinal = GetRequiredFirstOrdinal(reader, "CastMessageA", "CastMsgA");
        int castMessageBOrdinal = GetRequiredFirstOrdinal(reader, "CastMessageB", "CastMsgB");
        int messageStyleOrdinal = GetRequiredFirstOrdinal(reader, "MessageStyle", "MsgStyle");
        int resistAbilityOrdinal = GetRequiredFirstOrdinal(reader, "ResistAbility", "ResistAbil");
        int? durRandOrdinal = TryGetOrdinal(reader, "DurRand");
        while (reader.Read())
        {
            var spell = new GameSpell
            {
                Number = reader.GetInt32(reader.GetOrdinal("Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                Short = reader.GetString(reader.GetOrdinal("Short")),
                ReqLevel = reader.GetInt32(reader.GetOrdinal("ReqLevel")),
                EnergyCost = reader.GetInt32(reader.GetOrdinal("EnergyCost")),
                ManaCost = reader.GetInt32(reader.GetOrdinal("ManaCost")),
                MinBase = reader.GetInt32(reader.GetOrdinal("MinBase")),
                MaxBase = reader.GetInt32(reader.GetOrdinal("MaxBase")),
                Diff = reader.GetInt32(reader.GetOrdinal("Diff")),
                TypeOfResists = reader.GetInt32(reader.GetOrdinal("TypeOfResists")),
                Targets = reader.GetInt32(reader.GetOrdinal("Targets")),
                Duration = reader.GetInt32(reader.GetOrdinal("Duration")),
                AttType = reader.GetInt32(reader.GetOrdinal("AttType")),
                Magery = reader.GetInt32(reader.GetOrdinal("Magery")),
                MageryLvl = reader.GetInt32(reader.GetOrdinal("MageryLvl")),
                Cap = reader.GetInt32(reader.GetOrdinal("Cap")),
                MaxIncLvls = reader.GetInt32(reader.GetOrdinal("MaxIncLvls")),
                MaxInc = reader.GetInt32(reader.GetOrdinal("MaxInc")),
                MinIncLvls = reader.GetInt32(reader.GetOrdinal("MinIncLvls")),
                MinInc = reader.GetInt32(reader.GetOrdinal("MinInc")),
                DurIncLvls = reader.GetInt32(reader.GetOrdinal("DurIncLvls")),
                DurInc = reader.GetInt32(reader.GetOrdinal("DurInc")),
                DurRand = ReadOptionalInt32(reader, durRandOrdinal),
                Learnable = reader.GetInt32(reader.GetOrdinal("Learnable")) != 0,
                SpellType = ReadOptionalInt32(reader, spellTypeOrdinal),
                CastMessageA = ReadOptionalInt32(reader, castMessageAOrdinal),
                CastMessageB = ReadOptionalInt32(reader, castMessageBOrdinal),
                MessageStyle = ReadOptionalInt32(reader, messageStyleOrdinal),
                ResistAbility = ReadOptionalInt32(reader, resistAbilityOrdinal),
                Description = reader.IsDBNull(reader.GetOrdinal("Description")) ? "" : reader.GetString(reader.GetOrdinal("Description")),
            };

            for (int i = 0; i < 10; i++)
            {
                int abil = reader.GetInt32(reader.GetOrdinal($"Abil-{i}"));
                int val = reader.GetInt32(reader.GetOrdinal($"AbilVal-{i}"));
                if (abil != 0)
                {
                    spell.Abilities[abil] = val;
                    spell.AbilitySlots.Add(new KeyValuePair<int, int>(abil, val));
                }
            }

            Spells[spell.Number] = spell;
        }
    }

    private void LoadShops(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM game_data.\"Shops\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var shop = new Shop
            {
                Number = reader.GetInt32(reader.GetOrdinal("Number")),
                Name = reader.GetString(reader.GetOrdinal("Name")),
                ShopType = reader.GetInt32(reader.GetOrdinal("ShopType")),
                MinLvl = reader.GetInt32(reader.GetOrdinal("MinLvl")),
                MaxLvl = reader.GetInt32(reader.GetOrdinal("MaxLvl")),
                MarkupPercent = reader.GetInt32(reader.GetOrdinal("MarkupPercent")),
                ClassRest = reader.GetInt32(reader.GetOrdinal("ClassRest")),
            };

            for (int i = 0; i < 20; i++)
            {
                int itemId = reader.GetInt32(reader.GetOrdinal($"Item-{i}"));
                if (itemId > 0)
                {
                    int max = reader.GetInt32(reader.GetOrdinal($"ItemMax-{i}"));
                    shop.Items.Add(new ShopItem
                    {
                        ItemId = itemId,
                        Max = max,
                        Time = reader.GetInt32(reader.GetOrdinal($"ItemTime-{i}")),
                        Amount = reader.GetInt32(reader.GetOrdinal($"ItemAmt-{i}")),
                        Percent = reader.GetInt32(reader.GetOrdinal($"ItemPer-{i}")),
                        Current = max,   // shops boot full (no current-qty in static data)
                    });
                }
            }

            Shops[shop.Number] = shop;
        }
    }

    private void LoadMessages(NpgsqlConnection conn)
    {
        if (!TableExists(conn, "Messages"))
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"Number\", \"Line1\", \"Line2\", \"Line3\" FROM game_data.\"Messages\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var message = new RoomMessage
            {
                Number = reader.GetInt32(0),
                Line1 = reader.IsDBNull(1) ? "" : reader.GetString(1),
                Line2 = reader.IsDBNull(2) ? "" : reader.GetString(2),
                Line3 = reader.IsDBNull(3) ? "" : reader.GetString(3),
            };

            Messages[message.Number] = message;
        }
    }

    private void LoadTextBlocks(NpgsqlConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT \"Number\", \"Text\" FROM game_data.\"TextBlocks\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var number = reader.GetInt32(0);
            var text = reader.GetString(1);
            TextBlocks[number] = text;
        }
    }

    private void LoadTextBlockLinks(NpgsqlConnection conn)
    {
        TextBlockLinks.Clear();

        if (TableExists(conn, "TextBlockLinks"))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT \"Number\", \"LinkTo\" FROM game_data.\"TextBlockLinks\"";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var number = reader.GetInt32(0);
                var linkTo = reader.GetInt32(1);
                if (linkTo > 0)
                    TextBlockLinks[number] = linkTo;
            }

            return;
        }

        if (!TableExists(conn, "TBInfo"))
            return;

        using var fallbackCmd = conn.CreateCommand();
        fallbackCmd.CommandText = "SELECT \"Number\", \"LinkTo\" FROM game_data.\"TBInfo\"";
        using var fallbackReader = fallbackCmd.ExecuteReader();
        while (fallbackReader.Read())
        {
            var number = fallbackReader.GetInt32(0);
            var linkTo = fallbackReader.GetInt32(1);
            if (linkTo > 0)
                TextBlockLinks[number] = linkTo;
        }
    }

    private void LoadHelpTopics()
    {
        var helpPath = _helpTopicsPath;
        if (string.IsNullOrWhiteSpace(helpPath) || !File.Exists(helpPath))
        {
            if (!RuntimeConfiguration.IsQuietTestLoggingEnabled())
                Console.WriteLine("Warning: help_topics.json not found, help system will be limited.");
            return;
        }

        var json = File.ReadAllText(helpPath);
        var topics = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        if (topics != null)
        {
            foreach (var (key, value) in topics)
                HelpTopics[key] = value;
        }
    }

    private void LoadActions(NpgsqlConnection conn)
    {
        if (!TableExists(conn, "Actions"))
            return;

        EnsureActionSchema(conn);

        var actions = new Dictionary<string, SocialAction>(StringComparer.OrdinalIgnoreCase);
        var orderedActions = new List<SocialAction>();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                ""Name"",
                ""DisplayOrder"",
                ""SingleToUser"",
                ""SingleToRoom"",
                ""UserToUser"",
                ""UserToOtherUser"",
                ""UserToRoom"",
                ""MonsterToUser"",
                ""MonsterToRoom"",
                ""InventoryToUser"",
                ""InventoryToRoom"",
                ""FloorItemToUser"",
                ""FloorItemToRoom""
            FROM game_data.""Actions""
            ORDER BY ""DisplayOrder"" NULLS LAST, ""Name""";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var action = new SocialAction
            {
                Name = reader.GetString(reader.GetOrdinal("Name")),
                DisplayOrder = reader.IsDBNull(reader.GetOrdinal("DisplayOrder"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("DisplayOrder")),
                SingleToUser = reader.GetString(reader.GetOrdinal("SingleToUser")),
                SingleToRoom = reader.GetString(reader.GetOrdinal("SingleToRoom")),
                UserToUser = reader.GetString(reader.GetOrdinal("UserToUser")),
                UserToOtherUser = reader.GetString(reader.GetOrdinal("UserToOtherUser")),
                UserToRoom = reader.GetString(reader.GetOrdinal("UserToRoom")),
                MonsterToUser = reader.GetString(reader.GetOrdinal("MonsterToUser")),
                MonsterToRoom = reader.GetString(reader.GetOrdinal("MonsterToRoom")),
                InventoryToUser = reader.GetString(reader.GetOrdinal("InventoryToUser")),
                InventoryToRoom = reader.GetString(reader.GetOrdinal("InventoryToRoom")),
                FloorItemToUser = reader.GetString(reader.GetOrdinal("FloorItemToUser")),
                FloorItemToRoom = reader.GetString(reader.GetOrdinal("FloorItemToRoom")),
            };

            if (string.IsNullOrWhiteSpace(action.Name))
                continue;

            orderedActions.Add(action);
            actions[action.Name] = action;
        }

        _orderedActions = orderedActions;
        _actions = actions;
    }

}
