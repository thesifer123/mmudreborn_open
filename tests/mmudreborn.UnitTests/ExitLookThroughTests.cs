using mmudreborn.Data.Models;
using Xunit;

namespace mmudreborn.UnitTests;

// In the LOOK direction switch the "look <dir>" peek to the room beyond is blocked only for a
// closed Key (case 2) or Door (case 7) ("The door is closed in that direction!"). A Gate (case 11) is NOT
// in the switch, so it falls through to the room description — you see the room beyond a gate whether it is
// open or closed (e.g. peering into a jail cell through its closed gate). Look-opacity is therefore strictly
// narrower than movement-blocking (IsBarrierExit, which includes Gate).
public sealed class ExitLookThroughTests
{
    [Theory]
    [InlineData(RoomExitType.Door, true)]
    [InlineData(RoomExitType.Key, true)]
    [InlineData(RoomExitType.Gate, false)]     // the fix: a closed gate is see-through
    [InlineData(RoomExitType.Normal, false)]
    [InlineData(RoomExitType.Item, false)]
    [InlineData(RoomExitType.Toll, false)]
    public void Only_door_and_key_block_looking_through_when_closed(RoomExitType type, bool blocks)
        => Assert.Equal(blocks, new RoomExitDefinition { ExitType = type }.BlocksLookThroughWhenClosed);

    [Fact]
    public void Gate_is_a_movement_barrier_but_not_a_look_barrier()
    {
        var gate = new RoomExitDefinition { ExitType = RoomExitType.Gate };
        Assert.True(gate.IsBarrierExit);                 // a closed gate still can't be WALKED through
        Assert.False(gate.BlocksLookThroughWhenClosed);  // but it CAN be SEEN through
    }
}
