using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #212: "when Blue and Blueberry are in the same room, targeting (spells, drag, etc) selects the
// wrong person." FindPlayerInRoom took the FIRST boolean prefix match, so "aid blue" could land on
// Blueberry. These run against a deliberately ADVERSE candidate order — the longer name first — because
// the real room enumeration comes out of a ConcurrentDictionary and an end-to-end test of this passes or
// fails by luck.
//
// In the player branch of target resolution the terminal scan accepts a
// candidate on a loose match, then tests full-name equality — and RETURNS that
// player immediately, only falling through to the ambiguity list for a merely-loose hit.
public sealed class TargetNameSelectionTests
{
    private static Player[] Room(params string[] names)
        => [.. names.Select(n => new Player { Name = n })];

    [Fact]
    public void Exact_match_wins_even_when_a_longer_prefix_match_is_enumerated_first()
    {
        var candidates = Room("Blueberry", "Blue");

        var picked = GameWorld.SelectBestNameMatch(candidates, "blue");

        Assert.NotNull(picked);
        Assert.Equal("Blue", picked!.Name);
    }

    [Fact]
    public void Exact_match_wins_regardless_of_case()
    {
        var candidates = Room("Blueberry", "Blue");

        Assert.Equal("Blue", GameWorld.SelectBestNameMatch(candidates, "BLUE")!.Name);
        Assert.Equal("Blue", GameWorld.SelectBestNameMatch(candidates, "bLuE")!.Name);
    }

    [Fact]
    public void The_longer_name_is_still_reachable_by_its_own_exact_spelling()
    {
        var candidates = Room("Blue", "Blueberry");

        Assert.Equal("Blueberry", GameWorld.SelectBestNameMatch(candidates, "blueberry")!.Name);
    }

    [Fact]
    public void A_prefix_matching_only_one_player_still_resolves_loosely()
    {
        var candidates = Room("Blue", "Blueberry");

        Assert.Equal("Blueberry", GameWorld.SelectBestNameMatch(candidates, "blueb")!.Name);
    }

    [Fact]
    public void With_no_exact_match_the_first_loose_candidate_wins_as_before()
    {
        // Stock draws only a two-way exact/loose distinction, so among loose-only candidates we
        // keep the previous first-match behaviour rather than inventing a finer ranking.
        var candidates = Room("Blueberry", "Bluebell");

        Assert.Equal("Blueberry", GameWorld.SelectBestNameMatch(candidates, "blue")!.Name);
    }

    [Fact]
    public void No_match_returns_null()
    {
        Assert.Null(GameWorld.SelectBestNameMatch(Room("Blue", "Blueberry"), "crath"));
        Assert.Null(GameWorld.SelectBestNameMatch(Room(), "blue"));
    }
}
