using System.Text.Json;
using Npgsql;

namespace mmudreborn.Server;

/// <summary>
/// Holds one persistent Postgres <c>LISTEN realm_bus</c> connection and pushes each notification into
/// the <see cref="GameWorld"/>. This is the low-overhead web→game path: no polling, no HTTP surface on
/// the game — the shared realm DB is the bus. Payload is JSON: <c>{"type":"gossip","from":..,"msg":..}</c>.
/// Reconnects with backoff if the connection drops.
/// </summary>
internal sealed class RealmBusListener
{
    private const string Channel = "realm_bus";

    private readonly string _connectionString;
    private readonly GameWorld _world;
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public RealmBusListener(string connectionString, GameWorld world)
    {
        _connectionString = connectionString;
        _world = world;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); }
        catch { /* already disposed */ }
        _cts = null;
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ListenLoopAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[realm-bus] listener error: {ex.Message}; reconnecting in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        conn.Notification += OnNotification;

        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", conn))
            await listen.ExecuteNonQueryAsync(ct);

        Console.WriteLine($"[realm-bus] listening on '{Channel}'.");
        while (!ct.IsCancellationRequested)
            await conn.WaitAsync(ct);
    }

    private void OnNotification(object? sender, NpgsqlNotificationEventArgs e)
    {
        // Handle off the notification callback so a slow gate acquire never blocks the listen loop.
        string payload = e.Payload;
        _ = Task.Run(() => DispatchAsync(payload));
    }

    private async Task DispatchAsync(string payload)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? (t.GetString() ?? "") : "";
            string from = root.TryGetProperty("from", out var f) ? (f.GetString() ?? "") : "";
            string to = root.TryGetProperty("to", out var r) ? (r.GetString() ?? "") : "";
            string msg = root.TryGetProperty("msg", out var m) ? (m.GetString() ?? "") : "";
            string name = root.TryGetProperty("name", out var nm) ? (nm.GetString() ?? "") : "";

            switch (type)
            {
                case "gossip":
                case "auction":
                    await _world.InjectRealmBroadcastAsync(from, type, msg);
                    break;
                // Board-wide server notice (e.g. a restart countdown) pushed DOWN by the BBS front into
                // every realm. Delivered verbatim (it carries its own colour) to all players in this realm.
                case "system":
                    await _world.InjectRealmSystemBroadcastAsync(msg);
                    break;
                case "telepath":
                    await _world.InjectRealmTelepathAsync(from, to, msg);
                    break;
                // Web presence for the telepath bridge: a character viewing the web (dock open) but not
                // necessarily in the realm. Tracked separately from the online roster (never on WHO).
                case "web_present":
                    _world.MarkWebPresent(name);
                    break;
                case "web_gone":
                    _world.MarkWebGone(name);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[realm-bus] bad notification payload: {ex.Message}");
        }
    }
}
