using System.Threading.Tasks;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #139: searching while resting/meditating didn't stand you up — you could search without re-resting.
// SEARCH clears the rest and meditate flags at the top,
// before any search work and for any search type. These pin that.
public sealed class SearchBreaksRestTests
{
    [Theory]
    [InlineData("search")]      // room search
    [InlineData("search n")]    // directional search
    public async Task Searching_breaks_rest_and_meditate(string command)
    {
        var world = new GameWorld(new InMemoryGameDatabase(), new InMemoryPlayerRepository());
        var client = new RecordingGameClient();
        var player = new Player { Name = "Tester", CurrentMapNumber = 1, CurrentRoomNumber = 1, Client = client, IsResting = true, IsMeditating = true };
        client.Player = player;
        var parser = new CommandParser(client, world, player);

        await parser.ProcessCommand(command);

        Assert.False(player.IsResting);
        Assert.False(player.IsMeditating);
    }
}
