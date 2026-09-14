using mmudreborn.Data;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.UnitTests.Fakes;

/// <summary>
/// In-memory IPlayerRepository for unit tests. Players keyed by case-insensitive name.
/// Gang membership and invites tracked in plain dictionaries; server settings in two maps.
/// Tests that need behavior beyond what's implemented can subclass and override.
/// </summary>
public class InMemoryPlayerRepository : IPlayerRepository
{
    private readonly Dictionary<string, Player> _players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _passwordHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GangRecord> _gangs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Invitee, string Gang)> _gangInvites = new();
    private readonly Dictionary<string, int> _settingsInt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _settingsText = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> _groundItems = [];
    private readonly List<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> _groundCurrency = [];
    private readonly Dictionary<string, PendingRerollRecord> _pendingRerolls = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BugReportRecord> _bugReports = [];
    private int _nextBugReportId = 1;

    private sealed class GangRecord
    {
        public string Name = "";
        public string LeaderName = "";
        public int MaxSize = 9;
        public bool ToptenDisabled;
        public HashSet<string> Members = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Lieutenants = new(StringComparer.OrdinalIgnoreCase);
        public string Created = "";
    }

    public bool PlayerExists(string name)
    {
        return _players.ContainsKey(Player.NormalizeNamePart(name));
    }

    // Total number of times a player row was actually written (synchronous SavePlayer plus any deferred
    // CapturePlayerSave action that was invoked). Lets write-behind tests assert coalescing/flush counts.
    public int SaveCount { get; private set; }

    public virtual void SavePlayer(Player player)
    {
        player.Name = Player.NormalizeNamePart(player.Name);
        player.LastName = Player.NormalizeNamePart(player.LastName);
        _players[player.Name] = player;
        SaveCount++;
    }

    // Mirrors the real split: capture the player NOW, persist when the returned action runs. The fake's
    // capture is trivial (single-threaded tests have no torn-read concern), so it just defers SavePlayer.
    // Like the real repository, a deferred save is UPDATE-only: it must never resurrect a row that was
    // hard-deleted (reroll/permadeath) between capture and execution.
    public virtual Action CapturePlayerSave(Player player)
        => () =>
        {
            if (!_players.ContainsKey(Player.NormalizeNamePart(player.Name)))
                return;
            SavePlayer(player);
        };

    public void DeletePlayer(string name)
    {
        name = Player.NormalizeNamePart(name);
        _players.Remove(name);
        _passwordHashes.Remove(name);
    }

    public void ResetRealmPersistence()
    {
        _players.Clear();
        _passwordHashes.Clear();
        _gangs.Clear();
        _gangInvites.Clear();
        _pendingRerolls.Clear();
        _groundItems.Clear();
        _groundCurrency.Clear();
    }

    public virtual Player? LoadPlayer(string name, string password)
    {
        var player = LoadPlayerByName(name);
        if (player == null)
            return null;

        if (_passwordHashes.TryGetValue(Player.NormalizeNamePart(name), out var hash))
        {
            if (!CWGaming.Shared.BbsSecurity.VerifyPassword(password, hash))
                return null;
        }

        return player;
    }

    public virtual Player? LoadPlayerByName(string name)
    {
        name = Player.NormalizeNamePart(name);
        return _players.TryGetValue(name, out var player) ? player : null;
    }

    public virtual Player? LoadPlayerByBbsUserId(string bbsUserId)
    {
        if (string.IsNullOrWhiteSpace(bbsUserId))
            return null;

        bbsUserId = Player.NormalizeNamePart(bbsUserId);
        return _players.Values
            .Where(p => string.Equals(p.BbsUserId, bbsUserId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public List<(string Name, string PasswordHash, bool IsTestAccount)> GetPlayersForBbsSync()
    {
        return _players.Values
            .Select(p => (p.Name, _passwordHashes.TryGetValue(p.Name, out var hash) ? hash : p.PasswordHash, p.IsTestAccount))
            .ToList();
    }

    public bool ResetPassword(string name, string password)
    {
        name = Player.NormalizeNamePart(name);
        if (!_players.ContainsKey(name))
            return false;

        _passwordHashes[name] = PlayerRepository.HashPassword(password);
        return true;
    }

    public readonly List<(string Sender, string Channel, string Message)> ChannelMessages = new();

    public void AppendChannelMessage(string sender, string channel, string message)
    {
        ChannelMessages.Add((sender, channel, message));
    }

    public void SetPasswordHash(string name, string hash)
    {
        _passwordHashes[Player.NormalizeNamePart(name)] = hash;
    }

    public List<(string Name, string LastName, string BbsUserId)> GetPlayerDirectory(string mask = "")
    {
        return _players.Values
            .Where(p => !p.IsTestAccount)
            .Where(p => string.IsNullOrEmpty(mask)
                || p.Name.Contains(mask, StringComparison.OrdinalIgnoreCase)
                || p.LastName.Contains(mask, StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.Name, p.LastName, p.BbsUserId))
            .ToList();
    }

    public List<(string Name, string LastName, int ClassId, string Gang, long Experience)> GetTopPlayers(int limit = 10, int classId = 0)
    {
        IEnumerable<Player> query = _players.Values
            .Where(p => !p.IsTestAccount)
            .Where(p => classId <= 0 || p.ClassId == classId)
            .OrderByDescending(p => p.Experience)
            .ThenBy(p => p.Name);

        if (limit > 0)
            query = query.Take(limit);

        return query
            .Select(p => (p.Name, p.LastName, p.ClassId, p.Gang, p.Experience))
            .ToList();
    }

    public List<(string GangName, string LeaderName, int Members, string Created, long Experience)> GetTopGangs(int limit = 10)
    {
        return _gangs.Values
            .Where(g => !g.ToptenDisabled)
            .Select(g => (g.Name, g.LeaderName, g.Members.Count(member => _players.TryGetValue(member, out var player) && !player.IsTestAccount), g.Created, ComputeGangExperience(g)))
            .Where(g => g.Item3 > 0)
            .OrderByDescending(t => t.Item5)
            .Take(limit)
            .ToList();
    }

    private long ComputeGangExperience(GangRecord gang)
    {
        long sum = 0;
        foreach (var member in gang.Members)
            if (_players.TryGetValue(member, out var p))
                sum += p.IsTestAccount ? 0 : p.Experience;
        return sum;
    }

    public List<(string Name, string LastName)> GetPlayersNotTopten()
    {
        return _players.Values
            .Where(p => !p.IsTestAccount)
            .Select(p => (p.Name, p.LastName))
            .ToList();
    }

    public List<(string Name, string LastName)> GetPlayersByGang(string gangName)
    {
        return _players.Values
            .Where(p => !p.IsTestAccount)
            .Where(p => string.Equals(p.Gang, gangName, StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.Name, p.LastName))
            .ToList();
    }

    public int DisbandGang(string gangName)
    {
        if (!_gangs.TryGetValue(gangName, out var gang))
            return 0;

        int affected = 0;
        foreach (var member in gang.Members)
        {
            if (_players.TryGetValue(member, out var p))
            {
                p.Gang = "";
                p.IsGangLieutenant = false;
                affected++;
            }
        }

        _gangs.Remove(gangName);
        return affected;
    }

    public void SetGangToptenDisabled(string gangName, bool disabled)
    {
        if (_gangs.TryGetValue(gangName, out var gang))
            gang.ToptenDisabled = disabled;
    }

    public void SetGangMaxSize(string gangName, int maxSize)
    {
        if (_gangs.TryGetValue(gangName, out var gang))
            gang.MaxSize = maxSize;
    }

    public bool GangExists(string gangName) => _gangs.ContainsKey(gangName);

    public int GetGangMemberCount(string gangName)
        => _gangs.TryGetValue(gangName, out var gang) ? gang.Members.Count : 0;

    public int GetGangMaxSize(string gangName)
        => _gangs.TryGetValue(gangName, out var gang) ? gang.MaxSize : 0;

    public bool CreateGangRecord(string gangName, int maxSize = 9, string leaderName = "")
    {
        if (_gangs.ContainsKey(gangName))
            return false;

        _gangs[gangName] = new GangRecord
        {
            Name = gangName,
            LeaderName = leaderName,
            MaxSize = maxSize,
            Members = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { leaderName },
        };
        return true;
    }

    public bool IsGangLeader(string playerName, string gangName)
        => _gangs.TryGetValue(gangName, out var gang)
            && string.Equals(gang.LeaderName, playerName, StringComparison.OrdinalIgnoreCase);

    public void AddGangInvite(string inviteeName, string gangName, string inviterName)
    {
        _gangInvites.Add((Player.NormalizeNamePart(inviteeName), gangName));
    }

    public bool HasGangInvite(string inviteeName, string gangName)
        => _gangInvites.Contains((Player.NormalizeNamePart(inviteeName), gangName));

    public void RemoveGangInvite(string inviteeName, string gangName)
    {
        _gangInvites.Remove((Player.NormalizeNamePart(inviteeName), gangName));
    }

    public bool RemovePlayerFromGang(string playerName, string gangName)
    {
        if (!_gangs.TryGetValue(gangName, out var gang))
            return false;

        bool removed = gang.Members.Remove(playerName);
        gang.Lieutenants.Remove(playerName);
        return removed;
    }

    public bool IsGangLieutenant(string playerName, string gangName)
        => _gangs.TryGetValue(gangName, out var gang) && gang.Lieutenants.Contains(playerName);

    public bool SetGangLieutenant(string playerName, string gangName, bool isLieutenant)
    {
        if (!_gangs.TryGetValue(gangName, out var gang))
            return false;
        if (!gang.Members.Contains(playerName))
            return false;

        if (isLieutenant)
            gang.Lieutenants.Add(playerName);
        else
            gang.Lieutenants.Remove(playerName);
        return true;
    }

    public (string Name, string Gang)? GetPlayerNameAndGang(string playerName)
    {
        if (!_players.TryGetValue(Player.NormalizeNamePart(playerName), out var player))
            return null;

        return (player.Name, player.Gang ?? string.Empty);
    }

    public List<(string Name, string LastName, int Level, int RaceId, int ClassId, bool IsLieutenant)> GetGangRoster(string gangName)
    {
        if (!_gangs.TryGetValue(gangName, out var gang))
            return [];

        var result = new List<(string, string, int, int, int, bool)>();
        foreach (var member in gang.Members)
        {
            if (_players.TryGetValue(member, out var p))
                result.Add((p.Name, p.LastName, p.Level, p.RaceId, p.ClassId, gang.Lieutenants.Contains(member)));
        }
        return result;
    }

    public string? GetGangLeaderName(string gangName)
        => _gangs.TryGetValue(gangName, out var gang) ? gang.LeaderName : null;

    public string? ResolveGangName(string gangName)
        => _gangs.TryGetValue(gangName, out var gang) ? gang.Name : null;

    public bool RenameGang(string oldName, string newName)
    {
        if (!_gangs.TryGetValue(oldName, out var gang))
            return false;
        if (_gangs.ContainsKey(newName))
            return false;

        _gangs.Remove(oldName);
        gang.Name = newName;
        _gangs[newName] = gang;

        foreach (var member in gang.Members)
            if (_players.TryGetValue(member, out var p))
                p.Gang = newName;

        return true;
    }

    public bool RenamePlayer(string oldName, string newName)
    {
        oldName = Player.NormalizeNamePart(oldName);
        newName = Player.NormalizeNamePart(newName);

        if (!_players.TryGetValue(oldName, out var player))
            return false;
        if (_players.ContainsKey(newName))
            return false;

        _players.Remove(oldName);
        player.Name = newName;
        _players[newName] = player;

        if (_passwordHashes.Remove(oldName, out var hash))
            _passwordHashes[newName] = hash;

        return true;
    }

    private readonly Dictionary<int, GangHouseRecord> _gangHouses = new();

    public IReadOnlyList<GangHouseRecord> LoadGangHouses() => _gangHouses.Values.ToList();

    public void SaveGangHouse(GangHouseRecord house)
    {
        ArgumentNullException.ThrowIfNull(house);
        _gangHouses[house.HouseId] = house;
    }

    public void DeleteGangHouse(int houseId) => _gangHouses.Remove(houseId);

    private readonly Dictionary<int, GangShopRecord> _gangShops = new();

    public IReadOnlyList<GangShopRecord> LoadGangShops() => _gangShops.Values.ToList();

    public void SaveGangShop(GangShopRecord shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        _gangShops[shop.ShopId] = shop;
    }

    public void DeleteGangShop(int shopId) => _gangShops.Remove(shopId);

    public IReadOnlyList<string> GetAllPlayerNames() => _players.Keys.ToList();

    public int GetServerSettingInt(string key, int defaultValue)
        => _settingsInt.TryGetValue(key, out var v) ? v : defaultValue;

    public string GetServerSettingText(string key, string defaultValue)
        => _settingsText.TryGetValue(key, out var v) ? v : defaultValue;

    public void SetServerSettingInt(string key, int value) => _settingsInt[key] = value;
    public void SetServerSettingText(string key, string value) => _settingsText[key] = value;

    public IReadOnlyList<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> LoadRoomGroundItems()
        => _groundItems.ToList();

    public IReadOnlyList<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> LoadRoomGroundCurrency()
        => _groundCurrency.ToList();

    public void SaveRoomGroundState(
        IEnumerable<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> itemRows,
        IEnumerable<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> currencyRows)
    {
        _groundItems.Clear();
        _groundItems.AddRange(itemRows);
        _groundCurrency.Clear();
        _groundCurrency.AddRange(currencyRows);
    }

    public void ClearRoomGroundState()
    {
        _groundItems.Clear();
        _groundCurrency.Clear();
    }

    public void SavePendingReroll(string bbsUserName, string preservedName, long keptExperience, bool isSysop, bool toptenDisabled, string suicideRerollPassword)
    {
        bbsUserName = Player.NormalizeNamePart(bbsUserName);
        _pendingRerolls[bbsUserName] = new PendingRerollRecord
        {
            BbsUserName = bbsUserName,
            PreservedName = Player.NormalizeNamePart(preservedName ?? ""),
            KeptExperience = keptExperience,
            PreservedIsSysop = isSysop,
            PreservedToptenDisabled = toptenDisabled,
            PreservedSuicideRerollPassword = suicideRerollPassword ?? "",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        };
    }

    public PendingRerollRecord? LoadPendingReroll(string bbsUserName)
    {
        bbsUserName = Player.NormalizeNamePart(bbsUserName);
        return _pendingRerolls.TryGetValue(bbsUserName, out var record) ? record : null;
    }

    public void DeletePendingReroll(string bbsUserName)
    {
        bbsUserName = Player.NormalizeNamePart(bbsUserName);
        _pendingRerolls.Remove(bbsUserName);
    }

    private readonly List<HallOfFameEntry> _hallOfFame = new();
    private int _nextHallOfFameId = 1;

    public void SaveHallOfFameEntry(HallOfFameEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _hallOfFame.Add(new HallOfFameEntry
        {
            Id = _nextHallOfFameId++,
            PlayerName = Player.NormalizeNamePart(entry.PlayerName ?? string.Empty),
            LastName = entry.LastName ?? string.Empty,
            Level = entry.Level,
            RaceId = entry.RaceId,
            ClassId = entry.ClassId,
            Experience = entry.Experience,
            Alignment = entry.Alignment,
            KillerName = entry.KillerName ?? string.Empty,
            CreatedAt = string.IsNullOrWhiteSpace(entry.CreatedAt) ? DateTime.UtcNow.ToString("O") : entry.CreatedAt,
        });
    }

    public IReadOnlyList<HallOfFameEntry> GetHallOfFameEntries(int limit = 20)
    {
        return _hallOfFame
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    public int CreateBugReport(BugReportRecord report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var stored = CloneBugReport(report);
        stored.Id = _nextBugReportId++;
        stored.ReporterBbsUserName = Player.NormalizeNamePart(stored.ReporterBbsUserName);
        stored.ReporterPlayerName = Player.NormalizeNamePart(stored.ReporterPlayerName);
        stored.Title = (stored.Title ?? string.Empty).Trim();
        stored.BriefDescription = (stored.BriefDescription ?? string.Empty).Trim();
        stored.Description ??= string.Empty;
        stored.LocationText = (stored.LocationText ?? string.Empty).Trim();
        stored.ItemName = (stored.ItemName ?? string.Empty).Trim();
        stored.CreatedAt = string.IsNullOrWhiteSpace(stored.CreatedAt) ? DateTime.UtcNow.ToString("O") : stored.CreatedAt;
        stored.CompletedAt ??= string.Empty;
        stored.CompletedByBbsUserName ??= string.Empty;
        stored.ReporterReviewedAt ??= string.Empty;

        _bugReports.Add(stored);
        return stored.Id;
    }

    public IReadOnlyList<BugReportSummary> GetBugReports(int limit = 50)
    {
        return _bugReports
            .OrderByDescending(report => report.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(report => new BugReportSummary
            {
                Id = report.Id,
                Title = report.Title,
                BriefDescription = report.BriefDescription,
                CreatedAt = report.CreatedAt,
                Status = report.Status,
            })
            .ToList();
    }

    public IReadOnlyList<BugReportSummary> GetCompletedBugReportsForReporter(string reporterBbsUserName, string reporterPlayerName, int limit = 20)
    {
        reporterBbsUserName = Player.NormalizeNamePart(reporterBbsUserName ?? string.Empty);
        reporterPlayerName = Player.NormalizeNamePart(reporterPlayerName ?? string.Empty);

        return _bugReports
            .Where(report => report.Status == BugReportStatus.Complete)
            .Where(report =>
                (!string.IsNullOrWhiteSpace(reporterBbsUserName) && string.Equals(report.ReporterBbsUserName, reporterBbsUserName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(reporterPlayerName) && string.Equals(report.ReporterPlayerName, reporterPlayerName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(report => report.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(report => new BugReportSummary
            {
                Id = report.Id,
                Title = report.Title,
                BriefDescription = report.BriefDescription,
                CreatedAt = report.CreatedAt,
                Status = report.Status,
            })
            .ToList();
    }

    public BugReportRecord? LoadBugReport(int id)
    {
        var report = _bugReports.FirstOrDefault(entry => entry.Id == id);
        return report == null ? null : CloneBugReport(report);
    }

    public bool CompleteBugReport(int id, string completedByBbsUserName)
    {
        var report = _bugReports.FirstOrDefault(entry => entry.Id == id);
        if (report == null || (report.Status != BugReportStatus.Open && report.Status != BugReportStatus.StillPresent))
            return false;

        report.Status = BugReportStatus.Complete;
        report.CompletedAt = DateTime.UtcNow.ToString("O");
        report.CompletedByBbsUserName = Player.NormalizeNamePart(completedByBbsUserName ?? string.Empty);
        report.ReporterReviewedAt = string.Empty;
        return true;
    }

    public bool MarkBugNotABug(int id, string closedByBbsUserName, string resolutionNote)
    {
        resolutionNote = (resolutionNote ?? string.Empty).Trim();
        if (resolutionNote.Length == 0)
            return false;

        var report = _bugReports.FirstOrDefault(entry => entry.Id == id);
        if (report == null || report.Status == BugReportStatus.NotABug)
            return false;

        report.Status = BugReportStatus.NotABug;
        report.CompletedAt = DateTime.UtcNow.ToString("O");
        report.CompletedByBbsUserName = Player.NormalizeNamePart(closedByBbsUserName ?? string.Empty);
        report.ResolutionNote = resolutionNote;
        report.ReporterReviewedAt = string.Empty;
        return true;
    }

    public IReadOnlyList<BugReportSummary> GetUnseenNotABugReportsForReporter(string reporterBbsUserName, string reporterPlayerName, int limit = 20)
    {
        reporterBbsUserName = Player.NormalizeNamePart(reporterBbsUserName ?? string.Empty);
        reporterPlayerName = Player.NormalizeNamePart(reporterPlayerName ?? string.Empty);

        return _bugReports
            .Where(report => report.Status == BugReportStatus.NotABug)
            .Where(report => string.IsNullOrEmpty(report.ReporterReviewedAt))
            .Where(report =>
                (!string.IsNullOrWhiteSpace(reporterBbsUserName) && string.Equals(report.ReporterBbsUserName, reporterBbsUserName, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(reporterPlayerName) && string.Equals(report.ReporterPlayerName, reporterPlayerName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(report => report.Id)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(report => new BugReportSummary
            {
                Id = report.Id,
                Title = report.Title,
                BriefDescription = report.BriefDescription,
                CreatedAt = report.CreatedAt,
                Status = report.Status,
                ResolutionNote = report.ResolutionNote,
                CompletedByBbsUserName = report.CompletedByBbsUserName,
            })
            .ToList();
    }

    public bool AcknowledgeNotABugReport(int id)
    {
        var report = _bugReports.FirstOrDefault(entry => entry.Id == id);
        if (report == null || report.Status != BugReportStatus.NotABug || !string.IsNullOrEmpty(report.ReporterReviewedAt))
            return false;

        report.ReporterReviewedAt = DateTime.UtcNow.ToString("O");
        return true;
    }

    public bool VerifyBugReport(int id, string verifiedByBbsUserName, string verifiedByPlayerName)
    {
        var report = _bugReports.FirstOrDefault(entry => entry.Id == id);
        if (report == null || report.Status != BugReportStatus.Complete)
            return false;

        report.Status = BugReportStatus.Verified;
        report.VerifiedAt = DateTime.UtcNow.ToString("O");
        report.VerifiedByBbsUserName = Player.NormalizeNamePart(verifiedByBbsUserName ?? string.Empty);
        report.VerifiedByPlayerName = Player.NormalizeNamePart(verifiedByPlayerName ?? string.Empty);
        return true;
    }

    public bool MarkBugStillPresent(int id)
    {
        var report = _bugReports.FirstOrDefault(entry => entry.Id == id);
        if (report == null || report.Status != BugReportStatus.Complete)
            return false;

        report.Status = BugReportStatus.StillPresent;
        report.ReporterReviewedAt = DateTime.UtcNow.ToString("O");
        return true;
    }

    public bool DeleteBugReport(int id)
    {
        int index = _bugReports.FindIndex(entry => entry.Id == id);
        if (index < 0)
            return false;

        _bugReports.RemoveAt(index);
        return true;
    }

    public void ClearBugReports()
    {
        _bugReports.Clear();
        _nextBugReportId = 1;
    }

    private static BugReportRecord CloneBugReport(BugReportRecord report)
    {
        return new BugReportRecord
        {
            Id = report.Id,
            ReporterBbsUserName = report.ReporterBbsUserName,
            ReporterPlayerName = report.ReporterPlayerName,
            Title = report.Title,
            BriefDescription = report.BriefDescription,
            Description = report.Description,
            LocationText = report.LocationText,
            ItemName = report.ItemName,
            CreatedAt = report.CreatedAt,
            Status = report.Status,
            CompletedAt = report.CompletedAt,
            CompletedByBbsUserName = report.CompletedByBbsUserName,
            ResolutionNote = report.ResolutionNote,
            ReporterReviewedAt = report.ReporterReviewedAt,
        };
    }
}
