using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Charm-scaled shop pricing. The stock buy and sell formulas:
//   buy  = ((110 - Charm/5) * ((markup+100) * val / 100)) / 100   (higher Charm = cheaper)
//   sell = ((Charm/2 + 25) * val) / 100                           (higher Charm = more payout)
// Charm 50 is the neutral point: buy collapses to the markup-only price, sell to val/2.
// A realm-wide 25% markup floor (NMR "Orfero's Shop" fix) is applied to buys to kill the
// max-Charm bard buy-low/sell-high arbitrage — we do not reproduce that stock bug.
public sealed class ShopPriceFormulaTests
{
    [Theory]
    // Neutral Charm 50 reproduces the old markup-only price (markup ≥ floor).
    [InlineData(50, 100, 3000, 6000)]   // (110-10)=100 → 100*((200*3000/100))/100 = 6000
    [InlineData(50, 100, 14, 28)]       // (200*14/100)=28 → *100/100 = 28
    // Higher Charm makes buying cheaper; lower Charm makes it dearer.
    [InlineData(100, 100, 3000, 5400)]  // (110-20)=90 → 90*6000/100 = 5400
    [InlineData(0, 100, 3000, 6600)]    // (110-0)=110 → 110*6000/100 = 6600
    // Markup raises the price.
    [InlineData(50, 200, 1000, 3000)]   // (300*1000/100)=3000 → *100/100 = 3000
    // Non-positive value → free.
    [InlineData(50, 100, 0, 0)]
    public void ComputeShopBuyPrice_matches_stock_formula(int charm, int markup, long value, long expected)
    {
        Assert.Equal(expected, CommandParser.ComputeShopBuyPriceCopper(charm, markup, value));
    }

    [Theory]
    [InlineData(50, 3000, 1500)]   // (25+25)=50 → 50*3000/100 = 1500 (== val/2, old flat rate)
    [InlineData(100, 3000, 2250)]  // (50+25)=75 → 75*3000/100 = 2250
    [InlineData(0, 3000, 750)]     // (0+25)=25 → 25*3000/100 = 750
    [InlineData(50, 14, 7)]        // 50*14/100 = 7
    [InlineData(50, 0, 0)]         // non-positive value → 0
    public void ComputeShopSellPrice_matches_stock_formula(int charm, long value, long expected)
    {
        Assert.Equal(expected, CommandParser.ComputeShopSellPriceCopper(charm, value));
    }

    // The payout is taken against the item's COPPER value, not its raw price in its own
    // denomination. A hard leather helm is Price 2 / Currency 2 (gold) = 200 copper; scaling the raw
    // "2" first floored the whole sale to zero, so a helm that had just cost 4 gold sold for
    // "0 copper farthings".
    [Theory]
    [InlineData(30, 80)]    // (15+25) * 200 / 100 = 80 copper
    [InlineData(50, 100)]   // (25+25) * 200 / 100 = 100 copper = 1 gold
    [InlineData(100, 150)]  // (50+25) * 200 / 100 = 150 copper
    public void Cheap_item_still_pays_out_when_scaled_in_copper(int charm, long expectedCopper)
    {
        const long hardLeatherHelmCopper = 2 * 100;   // Price 2, Currency 2 (gold)

        long payout = CommandParser.ComputeShopSellPriceCopper(charm, hardLeatherHelmCopper);

        Assert.Equal(expectedCopper, payout);
        Assert.True(payout > 0, "a 2-gold item must never sell for nothing");
    }

    // Buy price is likewise taken against the COPPER value. A hard leather helm (Price 2 /
    // Currency 2 = 200 copper) at Charm 40 in a 100%-markup shop is 408 copper -- stock charges
    // "4 gold crowns, 8 copper farthings". Scaling the raw "2" truncated to a flat 4 gold and threw
    // the 8 copper away.
    [Fact]
    public void Buy_price_keeps_the_sub_denomination_remainder()
    {
        const long hardLeatherHelmCopper = 2 * 100;   // Price 2, Currency 2 (gold)

        long price = CommandParser.ComputeShopBuyPriceCopper(charm: 40, markupPercent: 100, baseValueCopper: hardLeatherHelmCopper);

        Assert.Equal(408, price);   // (110-8) * ((200*200)/100) / 100
    }

    [Fact]
    public void Buy_price_floors_markup_at_25_percent_for_the_orfero_fix()
    {
        // A 0%-markup shop is treated as 25% when buying, so the price is higher than the raw
        // markup-only value — this is the realm-wide minimum that removes the bard arbitrage.
        long zeroMarkup = CommandParser.ComputeShopBuyPriceCopper(charm: 50, markupPercent: 0, baseValueCopper: 1000);
        long twentyFiveMarkup = CommandParser.ComputeShopBuyPriceCopper(charm: 50, markupPercent: 25, baseValueCopper: 1000);

        Assert.Equal(twentyFiveMarkup, zeroMarkup);
        Assert.Equal(1250, zeroMarkup);   // (110-10)/100 * ((25+100)*1000/100) = 100/100 * 1250 = 1250
    }

    [Theory]
    // At realistic max Charm (≤100, even buffed with Beauty), a 0%-markup shop must not let a player
    // sell an item back for more than they paid — the Orfero exploit is closed by the 25% floor.
    [InlineData(80)]
    [InlineData(100)]
    public void No_buy_low_sell_high_arbitrage_at_realistic_charm_with_the_markup_floor(int charm)
    {
        const int value = 1000;
        long buy = CommandParser.ComputeShopBuyPriceCopper(charm, markupPercent: 0, baseValueCopper: value);
        long sell = CommandParser.ComputeShopSellPriceCopper(charm, value);

        Assert.True(buy >= sell, $"Charm {charm}: buy {buy} must be ≥ sell {sell} to prevent arbitrage.");
    }
}
