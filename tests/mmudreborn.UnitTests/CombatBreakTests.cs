using System.Threading;
using System.Threading.Tasks;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #97: `break` was gated on the OTHER side still pressuring you (a live monster target / incoming
// attacker / live player target in the room). A player who ENGAGED combat and then lost that pressure
// (the target died, fled, or left the room) stayed stuck in their own loop with `break` a no-op. Stock
// BREAK ends YOUR engaged loop unconditionally — InCombat is set only on
// player-initiated engage, so gating on it alone is correct. These pin the fix.
public sealed class CombatBreakTests
{
    [Fact]
    public async Task Break_ends_engaged_combat_loop_even_with_no_live_target()
    {
        var (world, client, player, parser) = Setup();
        player.InCombat = true;   // engaged, but no CombatTarget / PlayerCombatTarget / incoming attacker

        await parser.ProcessCommand("break");

        Assert.False(player.InCombat);
        Assert.Contains(client.Lines, l => l.Contains("*Combat Off*"));
    }

    [Fact]
    public async Task Break_with_nothing_active_reports_no_effect()
    {
        var (world, client, player, parser) = Setup();

        await parser.ProcessCommand("break");

        Assert.False(player.InCombat);
        Assert.Contains(client.Lines, l => l.Contains("had no effect"));
    }

    private static (GameWorld World, CaptureClient Client, Player Player, CommandParser Parser) Setup()
    {
        var world = new GameWorld(new InMemoryGameDatabase(), new InMemoryPlayerRepository());
        var client = new CaptureClient();
        var player = new Player { Name = "Tester", CurrentMapNumber = 1, CurrentRoomNumber = 1, Client = client };
        client.Player = player;
        return (world, client, player, new CommandParser(client, world, player));
    }

    private sealed class CaptureClient : IGameClient
    {
        public System.Collections.Generic.List<string> Lines { get; } = [];
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
}
