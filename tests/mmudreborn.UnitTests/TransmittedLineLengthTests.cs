using System;
using System.Linq;
using System.Text.RegularExpressions;
using CWGamingServ;
using Xunit;

namespace mmudreborn.UnitTests;

// Transport safety net: TelnetClient.EnforceMaxLineLength splits any CRLF-delimited line longer than the
// cap so it can't overrun a client's parse buffer and hard-freeze it (the evil-quest "ask old man
// prophecy" textblock #9569 has a ~1530-char prose line that froze a 4-player party; the good branch's
// 682-char line delivered fine). The split must keep ANSI escapes intact and never touch short lines.
public sealed class TransmittedLineLengthTests
{
    private const int Cap = 80;   // small cap for clarity; production uses 510

    private static readonly Regex AnsiCsi = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static int MaxLineLength(string s) =>
        s.Split('\n').Select(line => line.TrimEnd('\r').Length).DefaultIfEmpty(0).Max();

    private static string StripAnsi(string s) => AnsiCsi.Replace(s, string.Empty);

    [Fact]
    public void Short_text_is_returned_unchanged()
    {
        string text = "You hit the kobold for 12 damage!\r\n";
        Assert.Same(text, TelnetClient.EnforceMaxLineLength(text, Cap));
    }

    [Fact]
    public void Existing_short_lines_are_untouched()
    {
        string text = "Line one is short.\r\nLine two is also short.\r\n";
        Assert.Equal(text, TelnetClient.EnforceMaxLineLength(text, Cap));
    }

    [Fact]
    public void A_long_line_is_split_so_no_line_exceeds_the_cap()
    {
        string word = "blah ";
        string longLine = string.Concat(Enumerable.Repeat(word, 100)).TrimEnd() + "\r\n";  // ~499 chars
        Assert.True(longLine.IndexOf('\n') > Cap);

        string wrapped = TelnetClient.EnforceMaxLineLength(longLine, Cap);

        Assert.True(MaxLineLength(wrapped) <= Cap, $"longest line was {MaxLineLength(wrapped)}");
        // No characters lost or added (other than the inserted CRLFs replacing spaces).
        Assert.Equal(longLine.Replace(" ", "").Replace("\r\n", ""), wrapped.Replace(" ", "").Replace("\r\n", ""));
    }

    [Fact]
    public void Splitting_prefers_word_boundaries()
    {
        string longLine = string.Concat(Enumerable.Repeat("word ", 60)).TrimEnd();
        string wrapped = TelnetClient.EnforceMaxLineLength(longLine, Cap);

        // Every produced line is a run of whole "word" tokens — no token was cut in half.
        foreach (var line in wrapped.Split("\r\n"))
            Assert.DoesNotContain("wor\n", line + "\n");
        Assert.DoesNotContain("wordword", StripAnsi(wrapped).Replace("\r\n", " "));
    }

    [Fact]
    public void Ansi_escape_sequences_are_not_broken_across_a_split()
    {
        // A long colored paragraph: green opener, a highlighted word, back to green, long tail.
        string text = "\x1b[0;32m" + string.Concat(Enumerable.Repeat("alpha ", 20))
            + "\x1b[0;1;32mHIGHLIGHT\x1b[0;32m " + string.Concat(Enumerable.Repeat("omega ", 20)) + "\x1b[0m";

        string wrapped = TelnetClient.EnforceMaxLineLength(text, Cap);

        Assert.True(MaxLineLength(wrapped) <= Cap);
        // Every ESC[ in the output is a complete, well-formed CSI sequence (no break landed inside one).
        for (int i = 0; i < wrapped.Length; i++)
        {
            if (wrapped[i] != '\x1b') continue;
            var m = AnsiCsi.Match(wrapped, i);
            Assert.True(m.Success && m.Index == i, $"malformed/severed escape at {i}");
        }
        // The visible text is preserved exactly (ignoring whitespace the wrap rebalances).
        Assert.Equal(
            Regex.Replace(StripAnsi(text), @"\s+", " ").Trim(),
            Regex.Replace(StripAnsi(wrapped), @"\s+", " ").Trim());
    }

    [Fact]
    public void An_unbroken_run_with_no_spaces_is_hard_split()
    {
        string giant = new string('x', Cap * 3) + "\r\n";
        string wrapped = TelnetClient.EnforceMaxLineLength(giant, Cap);
        Assert.True(MaxLineLength(wrapped) <= Cap);
        Assert.Equal(Cap * 3, StripAnsi(wrapped).Replace("\r\n", "").Length);   // no chars lost
    }
}
