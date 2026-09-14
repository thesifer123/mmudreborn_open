namespace mmudreborn.Server;

/// <summary>
/// Command-name resolver. Stock resolves a typed verb to a command through
/// a hand-written trie that encodes each command's
/// <em>minimum unambiguous abbreviation</em>. That is why "exp", "expe", ... "experience"
/// all work without enumerating every spelling.
///
/// This class is the data-table equivalent of that trie: one row per command word with its
/// stock minimum-abbreviation length. <see cref="Resolve"/> normalizes any input to its
/// canonical command word, so the dispatch switch only needs one case per command. Input the
/// table doesn't recognize is returned unchanged, so unknown verbs fall through to the existing
/// dispatch / room-exit fallback exactly as before (nothing breaks).
///
/// Pure stock fidelity, with two deliberate deviations the project owner approved:
///   * accidental aliases that stock never had are removed: k, kill, jk, hp, unlock
///     (stock has no such mechanism — kill→killblow is dead in-game; "unlock" is just
///      use-key / pick / bash a direction).
///   * a small, explicitly-flagged set of house commands (not in stock) is kept — see
///     <see cref="House"/> rows (bug tracker, stat-all, abil, room, wealth, hall, gang tops).
///
/// Directions (n/s/e/w/ne/...) are resolved separately before this runs, so this table
/// intentionally omits them; that keeps the short prefixes (se, sw, ...) reserved for movement,
/// matching the stock trie where those prefixes are directions.
/// </summary>
internal static class CommandRegistry
{
    /// <param name="Word">The full canonical command word a player could type.</param>
    /// <param name="MinLen">Shortest prefix of <see cref="Word"/> that resolves to it (the stock minimum abbreviation).</param>
    /// <param name="Canonical">The token the dispatch switch matches on. Equals <see cref="Word"/> unless this row is a true alias of another command.</param>
    /// <param name="House">True for project-specific commands that have no stock equivalent.</param>
    internal readonly record struct Entry(string Word, int MinLen, string Canonical, bool House = false);

    // A row reads "Word, MinLen[, Canonical]". When Canonical is omitted it equals Word.
    // Multiple rows with the same Canonical are genuine aliases (distinct words → one handler),
    // e.g. who/scan, bg/gb, balance/bankbook.
    private static Entry E(string word, int minLen, string? canonical = null, bool house = false)
        => new(word, minLen, canonical ?? word, house);

