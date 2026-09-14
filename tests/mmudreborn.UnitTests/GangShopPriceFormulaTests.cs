using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Gang-shop pricing (STOCK / LIST / BUY, shop type 11). The owner sets a raw
// per-slot price and currency; the shop markup scales it. Display shows price = (markup+100)*slot/100
// in the slot currency (no Charm). Buying converts the slot price into copper, then applies the same
// Charm/markup curve normal shops use — but with NO 25% markup floor (the owner sets markup outright).
public sealed class GangShopPriceFormulaTests
{
    [Theory]
    [InlineData(0, 1000, 1000)]    // no markup → raw price
    [InlineData(100, 1000, 2000)]  // +100% → doubled
    [InlineData(250, 40, 140)]     // (350*40/100) = 140
    [InlineData(1000, 5, 55)]      // max markup (1100*5/100) = 55
    [InlineData(100, 0, 0)]        // free slot stays free
    public void DisplayPrice_scales_slot_price_by_markup(int markup, int slotPrice, int expected)
    {
        Assert.Equal(expected, CommandParser.ComputeGangShopDisplayPrice(markup, slotPrice));
    }

    [Theory]
    // Currency 0 = copper (multiplier 1). Charm 50 is neutral (110-10=100 → ×1).
    [InlineData(50, 100, 1000, 0, 2000)]  // (200*1000/100)=2000 → ×100/100 = 2000
    [InlineData(50, 0, 1000, 0, 1000)]    // markup 0, NO floor → 1000 (a normal shop would floor to 25%)
    [InlineData(100, 100, 1000, 0, 1800)] // (110-20)=90 → 90*2000/100 = 1800 (high Charm cheaper)
    [InlineData(0, 100, 1000, 0, 2200)]   // (110-0)=110 → 110*2000/100 = 2200 (low Charm dearer)
    [InlineData(50, 100, 0, 0, 0)]        // free slot → 0 copper
    public void BuyCopper_applies_currency_markup_and_charm(int charm, int markup, int slotPrice, int currency, long expected)
    {
        Assert.Equal(expected, CommandParser.ComputeGangShopBuyCopper(charm, markup, slotPrice, currency));
    }

    [Fact]
    public void BuyCopper_converts_the_slot_currency_to_copper()
    {
        // A gold-priced slot is charged at the gold→copper denomination, neutral Charm/markup.
        long expectedCopper = 5L * CurrencyHelper.CopperPerGold; // markup 0, charm 50 → raw
        Assert.Equal(expectedCopper, CommandParser.ComputeGangShopBuyCopper(50, 0, 5, 2));
    }
}
