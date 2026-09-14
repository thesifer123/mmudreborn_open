using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// 119 and 121 are adjacent, similarly named, and mean opposite things. Confusing them is what made
/// every torch answer "You must recharge that before you may light it again." forever instead of
/// being spent and destroyed: the light code tested 119 ("Delete at cleanup") as though it meant
/// "rechargeable". Pin the semantics so the swap cannot happen again silently.
/// </summary>
public class CleanupAbilityIdTests
{
    private const int DeleteAtCleanup = 119;
    private const int RechargeAtCleanup = 121;

    [Fact]
    public void Delete_and_recharge_at_cleanup_are_distinct_and_not_swapped()
    {
        Assert.Equal("Delete at cleanup(119)", AbilityNames.Format(DeleteAtCleanup));
        Assert.Equal("Recharge at cleanup(121)", AbilityNames.Format(RechargeAtCleanup));
        Assert.NotEqual(DeleteAtCleanup, RechargeAtCleanup);
    }

    [Fact]
    public void Neighbouring_cleanup_abilities_keep_their_own_meanings()
    {
        // The other two "at cleanup" ids in the same cluster, so a future renumbering trips here too.
        Assert.Equal("Delete from inventory at cleanup(149)", AbilityNames.Format(149));
        Assert.Equal("Remain Visible at cleanup(154)", AbilityNames.Format(154));
    }
}
