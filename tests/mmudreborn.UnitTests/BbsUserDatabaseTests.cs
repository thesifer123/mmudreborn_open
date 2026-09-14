using mmudreborn.Game;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class BbsUserDatabaseTests
{
    [Fact]
    public void GetPlayersForBbsSync_returns_names_and_password_hashes()
    {
        var repo = new InMemoryPlayerRepository();
        repo.SavePlayer(new Player { Name = "UnitSyncUser", PasswordHash = "HASH-UNITSYNC" });
        repo.SavePlayer(new Player { Name = "Adept", PasswordHash = "HASH-ADEPT" });

        var rows = repo.GetPlayersForBbsSync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Name == "UnitSyncUser" && row.PasswordHash == "HASH-UNITSYNC");
        Assert.Contains(rows, row => row.Name == "Adept" && row.PasswordHash == "HASH-ADEPT");
    }

    [Fact]
    public void LoadPlayerByName_returns_the_saved_player_without_password_validation()
    {
        var repo = new InMemoryPlayerRepository();
        var player = new Player { Name = "UnitLookupUser", PasswordHash = "HASH" };
        repo.SavePlayer(player);

        var loaded = repo.LoadPlayerByName("unitlookupuser");

        Assert.Same(player, loaded);
    }
}
