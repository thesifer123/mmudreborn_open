using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// BASH closes the already-open / bashed / failed branch and THEN rolls a recoil:
///     if (roll of 0..99 &lt; 25) { dmg = roll of 1..3; player HP -= dmg;
///                                  prf("You take %d damage for bashing the door!") }
/// Both genrdn calls are top-EXCLUSIVE, so that is a 25% chance of 1..3 damage.
/// </summary>
public class BashRecoilDamageTests
{
    [Fact]
    public void Bash_recoil_damage_is_always_one_to_three()
    {
        var rng = new Random(12345);
        int fired = 0;

        for (int i = 0; i < 200_000; i++)
        {
            if (!CommandParser.TryRollBashRecoilDamage(rng, out int damage))
            {
                Assert.Equal(0, damage);
                continue;
            }

            fired++;
            Assert.InRange(damage, 1, 3);
        }

        Assert.True(fired > 0, "Expected the recoil roll to fire at least once.");
    }

    [Fact]
    public void Bash_recoil_fires_on_about_a_quarter_of_bashes()
    {
        // A roll of 0..99 < 25 = exactly 25 of 100 outcomes.
        var rng = new Random(987);
        const int trials = 400_000;
        int fired = 0;

        for (int i = 0; i < trials; i++)
        {
            if (CommandParser.TryRollBashRecoilDamage(rng, out _))
                fired++;
        }

        double rate = (double)fired / trials;
        Assert.InRange(rate, 0.245, 0.255);
    }

    [Fact]
    public void Bash_recoil_damage_covers_the_full_one_to_three_band()
    {
        // Top-exclusive genrdn(1,4) must never yield 4 — the classic off-by-one here.
        var rng = new Random(2024);
        var seen = new HashSet<int>();

        for (int i = 0; i < 200_000; i++)
        {
            if (CommandParser.TryRollBashRecoilDamage(rng, out int damage))
                seen.Add(damage);
        }

        Assert.Equal([1, 2, 3], seen.OrderBy(d => d).ToArray());
    }
}
