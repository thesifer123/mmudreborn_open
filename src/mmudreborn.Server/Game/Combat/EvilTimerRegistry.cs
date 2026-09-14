using System;
using System.Collections.Generic;

namespace mmudreborn.Server;

/// <summary>
/// Port of the stock evil-timer relationship list (a singly-linked list of
/// directed edges, with add / query / star-display / retaliation / remove operations).
///
/// Each entry is an ordered <c>(aggressor → victim)</c> directed edge created when one
/// player opens PvP on another, recording who struck first and carrying the bit flags that drive the
/// "Also here:" name marker and the self-defence evil-points waiver.
///
/// Fidelity note: stock counts the timer down by an integer "duration" (11 on a melee open),
/// decremented once per slow pass. We approximate that countdown with a
/// wall-clock expiry (<see cref="EvilTimerWindow"/>) so the relationship list is the single source of
/// truth without needing a per-player slow tick. The directed-edge structure, bidirectional queries,
/// and flag bits are ported exactly; only the countdown unit differs (documented in pvp-parity.md §4).
/// </summary>
public sealed class EvilTimerRegistry
{
    // A PvP timer opens for a fixed duration (11 on the melee path),
    // decremented once per slow update. We keep the established 5-minute wall-clock window.
    public static readonly TimeSpan EvilTimerWindow = TimeSpan.FromMinutes(5);

    // Entry flag bits. A normal PvP open => 0; robbing => Robbing; hidden rob => both.
    [Flags]
    private enum TimerFlags
    {
        None = 0,
        Robbing = 1,        // bit0 — set on a robbing open (also the self-defence waiver)
        SuppressStar = 2,   // bit1 — set on a hidden rob; the '*' marker is suppressed
    }

    private sealed class Entry
    {
        public required string Aggressor;   // entry[0] — who struck first
        public required string Victim;       // entry[1] — who was struck
        public DateTime ExpiresUtc;          // approximates the entry[2] duration countdown
        public float Amount;                 // EP charged; nonzero => the victim is in retaliation
        public TimerFlags Flags;             // entry[4]
    }

    private readonly List<Entry> _entries = new();
    private readonly object _lock = new();

    private Func<DateTime>? _nowOverride;

    /// <summary>Test hook: pin "now" so window expiry is deterministic.</summary>
    public void SetClockForTests(Func<DateTime>? now)
    {
        lock (_lock)
            _nowOverride = now;
    }

    private DateTime Now => _nowOverride?.Invoke() ?? DateTime.UtcNow;

