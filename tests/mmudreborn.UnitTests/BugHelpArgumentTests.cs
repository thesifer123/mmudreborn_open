using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// "help bug" was always wired; "help bugs" was not, so the plural landed on the unknown-topic path.
/// The family it documents is plural-heavy (BUGS / LISTBUGS / SHOWBUG), so both must resolve.
/// </summary>
public class BugHelpArgumentTests
{
    [Theory]
    [InlineData("bug")]
    [InlineData("bugs")]
    [InlineData("BUGS")]
    [InlineData("  Bugs  ")]
    public void Both_numbers_reach_the_bug_help(string args)
        => Assert.True(CommandParser.IsBugHelpArgument(args));

    [Theory]
    [InlineData("")]
    [InlineData("exp")]
    [InlineData("bugreport")]
    [InlineData("debug")]
    public void Unrelated_help_topics_are_untouched(string args)
        => Assert.False(CommandParser.IsBugHelpArgument(args));
}
