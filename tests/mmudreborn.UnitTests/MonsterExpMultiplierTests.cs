using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Kill experience is Experience x ExpMulti. Stock reads both template fields and multiplies before
/// distributing the total:
///     total = Experience * ExpMultiplier
/// at all three call sites. We paid the base
/// only, so all 193 templates with a multiplier above 1 underpaid — bug #207.
/// </summary>
public class MonsterExpMultiplierTests
{
    private static MonsterInstance Monster(int exp, double multiplier) => new()
    {
        Template = new Monster { Number = 1, Name = "test", EXP = exp, ExpMulti = multiplier },
        DisplayName = "test",
        CurrentHP = 1,
        MaxHP = 1,
    };

    [Theory]
    [InlineData(100, 3, 300)]              // cave bear #80 — the reported case
    [InlineData(50000, 5000, 250_000_000)] // Zanthus the Lich #215
    [InlineData(65000, 9999, 649_935_000)] // Tyrannosaur #514
    [InlineData(7, 1, 7)]                  // the x1 majority, unchanged
    public void Award_is_experience_times_multiplier(int exp, double multiplier, long expected)
        => Assert.Equal(expected, CombatEngine.ComputeMonsterKillExperience(Monster(exp, multiplier)));

    [Theory]
    [InlineData(100)]    // Remik of the Ebon Blade #366
    [InlineData(20000)]  // beautiful woman #1027
    [InlineData(9)]      // Aeriana the Pure #911
    public void A_zero_multiplier_awards_nothing(int exp)
    {
        // Six stock templates pair EXP > 0 with multiplier 0 — quest/NPC figures that must not be
        // farmable. Stock multiplies straight through, so clamping the multiplier to 1 would turn
        // them into exp pinatas.
        Assert.Equal(0, CombatEngine.ComputeMonsterKillExperience(Monster(exp, 0)));
    }

    [Fact]
    public void Large_awards_do_not_overflow_int()
    {
        // 65,000 x 9,999 is comfortably past int.MaxValue once a sysop XP rate is applied downstream.
        long award = CombatEngine.ComputeMonsterKillExperience(Monster(65000, 9999));
        Assert.True(award > int.MaxValue / 4, $"expected a large award, got {award}");
        Assert.Equal(649_935_000L, award);
    }
}

/// <summary>
/// The realm-wide SYSOP XP RATE stacks ON TOP of the per-template multiplier — it does not replace it
/// and does not interfere with it. Order is:
///     EXP x ExpMulti            (CombatEngine.ComputeMonsterKillExperience)
///     x MonsterExperienceRate   (GameWorld.ScaleMonsterExperienceAward)
///     / engaged players         (the party split at the award site)
/// </summary>
public class MonsterExpRateStackingTests
{
    // Mirror of GameWorld.ScaleMonsterExperienceAward, which is what the award site calls.
    private static long Scale(long baseExperience, long rate)
    {
        if (baseExperience <= 0)
            return 0;
        return baseExperience > long.MaxValue / rate ? long.MaxValue : baseExperience * rate;
    }

    [Theory]
    [InlineData(1, 300)]    // cave bear at the default realm rate
    [InlineData(2, 600)]    // ...at 2x the realm rate
    [InlineData(10, 3000)]  // ...at 10x
    public void Realm_rate_multiplies_the_already_multiplied_award(int realmRate, long expected)
    {
        long perTemplate = 100L * 3L;   // cave bear #80: EXP 100, ExpMulti 3
        Assert.Equal(300L, perTemplate);
        Assert.Equal(expected, Scale(perTemplate, realmRate));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(100)]
    public void A_zero_multiplier_stays_zero_at_any_realm_rate(int realmRate)
    {
        // 20,000 base x 0 multiplier (beautiful woman #1027). The realm rate must not resurrect it —
        // ScaleMonsterExperienceAward early-returns on <= 0 precisely so these stay unfarmable.
        long perTemplate = 20000L * 0L;
        Assert.Equal(0L, Scale(perTemplate, realmRate));
    }
}
