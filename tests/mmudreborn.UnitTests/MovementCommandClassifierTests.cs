using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Pins CommandParser.IsMovementCommand, which decides whether GameSession pays the stock delay-before-move
// (the move's own delay, waited off-gate before relocating). It must match ONLY directional moves; a
// regression that classified a combat/info verb as movement would make e.g. `bs <monster>` pause for the
// move delay before engaging. Directions in, everything else (combat/look/get/door/go/text-exits) out.
public sealed class MovementCommandClassifierTests
{
    [Theory]
    [InlineData("n")]
    [InlineData("s")]
    [InlineData("e")]
    [InlineData("w")]
    [InlineData("ne")]
    [InlineData("nw")]
    [InlineData("se")]
    [InlineData("sw")]
    [InlineData("u")]
    [InlineData("d")]
    [InlineData("north")]
    [InlineData("southwest")]
    [InlineData("down")]
    [InlineData("  N  ")]   // trimmed + case-insensitive
    [InlineData("NorthEast")]
    public void Recognizes_directional_moves(string input)
    {
        Assert.True(CommandParser.IsMovementCommand(input));
    }

    [Theory]
    [InlineData("bs goblin")]      // backstab — the reported case: must be immediate after a move
    [InlineData("backstab orc")]
    [InlineData("attack rat")]
    [InlineData("a kobold")]
    [InlineData("cast 1 goblin")]
    [InlineData("look")]
    [InlineData("get all")]
    [InlineData("open door")]      // the door delay is the action gate, not move pacing
    [InlineData("bashdoor north")]
    [InlineData("go portal")]      // special/text exits stay immediate after arrival (still pace next move)
    [InlineData("enter cave")]
    [InlineData("nod")]            // starts with 'n' but is not a direction token
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/tell bob hi")]   // speech prefixes are never movement
    [InlineData("'hello")]
    public void Rejects_non_directional_commands(string input)
    {
        Assert.False(CommandParser.IsMovementCommand(input));
    }
}
