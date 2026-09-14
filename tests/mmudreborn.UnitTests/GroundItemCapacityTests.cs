using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// Per-room ground capacity. The room record holds two
/// fixed-size parallel arrays chosen by the function's hidden flag:
///   visible: 17 slots, each with a charge and stack-count entry
///   hidden:  15 slots, likewise
/// The record layout self-checks: each array ends exactly where the next begins, which is how the
/// 17/15 slot counts were confirmed.
///
/// Stacking is part of the slot ACCOUNTING, not a separate cap: an item whose charge parameter is -1
/// folds onto an existing slot holding the same id (bumping its stack counter), while an item
/// carrying a real charge value never stacks and always claims its own slot. We were unbounded before
/// 2026-08-16 — a room could hold any number of entries.
/// </summary>
public sealed class GroundItemCapacityTests
{
    private const int Map = 1;
    private const int Room = 1;
    private const int SwordId = 100;
    private const int PotionId = 101;

    private static GameWorld NewWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(Map, Room)] = new Room { MapNumber = Map, RoomNumber = Room, Name = "Square", Description = "x" };
        db.Items[SwordId] = new Item { Number = SwordId, Name = "sword" };
        db.Items[PotionId] = new Item { Number = PotionId, Name = "potion" };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void Distinct_items_fill_the_seventeen_visible_slots_and_then_the_room_refuses_more()
    {
        var world = NewWorld();

        // 17 DISTINCT ids, so no stacking can hide the cap.
        for (int i = 0; i < GameWorld.StockVisibleGroundSlots; i++)
        {
            int itemId = 500 + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"trinket {i}" };
            Assert.True(world.DropItemInRoom(Map, Room, itemId), $"slot {i} should still be free");
        }

        world.Database.Items[999] = new Item { Number = 999, Name = "one too many" };
        Assert.False(world.DropItemInRoom(Map, Room, 999), "the 18th distinct item must be refused");
        Assert.Equal(GameWorld.StockVisibleGroundSlots, world.GetVisibleGroundItems(Map, Room).Count);
    }

    [Fact]
    public void Hidden_pile_has_its_own_smaller_fifteen_slot_cap()
    {
        var world = NewWorld();

        for (int i = 0; i < GameWorld.StockHiddenGroundSlots; i++)
        {
            int itemId = 600 + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"stashed {i}" };
            Assert.True(world.HideItemInRoom(Map, Room, itemId), $"hidden slot {i} should still be free");
        }

        world.Database.Items[998] = new Item { Number = 998, Name = "one too many" };
        Assert.False(world.HideItemInRoom(Map, Room, 998), "the 16th hidden item must be refused");

        // The two piles are independent arrays — a full hidden pile does not block the visible one.
        Assert.True(world.DropItemInRoom(Map, Room, SwordId), "visible slots are a separate array");
    }

    [Fact]
    public void Identical_stackable_copies_share_one_slot_so_the_cap_counts_distinct_entries()
    {
        var world = NewWorld();

        // Far more copies than there are slots: all fold onto a single slot, exactly as the stock
        // stack counter does, so none of them is ever refused.
        for (int i = 0; i < 100; i++)
            Assert.True(world.DropItemInRoom(Map, Room, SwordId), $"stackable copy {i} must not consume a slot");

        // One slot is used, so 16 more DISTINCT ids still fit.
        for (int i = 0; i < GameWorld.StockVisibleGroundSlots - 1; i++)
        {
            int itemId = 700 + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"other {i}" };
            Assert.True(world.DropItemInRoom(Map, Room, itemId));
        }

        world.Database.Items[997] = new Item { Number = 997, Name = "one too many" };
        Assert.False(world.DropItemInRoom(Map, Room, 997), "the stacked copies should still occupy exactly one slot");
    }

    [Fact]
    public void A_charge_bearing_copy_never_stacks_and_claims_its_own_slot()
    {
        var world = NewWorld();

        // Stacking requires the charge parameter to be -1. A specific charge value forces a new slot.
        for (int i = 0; i < GameWorld.StockVisibleGroundSlots; i++)
            Assert.True(world.DropItemInRoom(Map, Room, PotionId, instanceId: null, uses: 3), $"charged copy {i}");

        Assert.False(world.DropItemInRoom(Map, Room, PotionId, instanceId: null, uses: 3),
            "charge-bearing copies do not stack, so the 18th must be refused");
    }

    [Fact]
    public void Turning_the_limit_off_restores_unbounded_rooms_but_keeps_stacking()
    {
        var world = NewWorld();
        world.GroundItemLimitEnabled = false;

        for (int i = 0; i < GameWorld.StockVisibleGroundSlots + 25; i++)
        {
            int itemId = 800 + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"junk {i}" };
            Assert.True(world.DropItemInRoom(Map, Room, itemId), "GROUNDLIMIT OFF must not refuse anything");
        }
    }

    // ── Overflow spill ──────────────────────────────────
    // Loot never vanishes just because the room under it is full: stock recurses through the room's
    // exits (skipping types 12 RemoteAction and 8 addon-gate) up to a global depth of 5.

    private static GameWorld NewWorldWithNeighbour()
    {
        var db = new InMemoryGameDatabase();
        var here = new Room { MapNumber = Map, RoomNumber = Room, Name = "Full Room", Description = "x" };
        here.SetExit(new RoomExitDefinition
        {
            MapNumber = Map,
            RoomNumber = Room,
            Direction = "north",
            DirectionIndex = 0,
            TargetMap = Map,
            TargetRoom = 2,
            ExitType = RoomExitType.Normal,
        });
        db.Rooms[(Map, Room)] = here;
        db.Rooms[(Map, 2)] = new Room { MapNumber = Map, RoomNumber = 2, Name = "Next Door", Description = "x" };
        db.Items[SwordId] = new Item { Number = SwordId, Name = "sword" };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void Overflow_spills_into_the_adjacent_room_instead_of_vanishing()
    {
        var world = NewWorldWithNeighbour();

        for (int i = 0; i < GameWorld.StockVisibleGroundSlots; i++)
        {
            int itemId = 900 + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"filler {i}" };
            Assert.True(world.DropItemInRoom(Map, Room, itemId));
        }

        // A plain drop is refused...
        Assert.False(world.DropItemInRoom(Map, Room, SwordId));

        // ...but the corpse-spill path finds room next door rather than destroying the item.
        Assert.True(world.DisposeOfItemInRoom(Map, Room, SwordId));
        Assert.Contains(world.GetVisibleGroundItems(Map, 2), entry => entry.ItemId == SwordId);
        Assert.Equal(GameWorld.StockVisibleGroundSlots, world.GetVisibleGroundItems(Map, Room).Count);
    }

    [Fact]
    public void Spill_refuses_to_travel_through_a_non_walkable_exit()
    {
        var db = new InMemoryGameDatabase();
        var here = new Room { MapNumber = Map, RoomNumber = Room, Name = "Sealed Room", Description = "x" };
        // Exit type 12 (RemoteAction) is one of the two stock skips when spilling.
        here.SetExit(new RoomExitDefinition
        {
            MapNumber = Map,
            RoomNumber = Room,
            Direction = "north",
            DirectionIndex = 0,
            TargetMap = Map,
            TargetRoom = 2,
            ExitType = RoomExitType.RemoteAction,
        });
        db.Rooms[(Map, Room)] = here;
        db.Rooms[(Map, 2)] = new Room { MapNumber = Map, RoomNumber = 2, Name = "Beyond", Description = "x" };
        db.Items[SwordId] = new Item { Number = SwordId, Name = "sword" };
        var world = new GameWorld(db, new InMemoryPlayerRepository());

        for (int i = 0; i < GameWorld.StockVisibleGroundSlots; i++)
        {
            int itemId = 950 + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"filler {i}" };
            Assert.True(world.DropItemInRoom(Map, Room, itemId));
        }

        Assert.False(world.DisposeOfItemInRoom(Map, Room, SwordId),
            "loot must not be pushed through a RemoteAction exit");
        Assert.Empty(world.GetVisibleGroundItems(Map, 2));
    }

    // A corridor of rooms, each already full, to pin how far the spill will travel.
    private static GameWorld NewCorridor(int roomCount)
    {
        var db = new InMemoryGameDatabase();
        for (int r = 1; r <= roomCount; r++)
        {
            var room = new Room { MapNumber = Map, RoomNumber = r, Name = $"Corridor {r}", Description = "x" };
            if (r < roomCount)
            {
                room.SetExit(new RoomExitDefinition
                {
                    MapNumber = Map,
                    RoomNumber = r,
                    Direction = "north",
                    DirectionIndex = 0,
                    TargetMap = Map,
                    TargetRoom = r + 1,
                    ExitType = RoomExitType.Normal,
                });
            }
            db.Rooms[(Map, r)] = room;
        }
        db.Items[SwordId] = new Item { Number = SwordId, Name = "sword" };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static void FillRoom(GameWorld world, int roomNumber, int seed)
    {
        for (int i = 0; i < GameWorld.StockVisibleGroundSlots; i++)
        {
            int itemId = seed + i;
            world.Database.Items[itemId] = new Item { Number = itemId, Name = $"filler {itemId}" };
            Assert.True(world.DropItemInRoom(Map, roomNumber, itemId));
        }
    }

    [Fact]
    public void Spill_walks_past_several_full_rooms_to_find_space()
    {
        // Rooms 1-4 full, room 5 empty: the hoard should travel the whole corridor rather than be lost.
        var world = NewCorridor(5);
        for (int r = 1; r <= 4; r++)
            FillRoom(world, r, 1000 * r);

        Assert.True(world.DisposeOfItemInRoom(Map, 1, SwordId), "the spill must keep walking to open ground");
        Assert.Contains(world.GetVisibleGroundItems(Map, 5), e => e.ItemId == SwordId);
    }

    [Fact]
    public void Spill_gives_up_past_the_stock_depth_of_five()
    {
        // The spill guards on a recursion depth of 5, so the search is bounded. With every
        // room inside that radius full, the item is reported undeliverable rather than silently dropped
        // somewhere impossible -- the caller is what decides what to do next (for a corpse, the trail).
        var world = NewCorridor(9);
        for (int r = 1; r <= 8; r++)
            FillRoom(world, r, 1000 * r);

        Assert.False(world.DisposeOfItemInRoom(Map, 1, SwordId),
            "the bounded search must report failure, not wander the whole map");
        Assert.Empty(world.GetVisibleGroundItems(Map, 9));
    }
}
