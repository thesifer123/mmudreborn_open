using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// The slow monster update regenerates a monster's HP by its per-monster rate (HPRegen)
// ONCE per global slow-update cycle — the slow updater round-robins one monster
// slot per background tick, so each monster is visited once per full pass. Megamud surfaces this as
// "Regens: 90 HPs every 90 seconds [18 rounds]" (chimera, HPRegen=90). The prior implementation
// applied HPRegen every 3s world tick, healing ~30x too fast; TickHpRegen restores the per-interval
// cadence. Interval here mirrors GameWorld.MonsterHpRegenIntervalTicks (90s / 3s = 30 ticks).
public sealed class MonsterInstanceHpRegenTests
{
    private const int IntervalTicks = 30;

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

    private static void Advance(MonsterInstance monster, int ticks)
    {
        for (int i = 0; i < ticks; i++)
            monster.TickHpRegen(IntervalTicks);
    }

    [Fact]
    public void Regen_applies_once_per_interval_not_every_tick()
    {
        // Chimera: 900 HP, regens 90 per cycle. Damaged to 450.
        var chimera = NewMonster(hp: 450, maxHp: 900, hpRegen: 90);

        // First processed tick fires once (cooldown defaults to 0, mirroring a freshly
        // initialized regen marker), then the countdown is seeded. One chunk, not 30.
        chimera.TickHpRegen(IntervalTicks);
        Assert.Equal(540, chimera.CurrentHP);

        // The next 29 ticks within the interval add nothing — regen is NOT per-tick.
        Advance(chimera, IntervalTicks - 1);
        Assert.Equal(540, chimera.CurrentHP);

        // Crossing the interval boundary adds exactly one more chunk.
        Advance(chimera, 1);
        Assert.Equal(630, chimera.CurrentHP);
    }

    [Fact]
    public void Regen_clamps_to_max_hp()
    {
        var monster = NewMonster(hp: 880, maxHp: 900, hpRegen: 90);

        Advance(monster, IntervalTicks + 1); // seed + one full cycle
        Assert.Equal(900, monster.CurrentHP);

        // At full HP the cadence keeps running but adds nothing.
        Advance(monster, IntervalTicks);
        Assert.Equal(900, monster.CurrentHP);
    }

    [Fact]
    public void Dead_monster_does_not_regen()
    {
        var monster = NewMonster(hp: 0, maxHp: 900, hpRegen: 90);

        Advance(monster, IntervalTicks * 3);

        Assert.True(monster.IsDead);
        Assert.Equal(0, monster.CurrentHP);
    }

    [Fact]
    public void Zero_regen_monster_never_heals()
    {
        var monster = NewMonster(hp: 5, maxHp: 12, hpRegen: 0);

        Advance(monster, IntervalTicks * 3);

        Assert.Equal(5, monster.CurrentHP);
    }
}
