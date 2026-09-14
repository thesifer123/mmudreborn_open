using mmudreborn.Data.Models;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The two cast-message engines, pinned to the stock contract.
//
//  * the no-target cast engine -- the textblock `cast` verb, quest
//    casts, item use, cast-on-ending chains. Formats the spell's CastMsgB Line1 (caster) / Line3 (room).
//  * the room-cast engine -- exit type 22, room-spell pulses, traps.
//    Formats the spell's CastMsgB **Line2**, with the caster name fixed to the literal "The room".
//
// BOTH branch on MsgStyle bit0: SET means the template names neither the spell nor the
// caster, so those sprintf arguments are simply NOT passed and the amount lands in the FIRST
// placeholder. Getting that branch wrong shifts every placeholder by one -- e.g. substituting the spell
// NAME into a "%d damage" slot.
public class CastMessageContractTests
{
    private static GameSpell Spell(string name, int msgStyle) =>
        new() { Number = 1, Name = name, MessageStyle = msgStyle };

    [Fact]
    public void MsgStyle_bit0_clear_passes_spell_name_first_to_the_caster_line()
    {
        // Stock #1135 "ray of fire from the heavens", MsgStyle 32 (bit0 clear), CastMsgB 2989 Line1.
        var spell = Spell("ray of fire from the heavens", msgStyle: 32);

        string line = CommandParser.FormatCastSuccessCasterLine(
            spell, "A %s smites you with great fury!", targetName: "Bob", amount: 61);

        Assert.Equal("A ray of fire from the heavens smites you with great fury!", line);
    }

    [Fact]
    public void MsgStyle_bit0_set_drops_the_spell_name_arg_so_the_target_lands_first()
    {
        // Stock #58 "deathtouch", MsgStyle 33 (bit0 SET), Targets 8, CastMsgB Line1
        // "Dark energy drains %s of life for %d damage!" -> (targetName, amount). The spell name is
        // never passed; reading the bit the other way would print it into the %s and shove the target
        // name into the "%d damage" slot.
        var spell = Spell("deathtouch", msgStyle: 33);

        string line = CommandParser.FormatCastSuccessCasterLine(
            spell, "Dark energy drains %s of life for %d damage!", targetName: "Bob", amount: 61);

        Assert.Equal("Dark energy drains Bob of life for 61 damage!", line);
    }

    [Fact]
    public void Area_targets_collapse_the_target_name_placeholder_to_nothing()
    {
        // Stock #120 "fireball", MsgStyle 1 (bit0 SET), Targets 12 (area). The cast
        // renders the target-name argument as the EMPTY string for Targets 3 / 9-13 -- which
        // is the whole reason the row is authored "%scausing" with no space after the placeholder.
        var spell = Spell("fireball", msgStyle: 1);

        string line = CommandParser.FormatCastSuccessCasterLine(
            spell, "The fireball explodes, %scausing %d damage!", targetName: "", amount: 34);

        Assert.Equal("The fireball explodes, causing 34 damage!", line);
    }

    [Fact]
    public void Room_cast_line_names_the_caster_The_room_when_bit0_is_clear()
    {
        // A room cast has no player caster: it passes the literal "The room"
        // where a player cast would pass the caster's name.
        var spell = Spell("sandstorm", msgStyle: 0);

        string line = CommandParser.FormatRoomCastLine(spell, "%s casts a spell on you!", amount: 12);

        Assert.Equal("The room casts a spell on you!", line);
    }

    [Fact]
    public void Room_cast_line_with_bit0_set_substitutes_only_the_amount()
    {
        // Stock #526 "magma heat", MsgStyle 1 (bit0 SET), CastMsgB 1553 Line2. The single %d is the
        // damage -- if the bit were misread, "The room" would be printed into it.
        var spell = Spell("magma heat", msgStyle: 1);

        string line = CommandParser.FormatRoomCastLine(
            spell, "You are seared by the flames for %d damage!", amount: 34);

        Assert.Equal("You are seared by the flames for 34 damage!", line);
    }

    [Fact]
    public void Room_cast_falls_back_to_the_stock_template_not_an_invented_line()
    {
        // This is the stock template used when the spell carries no CastMsgB row. "hits you for"
        // occurs ZERO times in the stock string table -- that phrasing was ours.
        var spell = Spell("magma drip", msgStyle: 0);

        string line = CommandParser.FormatRoomCastLine(
            spell, CommandParser.RoomCastFallbackTemplate, amount: 9);

        Assert.Equal("The room cast magma drip on you for 9 damage.", line);
        Assert.DoesNotContain("hits you for", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cast_success_room_line_leads_with_the_caster_then_spell_then_target()
    {
        // Stock #1135 CastMsgB 2989 Line3 "%s is smitten by a %s!" -> (casterName, spellName, ...).
        var spell = Spell("ray of fire from the heavens", msgStyle: 32);

        string line = CommandParser.FormatCastSuccessRoomLine(
            spell, "%s is smitten by a %s!", casterName: "Bob", targetName: "Bob", amount: 61);

        Assert.Equal("Bob is smitten by a ray of fire from the heavens!", line);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void Only_bit0_of_MsgStyle_selects_the_anonymous_style(int msgStyle, bool omitsNames)
    {
        Assert.Equal(omitsNames, CommandParser.SpellMessageOmitsNames(Spell("x", msgStyle)));
    }
}
