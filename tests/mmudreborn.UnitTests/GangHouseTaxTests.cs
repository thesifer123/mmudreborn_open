using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Nightly gang-house tax is drawn from the deed carrier's bankbook — the first one at or above bank 8,
// which is where stock's keyed bank read lands — and that one book must cover it all. A carrier who
// can't pay is evicted.
public sealed class GangHouseTaxTests
{
    [Fact]
    public void Tax_is_deducted_from_the_leaders_bank_when_affordable()
    {
        var leader = new Player();
        leader.BankBalances[8] = 5000;

        bool paid = GameWorld.TryChargeGangHouseTax(leader, 2000);

        Assert.True(paid);
        Assert.Equal(3000, leader.BankBalances[8]);
    }

    [Fact]
    public void Insufficient_total_bank_balance_cannot_pay_and_leaves_balances_untouched()
    {
        var leader = new Player();
        leader.BankBalances[8] = 800;
        leader.BankBalances[41] = 700; // total 1500 < 2000

        bool paid = GameWorld.TryChargeGangHouseTax(leader, 2000);

        Assert.False(paid);
        Assert.Equal(800, leader.BankBalances[8]);
        Assert.Equal(700, leader.BankBalances[41]);
    }

    [Fact]
    public void Tax_never_spills_over_into_a_second_bank()
    {
        var leader = new Player();
        leader.BankBalances[8] = 1500;  // the book the tax is read from — short
        leader.BankBalances[41] = 1000; // together they'd cover it, but stock never looks here

        bool paid = GameWorld.TryChargeGangHouseTax(leader, 2000);

        Assert.False(paid);
        Assert.Equal(1500, leader.BankBalances[8]);
        Assert.Equal(1000, leader.BankBalances[41]);
    }

    [Fact]
    public void Tax_comes_from_the_lowest_bankbook_at_or_above_bank_8()
    {
        var carrier = new Player();
        carrier.BankBalances[5] = 999_999;    // below 8: never read
        carrier.BankBalances[116] = 9_000;
        carrier.BankBalances[83] = 3_000;     // the first book at or above 8

        bool paid = GameWorld.TryChargeGangHouseTax(carrier, 2000);

        Assert.True(paid);
        Assert.Equal(1_000, carrier.BankBalances[83]);
        Assert.Equal(9_000, carrier.BankBalances[116]);
        Assert.Equal(999_999, carrier.BankBalances[5]);
    }

    [Fact]
    public void No_bankbook_at_or_above_bank_8_cannot_pay()
    {
        var carrier = new Player();
        carrier.BankBalances[5] = 999_999;

        Assert.False(GameWorld.TryChargeGangHouseTax(carrier, 2000));
        Assert.Equal(999_999, carrier.BankBalances[5]);
    }

    [Fact]
    public void Zero_tax_always_succeeds_without_touching_the_bank()
    {
        var leader = new Player();
        leader.BankBalances[8] = 100;

        Assert.True(GameWorld.TryChargeGangHouseTax(leader, 0));
        Assert.Equal(100, leader.BankBalances[8]);
    }

    [Theory]
    [InlineData(1, "Red")]
    [InlineData(6, "Blue")]
    [InlineData(9, "Gold")]
    [InlineData(10, "White")]
    [InlineData(0, "Gang")]
    [InlineData(99, "Gang")]
    public void Color_name_maps_house_id(int houseId, string expected)
    {
        Assert.Equal(expected, GameWorld.GangHouseColorName(houseId));
    }
}
