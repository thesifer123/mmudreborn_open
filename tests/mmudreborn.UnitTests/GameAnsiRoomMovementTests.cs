using Xunit;
using mmudreborn.Server;

namespace mmudreborn.UnitTests;

public class GameAnsiRoomMovementTests
{
    [Fact]
    public void RoomMovementDeparture_FormatsCompassDirectionCorrectly()
    {
        var name = "UnitWalker";
        var direction = "north";
        var expected = $"{Ansi.BrightRed}{name}{Ansi.Reset}{Ansi.Green} just left to the {direction}.{Ansi.Reset}";
        var actual = GameAnsi.RoomMovementDeparture(name, direction);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoomMovementDeparture_FormatsVerticalDirectionCorrectly()
    {
        var name = "UnitWalker";
        var expected = $"{Ansi.BrightRed}{name}{Ansi.Reset}{Ansi.Green} just left upwards.{Ansi.Reset}";
        var actual = GameAnsi.RoomMovementDeparture(name, "up");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoomMovementArrival_FormatsCompassDirectionCorrectly()
    {
        var name = "UnitWalker";
        var direction = "east";
        var expected = $"{Ansi.BrightRed}{name}{Ansi.Reset}{Ansi.Green} walks into the room from the {direction}.{Ansi.Reset}";
        var actual = GameAnsi.RoomMovementArrival(name, direction);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RoomMovementArrival_FormatsVerticalDirectionCorrectly()
    {
        var name = "UnitWalker";
        var expected = $"{Ansi.BrightRed}{name}{Ansi.Reset}{Ansi.Green} walks into the room from above.{Ansi.Reset}";
        var actual = GameAnsi.RoomMovementArrival(name, "above");
        Assert.Equal(expected, actual);
    }
}
