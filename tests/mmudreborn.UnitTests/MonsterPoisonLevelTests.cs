using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #140: a player poisoned by mermex queen/guards could still rest. The mermex poison (spell #331:
// Targets 8, Min/MaxBase -5, abilities 44/46/48 = str/will/health -5, ability 19 = poison value 1) is a
// stat-drain DoT whose Min/MaxBase is NEGATIVE. The poison-level setter used that rolled spell magnitude
// (-5), so Math.Max(0, -5) left PoisonLevel at 0 and the rest/meditate "too sick" gate never fired.
// Cast-effect ability 19 sets the poison field to the POISON ability's own value (1), gated to the
// single-TARGET type ({0,2,6,8}) — NOT AttType. These tests pin that magnitude source and
// the Targets gate.
public sealed class MonsterPoisonLevelTests
{
    // These targets are non-immune (no ability 21), so the boolean immunity gate is a no-op here; an
    // empty db satisfies the new signature. Immunity itself is pinned by PoisonImmunityTests.
    private static readonly Data.IGameDatabase EmptyDb = new InMemoryGameDatabase();

    private static GameSpell MermexPoison() => new()
    {
        Number = 331, Name = "mermex poison", Targets = 8, AttType = 6, MinBase = -5, MaxBase = -5,
        Abilities = new() { [44] = -5, [46] = -5, [48] = -5, [19] = 1 },
    };

    [Fact]
    public void Mermex_poison_sets_poison_level_from_ability_value_not_negative_spell_magnitude()
    {
        var player = new Player();
        Assert.Equal(0, player.PoisonLevel);

        // rolledMagnitude -5 is the (negative) spell magnitude the caller would pass; it must be ignored
        // in favour of ability 19's value (1).
        CommandParser.ApplyMonsterPoisonLevel(MermexPoison(), player, rolledMagnitude: -5, db: EmptyDb);

        Assert.Equal(1, player.PoisonLevel);
    }

    [Fact]
    public void Poison_level_never_decreases_takes_the_max()
    {
        var player = new Player { PoisonLevel = 9 };
        CommandParser.ApplyMonsterPoisonLevel(MermexPoison(), player, rolledMagnitude: -5, db: EmptyDb);
        Assert.Equal(9, player.PoisonLevel); // max(9, 1)
    }

    [Fact]
    public void Zero_ability_value_falls_back_to_the_rolled_magnitude()
    {
        // A classic poison whose ability 19 carries value 0 uses the rolled (positive) spell magnitude.
        var spell = new GameSpell { Number = 1, Targets = 2, Abilities = new() { [19] = 0 } };
        var player = new Player();

        CommandParser.ApplyMonsterPoisonLevel(spell, player, rolledMagnitude: 12, db: EmptyDb);

        Assert.Equal(12, player.PoisonLevel);
    }

    [Theory]
    // The stock gate: only single-TARGET types 0/2/6/8 raise the poison
    // field. Area types (3/12/…) and others do not. Regression: the iconic spider poison #761 is AttType 4
    // but Targets 8 — gating on AttType (the old bug) silently dropped all spider/wasp/asp poison.
    [InlineData(0, 1)]
    [InlineData(2, 1)]
    [InlineData(6, 1)]
    [InlineData(8, 1)]
    [InlineData(3, 0)]
    [InlineData(12, 0)]
    public void Poison_field_is_gated_to_target_types_0_2_6_8(int targets, int expected)
    {
        var spell = new GameSpell { Targets = targets, Abilities = new() { [19] = 1 } };
        var player = new Player();

        CommandParser.ApplyMonsterPoisonLevel(spell, player, rolledMagnitude: -5, db: EmptyDb);

        Assert.Equal(expected, player.PoisonLevel);
    }

    [Fact]
    public void Spider_poison_att_type_4_still_applies_because_it_targets_single_8()
    {
        // spider poison #761: AttType 4 but Targets 8, ability 19 value 0 (rolled magnitude). The AttType
        // gate dropped it; the Targets gate applies it.
        var spiderPoison = new GameSpell { Number = 761, Targets = 8, AttType = 4, Abilities = new() { [19] = 0 } };
        var player = new Player();

        CommandParser.ApplyMonsterPoisonLevel(spiderPoison, player, rolledMagnitude: 15, db: EmptyDb);

        Assert.Equal(15, player.PoisonLevel);
    }

    [Fact]
    public void Spell_without_poison_ability_does_not_set_poison_level()
    {
        var spell = new GameSpell { Targets = 8, Abilities = new() { [44] = -5 } };
        var player = new Player();

        CommandParser.ApplyMonsterPoisonLevel(spell, player, rolledMagnitude: 5, db: EmptyDb);

        Assert.Equal(0, player.PoisonLevel);
    }

    // The "poison never wears off" bug: a rolled-magnitude poison (ability 19 value 0) re-hit each combat
    // round set PoisonLevel to the running MAX, but AddOrRefreshActiveSpell overwrote the slot's stored
    // magnitude with the LATEST (often weaker) roll. On expiry ReverseTerminatedPoison subtracted only the
    // slot value, leaving a residual poison level that never cleared. The fix: the slot keeps the MAX
    // magnitude, so expiry reverses the full amount back to zero.
    [Fact]
    public void Poison_slot_keeps_the_max_roll_so_expiry_clears_the_full_poison_level()
    {
        var spiderPoison = new GameSpell { Number = 761, Targets = 8, AttType = 4, Abilities = new() { [19] = 0 } };
        var player = new Player();

        // Round 1: strong roll 18 — registers the slot, sets PoisonLevel.
        player.AddOrRefreshActiveSpell(spiderPoison.Number, 18, duration: 100);
        CommandParser.ApplyMonsterPoisonLevel(spiderPoison, player, rolledMagnitude: 18, db: EmptyDb);
        // Round 2: weaker roll 11 — refreshes the slot, PoisonLevel stays at the max.
        player.AddOrRefreshActiveSpell(spiderPoison.Number, 11, duration: 100);
        CommandParser.ApplyMonsterPoisonLevel(spiderPoison, player, rolledMagnitude: 11, db: EmptyDb);

        Assert.Equal(18, player.PoisonLevel);
        var slot = Assert.Single(player.ActiveSpells);
        Assert.Equal(18, slot.CastLevel); // kept the max, not overwritten to 11

        // On termination the full poison level is reversed back to zero — no lingering residual.
        SpellEffects.ReverseTerminatedPoison(player, spiderPoison, slot.CastLevel);
        Assert.Equal(0, player.PoisonLevel);
    }
}
