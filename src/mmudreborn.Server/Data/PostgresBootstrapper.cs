using NpgsqlTypes;
using Npgsql;

namespace mmudreborn.Data;

public sealed partial class PostgresBootstrapper
{
    private const string GameDataSchema = "game_data";
    private const string GameDataSourceSignatureKey = "game_data_source_signature";
    private const int MonsterAttackSlotCount = 5;

    private readonly string _connectionString;

    public PostgresBootstrapper(string connectionString)
    {
        _connectionString = connectionString;
    }

    public void EnsureReady()
    {
        EnsureDatabaseExists();

        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();

        using (var extension = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS citext", conn))
            extension.ExecuteNonQuery();

        EnsurePlayerSchema(conn);
        EnsureGameDataPresent(conn);
        EnsureBlankMessageSentinelsPresent(conn);
        EnsureRoomAttributesImported(conn);
        EnsureRoomTypeImported(conn);
        EnsureMonsterAttackMessageMetadataImported(conn);
        EnsureItemsDestructMsgImported(conn);
        EnsureItemsExtrasImported(conn);
        EnsureItemsOpenCoinsImported(conn);
        EnsureItemsNegateSpellsImported(conn);
        EnsureMonstersExtrasImported(conn);
        EnsureSpellsExtrasImported(conn);
        EnsureSpellsDurRandImported(conn);
        EnsureMonsterGroupFormationImported(conn);
        EnsureShopsExtrasImported(conn);
        EnsureRoomMaxAreaImported(conn);
        EnsureRoomByNumberImported(conn);
    }

    private void EnsureDatabaseExists()
    {
        PostgresDatabaseProvisioner.EnsureDatabaseExists(_connectionString);
    }

    private static void EnsurePlayerSchema(NpgsqlConnection conn)
    {
        const string playersSql = @"
            CREATE TABLE IF NOT EXISTS Players (
                Name CITEXT PRIMARY KEY,
                PasswordHash TEXT NOT NULL,
                RaceId INTEGER NOT NULL,
                ClassId INTEGER NOT NULL,
                Level INTEGER NOT NULL DEFAULT 1,
                Experience BIGINT NOT NULL DEFAULT 0,
                CurrentHP INTEGER NOT NULL,
                MaxHP INTEGER NOT NULL,
                CurrentMana INTEGER NOT NULL DEFAULT 0,
                MaxMana INTEGER NOT NULL DEFAULT 0,
                SpellCasting INTEGER NOT NULL DEFAULT 0,
                Strength INTEGER NOT NULL,
                Agility INTEGER NOT NULL,
                Intellect INTEGER NOT NULL,
                Willpower INTEGER NOT NULL,
                Health INTEGER NOT NULL,
                Charm INTEGER NOT NULL,
                ArmourClass INTEGER NOT NULL DEFAULT 0,
                DamageResist INTEGER NOT NULL DEFAULT 0,
                MagicResist INTEGER NOT NULL DEFAULT 0,
                CurrentMap INTEGER NOT NULL DEFAULT 1,
                CurrentRoom INTEGER NOT NULL DEFAULT 1,
                Runic INTEGER NOT NULL DEFAULT 0,
                Platinum INTEGER NOT NULL DEFAULT 0,
                Gold INTEGER NOT NULL DEFAULT 0,
                Silver INTEGER NOT NULL DEFAULT 0,
                Copper INTEGER NOT NULL DEFAULT 100,
                CharacterPoints INTEGER NOT NULL DEFAULT 0,
                SpentCP INTEGER NOT NULL DEFAULT 0,
                Alignment INTEGER NOT NULL DEFAULT 0,
                Lives INTEGER NOT NULL DEFAULT 9,
                Thievery INTEGER NOT NULL DEFAULT 0,
                Traps INTEGER NOT NULL DEFAULT 0,
                Picklocks INTEGER NOT NULL DEFAULT 0,
                Tracking INTEGER NOT NULL DEFAULT 0,
                MartialArts INTEGER NOT NULL DEFAULT 0,
                BaseStrength INTEGER NOT NULL DEFAULT 0,
                BaseAgility INTEGER NOT NULL DEFAULT 0,
                BaseIntellect INTEGER NOT NULL DEFAULT 0,
                BaseWillpower INTEGER NOT NULL DEFAULT 0,
                BaseHealth INTEGER NOT NULL DEFAULT 0,
                BaseCharm INTEGER NOT NULL DEFAULT 0,
                Gender INTEGER NOT NULL DEFAULT 0,
                HairLength INTEGER NOT NULL DEFAULT 0,
                HairColour INTEGER NOT NULL DEFAULT 0,
                EyeColour INTEGER NOT NULL DEFAULT 0,
                LastName TEXT NOT NULL DEFAULT '',
                BbsUserId CITEXT NOT NULL DEFAULT '',
                Gang CITEXT NOT NULL DEFAULT '',
                GangExperience BIGINT NOT NULL DEFAULT 0,
                GangLieutenant INTEGER NOT NULL DEFAULT 0,
                GangViewOnlineOnly INTEGER NOT NULL DEFAULT 0,
                IsSysop INTEGER NOT NULL DEFAULT 0,
                IsTestAccount INTEGER NOT NULL DEFAULT 0,
                ToptenDisabled INTEGER NOT NULL DEFAULT 0,
                TalkMode INTEGER NOT NULL DEFAULT 0,
                BroadcastChannel INTEGER NOT NULL DEFAULT 0,
                BriefMode INTEGER NOT NULL DEFAULT 0,
                PaletteId INTEGER NOT NULL DEFAULT 0,
                StatlineMode INTEGER NOT NULL DEFAULT 1,
                CustomStatline TEXT NOT NULL DEFAULT '',
                StyleTechnical INTEGER NOT NULL DEFAULT 0,
                ReceiveGossipEnabled INTEGER NOT NULL DEFAULT 1,
                ReceiveAuctionEnabled INTEGER NOT NULL DEFAULT 1,
                WarnOnEvilEnabled INTEGER NOT NULL DEFAULT 0,
                MinEvilPoints REAL NOT NULL DEFAULT -220,
                ReceiveItemsEnabled INTEGER NOT NULL DEFAULT 1,
                ActionsEnabled INTEGER NOT NULL DEFAULT 1,
                IgnoredPlayers TEXT NOT NULL DEFAULT '[]',
                SuicidePassword TEXT NOT NULL DEFAULT '',
                KeepMode INTEGER NOT NULL DEFAULT 1,
                QuestAbilities TEXT NOT NULL DEFAULT '{}',
                BankBalances TEXT NOT NULL DEFAULT '{}',
                Inventory TEXT NOT NULL DEFAULT '[]',
                Equipment TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT
            )";

        const string gangSettingsSql = @"
            CREATE TABLE IF NOT EXISTS GangSettings (
                Name CITEXT PRIMARY KEY,
                ToptenDisabled INTEGER NOT NULL DEFAULT 0,
                MaxSize INTEGER NOT NULL DEFAULT 9,
                LeaderName CITEXT NOT NULL DEFAULT ''
            )";

        const string gangInvitesSql = @"
            CREATE TABLE IF NOT EXISTS GangInvites (
                InviteeName CITEXT NOT NULL,
                GangName CITEXT NOT NULL,
                InviterName CITEXT NOT NULL,
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT,
                PRIMARY KEY (InviteeName, GangName)
            )";

        const string serverSettingsSql = @"
            CREATE TABLE IF NOT EXISTS ServerSettings (
                Key CITEXT PRIMARY KEY,
                Value TEXT NOT NULL
            )";

        const string bugReportsSql = @"
            CREATE TABLE IF NOT EXISTS BugReports (
                Id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                ReporterBbsUserName CITEXT NOT NULL DEFAULT '',
                ReporterPlayerName CITEXT NOT NULL DEFAULT '',
                Title TEXT NOT NULL DEFAULT '',
                BriefDescription TEXT NOT NULL DEFAULT '',
                Description TEXT NOT NULL DEFAULT '',
                LocationText TEXT NOT NULL DEFAULT '',
                ItemName TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT,
                Status INTEGER NOT NULL DEFAULT 0,
                IsCompleted INTEGER NOT NULL DEFAULT 0,
                CompletedAt TEXT NOT NULL DEFAULT '',
                CompletedByBbsUserName CITEXT NOT NULL DEFAULT '',
                ResolutionNote TEXT NOT NULL DEFAULT '',
                ReporterReviewedAt TEXT NOT NULL DEFAULT '',
                VerifiedAt TEXT NOT NULL DEFAULT '',
                VerifiedByBbsUserName CITEXT NOT NULL DEFAULT '',
                VerifiedByPlayerName CITEXT NOT NULL DEFAULT ''
            )";

        const string hallOfFameSql = @"
            CREATE TABLE IF NOT EXISTS HallOfFame (
                Id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                PlayerName CITEXT NOT NULL DEFAULT '',
                LastName TEXT NOT NULL DEFAULT '',
                Level INTEGER NOT NULL DEFAULT 1,
                RaceId INTEGER NOT NULL DEFAULT 0,
                ClassId INTEGER NOT NULL DEFAULT 0,
                Experience BIGINT NOT NULL DEFAULT 0,
                Alignment INTEGER NOT NULL DEFAULT 0,
                KillerName TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT
            )";

        // Gang-house ownership: one row per OWNED house (1..10). Absent row = unowned/available.
        const string gangHousesSql = @"
            CREATE TABLE IF NOT EXISTS GangHouses (
                HouseId INTEGER PRIMARY KEY,
                OwnerGang CITEXT NOT NULL DEFAULT '',
                OwnerPlayer CITEXT NOT NULL DEFAULT '',
                PurchasedAt TEXT NOT NULL DEFAULT '',
                LastTaxAt TEXT NOT NULL DEFAULT ''
            )";

        // Gang-shop state: one row per gang-owned shop that has been stocked or had its markup set.
        // Slots is a compact "itemId:qty:price:currency" list (';'-joined) — stock saves the whole
        // shop struct, so a single upsert per shop mirrors that without a per-slot table.
        const string gangShopsSql = @"
            CREATE TABLE IF NOT EXISTS GangShops (
                ShopId INTEGER PRIMARY KEY,
                MarkupPercent INTEGER NOT NULL DEFAULT 0,
                Slots TEXT NOT NULL DEFAULT ''
            )";

        const string roomGroundItemsSql = @"
            CREATE TABLE IF NOT EXISTS RoomGroundItems (
                MapNumber INTEGER NOT NULL,
                RoomNumber INTEGER NOT NULL,
                Sequence INTEGER NOT NULL,
                ItemId INTEGER NOT NULL,
                IsHidden INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (MapNumber, RoomNumber, Sequence)
            )";

        const string roomGroundCurrencySql = @"
            CREATE TABLE IF NOT EXISTS RoomGroundCurrency (
                MapNumber INTEGER NOT NULL,
                RoomNumber INTEGER NOT NULL,
                VisibleCopper BIGINT NOT NULL DEFAULT 0,
                HiddenCopper BIGINT NOT NULL DEFAULT 0,
                VisibleStacksJson TEXT NOT NULL DEFAULT '',
                HiddenStacksJson TEXT NOT NULL DEFAULT '',
                StaticInitialized INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (MapNumber, RoomNumber)
            )";

        using (var cmd = new NpgsqlCommand(playersSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(gangSettingsSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(gangInvitesSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(serverSettingsSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(bugReportsSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(hallOfFameSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(roomGroundItemsSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(roomGroundCurrencySql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(gangHousesSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(gangShopsSql, conn))
            cmd.ExecuteNonQuery();

        const string pendingRerollsSql = @"
            CREATE TABLE IF NOT EXISTS PendingRerolls (
                BbsUserName CITEXT PRIMARY KEY,
                PreservedName CITEXT NOT NULL DEFAULT '',
                KeptExperience BIGINT NOT NULL DEFAULT 0,
                PreservedIsSysop INTEGER NOT NULL DEFAULT 0,
                PreservedToptenDisabled INTEGER NOT NULL DEFAULT 0,
                PreservedSuicidePassword TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP::TEXT
            )";
        using (var cmd = new NpgsqlCommand(pendingRerollsSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE PendingRerolls ADD COLUMN IF NOT EXISTS PreservedName CITEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();

        // Realm broadcast log (gossip/auction) — an append-only, size-capped scroll persisted for
        // the web explorer. Broadcast delivery is unchanged/in-memory; this is a best-effort record.
        const string gossipLogSql = @"
            CREATE TABLE IF NOT EXISTS GossipLog (
                Id BIGSERIAL PRIMARY KEY,
                Sender TEXT NOT NULL,
                Channel TEXT NOT NULL DEFAULT 'gossip',
                Message TEXT NOT NULL,
                CreatedAt TIMESTAMPTZ NOT NULL DEFAULT now()
            )";
        using (var cmd = new NpgsqlCommand(gossipLogSql, conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_gossiplog_id_desc ON GossipLog (Id DESC)", conn))
            cmd.ExecuteNonQuery();

        var playerColumns = new (string Name, string Definition)[]
        {
            ("Lives", "INTEGER NOT NULL DEFAULT 9"),
            ("Thievery", "INTEGER NOT NULL DEFAULT 0"),
            ("Traps", "INTEGER NOT NULL DEFAULT 0"),
            ("Picklocks", "INTEGER NOT NULL DEFAULT 0"),
            ("Tracking", "INTEGER NOT NULL DEFAULT 0"),
            ("MartialArts", "INTEGER NOT NULL DEFAULT 0"),
            ("SpellCasting", "INTEGER NOT NULL DEFAULT 0"),
            ("BaseStrength", "INTEGER NOT NULL DEFAULT 0"),
            ("BaseAgility", "INTEGER NOT NULL DEFAULT 0"),
            ("BaseIntellect", "INTEGER NOT NULL DEFAULT 0"),
            ("BaseWillpower", "INTEGER NOT NULL DEFAULT 0"),
            ("BaseHealth", "INTEGER NOT NULL DEFAULT 0"),
            ("BaseCharm", "INTEGER NOT NULL DEFAULT 0"),
            ("Gender", "INTEGER NOT NULL DEFAULT 0"),
            ("HairLength", "INTEGER NOT NULL DEFAULT 0"),
            ("HairColour", "INTEGER NOT NULL DEFAULT 0"),
            ("EyeColour", "INTEGER NOT NULL DEFAULT 0"),
            ("LastName", "TEXT NOT NULL DEFAULT ''"),
            ("BbsUserId", "CITEXT NOT NULL DEFAULT ''"),
            ("Gang", "CITEXT NOT NULL DEFAULT ''"),
            ("GangExperience", "BIGINT NOT NULL DEFAULT 0"),
            ("GangLieutenant", "INTEGER NOT NULL DEFAULT 0"),
            ("GangViewOnlineOnly", "INTEGER NOT NULL DEFAULT 0"),
            ("IsSysop", "INTEGER NOT NULL DEFAULT 0"),
            ("IsTestAccount", "INTEGER NOT NULL DEFAULT 0"),
            ("ToptenDisabled", "INTEGER NOT NULL DEFAULT 0"),
            ("TalkMode", "INTEGER NOT NULL DEFAULT 0"),
            ("BroadcastChannel", "INTEGER NOT NULL DEFAULT 0"),
            ("BriefMode", "INTEGER NOT NULL DEFAULT 0"),
            ("PaletteId", "INTEGER NOT NULL DEFAULT 0"),
            ("StatlineMode", "INTEGER NOT NULL DEFAULT 1"),
            ("CustomStatline", "TEXT NOT NULL DEFAULT ''"),
            ("StyleTechnical", "INTEGER NOT NULL DEFAULT 0"),
            ("ReceiveGossipEnabled", "INTEGER NOT NULL DEFAULT 1"),
            ("ReceiveAuctionEnabled", "INTEGER NOT NULL DEFAULT 1"),
            ("WarnOnEvilEnabled", "INTEGER NOT NULL DEFAULT 0"),
            ("LookStyleModern", "INTEGER NOT NULL DEFAULT 0"),
            ("ReceiveItemsEnabled", "INTEGER NOT NULL DEFAULT 1"),
            ("ActionsEnabled", "INTEGER NOT NULL DEFAULT 1"),
            ("EvilPoints", "REAL NOT NULL DEFAULT 0"),
            ("IsLawful", "INTEGER NOT NULL DEFAULT 0"),
            ("EvilPointsForgivenToday", "REAL NOT NULL DEFAULT 0"),
            ("MinEvilPoints", "REAL NOT NULL DEFAULT -220"),
            ("LastEvilPointForgivenessDayNumber", "INTEGER NOT NULL DEFAULT 0"),
            ("IgnoredPlayers", "TEXT NOT NULL DEFAULT '[]'"),
            ("SuicidePassword", "TEXT NOT NULL DEFAULT ''"),
            ("KeepMode", "INTEGER NOT NULL DEFAULT 1"),
            // The drop-carrier flag: the penalty was applied and this
            // character has not yet been told. Read and cleared on their next login.
            ("DisconnectedWhilePlaying", "INTEGER NOT NULL DEFAULT 0"),
            ("QuestAbilities", "TEXT NOT NULL DEFAULT '{}'"),
            ("BankBalances", "TEXT NOT NULL DEFAULT '{}'"),
            // Timed spell slots (the active-spell array) + the poison accumulator. These
            // must persist across logout/restart or a player sheds any DoT/debuff/jail just by reconnecting.
            ("ActiveSpells", "TEXT NOT NULL DEFAULT '[]'"),
            // Non-stock recent-death log (see Player.DeathRecord), rendered on `stat all` when
            // SYSOP CONFIGURE DEATHLOG is on. Capped at Player.MaxDeathLogEntries when written.
            ("DeathLog", "TEXT NOT NULL DEFAULT '[]'"),
            ("PoisonLevel", "INTEGER NOT NULL DEFAULT 0"),
            ("TesterSysop", "INTEGER NOT NULL DEFAULT 0"),
            // The daily cleanup already applied to this character (ISO-8601, '' = never). Keeps the
            // login-time recharge pass from refilling charged items on every reconnect — see
            // GameWorld.RechargePlayerItemsIfNeeded.
            ("LastCleanupUtc", "TEXT NOT NULL DEFAULT ''"),
        };

        foreach (var (name, definition) in playerColumns)
        {
            using var cmd = new NpgsqlCommand($"ALTER TABLE Players ADD COLUMN IF NOT EXISTS {name} {definition}", conn);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = new NpgsqlCommand("ALTER TABLE RoomGroundCurrency ADD COLUMN IF NOT EXISTS VisibleStacksJson TEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE RoomGroundCurrency ADD COLUMN IF NOT EXISTS HiddenStacksJson TEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();

        using (var cmd = new NpgsqlCommand("ALTER TABLE GangSettings ADD COLUMN IF NOT EXISTS LeaderName CITEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS Status INTEGER NOT NULL DEFAULT 0", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS IsCompleted INTEGER NOT NULL DEFAULT 0", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS CompletedAt TEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS CompletedByBbsUserName CITEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS ReporterReviewedAt TEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS VerifiedAt TEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS VerifiedByBbsUserName CITEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS VerifiedByPlayerName CITEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("ALTER TABLE BugReports ADD COLUMN IF NOT EXISTS ResolutionNote TEXT NOT NULL DEFAULT ''", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand(@"
            UPDATE BugReports
            SET Status = 1
            WHERE Status = 0 AND IsCompleted <> 0", conn))
            cmd.ExecuteNonQuery();

        using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_players_gang ON Players (Gang)", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_players_bbsuserid ON Players (BbsUserId)", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_bugreports_createdat ON BugReports (Id DESC)", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_ganginvites_invitee ON GangInvites (InviteeName)", conn))
            cmd.ExecuteNonQuery();
        using (var cmd = new NpgsqlCommand("CREATE INDEX IF NOT EXISTS idx_roomgrounditems_room ON RoomGroundItems (MapNumber, RoomNumber, Sequence)", conn))
            cmd.ExecuteNonQuery();
    }

    private static void EnsureGameDataPresent(NpgsqlConnection conn)
    {
        using var cmd = new NpgsqlCommand(@"
            SELECT 1
            FROM information_schema.tables
            WHERE table_schema = @schema
              AND table_name = @tableName", conn);
        cmd.Parameters.AddWithValue("schema", GameDataSchema);
        cmd.Parameters.AddWithValue("tableName", "Races");
        if (cmd.ExecuteScalar() == null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL schema '{GameDataSchema}' has no game data. " +
                "Import game data first: see tools/dat-import/README.md.");
        }
    }

    // Stock wccmsg2.dat ships blank "silence sentinel" message records — every line empty — that
    // MoveMsg / CastMsg / DestructMsg fields point at to mean "print nothing". Verified against the raw
    // DAT (wccmsg2.dat): #66 occupies LIVE Btrieve data-page slots (usage
    // 3 and 6, both non-zero) with empty Line1/Line2, so it is real data, not a free-slot phantom. 321
    // monsters — cutpurse #260, black queen #1075, marble golem #1074, … — point their MoveMsg at #66
    // and must therefore spawn with NO arrival line (stock prints nothing for a
    // present-but-blank record; only a genuinely ABSENT one gets the "moves into the room" fallback).
    //
    // These rows are uniquely easy to lose: they carry no visible content, so nothing downstream
    // complains when they vanish, and any database snapshot taken while they are missing bakes the
    // loss in. Re-inserting here on every start makes them self-healing no matter how the database
    // was seeded. Never overwrites an existing
    // row, so a mod that gives a sentinel id real content keeps it.
    private static readonly int[] BlankMessageSentinelNumbers =
    [
        1, 66, 121, 173, 364, 448, 764, 787, 1064, 1319, 2421,
        2510, 2568, 2570, 2967, 3097, 3153, 3182, 3332, 8420, 21061, 21071,
    ];

    private static void EnsureBlankMessageSentinelsPresent(NpgsqlConnection conn)
    {
        foreach (int number in BlankMessageSentinelNumbers)
        {
            using var cmd = new NpgsqlCommand($@"
                INSERT INTO {GameDataSchema}.""Messages"" (""Number"", ""Line1"", ""Line2"", ""Line3"")
                SELECT @number, '', '', ''
                WHERE NOT EXISTS (
                    SELECT 1 FROM {GameDataSchema}.""Messages"" WHERE ""Number"" = @number)", conn);
            cmd.Parameters.AddWithValue("number", number);
            cmd.ExecuteNonQuery();
        }
    }

    private static void EnsureRoomAttributesImported(NpgsqlConnection conn)
    {
        using (var addColumn = new NpgsqlCommand($@"
            ALTER TABLE {GameDataSchema}.""Rooms""
            ADD COLUMN IF NOT EXISTS ""Attributes"" INTEGER NOT NULL DEFAULT 0", conn))
        {
            addColumn.ExecuteNonQuery();
        }
    }

    private static void EnsureRoomTypeImported(NpgsqlConnection conn)
    {
        using (var addColumn = new NpgsqlCommand($@"
            ALTER TABLE {GameDataSchema}.""Rooms""
            ADD COLUMN IF NOT EXISTS ""RoomType"" INTEGER NOT NULL DEFAULT 0", conn))
        {
            addColumn.ExecuteNonQuery();
        }
    }

    private static void EnsureMonsterAttackMessageMetadataImported(NpgsqlConnection conn)
    {
        foreach (string prefix in new[] { "AtkHitMsg", "AtkDodgeMsg", "AtkMissMsg" })
        {
            for (int slot = 0; slot < MonsterAttackSlotCount; slot++)
            {
                using var addColumn = new NpgsqlCommand($@"
                    ALTER TABLE {GameDataSchema}.""Monsters""
                    ADD COLUMN IF NOT EXISTS ""{prefix}-{slot}"" INTEGER NOT NULL DEFAULT 0", conn);
                addColumn.ExecuteNonQuery();
            }
        }
    }

    // The charge deduction reads the destruction-message id from the item record and
    // looks it up in the Messages table. The legacy converter was
    // dropping that field on the floor, so the game_data."Items" table has no DestructMsg.
    // This migration adds the column and seeds it from an embedded CSV when present (tools/dat-import
    // writes the column directly). Idempotent: the UPDATE
    // only fires when an item's stored DestructMsg is 0 and the CSV has a non-zero value, so
    // operator hand-tweaks survive a restart.
    private static void EnsureItemsDestructMsgImported(NpgsqlConnection conn)
    {
        AddColumnIfMissing(conn, "Items", "DestructMsg", "INTEGER NOT NULL DEFAULT 0");
        var rows = LoadIntIntCsvSeed("items_destruct_msg.csv");
        ApplyBulkIntColumnUpdate(conn, "Items", "DestructMsg", rows);
    }

    // ----------------------------------------------------------------------------------------
    // Per-record DAT fields added after the original import. Each Ensure*ExtrasImported migration
    // below adds the matching column(s) and, when an embedded seed resource is present, seeds
    // them from it. This build ships no seed resources: tools/dat-import writes these columns
    // directly, so the seed step is a no-op.
    //
    // All migrations are idempotent: the column add uses ADD COLUMN IF NOT EXISTS, and the
    // UPDATE statement only fires on rows whose target column is still at the default value,
    // so operator hand-edits survive a restart.
    // ----------------------------------------------------------------------------------------

    private static void EnsureItemsExtrasImported(NpgsqlConnection conn)
    {
        // The stealable flag (used by the `rob` command) and the read-text-block id
        // (sign content for signs, miss-msg id for weapons).
        AddColumnIfMissing(conn, "Items", "Robable", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Items", "ReadTextBlock", "INTEGER NOT NULL DEFAULT 0");

        var rows = LoadTsvSeed("items_extras.tsv");
        SeedFromTsv(conn, "Items", "Number", rows, new[] { "Robable", "ReadTextBlock" });
    }

    // Opening a container (ItemType 8) rolls coins straight into
    // the opener's purse, silently, AFTER the loot spell — `player.<denom> += lngrnd(0, item.<max>)`
    // for each of runic/platinum/gold/silver/copper. The maxes live at item record bytes
    // 944/948/952/956/960. The legacy parser read them four bytes
    // early and dropped them, so chests delivered only the gem/item loot and no coins. Seeded from
    // the open-coins export. Idempotent:
    // ADD COLUMN IF NOT EXISTS + UPDATE only rows still at the default 0.
    private static void EnsureItemsOpenCoinsImported(NpgsqlConnection conn)
    {
        AddColumnIfMissing(conn, "Items", "OpenRunic", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Items", "OpenPlatinum", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Items", "OpenGold", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Items", "OpenSilver", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Items", "OpenCopper", "INTEGER NOT NULL DEFAULT 0");

        var rows = LoadTsvSeed("items_open_coins.tsv");
        SeedFromTsv(conn, "Items", "Number", rows,
            new[] { "OpenRunic", "OpenPlatinum", "OpenGold", "OpenSilver", "OpenCopper" });
    }

    // The 10-entry Negate Spells list. The negate gate walks every worn slot's
    // list and short-circuits a spell about to land on the bearer when any slot matches. Powers
    // items like the magma amulet (negates magma heat / temple of fire fire), phoenix feather,
    // mirrored shield, holy medallion, swamp boots, etc. The DAT parser was
    // already reading these 40 bytes; we now persist them.
    private static void EnsureItemsNegateSpellsImported(NpgsqlConnection conn)
    {
        for (int i = 0; i < 10; i++)
            AddColumnIfMissing(conn, "Items", $"NegateSpell-{i}", "INTEGER NOT NULL DEFAULT 0");

        var rows = LoadTsvSeed("items_negate_spells.tsv");
        var cols = new string[10];
        for (int i = 0; i < 10; i++) cols[i] = $"NegateSpell-{i}";
        SeedFromTsv(conn, "Items", "Number", rows, cols);
    }

    private static void EnsureMonstersExtrasImported(NpgsqlConnection conn)
    {
        // Monster Description (was populated by a legacy one-off import that's no longer
        // reproducible) + stock message ids and charm-resist that were never imported.
        // DropUses-i pairs with DropItem-i (charges the dropped item carries on the corpse).
        AddColumnIfMissing(conn, "Monsters", "Description", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing(conn, "Monsters", "MoveMsg",  "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Monsters", "DeathMsg", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Monsters", "TalkTxt",  "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Monsters", "CharmRes", "INTEGER NOT NULL DEFAULT 0");
        for (int i = 0; i < 10; i++)
            AddColumnIfMissing(conn, "Monsters", $"DropUses-{i}", "INTEGER NOT NULL DEFAULT 0");

        var rows = LoadTsvSeed("monsters_extras.tsv");
        var cols = new List<string> { "Description", "MoveMsg", "DeathMsg", "TalkTxt", "CharmRes" };
        for (int i = 0; i < 10; i++) cols.Add($"DropUses-{i}");
        SeedFromTsv(conn, "Monsters", "Number", rows, cols.ToArray());
    }

    private static void EnsureSpellsExtrasImported(NpgsqlConnection conn)
    {
        // A 50-byte first half + 50-byte second half joined into the displayed spell description.
        AddColumnIfMissing(conn, "Spells", "Description", "TEXT NOT NULL DEFAULT ''");

        var rows = LoadTsvSeed("spells_extras.tsv");
        SeedFromTsv(conn, "Spells", "Number", rows, new[] { "Description" });
    }

    // The spell "duration random multiplier per effective level". Stock
    // rolls over [the stepped base, DurRand*effLevel] when
    // the random max exceeds the stepped base; only 6 stock spells use it (the legacy parser was
    // throwing it away as UNDEFINED01 at byte 202). Seeded from spells_dur_rand.csv when present
    // (tools/dat-import writes the column directly). Idempotent: skips rows that already have non-zero DurRand.
    private static void EnsureSpellsDurRandImported(NpgsqlConnection conn)
    {
        AddColumnIfMissing(conn, "Spells", "DurRand", "INTEGER NOT NULL DEFAULT 0");
        var rows = LoadIntIntCsvSeed("spells_dur_rand.csv");
        ApplyBulkIntColumnUpdate(conn, "Spells", "DurRand", rows);
    }

    // The area-wide monster cap ("Area Max" on the sysop screen), held in a cluster's
    // control room and read on spawn: refuse once the living count reaches it.
    // The original import dropped it. Seeded from room_max_area.csv when present
    // (rows `Map,Room,MaxArea` for the 3.6k non-zero rooms, extracted from wccmp002.dat). Idempotent:
    // only updates rooms still at the default 0, so operator edits survive a restart.
    private static void EnsureRoomMaxAreaImported(NpgsqlConnection conn)
    {
        AddColumnIfMissing(conn, "Rooms", "MaxArea", "INTEGER NOT NULL DEFAULT 0");
        ApplyRoomIntColumnSeed(conn, "MaxArea", LoadRoomIntCsvSeed("room_max_area.csv"));
    }

    // The monster group-match id and max-followers count — the two fields
    // that drive the Leader/Follower anchor+drag group formation. The legacy converter
    // dropped both ("something3"/"nothing2"); now surfaced and backfilled from CSVs extracted from
    // the monster data file. ExpMulti (the leader-strength tiebreak) is already imported. Idempotent: only
    // updates rows still at the default 0.
    private static void EnsureMonsterGroupFormationImported(NpgsqlConnection conn)
    {
        AddColumnIfMissing(conn, "Monsters", "GroupMatch", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(conn, "Monsters", "MaxFollowers", "INTEGER NOT NULL DEFAULT 0");
        ApplyBulkIntColumnUpdate(conn, "Monsters", "GroupMatch", LoadIntIntCsvSeed("monster_group_match.csv"));
        ApplyBulkIntColumnUpdate(conn, "Monsters", "MaxFollowers", LoadIntIntCsvSeed("monster_max_followers.csv"));
    }

    // The room "by Number" field — when non-zero, the spawn places THIS specific
    // monster # in the room and short-circuits the group/index random pick (which only runs when
    // no explicit number is passed). The legacy parser read it 2 bytes early (garbage) and dropped it, so 29
    // stock rooms (Silvermere newbie tunnels + shopkeeper rooms) spawned the wrong monster, a random
    // group member, or nothing. Seeded from room_by_number.csv (Map,Room,ByNumber). Idempotent: only
    // updates rooms still at the default 0, so operator edits survive a restart.
    private static void EnsureRoomByNumberImported(NpgsqlConnection conn)
    {
        AddColumnIfMissing(conn, "Rooms", "ByNumber", "INTEGER NOT NULL DEFAULT 0");
        ApplyRoomIntColumnSeed(conn, "ByNumber", LoadRoomIntCsvSeed("room_by_number.csv"));
    }

    // Loads a `Map,Room,Value` CSV seed resource (composite-keyed, for the Rooms table). Rows lacking
    // either key column or with malformed numbers are skipped (matches the single-key loaders).
    private static List<(int Map, int Room, int Value)> LoadRoomIntCsvSeed(string resourceFileName)
    {
        var assembly = typeof(PostgresBootstrapper).Assembly;
        string resourceName = $"mmudreborn.Server.Data.SeedResources.{resourceFileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return [];

        var rows = new List<(int, int, int)>();
        using var reader = new System.IO.StreamReader(stream);
        reader.ReadLine(); // header
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length < 3) continue;
            if (int.TryParse(parts[0], out int map) && int.TryParse(parts[1], out int room) && int.TryParse(parts[2], out int value))
                rows.Add((map, room, value));
        }
        return rows;
    }

    // Composite-keyed ((Map Number, Room Number)) idempotent bulk update for an int Rooms column. Only
    // touches rows whose target column is still 0, so a re-run on an operator-edited DB is a no-op there.
    private static void ApplyRoomIntColumnSeed(NpgsqlConnection conn, string column, List<(int Map, int Room, int Value)> rows)
    {
        if (rows.Count == 0)
            return;

        using var cmd = new NpgsqlCommand { Connection = conn };
        var valueRows = new List<string>(rows.Count);
        for (int i = 0; i < rows.Count; i++)
        {
            cmd.Parameters.AddWithValue($"m{i}", rows[i].Map);
            cmd.Parameters.AddWithValue($"r{i}", rows[i].Room);
            cmd.Parameters.AddWithValue($"v{i}", rows[i].Value);
            valueRows.Add($"(@m{i}, @r{i}, @v{i})");
        }

        cmd.CommandText =
            $"UPDATE game_data.\"Rooms\" AS t SET \"{column}\" = s.v " +
            $"FROM (VALUES {string.Join(",", valueRows)}) AS s(m, r, v) " +
            $"WHERE t.\"Map Number\" = s.m AND t.\"Room Number\" = s.r AND t.\"{column}\" = 0";
        cmd.ExecuteNonQuery();
    }

    // Shared helpers for the "ADD COLUMN + idempotent seed from embedded CSV" pattern used by
    // every per-column DAT migration above. Each CSV row is `<Number>,<intValue>` (rows lacking
    // either column or with malformed numbers are silently skipped — matches the original loaders).
    // The bulk-update emits one `UPDATE … FROM (VALUES …)` round-trip and only touches rows whose
    // target column is still 0, so re-running on a server with operator-edited values is a no-op
    // on those rows.
    private static List<(int Number, int Value)> LoadIntIntCsvSeed(string resourceFileName)
    {
        var assembly = typeof(PostgresBootstrapper).Assembly;
        string resourceName = $"mmudreborn.Server.Data.SeedResources.{resourceFileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return [];

        using var reader = new System.IO.StreamReader(stream);
        var rows = new List<(int, int)>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            int comma = line.IndexOf(',');
            if (comma <= 0)
                continue;
            if (!int.TryParse(line.AsSpan(0, comma), out int number))
                continue;
            if (!int.TryParse(line.AsSpan(comma + 1), out int value))
                continue;
            rows.Add((number, value));
        }
        return rows;
    }

    private static void ApplyBulkIntColumnUpdate(NpgsqlConnection conn, string table, string column, List<(int Number, int Value)> rows)
    {
        if (rows.Count == 0)
            return;

        var sql = new System.Text.StringBuilder();
        sql.Append($"UPDATE {GameDataSchema}.\"{table}\" AS t SET \"{column}\" = s.v FROM (VALUES ");
        bool first = true;
        foreach (var (number, value) in rows)
        {
            if (!first) sql.Append(", ");
            sql.Append('(').Append(number).Append(", ").Append(value).Append(')');
            first = false;
        }
        sql.Append($") AS s(num, v) WHERE t.\"Number\" = s.num AND t.\"{column}\" = 0");

        using var update = new NpgsqlCommand(sql.ToString(), conn);
        update.ExecuteNonQuery();
    }

    private static void EnsureShopsExtrasImported(NpgsqlConnection conn)
    {
        // V1.11p reality: shops have no populated description text in the DAT — every shop
        // record has 156 bytes of space-padded null description. We still add the column for
        // forward compatibility (custom shops, future content packs).
        AddColumnIfMissing(conn, "Shops", "Description", "TEXT NOT NULL DEFAULT ''");

        var rows = LoadTsvSeed("shops_extras.tsv");
        SeedFromTsv(conn, "Shops", "Number", rows, new[] { "Description" });
    }

    private static void AddColumnIfMissing(NpgsqlConnection conn, string table, string column, string columnDef)
    {
        using var cmd = new NpgsqlCommand($@"
            ALTER TABLE {GameDataSchema}.""{table}""
            ADD COLUMN IF NOT EXISTS ""{column}"" {columnDef}", conn);
        cmd.ExecuteNonQuery();
    }

    private sealed record TsvSeed(string[] Header, List<string[]> Rows);

    private static TsvSeed LoadTsvSeed(string resourceFileName)
    {
        var assembly = typeof(PostgresBootstrapper).Assembly;
        string resourceName = $"mmudreborn.Server.Data.SeedResources.{resourceFileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return new TsvSeed(Array.Empty<string>(), new());

        using var reader = new System.IO.StreamReader(stream);
        string? headerLine = reader.ReadLine();
        if (headerLine == null)
            return new TsvSeed(Array.Empty<string>(), new());

        var header = headerLine.Split('\t');
        var rows = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrEmpty(line)) continue;
            rows.Add(line.Split('\t'));
        }
        return new TsvSeed(header, rows);
    }

    private static string DecodeTsvCell(string raw)
    {
        // Reverse the export-side encoding: `\n`→newline, `\t`→tab, `\\`→`\`. Order matters
        // so the literal-backslash sequence is the LAST replacement.
        if (string.IsNullOrEmpty(raw) || !raw.Contains('\\'))
            return raw;
        var sb = new System.Text.StringBuilder(raw.Length);
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && i + 1 < raw.Length)
            {
                char next = raw[i + 1];
                if (next == 'n') { sb.Append('\n'); i++; continue; }
                if (next == 't') { sb.Append('\t'); i++; continue; }
                if (next == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void SeedFromTsv(NpgsqlConnection conn, string table, string keyColumn, TsvSeed seed, string[] columnsToSeed)
    {
        if (seed.Rows.Count == 0 || seed.Header.Length == 0)
            return;

        // Map header column name → index. The key column (Number/Name) is always at column 0
        // by export-tool convention.
        var headerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < seed.Header.Length; i++)
            headerIndex[seed.Header[i]] = i;

        if (!headerIndex.TryGetValue(keyColumn, out int keyIdx))
            return;

        var paramSet = new System.Text.StringBuilder();
        var whereClauses = new List<string>();
        using var cmd = new NpgsqlCommand { Connection = conn };
        int paramCount = 0;

        // Build a parameterised UPDATE that touches each row with at least one non-default
        // value. The WHERE clause skips rows whose target columns already differ from the
        // default so operator hand-edits survive.
        var setClauses = new List<string>();
        for (int c = 0; c < columnsToSeed.Length; c++)
            setClauses.Add($"\"{columnsToSeed[c]}\" = s.\"v{c}\"");

        foreach (string col in columnsToSeed)
        {
            // Skip rows where the column already has a non-default value.
            // Use a per-column comparison: numeric defaults to 0; text defaults to ''.
            whereClauses.Add($"(t.\"{col}\" = (CASE WHEN pg_typeof(t.\"{col}\") = 'text'::regtype THEN '' ELSE 0 END)::text::{col switch
            {
                _ => "text"
            }})");
        }

        // Build a single UPDATE … FROM (VALUES (...), (...) AS s(num,v0,v1,…) WHERE t.Number = s.num
        var valueRows = new List<string>(seed.Rows.Count);
        foreach (var row in seed.Rows)
        {
            if (row.Length <= keyIdx) continue;
            if (!int.TryParse(row[keyIdx], out int key)) continue;

            var rowParams = new List<string> { $"@k{paramCount}" };
            cmd.Parameters.AddWithValue($"k{paramCount}", key);

            for (int c = 0; c < columnsToSeed.Length; c++)
            {
                if (!headerIndex.TryGetValue(columnsToSeed[c], out int srcIdx) || srcIdx >= row.Length)
                {
                    rowParams.Add("NULL");
                    continue;
                }

                string raw = row[srcIdx];
                // Numeric column: detect by trying to parse the first non-empty value of this column.
                // Simpler: just attempt int first, fall back to text.
                if (int.TryParse(raw, out int intVal))
                {
                    rowParams.Add($"@v{paramCount}_{c}");
                    cmd.Parameters.AddWithValue($"v{paramCount}_{c}", intVal);
                }
                else
                {
                    rowParams.Add($"@v{paramCount}_{c}");
                    cmd.Parameters.AddWithValue($"v{paramCount}_{c}", DecodeTsvCell(raw));
                }
            }
            valueRows.Add($"({string.Join(", ", rowParams)})");
            paramCount++;
        }

        if (valueRows.Count == 0)
            return;

        // Generate the VALUES alias columns: s(num, v0, v1, …)
        var aliasCols = new List<string> { "num" };
        for (int c = 0; c < columnsToSeed.Length; c++)
            aliasCols.Add($"v{c}");

        // WHERE: only update rows whose ALL target columns are still default (0 / '').
        // This keeps the seed idempotent and preserves any operator overrides.
        var defaultGuards = new List<string>();
        foreach (string col in columnsToSeed)
        {
            // Use a permissive guard: only skip when the column has a non-default value.
            // Postgres can't introspect the column type here cheaply, so do this check
            // textually by trying both 0 and '' patterns. Using `IS NOT DISTINCT FROM` makes
            // it null-safe.
            defaultGuards.Add($"((t.\"{col}\"::text = '0') OR (t.\"{col}\"::text = '') OR t.\"{col}\" IS NULL)");
        }

        cmd.CommandText = $@"
            UPDATE {GameDataSchema}.""{table}"" AS t
            SET {string.Join(", ", setClauses)}
            FROM (VALUES {string.Join(", ", valueRows)}) AS s({string.Join(", ", aliasCols)})
            WHERE t.""{keyColumn}"" = s.num
              AND ({string.Join(" OR ", defaultGuards)})";

        cmd.ExecuteNonQuery();
    }
}
