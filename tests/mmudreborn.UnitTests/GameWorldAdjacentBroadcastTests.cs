using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class GameWorldAdjacentBroadcastTests
{
    [Fact]
    public void BroadcastToAdjacentRooms_includes_outgoing_and_incoming_exit_neighbors()
    {
        var database = new InMemoryGameDatabase();
        database.Rooms[(1, 1)] = CreateRoom(1, 1, ("east", 1, 2));
        database.Rooms[(1, 2)] = CreateRoom(1, 2, ("north", 1, 3));
        database.Rooms[(1, 3)] = CreateRoom(1, 3);
        database.Rooms[(1, 4)] = CreateRoom(1, 4);

        var world = new GameWorld(database, new InMemoryPlayerRepository());

        // Routing now lives in GameWorld (not the host): it reaches each player via Player.Client. Place
        // one recording connection in each room and assert who actually received the line.
        var inRoom1 = AddPlayerInRoom(world, "RoomOne", 1, 1);
        var inRoom2 = AddPlayerInRoom(world, "RoomTwo", 1, 2);
        var inRoom3 = AddPlayerInRoom(world, "RoomThree", 1, 3);
        var inRoom4 = AddPlayerInRoom(world, "RoomFour", 1, 4);

        world.BroadcastToAdjacentRooms(1, 2, "noise");

        Assert.Contains("noise", inRoom1.Lines);   // incoming exit 1 -> 2
        Assert.Contains("noise", inRoom3.Lines);   // outgoing exit 2 -> 3
        Assert.DoesNotContain("noise", inRoom2.Lines); // the source room itself is excluded
        Assert.DoesNotContain("noise", inRoom4.Lines); // disconnected room
    }

    private static RecordingGameClient AddPlayerInRoom(GameWorld world, string name, int mapNumber, int roomNumber)
    {
        var client = new RecordingGameClient();
        var player = new Player
        {
            Name = name,
            CurrentMapNumber = mapNumber,
            CurrentRoomNumber = roomNumber,
            Client = client,
        };
        client.Player = player;
        world.AddPlayer(player);
        return client;
    }

    private static Room CreateRoom(int mapNumber, int roomNumber, params (string Direction, int TargetMap, int TargetRoom)[] exits)
    {
        var room = new Room
        {
            MapNumber = mapNumber,
            RoomNumber = roomNumber,
        };

        foreach (var exit in exits)
        {
            room.SetExit(new RoomExitDefinition
            {
                MapNumber = mapNumber,
                RoomNumber = roomNumber,
                Direction = exit.Direction,
                TargetMap = exit.TargetMap,
                TargetRoom = exit.TargetRoom,
                ExitType = RoomExitType.Normal,
            });
        }

        return room;
    }

    // Minimal IGameClient that records the lines GameWorld routes to it. Send paths are the only ones
    // exercised by broadcast routing; the interactive read/telnet members are unused here.
    private sealed class RecordingGameClient : IGameClient
    {
        public List<string> Lines { get; } = [];

        public bool Connected => true;
        public Player? Player { get; set; }
        public IGameSession? Session { get; set; }
        public string CurrentBbsUserName { get; set; } = "";
        public string CurrentHostedAppId { get; set; } = "";
        public string CurrentHostedWorldId { get; set; } = "";
        public string CurrentHostedLocation { get; set; } = "";
        public bool HasPendingInput => false;
        public bool IsAtLineStart => true;
        public Func<string>? PromptProvider { get; set; }
        public object? AppData { get; set; }

        public Task SendAsync(string text) => Task.CompletedTask;
        public Task SendLineAsync(string text = "")
        {
            Lines.Add(text);
            return Task.CompletedTask;
        }
        public void BeginBuffering() { }
        public void FlushOutput() { }
        public bool TryDeferBroadcastLine(string text, bool reprompt, bool prependLineBreak = true) => false;
        public Task FlushDeferredBroadcastLinesAsync() => Task.CompletedTask;
        public Task EnsureNewLineAsync() => Task.CompletedTask;
        public Task PrepareForBroadcastAsync(bool forceClearCurrentLine = false) => Task.CompletedTask;
        public Task ClearCurrentLineAsync() => Task.CompletedTask;
        public Task RedrawPromptWithInputAsync(string prompt) => Task.CompletedTask;
        public Task DiscardBufferedLineEndingsAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ReadLineAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ReadLineEchoAsync(bool echo = true, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ReadLineEchoAsync(int timeoutMs, bool echo = true, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ReadLineMaskedAsync(char mask = '*', CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ReadKeyAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> ReadKeyAsync(int timeoutMs, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public void SetCommandHistoryEnabled(bool enabled) { }
        public void SetEcho(bool enabled) { }
        public void Disconnect() { }
    }
}
