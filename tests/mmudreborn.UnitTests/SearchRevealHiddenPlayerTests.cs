using System.Linq;
using System.Threading.Tasks;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Spotting a hidden player on SEARCH must SUPPRESS the "Your search revealed nothing." line. The
// room-items display: when nothing is found it reveals hidden players and prints
// "Your search revealed nothing." ONLY if no one was revealed — a spotted player is the better
// outcome. We used to print both lines.
public sealed class SearchRevealHiddenPlayerTests
{
    private static GameWorld NewWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Sovereign Street", Description = "x" };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static Player AddPlayer(GameWorld world, string name)
    {
        var player = new Player { Name = name, CurrentMapNumber = 1, CurrentRoomNumber = 1, Client = new RecordingGameClient() };
        ((RecordingGameClient)player.Client).Player = player;
        world.AddPlayer(player);
        return player;
    }

    [Fact]
    public async Task Spotting_a_hidden_player_suppresses_revealed_nothing()
    {
        var world = NewWorld();
        var searcher = AddPlayer(world, "Searcher");
        searcher.HasSeeHidden = true;                 // deterministic reveal
        var goober = AddPlayer(world, "Goober");
        goober.IsHidden = true;

        var client = (RecordingGameClient)searcher.Client!;
        client.Lines.Clear();
        await new CommandParser(client, world, searcher).ProcessCommand("search");

        Assert.Contains(client.Lines, l => l.Contains("You see Goober hiding in the shadows."));
        Assert.DoesNotContain(client.Lines, l => l.Contains("Your search revealed nothing."));
        Assert.False(goober.IsHidden);               // actually revealed
    }

    [Fact]
    public async Task Empty_search_with_no_one_hidden_still_reports_revealed_nothing()
    {
        var world = NewWorld();
        var searcher = AddPlayer(world, "Searcher");

        var client = (RecordingGameClient)searcher.Client!;
        client.Lines.Clear();
        await new CommandParser(client, world, searcher).ProcessCommand("search");

        Assert.Contains(client.Lines, l => l.Contains("Your search revealed nothing."));
        Assert.DoesNotContain(client.Lines, l => l.Contains("hiding in the shadows"));
    }
}
