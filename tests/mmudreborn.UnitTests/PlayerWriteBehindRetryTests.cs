using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A write-behind save that FAILS (e.g. the database briefly unreachable) must be retried, not dropped. The
// flusher removes a player's dirty mark before writing, so without a re-mark a failed write left that
// player's progress unsaved until their next command. Shares the GameDiagnostics collection because the
// flusher records the failure through that static state.
[Collection(GameDiagnosticsCollection.Name)]
public sealed class PlayerWriteBehindRetryTests
{
    // The in-memory repository, except the next FailuresRemaining deferred writes throw.
    private sealed class FailingSavesRepository : InMemoryPlayerRepository
    {
        public int FailuresRemaining { get; set; }
        public Action? BeforeFailure { get; set; }

        public override Action CapturePlayerSave(Player player)
        {
            var save = base.CapturePlayerSave(player);
            return () =>
            {
                if (FailuresRemaining > 0)
                {
                    FailuresRemaining--;
                    BeforeFailure?.Invoke();
                    throw new InvalidOperationException("database unavailable");
                }

                save();
            };
        }
    }

    private static Player NewPlayer(string name) => new()
    {
        Name = name,
        CurrentMapNumber = 1,
        CurrentRoomNumber = 1,
    };

    [Fact]
    public async Task A_failed_flush_is_retried_on_the_next_flush()
    {
        bool previousRethrow = GameDiagnostics.RethrowBackgroundExceptions;
        try
        {
            GameDiagnostics.RethrowBackgroundExceptions = false;
            var repo = new FailingSavesRepository();
            var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
            var player = NewPlayer("Faramir");
            repo.SavePlayer(player); // creation persists the row before any flush (production invariant)
            world.AddPlayer(player);

            world.MarkPlayerDirty(player);
            repo.FailuresRemaining = 1;
            int baseline = repo.SaveCount;

            await world.FlushPendingPlayerSavesAsync(); // the write fails
            Assert.Equal(baseline, repo.SaveCount);

            // No new command marks the player — the retry has to come from the failed flush itself.
            await world.FlushPendingPlayerSavesAsync();
            Assert.Equal(baseline + 1, repo.SaveCount);

            await world.FlushPendingPlayerSavesAsync(); // saved once; nothing left pending
            Assert.Equal(baseline + 1, repo.SaveCount);
        }
        finally
        {
            GameDiagnostics.RethrowBackgroundExceptions = previousRethrow;
            GameDiagnostics.Reset();
        }
    }

    // A player who logs out while their deferred write is failing already got the synchronous logout save,
    // which is authoritative — the failed flush must not re-mark the departed session.
    [Fact]
    public async Task A_failed_flush_is_not_retried_once_the_player_has_logged_out()
    {
        bool previousRethrow = GameDiagnostics.RethrowBackgroundExceptions;
        try
        {
            GameDiagnostics.RethrowBackgroundExceptions = false;
            var repo = new FailingSavesRepository();
            var world = new GameWorld(new InMemoryGameDatabase(), repo) { PlayerWriteBehindEnabled = true };
            var player = NewPlayer("Denethor");
            repo.SavePlayer(player);
            world.AddPlayer(player);

            world.MarkPlayerDirty(player);
            repo.FailuresRemaining = 1;
            repo.BeforeFailure = () => world.RemovePlayer(player); // logout lands while the write is failing

            await world.FlushPendingPlayerSavesAsync();
            int afterLogout = repo.SaveCount; // includes the synchronous logout save

            await world.FlushPendingPlayerSavesAsync();
            Assert.Equal(afterLogout, repo.SaveCount); // no retry of the departed session
        }
        finally
        {
            GameDiagnostics.RethrowBackgroundExceptions = previousRethrow;
            GameDiagnostics.Reset();
        }
    }
}
