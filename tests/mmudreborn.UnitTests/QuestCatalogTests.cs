using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// QuestCatalog is internal; tests reach it via InternalsVisibleTo (mmudreborn.UnitTests is already a
// friend assembly — other tests touch internal Game types). Covers the staged-quest map, the
// flag-vs-alias resolver, and the grantable-ability whitelist.
public sealed class QuestCatalogTests
{
    [Theory]
    [InlineData("126", 126)]
    [InlineData("good", 126)]
    [InlineData("GOOD", 126)]
    [InlineData("evil", 128)]
    [InlineData("dragon", 131)]
    [InlineData("dao", 134)]
    public void TryResolve_accepts_flag_numbers_and_name_aliases(string token, int expectedFlag)
    {
        Assert.True(QuestCatalog.TryResolve(token, out int flag));
        Assert.Equal(expectedFlag, flag);
    }

    [Theory]
    [InlineData("999")]   // numeric but not a quest flag
    [InlineData("77")]    // a real ability, but not a quest flag
    [InlineData("bogus")]
    public void TryResolve_rejects_unknown_tokens(string token)
    {
        Assert.False(QuestCatalog.TryResolve(token, out _));
    }

    [Fact]
    public void Catalog_has_correct_complete_values_and_multistep_flags()
    {
        Assert.True(QuestCatalog.TryGet(126, out var good));
        Assert.Equal("Good Alignment Quest", good.Name);
        Assert.Equal(31, good.CompleteValue);
        Assert.True(good.MultiStep);

        // Adult Red Dragon is atomic (touch ruby ticks the flag 1->4 in one action) and completes at 4.
        Assert.True(QuestCatalog.TryGet(131, out var dragon));
        Assert.Equal(4, dragon.CompleteValue);
        Assert.False(dragon.MultiStep);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(125)]
    [InlineData(134)]
    public void IsQuestFlag_true_for_progress_flags(int flag) => Assert.True(QuestCatalog.IsQuestFlag(flag));

    [Theory]
    [InlineData(186)]  // Perfect Stealth — a reward, not a progress flag
    [InlineData(77)]
    public void IsQuestFlag_false_for_non_progress_abilities(int abilityId) => Assert.False(QuestCatalog.IsQuestFlag(abilityId));

    [Theory]
    [InlineData(186)]  // Perfect Stealth
    [InlineData(34)]   // Dodge
    [InlineData(70)]   // Spellcasting
    public void IsGrantable_true_for_reward_abilities(int abilityId) => Assert.True(QuestCatalog.IsGrantable(abilityId));

    [Theory]
    [InlineData(102)]  // Race Stealth — intrinsic
    [InlineData(103)]  // Class Stealth — intrinsic
    [InlineData(126)]  // quest-progress flag — staged via SYSOP QUEST, not granted
    public void IsGrantable_false_for_intrinsic_or_quest_flags(int abilityId) => Assert.False(QuestCatalog.IsGrantable(abilityId));
}
