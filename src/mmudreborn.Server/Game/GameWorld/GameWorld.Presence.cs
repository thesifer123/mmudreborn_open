using Npgsql;
using mmudreborn.Game;

namespace mmudreborn.Server;

/// <summary>
/// Online-presence publisher: mirrors the live in-memory <c>_onlinePlayers</c> set into a small
/// <c>public.online_players</c> table in the shared realm DB so the web explorer can show a "who's
/// online" panel. The game is the single source of truth; the table is a self-healing snapshot
/// rewritten on every enter/leave (and reset on boot so a prior crash leaves no ghosts behind).
///
/// This reuses <see cref="RealmBusConnectionString"/> (the same realm DB the gossip bus writes to).
/// When it is unset (tests), presence is a no-op. Writes are fire-and-forget on a background task and
/// never touch the world gate, so a slow/absent DB can't stall the simulation.
/// </summary>
public partial class GameWorld
{
    // Serialises presence writes so overlapping enter/leave events can't interleave partial rewrites.
    private readonly SemaphoreSlim _presenceLock = new(1, 1);

    /// <summary>Create the presence table if needed and clear any stale rows left by a previous run.</summary>
    internal void InitOnlinePresence()
    {
        if (string.IsNullOrWhiteSpace(RealmBusConnectionString))
            return;
        _ = Task.Run(async () =>
        {
            await _presenceLock.WaitAsync();
            try
            {
                await using var conn = new NpgsqlConnection(RealmBusConnectionString);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    """
                    CREATE TABLE IF NOT EXISTS public.online_players (
                        name       text PRIMARY KEY,
                        lastname   text        NOT NULL DEFAULT '',
                        level      integer     NOT NULL DEFAULT 0,
                        classid    integer     NOT NULL DEFAULT 0,
                        raceid     integer     NOT NULL DEFAULT 0,
                        alignment  integer     NOT NULL DEFAULT 0,
                        issysop    boolean     NOT NULL DEFAULT false,
                        updatedat  timestamptz NOT NULL DEFAULT now()
                    );
                    TRUNCATE public.online_players;
                    """, conn);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[presence] init failed: {ex.Message}");
            }
            finally
            {
                _presenceLock.Release();
            }
        });
    }

    /// <summary>
    /// Rewrite the presence table to match the current online roster and notify web listeners. Safe to
    /// call from under the world gate (it only queues a background write). The write re-reads the live
    /// roster at write time, so out-of-order enter/leave calls still converge on the true set.
    /// </summary>
    public void PublishOnlinePresence()
    {
        if (string.IsNullOrWhiteSpace(RealmBusConnectionString))
            return;
        _ = Task.Run(async () =>
        {
            await _presenceLock.WaitAsync();
            try
            {
                await WriteOnlinePresenceAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[presence] publish failed: {ex.Message}");
            }
            finally
            {
                _presenceLock.Release();
            }
        });
    }

    private async Task WriteOnlinePresenceAsync()
    {
        // Same visibility rules as a non-sysop WHO: no test accounts, no one who has left the Realm to
        // train, and no sys-invisible sysops. The web viewer is treated as an ordinary player.
        var roster = GetAllOnlinePlayers()
            .Where(p => !p.IsTestAccount)
            .Where(p => !p.IsOutOfRealm)
            .Where(p => !p.IsSysopInvisible)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var conn = new NpgsqlConnection(RealmBusConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        await using (var truncate = new NpgsqlCommand("DELETE FROM public.online_players;", conn, tx))
            await truncate.ExecuteNonQueryAsync();

        foreach (var p in roster)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO public.online_players (name, lastname, level, classid, raceid, alignment, issysop, updatedat)
                VALUES (@name, @lastname, @level, @classid, @raceid, @alignment, @issysop, now());
                """, conn, tx);
            insert.Parameters.AddWithValue("name", p.Name);
            insert.Parameters.AddWithValue("lastname", p.LastName ?? string.Empty);
            insert.Parameters.AddWithValue("level", p.Level);
            insert.Parameters.AddWithValue("classid", p.ClassId);
            insert.Parameters.AddWithValue("raceid", p.RaceId);
            insert.Parameters.AddWithValue("alignment", p.Alignment);
            insert.Parameters.AddWithValue("issysop", p.IsSysop);
            await insert.ExecuteNonQueryAsync();
        }

        // Live push for the web (SSE): payload is just a ping — the panel refetches the small list.
        await using (var notify = new NpgsqlCommand("SELECT pg_notify('presence_feed', @n);", conn, tx))
        {
            notify.Parameters.AddWithValue("n", roster.Count.ToString());
            await notify.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}
