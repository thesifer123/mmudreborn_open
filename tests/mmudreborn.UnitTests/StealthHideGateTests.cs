using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Hide/sneak fidelity: the stealth % formula and the could-a-monster-attack gate.
public sealed class StealthHideGateTests
{
    // ── Stealth % (shared by hide and sneak) ──
    [Fact]
    public void Base_and_encumbrance_bands()
    {
        Assert.Equal(50, Player.CalculateStealthChance(50, encumbrancePercent: 0, otherPlayersInRoom: 0, monstersInRoom: 0, recentlySpotted: false));
        Assert.Equal(45, Player.CalculateStealthChance(50, 40, 0, 0, false));   // >33% → −5
        Assert.Equal(40, Player.CalculateStealthChance(50, 70, 0, 0, false));   // ≥67% → −10
        Assert.Equal(50, Player.CalculateStealthChance(50, 33, 0, 0, false));   // exactly 33% → no band
    }

    [Fact]
    public void Recently_spotted_is_two_thirds_before_the_100_clamp()
    {
        Assert.Equal(40, Player.CalculateStealthChance(60, 0, 0, 0, recentlySpotted: true));   // 60·2/3
        // 120·2/3 = 80 (applied before the 100 clamp); a clamp-first order would give 66.
        Assert.Equal(80, Player.CalculateStealthChance(120, 0, 0, 0, recentlySpotted: true));
    }

    [Fact]
    public void Subtracts_room_occupants_then_caps_and_floors()
    {
        Assert.Equal(47, Player.CalculateStealthChance(50, 0, otherPlayersInRoom: 2, monstersInRoom: 1, recentlySpotted: false));
        Assert.Equal(95, Player.CalculateStealthChance(200, 0, 0, 0, false));   // capped at 95
        Assert.Equal(0, Player.CalculateStealthChance(2, 0, 0, 5, false));      // floored at 0
    }

    // ── The could-a-monster-attack hide/sneak gate ──
    private static MonsterInstance Mon(int hp = 1, int pacifierItemId = 0)
    {
        var template = new Monster { Name = "guard" };
        if (pacifierItemId != 0)
            template.Abilities[CombatEngine.MonsterPacifierItemAbilityId] = pacifierItemId;
        return new MonsterInstance { Template = template, CurrentHP = hp };
    }

    [Fact]
    public void Ordinary_living_monster_blocks_hiding()
    {
        Assert.True(CombatEngine.MonsterCouldAttack(new Player(), [Mon()]));
    }

    [Fact]
    public void Dead_monster_does_not_block()
    {
        Assert.False(CombatEngine.MonsterCouldAttack(new Player(), [Mon(hp: 0)]));
    }

    [Fact]
    public void Snuck_in_undetected_bypasses_the_gate()
    {
        var player = new Player { IsSneaking = true, SneakedInThisTick = true };
        Assert.False(CombatEngine.MonsterCouldAttack(player, [Mon()]));
        // Sneaking alone (not just-moved) does not bypass it.
        player.SneakedInThisTick = false;
        Assert.True(CombatEngine.MonsterCouldAttack(player, [Mon()]));
    }

    [Fact]
    public void Ability185_monster_is_pacified_only_while_carrying_its_item()
    {
        var player = new Player();
        // Not carrying the pacifier item → the guardian still ignores you for hiding (doesn't block).
        Assert.False(CombatEngine.MonsterCouldAttack(player, [Mon(pacifierItemId: 500)]));
        // Carrying it → safe, and it short-circuits even a later ordinary monster.
        player.Inventory.Add(500);
        Assert.False(CombatEngine.MonsterCouldAttack(player, [Mon(pacifierItemId: 500), Mon()]));
    }

    [Fact]
    public void Your_own_pet_does_not_block_hiding()
    {
        // The charm-named exemption (owner match): a monster you OWN
        // never counts as able to attack you.
        var player = new Player { Name = "Owner" };
        var pet = Mon();
        pet.PlayerOwnerName = "Owner";
        Assert.False(CombatEngine.MonsterCouldAttack(player, [pet]));
        // A pet owned by someone ELSE still blocks.
        pet.PlayerOwnerName = "Stranger";
        Assert.True(CombatEngine.MonsterCouldAttack(player, [pet]));
    }

    [Fact]
    public void Freshly_charmed_monster_does_not_block_hiding()
    {
        // The charm-fresh latch: a just-summoned/charmed monster hasn't oriented yet.
        var monster = Mon();
        monster.IsCharmFresh = true;
        Assert.False(CombatEngine.MonsterCouldAttack(new Player(), [monster]));
        monster.IsCharmFresh = false;
        Assert.True(CombatEngine.MonsterCouldAttack(new Player(), [monster]));
    }

    [Fact]
    public void Ability185_does_not_shield_other_ordinary_monsters_when_item_absent()
    {
        // Guardian (no item carried) is skipped, but the ordinary monster after it still blocks.
        Assert.True(CombatEngine.MonsterCouldAttack(new Player(), [Mon(pacifierItemId: 500), Mon()]));
    }
}
