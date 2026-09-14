using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Going down mortally wounded must not clear the monsters standing over you — they keep swinging, and
/// that is how a downed player dies. The combat-path drop always did this by hand, but the FOUR
/// world-tick drop paths (poison tick, unconscious bleed, lifeforce drain, room-spell upkeep) called
/// ClearCombatState, which empties _incomingMonsterAttackers AND resets NextMonsterAttackAtUtc. That
/// drops the player out of GatherMonstersWithCandidates (its gate needs IsCombatBeatDue), so nothing
/// ever swung at them again.
/// </summary>
public class DropKeepsMonsterAttackersTests
{
    private static MonsterInstance Rat() => new()
    {
        Template = new Monster { Number = 1, Name = "giant rat" },
        DisplayName = "giant rat",
        CurrentHP = 10,
        MaxHP = 10,
    };

    private static Player DownedPlayerWithAttacker(MonsterInstance monster)
    {
        var player = new Player { Name = "Downed", MaxHP = 50, CurrentHP = -3 };
        player.AddIncomingMonsterAttacker(monster);
        player.InCombat = true;
        player.CombatTarget = monster;
        player.NextMonsterAttackAtUtc = DateTime.UtcNow.AddSeconds(-1);
        return player;
    }

    [Fact]
    public void Dropping_keeps_the_attackers_and_the_beat_schedule()
    {
        var monster = Rat();
        var player = DownedPlayerWithAttacker(monster);

        player.DropMortallyWoundedKeepingAttackers();

        // Your own fight is over...
        Assert.False(player.InCombat);
        Assert.Null(player.CombatTarget);
        Assert.Null(player.PlayerCombatTarget);

        // ...but theirs is not. Both of these are what the candidate gate needs.
        Assert.Equal(1, player.IncomingMonsterAttackerCount);
        Assert.NotEqual(DateTime.MinValue, player.NextMonsterAttackAtUtc);
    }

    [Fact]
    public void ClearCombatState_still_wipes_everything_for_flee_and_respawn()
    {
        // The flee and post-death respawn paths legitimately want the full clear — this pins the
        // difference between the two so the drop paths cannot quietly be pointed back at it.
        var monster = Rat();
        var player = DownedPlayerWithAttacker(monster);

        player.ClearCombatState();

        Assert.Equal(0, player.IncomingMonsterAttackerCount);
        Assert.Equal(DateTime.MinValue, player.NextMonsterAttackAtUtc);
    }
}
