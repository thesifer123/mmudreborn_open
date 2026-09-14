using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// The shop-list row format "%s%-29.29s %s%-8d %5d %-8s":
///     price = item.Price * (shop.Markup + 100) / 100   in the item's OWN currency unit
/// One integer, one currency name, no Charm term, no denomination breakdown.
/// The expectations below are the real stock board readings captured from Sentara's and Skali's
/// (both Markup 100) paired with the seed's actual item Price/Currency.
/// </summary>
public class ShopListDisplayPriceTests
{
    [Theory]
    // item                     basePrice  markup  expected
    [InlineData("rigid leather tunic",     8, 100,   16)]  // stock board: 16 gold crowns
    [InlineData("chainmail hauberk",      20, 100,   40)]  // stock board: 40 gold crowns
    [InlineData("scalemail tunic",        25, 100,   50)]  // stock board: 50 gold crowns
    [InlineData("half-plate corselet",    90, 100,  180)]  // stock board: 180 gold crowns
    [InlineData("light plate corselet",  150, 100,  300)]  // stock board: 300 gold crowns
    [InlineData("full plate corselet",   250, 100,  500)]  // stock board: 500 gold crowns
    [InlineData("fine breastplate",     1500, 100, 3000)]  // stock board: 3000 gold crowns
    [InlineData("cloth pants",             3, 100,    6)]  // stock board: 6 silver nobles
    [InlineData("leather gloves",          5, 100,   10)]  // stock board: 10 silver nobles
    [InlineData("leather belt",            1, 100,    2)]  // stock board: 2 silver nobles
    public void List_price_matches_the_stock_board(string item, int basePrice, int markup, int expected)
    {
        Assert.Equal(expected, CommandParser.ComputeShopListDisplayPrice(markup, basePrice));
        Assert.NotEqual(string.Empty, item);   // label only, keeps the capture readable
    }

    [Fact]
    public void List_price_ignores_charm_entirely()
    {
        // The Charm curve lives in the buy, never in the listing. The board shows the same
        // number to a Charm 1 character and a Charm 250 character — the regression that produced
        // "16 gold crowns, 3 silver nobles, 2 copper farthings" (base 8 * 2 markup * 102/100 Charm).
        int atAnyCharm = CommandParser.ComputeShopListDisplayPrice(100, 8);
        Assert.Equal(16, atAnyCharm);

        // For contrast: the BUY path is Charm-sensitive and still must be.
        long cheapBuy = CommandParser.ComputeShopBuyPriceCopper(charm: 250, markupPercent: 100, baseValueCopper: 800);
        long dearBuy = CommandParser.ComputeShopBuyPriceCopper(charm: 1, markupPercent: 100, baseValueCopper: 800);
        Assert.True(cheapBuy < dearBuy, "Charm must still move the purchase price.");
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void Free_slots_report_zero(int basePrice, int expected)
    {
        // Stock uses a sibling format ("... Free") when the price field is 0.
        Assert.Equal(expected, CommandParser.ComputeShopListDisplayPrice(100, basePrice));
    }

    [Fact]
    public void Markup_zero_quotes_the_bare_base_price()
    {
        // 52 stock shops carry Markup 0; the board then reads exactly the item's Price field.
        Assert.Equal(8, CommandParser.ComputeShopListDisplayPrice(0, 8));
        Assert.Equal(1500, CommandParser.ComputeShopListDisplayPrice(0, 1500));
    }

    [Fact]
    public void Markup_uses_integer_truncation_like_stock()
    {
        // (markup+100) * price / 100 in C integer math: 25 * 103 / 100 = 25, not 25.75 -> 26.
        Assert.Equal(25, CommandParser.ComputeShopListDisplayPrice(3, 25));
        Assert.Equal(51, CommandParser.ComputeShopListDisplayPrice(2, 50));
    }
}
