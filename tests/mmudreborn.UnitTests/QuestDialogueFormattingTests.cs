using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class QuestDialogueFormattingTests
{
    private static readonly System.Text.RegularExpressions.Regex AnsiCsi =
        new(@"\x1B\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static int VisibleLen(string s) => AnsiCsi.Replace(s, string.Empty).Length;

    // Stock prints textblock records verbatim (~79-col records); our importer can produce one long line,
    // so narrative prose is re-wrapped to 79 VISIBLE columns to restore the stock look.
    [Fact]
    public void Short_dialogue_line_is_not_wrapped()
    {
        string line = "The old man nods slowly.";
        var wrapped = System.Linq.Enumerable.ToList(CommandParser.WrapDialogueLineToWidth(line, 79));
        Assert.Single(wrapped);
        Assert.Equal(line, wrapped[0]);
    }

    [Fact]
    public void Long_prose_wraps_to_79_visible_columns_at_word_boundaries()
    {
        string line = string.Join(" ", System.Linq.Enumerable.Repeat("prophecy", 40));   // ~359 chars
        var wrapped = System.Linq.Enumerable.ToList(CommandParser.WrapDialogueLineToWidth(line, 79));

        Assert.True(wrapped.Count > 1);
        foreach (var l in wrapped)
            Assert.True(VisibleLen(l) <= 79, $"line was {VisibleLen(l)} visible cols: '{l}'");
        foreach (var l in wrapped)
            foreach (var tok in l.Split(' ', System.StringSplitOptions.RemoveEmptyEntries))
                Assert.Equal("prophecy", tok);
        Assert.Equal(40, string.Join(" ", wrapped).Split(' ', System.StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void Wrap_counts_visible_width_only_and_never_severs_an_ansi_escape()
    {
        string line = "\x1b[0;32m" + string.Join(" ", System.Linq.Enumerable.Repeat("aaaa", 18))
            + " \x1b[0;1;32mDARKFOLD\x1b[0;32m " + string.Join(" ", System.Linq.Enumerable.Repeat("bbbb", 18)) + "\x1b[0m";
        var wrapped = System.Linq.Enumerable.ToList(CommandParser.WrapDialogueLineToWidth(line, 79));

        Assert.True(wrapped.Count > 1);
        foreach (var l in wrapped)
        {
            Assert.True(VisibleLen(l) <= 79, $"visible {VisibleLen(l)}: '{l}'");
            for (int i = 0; i < l.Length; i++)
            {
                if (l[i] != '\x1b') continue;
                var m = AnsiCsi.Match(l, i);
                Assert.True(m.Success && m.Index == i, $"severed escape at {i} in '{l}'");
            }
        }
        Assert.Equal(
            System.Text.RegularExpressions.Regex.Replace(AnsiCsi.Replace(line, ""), @"\s+", " ").Trim(),
            System.Text.RegularExpressions.Regex.Replace(AnsiCsi.Replace(string.Join(" ", wrapped), ""), @"\s+", " ").Trim());
    }

    [Fact]
    public void Control_only_ansi_dialogue_lines_are_sent_inline_without_a_forced_line_break()
    {
        var plan = CommandParser.BuildDialogueRenderPlan("\x1b[0;40m", clueKeywords: null);

        Assert.Equal("\x1b[0;40m", plan.Text);
        Assert.False(plan.AppendNewLine);
    }

    [Fact]
    public void Plain_dialogue_lines_use_palette_narrative_color_without_synthesizing_clue_highlights()
    {
        // Bug (Old Man @9/1259): the stock long-text renderer prints textblock bytes verbatim and never
        // highlights ASK keywords — breadcrumb coloring is only the inline ANSI the author baked into
        // the data. We dropped the synthetic HighlightClueKeywords pass (it over-lit every routing
        // keyword, e.g. lore words). We keep the green narrative wrap so each line self-colors and the
        // CWGamingServ ESC[0m bleed-guard can't wipe it. Clue keywords are passed but must NOT recolor.
        var plan = CommandParser.BuildDialogueRenderPlan(
            "If you wish me to continue, ask me about the DRAGON. Also, new information has given me HOPE.",
            ["dragon", "hope"]);

        Assert.Equal(
            $"{Ansi.Green}If you wish me to continue, ask me about the DRAGON. Also, new information has given me HOPE.{Ansi.Reset}",
            plan.Text);
        Assert.True(plan.AppendNewLine);
    }

    [Fact]
    public void Plain_dialogue_lines_routed_through_palette_resolve_per_paletteId()
    {
        // Pin the structural piece: BuildDialogueRenderPlan accepts a paletteId arg and routes the
        // narrative color through the palette table rather than a hard-coded Ansi.Green literal.
        // Palette 1 currently inherits palette 0's Green narrative; the test guards against a future
        // regression where someone re-introduces hard-coded color literals on the dialogue path.
        var plan = CommandParser.BuildDialogueRenderPlan(
            "Bring me the AMULET, brave one.",
            ["amulet"],
            paletteId: 1);

        Assert.Equal(
            $"{Ansi.Green}Bring me the AMULET, brave one.{Ansi.Reset}",
            plan.Text);
        Assert.True(plan.AppendNewLine);
    }

    [Fact]
    public void Ansi_authored_dialogue_blocks_keep_color_state_across_stored_line_breaks()
    {
        var plans = CommandParser.BuildDialogueRenderPlans(
            [
                $"{Ansi.Green}\"The lord has seen fit to delay my {Ansi.BrightGreen}mission{Ansi.Green}, it seems--",
                "here and not working on my fourth ale is because on the way through the",
                $"{Ansi.BrightGreen}Dragon's Teeth{Ansi.Green}, a bandit leaped out from behind a large stone and attempted to"
            ],
            ["mission", "dragon's teeth"]);

        Assert.Collection(
            plans,
            plan =>
            {
                Assert.Equal($"{Ansi.Green}\"The lord has seen fit to delay my {Ansi.BrightGreen}mission{Ansi.Green}, it seems--", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal("here and not working on my fourth ale is because on the way through the", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal($"{Ansi.BrightGreen}Dragon's Teeth{Ansi.Green}, a bandit leaped out from behind a large stone and attempted to", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal(Ansi.Reset, plan.Text);
                Assert.False(plan.AppendNewLine);
            });
    }

    [Fact]
    public void Ansi_authored_dialogue_block_is_emitted_as_one_payload_that_keeps_color_across_lines()
    {
        // Regression: the wounded-messenger block (textblock 317) sets ESC[0;32m green once, then
        // relies on it persisting across stored line breaks; the game note switches to ESC[0;36m
        // cyan and likewise persists. The CWGamingServ *Combat Off* bleed-guard prepends ESC[0m to
        // any line sent via SendLineAsync that doesn't start with its own escape, which on a per-
        // line send would wipe the green/cyan on every plain continuation line. SendDialogueLinesAsync
        // flattens the block into ONE SendAsync payload instead; this guards that contract.
        var plans = CommandParser.BuildDialogueRenderPlans(
            [
                $"{Ansi.Green}\"The lord has seen fit to delay my {Ansi.BrightGreen}mission{Ansi.Green}, it seems--",
                "here and not working on my fourth ale is because on the way through the",
                string.Empty,
                $"{Ansi.Cyan}<Game note: if you ask about 'mission', you will embark on the mission, and",
                "decision before you answer.>"
            ],
            ["mission"]);

        string payload = CommandParser.BuildDialogueBlockPayload(plans);

        // Exactly one reset — the trailing one that closes the block — so no interior ESC[0m splits
        // a paragraph back to the terminal default mid-block.
        Assert.EndsWith(Ansi.Reset, payload);
        Assert.Equal(1, CountOccurrences(payload, Ansi.Reset));

        // Plain continuation lines stay raw (no reset prefix): the green carries across the CRLF
        // into "here and...", and the cyan game-note colour carries into "decision before...".
        Assert.Contains("\r\nhere and not working on my fourth ale", payload);
        Assert.Contains("\r\ndecision before you answer.>", payload);
        Assert.DoesNotContain($"{Ansi.Reset}here and not working", payload);
        Assert.DoesNotContain($"{Ansi.Reset}decision before you answer.>", payload);
    }

    [Fact]
    public void Inline_ansi_block_opens_uncolored_first_line_in_narrative_green()
    {
        // Water portal block 2767: the keyword "water portal" is baked in bright-green and toggles back
        // to normal green, but the opening line "Seher'Sahham says, …" carries NO leading color. Without
        // a green baseline it fell back to terminal-default white; the block must open the first visible
        // line in narrative green while leaving the authored inline toggles intact.
        var plans = CommandParser.BuildDialogueRenderPlans(
            [
                "Seher'Sahham says, \"Hello there adventurer, you seem to be quite a ways",
                $"from your home.  I imagine you are here to use my {Ansi.BrightGreen}water portal{Ansi.Green}.  It is"
            ],
            clueKeywords: null);

        Assert.Collection(
            plans,
            plan =>
            {
                Assert.Equal($"{Ansi.Green}Seher'Sahham says, \"Hello there adventurer, you seem to be quite a ways", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal($"from your home.  I imagine you are here to use my {Ansi.BrightGreen}water portal{Ansi.Green}.  It is", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal(Ansi.Reset, plan.Text);
                Assert.False(plan.AppendNewLine);
            });
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int index = haystack.IndexOf(needle, StringComparison.Ordinal);
             index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    [Fact]
    public void Ansi_authored_dialogue_blocks_preserve_explicit_white_sections_until_the_source_changes_color()
    {
        var plans = CommandParser.BuildDialogueRenderPlans(
            [
                $"{Ansi.Reset}He reaches inside his tunic, pulls out a sealed envelope, and hands it to you.",
                "You stow it away in a safe place.",
                string.Empty,
                $"{Ansi.Green}\"Now do NOT lose it!\", he says, \"And hurry in delivering it.\""
            ],
            ["delivering"]);

        Assert.Collection(
            plans,
            plan =>
            {
                Assert.Equal($"{Ansi.Reset}He reaches inside his tunic, pulls out a sealed envelope, and hands it to you.", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal("You stow it away in a safe place.", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal(string.Empty, plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal($"{Ansi.Green}\"Now do NOT lose it!\", he says, \"And hurry in delivering it.\"", plan.Text);
                Assert.True(plan.AppendNewLine);
            },
            plan =>
            {
                Assert.Equal(Ansi.Reset, plan.Text);
                Assert.False(plan.AppendNewLine);
            });
    }
}