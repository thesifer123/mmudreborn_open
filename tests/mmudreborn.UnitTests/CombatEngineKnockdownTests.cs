using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class CombatEngineKnockdownTests
{
    [Fact]
    public void Smash_knocks_down_player_when_attacker_score_exceeds_defender_score()
    {
        var attacker = CreatePlayer("SmashAttacker", level: 30, strength: 90, encumbrance: 0, maxEncumbrance: 100);
        var defender = CreatePlayer("SmashDefender", level: 10, strength: 25, encumbrance: 10, maxEncumbrance: 100);
        var result = ExecuteGuaranteedPlayerSmash(attacker, defender);

        Assert.True(defender.IsKnockedDown);
        Assert.Equal(KnockdownKind.Smash, defender.KnockdownKind);
        Assert.Equal(CombatEngine.BuiltInSmashKnockdownTicks, defender.KnockdownTicksRemaining);
        Assert.Contains(result.Messages, line => line.Contains("You smashed SmashDefender to the ground!", StringComparison.Ordinal));
        Assert.Contains(result.TargetMessages, line => line.Contains("You are smashed to the ground!", StringComparison.Ordinal));
    }

    [Fact]
    public void Smash_does_not_knock_down_player_when_defender_score_is_equal_or_higher()
    {
        var attacker = CreatePlayer("SmashAttacker", level: 10, strength: 25, encumbrance: 0, maxEncumbrance: 100);
        var defender = CreatePlayer("SmashDefender", level: 30, strength: 90, encumbrance: 0, maxEncumbrance: 100);
        var result = ExecuteGuaranteedPlayerSmash(attacker, defender);

        Assert.False(defender.IsKnockedDown);
        Assert.DoesNotContain(result.Messages, line => line.Contains("smashed SmashDefender to the ground", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.TargetMessages, line => line.Contains("smashed to the ground", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Smash_knocks_down_monster_when_attacker_score_exceeds_its_resist_score()
    {
        var attacker = CreatePlayer("SmashAttacker", level: 30, strength: 90, encumbrance: 0, maxEncumbrance: 100);
        var monster = CreateMonster("training dummy", magicRes: 10);

        var result = ExecuteGuaranteedMonsterSmash(attacker, monster);

        Assert.True(monster.IsKnockedDown);
        Assert.Equal(KnockdownKind.Smash, monster.KnockdownKind);
        Assert.Equal(CombatEngine.BuiltInSmashKnockdownTicks, monster.KnockdownTicksRemaining);
        Assert.Contains(result.Messages, line => line.Contains("The training dummy is smashed to the floor defenseless!", StringComparison.Ordinal));
        Assert.Contains(result.RoomMessages, line => line.Contains("The training dummy is smashed to the floor defenseless!", StringComparison.Ordinal));
    }

    [Fact]
    public void Smash_does_not_knock_down_monster_whose_resist_score_matches_or_beats_the_attacker()
    {
        var attacker = CreatePlayer("SmashAttacker", level: 10, strength: 25, encumbrance: 0, maxEncumbrance: 100);
        // Resist score is MagicRes + 30 == 35 vs the attacker's Strength + Level == 35: a tie does not knock down.
        var monster = CreateMonster("stone golem", magicRes: 5);

        var result = ExecuteGuaranteedMonsterSmash(attacker, monster);

        Assert.False(monster.IsKnockedDown);
        Assert.DoesNotContain(result.Messages, line => line.Contains("smashed to the floor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.RoomMessages, line => line.Contains("smashed to the floor", StringComparison.OrdinalIgnoreCase));
    }

    // Bug #235: knockdown is part of a damaging hit. A smash that misses (or is dodged) never knocks the
    // monster down or prints the line, even when the smasher's score would win.
    [Fact]
    public void Smash_that_misses_never_knocks_a_monster_down()
    {
        var attacker = CreatePlayer("SmashAttacker", level: 30, strength: 90, encumbrance: 0, maxEncumbrance: 100);
        var monster = CreateMonster("training dummy", magicRes: 10);
        monster.Template.ArmourClass = 100000;
        Item wildWeapon = new() { Number = 1, Name = "training maul", Min = 1, Max = 1, Accy = 0, WeaponType = 0 };

        int misses = 0;
        for (int attempt = 0; attempt < 200 && misses < 20; attempt++)
        {
            monster.ClearKnockdown();
            monster.CurrentHP = monster.MaxHP;
            var result = CombatEngine.BashAttack(attacker, monster, new CharacterClass { CombatLvl = 50 }, wildWeapon,
                attackType: CombatEngine.AttackType.Smash);
            if (result.TotalDamage > 0)
                continue;

            misses++;
            Assert.False(monster.IsKnockedDown);
            Assert.DoesNotContain(result.Messages, line => line.Contains("smashed to the floor", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(result.RoomMessages, line => line.Contains("smashed to the floor", StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(misses > 0, "Expected at least one missed smash within the retry window.");
    }

    [Fact]
    public void Plain_bash_never_knocks_a_monster_down()
    {
        var attacker = CreatePlayer("BashAttacker", level: 30, strength: 90, encumbrance: 0, maxEncumbrance: 100);
        var monster = CreateMonster("training dummy", magicRes: 10);

        var result = CombatEngine.BashAttack(attacker, monster, new CharacterClass { CombatLvl = 50 }, CreateSmashWeapon(),
            attackType: CombatEngine.AttackType.Bash);

        Assert.False(monster.IsKnockedDown);
        Assert.DoesNotContain(result.Messages, line => line.Contains("smashed to the floor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Weapon_hit_queues_percent_spell_proc_when_weapon_has_proc_abilities()
    {
        var attacker = CreatePlayer("ProcAttacker", level: 30, strength: 80, encumbrance: 0, maxEncumbrance: 100);

        CharacterClass cls = new()
        {
            CombatLvl = 50,
        };

        Monster targetMonster = new()
        {
            Number = 1,
            Name = "training dummy",
            Abilities = [],
            Drops = [],
        };

        MonsterInstance defender = new()
        {
            Template = targetMonster,
            DisplayName = "training dummy",
            CurrentHP = 500,
            MaxHP = 500,
        };

        Item procWeapon = new()
        {
            Number = 209,
            Name = "huge darkwood club",
            Min = 10,
            Max = 10,
            Accy = 100000,
            WeaponType = 0,
            Abilities = new Dictionary<int, int>
            {
                [CombatEngine.WeaponSpellProcChanceAbilityId] = 100,
                [CombatEngine.WeaponSpellProcSpellAbilityId] = 318,
            },
        };

        for (int attempt = 0; attempt < 100; attempt++)
        {
            defender.CurrentHP = 500;
            var result = CombatEngine.PlayerAttack(attacker, defender, cls, procWeapon);
            if (result.TotalDamage <= 0)
                continue;

            Assert.Contains(result.TriggeredWeaponProcs, proc => proc.SpellId == 318);
            return;
        }

        throw new Xunit.Sdk.XunitException("Expected a landed weapon hit within the retry window.");
    }

    private static CombatResult ExecuteGuaranteedPlayerSmash(Player attacker, Player defender)
    {
        CharacterClass cls = new()
        {
            CombatLvl = 50,
        };

        Item weapon = new()
        {
            Number = 1,
            Name = "training maul",
            Min = 10,
            Max = 10,
            Accy = 100000,
            WeaponType = 0,
        };

        for (int attempt = 0; attempt < 100; attempt++)
        {
            defender.CurrentHP = 500;
            defender.ClearKnockdown();
            var result = CombatEngine.PlayerAttackPlayer(attacker, defender, cls, weapon, action: PlayerCombatRoundAction.Smash);
            if (result.Hits > 0)
                return result;
        }

        throw new Xunit.Sdk.XunitException("Expected smash to land within the retry window.");
    }

    private static CombatResult ExecuteGuaranteedMonsterSmash(Player attacker, MonsterInstance monster)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            monster.ClearKnockdown();
            monster.CurrentHP = monster.MaxHP;
            var result = CombatEngine.BashAttack(attacker, monster, new CharacterClass { CombatLvl = 50 }, CreateSmashWeapon(),
                attackType: CombatEngine.AttackType.Smash);
            if (result.TotalDamage > 0)
                return result;
        }

        throw new Xunit.Sdk.XunitException("Expected smash to land within the retry window.");
    }

    private static MonsterInstance CreateMonster(string name, int magicRes)
    {
        return new MonsterInstance
        {
            Template = new Monster
            {
                Number = 1,
                Name = name,
                MagicRes = magicRes,
                Abilities = [],
                Drops = [],
            },
            DisplayName = name,
            CurrentHP = 5000,
            MaxHP = 5000,
        };
    }

    // 10 damage so even a weak smasher's strength penalty leaves a damaging hit — knockdown needs one.
    private static Item CreateSmashWeapon() => new()
    {
        Number = 1,
        Name = "training maul",
        Min = 10,
        Max = 10,
        Accy = 100000,
        WeaponType = 0,
    };

    private static Player CreatePlayer(string name, int level, int strength, int encumbrance, int maxEncumbrance)
    {
        return new Player
        {
            Name = name,
            Level = level,
            Strength = strength,
            BaseStrength = strength,
            Health = 50,
            BaseHealth = 50,
            Agility = 50,
            BaseAgility = 50,
            Intellect = 50,
            BaseIntellect = 50,
            Willpower = 50,
            BaseWillpower = 50,
            Charm = 50,
            BaseCharm = 50,
            CurrentHP = 500,
            MaxHP = 500,
            MaxStamina = 1000,
            CurrentEnergy = 1000,
            MaxEncumbrance = maxEncumbrance,
            Encumbrance = encumbrance,
        };
    }
}