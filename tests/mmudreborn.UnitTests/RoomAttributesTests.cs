using mmudreborn.Data.Models;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class RoomAttributesTests
{
    [Fact]
    public void Room_attribute_helpers_follow_the_documented_bitmask_layout()
    {
        var room = new Room
        {
            Attributes = (int)(
                RoomAttributes.Protected |
                RoomAttributes.Patrol |
                RoomAttributes.Ownable |
                RoomAttributes.Bit4 |
                RoomAttributes.Clear |
                RoomAttributes.Bit6 |
                RoomAttributes.GangHouse |
                RoomAttributes.Bit8)
        };

        Assert.Equal(
            RoomAttributes.Protected |
            RoomAttributes.Patrol |
            RoomAttributes.Ownable |
            RoomAttributes.Bit4 |
            RoomAttributes.Clear |
            RoomAttributes.Bit6 |
            RoomAttributes.GangHouse |
            RoomAttributes.Bit8,
            room.AttributeFlags);

        Assert.True(room.IsProtected);
        Assert.True(room.IsPatrollable);
        Assert.True(room.IsOwnable);
        Assert.True(room.IsClearAtCleanup);
        Assert.True(room.IsGangHouse);
        Assert.True(room.HasBit4);
        Assert.True(room.HasBit6);
        Assert.True(room.HasBit8);
    }
}
