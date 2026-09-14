using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The command resolver is the data-table equivalent of the stock resolver trie.
// These tests are the safety net for the dispatch refactor: they prove every abbreviation
// resolves unambiguously, and that the aliases we intentionally dropped no longer resolve.
public sealed class CommandRegistryTests
{
    // For every table row, EVERY prefix from its minimum-abbreviation length up to the full
    // word must resolve to that row's canonical command. If two rows collide on a prefix the
    // resolver returns the input unchanged (!= canonical), so this single assertion catches
    // both wrong-target and ambiguous-prefix table bugs.
    [Fact]
    public void Every_abbreviation_resolves_to_its_canonical_command()
    {
        foreach (var entry in CommandRegistry.AllEntries)
        {
            for (int len = entry.MinLen; len <= entry.Word.Length; len++)
            {
                string prefix = entry.Word[..len];
                string resolved = CommandRegistry.Resolve(prefix);
                Assert.True(
                    resolved == entry.Canonical,
                    $"'{prefix}' (prefix of '{entry.Word}', minLen {entry.MinLen}) resolved to '{resolved}', expected '{entry.Canonical}'.");
            }
        }
    }

    // A prefix shorter than the minimum must NOT resolve to the command (it falls through
    // unchanged), so a one-letter typo can't accidentally fire a long command.
    [Fact]
    public void Below_minimum_length_does_not_resolve()
    {
        foreach (var entry in CommandRegistry.AllEntries)
        {
            if (entry.MinLen <= 1)
                continue;
            string tooShort = entry.Word[..(entry.MinLen - 1)];

            // A shorter alias of the SAME command may legitimately resolve this prefix
            // (e.g. "still" → stillbug via the shorter "stillbug" row), so only assert when
            // no same-canonical row accepts the prefix.
            bool anotherAliasAccepts = false;
            foreach (var other in CommandRegistry.AllEntries)
            {
                if (other.Canonical == entry.Canonical && tooShort.Length >= other.MinLen &&
                    other.Word.StartsWith(tooShort, System.StringComparison.Ordinal))
                {
                    anotherAliasAccepts = true;
                    break;
                }
            }
            if (anotherAliasAccepts)
                continue;

            Assert.NotEqual(entry.Canonical, CommandRegistry.Resolve(tooShort));
        }
    }

    [Theory]
    // Removed accidental aliases pass through unchanged → dispatch treats them as no-command.
    [InlineData("k")]
    [InlineData("kill")]
    [InlineData("jk")]
    [InlineData("hp")]
    [InlineData("unlock")]
    public void Removed_aliases_do_not_resolve_to_a_command(string input)
    {
        Assert.Equal(input, CommandRegistry.Resolve(input));
    }

    [Theory]
    // Spot-check the abbreviations players actually use, including the whack-a-mole gaps.
    [InlineData("exp", "experience")]
    [InlineData("experien", "experience")]
    [InlineData("ap", "appraise")]
    [InlineData("auc", "auction")]
    [InlineData("sea", "search")]
    [InlineData("med", "meditate")]
    [InlineData("pro", "profile")]
    [InlineData("prom", "promote")]
    [InlineData("c", "cast")]
    [InlineData("cl", "close")]
    [InlineData("cr", "create")]
    [InlineData("a", "attack")]
    [InlineData("bs", "backstab")]
    [InlineData("backs", "backstab")]
    [InlineData("he", "health")]
    [InlineData("ju", "jumpkick")]
    [InlineData("bg", "broadgang")]
    [InlineData("gb", "broadgang")]
    [InlineData("who", "who")]
    [InlineData("sc", "who")]
    [InlineData("bal", "bankbook")]
    // "sta" is NOT stash — it's the stat sheet (handled in dispatch); stash needs the 4th char.
    [InlineData("stas", "hide")]
    [InlineData("stash", "hide")]
    [InlineData("br", "broadcast")]
    [InlineData("bre", "break")]
    [InlineData("bri", "brief")]
    [InlineData("broadc", "broadcast")]
    [InlineData("broadg", "broadgang")]
    // Inventory: stock-faithful — bare "i" and "inve"+ resolve; "in"/"inv" are dead.
    [InlineData("i", "inventory")]
    [InlineData("inve", "inventory")]
    [InlineData("inventory", "inventory")]
    public void Known_abbreviations_resolve(string input, string expected)
    {
        Assert.Equal(expected, CommandRegistry.Resolve(input));
    }

    [Fact]
    public void Show_stays_a_game_verb_and_does_not_hit_the_bug_tool()
    {
        // Bug fix: "show <item>" presents an item to a room object (peephole etc.) and resolves via
        // the room-exit fallback — it must ALWAYS win over the showbug viewer. The bug tool needs the
        // distinguishing "showb"+; bare "show" passes through unchanged to gameplay.
        Assert.Equal("show", CommandRegistry.Resolve("show"));
        Assert.Equal("showbug", CommandRegistry.Resolve("showb"));
        Assert.Equal("showbug", CommandRegistry.Resolve("showbug"));
    }

    [Theory]
    // The dead middle of the inventory prefix: these must NOT resolve to a command.
    [InlineData("in")]
    [InlineData("inv")]
    public void Inventory_dead_prefixes_do_not_resolve(string input)
    {
        Assert.Equal(input, CommandRegistry.Resolve(input));
    }
}
