using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// After a rename, the account->character link must still resolve. That link is now the player row's
// BbsUserId (which RenamePlayer never touches), NOT a player name stored BBS-side — so the renamed
// character stays resolvable for its owning account with no BBS interaction at all.
public sealed class SysopRenameAccountLinkTests
{
    [Fact]
    public void RenamePlayer_keeps_the_character_resolvable_by_its_owning_bbs_user_id()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo);

        // Offline character "Oldname" owned by BBS account "Accountx".
        repo.SavePlayer(new Player { Name = "Oldname", BbsUserId = "Accountx" });

        var outcome = world.RenamePlayer("Oldname", "Newname");

        Assert.Equal(GameWorld.RenamePlayerOutcome.Success, outcome);

        // The owning account still resolves to the (renamed) character, by BbsUserId.
        var resolved = repo.LoadPlayerByBbsUserId("Accountx");
        Assert.NotNull(resolved);
        Assert.Equal("Newname", resolved!.Name);
    }
}
