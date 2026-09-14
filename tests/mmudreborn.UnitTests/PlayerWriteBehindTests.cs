using System.Diagnostics;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Covers the write-behind player persistence path: MarkPlayerDirty defers the save off-gate, the flush
// coalesces repeated marks, and synchronous/logout saves stay authoritative. The mechanics are tested
// directly against GameWorld + the in-memory repo (no telnet/session stack) so they're deterministic.
public sealed class PlayerWriteBehindTests
{
    private static Player NewPlayer(string name) => new()
    {
        Name = name,
        CurrentMapNumber = 1,
        CurrentRoomNumber = 1,
    };

    [Fact]
    public void Disabled_write_behind_saves_synchronously()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo);
        var player = NewPlayer("Aragorn");
        world.AddPlayer(player);

        int baseline = repo.SaveCount;
        world.MarkPlayerDirty(player); // PlayerWriteBehindEnabled defaults to false

        Assert.Equal(baseline + 1, repo.SaveCount); // wrote immediately, no flush needed
    }

    [Fact]
    public async Task Enabled_write_behind_defers_save_until_flush()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
        var player = NewPlayer("Boromir");
        repo.SavePlayer(player); // creation persists the row before any flush (production invariant)
        world.AddPlayer(player);

        int baseline = repo.SaveCount;
        world.MarkPlayerDirty(player);
        Assert.Equal(baseline, repo.SaveCount); // nothing written yet — only marked dirty

        await world.FlushPendingPlayerSavesAsync();
        Assert.Equal(baseline + 1, repo.SaveCount); // the flush performed the deferred write
    }

    [Fact]
    public async Task Repeated_marks_between_flushes_coalesce_to_one_write()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
        var player = NewPlayer("Legolas");
        repo.SavePlayer(player); // creation persists the row before any flush (production invariant)
        world.AddPlayer(player);

        int baseline = repo.SaveCount;
        world.MarkPlayerDirty(player);
        world.MarkPlayerDirty(player);
        world.MarkPlayerDirty(player);

        await world.FlushPendingPlayerSavesAsync();

        Assert.Equal(baseline + 1, repo.SaveCount); // three marks → one persisted write
    }

    [Fact]
    public async Task Flush_is_a_noop_when_nothing_is_dirty()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
        world.AddPlayer(NewPlayer("Gimli"));

        int baseline = repo.SaveCount;
        await world.FlushPendingPlayerSavesAsync();

        Assert.Equal(baseline, repo.SaveCount);
    }

    [Fact]
    public async Task RemovePlayer_saves_once_and_drops_the_pending_mark()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
        var player = NewPlayer("Frodo");
        world.AddPlayer(player);

        world.MarkPlayerDirty(player);
        int baseline = repo.SaveCount;

        world.RemovePlayer(player); // logout: synchronous authoritative save (+1)
        Assert.Equal(baseline + 1, repo.SaveCount);

        await world.FlushPendingPlayerSavesAsync(); // pending mark was cleared → no second write
        Assert.Equal(baseline + 1, repo.SaveCount);
    }

    // A dirty mark left behind by a session that has since gone away must never be written over the NEW
    // session logged in under the same name. _dirtyPlayers is keyed by NAME but holds a live Player
    // reference, and the flusher's guard only asked "is this name online?" — so a stale object satisfied
    // it and got persisted on top of the character actually playing, silently reverting whatever had
    // changed since (HP, room, inventory, and any spell the reverted snapshot still had active).
    //
    // GameWorld.HandleDroppedConnection already guards this exact hazard by reference — "stops a stale
    // socket from evicting a NEW session that has since logged in under the same name" — the flusher just
    // didn't. Reachable when a hung connection's command loop marks dirty after DisconnectOnlineCharacter
    // has already swapped in the reconnecting session.
    [Fact]
    public async Task A_stale_players_pending_mark_never_overwrites_the_new_session_of_the_same_name()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };

        var hung = NewPlayer("Entangled");
        hung.Experience = 100;
        repo.SavePlayer(hung);
        world.AddPlayer(hung);

        // The hung session goes away (kicked by the reconnect), then a NEW object logs in under the name.
        world.RemovePlayer(hung);
        var reconnected = NewPlayer("Entangled");
        reconnected.Experience = 999;
        world.AddPlayer(reconnected);
        repo.SavePlayer(reconnected); // the live session's state is the row of record

        // The hung session's loop finally unblocks and marks its now-orphaned object dirty.
        hung.Experience = 100;
        world.MarkPlayerDirty(hung);

        await world.FlushPendingPlayerSavesAsync();

        // The live session's state must survive: the orphan must not be written over it.
        Assert.Equal(999, repo.LoadPlayerByName("Entangled")!.Experience);
    }

    [Fact]
    public async Task Flush_persists_the_latest_state()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
        var player = NewPlayer("Samwise");
        repo.SavePlayer(player); // creation persists the row before any flush (production invariant)
        world.AddPlayer(player);

        player.Experience = 100;
        world.MarkPlayerDirty(player);
        player.Experience = 250; // mutated again before the flush runs
        world.MarkPlayerDirty(player);

        await world.FlushPendingPlayerSavesAsync();

        Assert.Equal(250, repo.LoadPlayerByName("Samwise")!.Experience);
    }

    // A deferred (captured) save executes off the world gate, so it can land AFTER a reroll/permadeath
    // hard-deleted the row. It must be UPDATE-only: resurrecting the deleted character would let the
    // login-by-BbsUserId lookup find the old character (class, spellbook, inventory) instead of routing
    // the account to the pending-reroll create screen.
    [Fact]
    public void Deferred_save_does_not_resurrect_a_deleted_player()
    {
        var repo = new InMemoryPlayerRepository();
        var player = NewPlayer("Gollum");
        repo.SavePlayer(player);

        var deferredSave = repo.CapturePlayerSave(player); // captured while alive...
        repo.DeletePlayer("Gollum");                        // ...rerolled: row hard-deleted
        deferredSave();                                     // stale flush lands after the delete

        Assert.Null(repo.LoadPlayerByName("Gollum"));
    }
}
