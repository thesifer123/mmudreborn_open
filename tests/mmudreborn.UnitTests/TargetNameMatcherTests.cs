using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class TargetNameMatcherTests
{
    [Theory]
    [InlineData("s")]
    [InlineData("sm")]
    [InlineData("sma")]
    [InlineData("small")]
    [InlineData("small g")]
    [InlineData("small giant rat")]
    [InlineData("g")]
    [InlineData("gi")]
    [InlineData("giant")]
    [InlineData("giant r")]
    [InlineData("r")]
    [InlineData("ra")]
    [InlineData("rat")]
    public void MatchesWordPrefix_accepts_full_name_prefixes_from_word_starts(string target)
    {
        Assert.True(TargetNameMatcher.MatchesWordPrefix("small giant rat", target));
    }

    [Theory]
    [InlineData("m")]
    [InlineData("ma")]
    [InlineData("all")]
    [InlineData("iant")]
    [InlineData("at")]
    public void MatchesWordPrefix_rejects_interior_substrings(string target)
    {
        Assert.False(TargetNameMatcher.MatchesWordPrefix("small giant rat", target));
    }

    // GetMatchRank pins the four-tier ladder used by NarrowToBestMatches.
    [Theory]
    [InlineData("shovel", "shovel", TargetNameMatcher.MatchRank.Exact)]
    [InlineData("SHOVEL", "shovel", TargetNameMatcher.MatchRank.Exact)]
    [InlineData("shovel", "SHOVEL", TargetNameMatcher.MatchRank.Exact)]
    [InlineData("shovel of doom", "shov", TargetNameMatcher.MatchRank.PrefixFromStart)]
    [InlineData("shovel of doom", "shovel", TargetNameMatcher.MatchRank.PrefixFromStart)]
    [InlineData("black runed shovel", "shovel", TargetNameMatcher.MatchRank.WordPrefix)]
    [InlineData("black runed shovel", "runed", TargetNameMatcher.MatchRank.WordPrefix)]
    [InlineData("shovel", "axe", TargetNameMatcher.MatchRank.None)]
    [InlineData("small giant rat", "iant", TargetNameMatcher.MatchRank.None)]
    public void GetMatchRank_returns_expected_tier(string candidate, string target, TargetNameMatcher.MatchRank expected)
    {
        Assert.Equal(expected, TargetNameMatcher.GetMatchRank(candidate, target));
    }

    // The motivating case: a sysop typing "sys giveitem shovel" with both items in the world
    // used to get "be more specific" with no way to actually be more specific — typing fewer
    // chars than the full canonical name is impossible. NarrowToBestMatches keeps only the
    // Exact-rank match.
    [Fact]
    public void NarrowToBestMatches_picks_exact_match_over_word_prefix()
    {
        var names = new[] { "shovel", "black runed shovel" };
        var narrowed = TargetNameMatcher.NarrowToBestMatches(names, n => n, "shovel");
        Assert.Equal(new[] { "shovel" }, narrowed);
    }

    [Fact]
    public void NarrowToBestMatches_picks_prefix_from_start_over_word_prefix()
    {
        // "rune" matches "rune dagger" from start (PrefixFromStart) and "ancient rune" via the
        // word-boundary path (WordPrefix). The stronger match wins.
        var names = new[] { "rune dagger", "ancient rune" };
        var narrowed = TargetNameMatcher.NarrowToBestMatches(names, n => n, "rune");
        Assert.Equal(new[] { "rune dagger" }, narrowed);
    }

    [Fact]
    public void NarrowToBestMatches_keeps_all_when_tied_at_the_same_rank()
    {
        // Two items both start with "rune" — the input is genuinely ambiguous and the caller
        // should show the disambiguation prompt.
        var names = new[] { "rune dagger", "runed boots" };
        var narrowed = TargetNameMatcher.NarrowToBestMatches(names, n => n, "rune");
        Assert.Equal(new[] { "rune dagger", "runed boots" }, narrowed);
    }

    [Fact]
    public void NarrowToBestMatches_keeps_multiple_exact_matches_as_genuine_ambiguity()
    {
        // Stock data can carry duplicate item-name rows (one quest variant, one shop variant).
        // When two candidates both Exact-match, ambiguity stands — the caller still prompts.
        var names = new[] { "shovel", "shovel" };
        var narrowed = TargetNameMatcher.NarrowToBestMatches(names, n => n, "shovel");
        Assert.Equal(new[] { "shovel", "shovel" }, narrowed);
    }

    [Fact]
    public void NarrowToBestMatches_returns_empty_when_nothing_matches()
    {
        var names = new[] { "axe", "sword" };
        var narrowed = TargetNameMatcher.NarrowToBestMatches(names, n => n, "shovel");
        Assert.Empty(narrowed);
    }
}