    // Drops expired entries. Caller must hold _lock.
    private void PurgeExpiredLocked(DateTime now)
    {
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            if (_entries[i].ExpiresUtc <= now)
                _entries.RemoveAt(i);
        }
    }

    private Entry? FindLocked(string aggressor, string victim)
    {
        foreach (var e in _entries)
        {
            if (string.Equals(e.Aggressor, aggressor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Victim, victim, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    /// <summary>
    /// Is there a live timer where
    /// <paramref name="aggressor"/> struck <paramref name="victim"/>? Returns the stock code:
    /// 0 = none, 1 = plain (bit0 clear), 2 = robbing (bit0 set, bit1 clear), 3 = both bits set.
    /// </summary>
    public int AlreadyEvil(string aggressor, string victim)
    {
        lock (_lock)
        {
            PurgeExpiredLocked(Now);
            var e = FindLocked(aggressor, victim);
            if (e is null)
                return 0;
            if ((e.Flags & TimerFlags.Robbing) == 0)
                return 1;
            return (e.Flags & TimerFlags.SuppressStar) != 0 ? 3 : 2;
        }
    }

    /// <summary>
    /// The should-give-evil verdict used for a
    /// positive (evil-gaining) act against another player. Returns the stock code:
    /// <list type="bullet">
    /// <item>0 — free, and no timer is touched: either the TARGET already holds an edge on the actor
    /// (self-defence), or the actor already holds a PLAIN aggression edge on the target
    /// (a plain existing edge: the opening EP is paid once per window, not per swing).</item>
    /// <item>1 — the actor holds a ROBBING edge. A rob does NOT buy a free
    /// follow-up attack: the caller charges in full, then <see cref="RemoveSingleTimer"/>s before
    /// re-adding, so the replacement edge is a plain one (which un-suppresses the '*' the hidden rob
    /// was hiding).</item>
    /// <item>2 — fresh aggression: charge and open the edge.</item>
    /// </list>
    /// Note the asymmetry this encodes: robbing someone opens the edge that lets THEM hit back for
    /// free, while leaving the robber's own follow-up attack fully chargeable.
    /// </summary>
    public int ShouldGiveEvil(string actor, string target)
    {
        lock (_lock)
        {
            PurgeExpiredLocked(Now);
            if (FindLocked(target, actor) is not null)
                return 0;

            var own = FindLocked(actor, target);
            if (own is null)
                return 2;

            return (own.Flags & TimerFlags.Robbing) == 0 ? 0 : 1;
        }
    }

    /// <summary>
    /// Returns true (show the '*') unless the
    /// <paramref name="viewed"/>→<paramref name="viewer"/> timer has the bit1 suppression flag set.
    /// </summary>
    public bool DisplayEvilStar(string viewed, string viewer)
    {
        lock (_lock)
        {
            PurgeExpiredLocked(Now);
            var e = FindLocked(viewed, viewer);
            // Stock walks for an entry with bit1 set and returns 0 (suppress) only then; otherwise 1.
            return e is null || (e.Flags & TimerFlags.SuppressStar) == 0;
        }
    }

    /// <summary>
    /// True while <paramref name="player"/> is the
    /// aggressor on any live timer with a nonzero charged amount (drives the travel/teleport gate).
    /// </summary>
    public bool IsInRetaliation(string player)
    {
        lock (_lock)
        {
            PurgeExpiredLocked(Now);
            foreach (var e in _entries)
            {
                if (e.Amount != 0 && string.Equals(e.Aggressor, player, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// The open kind: 0 = a normal PvP open (no flags), 1 = a robbing attempt that
    /// was <em>noticed</em> (bit-0 only → the '*' marker shows), 2 = a robbing attempt that stayed
    /// hidden (both bits set → the marker is suppressed). See pvp-parity.md §5.
    /// </summary>
    public enum EvilTimerKind
    {
        Pvp = 0,           // a normal PvP open
        RobNoticed = 1,    // Robbing
        RobHidden = 2,     // Robbing | SuppressStar
    }

    /// <summary>
    /// Open (or refresh) the directed timer.
    /// Stock skips re-creation for a normal PvP open when an entry already exists; we refresh its
    /// window/amount so a renewed aggression keeps the relationship alive (matching the previous
    /// wall-clock behaviour). For a robbing open (<paramref name="kind"/> != Pvp) the stock path
    /// removes the old edge first and re-adds, so the robbing flags overwrite any prior edge.
    /// </summary>
    public void AddEvilTimer(string aggressor, string victim, float chargedEvilPoints, EvilTimerKind kind = EvilTimerKind.Pvp)
    {
        TimerFlags flags = kind switch
        {
            EvilTimerKind.RobNoticed => TimerFlags.Robbing,
            EvilTimerKind.RobHidden => TimerFlags.Robbing | TimerFlags.SuppressStar,
            _ => TimerFlags.None,
        };

        lock (_lock)
        {
            DateTime now = Now;
            PurgeExpiredLocked(now);
            var e = FindLocked(aggressor, victim);
            if (e is null)
            {
                _entries.Add(new Entry
                {
                    Aggressor = aggressor,
                    Victim = victim,
                    ExpiresUtc = now + EvilTimerWindow,
                    Amount = chargedEvilPoints,
                    Flags = flags,
                });
            }
            else
            {
                e.ExpiresUtc = now + EvilTimerWindow;
                if (chargedEvilPoints != 0)
                    e.Amount = chargedEvilPoints;
                // A robbing open overwrites the edge's flags (stock removes then re-adds);
                // a plain PvP refresh leaves whatever flags were already there.
                if (kind != EvilTimerKind.Pvp)
                    e.Flags = flags;
            }
        }
    }

    /// <summary>
    /// Forgiveness: find the FIRST live directed edge where
    /// <paramref name="aggressor"/> struck <paramref name="victim"/>, hand back its charged EP amount
    /// (entry[3]) in <paramref name="refundedEvilPoints"/>, and remove that single edge. Returns true
    /// on a match (the "the gods have forgiven" branch), false when no such edge exists ("the gods
    /// refuse"). The caller subtracts the refund from the aggressor's EvilPoints and recalculates the
    /// aggressor's worn-item gating.
    /// </summary>
    public bool TryForgive(string aggressor, string victim, out float refundedEvilPoints)
    {
        refundedEvilPoints = 0f;
        lock (_lock)
        {
            PurgeExpiredLocked(Now);
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (string.Equals(e.Aggressor, aggressor, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Victim, victim, StringComparison.OrdinalIgnoreCase))
                {
                    refundedEvilPoints = e.Amount;
                    _entries.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Remove a single directed timer.</summary>
    public void RemoveSingleTimer(string aggressor, string victim)
    {
        lock (_lock)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_entries[i].Aggressor, aggressor, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(_entries[i].Victim, victim, StringComparison.OrdinalIgnoreCase))
                    _entries.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Drop every timer involving <paramref name="player"/> in either direction. Not a stock function
    /// (stock leaves timers to count down), but used on death/character-reset where our snapshot
    /// restore would otherwise leave dangling relationships pointing at a stale character.
    /// </summary>
    public void RemoveAllFor(string player)
    {
        lock (_lock)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_entries[i].Aggressor, player, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(_entries[i].Victim, player, StringComparison.OrdinalIgnoreCase))
                    _entries.RemoveAt(i);
            }
        }
    }

    /// <summary>Test/diagnostic helper: live entry count after purging expired edges.</summary>
    public int ActiveCount
    {
        get
        {
            lock (_lock)
            {
                PurgeExpiredLocked(Now);
                return _entries.Count;
            }
        }
    }
}
