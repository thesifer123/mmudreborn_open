using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// New-character name content guards, reproducing the stock checks (the ALPHA test, the new-name handler's
// command-verb gate, and the monster-name scan). Length and
// existing-character uniqueness are enforced by the caller, not here.
public sealed class CharacterNameRulesTests
{
    private static readonly string[] Monsters = ["bandit", "barmaid", "giant spider", "duergar warrior"];

    private static string? Check(string name) => CharacterNameRules.FindViolation(name, Monsters);

    [Theory]
    [InlineData("Gandalf")]
    [InlineData("Testuser")]
    [InlineData("Aranthir")]
    public void Ordinary_names_are_allowed(string name)
        => Assert.Null(Check(name));

    [Theory]
    [InlineData("Ab1c")]   // digit
    [InlineData("Bob-2")]  // punctuation
    [InlineData("Va der")] // internal space
    public void Non_alpha_names_are_rejected(string name)
        => Assert.Equal("Only ALPHA characters are allowed in a name!", Check(name));

    [Theory]
    [InlineData("north")]   // movement table
    [InlineData("attack")]  // command registry
    [InlineData("look")]
    [InlineData("rest")]
    public void Command_verbs_are_rejected_as_reserved(string name)
        => Assert.Equal("You may not use that name!", Check(name));

    [Theory]
    // The match is case-insensitive (monster table is lower-case, "Bandit"/"bandit" both hit); the echoed
    // name keeps the player's own casing past the first letter, exactly like the stock stored name.
    [InlineData("Bandit", "You may not use Bandit as your name.")]
    [InlineData("bandit", "You may not use Bandit as your name.")]
    [InlineData("Barmaid", "You may not use Barmaid as your name.")]
    public void Exact_monster_names_are_rejected(string name, string expected)
        => Assert.Equal(expected, Check(name));

    [Theory]
    // The stock check is an EXACT full-name match, so a single word that only appears INSIDE a
    // multi-word monster name (duergar warrior / giant spider) is NOT blocked.
    [InlineData("Duergar")]
    [InlineData("Spider")]
    [InlineData("Warrior")]
    public void A_word_inside_a_multiword_monster_name_is_allowed(string name)
        => Assert.Null(Check(name));

    [Fact]
    public void Empty_name_is_invalid()
        => Assert.Equal("You have entered an invalid name!", Check("   "));
}
