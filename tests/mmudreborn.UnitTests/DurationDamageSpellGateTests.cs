using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Phase 2 of the DoT wiring: the stock single-target,
// monster-target and room-duration paths gate ability 1 (damage) and 8 (drain) on the
// spell's Duration field — Duration==0 is an instant hit, Duration>0 is a damage-over-time slot that
// ticks `magnitude` HP per medium upkeep with NO instant damage. CommandParser.IsDurationDamageSpell is
// the gate every cast path now routes through; these tests pin it so a regression can't silently turn
// the 64 stock DoTs (ice storm, acid rain, plague, leprosy, envelops/fire, …) back into instant hits.
public sealed class DurationDamageSpellGateTests
{
    private static GameSpell Harm(int ability, int duration, int abilityValue = 0)
        => new() { Number = 999, Duration = duration, Abilities = { [ability] = abilityValue } };

    [Fact]
    public void Damage_ability_with_duration_is_a_dot()
        => Assert.True(CommandParser.IsDurationDamageSpell(Harm(ability: 1, duration: 10)));

    [Fact]
    public void Drain_ability_with_duration_is_a_dot()
        => Assert.True(CommandParser.IsDurationDamageSpell(Harm(ability: 8, duration: 7)));

    [Fact]
    public void Damage_ability_without_duration_is_instant()
        => Assert.False(CommandParser.IsDurationDamageSpell(Harm(ability: 1, duration: 0)));

    [Fact]
    public void Elemental_ability_17_is_never_a_dot()
    {
        // Ability 17 (elemental) has no per-tick upkeep case — it is instant-only even with a duration.
        Assert.False(CommandParser.IsDurationDamageSpell(Harm(ability: 17, duration: 20)));
    }

    [Fact]
    public void Poison_ability_19_is_not_a_dot_slot()
    {
        // Poison (ability 19) drains via the separate PoisonLevel accumulator on the SLOW tick, not the
        // active-spell DoT slot — so it must not be routed through the duration-damage gate.
        Assert.False(CommandParser.IsDurationDamageSpell(Harm(ability: 19, duration: 30)));
    }

    [Fact]
    public void Pure_buff_with_duration_is_not_a_dot()
    {
        // A speed/haste-style timed buff (ability 87) carries a duration but no harm ability.
        Assert.False(CommandParser.IsDurationDamageSpell(Harm(ability: 87, duration: 60)));
    }

    [Theory]
    // Live game_data: every one of these carries ability 1 (or 8) plus a Duration → must be a DoT.
    [InlineData(116, 1, 10)]   // envelops (fire)
    [InlineData(135, 1, 15)]   // ice storm
    [InlineData(139, 1, 20)]   // acid rain
    [InlineData(294, 1, 40)]   // plague
    [InlineData(362, 1, 14)]   // leprosy
    [InlineData(958, 8, 7)]    // necro drain
    public void Stock_duration_harm_spells_are_dots(int number, int ability, int duration)
        => Assert.True(CommandParser.IsDurationDamageSpell(
            new GameSpell { Number = number, Duration = duration, Abilities = { [ability] = 0 } }));
}
