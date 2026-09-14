using System.Text.RegularExpressions;
using mmudreborn.Data.Models;
using mmudreborn.Server;
using mmudreborn.UnitTests.TestHelpers;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class CharacterCreationEditorLayoutTests
{
    [Fact]
    public void BuildStatScreenLines_does_not_pad_character_editor_title_with_extra_spaces()
    {
        string header = VisibleLines()[1];

        Assert.Contains("Character Editor /", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Character Editor   /", header, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildStatScreenLines_keeps_cost_connector_and_bottom_scroll_aligned()
    {
        string[] lines = VisibleLines();

        Assert.DoesNotContain(lines, line => line.Contains("┤  +20 to base stat", StringComparison.Ordinal));
        Assert.Contains(lines, line => line == $" ⌐┴{new string('─', 33)}.     │");
        Assert.Contains(lines, line => line == $" \\{new string('_', 33)}\\___/");
    }

    [Fact]
    public void BuildStatScreenLines_uses_parenthesized_stat_range_layout()
    {
        string[] lines = VisibleLines();

        Assert.Contains(lines, line => Regex.IsMatch(line, @"Strength\s+\(\s+\d+ to\s+\d+\)", RegexOptions.CultureInvariant));
        Assert.Contains(lines, line => Regex.IsMatch(line, @"Intellect\s+\(\s+\d+ to\s+\d+\)", RegexOptions.CultureInvariant));
        Assert.DoesNotContain(lines, line => Regex.IsMatch(line, @"Strength\s+\d+\s*-\s*\d+", RegexOptions.CultureInvariant));
        Assert.DoesNotContain(lines, line => Regex.IsMatch(line, @"Intellect\s+\d+\s*-\s*\d+", RegexOptions.CultureInvariant));
    }

    [Fact]
    public void BuildStatScreenLines_keeps_value_column_spaced_from_parenthesized_ranges()
    {
        string[] lines = VisibleLines();

        Assert.Contains(lines, line => Regex.IsMatch(line, @"Strength\s+\(\s+\d+ to\s+\d+\)\s{4}\d+ «", RegexOptions.CultureInvariant));
        Assert.Contains(lines, line => Regex.IsMatch(line, @"Health\s+\(\s+\d+ to\s+\d+\)\s{4}\d+ «", RegexOptions.CultureInvariant));
    }

    [Fact]
    public void BuildStatScreenLines_preserves_parenthesized_alignment_for_dwarf_mage_ranges()
    {
        var race = new Race
        {
            Name = "Dwarf",
            MinStr = 50,
            MaxStr = 110,
            MinInt = 30,
            MaxInt = 90,
            MinWil = 50,
            MaxWil = 120,
            MinAgl = 35,
            MaxAgl = 95,
            MinHea = 45,
            MaxHea = 105,
            MinChm = 30,
            MaxChm = 85,
        };
        var cls = new CharacterClass { Name = "Mage" };

        string[] lines = BuildVisibleLines(race, cls, [50, 30, 50, 35, 45, 30]);

        Assert.Contains(lines, line => line.Contains("│ » Strength   (  50 to  110)    50 « │", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("│ » Intellect  (  30 to   90)    30 « │", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("│ » Willpower  (  50 to  120)    50 « │", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("│ » Charm      (  30 to   85)    30 « │", StringComparison.Ordinal));
    }

    private static string[] VisibleLines()
    {
        var race = new Race
        {
            Name = "Human",
            MinStr = 40,
            MaxStr = 100,
            MinInt = 60,
            MaxInt = 140,
            MinWil = 55,
            MaxWil = 125,
            MinAgl = 45,
            MaxAgl = 115,
            MinHea = 50,
            MaxHea = 120,
            MinChm = 35,
            MaxChm = 95,
        };
        var cls = new CharacterClass { Name = "Warrior" };

        return BuildVisibleLines(race, cls, [40, 60, 55, 45, 50, 35]);
    }

    private static string[] BuildVisibleLines(Race race, CharacterClass cls, int[] stats)
    {
        string[] statNames = ["Strength", "Intellect", "Willpower", "Agility", "Health", "Charm"];
        int[] raceMins = [race.MinStr, race.MinInt, race.MinWil, race.MinAgl, race.MinHea, race.MinChm];
        int[] maxs = [race.MaxStr, race.MaxInt, race.MaxWil, race.MaxAgl, race.MaxHea, race.MaxChm];

        return CharacterCreation.BuildStatScreenLines(
            "UnitEditor",
                string.Empty,
                race,
                cls,
                stats,
                raceMins,
                maxs,
                statNames,
                cpLeft: 0,
                hairLength: 0,
                hairColour: 0,
                eyeColour: 0,
                cursorRow: 1,
                exitChoice: 0,
                isCreation: false)
            .Select(AnsiStripper.StripPreserveWhitespace)
            .ToArray();
    }
}