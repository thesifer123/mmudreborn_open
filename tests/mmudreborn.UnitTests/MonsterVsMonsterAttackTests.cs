using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// The monster-vs-monster swing a player-pet uses against a hostile monster.
public sealed class MonsterVsMonsterAttackTests
{
    private static MonsterInstance MakeMonster(int number, int hp, int ac, int min, int max, int accuracy)
    {
        var template = new Monster
        {
            Number = number,
            Name = $"mob{number}",
            HP = hp,
            ArmourClass = ac,
            DamageResist = 0,
            BSDefense = 0,
            Attacks = accuracy > 0
                ? [new MonsterAttack { SlotIndex = 0, Type = 1, Percent = 100, Accuracy = accuracy, Min = min, Max = max }]
                : [],
        };
        return MonsterInstance.Create(template, 1, 1);
    }

    [Fact]
    public void Strong_attacker_damages_and_kills_a_weak_defender()
    {
        var attacker = MakeMonster(9001, hp: 100, ac: 0, min: 40, max: 50, accuracy: 500);
        var defender = MakeMonster(9002, hp: 30, ac: 0, min: 1, max: 1, accuracy: 0);

        bool killed = false;
        bool everHit = false;
        for (int i = 0; i < 5 && !defender.IsDead; i++)
        {
            var result = CombatEngine.MonsterVsMonsterAttack(attacker, defender);
            if (result.Damage > 0)
                everHit = true;
            if (result.Outcome == CombatEngine.MonsterVsMonsterOutcome.Kill)
                killed = true;
        }

        Assert.True(everHit, "a 500-accuracy attacker vs a 0-AC defender should land at least one hit in 5 swings");
        Assert.True(killed && defender.IsDead, "40-50 damage swings should kill a 30-HP defender within 5 rounds");
    }

    [Fact]
    public void A_dead_attacker_does_nothing()
    {
        var attacker = MakeMonster(9003, hp: 100, ac: 0, min: 40, max: 50, accuracy: 500);
        var defender = MakeMonster(9004, hp: 30, ac: 0, min: 1, max: 1, accuracy: 0);
        attacker.CurrentHP = 0;

        var result = CombatEngine.MonsterVsMonsterAttack(attacker, defender);

        Assert.Equal(CombatEngine.MonsterVsMonsterOutcome.Miss, result.Outcome);
        Assert.Equal(30, defender.CurrentHP);
    }
}
