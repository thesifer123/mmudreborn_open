using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A room's own NPC (Room.NPC) that locks onto a player now chases them out (bug #240). Stock's "primary
// present" latch is cleared only by the primary's DEATH, so while it roams elsewhere its home room must
// not spawn a second copy — a GameLimit-0 primary (like the one below) has nothing else to stop it.
public sealed class RoomNpcChaseLatchTests
{
    private const int Primary = 900;

    private static (GameWorld World, Player Visitor) Setup()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 10)] = new Room { MapNumber = 1, RoomNumber = 10, NPC = Primary };
        db.Rooms[(1, 11)] = new Room { MapNumber = 1, RoomNumber = 11 };
        db.Monsters[Primary] = new Monster
        {
            Number = Primary, Name = "gatekeeper", HP = 100, Energy = 1000, Align = 2, Group = 7, FollowPercent = 100,
        };
        var world = new GameWorld(db, new InMemoryPlayerRepository());
        var visitor = new Player { Name = "Visitor", CurrentMapNumber = 1, CurrentRoomNumber = 10 };
        world.AddPlayer(visitor);
        return (world, visitor);
    }

    private static int LivingCopies(GameWorld world)
        => world.GetMonstersInRoom(1, 10).Concat(world.GetMonstersInRoom(1, 11))
            .Count(m => m.Template.Number == Primary && !m.IsDead);

    [Fact]
    public void A_room_npc_that_chased_someone_out_is_not_respawned_while_it_lives()
    {
        var (world, visitor) = Setup();
        world.NotifyPlayerEnteredRoom(visitor);   // the primary appears on entry
        var primary = Assert.Single(world.GetMonstersInRoom(1, 10), m => m.Template.Number == Primary);

        world.RelocateMonsterForTests(primary, 1, 11);   // it chased a player out
        world.NotifyPlayerEnteredRoom(visitor);          // someone walks back into its room

        Assert.Equal(1, LivingCopies(world));
    }

    [Fact]
    public void A_room_npc_that_died_away_from_home_respawns_at_home_on_the_next_entry()
    {
        var (world, visitor) = Setup();
        world.NotifyPlayerEnteredRoom(visitor);
        var primary = Assert.Single(world.GetMonstersInRoom(1, 10), m => m.Template.Number == Primary);
        world.RelocateMonsterForTests(primary, 1, 11);

        primary.CurrentHP = 0;
        Assert.True(world.TryBeginMonsterDeathProcessing(primary));
        world.RemoveDeadMonster(primary);
        world.NotifyPlayerEnteredRoom(visitor);

        var respawned = Assert.Single(world.GetMonstersInRoom(1, 10), m => m.Template.Number == Primary);
        Assert.NotSame(primary, respawned);
        Assert.Equal(1, LivingCopies(world));
    }
}
