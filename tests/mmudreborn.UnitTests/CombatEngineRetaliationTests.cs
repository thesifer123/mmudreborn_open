using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Retaliation = combat ability 72: spike-shield / thorns. When a DEFENDER takes a damaging
// melee hit, every worn item AND active spell granting ability 72 rolls separately for 1..AbilVal
// damage to the ATTACKER (a shield spell whose AbilVal is 0 uses its cast level). Flavor comes from
// the source's ability-137 message id.
//
public sealed class CombatEngineRetaliationTests
{
    private const int RetaliationAbility = 72;
    private const int RetaliationMessageAbility = 137;

    private static RoomMessage SpikeMessage(int number) => new()
    {
        Number = number,
        Line1 = "The shield spike stabs %s for %d damage!",   // defender (wearer) view
        Line2 = "The shield spike stabs you for %d damage!",  // attacker view
        Line3 = "The shield spike stabs %s for %d damage!",   // room view
    };

    [Fact]
    public void BuildRetaliationSources_reads_worn_items_and_active_spells_with_castlevel_fallback()
    {
        var db = new InMemoryGameDatabase();
        db.Items[1] = new Item { Number = 1, Name = "spiked shield", Abilities = new() { [RetaliationAbility] = 3, [RetaliationMessageAbility] = 3051 } };
        // shockshield carries ability 72 with value 0 → the source magnitude falls back to cast level.
        db.Spells[64] = new GameSpell { Number = 64, Name = "shockshield", Abilities = new() { [RetaliationAbility] = 0 } };
        db.Spells[405] = new GameSpell { Number = 405, Name = "chaos shield", Abilities = new() { [RetaliationAbility] = 10 } };

        var player = new Player
        {
            Equipment = new() { ["shield"] = 1 },
            ActiveSpells =
            {
                new ActiveSpell { SpellId = 64, CastLevel = 7 },
                new ActiveSpell { SpellId = 405, CastLevel = 99 },
            },
        };

        var sources = CombatEngine.BuildRetaliationSources(player, db);

        // item=3, shockshield=castLevel(7) since its AbilVal is 0, chaos shield=10.
        Assert.Equal(new[] { 3, 7, 10 }, sources.Select(s => s.Magnitude).OrderBy(x => x));
    }

    [Fact]
    public void Monster_attacking_a_spiked_player_takes_retaliation_and_player_sees_the_spike_line()
    {
        var db = new InMemoryGameDatabase();
        db.Items[60] = new Item { Number = 60, Name = "spiked shield", Abilities = new() { [RetaliationAbility] = 5, [RetaliationMessageAbility] = 3051 } };
        db.Messages[3051] = SpikeMessage(3051);

        var player = new Player
        {
            Name = "Tank",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Agility = 0,
            Dodge = 0,
            DamageResist = 0,
            Equipment = new() { ["shield"] = 60 },
        };
        var sources = CombatEngine.BuildRetaliationSources(player, db);
        Assert.Single(sources);

        bool exercised = false;
        for (int attempt = 0; attempt < 200 && !exercised; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            var monster = new MonsterInstance
            {
                Template = new Monster
                {
                    Name = "rat",
                    Energy = 1000,
                    Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1 }],
                },
                DisplayName = "rat",
                CurrentHP = 1000,
                MaxHP = 1000,
            };
            monster.ResetEnergy();
            monster.PrepareCombatRound();

            var result = CombatEngine.MonsterAttack(monster, player, db.Items, db.Messages, null, sources);
            if (result.Hits == 0)
                continue;

            exercised = true;
            // 1..5 retaliation per landing swing → the attacking monster lost HP.
            Assert.True(monster.CurrentHP < 1000, $"Spiked defender should damage the attacker; HP={monster.CurrentHP}");
            // The wearer (current player) sees their own spike line (message Line1, %s = monster name).
            Assert.Contains(result.Messages, m => m.Contains("shield spike stabs rat", StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(exercised, "Expected at least one monster hit to drive retaliation.");
    }

    [Fact]
    public void Player_attacking_a_spiked_monster_takes_retaliation_and_attacker_sees_the_spike_line()
    {
        var db = new InMemoryGameDatabase();
        db.Messages[3051] = SpikeMessage(3051);
        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "spiked golem",
                Abilities = new() { [RetaliationAbility] = 5, [RetaliationMessageAbility] = 3051 },
                HP = 1_000_000,
                ArmourClass = 0,
                BSDefense = 0,
            },
            DisplayName = "spiked golem",
            CurrentHP = 1_000_000,
            MaxHP = 1_000_000,
        };
        var sources = CombatEngine.BuildRetaliationSources(monster, db);
        Assert.Single(sources);

