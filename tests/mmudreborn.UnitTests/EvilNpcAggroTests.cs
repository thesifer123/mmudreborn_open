using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Align-6 "Evil NPC" aggression (e.g. duergar warrior #455 / captain #506 / lord #456, all Align 6).
// The proactive aggression scan: an align-6 monster excludes a player
// from its aggro ONLY when EvilPoints > 39. So it attacks Saint/Good/Neutral/Seedy (EP < 40)
// and spares Outlaw and worse (the "evil" side). The old gate stopped at Neutral, so a Seedy player was
// never aggroed while standing in the room yet still drew a departing free swing (cap 80) — the reported
// "duergar only attack when you leave".
public sealed class EvilNpcAggroTests
{
    private static readonly Monster Duergar = new() { Align = 6, Group = 24, FollowPercent = 90 };
    private static Player WithEvil(int ep) => new() { EvilPoints = ep };

    [Theory]
    [InlineData(-250)] // Saint
    [InlineData(-50)]  // Good
    [InlineData(0)]    // Neutral
    [InlineData(30)]   // Seedy (low)
    [InlineData(39)]   // Seedy (top of band — last attacked value)
    public void Evil_npc_attacks_good_neutral_and_seedy(int ep)
        => Assert.True(CombatEngine.ShouldMonsterAggro(Duergar, WithEvil(ep)));

    [Theory]
    [InlineData(40)]   // Outlaw (just over the cut)
    [InlineData(60)]   // Outlaw
    [InlineData(100)]  // Criminal
    [InlineData(250)]  // Villain
    public void Evil_npc_spares_outlaw_and_worse(int ep)
        => Assert.False(CombatEngine.ShouldMonsterAggro(Duergar, WithEvil(ep)));

    // Bug #240. The departing free swing has its OWN cut, EP < 80: stock skips an evil NPC's swing only
    // at EP 80 and up. So an Outlaw (40-79) is never
    // attacked on sight yet is still swung at on the way OUT, and only Criminal and worse (80+) are
    // left alone entirely. Two different cuts, both stock; neither is a bug.
    [Theory]
    [InlineData(-250, true)] // Saint
    [InlineData(39, true)]   // Seedy
    [InlineData(40, true)]   // Outlaw: spared on sight, still swung at leaving
    [InlineData(70, true)]   // Outlaw
    [InlineData(79, true)]   // last Outlaw value
    [InlineData(80, false)]  // Criminal: never swung at
    [InlineData(250, false)] // Villain
    public void Evil_npc_departing_swing_cut_is_EP_80(int ep, bool swings)
        => Assert.Equal(swings, CombatEngine.IsEligibleDepartingFreeAttacker(
            monsterAlign: 6, monsterAggression: 90, lockedOnPlayer: false, playerEvilPoints: ep, roll: 0, monsterGroup: 24));

    // The spread pick's fellow-evil exception is "fighting it RIGHT NOW" (the player's autocombat
    // target is this monster and they are in autocombat) — not "has ever hit it".
    // The engaged set never clears, so keying on it let a duergar an Outlaw once hit keep picking them
    // for the rest of its life.
    [Fact]
    public void Evil_npc_spread_skips_an_outlaw_who_hit_it_once_but_is_not_fighting_it_now()
    {
        var duergar = MonsterInstance.Create(Duergar, 6, 2648);
        var outlaw = new Player { Name = "Rook", EvilPoints = 70 };
        duergar.MarkPlayerEngaged(outlaw.Name);

        Assert.False(GameWorld.IsEligibleSpreadTarget(duergar, outlaw));
    }

    [Fact]
    public void Evil_npc_spread_may_pick_an_outlaw_who_is_fighting_it_now()
    {
        var duergar = MonsterInstance.Create(Duergar, 6, 2648);
        var outlaw = new Player { Name = "Rook", EvilPoints = 70, InCombat = true, CombatTarget = duergar };

        Assert.True(GameWorld.IsEligibleSpreadTarget(duergar, outlaw));
    }
}
