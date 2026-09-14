using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Regression tests for the 2026-06-23 combat-formula audit.
public sealed class CombatFormulaAuditTests
{
    private static MonsterInstance MonsterWith(int templateDr, Dictionary<int, int>? abilities = null) => new()
    {
        Template = new Monster { Name = "mob", DamageResist = templateDr, Abilities = abilities ?? new() },
        DisplayName = "mob",
        CurrentHP = 100,
        MaxHP = 100,
    };

    // H1 — the monster marshal stores template DR * 10 then += ability 7 (raw); the
    // shared attack resolver subtracts DR/10. So the effective-DR value we feed it must be in
    // tenths: templateDR*10 + ability7. (Was templateDR + ability7, which the /10 shrank ~10x.)
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 10)]     // DR 1 → 10 tenths → /10 = 1 full point (previously 0)
    [InlineData(15, 0, 150)]   // DR 15 → 150 → /10 = 15 (previously 1)
    [InlineData(5, 20, 70)]    // template 5*10=50, + ability7 raw 20 = 70 → /10 = 7
    public void Monster_damage_resist_is_scaled_to_tenths(int templateDr, int ability7, int expected)
    {
        var abilities = ability7 > 0 ? new Dictionary<int, int> { [7] = ability7 } : null;
        Assert.Equal(expected, CombatEngine.GetEffectiveMonsterDamageResist(MonsterWith(templateDr, abilities)));
    }

    // M1 — smash-knockdown score = encumbrance% + Level + Strength.
    // That field is Strength (the strength→damage bonus reads it), NOT Willpower.
    [Fact]
    public void Smash_knockdown_score_tracks_strength_not_willpower()
    {
        var baseline = new Player { Level = 10, Strength = 60, Willpower = 99 };
        var moreStrength = new Player { Level = 10, Strength = 70, Willpower = 99 };
        var moreWillpower = new Player { Level = 10, Strength = 60, Willpower = 10 };

        int baseScore = CombatEngine.GetPlayerSmashKnockdownScore(baseline);

        // +10 Strength → +10 score.
        Assert.Equal(baseScore + 10, CombatEngine.GetPlayerSmashKnockdownScore(moreStrength));
        // Changing Willpower (Strength fixed) → no change.
        Assert.Equal(baseScore, CombatEngine.GetPlayerSmashKnockdownScore(moreWillpower));
    }
}
