using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #172: a resting player dropped by an AoE ("hand of death") was left "still resting" while
// unconscious. Stock clears the rest AND meditate flags the instant a monster attacks
// — BreakRestAndMeditate is that primitive, wired
// into the monster melee + spell paths and used as a fast-tick safety net for any other damage source.
public sealed class RestBreakOnDamageTests
{
    [Fact]
    public void Break_clears_both_flags_and_their_regen_counters()
    {
        var player = new Player
        {
            IsResting = true,
            IsMeditating = true,
            RestRegenTicks = 7,
            MeditateRegenTicks = 4,
        };

        Assert.True(player.BreakRestAndMeditate());

        Assert.False(player.IsResting);
        Assert.False(player.IsMeditating);
        Assert.Equal(0, player.RestRegenTicks);
        Assert.Equal(0, player.MeditateRegenTicks);
    }

    [Fact]
    public void Break_reports_whether_anything_was_active()
    {
        Assert.True(new Player { IsResting = true }.BreakRestAndMeditate());
        Assert.True(new Player { IsMeditating = true }.BreakRestAndMeditate());
        Assert.False(new Player().BreakRestAndMeditate());
    }
}
