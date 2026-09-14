using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// SYSOP GOTO <name> is a non-stock QOL helper that finds a room by text. The old resolver matched the
// query as a raw substring in Name OR Description and took the first room by map/room order, which sent
// "bank of godfrey" one room out onto the street, "khaz" into Silvermere (a room whose description names
// the road to Khazarad), and "arly" into a room whose description said "nearly". CommandParser.
// ScoreRoomTextMatch ranks candidates so the actual place wins; these pin the reported cases.
public sealed class GotoRoomMatchTests
{
    [Fact]
    public void Exact_room_name_scores_highest()
        => Assert.Equal(100, CommandParser.ScoreRoomTextMatch("Bank of Godfrey", "some description", "bank of godfrey"));

    [Fact]
    public void Name_prefix_beats_name_substring()
    {
        // "khaz" is a prefix of "Khazarad, Entry Arch" but only a mid-word substring of "Ascent to Khazarad".
        Assert.Equal(90, CommandParser.ScoreRoomTextMatch("Khazarad, Entry Arch", "d", "khaz"));
        Assert.Equal(70, CommandParser.ScoreRoomTextMatch("Ascent to Khazarad", "d", "khaz"));
    }

    [Fact]
    public void Name_whole_word_beats_mid_word_substring()
        => Assert.Equal(80, CommandParser.ScoreRoomTextMatch("Gates of Khazarad", "d", "khazarad"));

    [Fact]
    public void Description_whole_word_is_a_weak_match()
        => Assert.Equal(40, CommandParser.ScoreRoomTextMatch(
            "Sovereign Street, Northern End", "You can see the Bank of Godfrey to the east.", "bank of godfrey"));

    // --- The three reported tickets ---

    [Fact]
    public void Ticket5_khaz_prefers_a_khazarad_room_over_a_silvermere_description_mention()
    {
        // Silvermere room only mentions "Khazarad" in its description — "khaz" is NOT a whole word there,
        // so it must not match at all; the actual Khazarad room wins by name.
        int silvermere = CommandParser.ScoreRoomTextMatch(
            "Main Road, Silvermere Gates", "The road north leads to Khazarad.", "khaz");
        int khazarad = CommandParser.ScoreRoomTextMatch("Khazarad, Entry Arch", "an arch", "khaz");
        Assert.Equal(0, silvermere);
        Assert.True(khazarad > silvermere);
    }

    [Fact]
    public void Ticket_bank_of_godfrey_prefers_the_bank_over_the_street_outside()
    {
        int bank = CommandParser.ScoreRoomTextMatch("Bank of Godfrey", "a bank", "bank of godfrey");
        int street = CommandParser.ScoreRoomTextMatch(
            "Sovereign Street", "The Bank of Godfrey stands to the east.", "bank of godfrey");
        Assert.True(bank > street);
        Assert.Equal(100, bank);
    }

    [Fact]
    public void Ticket7_arly_does_not_match_nearly_in_a_description()
        => Assert.Equal(0, CommandParser.ScoreRoomTextMatch(
            "Silver Street, Eastern End", "The gate is nearly shut and clearly guarded.", "arly"));

    [Fact]
    public void Empty_or_absent_query_never_matches()
    {
        Assert.Equal(0, CommandParser.ScoreRoomTextMatch("Anywhere", "anything", ""));
        Assert.Equal(0, CommandParser.ScoreRoomTextMatch("Anywhere", "anything", "   "));
    }
}
