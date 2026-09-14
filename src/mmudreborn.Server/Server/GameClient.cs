using System;
using System.Threading;
using System.Threading.Tasks;
using CWGaming.Shared;

namespace mmudreborn.Server;

/// <summary>
/// The mmudreborn door's per-session connection wrapper. It wraps the generic
/// <see cref="IBbsConnection"/> the BBS host owns, re-attaches the game-specific
/// <see cref="Player"/>/<see cref="Session"/> and the MUD prompt, and delegates all I/O straight to the
/// underlying connection. One is created when the door starts a session; it registers itself in the
/// connection's <see cref="IBbsConnection.AppData"/> slot (so the host can recover the attached player
/// on a dropped connection) and installs the <see cref="IBbsConnection.PromptProvider"/> the host
/// re-emits after broadcasts/reprompts.
/// </summary>
public sealed class GameClient : IGameClient, IBbsIdentity
{
    private readonly IBbsConnection _conn;
    private readonly GameWorld _world;

    public GameClient(IBbsConnection connection, GameWorld world)
    {
        _conn = connection;
        _world = world;
        _conn.AppData = this;
        // The host's reprompt path (after a broadcast or deferred-broadcast flush) draws this. Empty
        // means "no reprompt" — byte-for-byte equivalent to the old guard
        // (Player != null && !Player.SuppressBroadcastReprompt) ? CurrentPrompt : nothing.
        _conn.PromptProvider = () =>
            Player is { SuppressBroadcastReprompt: false } p ? MudAnsi.Prompt(p) : string.Empty;
    }

    /// <summary>The underlying host connection (e.g. for transport-specific operations the door needs).</summary>
    public IBbsConnection Connection => _conn;

    public Game.Player? Player { get; set; }
    public IGameSession? Session { get; set; }
    public string CurrentPrompt => Player is null ? string.Empty : MudAnsi.Prompt(Player);

    // IBbsIdentity — the generic view the BBS host reads (via AppData) so it never touches Player.
    // All members return empty/false when no realm character is attached (BBS login / main menu).
    public bool HasActiveCharacter => Player != null;
    public string CharacterName => Player?.Name ?? string.Empty;

    public string PresenceUserName => Player is { } p
        ? (string.IsNullOrWhiteSpace(p.BbsUserId) ? p.Name : p.BbsUserId)
        : string.Empty;

    // The ;who location label while in the realm. The door composes its own "title card" for the BBS:
    // the door id plus the active character name, e.g. "mmudreborn (Ptery)". An account with no realm
    // character (BBS login / main menu) reports nothing here, so it just shows its BBS location.
    public string PresenceLocation => Player is { } p
        ? $"{HostedAppIds.Mmudreborn} ({p.Name})"
        : string.Empty;

    // Rich location for a ticket draft: "<room name> (map/room)", or just "map/room" when unnamed.
    public string TicketLocation
    {
        get
        {
            if (Player is not { } p)
                return string.Empty;

            var room = _world.GetRoom(p.CurrentMapNumber, p.CurrentRoomNumber);
            string roomName = room?.Name?.Trim() ?? string.Empty;
            return string.IsNullOrWhiteSpace(roomName)
                ? $"{p.CurrentMapNumber}/{p.CurrentRoomNumber}"
                : $"{roomName} ({p.CurrentMapNumber}/{p.CurrentRoomNumber})";
        }
    }

    public bool Connected => _conn.Connected;
    public string CurrentBbsUserName { get => _conn.CurrentBbsUserName; set => _conn.CurrentBbsUserName = value; }
    public string CurrentHostedAppId { get => _conn.CurrentHostedAppId; set => _conn.CurrentHostedAppId = value; }
    public string CurrentHostedWorldId { get => _conn.CurrentHostedWorldId; set => _conn.CurrentHostedWorldId = value; }
    public string CurrentHostedLocation { get => _conn.CurrentHostedLocation; set => _conn.CurrentHostedLocation = value; }
    public bool HasPendingInput => _conn.HasPendingInput;
    public bool IsAtLineStart => _conn.IsAtLineStart;
    public Func<string>? PromptProvider { get => _conn.PromptProvider; set => _conn.PromptProvider = value; }
    public object? AppData { get => _conn.AppData; set => _conn.AppData = value; }

    public Task SendAsync(string text) => _conn.SendAsync(text);
    public Task SendLineAsync(string text = "") => _conn.SendLineAsync(text);
    public void BeginBuffering() => _conn.BeginBuffering();
    public void FlushOutput() => _conn.FlushOutput();
    public bool GmcpEnabled => _conn.GmcpEnabled;
    public Task SendGmcpAsync(string package, string payloadJson) => _conn.SendGmcpAsync(package, payloadJson);
    public bool TryDeferBroadcastLine(string text, bool reprompt, bool prependLineBreak = true) => _conn.TryDeferBroadcastLine(text, reprompt, prependLineBreak);
    public Task FlushDeferredBroadcastLinesAsync() => _conn.FlushDeferredBroadcastLinesAsync();
    public void BeginHeldOutput() => _conn.BeginHeldOutput();
    public void EndHeldOutput() => _conn.EndHeldOutput();
    public Task EnsureNewLineAsync() => _conn.EnsureNewLineAsync();
    public Task PrepareForBroadcastAsync(bool forceClearCurrentLine = false) => _conn.PrepareForBroadcastAsync(forceClearCurrentLine);
    public Task ClearCurrentLineAsync() => _conn.ClearCurrentLineAsync();
    public Task RedrawPromptWithInputAsync(string prompt) => _conn.RedrawPromptWithInputAsync(prompt);
    public Task DiscardBufferedLineEndingsAsync(CancellationToken ct = default) => _conn.DiscardBufferedLineEndingsAsync(ct);
    public Task<string?> ReadLineAsync(CancellationToken ct = default) => _conn.ReadLineAsync(ct);
    public Task<string?> ReadLineEchoAsync(bool echo = true, CancellationToken ct = default) => _conn.ReadLineEchoAsync(echo, ct);
    public Task<string?> ReadLineEchoAsync(int timeoutMs, bool echo = true, CancellationToken ct = default) => _conn.ReadLineEchoAsync(timeoutMs, echo, ct);
    public Task<string?> ReadLineMaskedAsync(char mask = '*', CancellationToken ct = default) => _conn.ReadLineMaskedAsync(mask, ct);
    public Task<string?> ReadKeyAsync(CancellationToken ct = default) => _conn.ReadKeyAsync(ct);
    public Task<string?> ReadKeyAsync(int timeoutMs, CancellationToken ct = default) => _conn.ReadKeyAsync(timeoutMs, ct);
    public void SetCommandHistoryEnabled(bool enabled) => _conn.SetCommandHistoryEnabled(enabled);
    public void SetEcho(bool enabled) => _conn.SetEcho(enabled);
    public Task ReassertGameplayInputModeAsync() => _conn.ReassertGameplayInputModeAsync();
    public void Disconnect() => _conn.Disconnect();
}
