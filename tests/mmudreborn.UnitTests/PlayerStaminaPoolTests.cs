using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class PlayerStaminaPoolTests
{
    [Fact]
    public void New_player_uses_default_max_stamina_of_one_thousand()
    {
        var player = new Player();

        // The energy update refills the pool toward staminaCap. Stock cap is 1000.
        Assert.Equal(WeaponSwingPreviewCalculator.DefaultPlayerMaxStamina, player.MaxStamina);
        Assert.Equal(1000, player.MaxStamina);
        Assert.Equal(player.MaxStamina, player.GetEffectiveMaxStamina());
        // CurrentEnergy starts empty; combat round prep / out-of-combat refill bring it up.
        Assert.Equal(0, player.CurrentEnergy);
    }

    [Fact]
    public void CurrentEnergy_is_aliased_with_WeaponSwingEnergyRemainder()
    {
        var player = new Player();

        player.CurrentEnergy = 250;
        Assert.Equal(250, player.WeaponSwingEnergyRemainder);

        player.WeaponSwingEnergyRemainder = 700;
        Assert.Equal(700, player.CurrentEnergy);
    }

    [Fact]
    public void PrepareCombatRound_adds_full_max_stamina_and_allows_overshoot()
    {
        var player = new Player { MaxStamina = 1000, CurrentEnergy = 350 };

        player.PrepareCombatRound();

        // In autocombat the fast tick simply adds the cap without clamping.
        Assert.Equal(1350, player.CurrentEnergy);
    }

    [Fact]
    public void PrepareCombatRound_at_full_pool_does_not_overshoot_so_no_opening_burst()
    {
        // The energy update only adds the cap when curEnergy < max (or the idle bit
        // is clear). A player engaging from a full pool (out-of-combat clamp left it
        // at exactly cap) must NOT jump to 2×cap — that produced a spurious opening-round swing
        // burst (e.g. 4 swings at level 1 instead of 2). The carried remainder in sustained combat
        // is always < EU ≤ cap, so this only suppresses the engage-from-full double.
        var player = new Player { MaxStamina = 1000, CurrentEnergy = 1000 };

        player.PrepareCombatRound();

        Assert.Equal(1000, player.CurrentEnergy);
    }

    [Fact]
    public void PrepareCombatRound_clamps_negative_remainder_before_adding_cap()
    {
        var player = new Player { MaxStamina = 1000, CurrentEnergy = -50 };

        player.PrepareCombatRound();

        Assert.Equal(1000, player.CurrentEnergy);
    }

    [Fact]
    public void RefillStaminaOutOfCombat_raises_low_pool_to_cap()
    {
        var player = new Player { MaxStamina = 1000, CurrentEnergy = 200 };

        player.RefillStaminaOutOfCombat();

        Assert.Equal(1000, player.CurrentEnergy);
    }

    [Fact]
    public void RefillStaminaOutOfCombat_leaves_overshoot_alone_so_active_combat_buffs_persist()
    {
        // Out-of-combat refill is a one-way ratchet up to the cap; the stock slow tick clamps the
        // pool to the cap, but the regen path here only fires when CurrentEnergy is below cap.
        var player = new Player { MaxStamina = 1000, CurrentEnergy = 1500 };

        player.RefillStaminaOutOfCombat();

        Assert.Equal(1500, player.CurrentEnergy);
    }

    [Fact]
    public void GetEffectiveMaxStamina_falls_back_to_default_when_cap_is_unset()
    {
        var player = new Player { MaxStamina = 0 };

        Assert.Equal(WeaponSwingPreviewCalculator.DefaultPlayerMaxStamina, player.GetEffectiveMaxStamina());
    }
}
