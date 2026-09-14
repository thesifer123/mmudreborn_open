using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Poison immunity (ability 21) is BOOLEAN, not a graded resistance. The attack calc, ability 19,
// gates the poison-status application on ability 21 being ABSENT: a target immune from ANY source
// (Kang race, golden headdress, a buff) never has its PoisonLevel raised, so it takes no slow-tick drain
// and can still rest. Verified live on a stock server with a Kang: 0 damage AND can rest = never poisoned.
// The "You feel ill" the victim still sees comes from the spell's SEPARATE ability-115 message, not the
// poison status — so it is applied unconditionally and is not what these tests pin.
public sealed class PoisonImmunityTests
{
    private static GameSpell SingleTargetPoison(int abilityValue = 8)
        => new() { Number = 760, Name = "bites", Targets = 8, Abilities = { [19] = abilityValue } };

    [Fact]
    public void Immune_target_is_never_poisoned()
    {
        var db = new InMemoryGameDatabase();
        var immune = new Player { QuestAbilities = { [21] = 100 } };   // any nonzero ability-21 source = immune

        CommandParser.ApplyMonsterPoisonLevel(SingleTargetPoison(), immune, rolledMagnitude: 8, db);

        Assert.Equal(0, immune.PoisonLevel);   // boolean gate: PoisonLevel never raised
    }

    [Fact]
    public void Even_a_small_immunity_value_fully_blocks_the_poison_status()
    {
        // It is a presence check — a value of 1 blocks exactly like 100 (not scaled).
        var db = new InMemoryGameDatabase();
        var immune = new Player { QuestAbilities = { [21] = 1 } };

        CommandParser.ApplyMonsterPoisonLevel(SingleTargetPoison(), immune, rolledMagnitude: 12, db);

        Assert.Equal(0, immune.PoisonLevel);
    }

    [Fact]
    public void Non_immune_target_is_poisoned_at_full_magnitude()
    {
        var db = new InMemoryGameDatabase();
        var victim = new Player();

        CommandParser.ApplyMonsterPoisonLevel(SingleTargetPoison(abilityValue: 8), victim, rolledMagnitude: 8, db);

        Assert.Equal(8, victim.PoisonLevel);   // no immunity → poison applies unreduced
    }
}
