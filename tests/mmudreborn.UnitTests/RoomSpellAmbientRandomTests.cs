using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Area-ambient flavor (e.g. Silvermere "drunken chorus", Darkwood forest noises) is driven by a room's
// Spell carrying ability 148 -> a textblock "random <tb>" -> a weighted "<threshold>:<command>" table.
// This pins the stock weighted selection: roll 0..99, run the FIRST line whose threshold
// exceeds the roll (ascending to 100; a leading "<n>:addexp 0" is the n% "nothing happens" slot).
public sealed class RoomSpellAmbientRandomTests
{
    // The live Silvermere ambient table (textblock 9056), fired by spell 918 "silvermere spell".
    private const string SilvermereTable =
        "77:addexp 0\n81:message 2645\n83:message 2646\n87:message 2647\n93:message 2648\n98:message 2649\n100:message 2650";

    [Theory]
    [InlineData(0, "addexp 0")]    // bottom of the 77% no-op slot
    [InlineData(76, "addexp 0")]   // last roll still in the no-op slot
    [InlineData(77, "message 2645")] // A dog barks off in the distance.
    [InlineData(80, "message 2645")]
    [InlineData(81, "message 2646")] // The awful sound of a drunken chorus echoes through the streets.
    [InlineData(82, "message 2646")]
    [InlineData(83, "message 2647")] // A cheer of many voices...
    [InlineData(92, "message 2648")] // Children rush past... youthful glee.
    [InlineData(97, "message 2649")] // Read the bulletin...
    [InlineData(99, "message 2650")] // A guardsman shouts out the time of day.
    public void Weighted_table_maps_roll_to_the_correct_command(int roll, string expected)
    {
        Assert.Equal(expected, GameWorld.SelectWeightedRoomSpellCommand(SilvermereTable, roll));
    }

    [Fact]
    public void Roll_at_or_above_the_top_threshold_selects_nothing()
    {
        // genrdn(0,100) can return 100; no line has threshold > 100, so the pulse emits nothing.
        Assert.Null(GameWorld.SelectWeightedRoomSpellCommand(SilvermereTable, 100));
    }

    [Fact]
    public void Darkwood_nested_random_line_keeps_its_full_command_chain()
    {
        // Darkwood (textblock 9055) has lines like "95:message 2654:random 9069" — the selector returns
        // the whole command part after the first colon, so the op runner can fire the message AND recurse
        // into the nested random table.
        const string darkwood =
            "70:addexp 0\n75:message 2651\n84:message 2652\n90:message 2653\n95:message 2654:random 9069\n100:message 2655:random 9069";

        Assert.Equal("message 2654:random 9069", GameWorld.SelectWeightedRoomSpellCommand(darkwood, 90));
        Assert.Equal("message 2651", GameWorld.SelectWeightedRoomSpellCommand(darkwood, 70));
    }

    [Fact]
    public void Malformed_or_empty_table_selects_nothing()
    {
        Assert.Null(GameWorld.SelectWeightedRoomSpellCommand("", 5));
        Assert.Null(GameWorld.SelectWeightedRoomSpellCommand("no thresholds here", 5));
    }
}
