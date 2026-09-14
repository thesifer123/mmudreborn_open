using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class GameWorld
{
    // Proactive health alerting. Rather than waiting for a sysop to poll SYSOP DIAG, the slow (30s) tick
    // runs a lightweight check over the same diagnostics counters and, when something looks wrong, pushes
    // a one-line alert to every online sysop and the server console. Detection is delta-based (what got
    // worse since the last check) so a single transient blip surfaces once; a cooldown stops a sustained
    // problem from spamming. All conditions are derived from data we already collect — no new hot-path cost.
    public bool HealthAlertsEnabled { get; set; }

    private static readonly TimeSpan HealthAlertCooldown = TimeSpan.FromMinutes(5);
    // A gate held this long is a stall a player would actually feel (vs. the 5ms "slow sample" threshold,
    // which is just instrumentation noise). Tunable enough that normal persist-flush blips never trip it.
    private const int HealthGateStallMs = 100;
    // Pending write-behind saves piling up this high means the flush loop can't keep up — usually a slow
    // or unreachable database.
    private const int HealthSaveBacklogThreshold = 100;
    // Room-spell-tick overlaps are benign in isolation — an overlap just means the tick queued behind
    // another gate holder and serialized (nothing dropped). A handful per 30s window is normal lock
    // contention noise. Only a SUSTAINED burst (a meaningful fraction of the ~30 ticks/window) signals
    // real gate pressure worth an alert. World-tick SKIPS are different — those drop work, so they alert
    // at the first occurrence (below).
    private const int HealthRoomSpellOverlapThreshold = 8;

    private long _healthLastSkippedWorldTicks;
    private long _healthLastRoomSpellOverlaps;
    private long _healthLastBackgroundExceptions;
    private DateTime _healthLastCheckUtc = DateTime.MinValue;
    private DateTime _healthLastAlertUtc = DateTime.MinValue;

    // Called once per slow (30s) tick. Compares the current diagnostics counters to the previous check,
    // raises an alert for anything that regressed, then rolls the baselines forward. The first call only
    // establishes the baseline (no alert), so a fresh server doesn't fire on accumulated startup counts.
    internal void RunHealthMonitor(DateTime nowUtc)
    {
        var gate = GameDiagnostics.GetGateHoldDiagnosticsSnapshot();
        var world = GameDiagnostics.GetWorldTickDiagnosticsSnapshot();
        var roomSpell = GameDiagnostics.GetRoomSpellTickDiagnosticsSnapshot();
        long backgroundExceptions = GameDiagnostics.BackgroundExceptionCount;

        bool firstRun = _healthLastCheckUtc == DateTime.MinValue;

        long backgroundErrorDelta = backgroundExceptions - _healthLastBackgroundExceptions;
        // Keep these two SEPARATE: a world-tick skip drops a whole simulation pass (real overload); a
        // room-spell overlap merely serialized behind the gate (benign unless sustained). Conflating them
        // is what made a single benign overlap shout "world simulation overloaded".
        long worldSkipDelta = world.SkippedTicks - _healthLastSkippedWorldTicks;
        long roomSpellOverlapDelta = roomSpell.OverlapTicks - _healthLastRoomSpellOverlaps;

        // When overlaps are worth reporting, name the most recent thing they queued behind so the alert
        // points at the culprit (combat / a slow tick / a command) instead of just a count.
        string overlapCause = GameDiagnostics.GetRecentRoomSpellOverlaps() is { Length: > 0 } overlaps
            ? overlaps[^1].Cause
            : "";

        // Worst NEW gate stall since the last check (the ring only keeps holds over the slow-sample
        // threshold; we filter to ones recorded this window and at/over the felt-stall threshold).
        var worstStall = gate.RecentSlowHolds
            .Where(sample => sample.AtUtc > _healthLastCheckUtc && sample.Milliseconds >= HealthGateStallMs)
            .OrderByDescending(sample => sample.Milliseconds)
            .FirstOrDefault();

        int saveBacklog = _dirtyPlayers.Count;

        // Roll baselines forward BEFORE any early-out so a disabled/first-run window doesn't later report a
        // giant delta the moment it's enabled.
        _healthLastSkippedWorldTicks = world.SkippedTicks;
        _healthLastRoomSpellOverlaps = roomSpell.OverlapTicks;
        _healthLastBackgroundExceptions = backgroundExceptions;
        _healthLastCheckUtc = nowUtc;

        if (firstRun || !HealthAlertsEnabled)
            return;

        string lastErrorText = GameDiagnostics.LastBackgroundException is { } ex
            ? $"{ex.GetType().Name}: {ex.Message}"
            : "unknown";

        var reasons = BuildHealthAlertReasons(
            backgroundErrorDelta, lastErrorText,
            worldSkipDelta,
            roomSpellOverlapDelta, overlapCause,
            worstStall?.Milliseconds ?? 0, worstStall?.Source ?? "",
            saveBacklog);

        if (reasons.Count == 0)
            return;

        // Cooldown: a sustained problem keeps tripping every 30s; only re-alert once per cooldown window.
        if (nowUtc - _healthLastAlertUtc < HealthAlertCooldown)
            return;
        _healthLastAlertUtc = nowUtc;

        string summary = string.Join("; ", reasons);
        Console.WriteLine($"[MMUD HEALTH] {summary}");

        string line = $"{MudAnsi.BrightYellow}{MudAnsi.BgRed}[REALM HEALTH]{MudAnsi.Reset}{MudAnsi.BrightYellow} {summary}. Check SYSOP DIAG.{MudAnsi.Reset}";
        foreach (var sysop in _onlinePlayers.Values.Where(player => player.IsSysop).ToList())
            SendToPlayer(sysop.Name, line, reprompt: true, prependLineBreak: true);
    }

    // Pure formatter for the tripped conditions — factored out so the thresholds and wording are unit
    // testable without the tick/host/session stack.
    internal static List<string> BuildHealthAlertReasons(
        long backgroundErrorDelta, string lastErrorText,
        long worldSkipDelta,
        long roomSpellOverlapDelta, string overlapCause,
        double worstGateStallMs, string gateStallSource,
        int saveBacklog)
    {
        var reasons = new List<string>();

        if (backgroundErrorDelta > 0)
            reasons.Add($"{backgroundErrorDelta} background error(s) (last: {Truncate(lastErrorText, 120)})");

        // Dropped simulation passes = real overload; alert on the first one.
        if (worldSkipDelta > 0)
            reasons.Add($"world simulation overloaded ({worldSkipDelta} world tick(s) dropped — previous tick still running)");

        // Overlaps only matter in bulk; a sustained burst means the world lock is genuinely contended.
        if (roomSpellOverlapDelta >= HealthRoomSpellOverlapThreshold)
        {
            string causeSuffix = string.IsNullOrEmpty(overlapCause) ? "" : $", mostly behind '{overlapCause}'";
            reasons.Add($"world lock contended ({roomSpellOverlapDelta} room-spell tick overlaps in the last 30s{causeSuffix}) — see SYSOP DIAG");
        }

        if (worstGateStallMs >= HealthGateStallMs)
            reasons.Add($"game-state lock stalled {worstGateStallMs.ToString("F0", CultureInfo.InvariantCulture)}ms (source {gateStallSource})");

        if (saveBacklog >= HealthSaveBacklogThreshold)
            reasons.Add($"player-save backlog is {saveBacklog} (database may be slow)");

        return reasons;
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..(maxLength - 1)] + "~";
}