    private static readonly Entry[] Entries =
    [
        // ── Information / look ──
        E("look", 1),                 // L (bare) → look
        E("map", 2),
        E("read", 4),                 // full word: "read" a scroll. "ready" (equip) shares the prefix, so neither abbreviates short
        // Inventory is faithful to the stock trie: bare "i" works, and "inve".."inventory" work, but
        // "in"/"inv" do NOT (inv is the shared prefix of inventory/invite/invoke, so the 4th char is
        // required). Two rows express the 1-char-OR-4+ split. (No-arg-only: "i <text>" falls through
        // to say, guarded in the dispatch's `case "inventory"`.)
        E("i", 1, "inventory"),
        E("inventory", 4),            // "inve" is the minimum; in/inv are dead
        E("keys", 2),
        E("exits", 3),
        E("health", 2),               // HE → health  (hp alias removed)
        E("experience", 3),           // EXP → experience
        E("spells", 2),               // SP → spells
        E("powers", 2),               // PO → powers
        E("profile", 2),              // PR → profile
        E("statline", 5),
        E("version", 2),

        // ── Movement-adjacent (room interactions handled via fallback; listed so abbrevs normalize) ──
        E("follow", 3),               // FO → follow
        E("leave", 2),
        E("sneak", 2),
        E("hide", 3),
        E("stash", 4, "hide"),        // STAS→stash (hide path); "sta" is status, not stash
        E("search", 3),               // SEA → search (SE is southeast)
        E("track", 4),
        E("rest", 1),                 // keep R → rest (current behavior)
        E("meditate", 3),             // MED → meditate
        E("drag", 3),
        E("aid", 2),
        E("break", 3),

        // ── Combat ──
        E("attack", 1),               // A (bare) → attack   (k/kill removed)
        E("backstab", 5),             // BACKS… → backstab (BACK is shared with backrank)
        E("bs", 2, "backstab"),       // BS → backstab (short alias)
        E("bash", 3),
        E("aa", 2, "bash"),           // all-out attack alias
        E("smash", 2),
        E("punch", 1),                // the P path → punch (we accept p)
        E("kick", 2),
        E("jumpkick", 2),             // JU → jumpkick   (jk removed)
        E("guard", 2),
        E("rob", 2),
        E("disarm", 3),
        E("picklock", 2),

        // ── Items / inventory actions ──
        E("get", 1),                  // G (bare) → get
        E("take", 3, "get"),          // house convenience: take → get
        E("drop", 3),
        E("give", 2),
        E("eat", 2),
        E("drink", 3),
        E("use", 2),
        E("light", 3),
        E("extinguish", 3),           // house (douse light)
        E("equip", 2),
        E("wear", 3, "equip"),
        E("wield", 3, "equip"),
        E("arm", 2, "equip"),
        E("ready", 5, "equip"),       // full word: "read" is the scroll command, so ready needs all 5 chars
        E("remove", 3),
        E("appraise", 2),
        E("share", 3),
        E("train", 4),

        // ── Doors / locks ──
        E("open", 2),
        E("close", 2),
        E("lock", 3),                 // unlock removed

        // ── Shops / banking ──
        E("list", 3),
        E("buy", 3),
        E("sell", 3),
        E("stock", 3),
        E("unstock", 4),
        E("markup", 3),
        E("deposit", 3),
        E("withdraw", 4),
        E("bankbook", 3),
        E("balance", 3, "bankbook"),

        // ── Communication ──
        E("say", 3),
        E("yell", 4),
        E("gossip", 3),
        E("auction", 3),
        E("broadcast", 2),            // "br" → broadcast (bre=break, bri=brief need the 3rd char)
        E("whisper", 3),
        E("telepath", 3, "whisper"),
        E("greet", 3),
        E("ask", 2),
        E("broadgang", 6),            // "broad" shared with broadcast → needs the 6th char
        E("bg", 2, "broadgang"),
        E("gb", 2, "broadgang"),
        E("ignore", 3),               // house alias kept (forget subsystem not built)

        // ── Party ──
        E("join", 2),
        E("invite", 4),
        E("uninvite", 3),
        E("party", 3),
        E("frontrank", 2),
        E("backrank", 5),
        E("midrank", 3),
        E("promote", 4),
        E("demote", 3),
        E("create", 2),
        E("disband", 4),

        // ── Magic ──
        E("cast", 1),                 // C (bare) → cast
        E("invoke", 4),               // house alias kept
        E("abilities", 4),            // house (rich ability list)

        // ── Social / standing ──
        E("who", 2),
        E("scan", 2, "who"),          // SC → scan; WHO also → scan list
        // Non-stock: "web-who" lists characters present on the web (telepath-eligible). Deliberately NOT
        // "who-web": MegaMud lags on any command STARTING with "who" (it treats it as a WHO cycle), so
        // the verb leads with "web". "webwho" is canonical; "web-who" is the hyphenated alias.
        E("webwho", 4, house: true),
        E("web-who", 5, "webwho", house: true),
        // (topten intentionally omitted: dispatch distinguishes "top" vs "topten" on the raw verb.)
        E("forgive", 4),

        // ── Admin / lifecycle ──
        E("sysop", 3),
        E("purge", 3),                // PUR → purge (PU is punch)
        E("suicide", 4),
        E("reroll", 3),
        E("quit", 2),                 // QU → quit (Q alone is quiet; we keep quit-on-q via switch)
        E("verbose", 4),
        E("brief", 3),
        E("help", 3),

        // ── House-only commands (no stock equivalent) ──
        E("room", 3, "room", house: true),
        E("wealth", 4, "wealth", house: true),
        E("hall", 4, "hall", house: true),
        E("halloffame", 4, "hall", house: true),
        // Full word only (no "ho"/"hom" abbreviation) — house command, keeps short prefixes free.
        E("home", 4, "home", house: true),
        // (topgangs/gangtop removed — the gang leaderboard is the STOCK `top gang`, handled by HandleTop.)
        E("bug", 3, "bug", house: true),
        E("bugs", 4, "bugs", house: true),
        E("listbugs", 5, "bugs", house: true),
        // MinLen 5 ("showb"), NOT 4: the 4-letter "show" is a game verb (present an item to a room
        // object/peephole, resolved via the room text-exit fallback) and MUST keep priority over the
        // bug tool. "showbug"/"showb…" still reach the bug viewer; bare "show <item>" flows to gameplay.
        E("showbug", 5, "showbug", house: true),
        E("completebug", 5, "completebug", house: true),
        // 4 ("nota") is the shortest prefix that can't be confused with a bare direction or a
        // common word; nothing else in the table starts with "n" (north is resolved earlier).
        E("notabug", 4, "notabug", house: true),
        E("verifybug", 4, "verifybug", house: true),
        E("stillbug", 5, "stillbug", house: true),
        E("stillpresentbug", 6, "stillbug", house: true),
        E("deletebug", 4, "deletebug", house: true),
        E("removebug", 7, "deletebug", house: true),
    ];

    /// <summary>
    /// Resolve a typed verb to its canonical command word. Returns the lower-cased input unchanged
    /// when no command matches (so unknown verbs flow on to the existing dispatch / room-exit
    /// fallback). Matching mirrors stock: a row matches when its word starts with the input and
    /// the input is at least the row's minimum-abbreviation length.
    /// </summary>
    public static string Resolve(string word)
    {
        if (string.IsNullOrEmpty(word))
            return word;

        string w = word.ToLowerInvariant();

        string? canonical = null;
        foreach (var e in Entries)
        {
            if (w.Length >= e.MinLen && e.Word.StartsWith(w, System.StringComparison.Ordinal))
            {
                if (canonical != null && canonical != e.Canonical)
                    return w; // ambiguous — let the caller's normal handling decide (guarded by tests)
                canonical = e.Canonical;
            }
        }

        return canonical ?? w;
    }

    /// <summary>
    /// True when <paramref name="word"/> is recognized as a command (full word OR a valid abbreviation),
    /// the same resolver stock uses to forbid command-like character names ("You may not use
    /// that name!"). Matches the same rule as <see cref="Resolve"/>: a row matches when its word starts
    /// with the input and the input meets the row's minimum-abbreviation length. Movement verbs live in a
    /// separate table (CommandParser.IsMovementCommand) and are checked alongside this by the caller.
    /// </summary>
    public static bool MatchesCommand(string word)
    {
        if (string.IsNullOrEmpty(word))
            return false;

        string w = word.ToLowerInvariant();
        foreach (var e in Entries)
        {
            if (w.Length >= e.MinLen && e.Word.StartsWith(w, System.StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>Every distinct canonical command word the resolver can emit (used by tests).</summary>
    public static System.Collections.Generic.IEnumerable<string> CanonicalCommands()
    {
        var seen = new System.Collections.Generic.HashSet<string>();
        foreach (var e in Entries)
            if (seen.Add(e.Canonical))
                yield return e.Canonical;
    }

    /// <summary>All table rows (used by the ambiguity/regression tests).</summary>
    internal static System.Collections.Generic.IReadOnlyList<Entry> AllEntries => Entries;
}
