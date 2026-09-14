using mmudreborn.Data.Models;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The "logical room item" layer. A non-gettable
// visible-placed item (ability 138, e.g. the giant apparatus 819) is drawn from a room's static Placed
// field and has no dynamic ground entry. clearitem must still be able to remove it (so the apparatus
// "poofs"), roomitem must gate on its presence, and the renderer must drop it once removed. See the
// Apparatus quest (room 8/955) bug reports #84/#85.
public sealed class RoomLogicalItemTests
{
    private const int Map = 8;
    private const int Room = 955;
    private const int ApparatusId = 819;   // non-gettable, visible-placed (ability 138)
    private const int GettablePropId = 820;

    private static GameWorld CreateWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Items[ApparatusId] = new Item { Number = ApparatusId, Name = "giant apparatus", Gettable = false, Abilities = { [138] = 0 } };
        db.Items[GettablePropId] = new Item { Number = GettablePropId, Name = "brass key", Gettable = true };
        db.Rooms[(Map, Room)] = new Room
        {
            MapNumber = Map,
            RoomNumber = Room,
            Name = "Arcane Laboratory",
            Placed = $"{ApparatusId},{GettablePropId}",
        };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void RoomHasItem_sees_a_non_gettable_placed_prop()
    {
        var world = CreateWorld();
        Assert.True(world.RoomHasItem(Map, Room, ApparatusId));
        Assert.False(world.IsPlacedItemRemoved(Map, Room, ApparatusId));
    }

    [Fact]
    public void ClearRoomItem_poofs_a_non_gettable_placed_prop()
    {
        var world = CreateWorld();

        Assert.True(world.ClearRoomItem(Map, Room, ApparatusId));   // first destroy succeeds

        Assert.True(world.IsPlacedItemRemoved(Map, Room, ApparatusId));
        Assert.False(world.RoomHasItem(Map, Room, ApparatusId));    // gate now sees it gone
        Assert.False(world.ClearRoomItem(Map, Room, ApparatusId));  // nothing left to clear
    }

    [Fact]
    public void Gettable_placed_prop_presence_follows_the_ground_state_not_the_static_field()
    {
        var world = CreateWorld();

        // Seeded into dynamic ground state and present...
        Assert.True(world.RoomHasItem(Map, Room, GettablePropId));

        // ...then picked up — even though the static Placed field still lists it, it's gone.
        Assert.True(world.RemoveGroundItemById(Map, Room, GettablePropId));
        Assert.False(world.RoomHasItem(Map, Room, GettablePropId));
        // And it was never a "logical removal" — that path is only for non-gettable props.
        Assert.False(world.IsPlacedItemRemoved(Map, Room, GettablePropId));
    }

    [Fact]
    public void Clearing_a_gettable_prop_removes_the_ground_entry_then_reports_nothing_left()
    {
        var world = CreateWorld();

        Assert.True(world.ClearRoomItem(Map, Room, GettablePropId));   // removes the seeded ground entry
        Assert.False(world.RoomHasItem(Map, Room, GettablePropId));
        Assert.False(world.ClearRoomItem(Map, Room, GettablePropId));  // already gone
    }

    [Fact]
    public void RoomHasItem_is_false_for_an_item_not_placed_or_dropped()
    {
        var world = CreateWorld();
        Assert.False(world.RoomHasItem(Map, Room, 99999));
    }

    [Fact]
    public void A_dropped_dynamic_item_is_present_and_clearable()
    {
        var world = CreateWorld();
        world.DropItemInRoom(Map, Room, GettablePropId);

        Assert.True(world.RoomHasItem(Map, Room, GettablePropId));
        Assert.True(world.ClearRoomItem(Map, Room, GettablePropId));
    }

    // The rubbish-disposal lever ("clearitem 0", bug #167): flush destroys everything on the floor —
    // dropped items and seeded gettable placed loot — but leaves static non-gettable scenery in place.
    [Fact]
    public void ClearAllRoomItems_flushes_dropped_and_gettable_placed_but_keeps_scenery()
    {
        var world = CreateWorld();
        world.DropItemInRoom(Map, Room, GettablePropId);          // a dropped copy
        world.DropItemInRoom(Map, Room, GettablePropId);          // and another
        // GettablePropId is also seeded into ground state from the Placed field.

        int removed = world.ClearAllRoomItems(Map, Room);

        Assert.True(removed >= 2);                                // floor rubbish destroyed
        Assert.False(world.RoomHasItem(Map, Room, GettablePropId));
        // The non-gettable apparatus is scenery, not rubbish — it survives the flush.
        Assert.True(world.RoomHasItem(Map, Room, ApparatusId));
        Assert.False(world.IsPlacedItemRemoved(Map, Room, ApparatusId));
    }

    [Fact]
    public void ClearAllRoomItems_on_an_empty_floor_removes_nothing()
    {
        var world = CreateWorld();
        // Drain the seeded gettable prop first, then flush again.
        world.ClearAllRoomItems(Map, Room);
        Assert.Equal(0, world.ClearAllRoomItems(Map, Room));
    }
}
