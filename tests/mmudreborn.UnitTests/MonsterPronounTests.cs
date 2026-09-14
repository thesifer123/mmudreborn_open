using mmudreborn.Data.Models;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class MonsterPronounTests
{
    [Fact]
    public void GetLookSubjectPronoun_returns_she_for_female_gender()
    {
        var monster = new Monster { Gender = 2, Description = "A fierce guardian watches you." };

        Assert.Equal("She", monster.GetLookSubjectPronoun());
    }

    [Fact]
    public void GetLookSubjectPronoun_returns_he_for_male_gender()
    {
        var monster = new Monster { Gender = 1, Description = "A wiry-looking man watches from the dock." };

        Assert.Equal("He", monster.GetLookSubjectPronoun());
    }

    [Fact]
    public void GetLookSubjectPronoun_uses_description_clues_when_gender_is_neutral()
    {
        var monster = new Monster
        {
            Gender = 0,
            Description = "This ancient gaunt one shares the wisdom of his people with passing adventurers."
        };

        Assert.Equal("He", monster.GetLookSubjectPronoun());
    }

    [Fact]
    public void GetLookSubjectPronoun_keeps_it_for_non_gendered_creatures()
    {
        var monster = new Monster
        {
            Gender = 0,
            Description = "A pulsing slime quivers across the stone floor without making a sound."
        };

        Assert.Equal("It", monster.GetLookSubjectPronoun());
    }
}
