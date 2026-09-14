using System.Threading;
using System.Threading.Tasks;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Phase 2/5 of the DoT wiring (reference_dot_message_model): once a duration-bearing harm spell is on a
// player's active-spell slot, the medium-tick upkeep (ProcessActiveSpellUpkeep -> ApplyUpkeepDamageOverTime,
// the routine spell upkeep, abilities 1/8) must drain `magnitude` HP per tick — SILENTLY, no
// damage number — for exactly `duration` ticks, then drop the slot. This pins that end-to-end lifecycle so
// a regression can't turn a DoT back into a no-op or an instant hit.
public sealed class DotLifecycleTests
{
    [Fact]
    public void Active_dot_drains_hp_each_medium_tick_then_expires()
    {
        const int spellId = 999, perTick = 5, duration = 3, startHp = 100;
        var db = new InMemoryGameDatabase();
        db.Spells[spellId] = new GameSpell { Number = spellId, Name = "burning", Duration = duration, Abilities = { [1] = 0 } };

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var player = new Player { Name = "Tester", CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = startHp, MaxHP = startHp, Client = new NoopClient() };
        world.AddPlayer(player);

        // Magnitude is stored in the slot (ActiveSpell.CastLevel); ability 1 with AbilVal 0 falls back to it.
        player.AddOrRefreshActiveSpell(spellId, castLevel: perTick, duration: duration);

        world.AdvanceMediumTicksForTests(1);
        Assert.Equal(startHp - perTick, player.CurrentHP);          // ticked once
        Assert.True(player.HasActiveSpell(spellId));

        world.AdvanceMediumTicksForTests(2);
        Assert.Equal(startHp - perTick * duration, player.CurrentHP); // 3 ticks total
        Assert.False(player.HasActiveSpell(spellId));                 // slot expired after `duration` ticks

        int hpAfterExpiry = player.CurrentHP;
        world.AdvanceMediumTicksForTests(3);
        Assert.Equal(hpAfterExpiry, player.CurrentHP);               // no further drain once expired
    }

    [Fact]
    public void Recasting_dot_refreshes_one_slot_without_stacking_or_instant_damage()
    {
        // The death-loop guard: a monster re-casting its DoT each combat round must REFRESH the single
        // slot (a re-cast overwrites the slot), never stack a second one and never deal
        // instant damage on the re-cast — and once re-casting stops it must still expire within `duration`
        // ticks (bounded, not "on fire forever").
        const int spellId = 999, perTick = 5, duration = 3, startHp = 100;
        var db = new InMemoryGameDatabase();
        db.Spells[spellId] = new GameSpell { Number = spellId, Name = "burning", Duration = duration, Abilities = { [1] = 0 } };

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var player = new Player { Name = "Tester", CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = startHp, MaxHP = startHp, Client = new NoopClient() };
        world.AddPlayer(player);

        player.AddOrRefreshActiveSpell(spellId, castLevel: perTick, duration: duration);
        world.AdvanceMediumTicksForTests(2);                       // HP -10, duration 3 -> 1

        // Monster re-casts (refresh): no instant damage, still exactly one slot, timer reset to full.
        int hpBeforeRecast = player.CurrentHP;
        player.AddOrRefreshActiveSpell(spellId, castLevel: perTick, duration: duration);
        Assert.Equal(hpBeforeRecast, player.CurrentHP);            // re-cast itself deals 0 HP
        Assert.Single(player.ActiveSpells);                        // refreshed, NOT stacked

        // Re-casting stops (monster dead / fled): runs at most `duration` more ticks, then expires.
        world.AdvanceMediumTicksForTests(duration);
        Assert.False(player.HasActiveSpell(spellId));
        Assert.Equal(hpBeforeRecast - perTick * duration, player.CurrentHP);
    }

    private sealed class NoopClient : IGameClient
    {
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
        public Task SendLineAsync(string text = "") => Task.CompletedTask;
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
