using System.Collections.Concurrent;
using System.Text.Json;
using Npgsql;
using mmudreborn.Game;

namespace mmudreborn.Server;

/// <summary>
/// Tracks characters that are present on the WEB (their telepath dock is open) but not necessarily in
/// the realm, and delivers telepaths to them. This is a SEPARATE list from the online roster — it never
/// appears on WHO and doesn't touch <c>_onlinePlayers</c> — so a web-only player is reachable by name
/// without looking "in-realm". Presence is pushed in over <c>realm_bus</c> (web_present/web_gone) and is
/// time-boxed, so a missed "gone" (web crash) self-heals after the TTL.
/// </summary>
public partial class GameWorld
{
    private static readonly TimeSpan WebPresenceTtl = TimeSpan.FromSeconds(60);

    // name -> (canonical name, last heartbeat). Case-insensitive keys.
    private readonly ConcurrentDictionary<string, (string Name, DateTime LastSeen)> _webPresence =
        new(StringComparer.OrdinalIgnoreCase);

    private const string WebTelepathsDdl = """
        CREATE TABLE IF NOT EXISTS public.web_telepaths (
            id serial PRIMARY KEY,
            sender    citext NOT NULL,
            recipient citext NOT NULL,
            message   text   NOT NULL,
            createdat timestamptz NOT NULL DEFAULT now()
        );
        """;

    /// <summary>Record/refresh a character's web presence (a realm_bus web_present heartbeat).</summary>
    public void MarkWebPresent(string name)
    {
        name = SanitizeBroadcastText(name, 40);
        if (name.Length == 0)
            return;
        _webPresence[name] = (name, DateTime.UtcNow);
    }

    /// <summary>Drop a character's web presence (their telepath stream closed).</summary>
    public void MarkWebGone(string name)
    {
        name = SanitizeBroadcastText(name, 40);
        if (name.Length == 0)
            return;
        _webPresence.TryRemove(name, out _);
    }

    /// <summary>True if this exact character is currently present on the web.</summary>
    public bool IsWebPresent(string name)
        => _webPresence.TryGetValue(name, out var e) && e.LastSeen >= DateTime.UtcNow - WebPresenceTtl;

    /// <summary>
    /// Canonical names of every character currently present on the web (heartbeat within the TTL),
    /// case-insensitively sorted. Prunes stale entries as a side effect. Backs the in-game
    /// <c>web-who</c> command — the live web-presence set is exactly the telepath-eligible roster.
    /// </summary>
    public IReadOnlyList<string> GetWebPresentNames()
    {
        DateTime cutoff = DateTime.UtcNow - WebPresenceTtl;
        var live = new List<string>();
        foreach (var kv in _webPresence)
        {
            if (kv.Value.LastSeen >= cutoff)
                live.Add(kv.Value.Name);
            else
                _webPresence.TryRemove(kv.Key, out _); // prune stale on access
        }
        live.Sort(StringComparer.OrdinalIgnoreCase);
        return live;
    }

    /// <summary>Canonical name of a web-present character matching name (exact, else shortest prefix), or null.</summary>
    public string? ResolveWebPresent(string nameOrPrefix)
    {
        DateTime cutoff = DateTime.UtcNow - WebPresenceTtl;
        var live = new List<string>();
        foreach (var kv in _webPresence)
        {
            if (kv.Value.LastSeen >= cutoff)
                live.Add(kv.Value.Name);
            else
                _webPresence.TryRemove(kv.Key, out _); // prune stale on access
        }

        var exact = live.FirstOrDefault(n => n.Equals(nameOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;
        return live
            .Where(n => n.StartsWith(nameOrPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n.Length)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Persist a telepath to the shared web store and push it live to the recipient's (and sender's)
    /// browser via the <c>telepath_feed</c> SSE — the game→web half of the bridge, mirroring how native
    /// gossip is appended to GossipLog + gossip_feed. Fire-and-forget off the world gate on its own
    /// connection; a no-op when the realm DB isn't configured (tests without a bus).
    /// </summary>
    public void PublishTelepathToWeb(string sender, string recipient, string message)
    {
        if (string.IsNullOrWhiteSpace(RealmBusConnectionString))
            return;
        sender = SanitizeBroadcastText(sender, 40);
        recipient = SanitizeBroadcastText(recipient, 40);
        message = SanitizeBroadcastText(message, 250);
        if (sender.Length == 0 || recipient.Length == 0 || message.Length == 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await using var conn = new NpgsqlConnection(RealmBusConnectionString);
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();

                await using (var ensure = new NpgsqlCommand(WebTelepathsDdl, conn, tx))
                    await ensure.ExecuteNonQueryAsync();

                int id;
                DateTimeOffset createdAt;
                await using (var insert = new NpgsqlCommand(
                    "INSERT INTO public.web_telepaths (sender, recipient, message) VALUES (@s, @r, @m) RETURNING id, createdat",
                    conn, tx))
                {
                    insert.Parameters.AddWithValue("s", sender);
                    insert.Parameters.AddWithValue("r", recipient);
                    insert.Parameters.AddWithValue("m", message);
                    await using var reader = await insert.ExecuteReaderAsync();
                    await reader.ReadAsync();
                    id = reader.GetInt32(0);
                    createdAt = reader.GetFieldValue<DateTimeOffset>(1);
                }

                string payload = JsonSerializer.Serialize(new
                {
                    id,
                    from = sender,
                    to = recipient,
                    message,
                    createdat = createdAt.ToUniversalTime().ToString("o"),
                });
                await using (var notify = new NpgsqlCommand("SELECT pg_notify('telepath_feed', @p)", conn, tx))
                {
                    notify.Parameters.AddWithValue("p", payload);
                    await notify.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[telepath-web] publish failed: {ex.Message}");
            }
        });
    }
}
