using mmudreborn.Server;
using mmudreborn.UnitTests.TestHelpers;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class RoomOutputFormatterTests
{
    [Fact]
    public void WrapEntryList_keeps_commas_at_line_breaks_without_indent()
    {
        var lines = RoomOutputFormatter.WrapEntryList(
            $"{Ansi.Magenta}Also here: {Ansi.Reset}",
            [
                $"{Ansi.BrightMagenta}Watcher{Ansi.Reset}",
                $"{Ansi.White}gaunt one temple master{Ansi.Reset}",
                $"{Ansi.Cyan}ancient dragon{Ansi.Reset}"
            ],
            $"{Ansi.Reset}{Ansi.Magenta}, {Ansi.Reset}",
            width: 35);

        string[] visibleLines = lines.Select(AnsiStripper.Strip).ToArray();

        Assert.Equal("Also here: Watcher,", visibleLines[0]);
        Assert.Equal("gaunt one temple master,", visibleLines[1]);
        Assert.Equal("ancient dragon", visibleLines[2]);
        Assert.False(visibleLines[1].StartsWith(' '));
        Assert.False(visibleLines[2].StartsWith(' '));
    }

    [Fact]
    public void WrapEntryList_keeps_single_line_output_when_width_allows()
    {
        var lines = RoomOutputFormatter.WrapEntryList(
            "Also here: ",
            ["Watcher", "gaunt one elder"],
            ", ",
            width: 79);

        Assert.Single(lines);
        Assert.Equal("Also here: Watcher, gaunt one elder", lines[0]);
    }

    [Fact]
    public void WrapEntryList_applies_suffix_to_final_line_when_requested()
    {
        var lines = RoomOutputFormatter.WrapEntryList(
            "Also here: ",
            ["Watcher", "gaunt one elder"],
            ", ",
            width: 79);

        Assert.Single(lines);
        Assert.Equal("Also here: Watcher, gaunt one elder", lines[0]);

        var rendered = string.Concat(lines[0], ".");
        Assert.Equal("Also here: Watcher, gaunt one elder.", rendered);
    }
}
