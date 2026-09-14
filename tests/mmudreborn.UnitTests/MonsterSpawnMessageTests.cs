using System.Text.RegularExpressions;
using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The spawn line: the monster DescTxt flag (the flavour-adjective list)
// selects how a MoveMsg's lone %s is filled. A flavour-named monster (DescTxt != 0) templates its
// (flavoured) name in — "A %s stomps into the area." -> "A fierce dwarven royal guard stomps into the
// area." A plain monster bakes its name into the message and the %s is the source/direction. The bug
// was that a single-%s template always got the source, so flavour-named spawns read "A nowhere stomps
// into the area." Colours (stock-confirmed): name ESC[1;33m (BrightYellow), rest ESC[0;32m (Green).
public sealed class MonsterSpawnMessageTests
{
    private static string Visible(string s) => Regex.Replace(s, "\\[[0-9;]*m", "");

    [Fact]
    public void Flavour_named_monster_templates_its_name_into_a_single_token_spawn_line()
    {
        var guard = new Monster
        {
            Name = "dwarven royal guard",
            MoveMsg = 1462,
            EntranceMessage = "A %s stomps into the area.",   // one %s = the name
            DescTxt = 2003,                                    // has a flavour-adjective list
        };

        string msg = GameWorld.FormatMonsterSpawnMessage(guard, "fierce dwarven royal guard")!;

        Assert.Equal("A fierce dwarven royal guard stomps into the area.", Visible(msg));
        Assert.DoesNotContain("nowhere", Visible(msg));
        // Line starts BrightYellow (name), then switches to Green for the rest (the stock colour split).
        Assert.StartsWith(Ansi.BrightYellow, msg);
        Assert.Contains($"{Ansi.BrightYellow}A fierce dwarven royal guard{Ansi.Green} stomps", msg);
    }

    [Fact]
    public void Plain_monster_uses_the_single_token_for_the_source()
    {
        var barmaid = new Monster
        {
            Name = "barmaid",
            MoveMsg = 1183,
            EntranceMessage = "A barmaid walks in from %s.",   // name baked in, %s = source
            DescTxt = 0,
        };

        string msg = GameWorld.FormatMonsterSpawnMessage(barmaid, "barmaid")!;

        Assert.Equal("A barmaid walks in from nowhere.", Visible(msg));
        // A baked-in-name MoveMsg substitutes no name, so the WHOLE line stays BrightYellow (no Green).
        Assert.StartsWith(Ansi.BrightYellow, msg);
        Assert.DoesNotContain(Ansi.Green, msg);
    }

    [Fact]
    public void Two_token_spawn_line_fills_name_then_source()
    {
        var crawler = new Monster
        {
            Name = "cave crawler",
            MoveMsg = 8273,
            EntranceMessage = "A %s scuttles into the room from %s.",
            DescTxt = 2003,
        };

        string msg = GameWorld.FormatMonsterSpawnMessage(crawler, "giant cave crawler")!;

        Assert.Equal("A giant cave crawler scuttles into the room from nowhere.", Visible(msg));
    }

    [Fact]
    public void No_token_spawn_line_is_left_verbatim()
    {
        var golem = new Monster
        {
            Name = "small golem",
            MoveMsg = 8257,
            EntranceMessage = "A small golem stomps into the room.",
            DescTxt = 0,
        };

        string msg = GameWorld.FormatMonsterSpawnMessage(golem, "small golem")!;

        Assert.Equal("A small golem stomps into the room.", Visible(msg));
    }

    [Fact]
    public void Blank_silence_sentinel_movemsg_spawns_with_no_arrival_line()
    {
        // The death-spell-summoned "dying master assassin" (#745) has MoveMsg = 66, the universal blank
        // silence sentinel. Verified against the raw stock DAT: #66 sits on LIVE Btrieve data-page slots
        // (usage 3 and 6) with empty Line1/Line2, so it is a PRESENT-but-blank record. The record
        // resolves (MoveMsgMissing stays false) and its blank Line1 leaves EntranceMessage null, which
        // stock renders as NO spawn line at all (the silent branch) — not
        // "moves into the room from nowhere". 321 monsters ride this path, cutpurse #260 included.
        var dying = new Monster
        {
            Name = "dying master assassin",
            MoveMsg = 66,
            EntranceMessage = null,
            DescTxt = 0,
        };

        string? msg = GameWorld.FormatMonsterSpawnMessage(dying, "dying master assassin");

        Assert.Null(msg);
    }

    [Fact]
    public void Movemsg_pointing_at_an_absent_record_uses_the_generic_moves_into_the_room_line()
    {
        // The tier stock keeps distinct from the silence sentinel above: the spawn
        // tests the record and its Line1 separately, and then prints the
        // fallback ONLY for `msg == NULL`. So a MoveMsg whose Messages row is genuinely absent still
        // announces; only a present-blank row is silent. Collapsing the two (which we used to do, because
        // both leave EntranceMessage null) made this tier unreachable.
        var orphan = new Monster
        {
            Name = "orphaned mob",
            MoveMsg = 9999,
            EntranceMessage = null,
            MoveMsgMissing = true,
            DescTxt = 0,
        };

        string msg = GameWorld.FormatMonsterSpawnMessage(orphan, "orphaned mob")!;

        Assert.Equal("orphaned mob moves into the room from nowhere.", Visible(msg));
        Assert.StartsWith(Ansi.BrightYellow, msg);
    }

    [Fact]
    public void Zero_movemsg_uses_the_generic_just_arrived_line()
    {
        // MoveMsg == 0 is the stock no-message default → "<name> just arrived from nowhere." (distinct from
        // a blank-sentinel MoveMsg, which is silent).
        var rat = new Monster { Name = "large rat", MoveMsg = 0, EntranceMessage = null, DescTxt = 0 };

        string msg = GameWorld.FormatMonsterSpawnMessage(rat, "large rat")!;

        Assert.Equal("large rat just arrived from nowhere.", Visible(msg));
        Assert.StartsWith(Ansi.BrightYellow, msg);
    }
}
