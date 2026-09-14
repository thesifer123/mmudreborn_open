using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #178: an alignment change must re-validate worn gear and strip items the new alignment no longer
// permits — the black-and-white serpent ring (item 645, ability 112 "Neutral Aligned") should come off
// when a Neutral character goes Good. Stock re-derives the alignment band and,
// when it changed, re-validates the worn items (an eligibility check per worn item, unequip the
// failures, print "Your <item> has been removed."). These pin that behavior.
public sealed class AlignmentEquipmentRemovalTests
{
    private const int SerpentRingId = 645;
    private const string Slot = "finger";
    private const int NeutralAlignedAbility = 112;
    private const int MaxHpAbility = 88;   // serpent ring also grants +HP, reversed on unequip

    private static GameWorld NewWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Square", Description = "x" };
        db.Items[SerpentRingId] = new Item
        {
            Number = SerpentRingId,
            Name = "black and white serpent ring",
            Worn = 10,
            Abilities = { [NeutralAlignedAbility] = 1, [MaxHpAbility] = 35 },
        };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static (GameWorld World, Player Player, RecordingGameClient Client, CommandParser Parser) Setup(float evilPoints)
    {
        var world = NewWorld();
        var client = new RecordingGameClient();
        var player = new Player
        {
            Name = "Pookie",
            CurrentMapNumber = 1,
            CurrentRoomNumber = 1,
            Client = client,
            EvilPoints = evilPoints,
        };
        client.Player = player;
        player.Equipment[Slot] = SerpentRingId;
        world.AddPlayer(player);   // registers online so SendToPlayer reaches the client
        return (world, player, client, new CommandParser(client, world, player));
    }

    [Fact]
    public void Neutral_item_is_stripped_when_alignment_crosses_to_good()
    {
        var (_, player, client, parser) = Setup(evilPoints: 0f);   // Neutral
        int bucketBefore = CombatEngine.GetAlignmentBucket(player.EvilPoints);
        Assert.Equal(0, bucketBefore);

        player.EvilPoints = -100f;   // now Good
        parser.RevalidateWornItemsAfterAlignmentChange(player, bucketBefore);

        Assert.False(player.Equipment.ContainsKey(Slot));            // unequipped
        Assert.Contains(SerpentRingId, player.Inventory);            // moved to pack
        Assert.Contains(client.Lines, l => l.Contains("Your black and white serpent ring has been removed."));
    }

    [Fact]
    public void No_removal_when_the_alignment_band_did_not_change()
    {
        // Drift within Neutral (0 -> 20 is still the Neutral band): nothing should be stripped.
        var (_, player, client, parser) = Setup(evilPoints: 0f);
        int bucketBefore = CombatEngine.GetAlignmentBucket(player.EvilPoints);

        player.EvilPoints = 20f;   // still Neutral
        parser.RevalidateWornItemsAfterAlignmentChange(player, bucketBefore);

        Assert.True(player.Equipment.ContainsKey(Slot));
        Assert.DoesNotContain(client.Lines, l => l.Contains("has been removed"));
    }

    [Fact]
    public void Neutral_item_is_stripped_when_alignment_crosses_to_evil()
    {
        var (_, player, client, parser) = Setup(evilPoints: 0f);   // Neutral
        int bucketBefore = CombatEngine.GetAlignmentBucket(player.EvilPoints);

        player.EvilPoints = 60f;   // now Evil (Outlaw band)
        parser.RevalidateWornItemsAfterAlignmentChange(player, bucketBefore);

        Assert.False(player.Equipment.ContainsKey(Slot));
        Assert.Contains(SerpentRingId, player.Inventory);
    }

    [Theory]
    [InlineData(-100f, 1)]   // Good
    [InlineData(0f, 0)]      // Neutral
    [InlineData(60f, -1)]    // Evil
    public void Alignment_bucket_maps_evil_points_to_good_neutral_evil(float evilPoints, int expectedBucket)
    {
        Assert.Equal(expectedBucket, CombatEngine.GetAlignmentBucket(evilPoints));
    }
}
