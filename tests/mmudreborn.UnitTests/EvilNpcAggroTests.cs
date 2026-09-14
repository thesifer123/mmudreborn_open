using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
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
}
