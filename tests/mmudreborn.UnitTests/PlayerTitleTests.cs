using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class PlayerTitleTests
{
    [Theory]
    [InlineData("Mystic", 3, 0, "Mystic Novice")]
    [InlineData("Mystic", 65, 1, "Kai Mistress")]
    [InlineData("Mystic", 70, 0, "Supreme Kai")]
    [InlineData("Paladin", 60, 1, "Lady Justice")]
    [InlineData("Unknown", 1, 0, "Apprentice")]
    public void GetTitle_returns_expected_title_for_class_level_and_gender(string className, int level, int gender, string expected)
    {
        var player = new Player
        {
            Level = level,
            Gender = gender,
        };
        var cls = new CharacterClass
        {
            Name = className,
        };

        string title = player.GetTitle(cls);

        Assert.Equal(expected, title);
    }
}
