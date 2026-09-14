using mmudreborn.Game;
using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Nightly gang-house tax is drawn from the gang leader's banked copper (largest balance first),
// mirroring stock drawing house tax from the leader's bankbook. A leader who can't cover it is
// evicted.
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
    public void Tax_drains_the_largest_balance_first_then_spills_over()
    {
        var leader = new Player();
        leader.BankBalances[8] = 1500;  // largest — fully drained
        leader.BankBalances[41] = 1000; // covers the remainder

        bool paid = GameWorld.TryChargeGangHouseTax(leader, 2000);

        Assert.True(paid);
        Assert.Equal(0, leader.BankBalances[8]);
        Assert.Equal(500, leader.BankBalances[41]);
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