        var cls = new CharacterClass { CombatLvl = 20 };
        var player = new Player
        {
            Name = "Hero",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 1000,
            MaxHP = 5000,
            CurrentHP = 5000,
            CurrentEnergy = 1000,
        };
        var weapon = new Item { Min = 5, Max = 5, Accy = 1000, WeaponType = 1 };

        bool exercised = false;
        for (int attempt = 0; attempt < 200 && !exercised; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            player.PrepareCombatRound();

            var result = CombatEngine.PlayerAttack(player, monster, cls, weapon, db.Messages, db: db, defenderRetaliation: sources);
            if (result.Hits == 0)
                continue;

            exercised = true;
            // The attacking player took spike damage (monster stays alive — 1e6 HP vs 5 dmg/swing).
            Assert.True(player.CurrentHP < player.MaxHP, $"Spiked monster should damage the attacking player; HP={player.CurrentHP}");
            // The attacker sees the "stabs you" line (message Line2).
            Assert.Contains(result.Messages, m => m.Contains("shield spike stabs you", StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(exercised, "Expected at least one player hit to drive retaliation.");
    }

    [Fact]
    public void Retaliation_without_a_flavor_message_falls_back_to_generic_line_for_player_attacker()
    {
        var db = new InMemoryGameDatabase();
        // Ability 72 but no ability-137 message id → the attacker sees the generic "You took N damage!".
        var monster = new MonsterInstance
        {
            Template = new Monster { Name = "thornbeast", Abilities = new() { [RetaliationAbility] = 4 }, HP = 1_000_000, ArmourClass = 0, BSDefense = 0 },
            DisplayName = "thornbeast",
            CurrentHP = 1_000_000,
            MaxHP = 1_000_000,
        };
        var sources = CombatEngine.BuildRetaliationSources(monster, db);

        var cls = new CharacterClass { CombatLvl = 20 };
        var player = new Player { Name = "Hero", Level = 50, Strength = 90, Agility = 90, PartyAccuracyModifier = 1000, MaxHP = 5000, CurrentHP = 5000, CurrentEnergy = 1000 };
        var weapon = new Item { Min = 5, Max = 5, Accy = 1000, WeaponType = 1 };

        bool exercised = false;
        for (int attempt = 0; attempt < 200 && !exercised; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            player.PrepareCombatRound();
            var result = CombatEngine.PlayerAttack(player, monster, cls, weapon, db.Messages, db: db, defenderRetaliation: sources);
            if (result.Hits == 0)
                continue;

            exercised = true;
            Assert.Contains(result.Messages, m => m.Contains("You took", StringComparison.OrdinalIgnoreCase) && m.Contains("damage", StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(exercised, "Expected at least one player hit to drive retaliation.");
    }

    [Fact]
    public void No_retaliation_sources_leave_the_attacking_monster_unharmed()
    {
        var player = new Player { Name = "Target", CurrentHP = 5000, MaxHP = 5000, ArmourClass = 0, Agility = 0, Dodge = 0, DamageResist = 0 };

        bool exercised = false;
        for (int attempt = 0; attempt < 100 && !exercised; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            var monster = new MonsterInstance
            {
                Template = new Monster { Name = "rat", Energy = 1000, Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1 }] },
                DisplayName = "rat",
                CurrentHP = 1000,
                MaxHP = 1000,
            };
            monster.ResetEnergy();
            monster.PrepareCombatRound();

            // No defenderRetaliation argument → the attacker must be untouched.
            var result = CombatEngine.MonsterAttack(monster, player);
            if (result.Hits == 0)
                continue;

            exercised = true;
            Assert.Equal(1000, monster.CurrentHP);
        }

        Assert.True(exercised, "Expected at least one monster hit.");
    }
}
