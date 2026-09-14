using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class LightBurnoutMessageResolverTests
{
    [Fact]
    public void ResolveLightBurnoutMessage_uses_stock_lamp_text_for_marsh_light()
    {
        var item = new Item
        {
            Number = 692,
            Name = "marsh light"
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [8604] = new RoomMessage { Number = 8604, Line1 = "Your lamp runs out of oil, and goes out." }
        };

        string message = CommandParser.ResolveLightBurnoutMessage(item, messages);

        Assert.Equal("Your lamp runs out of oil, and goes out.", message);
    }

    [Fact]
    public void ResolveLightBurnoutMessage_uses_stock_generic_text_for_scaled_lantern()
    {
        var item = new Item
        {
            Number = 1233,
            Name = "scaled lantern"
        };

        string message = CommandParser.ResolveLightBurnoutMessage(item, new Dictionary<int, RoomMessage>());

        Assert.Equal("It's uses gone, scaled lantern disappears from your inventory!", message);
    }

    [Fact]
    public void ResolveLightBurnoutMessage_returns_empty_when_stock_message_id_has_no_loaded_row()
    {
        var item = new Item
        {
            Number = 935,
            Name = "Eternal Fire"
        };

        string message = CommandParser.ResolveLightBurnoutMessage(item, new Dictionary<int, RoomMessage>());

        Assert.Equal(string.Empty, message);
    }
}