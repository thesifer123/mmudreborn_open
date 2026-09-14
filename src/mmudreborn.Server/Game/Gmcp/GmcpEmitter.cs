using System.Linq;
using System.Text.Json;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

/// <summary>
/// Builds and emits GMCP packages (telnet option 201) that describe live game state to
/// clients that opted in (e.g. GigaMud). Every method is a no-op for clients that did not
/// negotiate GMCP, so MegaMUD and raw-telnet sessions are byte-for-byte unaffected.
///
/// Package shapes are intentionally small and stable so the client can treat them as a
/// typed state feed instead of scraping ANSI text:
///   Char.Vitals  { hp, maxhp, mana, maxmana, level }
///   Room.Info    { num: "map,room", map, room, name, exits: ["n","s",...] }
/// </summary>
public static class GmcpEmitter
{
    // Compact JSON (no indentation); default escaping is safe for telnet subnegotiation
    // because the framing layer doubles any IAC byte before transmission.
    private static readonly JsonSerializerOptions JsonOptions = new();

    // Map the world's exit-direction keys (which may be full words) onto the short codes
    // mapping clients use ("n","se",...), so Room.Info exits line up with their room graph.
    private static readonly Dictionary<string, string> ShortDirections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w",
        ["northeast"] = "ne", ["northwest"] = "nw", ["southeast"] = "se", ["southwest"] = "sw",
        ["up"] = "u", ["down"] = "d"
    };

    private static string ToShortDirection(string direction)
        => ShortDirections.TryGetValue(direction, out var code) ? code : direction.ToLowerInvariant();

    public static Task SendVitalsAsync(IGameClient client, Player player)
    {
        if (!client.GmcpEnabled)
            return Task.CompletedTask;

        var payload = new
        {
            hp = player.CurrentHP,
            maxhp = player.MaxHP,
            mana = player.CurrentMana,
            maxmana = player.MaxMana,
            level = player.Level
        };

        return client.SendGmcpAsync("Char.Vitals", JsonSerializer.Serialize(payload, JsonOptions));
    }

    public static Task SendRoomInfoAsync(IGameClient client, GameWorld world, Player player, Room room)
    {
        if (!client.GmcpEnabled)
            return Task.CompletedTask;

        // GetVisibleExits is keyed by normalized direction; emit lowercase short codes
        // ("n","se",...) so the client can map directly onto its room graph.
        var exits = world.GetVisibleExits(player, room).Keys
            .Select(ToShortDirection)
            .ToArray();

        var payload = new
        {
            num = $"{room.MapNumber},{room.RoomNumber}",
            map = room.MapNumber,
            room = room.RoomNumber,
            name = room.Name,
            exits
        };

        return client.SendGmcpAsync("Room.Info", JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Char.Status { combat, resting } — drives the client's auto-combat gating (only
    /// act when the player is idle at a prompt) and, later, auto-rest.
    /// </summary>
    public static Task SendStatusAsync(IGameClient client, Player player)
    {
        if (!client.GmcpEnabled)
            return Task.CompletedTask;

        var payload = new { combat = player.InCombat, resting = player.IsResting };
        return client.SendGmcpAsync("Char.Status", JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Room.Chars { monsters: [{name, display, align}], players: [name] } — the room's
    /// living occupants. `name` is the base template name for targeting (attack &lt;name&gt;);
    /// `display` carries the flavor prefix. The client classifies hostility from its own
    /// monster catalog (align is included as a hint).
    /// </summary>
    public static Task SendRoomCharsAsync(IGameClient client, GameWorld world, Player player)
    {
        if (!client.GmcpEnabled)
            return Task.CompletedTask;

        var monsters = world.GetMonstersInRoom(player.CurrentMapNumber, player.CurrentRoomNumber)
            .Select(monster => new
            {
                name = monster.Name,
                display = monster.DisplayName,
                align = monster.Template.Align
            })
            .ToArray();

        var players = world.GetPlayersInRoom(player.CurrentMapNumber, player.CurrentRoomNumber, player)
            .Where(other => !other.IsHidden && !other.IsSysopInvisible && !other.IsOutOfRealm)
            .Select(other => other.Name)
            .ToArray();

        var payload = new { monsters, players };
        return client.SendGmcpAsync("Room.Chars", JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>
    /// Emit the full per-turn state bundle (vitals + status + room occupants). Called at
    /// the top-of-loop prompt — the boundary where the server is idle awaiting input, which
    /// is exactly when the client may safely issue an automated action.
    /// </summary>
    public static async Task SendTurnStateAsync(IGameClient client, GameWorld world, Player player)
    {
        if (!client.GmcpEnabled)
            return;

        await SendVitalsAsync(client, player);
        await SendStatusAsync(client, player);
        await SendRoomCharsAsync(client, world, player);
    }
}
