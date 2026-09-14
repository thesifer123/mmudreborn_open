using System.Text;
using mmudreborn.Game;

namespace mmudreborn.Server;

/// <summary>
/// Realm message bus (phase 2): lets an external process — the web explorer — push a broadcast
/// (gossip/auction, later telepath, …) into the live realm WITHOUT the game polling. The web writes
/// the row + fires Postgres NOTIFY on the shared realm DB; <see cref="RealmBusListener"/> receives the
/// push and calls back here to inject it through the SAME broadcast path a native in-game gossip uses,
/// so the on-wire control/colour codes are byte-for-byte identical (Megamud-safe).
/// </summary>
public partial class GameWorld
{
    /// <summary>Realm-DB connection string used by the LISTEN loop. Unset (e.g. in tests) => no listener.</summary>
    public string? RealmBusConnectionString { get; set; }

    private RealmBusListener? _realmBus;

    internal void StartRealmBus()
    {
        if (string.IsNullOrWhiteSpace(RealmBusConnectionString))
            return;
        _realmBus = new RealmBusListener(RealmBusConnectionString!, this);
        _realmBus.Start();
    }

    internal void StopRealmBus()
    {
        _realmBus?.Stop();
        _realmBus = null;
    }

    /// <summary>
    /// Inject an externally-originated realm broadcast (from the web) into the live realm. Formats it
    /// exactly like a native gossip/auction and delivers through <see cref="BroadcastChannelToRealm"/>
    /// under the world gate. The text is sanitised first (control/ESC bytes stripped, length capped) so
    /// an untrusted web message can't inject ANSI, forge lines, or trip the Megamud long-line freeze.
    /// </summary>
    public async Task InjectRealmBroadcastAsync(string sender, string channel, string message)
    {
        sender = SanitizeBroadcastText(sender, 40);
        message = SanitizeBroadcastText(message, 200);
        if (sender.Length == 0 || message.Length == 0)
            return;

        bool isAuction = string.Equals(channel, "auction", StringComparison.OrdinalIgnoreCase);
        string verb = isAuction ? "auctions" : "gossips";
        Func<Player, bool> canReceive = isAuction
            ? (p => p.ReceiveAuctionEnabled)
            : (p => p.ReceiveGossipEnabled);

        // Identical format to CommandParser.HandleRealmBroadcastChannel — keep these in lockstep.
        string line = $"{MudAnsi.White}{sender} {verb}: {MudAnsi.Magenta}{message}{MudAnsi.Reset}";

        await WorldStateGate.WaitAsync();
        try
        {
            BroadcastChannelToRealm(sender, line, except: null, reprompt: true, prependLineBreak: true, canReceive: canReceive, includeSender: true);
        }
        finally
        {
            WorldStateGate.Release();
        }
    }

    /// <summary>
    /// Inject an externally-originated PRIVATE telepath (from the web) to a single online character.
    /// Same trust boundary as <see cref="InjectRealmBroadcastAsync"/> (control/ESC stripped, length
    /// capped), and delivered through the SAME path a native in-game telepath uses so the on-wire bytes
    /// (prefix + recipient-palette message colour) are identical. If the recipient is not currently in
    /// the realm the message is dropped here — the web already stored/showed the sender's copy — and if
    /// the recipient ignores the sender, <see cref="SendChannelMessage"/> silently declines delivery.
    /// </summary>
    public async Task InjectRealmTelepathAsync(string sender, string recipient, string message)
    {
        sender = SanitizeBroadcastText(sender, 40);
        recipient = SanitizeBroadcastText(recipient, 40);
        message = SanitizeBroadcastText(message, 250);
        if (sender.Length == 0 || recipient.Length == 0 || message.Length == 0)
            return;

        await WorldStateGate.WaitAsync();
        try
        {
            var target = FindOnlinePlayer(recipient);
            if (target == null)
                return; // not in the realm right now; the web copy is already persisted/shown.

            // Byte-faithful with CommandParser.HandleTelepath: "Green {sender} telepaths:" then the
            // message in the RECIPIENT's telepath palette colour (cat 13).
            string messageColor = GameColorPalettes.Resolve(target.PaletteId).Get(GameColorRole.TelepathMessage);
            string line = $"{MudAnsi.Green}{sender} telepaths:{MudAnsi.Reset} {messageColor}{message}{MudAnsi.Reset}";

            // SendChannelMessage re-checks online status and honours the recipient's ignore list.
            SendChannelMessage(sender, target.Name, line, reprompt: true, prependLineBreak: true);
        }
        finally
        {
            WorldStateGate.Release();
        }
    }

    /// <summary>
    /// Inject a server-originated SYSTEM broadcast (e.g. a restart countdown relayed from a sibling realm)
    /// to every player in THIS realm. Unlike <see cref="InjectRealmBroadcastAsync"/> this does NOT sanitise
    /// the text: the line was composed by the server itself (a sysop's restart), already carries its own
    /// ANSI colour, and is delivered verbatim through the same <see cref="BroadcastToRealm"/> path a native
    /// server notice uses. Never route untrusted (web) input here.
    /// </summary>
    public async Task InjectRealmSystemBroadcastAsync(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        await WorldStateGate.WaitAsync();
        try
        {
            BroadcastToRealm(line, reprompt: true);
        }
        finally
        {
            WorldStateGate.Release();
        }
    }

    // Drop every control character (ESC, CR/LF, tab, …) so untrusted input can't carry ANSI escape
    // sequences or split into multiple lines, then hard-cap the length.
    private static string SanitizeBroadcastText(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (!char.IsControl(ch))
                sb.Append(ch);
        }

        string cleaned = sb.ToString().Trim();
        return cleaned.Length > maxLength ? cleaned[..maxLength] : cleaned;
    }
}
