using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// Gang-house guardians carry ability 185 "Do not Attack if Item Nmbr" (= an emblem item id). A player
// holding the matching emblem gets safe passage; everyone else is attacked normally. See
// (ruby guardian 510 -> ruby emblem 843; diamond 537 -> 1054).
public sealed class GangHouseEmblemAggroTests
{
    private const int RubyEmblemItemId = 843;
    private const int DoNotAttackIfItemAbilityId = 185;

    private static Monster Guardian() => new()
    {
        Number = 510,
        Name = "ruby guardian",
        Align = 2, // evil hostile: attacks everyone unless safe-passage applies
        Abilities = { [DoNotAttackIfItemAbilityId] = RubyEmblemItemId },
    };

    [Fact]
    public void Guardian_attacks_player_without_the_emblem()
    {
        var player = new Player { EvilPoints = 0 };
        Assert.True(CombatEngine.ShouldMonsterAggro(Guardian(), player));
    }

    [Fact]
    public void Guardian_grants_safe_passage_when_emblem_carried_in_inventory()
    {
        var player = new Player { EvilPoints = 0 };
        player.Inventory.Add(RubyEmblemItemId);
        Assert.False(CombatEngine.ShouldMonsterAggro(Guardian(), player));
    }

    [Fact]
    public void Guardian_grants_safe_passage_when_emblem_is_equipped()
    {
        var player = new Player { EvilPoints = 0 };
        player.Equipment["worn"] = RubyEmblemItemId;
        Assert.False(CombatEngine.ShouldMonsterAggro(Guardian(), player));
    }

    [Fact]
    public void Carrying_a_different_houses_emblem_does_not_grant_passage()
    {
        var player = new Player { EvilPoints = 0 };
        player.Inventory.Add(1054); // diamond (white house) emblem, not this guardian's
        Assert.True(CombatEngine.ShouldMonsterAggro(Guardian(), player));
    }

    [Fact]
    public void Monster_without_ability_185_is_unaffected_by_emblems()
    {
        var monster = new Monster { Align = 2 }; // no ability 185
        var player = new Player { EvilPoints = 0 };
        player.Inventory.Add(RubyEmblemItemId);
        Assert.True(CombatEngine.ShouldMonsterAggro(monster, player));
    }

    // A guardian with a sure-hit melee attack — used to prove the safe-passage gate at the SWING level
    // (MonsterAttack / the monster swing), which covers the departing free attack and combat rounds,
    // not just on-sight aggro.
    private static MonsterInstance GuardianInstance()
    {
        var instance = new MonsterInstance
        {
            Template = new Monster
            {
                Number = 510,
                Name = "ruby guardian",
                Align = 2,
                Energy = 1000,
                Abilities = { [DoNotAttackIfItemAbilityId] = RubyEmblemItemId },
                Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 1000, Percent = 100, Min = 20, Max = 20 }],
            },
            DisplayName = "ruby guardian",
            CurrentHP = 200,
            MaxHP = 200,
        };
        instance.ResetEnergy();
        instance.PrepareCombatRound();
        return instance;
    }

    private static Player VictimWithHp() => new() { EvilPoints = 0, CurrentHP = 500, MaxHP = 500 };

    [Fact]
    public void Guardian_makes_no_swing_against_a_player_holding_the_emblem()
    {
        var player = VictimWithHp();
        player.Inventory.Add(RubyEmblemItemId);

        var result = CombatEngine.MonsterAttack(GuardianInstance(), player);

        Assert.Equal(0, result.Hits);
        Assert.Equal(0, result.TotalDamage);
        Assert.Equal(500, player.CurrentHP);
    }

    [Fact]
    public void Guardian_makes_no_swing_when_the_emblem_is_worn()
    {
        var player = VictimWithHp();
        player.Equipment["worn"] = RubyEmblemItemId;

        var result = CombatEngine.MonsterAttack(GuardianInstance(), player);

        Assert.Equal(0, result.Hits);
        Assert.Equal(500, player.CurrentHP);
    }

    [Fact]
    public void Guardian_swings_normally_against_a_player_without_the_emblem()
    {
        var player = VictimWithHp();

        var result = CombatEngine.MonsterAttack(GuardianInstance(), player);

        Assert.True(result.Hits > 0, "guardian should attack a player with no emblem");
        Assert.True(player.CurrentHP < 500, "guardian should deal damage with no emblem present");
    }
}
