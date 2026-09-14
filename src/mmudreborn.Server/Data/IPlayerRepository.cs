using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Data;

public interface IPlayerRepository
{
    bool PlayerExists(string name);
    void SavePlayer(Player player);

    // Write-behind save split. CapturePlayerSave reads the player's state NOW (the CPU-only half: field
    // reads + serialization) and returns a delegate that performs the database write when invoked later.
    // The world's flush loop captures under the gate — so the copy is consistent — then runs the returned
    // action OFF the gate, keeping the round-trip out of the single global game-state lock.
    Action CapturePlayerSave(Player player);
    void DeletePlayer(string name);

    /// <summary>
    /// Wipe all persisted realm state (players + carried items/bank, pending rerolls, gangs + invites,
    /// gang houses, gang shops, ground item/currency drops, hall of fame) for a full SYSOP BOARD RESET.
    /// Server config and support tickets are preserved.
    /// </summary>
    void ResetRealmPersistence();
    Player? LoadPlayer(string name, string password);
    Player? LoadPlayerByName(string name);
    Player? LoadPlayerByBbsUserId(string bbsUserId);
    bool ResetPassword(string name, string password);

    List<(string Name, string PasswordHash, bool IsTestAccount)> GetPlayersForBbsSync();
    List<(string Name, string LastName, string BbsUserId)> GetPlayerDirectory(string mask = "");
    List<(string Name, string LastName, int ClassId, string Gang, long Experience)> GetTopPlayers(int limit = 10, int classId = 0);
    List<(string GangName, string LeaderName, int Members, string Created, long Experience)> GetTopGangs(int limit = 10);
    List<(string Name, string LastName)> GetPlayersNotTopten();
    List<(string Name, string LastName)> GetPlayersByGang(string gangName);

    int DisbandGang(string gangName);
    void SetGangToptenDisabled(string gangName, bool disabled);
    void SetGangMaxSize(string gangName, int maxSize);
    bool GangExists(string gangName);
    int GetGangMemberCount(string gangName);
    int GetGangMaxSize(string gangName);
    bool CreateGangRecord(string gangName, int maxSize = 9, string leaderName = "");
    bool IsGangLeader(string playerName, string gangName);
    void AddGangInvite(string inviteeName, string gangName, string inviterName);
    bool HasGangInvite(string inviteeName, string gangName);
    void RemoveGangInvite(string inviteeName, string gangName);
    bool RemovePlayerFromGang(string playerName, string gangName);
    bool IsGangLieutenant(string playerName, string gangName);
    bool SetGangLieutenant(string playerName, string gangName, bool isLieutenant);

    // The offline half of PROMOTE/DEMOTE. Null when no player by that name exists at all; otherwise the
    // stored name (for message casing) and the gang they belong to, "" when they belong to none. Callers
    // need all three outcomes kept apart — see HandlePromote.
    (string Name, string Gang)? GetPlayerNameAndGang(string playerName);
    List<(string Name, string LastName, int Level, int RaceId, int ClassId, bool IsLieutenant)> GetGangRoster(string gangName);
    string? GetGangLeaderName(string gangName);
    string? ResolveGangName(string gangName);
    bool RenameGang(string oldName, string newName);

    IReadOnlyList<GangHouseRecord> LoadGangHouses();
    void SaveGangHouse(GangHouseRecord house);
    void DeleteGangHouse(int houseId);

    IReadOnlyList<GangShopRecord> LoadGangShops();
    void SaveGangShop(GangShopRecord shop);
    void DeleteGangShop(int shopId);

    IReadOnlyList<string> GetAllPlayerNames();

    bool RenamePlayer(string oldName, string newName);

    int GetServerSettingInt(string key, int defaultValue);
    string GetServerSettingText(string key, string defaultValue);
    void SetServerSettingInt(string key, int value);
    void SetServerSettingText(string key, string value);

    IReadOnlyList<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> LoadRoomGroundItems();
    IReadOnlyList<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> LoadRoomGroundCurrency();
    void SaveRoomGroundState(
        IEnumerable<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)> itemRows,
        IEnumerable<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)> currencyRows);
    void ClearRoomGroundState();

    void SavePendingReroll(string bbsUserName, string preservedName, long keptExperience, bool isSysop, bool toptenDisabled, string suicideRerollPassword);
    PendingRerollRecord? LoadPendingReroll(string bbsUserName);
    void DeletePendingReroll(string bbsUserName);

    /// <summary>
    /// Append a realm broadcast (gossip/auction) to the size-capped GossipLog for the web explorer.
    /// Best-effort: callers invoke this fire-and-forget so a logging hiccup never affects gameplay.
    /// </summary>
    void AppendChannelMessage(string sender, string channel, string message);

    void SaveHallOfFameEntry(HallOfFameEntry entry);
    IReadOnlyList<HallOfFameEntry> GetHallOfFameEntries(int limit = 20);

    int CreateBugReport(BugReportRecord report);
    IReadOnlyList<BugReportSummary> GetBugReports(int limit = 50);
    IReadOnlyList<BugReportSummary> GetCompletedBugReportsForReporter(string reporterBbsUserName, string reporterPlayerName, int limit = 20);
    BugReportRecord? LoadBugReport(int id);
    bool CompleteBugReport(int id, string completedByBbsUserName);
    bool MarkBugNotABug(int id, string closedByBbsUserName, string resolutionNote);
    IReadOnlyList<BugReportSummary> GetUnseenNotABugReportsForReporter(string reporterBbsUserName, string reporterPlayerName, int limit = 20);
    bool AcknowledgeNotABugReport(int id);
    bool VerifyBugReport(int id, string verifiedByBbsUserName, string verifiedByPlayerName);
    bool MarkBugStillPresent(int id);
    bool DeleteBugReport(int id);
    void ClearBugReports();
}
