using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Byte-for-byte fidelity of the `party` per-member row against stock. The stock
// format string is " [H:%3.3s%%]%c%c%s" — the two status chars (poison, then
// rest/meditate) sit IMMEDIATELY after the "]" with no padding, and the rank string carries its own
// leading " - ". The old code inserted extra spaces, so the R/M/P flags rendered one+ columns too far
// right (the reported "Resting flag in the wrong spot").
public sealed class PartyDisplayTests
{
    private static string Row(bool poisoned = false, bool resting = false, bool meditating = false,
        GameWorld.PartyRank rank = GameWorld.PartyRank.Middle, bool showMana = true, bool usesKai = false,
        int manaPercent = 100, int hitsPercent = 100, string name = "Testuser Athelstan", string cls = "(Ranger)")
        => CommandParser.FormatPartyMemberRow(name, cls, showMana, usesKai, manaPercent, hitsPercent,
            poisoned, resting, meditating, rank);

    [Fact]
    public void Resting_flag_sits_directly_after_hits_with_a_single_blank_poison_slot()
        => Assert.EndsWith("[H:100%] R - Midrank", Row(resting: true));

    [Fact]
    public void Meditating_renders_M_in_the_rest_slot()
        => Assert.EndsWith("[H:100%] M - Backrank", Row(meditating: true, rank: GameWorld.PartyRank.Back));

    [Fact]
    public void Poison_occupies_the_first_status_slot()
        => Assert.EndsWith("[H:100%]P  - Frontrank", Row(poisoned: true, rank: GameWorld.PartyRank.Front));

    [Fact]
    public void Poisoned_and_resting_fill_both_slots_adjacently()
        => Assert.EndsWith("[H:100%]PR - Frontrank", Row(poisoned: true, resting: true, rank: GameWorld.PartyRank.Front));

    [Fact]
    public void Plain_member_shows_two_blank_status_slots_then_the_rank()
        => Assert.EndsWith("[H:100%]   - Midrank", Row()); // two blanks + " - " == three spaces

    [Fact]
    public void Kai_class_uses_a_K_mana_label()
        => Assert.Contains(" [K:100%]", Row(usesKai: true));

    [Fact]
    public void Non_kai_caster_uses_an_M_mana_label()
        => Assert.Contains(" [M:100%]", Row(usesKai: false));

    [Fact]
    public void Non_caster_shows_a_nine_space_mana_filler_not_a_bracket()
    {
        string row = Row(showMana: false);
        Assert.DoesNotContain("[M:", row);
        Assert.DoesNotContain("[K:", row);
        // 2 leading + 30 name + 1 + 12 class = column 45, then 9 blanks, then " [H:".
        Assert.Contains("(Ranger)              [H:100%]", row); // class(-12) + 9-space filler + " [H:"
    }

    [Fact]
    public void Full_row_is_byte_exact_for_the_stock_layout()
    {
        // Matches the stock reference: "  <name -30> <class -12> [M: 39%] [H:100%] R - Midrank".
        string row = Row(resting: true, manaPercent: 39, hitsPercent: 100);
        Assert.Equal(
            "  Testuser Athelstan             (Ranger)     [M: 39%] [H:100%] R - Midrank",
            row);
    }
}
