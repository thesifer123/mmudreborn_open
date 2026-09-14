using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The prose-vs-command gate for the special-command verb engine
// (CommandParser.IsRecognizedScriptOperation). A recognized verb is only a command when its arguments
// fit the verb's signature; otherwise the matched first word is just prose and the line must stay
// narrative (not be parsed/swallowed as a command).
public sealed class ScriptCommandParsingTests
{
    [Theory]
    // ── Real commands (verb + well-formed args) ──
    [InlineData("price 1000 1476", true)]
    [InlineData("giveitem 5", true)]
    [InlineData("takeitem 1947", true)]
    [InlineData("cast 702", true)]
    [InlineData("summon 5", true)]
    [InlineData("random 2641", true)]
    [InlineData("teleport 2431 16", true)]
    [InlineData("givecoins 100 G", true)]      // amount + single denomination letter
    [InlineData("givecoins 500", true)]        // amount only (defaults to copper)
    [InlineData("testskill strength 60 4235", true)]
    [InlineData("checkskill thievery 100 4251", true)]
    [InlineData("delay 5", true)]              // boulder-puzzle round delay ("push boulder:delay 5:…")
    [InlineData("adddelay 5", true)]
    [InlineData("monsters", true)]             // the nomonsters alias (healer / cleanup gates)
    [InlineData("monsters 289", true)]         // optional fail-reference form
    [InlineData("goodability -51 3154", true)] // stock typo for goodaligned (pledge-good turn-in)
    [InlineData("evilaligned -50 839", true)]  // negative threshold is a valid numeric arg
    [InlineData("goodaligned -51", true)]
    [InlineData("maxlevel 49", true)]
    [InlineData("check class", true)]          // argless verbs
    [InlineData("levelcheck", true)]
    // ── Prose that happens to begin with a verb word — must stay narrative ──
    [InlineData("price is extremely reasonable.", false)]
    [InlineData("price o' one hundred gold crowns", false)]
    [InlineData("price 50 gold crowns.", false)]            // numeric first arg but trailing prose
    [InlineData("Cast your gaze upon the altar.", false)]
    [InlineData("Summon the guards!", false)]
    [InlineData("Random monsters roam these halls.", false)]
    [InlineData("testskill is hard work", false)]
    [InlineData("givecoins to the beggar", false)]
    // ── Not verbs at all ──
    [InlineData("The dragon sleeps soundly.", false)]
    [InlineData("", false)]
    public void Recognizes_commands_but_not_prose(string segment, bool expected)
    {
        Assert.Equal(expected, CommandParser.IsRecognizedScriptOperation(segment));
    }

    [Fact]
    public void Leading_colour_code_on_a_trigger_is_peeled_so_the_line_stays_a_command()
    {
        // Text block 9033 verbatim from wcctext2.dat — the Silvermere guildmaster's orc-warlord bounty
        // (room 1/546 CMD). The colour code sits on the TRIGGER, which is formatting, not something the
        // player types. The trigger must come back clean or "give head of orc warlord to guildmaster"
        // never reaches takeitem/givecoins/addexp.
        const string line = "\x1b[0;33mgive head of orc warlord to guildmaster:takeitem 1335 1912:message 1914:message 1913:givecoins 500 G:addexp 1000:message 2636";

        Assert.True(CommandParser.TryParseScriptLine(line, out string? trigger, out var operations));
        Assert.Equal("give head of orc warlord to guildmaster", trigger);
        Assert.Equal(
            new[] { "takeitem 1335 1912", "message 1914", "message 1913", "givecoins 500 G", "addexp 1000", "message 2636" },
            operations);
    }

    [Fact]
    public void Uncoloured_sibling_trigger_still_parses_unchanged()
    {
        // Line 2 of the same block — no colour code, and it worked before the peel. Guards the peel
        // against changing what already parsed.
        const string line = "give orc-head to guildmaster:takeitem 1101 1912:message 1914:message 2634:givecoins 10 G:addexp 50:message 2635";

        Assert.True(CommandParser.TryParseScriptLine(line, out string? trigger, out var operations));
        Assert.Equal("give orc-head to guildmaster", trigger);
        Assert.Equal(6, operations.Count);
    }

    [Theory]
    // A colour code PAST the trigger still marks the line as coloured prose, never a command — the peel
    // is deliberately limited to a leading run.
    [InlineData("\x1b[0;32mThe %s says, \"Simply \x1b[1mbuy minor healing\x1b[0;32m, and I shall heal your wounds.\"")]
    [InlineData("give head of orc warlord to guildmaster:\x1b[0;33mtakeitem 1335 1912")]
    // A leading colour code on plain prose is still prose: peeling it changes nothing, because the
    // segments after it are not recognized verbs.
    [InlineData("\x1b[0mThe barkeep leans over, and in a hushed, conspirational tone says:")]
    [InlineData("\x1b[0;33mThe guildmaster grins: he has no work for you today.")]
    public void Coloured_prose_stays_narrative(string line)
    {
        Assert.False(CommandParser.TryParseScriptLine(line, out _, out _));
    }
}
