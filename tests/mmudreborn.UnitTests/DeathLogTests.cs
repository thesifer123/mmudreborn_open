using System;
using System.Linq;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Recent-death log. NON-STOCK: the original keeps no death history, so the whole thing is off unless a sysop
// switches DEATHLOG on, and these cover both that gate and the "what killed me" attribution.
public sealed class DeathLogTests
{
    private static readonly DateTime When = new(2026, 8, 11, 14, 32, 0, DateTimeKind.Utc);

    private static (GameWorld World, Player Player) NewWorld()
    {
        var world = new GameWorld(new InMemoryGameDatabase(), new InMemoryPlayerRepository());
        var player = new Player { Name = "Jerk", CurrentMapNumber = 1, CurrentRoomNumber = 2321 };
        world.AddPlayer(player);
        return (world, player);
    }

    [Fact]
    public void Nothing_is_recorded_while_the_feature_is_off()
    {
        var (world, player) = NewWorld();

        Assert.False(world.DeathLogEnabled);   // non-stock features default OFF here
        world.RecordPlayerDeath(player, 1, 2321, "Giant Rat");

        Assert.Empty(player.DeathLog);
    }

    [Fact]
    public void A_death_records_where_and_what_killed_you()
    {
        var (world, player) = NewWorld();
        world.SetDeathLogEnabled(true);

        world.RecordPlayerDeath(player, 1, 2321, "Giant Rat");

        var death = Assert.Single(player.DeathLog);
        Assert.Equal(1, death.MapNumber);
        Assert.Equal(2321, death.RoomNumber);
        Assert.Equal("Giant Rat", death.Killer);
    }

    // The combat path has no attacker in scope by the time the death resolves, so it relies on whatever
    // last damaged the character -- stamped at the damage site (CombatEngine's PvP and monster paths).
    [Fact]
    public void An_unattributed_death_falls_back_to_whoever_last_dealt_damage()
    {
        var (world, player) = NewWorld();
        world.SetDeathLogEnabled(true);

        player.RecordDamageSource("Scott");
        world.RecordPlayerDeath(player, 1, 2321);

        Assert.Equal("Scott", Assert.Single(player.DeathLog).Killer);
    }

    [Fact]
    public void A_death_with_no_attacker_at_all_still_names_something()
    {
        var (world, player) = NewWorld();
        world.SetDeathLogEnabled(true);

        world.RecordPlayerDeath(player, 1, 2321);

        Assert.Equal("unknown", Assert.Single(player.DeathLog).Killer);
    }

    // The attacker is a fact about the CURRENT fight. Leaving it set would let a later, unrelated death
    // (poison on the way home) be credited to the monster that beat you an hour ago.
    [Fact]
    public void Recording_a_death_clears_the_last_damage_source()
    {
        var (world, player) = NewWorld();
        world.SetDeathLogEnabled(true);

        player.RecordDamageSource("Giant Rat");
        world.RecordPlayerDeath(player, 1, 2321);
        world.RecordPlayerDeath(player, 1, 2321);

        Assert.Equal("Giant Rat", player.DeathLog[1].Killer);
        Assert.Equal("unknown", player.DeathLog[0].Killer);
    }

    [Fact]
    public void The_log_keeps_the_newest_entries_first_and_caps_at_four()
    {
        var (world, player) = NewWorld();
        world.SetDeathLogEnabled(true);

        foreach (int room in new[] { 1, 2, 3, 4, 5, 6 })
            world.RecordPlayerDeath(player, 1, room, $"killer{room}");

        Assert.Equal(Player.MaxDeathLogEntries, player.DeathLog.Count);
        Assert.Equal(new[] { "killer6", "killer5", "killer4", "killer3" }, player.DeathLog.Select(d => d.Killer));
    }

    [Fact]
    public void The_room_name_is_snapshotted_at_death_not_resolved_later()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 2321)] = new mmudreborn.Data.Models.Room { Name = "Newhaven Arena" };
        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var player = new Player { Name = "Jerk", CurrentMapNumber = 1, CurrentRoomNumber = 2321 };
        world.AddPlayer(player);
        world.SetDeathLogEnabled(true);

        world.RecordPlayerDeath(player, 1, 2321, "Giant Rat");
        db.Rooms[(1, 2321)].Name = "Renamed By A Reseed";

        Assert.Equal("Newhaven Arena", Assert.Single(player.DeathLog).RoomName);
    }

    [Fact]
    public void Records_round_trip_through_the_player_repository()
    {
        var repo = new InMemoryPlayerRepository();
        var world = new GameWorld(new InMemoryGameDatabase(), repo);
        var player = new Player { Name = "Jerk", CurrentMapNumber = 1, CurrentRoomNumber = 2321 };
        world.AddPlayer(player);
        world.SetDeathLogEnabled(true);

        player.RecordDeath(When, 1, 2321, "Newhaven Arena", "Giant Rat");
        repo.SavePlayer(player);

        var reloaded = repo.LoadPlayerByName("Jerk");
        Assert.NotNull(reloaded);
        var death = Assert.Single(reloaded!.DeathLog);
        Assert.Equal("Newhaven Arena", death.RoomName);
        Assert.Equal("Giant Rat", death.Killer);
        Assert.Equal(When, death.WhenUtc);
    }
}
