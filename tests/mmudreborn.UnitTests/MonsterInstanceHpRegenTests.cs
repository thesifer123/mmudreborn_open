using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// The slow monster update regenerates a monster's HP by its per-monster rate (HPRegen) ONCE per slow
// update. How often that comes round is the slow sweep's business (GameWorld.MonsterSweeps — every 30 s
// cycle on a busy board, longer with few players in the game); these pin the per-update amount.
public sealed class MonsterInstanceHpRegenTests
{
    private static MonsterInstance NewMonster(int hp, int maxHp, int hpRegen)
    {
        return new MonsterInstance
        {
            Template = new Monster { Number = 313, Name = "chimera", HP = maxHp, HPRegen = hpRegen },
            CurrentHP = hp,
            MaxHP = maxHp,
            MapNumber = 1,
            RoomNumber = 1,
        };
    }

    private static void Advance(MonsterInstance monster, int slowUpdates)
    {
        for (int i = 0; i < slowUpdates; i++)
            monster.ApplySlowUpdate();
    }

    [Fact]
    public void Regen_applies_its_rate_once_per_slow_update()
    {
        // Chimera: 900 HP, regens 90 per slow update. Damaged to 450.
        var chimera = NewMonster(hp: 450, maxHp: 900, hpRegen: 90);

        chimera.ApplySlowUpdate();
        Assert.Equal(540, chimera.CurrentHP);

        chimera.ApplySlowUpdate();
        Assert.Equal(630, chimera.CurrentHP);
    }

    [Fact]
    public void Regen_clamps_to_max_hp()
    {
        var monster = NewMonster(hp: 880, maxHp: 900, hpRegen: 90);

        Advance(monster, 1);
        Assert.Equal(900, monster.CurrentHP);

        // At full HP further slow updates add nothing.
        Advance(monster, 3);
        Assert.Equal(900, monster.CurrentHP);
    }

    [Fact]
    public void Dead_monster_does_not_regen()
    {
        var monster = NewMonster(hp: 0, maxHp: 900, hpRegen: 90);

        Advance(monster, 3);

        Assert.True(monster.IsDead);
        Assert.Equal(0, monster.CurrentHP);
    }

    [Fact]
    public void Zero_regen_monster_never_heals()
    {
        var monster = NewMonster(hp: 5, maxHp: 12, hpRegen: 0);

        Advance(monster, 3);

        Assert.Equal(5, monster.CurrentHP);
    }
}
