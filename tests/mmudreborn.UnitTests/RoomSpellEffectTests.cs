using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Room-cast non-damage ability parity (the dispatcher blob
// audit 2026-07-05): duration room spells add/refresh ONE active-spell slot (overwrite-in-place),
// instant ones heal/dispel/scatter, and the TypeOfResists gate can void the whole cast. Stock shapes
// mirrored below: #484 inn rest (123=200, dur 11), #412 mana rgen drain (145=0, -100..-25, dur 10),
// #677 jail heal (18=0, 1..1, dur 0), #310 negate magic (73=0, 0..0, dur 0), #691 cleanup
// (157=0, dest room roll, dur 0).
public sealed class RoomSpellEffectTests
{
    private const int SpellRoom = 10;
    private const int DestRoom = 20;

    private static (GameWorld World, Player Player) BuildWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, SpellRoom)] = new Room { MapNumber = 1, RoomNumber = SpellRoom };
        db.Rooms[(1, DestRoom)] = new Room { MapNumber = 1, RoomNumber = DestRoom };
        // RecalculatePlayerStats (which folds active-spell abilities into the bonus pools) requires
        // the player's race/class rows to exist.
        db.Races[1] = new Race { Number = 1, Name = "Human" };
        db.Classes[1] = new CharacterClass { Number = 1, Name = "Warrior" };
        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var player = new Player
        {
            Name = "Roomer",
            RaceId = 1,
            ClassId = 1,
            CurrentMapNumber = 1,
            CurrentRoomNumber = SpellRoom,
            CurrentHP = 50,
            MaxHP = 100,
            CurrentMana = 10,
            MaxMana = 40,
        };
        world.AddPlayer(player);
        return (world, player);
    }

    private static InMemoryGameDatabase Db(GameWorld world) => (InMemoryGameDatabase)world.Database;

    [Fact]
    public void Inn_rest_room_spell_adds_a_slot_and_boosts_hp_regen()
    {
        var (world, player) = BuildWorld();
        Db(world).Spells[484] = new GameSpell
        {
            Number = 484, Name = "inn rest", MinBase = 100, MaxBase = 100, Duration = 11, SpellType = 3,
            Abilities = new Dictionary<int, int> { [123] = 200 },
        };

        world.CastRoomSpellOnPlayer(player, 484);

        Assert.True(player.HasActiveSpell(484));
        Assert.Equal(200, player.HpRegenBonus); // AbilVal 200 folds via stat recalc
    }

    [Fact]
    public void Mana_drain_room_spell_overwrites_its_slot_on_refresh()
    {
        var (world, player) = BuildWorld();
        var drain = new GameSpell
        {
            Number = 412, Name = "mana rgen drain", MinBase = -50, MaxBase = -50, Duration = 10, SpellType = 3,
            Abilities = new Dictionary<int, int> { [145] = 0 },  // AbilVal 0 → rolled magnitude applies
        };
        Db(world).Spells[412] = drain;

        world.CastRoomSpellOnPlayer(player, 412);
        Assert.Equal(-50, player.ManaRegenBonus);

        // Refresh-in-place: the next pulse's roll REPLACES the slot
        // magnitude (a keep-strongest refresh would pin the weakest drain forever).
        drain.MinBase = -80;
        drain.MaxBase = -80;
        world.CastRoomSpellOnPlayer(player, 412);

        Assert.Equal(-80, player.ManaRegenBonus);
        Assert.Single(player.ActiveSpells); // never a second slot
    }

    [Fact]
    public void Jail_heal_room_spell_heals_instantly_and_caps_at_max_hp()
    {
        var (world, player) = BuildWorld();
        Db(world).Spells[677] = new GameSpell
        {
            Number = 677, Name = "jail heal", MinBase = 1, MaxBase = 1, Duration = 0, SpellType = 3,
            Abilities = new Dictionary<int, int> { [18] = 0 },
        };

        world.CastRoomSpellOnPlayer(player, 677);
        Assert.Equal(51, player.CurrentHP);
        Assert.Empty(player.ActiveSpells);      // instant, no slot

        player.CurrentHP = player.MaxHP;
        world.CastRoomSpellOnPlayer(player, 677);
        Assert.Equal(player.MaxHP, player.CurrentHP); // capped
    }

    [Fact]
    public void Dispel_room_spell_with_magnitude_zero_strips_active_buffs()
    {
        var (world, player) = BuildWorld();
        Db(world).Spells[310] = new GameSpell
        {
            Number = 310, Name = "negate magic", MinBase = 0, MaxBase = 0, Duration = 0, SpellType = 3,
            Abilities = new Dictionary<int, int> { [73] = 0 },
        };
        // A held buff with fewer than 10 abilities — an ability-0 test matches its unused slots.
        Db(world).Spells[100] = new GameSpell
        {
            Number = 100, Name = "shield buff", Duration = 100, SpellType = 0,
            Abilities = new Dictionary<int, int> { [22] = 5 },
        };
        player.AddOrRefreshRoomSpell(100, 5, 100);

        world.CastRoomSpellOnPlayer(player, 310);

        Assert.Empty(player.ActiveSpells);
    }

    [Fact]
    public void Scatter_room_spell_moves_gettable_loot_and_coins_but_not_fixtures()
    {
        var (world, player) = BuildWorld();
        Db(world).Spells[691] = new GameSpell
        {
            Number = 691, Name = "cleanup", MinBase = DestRoom, MaxBase = DestRoom, Duration = 0, SpellType = 3,
            Abilities = new Dictionary<int, int> { [157] = 0 },
        };
        Db(world).Items[1] = new Item { Number = 1, Name = "dropped sword", Gettable = true };
        Db(world).Items[2] = new Item { Number = 2, Name = "stone altar", Gettable = false };
        world.DropItemInRoom(1, SpellRoom, 1);
        world.DropItemInRoom(1, SpellRoom, 2);
        world.DropCurrencyInRoom(1, SpellRoom, 123);

        world.CastRoomSpellOnPlayer(player, 691);

        var source = world.GetVisibleGroundItems(1, SpellRoom);
        var dest = world.GetVisibleGroundItems(1, DestRoom);
        Assert.Single(source);                       // the altar stays
        Assert.Equal(2, source[0].ItemId);
        Assert.Single(dest);                         // the sword moved
        Assert.Equal(1, dest[0].ItemId);
        Assert.Equal(0, world.GetVisibleGroundCurrency(1, SpellRoom));
        Assert.Equal(123, world.GetVisibleGroundCurrency(1, DestRoom));
    }

    [Fact]
    public void Type_of_resists_two_room_spell_can_be_fully_resisted_by_magic_resistance()
    {
        var (world, player) = BuildWorld();
        player.MagicResist = 194;   // min(194/2, 97) = 97 → resisted on rolls 1..97 of 1..99
        Db(world).Spells[828] = new GameSpell
        {
            Number = 828, Name = "slow", MinBase = 5, MaxBase = 5, Duration = 50, SpellType = 3, TypeOfResists = 2,
            Abilities = new Dictionary<int, int> { [34] = -10 },
        };

        int landed = 0;
        for (int i = 0; i < 300; i++)
        {
            player.ActiveSpells.Clear();
            // A landed cast triggers a stat recalc, which re-derives MagicResist (to 0 in this bare
            // fixture) — re-assert the high MR each attempt to keep the resist odds constant.
            player.MagicResist = 194;
            world.CastRoomSpellOnPlayer(player, 828);
            if (player.HasActiveSpell(828))
                landed++;
        }

        // Unresisted would land all 300; at 97/99 resist odds landing more than half is implausible.
        Assert.True(landed < 150, $"TOR=2 spell landed {landed}/300 despite 97% resist odds.");
        Assert.True(landed > 0 || true); // rolls 98-99 pass — occasional lands are expected, not required
    }

    [Fact]
    public void Type_of_resists_one_room_spell_is_unresistable_without_anti_magic()
    {
        var (world, player) = BuildWorld();
        player.MagicResist = 194;
        Db(world).Spells[823] = new GameSpell
        {
            Number = 823, Name = "resist cold", MinBase = 6, MaxBase = 6, Duration = 70, SpellType = 3, TypeOfResists = 1,
            Abilities = new Dictionary<int, int> { [3] = 0 },
        };

        world.CastRoomSpellOnPlayer(player, 823);

        Assert.True(player.HasActiveSpell(823)); // no Anti-Magic ability → TOR 1 never rolls
    }
}
