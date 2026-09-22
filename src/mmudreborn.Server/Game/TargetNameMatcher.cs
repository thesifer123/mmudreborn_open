namespace mmudreborn.Game;

public static class TargetNameMatcher
{
    /// <summary>
    /// Quality of a target-string match against a candidate name, ordered worst-to-best so the
    /// numeric value can be compared directly. Used by <see cref="NarrowToBestMatches"/> to break
    /// ties: if the user typed "shovel" and the room has both "shovel" (Exact) and "black runed
    /// shovel" (WordPrefix), the Exact match wins because the user literally cannot be more
    /// specific than the full name of one of the items.
    /// </summary>
    public enum MatchRank
    {
        None = 0,
        WordPrefix = 1,        // matches the start of a non-leading word ("shovel" in "black runed shovel")
        PrefixFromStart = 2,   // matches from the candidate's first character ("shov" in "shovel")
        Exact = 3,             // case-insensitive full-name equality ("shovel" == "shovel")
    }

    // Stock word match: the typed text is a case-insensitive prefix of the name, or of any later word in it.
    // Words are split on whitespace only (stock steps word to word), so "t" matches
    // "topaz stone" but never "hematite stone", and "axe" does not match "hand-axe".
    public static bool MatchesWordPrefix(string candidate, string target)
        => GetMatchRank(candidate, target) != MatchRank.None;

    public static MatchRank GetMatchRank(string candidate, string target)
    {
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(target))
            return MatchRank.None;

        string normalizedCandidate = candidate.Trim();
        string normalizedTarget = target.Trim();
        if (normalizedTarget.Length == 0)
            return MatchRank.None;

        if (normalizedCandidate.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            return MatchRank.Exact;

        if (normalizedCandidate.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            return MatchRank.PrefixFromStart;

        for (int index = 1; index < normalizedCandidate.Length; index++)
        {
            if (!IsWordBoundary(normalizedCandidate[index - 1], normalizedCandidate[index]))
                continue;

            if (normalizedCandidate[index..].StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                return MatchRank.WordPrefix;
        }

        return MatchRank.None;
    }

    /// <summary>
    /// When the player types a target string that matches multiple candidates, prefer the
    /// stronger match: Exact > PrefixFromStart > WordPrefix. Without this, typing "shovel" with
    /// both "shovel" and "black runed shovel" in the room produces an unresolvable "be more
    /// specific" loop — the user literally cannot type fewer characters than the full canonical
    /// name. Filters the input list down to the highest-rank tier and returns whatever's left;
    /// the caller's ambiguity check then runs against that narrowed list. Returns an empty list
    /// when no candidate matches at all.
    /// </summary>
    public static List<T> NarrowToBestMatches<T>(IEnumerable<T> candidates, System.Func<T, string> nameOf, string target)
    {
        var ranked = new List<(T Item, MatchRank Rank)>();
        MatchRank best = MatchRank.None;

        foreach (var candidate in candidates)
        {
            var rank = GetMatchRank(nameOf(candidate), target);
            if (rank == MatchRank.None)
                continue;

            ranked.Add((candidate, rank));
            if (rank > best)
                best = rank;
        }

        if (best == MatchRank.None)
            return new List<T>();

        var narrowed = new List<T>(ranked.Count);
        foreach (var (item, rank) in ranked)
        {
            if (rank == best)
                narrowed.Add(item);
        }
        return narrowed;
    }

    /// <summary>
    /// Stock's own two-tier rule for item lookups (carried items, room items and shops):
    /// an exact full-name match wins outright; otherwise EVERY
    /// word-prefix match is equal — there is no "starts with beats later word" preference —
    /// so two different loose matches stay ambiguous. Returns the exact matches when any exist, else all
    /// loose matches, in the caller's scan order.
    /// </summary>
    public static List<T> NarrowToExactOrAllMatches<T>(IEnumerable<T> candidates, System.Func<T, string> nameOf, string target)
    {
        var loose = new List<T>();
        var exact = new List<T>();
        foreach (var candidate in candidates)
        {
            var rank = GetMatchRank(nameOf(candidate), target);
            if (rank == MatchRank.None)
                continue;

            loose.Add(candidate);
            if (rank == MatchRank.Exact)
                exact.Add(candidate);
        }

        return exact.Count > 0 ? exact : loose;
    }

    private static bool IsWordBoundary(char previous, char current)
    {
        return char.IsWhiteSpace(previous) && !char.IsWhiteSpace(current);
    }
}