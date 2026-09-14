using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// SYSOP BOARD RESET wipes the realm. ResetRealmToFreshState must remove every online character from the
// world WITHOUT persisting them (so the background flusher can't resurrect a deleted row) and wipe all
// persisted realm state.
public sealed class SysopBoardResetTests
{
    private static GameWorld NewWorld(out InMemoryPlayerRepository repo)
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Square", Description = "x" };
        repo = new InMemoryPlayerRepository();
        return new GameWorld(db, repo);
    }

    private static void AddOnline(GameWorld world, InMemoryPlayerRepository repo, string name)
    {
        var player = new Player { Name = name, BbsUserId = name, CurrentMapNumber = 1, CurrentRoomNumber = 1, Client = new RecordingGameClient() };
        ((RecordingGameClient)player.Client!).Player = player;
        repo.SavePlayer(player);
        world.AddPlayer(player);
    }

    [Fact]
    public void ResetRealmToFreshState_removes_online_players_and_wipes_persistence()
    {
        var world = NewWorld(out var repo);
        AddOnline(world, repo, "Alpha");
        AddOnline(world, repo, "Bravo");
        Assert.Equal(2, world.GetAllOnlinePlayers().Count);

        world.ResetRealmToFreshState();

        Assert.Empty(world.GetAllOnlinePlayers());
        Assert.Null(repo.LoadPlayerByName("Alpha"));
        Assert.Null(repo.LoadPlayerByName("Bravo"));
        Assert.Null(repo.LoadPlayerByBbsUserId("Alpha"));
    }

    // The wipe kicks every online socket, so the host's dropped-connection teardown lands AFTERWARDS.
    // That teardown used to re-save the character it found on the connection, re-INSERTING every row the
    // wipe had just deleted — leaving only the invoking sysop (whose client had been unbound) deleted.
    [Fact]
    public void Dropped_connection_after_a_wipe_does_not_resurrect_the_character()
    {
        var world = NewWorld(out var repo);
        AddOnline(world, repo, "Alpha");
        var alpha = world.FindOnlinePlayer("Alpha")!;
        var client = (RecordingGameClient)alpha.Client!;
        client.AppData = client; // the host stashes the door's client in the connection's opaque slot

        world.ResetRealmToFreshState();
        Assert.Null(repo.LoadPlayerByName("Alpha"));

        world.HandleDroppedConnection(client);

        Assert.Null(repo.LoadPlayerByName("Alpha"));
        Assert.Empty(world.GetAllOnlinePlayers());
    }

    // A stale socket closing after the same account has logged back in must not evict (or save over) the
    // NEW session that now owns the name.
    [Fact]
    public void Dropped_connection_does_not_evict_a_newer_session_for_the_same_name()
    {
        var world = NewWorld(out var repo);
        AddOnline(world, repo, "Alpha");
        var stale = world.FindOnlinePlayer("Alpha")!;
        var staleClient = (RecordingGameClient)stale.Client!;
        staleClient.AppData = staleClient;

        world.RemovePlayer(stale, save: false);
        AddOnline(world, repo, "Alpha");
        var fresh = world.FindOnlinePlayer("Alpha")!;

        world.HandleDroppedConnection(staleClient);

        Assert.Same(fresh, world.FindOnlinePlayer("Alpha"));
    }
}
