using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using mmudreborn.Game;
using Npgsql;
using NpgsqlTypes;

namespace mmudreborn.Data;

public class PlayerRepository : IPlayerRepository
{
    private readonly string _connectionString;

    // GossipLog caps (bounded so the table can't bloat): keep the newest N rows AND drop anything
    // older than N days, enforced opportunistically every PruneEvery inserts.
    private const int GossipRowCap = 1000;
    private const int GossipMaxAgeDays = 7;
    private const int GossipPruneEvery = 50;
    private static int _gossipInsertCount;

    public PlayerRepository(string connectionString)
    {
        _connectionString = connectionString;
        EnsurePlayerTable();
    }

    private NpgsqlConnection OpenConnection()
    {
        var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    private static void AddCitextParameter(NpgsqlCommand cmd, string name, string value)
    {
        cmd.Parameters.Add(name, NpgsqlDbType.Citext).Value = value;
    }

    private void EnsurePlayerTable()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = 'public'
                            AND table_name IN ('players', 'gangsettings', 'ganginvites', 'serversettings', 'bugreports')";

        if (Convert.ToInt32(cmd.ExecuteScalar() ?? 0) < 5)
            throw new InvalidOperationException("Player schema is missing. PostgreSQL bootstrap must run before PlayerRepository is constructed.");

        using var normalize = conn.CreateCommand();
        normalize.CommandText = @"
            UPDATE Players
            SET Name = CASE
                    WHEN BTRIM(Name::text) = '' THEN ''
                    WHEN BTRIM(Name::text) = UPPER(BTRIM(Name::text))
                        THEN UPPER(LEFT(BTRIM(Name::text), 1)) || LOWER(SUBSTRING(BTRIM(Name::text) FROM 2))
                    ELSE UPPER(LEFT(BTRIM(Name::text), 1)) || SUBSTRING(BTRIM(Name::text) FROM 2)
                END,
                LastName = CASE
                    WHEN BTRIM(LastName::text) = '' THEN ''
                    WHEN BTRIM(LastName::text) = UPPER(BTRIM(LastName::text))
                        THEN UPPER(LEFT(BTRIM(LastName::text), 1)) || LOWER(SUBSTRING(BTRIM(LastName::text) FROM 2))
                    ELSE UPPER(LEFT(BTRIM(LastName::text), 1)) || SUBSTRING(BTRIM(LastName::text) FROM 2)
                END
            WHERE Name <> CASE
                    WHEN BTRIM(Name::text) = '' THEN ''
                    WHEN BTRIM(Name::text) = UPPER(BTRIM(Name::text))
                        THEN UPPER(LEFT(BTRIM(Name::text), 1)) || LOWER(SUBSTRING(BTRIM(Name::text) FROM 2))
                    ELSE UPPER(LEFT(BTRIM(Name::text), 1)) || SUBSTRING(BTRIM(Name::text) FROM 2)
                END
               OR LastName <> CASE
                    WHEN BTRIM(LastName::text) = '' THEN ''
                    WHEN BTRIM(LastName::text) = UPPER(BTRIM(LastName::text))
                        THEN UPPER(LEFT(BTRIM(LastName::text), 1)) || LOWER(SUBSTRING(BTRIM(LastName::text) FROM 2))
                    ELSE UPPER(LEFT(BTRIM(LastName::text), 1)) || SUBSTRING(BTRIM(LastName::text) FROM 2)
                END";
        normalize.ExecuteNonQuery();
    }

    // BBS account password hashing lives in the shared core; forwarder kept for existing call sites.
    public static string HashPassword(string password) => CWGaming.Shared.BbsSecurity.HashPassword(password);

    public int BackfillMissingBbsUserIds(IReadOnlyDictionary<string, string> links)
    {
        ArgumentNullException.ThrowIfNull(links);

        if (links.Count == 0)
            return 0;

        using var conn = OpenConnection();
        using var transaction = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = @"
            UPDATE Players
            SET BbsUserId = @bbsuserid
            WHERE Name = @name
              AND BTRIM(COALESCE(BbsUserId::text, '')) = ''";

        AddCitextParameter(cmd, "@name", string.Empty);
        AddCitextParameter(cmd, "@bbsuserid", string.Empty);

        int updated = 0;
        foreach (var (playerName, bbsUserId) in links)
        {
            cmd.Parameters["@name"].Value = Game.Player.NormalizeNamePart(playerName);
            cmd.Parameters["@bbsuserid"].Value = Game.Player.NormalizeNamePart(bbsUserId);
            updated += cmd.ExecuteNonQuery();
        }

        transaction.Commit();
        return updated;
    }

    public bool PlayerExists(string name)
    {
        name = Player.NormalizeNamePart(name);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Players WHERE Name = @name";
        AddCitextParameter(cmd, "@name", name);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    // The player-row upsert SQL. Bound parameters come from BuildPlayerSaveParameters; the statement is
    // a constant so both the synchronous SavePlayer path and the write-behind CaptureSnapshot/SaveSnapshot
    // pair share one definition.
    private const string PlayerUpsertSql = @"
            INSERT INTO Players
            (Name, PasswordHash, RaceId, ClassId, Level, Experience,
             CurrentHP, MaxHP, CurrentMana, MaxMana, SpellCasting,
             Strength, Agility, Intellect, Willpower, Health, Charm,
             ArmourClass, DamageResist, MagicResist,
             CurrentMap, CurrentRoom,
             Runic, Platinum, Gold, Silver, Copper,
             CharacterPoints, SpentCP, Alignment, EvilPoints, IsLawful, EvilPointsForgivenToday, LastEvilPointForgivenessDayNumber, MinEvilPoints,
             Lives, Thievery, Traps, Picklocks, Tracking, MartialArts,
             BaseStrength, BaseAgility, BaseIntellect, BaseWillpower, BaseHealth, BaseCharm,
             HairLength, HairColour, EyeColour, Gender, LastName, BbsUserId, Gang, GangExperience, GangLieutenant, GangViewOnlineOnly, IsSysop, IsTestAccount, ToptenDisabled, TalkMode, BroadcastChannel, BriefMode, PaletteId, StatlineMode, CustomStatline, ReceiveItemsEnabled, ActionsEnabled,
             StyleTechnical, ReceiveGossipEnabled, ReceiveAuctionEnabled, WarnOnEvilEnabled, LookStyleModern,
             IgnoredPlayers,
             SuicidePassword, KeepMode, DisconnectedWhilePlaying,
             QuestAbilities,
             BankBalances,
             ActiveSpells, DeathLog, PoisonLevel, TesterSysop, LastCleanupUtc,
             Inventory, Equipment)
            VALUES
            (@name, @pass, @race, @class, @level, @exp,
             @hp, @maxhp, @mana, @maxmana, @spellcasting,
             @str, @agl, @int, @wil, @hea, @chm,
             @ac, @dr, @mr,
             @map, @room,
             @runic, @plat, @gold, @silver, @copper,
             @cp, @scp, @align, @evilpoints, @islawful, @evilpointsforgiventoday, @lastevilpointforgivenessdaynumber, @minevilpoints,
             @lives, @thievery, @traps, @picklocks, @tracking, @martialarts,
             @basestr, @baseagl, @baseint, @basewil, @basehea, @basechm,
             @hairlength, @haircolour, @eyecolour, @gender, @lastname, @bbsuserid, @gang, @gangexp, @ganglieutenant, @gangviewonline, @issysop, @istestaccount, @topten, @talkmode, @broadcastchannel, @briefmode, @paletteid, @statlinemode, @customstatline, @receiveitemsenabled, @actionsenabled,
             @styletechnical, @receivegossipenabled, @receiveauctionenabled, @warnonevilenabled, @lookstylemodern,
             @ignoredplayers,
             @suicidepassword, @keepmode, @disconnectedwhileplaying,
             @questabilities,
             @bankbalances,
             @activespells, @deathlog, @poisonlevel, @testersysop, @lastcleanuputc,
             @inv, @equip)
            ON CONFLICT (Name) DO UPDATE SET
             Name = EXCLUDED.Name,
             PasswordHash = EXCLUDED.PasswordHash,
             RaceId = EXCLUDED.RaceId,
             ClassId = EXCLUDED.ClassId,
             Level = EXCLUDED.Level,
             Experience = EXCLUDED.Experience,
             CurrentHP = EXCLUDED.CurrentHP,
             MaxHP = EXCLUDED.MaxHP,
             CurrentMana = EXCLUDED.CurrentMana,
             MaxMana = EXCLUDED.MaxMana,
             SpellCasting = EXCLUDED.SpellCasting,
             Strength = EXCLUDED.Strength,
             Agility = EXCLUDED.Agility,
             Intellect = EXCLUDED.Intellect,
             Willpower = EXCLUDED.Willpower,
             Health = EXCLUDED.Health,
             Charm = EXCLUDED.Charm,
             ArmourClass = EXCLUDED.ArmourClass,
             DamageResist = EXCLUDED.DamageResist,
             MagicResist = EXCLUDED.MagicResist,
             CurrentMap = EXCLUDED.CurrentMap,
             CurrentRoom = EXCLUDED.CurrentRoom,
             Runic = EXCLUDED.Runic,
             Platinum = EXCLUDED.Platinum,
             Gold = EXCLUDED.Gold,
             Silver = EXCLUDED.Silver,
             Copper = EXCLUDED.Copper,
             CharacterPoints = EXCLUDED.CharacterPoints,
             SpentCP = EXCLUDED.SpentCP,
             Alignment = EXCLUDED.Alignment,
             EvilPoints = EXCLUDED.EvilPoints,
             IsLawful = EXCLUDED.IsLawful,
             EvilPointsForgivenToday = EXCLUDED.EvilPointsForgivenToday,
             LastEvilPointForgivenessDayNumber = EXCLUDED.LastEvilPointForgivenessDayNumber,
             MinEvilPoints = EXCLUDED.MinEvilPoints,
             Lives = EXCLUDED.Lives,
             Thievery = EXCLUDED.Thievery,
             Traps = EXCLUDED.Traps,
             Picklocks = EXCLUDED.Picklocks,
             Tracking = EXCLUDED.Tracking,
             MartialArts = EXCLUDED.MartialArts,
             BaseStrength = EXCLUDED.BaseStrength,
             BaseAgility = EXCLUDED.BaseAgility,
             BaseIntellect = EXCLUDED.BaseIntellect,
             BaseWillpower = EXCLUDED.BaseWillpower,
             BaseHealth = EXCLUDED.BaseHealth,
             BaseCharm = EXCLUDED.BaseCharm,
             HairLength = EXCLUDED.HairLength,
             HairColour = EXCLUDED.HairColour,
             EyeColour = EXCLUDED.EyeColour,
             Gender = EXCLUDED.Gender,
             LastName = EXCLUDED.LastName,
             BbsUserId = EXCLUDED.BbsUserId,
             Gang = EXCLUDED.Gang,
             GangExperience = EXCLUDED.GangExperience,
             GangLieutenant = EXCLUDED.GangLieutenant,
             GangViewOnlineOnly = EXCLUDED.GangViewOnlineOnly,
             IsSysop = EXCLUDED.IsSysop,
             TesterSysop = EXCLUDED.TesterSysop,
             IsTestAccount = EXCLUDED.IsTestAccount,
             ToptenDisabled = EXCLUDED.ToptenDisabled,
             TalkMode = EXCLUDED.TalkMode,
             BroadcastChannel = EXCLUDED.BroadcastChannel,
             BriefMode = EXCLUDED.BriefMode,
             PaletteId = EXCLUDED.PaletteId,
             StatlineMode = EXCLUDED.StatlineMode,
             CustomStatline = EXCLUDED.CustomStatline,
             StyleTechnical = EXCLUDED.StyleTechnical,
             ReceiveGossipEnabled = EXCLUDED.ReceiveGossipEnabled,
             ReceiveAuctionEnabled = EXCLUDED.ReceiveAuctionEnabled,
             WarnOnEvilEnabled = EXCLUDED.WarnOnEvilEnabled,
             LookStyleModern = EXCLUDED.LookStyleModern,
             ReceiveItemsEnabled = EXCLUDED.ReceiveItemsEnabled,
             ActionsEnabled = EXCLUDED.ActionsEnabled,
             IgnoredPlayers = EXCLUDED.IgnoredPlayers,
             SuicidePassword = EXCLUDED.SuicidePassword,
             KeepMode = EXCLUDED.KeepMode,
            DisconnectedWhilePlaying = EXCLUDED.DisconnectedWhilePlaying,
             QuestAbilities = EXCLUDED.QuestAbilities,
             BankBalances = EXCLUDED.BankBalances,
             ActiveSpells = EXCLUDED.ActiveSpells,
             DeathLog = EXCLUDED.DeathLog,
             PoisonLevel = EXCLUDED.PoisonLevel,
             LastCleanupUtc = EXCLUDED.LastCleanupUtc,
             Inventory = EXCLUDED.Inventory,
             Equipment = EXCLUDED.Equipment";

    public void SavePlayer(Player player)
        => ExecutePlayerUpsert(BuildPlayerSaveParameters(player));

    // Capture the player's CURRENT persisted state — the CPU-only half of a save (field reads + JSON
    // serialization of the mutable collections, no I/O) — into the bound upsert parameters, and return a
    // delegate that runs the database write off-gate. The write-behind flusher calls this while the world
    // gate is held (so the captured copy is consistent) and invokes the returned action after releasing.
    public Action CapturePlayerSave(Player player)
    {
        var parameters = BuildPlayerSaveParameters(player);
        return () => ExecutePlayerUpdateIfExists(parameters);
    }

    // The write-behind flush executes OFF the world gate, so it can race a hard character deletion
    // (reroll / permadeath DeletePlayer, which runs under the gate a moment later). A plain upsert
    // landing after the DELETE would re-INSERT the whole deleted character — and since login resolves
    // the account's character by BbsUserId, the resurrected row (old class, spellbook, inventory)
    // would win over the pending-reroll create screen. Deferred saves are therefore UPDATE-only:
    // lock the row, and if it is gone, drop the stale snapshot instead of recreating it. Synchronous
    // SavePlayer (creation/logout/death) keeps full upsert semantics.
    private void ExecutePlayerUpdateIfExists(NpgsqlParameter[] parameters)
    {
        string name = (string)parameters.First(p => p.ParameterName == "@name").Value!;

        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var check = conn.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = "SELECT 1 FROM Players WHERE Name = @name FOR UPDATE";
            AddCitextParameter(check, "@name", name);
            if (check.ExecuteScalar() == null)
                return; // deleted since capture — never resurrect
        }

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = PlayerUpsertSql;  // row exists and is locked → always the ON CONFLICT UPDATE path
        foreach (var parameter in parameters)
            cmd.Parameters.Add(parameter);
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    private void ExecutePlayerUpsert(NpgsqlParameter[] parameters)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = PlayerUpsertSql;
        foreach (var parameter in parameters)
            cmd.Parameters.Add(parameter);
        cmd.ExecuteNonQuery();
    }

