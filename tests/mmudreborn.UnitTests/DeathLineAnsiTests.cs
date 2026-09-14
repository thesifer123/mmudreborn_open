using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Byte-exact death-line colours, taken from stock rather than eyeballed.
///   "%s drops to the ground!":
///       prefix = ESC[79D ESC[K + ESC[0;37m + ESC[41m
///       then the text, a BEL, and ESC[40m
///   "You have been killed!"    emit @13126:
///       prefix = ESC[79D ESC[K + ESC[1;31;40m   (bright RED on black)
/// </summary>
public class DeathLineAnsiTests
{
    private const string Preamble = "\x1b[79D\x1b[K";

    [Fact]
    public void Drops_to_the_ground_is_normal_white_on_red_with_a_bell()
    {
        string line = GameAnsi.DropsToTheGround("Suijin drops to the ground!");

        Assert.StartsWith($"{Preamble}\x1b[0;37m\x1b[41m", line, StringComparison.Ordinal);
        Assert.EndsWith("\x07\x1b[40m", line, StringComparison.Ordinal);
        Assert.Contains("Suijin drops to the ground!", line, StringComparison.Ordinal);

        // The regression: ESC[1;37m read visibly brighter than a stock capture side by side.
        Assert.DoesNotContain("\x1b[1;37m", line, StringComparison.Ordinal);
        // Stock closes with ESC[40m (background only), never a full reset.
        Assert.DoesNotContain("\x1b[0m", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Killed_outright_is_bright_red_not_white_on_red()
    {
        string line = GameAnsi.KilledOutright("You have been killed!");

        Assert.StartsWith($"{Preamble}\x1b[1;31m", line, StringComparison.Ordinal);
        Assert.Contains("You have been killed!", line, StringComparison.Ordinal);

        // We used to paint this bright white on a red background — a different colour, not a shade.
        Assert.DoesNotContain("\x1b[1;37m", line, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[41m", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_death_lines_are_not_the_same_colour()
    {
        // Stock deliberately distinguishes them: knocked down = white on red, killed = red on black.
        Assert.NotEqual(
            GameAnsi.DropsToTheGround("x"),
            GameAnsi.KilledOutright("x"));
    }
}

/// <summary>
/// "%s is dead." rides ESC[1;31;40m with NO line preamble of its own —
/// the same bright red as "You have been killed!", because the pair is one announcement sent in two
/// directions (to the victim, and to everyone else).
/// </summary>
public class IsDeadAnsiTests
{
    [Fact]
    public void Is_dead_is_the_same_red_as_you_have_been_killed()
    {
        string roomLine = GameAnsi.IsDead("Raijin is dead.");
        string victimLine = GameAnsi.KilledOutright("You have been killed!");

        Assert.Contains("\x1b[1;31m", roomLine, StringComparison.Ordinal);
        Assert.Contains("\x1b[1;31m", victimLine, StringComparison.Ordinal);

        // Room half carries no ESC[79D ESC[K of its own; the victim half does.
        Assert.DoesNotContain("\x1b[79D", roomLine, StringComparison.Ordinal);
        Assert.Contains("\x1b[79D", victimLine, StringComparison.Ordinal);

        // Neither is white-on-red — that was the drop line's colour, a different state.
        Assert.DoesNotContain("\x1b[41m", roomLine, StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b[1;37m", roomLine, StringComparison.Ordinal);
    }
}
