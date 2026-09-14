using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #43: traps at Hillside Path/Guard Post (2/1106) triggered correctly but couldn't be
// searched or disarmed. Stock DISARM accepts "disarm trap <direction>" and
// looks up the exit at the given direction index; the directional search has
// a separate branch for trap exits that rolls 0..99 < Traps. These tests pin
// down the two parts that were broken: the disarm exit lookup with the "trap" prefix, and the
// fact that a trap exit isn't filtered out as "not a visible exit".
public sealed class TrapExitTests
{
    [Fact]
    public void TryDisarmExit_uses_direction_after_stripping_trap_prefix()
    {
        var world = CreateWorldWithTrap(damage: 0, traps: 100, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);

        // The HandleDisarm parser strips a leading "trap" token, so the world layer receives
        // just the direction selector. A 100% disarm skill guarantees the success branch.
        bool ok = world.World.TryDisarmExit(player, room, "northwest", out var message, out _);

        Assert.True(ok);
        Assert.Contains("successfully disarmed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDisarmExit_finds_trap_exit_via_short_direction_alias()
    {
        var world = CreateWorldWithTrap(damage: 0, traps: 100, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);

        // "nw" is the short alias the bug reporter used after typing "disarm trap nw".
        bool ok = world.World.TryDisarmExit(player, room, "nw", out var message, out _);

        Assert.True(ok);
        Assert.Contains("northwest", message);
    }

    [Fact]
    public void TryDisarmExit_with_zero_skill_triggers_trap_damage()
    {
        // The stock branches are:
        //   roll < skill            → success
        //   roll < skill + 10       → "failed to disarm" (safe)
        //   else                    → "set off the trap" (damage applied)
        // To deterministically hit the third branch we need every possible roll [0..100] to
        // satisfy `roll >= skill + 10`. With `disarmTraps = -100` we get `skill + 10 = -90`,
        // so any non-negative roll triggers. (The earlier `0` value had a 10% flake — rolls
        // 0–9 took the "failed" branch instead.)
        var world = CreateWorldWithTrap(damage: 36, traps: 0, disarmTraps: -100);
        var (player, room) = (world.Player, world.Room);
        int hpBefore = player.CurrentHP;

        bool ok = world.World.TryDisarmExit(player, room, "northwest", out var message, out _);

        Assert.False(ok);
        Assert.Contains("set off the trap", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(player.CurrentHP < hpBefore, $"expected HP loss but {hpBefore} -> {player.CurrentHP}");
    }

    [Fact]
    public void TryDisarmExit_after_disarmed_reports_a_failed_disarm_not_the_trap_state()
    {
        var world = CreateWorldWithTrap(damage: 0, traps: 100, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);

        Assert.True(world.World.TryDisarmExit(player, room, "northwest", out _, out _));

        // DISARM never leaks whether a trap is there: re-disarming an already-disarmed exit
        // reports the same "failed to disarm any trap" line, not "there is no longer a trap".
        bool ok = world.World.TryDisarmExit(player, room, "northwest", out var message, out _);
        Assert.False(ok);
        Assert.Contains("failed to disarm any trap to the northwest", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no longer a trap", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDisarmExit_reports_failed_disarm_for_a_direction_with_no_trap()
    {
        var world = CreateWorldWithTrap(damage: 0, traps: 100, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);

        // "north" is a valid direction but carries no trap (the trap is northwest). Stock still
        // reports a failed disarm to that direction instead of revealing there is no trap.
        bool ok = world.World.TryDisarmExit(player, room, "north", out var message, out _);
        Assert.False(ok);
        Assert.Contains("failed to disarm any trap to the north", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryDisarmExit_is_silent_for_a_non_direction_selector()
    {
        var world = CreateWorldWithTrap(damage: 0, traps: 100, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);

        // "green" is not one of the ten directions; the parse fails and produces no output.
        bool ok = world.World.TryDisarmExit(player, room, "green", out var message, out _);
        Assert.False(ok);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public void MaybeTriggerTrapOnTraverse_returns_null_after_disarm()
    {
        var world = CreateWorldWithTrap(damage: 36, traps: 100, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);
        var exit = room.GetExit("northwest")!;

        Assert.True(world.World.TryDisarmExit(player, room, "northwest", out _, out _));
        Assert.Null(world.World.MaybeTriggerTrapOnTraverse(player, exit));
    }

    // Spell-trap (type 24, e.g. the blow-dart traps in map 15): DISARM handles it
    // like a type-9 trap (disarm + re-arm), but botching it fires the trap's spell instead of flat
    // damage. The spell cast is the caller's job, so TryDisarmExit just signals the exit back.
    [Fact]
    public void SpellTrap_starts_armed_and_disarm_succeeds_and_disarms_it()
    {
        var world = CreateWorldWithSpellTrap(spellId: 905, disarmTraps: 100);
        var (player, room) = (world.Player, world.Room);
        var exit = room.GetExit("northwest")!;

        Assert.False(world.World.IsTrapCurrentlyDisarmed(exit));   // armed by default

        bool ok = world.World.TryDisarmExit(player, room, "northwest", out var message, out var triggered);

        Assert.True(ok);
        Assert.Null(triggered);
        Assert.Contains("successfully disarmed", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(world.World.IsTrapCurrentlyDisarmed(exit));    // now disarmed
    }

    [Fact]
    public void Botched_spell_trap_disarm_signals_the_trap_to_fire_without_flat_damage()
    {
        var world = CreateWorldWithSpellTrap(spellId: 905, disarmTraps: -100);
        var (player, room) = (world.Player, world.Room);
        int hpBefore = player.CurrentHP;

        bool ok = world.World.TryDisarmExit(player, room, "northwest", out var message, out var triggered);

        Assert.False(ok);
        Assert.NotNull(triggered);                                 // caller casts the spell
        Assert.Equal(RoomExitType.SpellTrap, triggered!.ExitType);
        Assert.Contains("set off the trap", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hpBefore, player.CurrentHP);                  // no ApplyTrapDamage for spell-traps
    }

    private sealed record TrapWorld(GameWorld World, Player Player, Room Room);

    private static TrapWorld CreateWorldWithSpellTrap(int spellId, int disarmTraps)
    {
        var database = new InMemoryGameDatabase();
        var room = new Room { MapNumber = 2, RoomNumber = 1106 };
        room.SetExit(new RoomExitDefinition
        {
            MapNumber = 2,
            RoomNumber = 1106,
            Direction = "northwest",
            TargetMap = 2,
            TargetRoom = 1105,
            ExitType = RoomExitType.SpellTrap,
            Para1 = spellId,
            Para2 = 0,
        });
        database.Rooms[(2, 1106)] = room;
        database.Rooms[(2, 1105)] = new Room { MapNumber = 2, RoomNumber = 1105 };

        var world = new GameWorld(database, new InMemoryPlayerRepository());
        var player = new Player
        {
            Name = "Scott2",
            CurrentMapNumber = 2,
            CurrentRoomNumber = 1106,
            CurrentHP = 200,
            MaxHP = 200,
            DisarmTraps = disarmTraps,
        };

        return new TrapWorld(world, player, room);
    }

    private static TrapWorld CreateWorldWithTrap(int damage, int traps, int disarmTraps)
    {
        var database = new InMemoryGameDatabase();
        var room = new Room { MapNumber = 2, RoomNumber = 1106 };
        room.SetExit(new RoomExitDefinition
        {
            MapNumber = 2,
            RoomNumber = 1106,
            Direction = "northwest",
            TargetMap = 2,
            TargetRoom = 1105,
            ExitType = RoomExitType.Trap,
            Para1 = damage,
            Para2 = 0,
        });
        database.Rooms[(2, 1106)] = room;
        database.Rooms[(2, 1105)] = new Room { MapNumber = 2, RoomNumber = 1105 };

        var world = new GameWorld(database, new InMemoryPlayerRepository());
        var player = new Player
        {
            Name = "Scott2",
            CurrentMapNumber = 2,
            CurrentRoomNumber = 1106,
            CurrentHP = 200,
            MaxHP = 200,
            Traps = traps,
            DisarmTraps = disarmTraps,
        };

        return new TrapWorld(world, player, room);
    }
}
