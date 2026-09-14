using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// The room death line is the DeathMsg message's Line3, printed ONLY when
// DeathMsg is non-zero AND resolves to a record. There is NO generic fallback — a monster with no
// DeathMsg, or one pointing at a missing/blank message (stock blank sentinels 1/66/121, same as item
// DestructMsg), dies with NO death line. GetDeathMessage must therefore return the stock line or empty.
public sealed class CombatEngineDeathMessageTests
{
    private static MonsterInstance MonsterWithDeathMessage(string? deathMessage) => new()
    {
        Template = new Monster { Number = 1, Name = "giant rat", DeathMessage = deathMessage },
        DisplayName = "thin giant rat",
    };

    [Fact]
    public void Returns_stock_death_line_when_present()
    {
        var monster = MonsterWithDeathMessage("The giant rat falls to the ground with a tortured squeak.");

        Assert.Equal(
            "The giant rat falls to the ground with a tortured squeak.",
            CombatEngine.GetDeathMessage(monster));
    }

    [Fact]
    public void Returns_empty_when_no_stock_death_line_so_the_monster_dies_silently()
    {
        // LinkMonsterDeathMessages leaves DeathMessage empty for DeathMsg==0 / missing / blank-Line3.
        Assert.Equal(string.Empty, CombatEngine.GetDeathMessage(MonsterWithDeathMessage(null)));
        Assert.Equal(string.Empty, CombatEngine.GetDeathMessage(MonsterWithDeathMessage("")));
    }

    [Fact]
    public void Never_invents_a_generic_english_death_line()
    {
        // Regression guard for the removed fabricated fallback ("The X crumbles to the ground, dead." etc.)
        // that stock never shows for the ~94 monsters whose DeathMsg is 0/missing/blank.
        string result = CombatEngine.GetDeathMessage(MonsterWithDeathMessage(null));

        Assert.DoesNotContain("crumbles", result);
        Assert.DoesNotContain("collapses", result);
        Assert.DoesNotContain("final gasp", result);
        Assert.DoesNotContain("lifeless", result);
    }
}
