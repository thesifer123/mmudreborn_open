using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The summon loops (self/ally, area, and
// player cast) all walk ALL TEN ability slots and spawn once per slot carrying
// ability 12. The template id is that slot's AbilVal, or the single magnitude rolled before the loop
// when the slot's value is 0. ResolveSummonTemplateIds is the shared implementation of that rule.
public class SummonSlotResolutionTests
{
    private const int SummonAbilityId = 12;

    private static GameSpell BuildSummonSpell(int minBase, int maxBase, params (int Ability, int Value)[] slots)
    {
        var spell = new GameSpell { Number = 1, Name = "test summon", MinBase = minBase, MaxBase = maxBase };
        foreach (var (ability, value) in slots)
        {
            spell.Abilities[ability] = value;   // the dictionary collapses duplicates, exactly as in the loader
            spell.AbilitySlots.Add(new KeyValuePair<int, int>(ability, value));
        }

        return spell;
    }

    // The hydra's create spell #90 shape: ability 12 six times, every slot naming the hydra head (590).
    // Stock puts a full set of six heads in the pit the moment the hydra spawns, and the head's own
    // GameLimit is 6 — the slot count and the game limit agree. Reading the Abilities DICTIONARY collapses
    // those six slots to one entry, which is what limited the pit to a single head.
    [Fact]
    public void Six_identical_summon_slots_resolve_to_six_summons()
    {
        var spell = BuildSummonSpell(0, 0,
            (SummonAbilityId, 590), (SummonAbilityId, 590), (SummonAbilityId, 590),
            (SummonAbilityId, 590), (SummonAbilityId, 590), (SummonAbilityId, 590));

        var ids = CommandParser.ResolveSummonTemplateIds(spell, new Random(1));

        Assert.Equal(new[] { 590, 590, 590, 590, 590, 590 }, ids);
    }

    // The Great Hydra's #1309: two massive-hydra-head (1029) slots, matching that head's GameLimit of 2.
    // Trailing non-summon slots are ignored.
    [Fact]
    public void Only_summon_slots_contribute_and_slot_order_is_preserved()
    {
        var spell = BuildSummonSpell(0, 0,
            (SummonAbilityId, 1029), (SummonAbilityId, 1029), (1, 25), (115, 8224));

        var ids = CommandParser.ResolveSummonTemplateIds(spell, new Random(1));

        Assert.Equal(new[] { 1029, 1029 }, ids);
    }

    // A summon spell whose slots carry different templates spawns each in turn ("burning summon" #1182
    // spawns both the flaming waist and the flaming torso).
    [Fact]
    public void Distinct_summon_slots_each_resolve_to_their_own_template()
    {
        var spell = BuildSummonSpell(0, 0, (SummonAbilityId, 535), (SummonAbilityId, 536));

        var ids = CommandParser.ResolveSummonTemplateIds(spell, new Random(1));

        Assert.Equal(new[] { 535, 536 }, ids);
    }

    // A zero-value slot takes the rolled MinBase..MaxBase id instead (the dragon-summon-tapestry
    // convention), and stock rolls that magnitude ONCE before the slot loop — so two zero-value slots
    // summon the same species twice, not two different rolls.
    [Fact]
    public void Zero_value_slots_share_one_rolled_min_base_fallback()
    {
        var spell = BuildSummonSpell(1002, 1006, (SummonAbilityId, 0), (SummonAbilityId, 0));

        var ids = CommandParser.ResolveSummonTemplateIds(spell, new Random(7));

        Assert.Equal(2, ids.Count);
        Assert.Equal(ids[0], ids[1]);
        Assert.InRange(ids[0], 1002, 1006);
    }

    // A trailing zero-value slot must not make the whole spell read as "not a summon": the Abilities
    // dictionary keeps only the LAST slot, so the old dictionary-based gate saw {12: 0} with no MinBase
    // pool and resolved nothing at all.
    [Fact]
    public void A_trailing_zero_value_slot_does_not_cancel_the_earlier_summons()
    {
        var spell = BuildSummonSpell(0, 0, (SummonAbilityId, 590), (SummonAbilityId, 0));

        Assert.True(CommandParser.MonsterSpellSummonsReinforcement(spell));
        Assert.Equal(new[] { 590 }, CommandParser.ResolveSummonTemplateIds(spell, new Random(1)));
    }

    // No usable id (no template, no MinBase pool) is not a summon at all — the caller falls through to
    // its normal routing rather than trying to spawn monster #0.
    [Fact]
    public void A_summon_slot_with_no_usable_id_resolves_to_nothing()
    {
        var spell = BuildSummonSpell(0, 0, (SummonAbilityId, 0));

        Assert.False(CommandParser.MonsterSpellSummonsReinforcement(spell));
        Assert.Empty(CommandParser.ResolveSummonTemplateIds(spell, new Random(1)));
    }
}
