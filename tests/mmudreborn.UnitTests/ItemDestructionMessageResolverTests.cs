using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The generic "It's uses gone..." line fires ONLY when the item has no
// destruction message (DestructMsg == 0). A non-zero DestructMsg prints that message's Line1 (holder) +
// Line2 (room); if the row is missing or blank it stays SILENT and never falls back to the generic line.
public sealed class ItemDestructionMessageResolverTests
{
    private static (string? HolderLine, string? RoomLine) Resolve(
        Item item, Dictionary<int, RoomMessage> messages)
        => CommandParser.ResolveItemDestructionMessages(item, messages, "Goober");

    [Fact]
    public void Generic_line_only_when_DestructMsg_is_zero()
    {
        var item = new Item { Number = 1233, Name = "scaled lantern", DestructMsg = 0 };

        var (holder, room) = Resolve(item, new Dictionary<int, RoomMessage>());

        Assert.Equal("It's uses gone, scaled lantern disappears from your inventory!", holder);
        Assert.Null(room);
    }

    [Fact]
    public void NonZero_DestructMsg_with_missing_row_is_silent()
    {
        // 120 stock potions/scrolls point at message 8420, which is deliberately blank/absent.
        var item = new Item { Number = 223, Name = "minor healing potion", DestructMsg = 8420 };

        var (holder, room) = Resolve(item, new Dictionary<int, RoomMessage>());

        Assert.Null(holder);   // NOT the generic "It's uses gone..." line
        Assert.Null(room);
    }

    [Fact]
    public void NonZero_DestructMsg_with_blank_row_is_silent()
    {
        var item = new Item { Number = 223, Name = "minor healing potion", DestructMsg = 8420 };
        var messages = new Dictionary<int, RoomMessage>
        {
            [8420] = new RoomMessage { Number = 8420, Line1 = "", Line2 = "" }
        };

        var (holder, room) = Resolve(item, messages);

        Assert.Null(holder);
        Assert.Null(room);
    }

    [Fact]
    public void NonZero_DestructMsg_prints_Line1_to_holder()
    {
        // Stock learn-spell scrolls carry DestructMsg 97 = "Its magic used, the scroll disintegrates."
        var item = new Item { Number = 150, Name = "scroll of swarm", DestructMsg = 97 };
        var messages = new Dictionary<int, RoomMessage>
        {
            [97] = new RoomMessage { Number = 97, Line1 = "Its magic used, the scroll disintegrates." }
        };

        var (holder, room) = Resolve(item, messages);

        Assert.Equal("Its magic used, the scroll disintegrates.", holder);
        Assert.Null(room);
    }

    [Fact]
    public void NonZero_DestructMsg_routes_Line2_to_room_with_player_and_item_substitution()
    {
        var item = new Item { Number = 500, Name = "exploding rune", DestructMsg = 1357 };
        var messages = new Dictionary<int, RoomMessage>
        {
            [1357] = new RoomMessage
            {
                Number = 1357,
                Line1 = "The %s explodes with unfathomable force!",
                Line2 = "%s's %s explodes with unfathomable force!"
            }
        };

        var (holder, room) = Resolve(item, messages);

        Assert.Equal("The exploding rune explodes with unfathomable force!", holder);
        Assert.Equal("Goober's exploding rune explodes with unfathomable force!", room);
    }
}
