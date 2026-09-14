using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class MonsterInstanceStaminaTests
{
    private static MonsterInstance NewMonster(int energyCap = 1000)
    {
        var template = new Monster
        {
            Number = 1,
            Name = "Test Rat",
            Energy = energyCap,
        };
        var monster = new MonsterInstance
        {
            Template = template,
            CurrentHP = 50,
            MaxHP = 50,
            MapNumber = 1,
            RoomNumber = 1,
        };
        monster.ResetEnergy();
        return monster;
    }

    [Fact]
    public void PrepareCombatRound_adds_cap_and_allows_overshoot_during_autocombat()
    {
        var monster = NewMonster(energyCap: 1000);
        Assert.Equal(0, monster.CurrentEnergy);

        // The energy update adds the cap only when current < cap. From empty → cap.
        monster.PrepareCombatRound();
        Assert.Equal(1000, monster.CurrentEnergy);

        // At exactly the cap (a fresh engage from a full pool) it does NOT double to 2×cap — no
        // opening-round burst. The += only fires once stamina has been spent below the cap.
        monster.PrepareCombatRound();
        Assert.Equal(1000, monster.CurrentEnergy);

        // After spending, the carried sub-cap remainder is topped up by the cap, overshooting it —
        // this is the in-combat carry that lets a fresh combatant burst before settling.
        monster.CurrentEnergy = 300;
        monster.PrepareCombatRound();
        Assert.Equal(1300, monster.CurrentEnergy);
    }

    [Fact]
    public void RefillEnergyOutOfCombat_clamps_overshoot_back_to_cap()
    {
        var monster = NewMonster(energyCap: 1000);
        monster.CurrentEnergy = 1750;

        monster.RefillEnergyOutOfCombat();

        // The slow tick clamps stamina to cap when the monster isn't engaged. Prevents stale
        // overshoot from a prior fight bleeding into the next engagement.
        Assert.Equal(1000, monster.CurrentEnergy);
    }

    [Fact]
    public void RefillEnergyOutOfCombat_raises_low_pool_up_to_cap()
    {
        var monster = NewMonster(energyCap: 1000);
        monster.CurrentEnergy = 200;

        monster.RefillEnergyOutOfCombat();

        Assert.Equal(1000, monster.CurrentEnergy);
    }

    [Fact]
    public void EngagedPlayerCount_tracks_MarkPlayerEngaged_calls()
    {
        var monster = NewMonster();
        Assert.Equal(0, monster.EngagedPlayerCount);

        monster.MarkPlayerEngaged("Goober");
        Assert.Equal(1, monster.EngagedPlayerCount);

        // Same name again does not double-count (engagement map is keyed by name).
        monster.MarkPlayerEngaged("Goober");
        Assert.Equal(1, monster.EngagedPlayerCount);

        monster.MarkPlayerEngaged("OtherPlayer");
        Assert.Equal(2, monster.EngagedPlayerCount);
    }
}
