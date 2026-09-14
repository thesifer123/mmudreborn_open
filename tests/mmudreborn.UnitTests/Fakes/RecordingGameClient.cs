using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using mmudreborn.Game;
using mmudreborn.Server;

namespace mmudreborn.UnitTests.Fakes;

// Minimal IGameClient for unit tests: captures every emitted line in Lines, no-ops everything else.
public sealed class RecordingGameClient : IGameClient
{
    public List<string> Lines { get; } = [];

    public bool Connected => true;
    public Player? Player { get; set; }
    public IGameSession? Session { get; set; }
    public string CurrentBbsUserName { get; set; } = "";
    public string CurrentHostedAppId { get; set; } = "";
    public string CurrentHostedWorldId { get; set; } = "";
    public string CurrentHostedLocation { get; set; } = "";
    public bool HasPendingInput => false;
    public bool IsAtLineStart => true;
    public System.Func<string>? PromptProvider { get; set; }
    public object? AppData { get; set; }

    // Lets a test say "the BOARD closed this connection" (duplicate login, sysop kick, shutdown) rather
    // than "the peer vanished" — the distinction the drop-carrier penalty turns on.
    public bool DisconnectedByHost { get; set; }
    public Task SendAsync(string text) => Task.CompletedTask;
    public Task SendLineAsync(string text = "") { Lines.Add(text); return Task.CompletedTask; }
    public void BeginBuffering() { }
    public void FlushOutput() { }
    public bool TryDeferBroadcastLine(string text, bool reprompt, bool prependLineBreak = true) => false;
    public Task FlushDeferredBroadcastLinesAsync() => Task.CompletedTask;
    public Task EnsureNewLineAsync() => Task.CompletedTask;
    public Task PrepareForBroadcastAsync(bool forceClearCurrentLine = false) => Task.CompletedTask;
    public Task ClearCurrentLineAsync() => Task.CompletedTask;
    public Task RedrawPromptWithInputAsync(string prompt) => Task.CompletedTask;
    public Task DiscardBufferedLineEndingsAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<string?> ReadLineAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> ReadLineEchoAsync(bool echo = true, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> ReadLineEchoAsync(int timeoutMs, bool echo = true, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> ReadLineMaskedAsync(char mask = '*', CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> ReadKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task<string?> ReadKeyAsync(int timeoutMs, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public void SetCommandHistoryEnabled(bool enabled) { }
    public void SetEcho(bool enabled) { }
    public void Disconnect() { }
}
