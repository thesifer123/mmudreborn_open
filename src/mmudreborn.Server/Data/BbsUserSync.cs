namespace mmudreborn.Data;

/// <summary>
/// The game→BBS account bridge: seeds/refreshes BBS board accounts from the door's player records.
/// This is door-specific (it reads game players), so it lives in mmudreborn rather than on the generic
/// CWGaming.Shared <see cref="IBbsUserRepository"/>. It writes through the generic repository surface,
/// so it works against either the direct (Postgres) or HTTP-backed BBS user store.
/// </summary>
public static class BbsUserSync
{
    public static int SyncFromPlayers(IBbsUserRepository bbsUserRepository, IPlayerRepository playerRepository, bool overwriteExistingPasswords = false)
    {
        ArgumentNullException.ThrowIfNull(bbsUserRepository);
        ArgumentNullException.ThrowIfNull(playerRepository);

        var users = playerRepository.GetPlayersForBbsSync()
            .Select(player => new BbsUserAccount
            {
                UserName = player.Name,
                PasswordHash = player.PasswordHash,
                IsTestAccount = player.IsTestAccount,
            })
            .ToList();

        return bbsUserRepository.SyncUsers(users, overwriteExistingPasswords);
    }
}