    private static NpgsqlParameter Citext(string name, string? value)
        => new() { ParameterName = name, NpgsqlDbType = NpgsqlDbType.Citext, Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter P(string name, object? value)
        => new() { ParameterName = name, Value = value ?? DBNull.Value };

    // Builds the full, detached upsert parameter set from the player's current state. Mirrors the column
    // list / order of PlayerUpsertSql exactly; the only I/O-free work here (field reads + JSON) is what
    // makes a consistent under-gate snapshot cheap. Null scalars map to DBNull, matching AddWithValue.
    private static NpgsqlParameter[] BuildPlayerSaveParameters(Player player)
    {
        player.Name = Player.NormalizeNamePart(player.Name);
        player.LastName = Player.NormalizeNamePart(player.LastName);

        return new[]
        {
            Citext("@name", player.Name),
            P("@pass", player.PasswordHash),
            P("@race", player.RaceId),
            P("@class", player.ClassId),
            P("@level", player.Level),
            P("@exp", player.Experience),
            P("@hp", player.CurrentHP),
            P("@maxhp", player.MaxHP),
            P("@mana", player.CurrentMana),
            P("@maxmana", player.MaxMana),
            P("@spellcasting", player.SpellCasting),
            P("@str", player.Strength),
            P("@agl", player.Agility),
            P("@int", player.Intellect),
            P("@wil", player.Willpower),
            P("@hea", player.Health),
            P("@chm", player.Charm),
            P("@ac", player.ArmourClass),
            P("@dr", player.DamageResist),
            P("@mr", player.MagicResist),
            P("@map", player.CurrentMapNumber),
            P("@room", player.CurrentRoomNumber),
            P("@runic", player.Runic),
            P("@plat", player.Platinum),
            P("@gold", player.Gold),
            P("@silver", player.Silver),
            P("@copper", player.Copper),
            P("@cp", player.CharacterPoints),
            P("@scp", player.SpentCP),
            P("@align", player.Alignment),
            P("@evilpoints", player.EvilPoints),
            P("@islawful", player.IsLawful ? 1 : 0),
            P("@evilpointsforgiventoday", player.EvilPointsForgivenToday),
            P("@lastevilpointforgivenessdaynumber", player.LastEvilPointForgivenessDayNumber),
            P("@minevilpoints", player.MinEvilPoints),
            P("@lives", player.Lives),
            P("@thievery", player.Thievery),
            P("@traps", player.Traps),
            P("@picklocks", player.Picklocks),
            P("@tracking", player.Tracking),
            P("@martialarts", player.MartialArts),
            P("@basestr", player.BaseStrength),
            P("@baseagl", player.BaseAgility),
            P("@baseint", player.BaseIntellect),
            P("@basewil", player.BaseWillpower),
            P("@basehea", player.BaseHealth),
            P("@basechm", player.BaseCharm),
            P("@hairlength", player.HairLength),
            P("@haircolour", player.HairColour),
            P("@eyecolour", player.EyeColour),
            P("@gender", player.Gender),
            P("@lastname", player.LastName),
            Citext("@bbsuserid", player.BbsUserId),
            Citext("@gang", player.Gang),
            P("@gangexp", player.GangExperience),
            P("@ganglieutenant", player.IsGangLieutenant ? 1 : 0),
            P("@gangviewonline", player.GangViewOnlineOnly ? 1 : 0),
            P("@issysop", player.IsSysop ? 1 : 0),
            P("@testersysop", player.IsTesterSysop ? 1 : 0),
            P("@istestaccount", player.IsTestAccount ? 1 : 0),
            P("@topten", player.IsToptenDisabled ? 1 : 0),
            P("@talkmode", player.TalkMode),
            P("@broadcastchannel", player.BroadcastChannel),
            P("@briefmode", player.BriefMode ? 1 : 0),
            P("@paletteid", player.PaletteId),
            P("@statlinemode", player.StatlineMode),
            P("@customstatline", player.CustomStatline ?? string.Empty),
            P("@styletechnical", player.UseTechnicalStyle ? 1 : 0),
            P("@receivegossipenabled", player.ReceiveGossipEnabled ? 1 : 0),
            P("@receiveauctionenabled", player.ReceiveAuctionEnabled ? 1 : 0),
            P("@warnonevilenabled", player.WarnOnEvilEnabled ? 1 : 0),
            P("@lookstylemodern", player.UseModernLookStyle ? 1 : 0),
            P("@receiveitemsenabled", player.ReceiveItemsEnabled ? 1 : 0),
            P("@actionsenabled", player.ActionsEnabled ? 1 : 0),
            P("@ignoredplayers", JsonSerializer.Serialize(player.IgnoredPlayerNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList())),
            P("@suicidepassword", player.SuicideRerollPassword ?? ""),
            P("@keepmode", player.KeepMode ? 1 : 0),
            P("@disconnectedwhileplaying", player.DisconnectedWhilePlaying ? 1 : 0),
            P("@questabilities", JsonSerializer.Serialize(player.QuestAbilities)),
            P("@bankbalances", JsonSerializer.Serialize(player.BankBalances)),
            P("@activespells", JsonSerializer.Serialize(player.ActiveSpells)),
            P("@deathlog", JsonSerializer.Serialize(player.DeathLog)),
            P("@poisonlevel", player.PoisonLevel),
            P("@lastcleanuputc", player.LastCleanupAppliedUtc == DateTime.MinValue
                ? ""
                : player.LastCleanupAppliedUtc.ToString("o", CultureInfo.InvariantCulture)),
            P("@inv", JsonSerializer.Serialize(player.Inventory)),
            P("@equip", JsonSerializer.Serialize(player.Equipment)),
        };
    }

    public Player? LoadPlayer(string name, string password)
    {
        var player = LoadPlayerByName(name);
        if (player == null)
            return null;

        if (!CWGaming.Shared.BbsSecurity.VerifyPassword(password, player.PasswordHash))
            return null;

        // Transparent upgrade: re-hash legacy/weaker credentials with the current scheme on a
        // successful login so stored hashes strengthen over time without locking anyone out.
        if (CWGaming.Shared.BbsSecurity.NeedsRehash(player.PasswordHash))
        {
            string upgraded = HashPassword(password);
            if (StorePasswordHash(name, upgraded))
                player.PasswordHash = upgraded;
        }

        return player;
    }

    public Player? LoadPlayerByName(string name)
    {
        name = Player.NormalizeNamePart(name);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Players WHERE Name = @name";
        AddCitextParameter(cmd, "@name", name);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadPlayer(reader);
    }

    /// <summary>
    /// Load the character owned by a BBS account, keyed by the player's stored BBS user id. This is the
    /// authoritative account→character link (the BBS no longer stores a player name). A game account
    /// owns at most one live character (reroll/permadeath delete the row), so at most one row matches;
    /// if data ever drifted, the alphabetically-first name is returned deterministically.
    /// </summary>
    public Player? LoadPlayerByBbsUserId(string bbsUserId)
    {
        if (string.IsNullOrWhiteSpace(bbsUserId))
            return null;

        bbsUserId = Player.NormalizeNamePart(bbsUserId);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM Players WHERE BbsUserId = @bbsuserid ORDER BY Name LIMIT 1";
        AddCitextParameter(cmd, "@bbsuserid", bbsUserId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return ReadPlayer(reader);
    }

    public bool ResetPassword(string name, string password) => StorePasswordHash(name, HashPassword(password));

    /// <summary>Persist an already-computed password hash for a character.</summary>
    private bool StorePasswordHash(string name, string passwordHash)
    {
        name = Player.NormalizeNamePart(name);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Players
            SET PasswordHash = @pass
            WHERE Name = @name";
        AddCitextParameter(cmd, "@name", name);
        cmd.Parameters.AddWithValue("@pass", passwordHash);

        return cmd.ExecuteNonQuery() > 0;
    }

    public void AppendChannelMessage(string sender, string channel, string message)
    {
        using var conn = OpenConnection();

        long id;
        DateTimeOffset createdAt;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO GossipLog (Sender, Channel, Message) VALUES (@sender, @channel, @message) RETURNING Id, CreatedAt";
            cmd.Parameters.AddWithValue("@sender", sender);
            cmd.Parameters.AddWithValue("@channel", channel);
            cmd.Parameters.AddWithValue("@message", message);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            id = reader.GetInt64(0);
            createdAt = reader.GetFieldValue<DateTimeOffset>(1);
        }

        // Live push to the web scroll (SSE), mirroring the web POST path. Best-effort like the insert.
        NotifyGossipFeed(conn, id, sender, channel, message, createdAt);

        // Enforce both caps every so often rather than on every insert.
        if (Interlocked.Increment(ref _gossipInsertCount) % GossipPruneEvery == 0)
        {
            using var prune = conn.CreateCommand();
            prune.CommandText = @"
                DELETE FROM GossipLog
                WHERE CreatedAt < now() - make_interval(days => @days)
                   OR Id <= COALESCE((SELECT Id FROM GossipLog ORDER BY Id DESC OFFSET @cap LIMIT 1), 0)";
            prune.Parameters.AddWithValue("@days", GossipMaxAgeDays);
            prune.Parameters.AddWithValue("@cap", GossipRowCap);
            prune.ExecuteNonQuery();
        }
    }

    // Push a newly-appended broadcast onto the 'gossip_feed' channel so the web explorer can render it
    // live (via its SSE stream) instead of waiting for a poll. Same JSON shape the web POST emits.
    private static void NotifyGossipFeed(NpgsqlConnection conn, long id, string sender, string channel, string message, DateTimeOffset createdAt)
    {
        string payload = JsonSerializer.Serialize(new
        {
            id,
            sender,
            channel,
            message,
            createdat = createdAt.ToUniversalTime().ToString("o"),
        });
        using var notify = conn.CreateCommand();
        notify.CommandText = "SELECT pg_notify('gossip_feed', @payload)";
        notify.Parameters.AddWithValue("@payload", payload);
        notify.ExecuteNonQuery();
    }

    public List<(string Name, string PasswordHash, bool IsTestAccount)> GetPlayersForBbsSync()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name, PasswordHash, IsTestAccount FROM Players ORDER BY Name";

        var rows = new List<(string Name, string PasswordHash, bool IsTestAccount)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("Name"))),
                reader.GetString(reader.GetOrdinal("PasswordHash")),
                reader.GetInt32(reader.GetOrdinal("IsTestAccount")) != 0));
        }

        return rows;
    }

    public List<(string Name, string LastName, string BbsUserId)> GetPlayerDirectory(string mask = "")
    {
        return GetPlayerDirectory(mask, includeTestAccounts: false);
    }

    internal List<(string Name, string LastName, string BbsUserId)> GetPlayerDirectoryIncludingTests(string mask = "")
    {
        return GetPlayerDirectory(mask, includeTestAccounts: true);
    }

    private List<(string Name, string LastName, string BbsUserId)> GetPlayerDirectory(string mask, bool includeTestAccounts)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(mask))
        {
            cmd.CommandText = includeTestAccounts
                ? "SELECT Name, LastName, BbsUserId FROM Players ORDER BY Name"
                : "SELECT Name, LastName, BbsUserId FROM Players WHERE IsTestAccount = 0 ORDER BY Name";
        }
        else
        {
            cmd.CommandText = includeTestAccounts
                ? @"
                SELECT Name, LastName, BbsUserId
                FROM Players
                WHERE Name ILIKE @mask OR LastName ILIKE @mask OR BbsUserId ILIKE @mask
                ORDER BY Name"
                : @"
                SELECT Name, LastName, BbsUserId
                FROM Players
                WHERE IsTestAccount = 0
                  AND (Name ILIKE @mask OR LastName ILIKE @mask OR BbsUserId ILIKE @mask)
                ORDER BY Name";
            cmd.Parameters.AddWithValue("@mask", $"%{mask}%");
        }

        var rows = new List<(string Name, string LastName, string BbsUserId)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("Name"))),
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("LastName"))),
                reader.GetString(reader.GetOrdinal("BbsUserId"))));
        }

        return rows;
    }

    public List<(string Name, string LastName, int ClassId, string Gang, long Experience)> GetTopPlayers(int limit = 10, int classId = 0)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        string classFilter = classId > 0 ? " AND ClassId = @classId" : string.Empty;
        if (limit > 0)
        {
            cmd.CommandText = $@"
                SELECT Name, LastName, ClassId, Gang, Experience
                FROM Players
                WHERE ToptenDisabled = 0
                    AND IsTestAccount = 0
                    AND IsSysop = 0{classFilter}
                ORDER BY Experience DESC, Name
                LIMIT @limit";
            cmd.Parameters.AddWithValue("@limit", limit);
        }
        else
        {
            cmd.CommandText = $@"
                SELECT Name, LastName, ClassId, Gang, Experience
                FROM Players
                WHERE ToptenDisabled = 0
                    AND IsTestAccount = 0
                    AND IsSysop = 0{classFilter}
                ORDER BY Experience DESC, Name";
        }

        if (classId > 0)
            cmd.Parameters.AddWithValue("@classId", classId);

        var rows = new List<(string Name, string LastName, int ClassId, string Gang, long Experience)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("Name"))),
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("LastName"))),
                reader.GetInt32(reader.GetOrdinal("ClassId")),
                reader.GetString(reader.GetOrdinal("Gang")),
                reader.GetInt64(reader.GetOrdinal("Experience"))));
        }

        return rows;
    }

    public List<(string GangName, string LeaderName, int Members, string Created, long Experience)> GetTopGangs(int limit = 10)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        string limitClause = limit > 0 ? "LIMIT @limit" : string.Empty;
        cmd.CommandText = $@"
            WITH GangStats AS (
                SELECT
                    p.Gang AS GangName,
                    COUNT(*) AS Members,
                    SUM(p.GangExperience) AS Experience,
                    MIN(COALESCE(p.CreatedAt, CURRENT_TIMESTAMP::TEXT)) AS CreatedAt
                FROM Players p
                LEFT JOIN GangSettings gs ON gs.Name = p.Gang
                WHERE TRIM(p.Gang) <> ''
                  AND p.ToptenDisabled = 0
                  AND p.IsTestAccount = 0
                  AND COALESCE(gs.ToptenDisabled, 0) = 0
                  AND p.IsSysop = 0
                GROUP BY p.Gang
            )
            SELECT
                s.GangName,
                COALESCE(gs.LeaderName, '') AS LeaderName,
                s.Members,
                COALESCE(s.CreatedAt, '') AS Created,
                s.Experience
            FROM GangStats s
            LEFT JOIN GangSettings gs ON gs.Name = s.GangName
            ORDER BY s.Experience DESC, s.GangName
            {limitClause}";

        if (limit > 0)
            cmd.Parameters.AddWithValue("@limit", limit);

        var rows = new List<(string GangName, string LeaderName, int Members, string Created, long Experience)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(reader.GetOrdinal("GangName")),
                reader.GetString(reader.GetOrdinal("LeaderName")),
                reader.GetInt32(reader.GetOrdinal("Members")),
                reader.GetString(reader.GetOrdinal("Created")),
                reader.GetInt64(reader.GetOrdinal("Experience"))));
        }

        return rows;
    }

    private Player ReadPlayer(DbDataReader reader)
    {
        var player = new Player
        {
            Name = reader.GetString(reader.GetOrdinal("Name")),
            PasswordHash = reader.GetString(reader.GetOrdinal("PasswordHash")),
            RaceId = reader.GetInt32(reader.GetOrdinal("RaceId")),
            ClassId = reader.GetInt32(reader.GetOrdinal("ClassId")),
            Level = reader.GetInt32(reader.GetOrdinal("Level")),
            Experience = reader.GetInt64(reader.GetOrdinal("Experience")),
            CurrentHP = reader.GetInt32(reader.GetOrdinal("CurrentHP")),
            MaxHP = reader.GetInt32(reader.GetOrdinal("MaxHP")),
            CurrentMana = reader.GetInt32(reader.GetOrdinal("CurrentMana")),
            MaxMana = reader.GetInt32(reader.GetOrdinal("MaxMana")),
            SpellCasting = reader.GetInt32(reader.GetOrdinal("SpellCasting")),
            Strength = reader.GetInt32(reader.GetOrdinal("Strength")),
            Agility = reader.GetInt32(reader.GetOrdinal("Agility")),
            Intellect = reader.GetInt32(reader.GetOrdinal("Intellect")),
            Willpower = reader.GetInt32(reader.GetOrdinal("Willpower")),
            Health = reader.GetInt32(reader.GetOrdinal("Health")),
            Charm = reader.GetInt32(reader.GetOrdinal("Charm")),
            ArmourClass = reader.GetInt32(reader.GetOrdinal("ArmourClass")),
            DamageResist = reader.GetInt32(reader.GetOrdinal("DamageResist")),
            MagicResist = reader.GetInt32(reader.GetOrdinal("MagicResist")),
            CurrentMapNumber = reader.GetInt32(reader.GetOrdinal("CurrentMap")),
            CurrentRoomNumber = reader.GetInt32(reader.GetOrdinal("CurrentRoom")),
            Runic = reader.GetInt32(reader.GetOrdinal("Runic")),
            Platinum = reader.GetInt32(reader.GetOrdinal("Platinum")),
            Gold = reader.GetInt32(reader.GetOrdinal("Gold")),
            Silver = reader.GetInt32(reader.GetOrdinal("Silver")),
            Copper = reader.GetInt32(reader.GetOrdinal("Copper")),
            CharacterPoints = reader.GetInt32(reader.GetOrdinal("CharacterPoints")),
            SpentCP = reader.GetInt32(reader.GetOrdinal("SpentCP")),
            Alignment = reader.GetInt32(reader.GetOrdinal("Alignment")),
            EvilPoints = reader.GetFloat(reader.GetOrdinal("EvilPoints")),
            IsLawful = reader.GetInt32(reader.GetOrdinal("IsLawful")) != 0,
            EvilPointsForgivenToday = reader.GetFloat(reader.GetOrdinal("EvilPointsForgivenToday")),
            LastEvilPointForgivenessDayNumber = reader.GetInt32(reader.GetOrdinal("LastEvilPointForgivenessDayNumber")),
            MinEvilPoints = reader.GetFloat(reader.GetOrdinal("MinEvilPoints")),
            Lives = reader.GetInt32(reader.GetOrdinal("Lives")),
            Thievery = reader.GetInt32(reader.GetOrdinal("Thievery")),
            Traps = reader.GetInt32(reader.GetOrdinal("Traps")),
            Picklocks = reader.GetInt32(reader.GetOrdinal("Picklocks")),
            Tracking = reader.GetInt32(reader.GetOrdinal("Tracking")),
            MartialArts = reader.GetInt32(reader.GetOrdinal("MartialArts")),
            BaseStrength = reader.GetInt32(reader.GetOrdinal("BaseStrength")),
            BaseAgility = reader.GetInt32(reader.GetOrdinal("BaseAgility")),
            BaseIntellect = reader.GetInt32(reader.GetOrdinal("BaseIntellect")),
            BaseWillpower = reader.GetInt32(reader.GetOrdinal("BaseWillpower")),
            BaseHealth = reader.GetInt32(reader.GetOrdinal("BaseHealth")),
            BaseCharm = reader.GetInt32(reader.GetOrdinal("BaseCharm")),
            HairLength = reader.GetInt32(reader.GetOrdinal("HairLength")),
            HairColour = reader.GetInt32(reader.GetOrdinal("HairColour")),
            EyeColour = reader.GetInt32(reader.GetOrdinal("EyeColour")),
            Gender = reader.GetInt32(reader.GetOrdinal("Gender")),
            LastName = reader.GetString(reader.GetOrdinal("LastName")),
            BbsUserId = reader.GetString(reader.GetOrdinal("BbsUserId")),
            Gang = reader.GetString(reader.GetOrdinal("Gang")),
            GangExperience = reader.GetInt64(reader.GetOrdinal("GangExperience")),
            IsGangLieutenant = reader.GetInt32(reader.GetOrdinal("GangLieutenant")) != 0,
            GangViewOnlineOnly = reader.GetInt32(reader.GetOrdinal("GangViewOnlineOnly")) != 0,
            IsSysop = reader.GetInt32(reader.GetOrdinal("IsSysop")) != 0,
            IsTesterSysop = reader.GetInt32(reader.GetOrdinal("TesterSysop")) != 0,
            IsTestAccount = reader.GetInt32(reader.GetOrdinal("IsTestAccount")) != 0,
            IsToptenDisabled = reader.GetInt32(reader.GetOrdinal("ToptenDisabled")) != 0,
            TalkMode = reader.GetInt32(reader.GetOrdinal("TalkMode")),
            BroadcastChannel = reader.GetInt32(reader.GetOrdinal("BroadcastChannel")),
            BriefMode = reader.GetInt32(reader.GetOrdinal("BriefMode")) != 0,
            PaletteId = reader.GetInt32(reader.GetOrdinal("PaletteId")),
            StatlineMode = reader.GetInt32(reader.GetOrdinal("StatlineMode")),
            CustomStatline = reader.IsDBNull(reader.GetOrdinal("CustomStatline")) ? string.Empty : reader.GetString(reader.GetOrdinal("CustomStatline")),
            UseTechnicalStyle = reader.GetInt32(reader.GetOrdinal("StyleTechnical")) != 0,
            ReceiveGossipEnabled = reader.GetInt32(reader.GetOrdinal("ReceiveGossipEnabled")) != 0,
            ReceiveAuctionEnabled = reader.GetInt32(reader.GetOrdinal("ReceiveAuctionEnabled")) != 0,
            WarnOnEvilEnabled = reader.GetInt32(reader.GetOrdinal("WarnOnEvilEnabled")) != 0,
            UseModernLookStyle = reader.GetInt32(reader.GetOrdinal("LookStyleModern")) != 0,
            ReceiveItemsEnabled = reader.GetInt32(reader.GetOrdinal("ReceiveItemsEnabled")) != 0,
            ActionsEnabled = reader.GetInt32(reader.GetOrdinal("ActionsEnabled")) != 0,
            SuicideRerollPassword = reader.GetString(reader.GetOrdinal("SuicidePassword")),
            KeepMode = reader.IsDBNull(reader.GetOrdinal("KeepMode")) ? true : reader.GetInt32(reader.GetOrdinal("KeepMode")) != 0,
            DisconnectedWhilePlaying = !reader.IsDBNull(reader.GetOrdinal("DisconnectedWhilePlaying"))
                && reader.GetInt32(reader.GetOrdinal("DisconnectedWhilePlaying")) != 0,
        };

        var ignoredPlayers = reader.IsDBNull(reader.GetOrdinal("IgnoredPlayers"))
            ? "[]"
            : reader.GetString(reader.GetOrdinal("IgnoredPlayers"));
        player.IgnoredPlayerNames = new HashSet<string>(
            JsonSerializer.Deserialize<List<string>>(ignoredPlayers) ?? [],
            StringComparer.OrdinalIgnoreCase);

        var questAbilities = reader.IsDBNull(reader.GetOrdinal("QuestAbilities"))
            ? "{}"
            : reader.GetString(reader.GetOrdinal("QuestAbilities"));
        player.QuestAbilities = JsonSerializer.Deserialize<Dictionary<int, int>>(questAbilities) ?? [];

        var bankBalances = reader.IsDBNull(reader.GetOrdinal("BankBalances"))
            ? "{}"
            : reader.GetString(reader.GetOrdinal("BankBalances"));
        player.BankBalances = JsonSerializer.Deserialize<Dictionary<int, long>>(bankBalances) ?? [];

        // Timed spell slots + poison accumulator survive logout/restart (anti-exploit: no shedding a
        // DoT/debuff/jail by reconnecting). Columns are guaranteed by the startup bootstrap migration.
        var activeSpells = reader.IsDBNull(reader.GetOrdinal("ActiveSpells"))
            ? "[]"
            : reader.GetString(reader.GetOrdinal("ActiveSpells"));
        player.ActiveSpells = JsonSerializer.Deserialize<List<ActiveSpell>>(activeSpells) ?? [];

        var deathLog = reader.IsDBNull(reader.GetOrdinal("DeathLog"))
            ? "[]"
            : reader.GetString(reader.GetOrdinal("DeathLog"));
        player.DeathLog = JsonSerializer.Deserialize<List<DeathRecord>>(deathLog) ?? [];

        player.PoisonLevel = reader.IsDBNull(reader.GetOrdinal("PoisonLevel"))
            ? 0
            : reader.GetInt32(reader.GetOrdinal("PoisonLevel"));

        // The last daily cleanup applied to this character ('' = never). Drives the login-time
        // catch-up recharge so it runs once per cleanup, not once per reconnect.
        var lastCleanup = reader.IsDBNull(reader.GetOrdinal("LastCleanupUtc"))
            ? ""
            : reader.GetString(reader.GetOrdinal("LastCleanupUtc"));
        player.LastCleanupAppliedUtc = DateTime.TryParse(
            lastCleanup, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedLastCleanup)
            ? parsedLastCleanup
            : DateTime.MinValue;

        var inv = reader.GetString(reader.GetOrdinal("Inventory"));
        player.Inventory = JsonSerializer.Deserialize<List<int>>(inv) ?? [];

        var equip = reader.GetString(reader.GetOrdinal("Equipment"));
        player.Equipment = JsonSerializer.Deserialize<Dictionary<string, int>>(equip) ?? [];

        if (player.BaseStrength == 0 && player.Strength > 0)
        {
            player.BaseStrength = player.Strength;
            player.BaseAgility = player.Agility;
            player.BaseIntellect = player.Intellect;
            player.BaseWillpower = player.Willpower;
            player.BaseHealth = player.Health;
            player.BaseCharm = player.Charm;
        }

        return player;
    }

    public List<(string Name, string LastName)> GetPlayersNotTopten()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name, LastName FROM Players WHERE ToptenDisabled = 1 AND IsTestAccount = 0 ORDER BY Name";

        var rows = new List<(string Name, string LastName)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("Name"))),
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("LastName")))));
        }

        return rows;
    }

    public List<(string Name, string LastName)> GetPlayersByGang(string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Name, LastName
            FROM Players
                        WHERE Gang = @gang
                            AND IsTestAccount = 0
            ORDER BY Name";
        AddCitextParameter(cmd, "@gang", gangName);

        var rows = new List<(string Name, string LastName)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("Name"))),
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("LastName")))));
        }

        return rows;
    }

    public int DisbandGang(string gangName)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using var clearGang = conn.CreateCommand();
        clearGang.Transaction = tx;
        clearGang.CommandText = "UPDATE Players SET Gang = '', GangExperience = 0, GangLieutenant = 0 WHERE Gang = @gang";
        AddCitextParameter(clearGang, "@gang", gangName);
        int affected = clearGang.ExecuteNonQuery();

        using var deleteSettings = conn.CreateCommand();
        deleteSettings.Transaction = tx;
        deleteSettings.CommandText = "DELETE FROM GangSettings WHERE Name = @gang";
        AddCitextParameter(deleteSettings, "@gang", gangName);
        deleteSettings.ExecuteNonQuery();

        using var deleteInvites = conn.CreateCommand();
        deleteInvites.Transaction = tx;
        deleteInvites.CommandText = "DELETE FROM GangInvites WHERE GangName = @gang";
        AddCitextParameter(deleteInvites, "@gang", gangName);
        deleteInvites.ExecuteNonQuery();

        tx.Commit();
        return affected;
    }

    public void SetGangToptenDisabled(string gangName, bool disabled)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GangSettings (Name, ToptenDisabled, MaxSize, LeaderName)
            VALUES (@name, @disabled, 9, '')
            ON CONFLICT(Name) DO UPDATE SET ToptenDisabled = EXCLUDED.ToptenDisabled";
        AddCitextParameter(cmd, "@name", gangName);
        cmd.Parameters.AddWithValue("@disabled", disabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void SetGangMaxSize(string gangName, int maxSize)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GangSettings (Name, ToptenDisabled, MaxSize, LeaderName)
            VALUES (@name, 0, @maxSize, '')
            ON CONFLICT(Name) DO UPDATE SET MaxSize = EXCLUDED.MaxSize";
        AddCitextParameter(cmd, "@name", gangName);
        cmd.Parameters.AddWithValue("@maxSize", maxSize);
        cmd.ExecuteNonQuery();
    }

    public bool GangExists(string gangName)
    {
        using var conn = OpenConnection();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM Players
            WHERE Gang = @gang
            LIMIT 1";
        AddCitextParameter(cmd, "@gang", gangName);

        if (cmd.ExecuteScalar() != null)
            return true;

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = @"
            SELECT 1
            FROM GangSettings
            WHERE Name = @gang
            LIMIT 1";
        AddCitextParameter(cmd2, "@gang", gangName);
        return cmd2.ExecuteScalar() != null;
    }

    public int GetGangMemberCount(string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM Players WHERE Gang = @gang";
        AddCitextParameter(cmd, "@gang", gangName);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public int GetGangMaxSize(string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MaxSize FROM GangSettings WHERE Name = @gang";
        AddCitextParameter(cmd, "@gang", gangName);
        var scalar = cmd.ExecuteScalar();
        if (scalar == null)
            return 9;

        return Math.Max(1, Convert.ToInt32(scalar));
    }

    public bool CreateGangRecord(string gangName, int maxSize = 9, string leaderName = "")
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GangSettings (Name, ToptenDisabled, MaxSize, LeaderName)
            VALUES (@name, 0, @maxSize, @leaderName)
            ON CONFLICT (Name) DO NOTHING";
        AddCitextParameter(cmd, "@name", gangName);
        cmd.Parameters.AddWithValue("@maxSize", Math.Max(1, maxSize));
        AddCitextParameter(cmd, "@leaderName", leaderName);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool IsGangLeader(string playerName, string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT LeaderName
            FROM GangSettings
            WHERE Name = @gang
            LIMIT 1";
        AddCitextParameter(cmd, "@gang", gangName);
        var leaderName = cmd.ExecuteScalar() as string;

        if (!string.IsNullOrWhiteSpace(leaderName))
            return leaderName.Equals(playerName, StringComparison.OrdinalIgnoreCase);

        using var fallback = conn.CreateCommand();
        fallback.CommandText = @"
            SELECT Name
            FROM Players
            WHERE Gang = @gang
            ORDER BY Experience DESC, Name
            LIMIT 1";
        AddCitextParameter(fallback, "@gang", gangName);
        var derivedLeader = fallback.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(derivedLeader))
            return false;

        return derivedLeader.Equals(playerName, StringComparison.OrdinalIgnoreCase);
    }

    public void AddGangInvite(string inviteeName, string gangName, string inviterName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GangInvites (InviteeName, GangName, InviterName)
            VALUES (@invitee, @gang, @inviter)
            ON CONFLICT(InviteeName, GangName) DO UPDATE SET
                InviterName = EXCLUDED.InviterName,
                CreatedAt = CURRENT_TIMESTAMP::TEXT";
        AddCitextParameter(cmd, "@invitee", inviteeName);
        AddCitextParameter(cmd, "@gang", gangName);
        AddCitextParameter(cmd, "@inviter", inviterName);
        cmd.ExecuteNonQuery();
    }

    public bool HasGangInvite(string inviteeName, string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM GangInvites
            WHERE InviteeName = @invitee
              AND GangName = @gang
            LIMIT 1";
        AddCitextParameter(cmd, "@invitee", inviteeName);
        AddCitextParameter(cmd, "@gang", gangName);
        return cmd.ExecuteScalar() != null;
    }

    public void RemoveGangInvite(string inviteeName, string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM GangInvites
            WHERE InviteeName = @invitee
              AND GangName = @gang";
        AddCitextParameter(cmd, "@invitee", inviteeName);
        AddCitextParameter(cmd, "@gang", gangName);
        cmd.ExecuteNonQuery();
    }

    public bool RemovePlayerFromGang(string playerName, string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Players
            SET Gang = '', GangExperience = 0, GangLieutenant = 0
            WHERE Name = @name
              AND Gang = @gang";
        AddCitextParameter(cmd, "@name", playerName);
        AddCitextParameter(cmd, "@gang", gangName);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool IsGangLieutenant(string playerName, string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 1
            FROM Players
            WHERE Name = @name
              AND Gang = @gang
              AND GangLieutenant = 1
            LIMIT 1";
        AddCitextParameter(cmd, "@name", playerName);
        AddCitextParameter(cmd, "@gang", gangName);
        return cmd.ExecuteScalar() != null;
    }

    public bool SetGangLieutenant(string playerName, string gangName, bool isLieutenant)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE Players
            SET GangLieutenant = @rank
            WHERE Name = @name
              AND Gang = @gang";
        cmd.Parameters.AddWithValue("@rank", isLieutenant ? 1 : 0);
        AddCitextParameter(cmd, "@name", playerName);
        AddCitextParameter(cmd, "@gang", gangName);
        return cmd.ExecuteNonQuery() > 0;
    }

    // The PROMOTE/DEMOTE offline branch: when the realm-wide lookup turns up nobody online,
    // stock falls back to a lookup in the user file and reads that record's gang field
    // and name. Three outcomes have to stay apart — no such user, a user in some OTHER gang, and
    // a user in YOUR gang — because each gets a different message. A reader (not ExecuteScalar) so an
    // absent row is distinguishable from a present row holding a NULL gang.
    public (string Name, string Gang)? GetPlayerNameAndGang(string playerName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name, Gang FROM Players WHERE Name = @name LIMIT 1";
        AddCitextParameter(cmd, "@name", playerName);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
    }

    public List<(string Name, string LastName, int Level, int RaceId, int ClassId, bool IsLieutenant)> GetGangRoster(string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Name, LastName, Level, RaceId, ClassId, GangLieutenant
            FROM Players
                        WHERE Gang = @gang
                            AND IsTestAccount = 0
            ORDER BY Level DESC, Name";
        AddCitextParameter(cmd, "@gang", gangName);

        var rows = new List<(string Name, string LastName, int Level, int RaceId, int ClassId, bool IsLieutenant)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("Name"))),
                Player.NormalizeNamePart(reader.GetString(reader.GetOrdinal("LastName"))),
                reader.GetInt32(reader.GetOrdinal("Level")),
                reader.GetInt32(reader.GetOrdinal("RaceId")),
                reader.GetInt32(reader.GetOrdinal("ClassId")),
                reader.GetInt32(reader.GetOrdinal("GangLieutenant")) != 0));
        }

        return rows;
    }

    public string? GetGangLeaderName(string gangName)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT LeaderName
            FROM GangSettings
            WHERE Name = @gang
            LIMIT 1";
        AddCitextParameter(cmd, "@gang", gangName);
        var leader = cmd.ExecuteScalar() as string;
        if (!string.IsNullOrWhiteSpace(leader))
            return leader;

        using var fallback = conn.CreateCommand();
        fallback.CommandText = @"
            SELECT Name
            FROM Players
            WHERE Gang = @gang
            ORDER BY Experience DESC, Name
            LIMIT 1";
        AddCitextParameter(fallback, "@gang", gangName);
        return fallback.ExecuteScalar() as string;
    }

    public string? ResolveGangName(string gangName)
    {
        using var conn = OpenConnection();

        using var cmdSettings = conn.CreateCommand();
        cmdSettings.CommandText = @"
            SELECT Name
            FROM GangSettings
            WHERE Name = @gang
            LIMIT 1";
        AddCitextParameter(cmdSettings, "@gang", gangName);
        var fromSettings = cmdSettings.ExecuteScalar() as string;
        if (!string.IsNullOrWhiteSpace(fromSettings))
            return fromSettings;

        using var cmdPlayers = conn.CreateCommand();
        cmdPlayers.CommandText = @"
            SELECT Gang
            FROM Players
            WHERE Gang = @gang
            LIMIT 1";
        AddCitextParameter(cmdPlayers, "@gang", gangName);
        return cmdPlayers.ExecuteScalar() as string;
    }

    public bool RenameGang(string oldName, string newName)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using var existsOld = conn.CreateCommand();
        existsOld.Transaction = tx;
        existsOld.CommandText = @"
            SELECT 1
            FROM Players
            WHERE Gang = @old
            LIMIT 1";
        AddCitextParameter(existsOld, "@old", oldName);
        bool oldInPlayers = existsOld.ExecuteScalar() != null;

        using var existsOldSettings = conn.CreateCommand();
        existsOldSettings.Transaction = tx;
        existsOldSettings.CommandText = @"
            SELECT 1
            FROM GangSettings
            WHERE Name = @old
            LIMIT 1";
        AddCitextParameter(existsOldSettings, "@old", oldName);
        bool oldInSettings = existsOldSettings.ExecuteScalar() != null;

        if (!oldInPlayers && !oldInSettings)
            return false;

        using var existsNewPlayers = conn.CreateCommand();
        existsNewPlayers.Transaction = tx;
        existsNewPlayers.CommandText = @"
            SELECT 1
            FROM Players
            WHERE Gang = @new
            LIMIT 1";
        AddCitextParameter(existsNewPlayers, "@new", newName);
        bool newExistsPlayers = existsNewPlayers.ExecuteScalar() != null;

        using var existsNewSettings = conn.CreateCommand();
        existsNewSettings.Transaction = tx;
        existsNewSettings.CommandText = @"
            SELECT 1
            FROM GangSettings
            WHERE Name = @new
            LIMIT 1";
        AddCitextParameter(existsNewSettings, "@new", newName);
        bool newExistsSettings = existsNewSettings.ExecuteScalar() != null;

        if (newExistsPlayers || newExistsSettings)
            return false;

        using var updatePlayers = conn.CreateCommand();
        updatePlayers.Transaction = tx;
        updatePlayers.CommandText = "UPDATE Players SET Gang = @new WHERE Gang = @old";
        AddCitextParameter(updatePlayers, "@new", newName);
        AddCitextParameter(updatePlayers, "@old", oldName);
        updatePlayers.ExecuteNonQuery();

        using var updateSettings = conn.CreateCommand();
        updateSettings.Transaction = tx;
        updateSettings.CommandText = "UPDATE GangSettings SET Name = @new WHERE Name = @old";
        AddCitextParameter(updateSettings, "@new", newName);
        AddCitextParameter(updateSettings, "@old", oldName);
        int settingsUpdated = updateSettings.ExecuteNonQuery();

        if (settingsUpdated == 0)
        {
            using var insertSettings = conn.CreateCommand();
            insertSettings.Transaction = tx;
            insertSettings.CommandText = @"
                INSERT INTO GangSettings (Name, ToptenDisabled, MaxSize, LeaderName)
                VALUES (@new, 0, 9, '')";
            AddCitextParameter(insertSettings, "@new", newName);
            insertSettings.ExecuteNonQuery();
        }

        using var updateInvites = conn.CreateCommand();
        updateInvites.Transaction = tx;
        updateInvites.CommandText = "UPDATE GangInvites SET GangName = @new WHERE GangName = @old";
        AddCitextParameter(updateInvites, "@new", newName);
        AddCitextParameter(updateInvites, "@old", oldName);
        updateInvites.ExecuteNonQuery();

        tx.Commit();
        return true;
    }

    // Rename a character everywhere it is stored, atomically. Renames the Players row (the CITEXT primary
    // key) AND re-points every other record that referenced the old name so nothing is orphaned: gang
    // leadership/invites/house ownership and the leaderboard + bug-report attributions. Returns false if
    // newName is already taken or oldName does not exist. (The account→character link needs no update: it
    // lives on this row as BbsUserId, which the rename never touches.)
    public bool RenamePlayer(string oldName, string newName)
    {
        oldName = Player.NormalizeNamePart(oldName);
        newName = Player.NormalizeNamePart(newName);

        using var conn = OpenConnection();

        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM Players WHERE Name = @name";
            AddCitextParameter(check, "@name", newName);
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0)
                return false;
        }

        using var tx = conn.BeginTransaction();

        int renamed;
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE Players SET Name = @new WHERE Name = @old";
            AddCitextParameter(cmd, "@new", newName);
            AddCitextParameter(cmd, "@old", oldName);
            renamed = cmd.ExecuteNonQuery();
        }
        if (renamed == 0)
        {
            tx.Rollback();
            return false;
        }

        // KillerName is a plain TEXT column (killer may be a monster), so match it case-insensitively.
        foreach (var sql in new[]
        {
            "UPDATE GangSettings SET LeaderName = @new WHERE LeaderName = @old",
            "UPDATE GangInvites  SET InviteeName = @new WHERE InviteeName = @old",
            "UPDATE GangInvites  SET InviterName = @new WHERE InviterName = @old",
            "UPDATE GangHouses   SET OwnerPlayer = @new WHERE OwnerPlayer = @old",
            "UPDATE HallOfFame   SET PlayerName = @new WHERE PlayerName = @old",
            "UPDATE HallOfFame   SET KillerName = @new WHERE KillerName::citext = @old",
            "UPDATE BugReports   SET ReporterPlayerName = @new WHERE ReporterPlayerName = @old",
            "UPDATE BugReports   SET VerifiedByPlayerName = @new WHERE VerifiedByPlayerName = @old",
        })
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            AddCitextParameter(cmd, "@new", newName);
            AddCitextParameter(cmd, "@old", oldName);
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    public int GetServerSettingInt(string key, int defaultValue)
    {
        var value = GetServerSettingText(key, string.Empty);
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    public string GetServerSettingText(string key, string defaultValue)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM ServerSettings WHERE Key = @key";
        AddCitextParameter(cmd, "@key", key);

        return cmd.ExecuteScalar() as string ?? defaultValue;
    }

    public void SetServerSettingInt(string key, int value)
    {
        SetServerSettingText(key, value.ToString());
    }

    public void SetServerSettingText(string key, string value)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO ServerSettings (Key, Value)
            VALUES (@key, @value)
            ON CONFLICT(Key) DO UPDATE SET Value = EXCLUDED.Value";
        AddCitextParameter(cmd, "@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<mmudreborn.Data.Models.GangHouseRecord> LoadGangHouses()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT HouseId, OwnerGang, OwnerPlayer, PurchasedAt, LastTaxAt FROM GangHouses ORDER BY HouseId";

        var rows = new List<mmudreborn.Data.Models.GangHouseRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new mmudreborn.Data.Models.GangHouseRecord
            {
                HouseId = reader.GetInt32(reader.GetOrdinal("HouseId")),
                OwnerGang = reader.GetString(reader.GetOrdinal("OwnerGang")),
                OwnerPlayer = reader.GetString(reader.GetOrdinal("OwnerPlayer")),
                PurchasedAt = reader.GetString(reader.GetOrdinal("PurchasedAt")),
                LastTaxAt = reader.GetString(reader.GetOrdinal("LastTaxAt")),
            });
        }

        return rows;
    }

    public void SaveGangHouse(mmudreborn.Data.Models.GangHouseRecord house)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GangHouses (HouseId, OwnerGang, OwnerPlayer, PurchasedAt, LastTaxAt)
            VALUES (@id, @gang, @player, @purchased, @tax)
            ON CONFLICT(HouseId) DO UPDATE SET
                OwnerGang = EXCLUDED.OwnerGang,
                OwnerPlayer = EXCLUDED.OwnerPlayer,
                PurchasedAt = EXCLUDED.PurchasedAt,
                LastTaxAt = EXCLUDED.LastTaxAt";
        cmd.Parameters.AddWithValue("@id", house.HouseId);
        AddCitextParameter(cmd, "@gang", house.OwnerGang ?? "");
        AddCitextParameter(cmd, "@player", house.OwnerPlayer ?? "");
        cmd.Parameters.AddWithValue("@purchased", house.PurchasedAt ?? "");
        cmd.Parameters.AddWithValue("@tax", house.LastTaxAt ?? "");
        cmd.ExecuteNonQuery();
    }

    public void DeleteGangHouse(int houseId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM GangHouses WHERE HouseId = @id";
        cmd.Parameters.AddWithValue("@id", houseId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<mmudreborn.Data.Models.GangShopRecord> LoadGangShops()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ShopId, MarkupPercent, Slots FROM GangShops ORDER BY ShopId";

        var rows = new List<mmudreborn.Data.Models.GangShopRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new mmudreborn.Data.Models.GangShopRecord
            {
                ShopId = reader.GetInt32(reader.GetOrdinal("ShopId")),
                MarkupPercent = reader.GetInt32(reader.GetOrdinal("MarkupPercent")),
                Slots = reader.GetString(reader.GetOrdinal("Slots")),
            });
        }

        return rows;
    }

    public void SaveGangShop(mmudreborn.Data.Models.GangShopRecord shop)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO GangShops (ShopId, MarkupPercent, Slots)
            VALUES (@id, @markup, @slots)
            ON CONFLICT(ShopId) DO UPDATE SET
                MarkupPercent = EXCLUDED.MarkupPercent,
                Slots = EXCLUDED.Slots";
        cmd.Parameters.AddWithValue("@id", shop.ShopId);
        cmd.Parameters.AddWithValue("@markup", shop.MarkupPercent);
        cmd.Parameters.AddWithValue("@slots", shop.Slots ?? "");
        cmd.ExecuteNonQuery();
    }

    public void DeleteGangShop(int shopId)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM GangShops WHERE ShopId = @id";
        cmd.Parameters.AddWithValue("@id", shopId);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<string> GetAllPlayerNames()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Players";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names;
    }

    public IReadOnlyList<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> LoadRoomGroundItems()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MapNumber, RoomNumber, Sequence, ItemId, IsHidden
            FROM RoomGroundItems
            ORDER BY MapNumber, RoomNumber, Sequence";

        var rows = new List<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt32(reader.GetOrdinal("MapNumber")),
                reader.GetInt32(reader.GetOrdinal("RoomNumber")),
                reader.GetInt32(reader.GetOrdinal("Sequence")),
                reader.GetInt32(reader.GetOrdinal("ItemId")),
                reader.GetInt32(reader.GetOrdinal("IsHidden")) != 0));
        }

        return rows;
    }

    public IReadOnlyList<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> LoadRoomGroundCurrency()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT MapNumber, RoomNumber, VisibleCopper, HiddenCopper, VisibleStacksJson, HiddenStacksJson, StaticInitialized
            FROM RoomGroundCurrency
            ORDER BY MapNumber, RoomNumber";

        var rows = new List<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetInt32(reader.GetOrdinal("MapNumber")),
                reader.GetInt32(reader.GetOrdinal("RoomNumber")),
                reader.GetInt64(reader.GetOrdinal("VisibleCopper")),
                reader.GetInt64(reader.GetOrdinal("HiddenCopper")),
                reader.IsDBNull(reader.GetOrdinal("VisibleStacksJson")) ? string.Empty : reader.GetString(reader.GetOrdinal("VisibleStacksJson")),
                reader.IsDBNull(reader.GetOrdinal("HiddenStacksJson")) ? string.Empty : reader.GetString(reader.GetOrdinal("HiddenStacksJson")),
                reader.GetInt32(reader.GetOrdinal("StaticInitialized")) != 0));
        }

        return rows;
    }

    public void SaveRoomGroundState(
        IEnumerable<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> itemRows,
        IEnumerable<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> currencyRows)
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();

        using (var deleteItems = conn.CreateCommand())
        {
            deleteItems.Transaction = tx;
            deleteItems.CommandText = "DELETE FROM RoomGroundItems";
            deleteItems.ExecuteNonQuery();
        }

        using (var deleteCurrency = conn.CreateCommand())
        {
            deleteCurrency.Transaction = tx;
            deleteCurrency.CommandText = "DELETE FROM RoomGroundCurrency";
            deleteCurrency.ExecuteNonQuery();
        }

        using (var insertItem = conn.CreateCommand())
        {
            insertItem.Transaction = tx;
            insertItem.CommandText = @"
                INSERT INTO RoomGroundItems (MapNumber, RoomNumber, Sequence, ItemId, IsHidden)
                VALUES (@map, @room, @sequence, @itemId, @isHidden)";
            insertItem.Parameters.Add(new NpgsqlParameter("@map", NpgsqlDbType.Integer));
            insertItem.Parameters.Add(new NpgsqlParameter("@room", NpgsqlDbType.Integer));
            insertItem.Parameters.Add(new NpgsqlParameter("@sequence", NpgsqlDbType.Integer));
            insertItem.Parameters.Add(new NpgsqlParameter("@itemId", NpgsqlDbType.Integer));
            insertItem.Parameters.Add(new NpgsqlParameter("@isHidden", NpgsqlDbType.Integer));

            foreach (var row in itemRows)
            {
                insertItem.Parameters["@map"].Value = row.MapNumber;
                insertItem.Parameters["@room"].Value = row.RoomNumber;
                insertItem.Parameters["@sequence"].Value = row.Sequence;
                insertItem.Parameters["@itemId"].Value = row.ItemId;
                insertItem.Parameters["@isHidden"].Value = row.IsHidden ? 1 : 0;
                insertItem.ExecuteNonQuery();
            }
        }

        using (var insertCurrency = conn.CreateCommand())
        {
            insertCurrency.Transaction = tx;
            insertCurrency.CommandText = @"
                INSERT INTO RoomGroundCurrency (MapNumber, RoomNumber, VisibleCopper, HiddenCopper, VisibleStacksJson, HiddenStacksJson, StaticInitialized)
                VALUES (@map, @room, @visible, @hidden, @visibleStacksJson, @hiddenStacksJson, @staticInitialized)";
            insertCurrency.Parameters.Add(new NpgsqlParameter("@map", NpgsqlDbType.Integer));
            insertCurrency.Parameters.Add(new NpgsqlParameter("@room", NpgsqlDbType.Integer));
            insertCurrency.Parameters.Add(new NpgsqlParameter("@visible", NpgsqlDbType.Bigint));
            insertCurrency.Parameters.Add(new NpgsqlParameter("@hidden", NpgsqlDbType.Bigint));
            insertCurrency.Parameters.Add(new NpgsqlParameter("@visibleStacksJson", NpgsqlDbType.Text));
            insertCurrency.Parameters.Add(new NpgsqlParameter("@hiddenStacksJson", NpgsqlDbType.Text));
            insertCurrency.Parameters.Add(new NpgsqlParameter("@staticInitialized", NpgsqlDbType.Integer));

            foreach (var row in currencyRows)
            {
                insertCurrency.Parameters["@map"].Value = row.MapNumber;
                insertCurrency.Parameters["@room"].Value = row.RoomNumber;
                insertCurrency.Parameters["@visible"].Value = row.VisibleCopper;
                insertCurrency.Parameters["@hidden"].Value = row.HiddenCopper;
                insertCurrency.Parameters["@visibleStacksJson"].Value = row.VisibleStacksJson;
                insertCurrency.Parameters["@hiddenStacksJson"].Value = row.HiddenStacksJson;
                insertCurrency.Parameters["@staticInitialized"].Value = row.StaticInitialized ? 1 : 0;
                insertCurrency.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    public void ClearRoomGroundState()
    {
        SaveRoomGroundState([], []);
    }

    // Full realm wipe for SYSOP BOARD RESET: delete every piece of persisted realm state in one
    // transaction so a fresh start is all-or-nothing — all player characters (their carried items + bank
    // travel on the Players row), pending rerolls, gangs + invites, gang-house ownership, gang shops,
    // ground item/currency drops, the hall of fame, and the persisted chat scrollback (gossip/auction log
    // + web telepaths) so a "fresh" realm doesn't surface pre-reset chatter. Server config (ServerSettings)
    // and support tickets (BugReports) are intentionally preserved, as are BBS accounts (bbs.users) — a
    // reset wipes CHARACTERS, not accounts, so players can still log back in.
    public void ResetRealmPersistence()
    {
        using var conn = OpenConnection();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"
            DELETE FROM Players;
            DELETE FROM PendingRerolls;
            DELETE FROM GangInvites;
            DELETE FROM GangSettings;
            DELETE FROM GangHouses;
            DELETE FROM GangShops;
            DELETE FROM RoomGroundItems;
            DELETE FROM RoomGroundCurrency;
            DELETE FROM HallOfFame;
            DELETE FROM GossipLog;
            -- web_telepaths is created lazily (first web telepath ever sent), so it may not exist on a
            -- realm that never used web telepaths. Guard the delete so a missing table can't abort the
            -- whole all-or-nothing wipe. (GossipLog is always created at bootstrap, so it needs no guard.)
            DO $$ BEGIN
                IF to_regclass('public.web_telepaths') IS NOT NULL THEN
                    DELETE FROM public.web_telepaths;
                END IF;
            END $$;";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    public void DeletePlayer(string name)
    {
        name = Player.NormalizeNamePart(name);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Players WHERE Name = @name";
        AddCitextParameter(cmd, "@name", name);
        cmd.ExecuteNonQuery();
    }

    public void SavePendingReroll(string bbsUserName, string preservedName, long keptExperience, bool isSysop, bool toptenDisabled, string suicideRerollPassword)
    {
        bbsUserName = Player.NormalizeNamePart(bbsUserName);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO PendingRerolls (BbsUserName, PreservedName, KeptExperience, PreservedIsSysop, PreservedToptenDisabled, PreservedSuicidePassword, CreatedAt)
            VALUES (@name, @prevname, @xp, @issysop, @topten, @suicidepassword, CURRENT_TIMESTAMP::TEXT)
            ON CONFLICT (BbsUserName) DO UPDATE SET
                PreservedName = EXCLUDED.PreservedName,
                KeptExperience = EXCLUDED.KeptExperience,
                PreservedIsSysop = EXCLUDED.PreservedIsSysop,
                PreservedToptenDisabled = EXCLUDED.PreservedToptenDisabled,
                PreservedSuicidePassword = EXCLUDED.PreservedSuicidePassword,
                CreatedAt = EXCLUDED.CreatedAt";
        cmd.Parameters.Add("@name", NpgsqlDbType.Citext).Value = bbsUserName;
        cmd.Parameters.Add("@prevname", NpgsqlDbType.Citext).Value = preservedName ?? "";
        cmd.Parameters.AddWithValue("@xp", keptExperience);
        cmd.Parameters.AddWithValue("@issysop", isSysop ? 1 : 0);
        cmd.Parameters.AddWithValue("@topten", toptenDisabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@suicidepassword", suicideRerollPassword ?? "");
        cmd.ExecuteNonQuery();
    }

    public mmudreborn.Data.Models.PendingRerollRecord? LoadPendingReroll(string bbsUserName)
    {
        bbsUserName = Player.NormalizeNamePart(bbsUserName);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT BbsUserName, PreservedName, KeptExperience, PreservedIsSysop, PreservedToptenDisabled, PreservedSuicidePassword, CreatedAt
            FROM PendingRerolls
            WHERE BbsUserName = @name";
        cmd.Parameters.Add("@name", NpgsqlDbType.Citext).Value = bbsUserName;
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new mmudreborn.Data.Models.PendingRerollRecord
        {
            BbsUserName = reader.GetString(0),
            PreservedName = reader.GetString(1),
            KeptExperience = reader.GetInt64(2),
            PreservedIsSysop = reader.GetInt32(3) != 0,
            PreservedToptenDisabled = reader.GetInt32(4) != 0,
            PreservedSuicideRerollPassword = reader.GetString(5),
            CreatedAt = reader.GetString(6),
        };
    }

    public void DeletePendingReroll(string bbsUserName)
    {
        bbsUserName = Player.NormalizeNamePart(bbsUserName);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PendingRerolls WHERE BbsUserName = @name";
        cmd.Parameters.Add("@name", NpgsqlDbType.Citext).Value = bbsUserName;
        cmd.ExecuteNonQuery();
    }

    public void SaveHallOfFameEntry(mmudreborn.Data.Models.HallOfFameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO HallOfFame
                (PlayerName, LastName, Level, RaceId, ClassId, Experience, Alignment, KillerName, CreatedAt)
            VALUES
                (@playerName, @lastName, @level, @raceId, @classId, @experience, @alignment, @killerName, CURRENT_TIMESTAMP::TEXT)";
        cmd.Parameters.Add("@playerName", NpgsqlDbType.Citext).Value = Player.NormalizeNamePart(entry.PlayerName ?? string.Empty);
        cmd.Parameters.AddWithValue("@lastName", entry.LastName ?? string.Empty);
        cmd.Parameters.AddWithValue("@level", entry.Level);
        cmd.Parameters.AddWithValue("@raceId", entry.RaceId);
        cmd.Parameters.AddWithValue("@classId", entry.ClassId);
        cmd.Parameters.AddWithValue("@experience", entry.Experience);
        cmd.Parameters.AddWithValue("@alignment", entry.Alignment);
        cmd.Parameters.AddWithValue("@killerName", entry.KillerName ?? string.Empty);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<mmudreborn.Data.Models.HallOfFameEntry> GetHallOfFameEntries(int limit = 20)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, PlayerName, LastName, Level, RaceId, ClassId, Experience, Alignment, KillerName, CreatedAt
            FROM HallOfFame
            ORDER BY Id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var rows = new List<mmudreborn.Data.Models.HallOfFameEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new mmudreborn.Data.Models.HallOfFameEntry
            {
                Id = reader.GetInt32(0),
                PlayerName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                LastName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                Level = reader.GetInt32(3),
                RaceId = reader.GetInt32(4),
                ClassId = reader.GetInt32(5),
                Experience = reader.GetInt64(6),
                Alignment = reader.GetInt32(7),
                KillerName = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                CreatedAt = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
            });
        }

        return rows;
    }

    public int CreateBugReport(mmudreborn.Data.Models.BugReportRecord report)
    {
        ArgumentNullException.ThrowIfNull(report);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO BugReports
                (ReporterBbsUserName, ReporterPlayerName, Title, BriefDescription, Description, LocationText, ItemName, CreatedAt)
            VALUES
                (@bbsUserName, @playerName, @title, @brief, @description, @location, @itemName, CURRENT_TIMESTAMP::TEXT)
            RETURNING Id";
        cmd.Parameters.Add("@bbsUserName", NpgsqlDbType.Citext).Value = Player.NormalizeNamePart(report.ReporterBbsUserName ?? string.Empty);
        cmd.Parameters.Add("@playerName", NpgsqlDbType.Citext).Value = Player.NormalizeNamePart(report.ReporterPlayerName ?? string.Empty);
        cmd.Parameters.AddWithValue("@title", (report.Title ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("@brief", (report.BriefDescription ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("@description", report.Description ?? string.Empty);
        cmd.Parameters.AddWithValue("@location", (report.LocationText ?? string.Empty).Trim());
        cmd.Parameters.AddWithValue("@itemName", (report.ItemName ?? string.Empty).Trim());

        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public IReadOnlyList<mmudreborn.Data.Models.BugReportSummary> GetBugReports(int limit = 50)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, BriefDescription, CreatedAt, Status
            FROM BugReports
            ORDER BY Id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var rows = new List<mmudreborn.Data.Models.BugReportSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new mmudreborn.Data.Models.BugReportSummary
            {
                Id = reader.GetInt32(0),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                BriefDescription = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                CreatedAt = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Status = ReadBugReportStatus(reader, 4),
            });
        }

        return rows;
    }

    public IReadOnlyList<mmudreborn.Data.Models.BugReportSummary> GetCompletedBugReportsForReporter(string reporterBbsUserName, string reporterPlayerName, int limit = 20)
    {
        reporterBbsUserName = Player.NormalizeNamePart(reporterBbsUserName ?? string.Empty);
        reporterPlayerName = Player.NormalizeNamePart(reporterPlayerName ?? string.Empty);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, BriefDescription, CreatedAt, Status
            FROM BugReports
            WHERE Status = @completeStatus
              AND ((@bbsUserName <> '' AND ReporterBbsUserName = @bbsUserName)
                OR (@playerName <> '' AND ReporterPlayerName = @playerName))
            ORDER BY Id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@completeStatus", (int)mmudreborn.Data.Models.BugReportStatus.Complete);
        cmd.Parameters.Add("@bbsUserName", NpgsqlDbType.Citext).Value = reporterBbsUserName;
        cmd.Parameters.Add("@playerName", NpgsqlDbType.Citext).Value = reporterPlayerName;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var rows = new List<mmudreborn.Data.Models.BugReportSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new mmudreborn.Data.Models.BugReportSummary
            {
                Id = reader.GetInt32(0),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                BriefDescription = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                CreatedAt = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Status = ReadBugReportStatus(reader, 4),
            });
        }

        return rows;
    }

    public mmudreborn.Data.Models.BugReportRecord? LoadBugReport(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, ReporterBbsUserName, ReporterPlayerName, Title, BriefDescription, Description, LocationText, ItemName, CreatedAt, Status, CompletedAt, CompletedByBbsUserName, ReporterReviewedAt, VerifiedAt, VerifiedByBbsUserName, VerifiedByPlayerName, ResolutionNote
            FROM BugReports
            WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new mmudreborn.Data.Models.BugReportRecord
        {
            Id = reader.GetInt32(0),
            ReporterBbsUserName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            ReporterPlayerName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            Title = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            BriefDescription = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
            Description = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
            LocationText = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            ItemName = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
            CreatedAt = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
            Status = ReadBugReportStatus(reader, 9),
            CompletedAt = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
            CompletedByBbsUserName = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            ReporterReviewedAt = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
            VerifiedAt = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
            VerifiedByBbsUserName = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            VerifiedByPlayerName = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            ResolutionNote = reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
        };
    }

    public bool CompleteBugReport(int id, string completedByBbsUserName)
    {
        completedByBbsUserName = Player.NormalizeNamePart(completedByBbsUserName ?? string.Empty);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE BugReports
            SET Status = @completeStatus,
                IsCompleted = 1,
                CompletedAt = CURRENT_TIMESTAMP::TEXT,
                CompletedByBbsUserName = @completedBy,
                ReporterReviewedAt = ''
            WHERE Id = @id
              AND (Status = @openStatus OR Status = @stillPresentStatus)";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@completeStatus", (int)mmudreborn.Data.Models.BugReportStatus.Complete);
        cmd.Parameters.AddWithValue("@openStatus", (int)mmudreborn.Data.Models.BugReportStatus.Open);
        cmd.Parameters.AddWithValue("@stillPresentStatus", (int)mmudreborn.Data.Models.BugReportStatus.StillPresent);
        cmd.Parameters.Add("@completedBy", NpgsqlDbType.Citext).Value = completedByBbsUserName;
        return cmd.ExecuteNonQuery() > 0;
    }

    // Close a report as working-as-intended. Unlike CompleteBugReport this is reachable from ANY
    // other status (including Complete), so a sysop who marked something complete before realizing
    // nothing was actually wrong can correct the record without deleting the reporter's write-up.
    // The note is mandatory at the call site; the empty guard here is the backstop that keeps a
    // reason-less dismissal out of the table no matter who calls it.
    public bool MarkBugNotABug(int id, string closedByBbsUserName, string resolutionNote)
    {
        resolutionNote = (resolutionNote ?? string.Empty).Trim();
        if (resolutionNote.Length == 0)
            return false;

        closedByBbsUserName = Player.NormalizeNamePart(closedByBbsUserName ?? string.Empty);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE BugReports
            SET Status = @notABugStatus,
                IsCompleted = 0,
                CompletedAt = CURRENT_TIMESTAMP::TEXT,
                CompletedByBbsUserName = @closedBy,
                ResolutionNote = @note,
                ReporterReviewedAt = ''
            WHERE Id = @id
              AND Status <> @notABugStatus";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@notABugStatus", (int)mmudreborn.Data.Models.BugReportStatus.NotABug);
        cmd.Parameters.Add("@closedBy", NpgsqlDbType.Citext).Value = closedByBbsUserName;
        cmd.Parameters.AddWithValue("@note", resolutionNote);
        return cmd.ExecuteNonQuery() > 0;
    }

    // Not-a-bug closes the reporter has not been shown yet. ReporterReviewedAt is the "seen" marker
    // (MarkBugNotABug clears it, AcknowledgeNotABugReport stamps it), so the login notice fires once
    // per close rather than every login forever.
    public IReadOnlyList<mmudreborn.Data.Models.BugReportSummary> GetUnseenNotABugReportsForReporter(string reporterBbsUserName, string reporterPlayerName, int limit = 20)
    {
        reporterBbsUserName = Player.NormalizeNamePart(reporterBbsUserName ?? string.Empty);
        reporterPlayerName = Player.NormalizeNamePart(reporterPlayerName ?? string.Empty);

        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, Title, BriefDescription, CreatedAt, Status, ResolutionNote, CompletedByBbsUserName
            FROM BugReports
            WHERE Status = @notABugStatus
              AND ReporterReviewedAt = ''
              AND ((@bbsUserName <> '' AND ReporterBbsUserName = @bbsUserName)
                OR (@playerName <> '' AND ReporterPlayerName = @playerName))
            ORDER BY Id DESC
            LIMIT @limit";
        cmd.Parameters.AddWithValue("@notABugStatus", (int)mmudreborn.Data.Models.BugReportStatus.NotABug);
        cmd.Parameters.Add("@bbsUserName", NpgsqlDbType.Citext).Value = reporterBbsUserName;
        cmd.Parameters.Add("@playerName", NpgsqlDbType.Citext).Value = reporterPlayerName;
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 200));

        var rows = new List<mmudreborn.Data.Models.BugReportSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new mmudreborn.Data.Models.BugReportSummary
            {
                Id = reader.GetInt32(0),
                Title = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                BriefDescription = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                CreatedAt = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Status = ReadBugReportStatus(reader, 4),
                ResolutionNote = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                CompletedByBbsUserName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            });
        }

        return rows;
    }

    public bool AcknowledgeNotABugReport(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE BugReports
            SET ReporterReviewedAt = CURRENT_TIMESTAMP::TEXT
            WHERE Id = @id
              AND Status = @notABugStatus
              AND ReporterReviewedAt = ''";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@notABugStatus", (int)mmudreborn.Data.Models.BugReportStatus.NotABug);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool VerifyBugReport(int id, string verifiedByBbsUserName, string verifiedByPlayerName)
    {
        verifiedByBbsUserName = Player.NormalizeNamePart(verifiedByBbsUserName ?? string.Empty);
        verifiedByPlayerName = Player.NormalizeNamePart(verifiedByPlayerName ?? string.Empty);
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE BugReports
            SET Status = @verifiedStatus,
                IsCompleted = 0,
                VerifiedAt = CURRENT_TIMESTAMP::TEXT,
                VerifiedByBbsUserName = @verifiedByBbs,
                VerifiedByPlayerName = @verifiedByPlayer
            WHERE Id = @id
              AND Status = @completeStatus";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@verifiedStatus", (int)mmudreborn.Data.Models.BugReportStatus.Verified);
        cmd.Parameters.AddWithValue("@completeStatus", (int)mmudreborn.Data.Models.BugReportStatus.Complete);
        cmd.Parameters.Add("@verifiedByBbs", NpgsqlDbType.Citext).Value = verifiedByBbsUserName;
        cmd.Parameters.Add("@verifiedByPlayer", NpgsqlDbType.Citext).Value = verifiedByPlayerName;
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool MarkBugStillPresent(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE BugReports
            SET Status = @stillPresentStatus,
                IsCompleted = 0,
                ReporterReviewedAt = CURRENT_TIMESTAMP::TEXT
            WHERE Id = @id
              AND Status = @completeStatus";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@stillPresentStatus", (int)mmudreborn.Data.Models.BugReportStatus.StillPresent);
        cmd.Parameters.AddWithValue("@completeStatus", (int)mmudreborn.Data.Models.BugReportStatus.Complete);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteBugReport(int id)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM BugReports WHERE Id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void ClearBugReports()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "TRUNCATE TABLE BugReports RESTART IDENTITY";
        cmd.ExecuteNonQuery();
    }

    private static mmudreborn.Data.Models.BugReportStatus ReadBugReportStatus(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return mmudreborn.Data.Models.BugReportStatus.Open;

        int raw = reader.GetInt32(ordinal);
        return Enum.IsDefined(typeof(mmudreborn.Data.Models.BugReportStatus), raw)
            ? (mmudreborn.Data.Models.BugReportStatus)raw
            : mmudreborn.Data.Models.BugReportStatus.Open;
    }
}
