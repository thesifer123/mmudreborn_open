using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The `abilities` panel right-aligns every value to one shared edge. The old "{0,-35}{1,8}" format
// pushed the value a column past that edge whenever a label exceeded 35 chars (e.g. ability 5,
// "Alter Hot Attack Damage (Defence)(5)" = 36) — the reported misalignment of the trailing "10".
public sealed class AbilityRowAlignmentTests
{
    private const int RightEdge = 43;

    [Theory]
    [InlineData("Magical(28)", "5")]
    [InlineData("Auto Use Abilities(114)", "100")]
    [InlineData("Alter Hot Attack Damage (Defence)(5)", "10")]   // 36-char label — the bug case
    [InlineData("Alter healing rate(123)", "-100")]
    public void Values_share_one_right_edge(string label, string value)
    {
        string row = CommandParser.FormatAbilityRow(label, value);

        Assert.EndsWith(value, row);
        Assert.Equal(RightEdge, row.Length);                 // value's right edge is constant
        Assert.True(row.Length - label.Length - value.Length >= 1, "at least one space gap");
    }

    [Fact]
    public void An_over_long_label_keeps_a_single_space_gap()
    {
        // Longer than the right edge: can't fit, so it degrades to exactly one gap space (no crash, no
        // negative padding) and the value overflows the edge.
        string label = new string('x', 50);
        string row = CommandParser.FormatAbilityRow(label, "7");

        Assert.Equal($"{label} 7", row);
    }
}
