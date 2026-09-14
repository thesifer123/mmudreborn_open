using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The death drop passes each item along with its DropUses entry —
// a dropped item lands with the monster-specified uses/charges, overriding the item's default UseCount.
// Previously the loader dropped DropUses on the floor, so every dropped charged item used its default.
// (obsidian statue #347 drops gate key #806 with 3 uses; the key's own default is 1.)
public sealed class MonsterDropUsesTests
{
    private const int Map = 1;
    private const int Room = 10;
    private const int MobId = 999;
    private const int KeyItemId = 806;

    private static (GameWorld World, MonsterInstance Mob) BuildAndSpawn(int dropUses, int itemDefaultUses)
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(Map, Room)] = new Room { MapNumber = Map, RoomNumber = Room };
        db.Items[KeyItemId] = new Item { Number = KeyItemId, Name = "gate key", Gettable = true, UseCount = itemDefaultUses };
        db.Monsters[MobId] = new Monster
        {
            Number = MobId, Name = "obsidian statue", HP = 10,
            Drops = [new MonsterDrop { ItemId = KeyItemId, Percent = 100, Uses = dropUses }],
        };
        var world = new GameWorld(db, new InMemoryPlayerRepository());
        Assert.True(world.TrySpawnMonsterInRoom(Map, Room, MobId, ignoreRoomRestrictions: true, out var mob, out _));
        return (world, mob!);
    }

    // Reproduce the production death-drop: CreateMonsterDeathResult surfaces the (spawn-rolled) carried
    // drops, then each drops with the template's DropUses — exactly what HandleMonsterDeath /
    // KillMonsterFromUpkeep do.
    private static long DropCarriedTreasureAndReturnKeyInstance(GameWorld world, MonsterInstance mob)
    {
        var death = CombatEngine.CreateMonsterDeathResult(mob);
        foreach (var dropId in death.Drops)
            world.DropItemInRoom(Map, Room, dropId, uses: mob.Template.GetDropUses(dropId));

        var ground = world.GetVisibleGroundItems(Map, Room);
        var keyEntry = ground.Find(entry => entry.ItemId == KeyItemId);
        Assert.Equal(KeyItemId, keyEntry.ItemId);
        var (pickedId, instanceId) = world.PickUpGroundItemWithInstance(Map, Room, keyEntry.Index);
        Assert.Equal(KeyItemId, pickedId);
        return instanceId;
    }

    [Fact]
    public void Dropped_item_carries_the_monster_specified_uses()
    {
        var (world, mob) = BuildAndSpawn(dropUses: 3, itemDefaultUses: 1);
        Assert.Contains(KeyItemId, mob.CarriedDropItemIds); // 100% roll landed at spawn

        long instanceId = DropCarriedTreasureAndReturnKeyInstance(world, mob);

        Assert.True(world.TryGetItemRuntimeState(instanceId, out var state));
        Assert.Equal(3, state.RemainingCharges);   // overrides the item's default UseCount of 1
    }

    [Fact]
    public void Drop_uses_zero_leaves_charges_at_item_default()
    {
        var (world, mob) = BuildAndSpawn(dropUses: 0, itemDefaultUses: 4);

        long instanceId = DropCarriedTreasureAndReturnKeyInstance(world, mob);

        // DropUses 0 = the stock default path: RemainingCharges stays null, so the use/display paths
        // resolve it to the item's own UseCount (4) rather than a seeded override.
        if (world.TryGetItemRuntimeState(instanceId, out var state))
            Assert.Null(state.RemainingCharges);
    }

    [Fact]
    public void GetDropUses_returns_the_slots_uses_or_zero_when_absent()
    {
        var (_, mob) = BuildAndSpawn(dropUses: 7, itemDefaultUses: 1);
        Assert.Equal(7, mob.Template.GetDropUses(KeyItemId));
        Assert.Equal(0, mob.Template.GetDropUses(4242)); // not a drop of this monster
    }
}
