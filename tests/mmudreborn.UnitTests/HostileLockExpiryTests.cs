using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A hostile lock (the monster's target-name slot) makes it attack that player on sight with no
// alignment test, so the lock has to END: the fast monster update counts a miss each pass the locked
// player is offline or in another room, blanks the slot past 15, and every swing zeroes the count.
// Before bug #240 our locks never lapsed.
public sealed class HostileLockExpiryTests
{
    private const int DuergarWarrior = 455;

    private static (GameWorld World, MonsterInstance Duergar) Setup()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(6, 2648)] = new Room { MapNumber = 6, RoomNumber = 2648, MonsterType = 24 };
        // FollowPercent 100: the post-swing lock roll (a 1-99 roll under FollowPercent) always re-locks,
        // so the swing-reset case below is deterministic.
        db.Monsters[DuergarWarrior] = new Monster
        {
            Number = DuergarWarrior, Name = "duergar warrior", HP = 300, Energy = 1000,
            Align = 6, Group = 24, FollowPercent = 100,
        };
        var world = new GameWorld(db, new InMemoryPlayerRepository());
        Assert.True(world.TrySpawnMonsterInRoom(6, 2648, DuergarWarrior, ignoreRoomRestrictions: true, out var duergar, out _));
        duergar!.SetLockedTarget("Rook");
        return (world, duergar);
    }

    [Fact]
    public void A_lock_lapses_on_the_sixteenth_pass_without_its_target()
    {
        var (world, duergar) = Setup();   // Rook is not online

        for (int i = 0; i < 15; i++)
            world.ProcessHostileLockMisses();
        Assert.True(duergar.IsLockedOnTarget("Rook"));

        world.ProcessHostileLockMisses();
        Assert.False(duergar.HasLockedTarget);
    }

    [Fact]
    public void Sharing_a_room_with_its_target_is_never_a_miss()
    {
        var (world, duergar) = Setup();
        world.AddPlayer(new Player { Name = "Rook", CurrentMapNumber = 6, CurrentRoomNumber = 2648 });

        for (int i = 0; i < 40; i++)
            world.ProcessHostileLockMisses();

        Assert.True(duergar.IsLockedOnTarget("Rook"));
    }

    [Fact]
    public void A_target_in_another_room_counts_as_a_miss()
    {
        var (world, duergar) = Setup();
        world.AddPlayer(new Player { Name = "Rook", CurrentMapNumber = 6, CurrentRoomNumber = 2647 });

        for (int i = 0; i < 16; i++)
            world.ProcessHostileLockMisses();

        Assert.False(duergar.HasLockedTarget);
    }

    [Fact]
    public void A_swing_restarts_the_count()
    {
        var (world, duergar) = Setup();

        for (int i = 0; i < 15; i++)
            world.ProcessHostileLockMisses();
        world.ApplyMonsterPostAttackLock(duergar, "Rook");   // it swung: count back to zero, re-locked

        for (int i = 0; i < 15; i++)
            world.ProcessHostileLockMisses();
        Assert.True(duergar.IsLockedOnTarget("Rook"));

        world.ProcessHostileLockMisses();
        Assert.False(duergar.HasLockedTarget);
    }

    [Fact]
    public void A_lock_is_on_an_exact_name_not_a_prefix()
    {
        var (world, duergar) = Setup();
        // A longer-named player standing right there must not keep "Rook"'s lock alive.
        world.AddPlayer(new Player { Name = "Rookie", CurrentMapNumber = 6, CurrentRoomNumber = 2648 });

        for (int i = 0; i < 16; i++)
            world.ProcessHostileLockMisses();

        Assert.False(duergar.HasLockedTarget);
    }
}
