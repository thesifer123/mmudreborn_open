using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A speech-triggered remote action (e.g. "pull lever") broadcasts its message to the room. The
// message convention is Line1 = first-person to the actor ("You pull the large iron lever.") and
// Line2 = third-person to the room ("%s pulls the large iron lever.", %s = the actor's name). The
// %s in the room line must be substituted with the actor's name; it was leaking literally to other
// players ("%s pulls the large iron lever.") — see the Silver Mine Tunnel lever bug report.
public sealed class RemoteActionSpeechMessageTests
{
    private const int Map = 6;
    private const int Room = 3289;
    private const int LeverActionMessageId = 505;   // Line1/Line2 speech (Para3)
    private const int LeverCommandMessageId = 209;   // command phrases (Para1)

    private static GameWorld CreateWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Messages[LeverActionMessageId] = new RoomMessage
        {
            Number = LeverActionMessageId,
            Line1 = "You pull the large iron lever.",
            Line2 = "%s pulls the large iron lever.",
        };
        db.Messages[LeverCommandMessageId] = new RoomMessage
        {
            Number = LeverCommandMessageId,
            Line1 = "pull lever",
        };

        var room = new Room { MapNumber = Map, RoomNumber = Room, Name = "Silver Mine Tunnel, Dead End" };
        room.SetExit(new RoomExitDefinition
        {
            Direction = "west",
            DirectionIndex = 3,
            ExitType = RoomExitType.RemoteAction,
            Para1 = LeverCommandMessageId,   // "pull lever" matches here
            Para2 = 0,                       // target "north" — no such exit, so the action just narrates
            Para3 = LeverActionMessageId,    // the speech message
            TargetMap = Map,
            TargetRoom = Room,
        });
        db.Rooms[(Map, Room)] = room;

        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void Remote_action_room_speech_substitutes_actor_name_for_percent_s()
    {
        var world = CreateWorld();
        var room = world.GetRoom(Map, Room)!;
        var player = new Player { Name = "Tiny" };

        var result = world.TryTriggerRemoteAction(player, room, "pull lever");

        Assert.NotNull(result);
        Assert.Equal("Tiny pulls the large iron lever.", result!.RoomSpeechMessage);
        Assert.DoesNotContain("%s", result.RoomSpeechMessage);
        // The first-person actor line has no %s and is unchanged.
        Assert.Equal("You pull the large iron lever.", result.PlayerSpeechMessage);
    }
}
