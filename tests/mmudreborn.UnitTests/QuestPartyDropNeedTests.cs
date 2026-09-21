using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// QUESTALLPARTY need gate (ticket #24): with the toggle ON, a main-quest ground-drop item goes only to the
// engaged players who are ON a quest that uses it and haven't yet passed the step that takes it — never to
// a player who never started the quest, finished that step, or already carries enough copies.
public sealed class QuestPartyDropNeedTests
{
    private const int ObsidianTalisman = 948;   // Evil 128(9) duergar lord "transport"
    private const int LockedWoodenBox = 622;    // Good 126(5) / Neutral 127(5) / Evil 128(2)
    private const int ElfHead = 776;            // Evil 128(3) takes ten

    private static bool Needs(Player player, int itemId)
        => CommandParser.PlayerNeedsQuestPartyDrop(player, itemId, CommandParser.QuestPartyDropNeeds[itemId]);

    private static Player PlayerAtStage(int flag, int stage)
    {
        var player = new Player { Name = "Quester" };
        if (stage > 0)
            player.GrantQuestAbility(flag, stage);
        return player;
    }

    [Theory]
    [InlineData(0, false)]   // never started the evil quest — the ticket's case
    [InlineData(1, true)]    // on the quest, step not reached yet
    [InlineData(8, true)]    // at the golem kill step
    [InlineData(9, true)]    // at the turn-in step
    [InlineData(10, false)]  // turn-in done
    [InlineData(31, false)]  // quest finished
    public void Talisman_goes_only_to_players_on_the_quest_before_the_turn_in(int evilStage, bool expected)
    {
        Assert.Equal(expected, Needs(PlayerAtStage(128, evilStage), ObsidianTalisman));
    }

    [Fact]
    public void Talisman_is_not_needed_by_a_good_or_neutral_quester()
    {
        Assert.False(Needs(PlayerAtStage(126, 9), ObsidianTalisman));
        Assert.False(Needs(PlayerAtStage(127, 9), ObsidianTalisman));
    }

    [Fact]
    public void A_player_already_carrying_the_item_does_not_need_another()
    {
        var player = PlayerAtStage(128, 9);
        player.Inventory.Add(ObsidianTalisman);
        Assert.False(Needs(player, ObsidianTalisman));
    }

    [Fact]
    public void Equipped_copy_counts_as_carried()
    {
        var player = PlayerAtStage(128, 9);
        player.Equipment["Neck"] = ObsidianTalisman;
        Assert.False(Needs(player, ObsidianTalisman));
    }

    [Fact]
    public void Elf_heads_are_needed_until_ten_are_carried()
    {
        var player = PlayerAtStage(128, 3);
        for (int i = 0; i < 9; i++)
            player.Inventory.Add(ElfHead);
        Assert.True(Needs(player, ElfHead));

        player.Inventory.Add(ElfHead);
        Assert.False(Needs(player, ElfHead));
    }

    [Theory]
    [InlineData(126, 5, true)]
    [InlineData(126, 6, false)]
    [InlineData(127, 5, true)]
    [InlineData(127, 6, false)]
    [InlineData(128, 2, true)]
    [InlineData(128, 3, false)]
    public void Locked_wooden_box_follows_each_alignment_chain(int flag, int stage, bool expected)
    {
        Assert.Equal(expected, Needs(PlayerAtStage(flag, stage), LockedWoodenBox));
    }
}
