using System.Collections.Generic;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Proves the monster energy model that lets an Adult Red Dragon (energy cap 1000) cast its 600-EU
    /// dragonfire TWICE in one combat round from round 2 onward: PrepareCombatRound mirrors the stock
    /// monster energy update, which ADDS the cap when current &lt; cap (no upward clamp), so a
/// monster that spends 600 of its 1000 pool in round 1 starts round 2 at 400+1000 = 1400 — enough for
/// two 600-EU casts. Uses one AtkType=2 cast slot at 100% selection so the outcome is deterministic.
/// </summary>
public sealed class MonsterEnergyDoubleCastTests
{
    private static MonsterInstance MakeDragon()
    {
        var template = new Monster
        {
            Number = 999,
            Name = "test dragon",
            Energy = 1000,
            Attacks =
            {
                // Single 600-EU spell slot, always selected (Percent 100). AtkType 2 = cast.
                new MonsterAttack { SlotIndex = 0, Type = 2, Accuracy = 265, Percent = 100, Energy = 600, Max = 50 },
            },
        };
        var monster = new MonsterInstance { Template = template, DisplayName = "test dragon", CurrentHP = 5000, MaxHP = 5000 };
        // Engage at a full pool (idle monster), like the live path.
        monster.CurrentEnergy = monster.GetCombatEnergyCap();
        return monster;
    }

    private static int CountCastsForRound(MonsterInstance monster, Player target)
    {
        monster.PrepareCombatRound();
        int casts = 0;
        CombatEngine.MonsterAttack(monster, target, null, null,
            (_, _) => { casts++; return (new List<string>(), new List<string>()); });
        return casts;
    }

    [Fact]
    public void Dragon_casts_once_in_round_one_then_twice_from_round_two()
    {
        var monster = MakeDragon();
        // Unkillable target so the swing loop runs the full pool, not cut short by a "death".
        var target = new Player { Name = "Target", MaxHP = 1_000_000, CurrentHP = 1_000_000 };

        // Round 1: pool = 1000 → one 600-EU cast (400 left, can't afford a second).
        Assert.Equal(1, CountCastsForRound(monster, target));
        Assert.Equal(400, monster.CurrentEnergy);

        // Round 2: 400 < 1000 → +1000 = 1400 pool → TWO 600-EU casts (200 left). The reported burst.
        Assert.Equal(2, CountCastsForRound(monster, target));
        Assert.Equal(200, monster.CurrentEnergy);

        // Round 3: 200 < 1000 → 1200 → two casts again (0 left). Steady-state oscillation 1,2,2,1,…
        Assert.Equal(2, CountCastsForRound(monster, target));
        Assert.Equal(0, monster.CurrentEnergy);
    }

    [Fact]
    public void Underspending_monster_is_clamped_down_to_one_cap_each_round()
    {
        // The clamp-DOWN branch: a monster that ends a round above its cap is reset
        // to exactly one cap, so cheap-attack monsters can't accumulate energy without bound.
        var monster = MakeDragon();
        monster.CurrentEnergy = 1800; // simulate an over-accumulated pool (> cap 1000)

        monster.PrepareCombatRound();

        Assert.Equal(1000, monster.CurrentEnergy);
    }
}
