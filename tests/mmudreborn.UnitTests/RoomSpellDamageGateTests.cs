using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #?: at 15/1138 (Lava Tube, Spell=526 magma heat) the room spell never ran because the room-
// spell pulse code only handled spells carrying ability 148 (Trigger Text Block) and bailed on
// everything else. After the pulse path was wired to direct-cast non-textblock spells, a sibling
// concern surfaced: pure buff/regen room spells (inn rest, mana drain, jail heal) have a positive
// MinBase but no damage ability, and would otherwise be miscast as damage rolls.
// IsRoomSpellDamageEligible is the gate: only spells that carry one of {1 HP-damage, 8 drain,
// 17 elemental} AND have a valid MinBase..MaxBase band are treated as damaging room spells. Other
// spells silently skip the damage path (still a TODO to implement their buff/heal effects).
public sealed class RoomSpellDamageGateTests
{
    [Fact]
    public void Magma_heat_is_eligible_for_room_spell_damage()
    {
        // Spell 526 abilities (per live game_data): 1 (HP damage), 115 (DescMsg). Damage 30..60.
        var spell = new GameSpell
        {
            Number = 526,
            MinBase = 30,
            MaxBase = 60,
            Abilities = { [1] = 0, [115] = 66 },
        };

        Assert.True(GameWorld.IsRoomSpellDamageEligible(spell));
    }

    [Fact]
    public void Inn_rest_is_not_eligible_for_room_spell_damage()
    {
        // Spell 484 (inn rest): MinBase/MaxBase=100, abilities 123 (Alter healing rate), 144, 115.
        // No HP-damage ability — without the gate it would deal 100 damage per pulse in every inn.
        var spell = new GameSpell
        {
            Number = 484,
            MinBase = 100,
            MaxBase = 100,
            Abilities = { [123] = 200, [144] = 0, [115] = 66 },
        };

        Assert.False(GameWorld.IsRoomSpellDamageEligible(spell));
    }

    [Fact]
    public void Jail_heal_is_not_eligible_for_room_spell_damage()
    {
        // Spell 677 (jail heal): ability 18 (Heal) — restorative, not damage.
        var spell = new GameSpell
        {
            Number = 677,
            MinBase = 1,
            MaxBase = 1,
            Abilities = { [18] = 0, [144] = 0, [115] = 66 },
        };

        Assert.False(GameWorld.IsRoomSpellDamageEligible(spell));
    }

    [Fact]
    public void Drain_room_spell_with_negative_base_is_not_eligible()
    {
        // Spell 412 (mana rgen drain): MinBase=-100, MaxBase=-25 — the negative band signals "drain"
        // semantics handled by the active-spell upkeep, NOT a damage roll. The MinBase<=0 guard
        // skips it so the room-cast tick doesn't accidentally heal players (negative damage).
        var spell = new GameSpell
        {
            Number = 412,
            MinBase = -100,
            MaxBase = -25,
            Abilities = { [145] = 0, [144] = 0, [115] = 66 },
        };

        Assert.False(GameWorld.IsRoomSpellDamageEligible(spell));
    }

    [Fact]
    public void Elemental_damage_ability_alone_is_eligible()
    {
        // Hypothetical room spell carrying only ability 17 (elemental damage). The gate accepts
        // {1, 8, 17}; this pins the elemental-only path so the slot doesn't silently regress.
        var spell = new GameSpell
        {
            Number = 9999,
            MinBase = 10,
            MaxBase = 20,
            Abilities = { [17] = 5 },
        };

        Assert.True(GameWorld.IsRoomSpellDamageEligible(spell));
    }

    // --- The room-cast dispatcher gates (Targets 0/2/6/8 AND Duration 0, ability 17 excepted) ---

    [Fact]
    public void Exit_muddy_water_is_not_eligible_it_is_Targets_1_and_timed()
    {
        // #681 "exit muddy water", the exit-type-22 cast on surfacing from the Muddy Underwater
        // Passage: Targets 1, Duration 1, MinBase/MaxBase 1 as filler. The dispatcher applies NO instant
        // damage outside Targets 0/2/6/8, and a Duration>0 spell becomes a timed slot instead. Reading
        // its filler MinBase as damage is what produced "stop mud drown hits you for 1 damage!".
        var spell = new GameSpell
        {
            Number = 681, MinBase = 1, MaxBase = 1, Targets = 1, Duration = 1,
            Abilities = { [151] = 682, [115] = 66 },
        };

        Assert.False(GameWorld.IsRoomCastInstantDamageEligible(spell));
    }

    [Fact]
    public void A_damage_spell_with_an_ineligible_target_type_is_not_instant_damage()
    {
        var spell = new GameSpell
        {
            Number = 900, MinBase = 10, MaxBase = 20, Targets = 3, Duration = 0,
            Abilities = { [1] = 0 },
        };

        Assert.False(GameWorld.IsRoomCastInstantDamageEligible(spell));
    }

    [Fact]
    public void Envelops_830_is_a_timed_slot_not_a_per_pulse_hit()
    {
        // #830 "envelops" (2 rooms on map 16): ability 1, Targets 8, Duration 10. Stock defers
        // it to a timed slot — damage over time, not 2 HP every pulse.
        var spell = new GameSpell
        {
            Number = 830, MinBase = 2, MaxBase = 2, Targets = 8, Duration = 10,
            Abilities = { [1] = 0, [115] = 66, [144] = 0 },
        };

        Assert.False(GameWorld.IsRoomCastInstantDamageEligible(spell));
    }

    [Fact]
    public void Elemental_ability_17_ignores_the_duration_gate()
    {
        // Ability 17 keeps the Targets gate but has no duration test and never defers — #884
        // "envelops" (ability 17, Targets 8, Duration 10) still lands instantly.
        var spell = new GameSpell
        {
            Number = 884, MinBase = 6, MaxBase = 8, Targets = 8, Duration = 10,
            Abilities = { [17] = 0, [115] = 66, [144] = 0 },
        };

        Assert.True(GameWorld.IsRoomCastInstantDamageEligible(spell));
    }
}
