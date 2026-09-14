using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class CombatEngineWeaponVerbTests
{
    [Theory]
    [InlineData(false, "You punch training dummy", "UnitStriker punches training dummy")]
    [InlineData(true, "You surprise punch training dummy", "UnitStriker surprise punches training dummy")]
    public void Player_attack_without_weapon_uses_punch_verbs(bool isBackstab, string expectedPlayerText, string expectedRoomText)
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            Stealth = 120,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();

        var result = ExecuteUntil(
            () =>
            {
                // Bare fists draw from the same stamina pool as a weapon (weaponless speed 1200),
                // so the round needs the same energy refill the combat pulse does.
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon: null, messages: null, isBackstab: isBackstab);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains(expectedPlayerText, combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expectedRoomText, combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Player_attack_without_weapon_does_not_use_mystic_punch_damage_bonus()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 0,
        };

        var attacker = new Player
        {
            Name = "Mystic",
            Level = 1,
            // Strength fixed at 50 so the Strength damage bonus is a no-op for this test
            // (max += (50-50)/10 = 0). The test's intent is to verify a non-Mystic without a weapon
            // uses stock weaponless 1-4 damage rather than Mystic's PunchDamage=250 — not to test
            // Strength scaling, which has its own coverage in ApplyStrengthDamageBonus_matches_stock_formula.
            Strength = 50,
            Agility = 0,
            PartyAccuracyModifier = 500,
            PunchDamage = 250,
            MinDamage = 0,
            MaxDamage = 0,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon: null, messages: null);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.InRange(result.TotalDamage, 1, 4);
    }

    [Fact]
    public void Player_attack_without_weapon_applies_stock_strength_damage_bonus()
    {
        // The fighter marshal applies the Strength damage bonus to EVERY fighter, including
        // bare-fist swings. For Str 90 on stock weaponless
        // 1/4 base: max += (90-50)/10 = +4 → 8; min bonus skipped (((90-100)/10)*2 = -2 ≤ 0).
        var cls = new CharacterClass
        {
            CombatLvl = 0,
        };

        var attacker = new Player
        {
            Name = "Brawler",
            Level = 1,
            Strength = 90,
            Agility = 0,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon: null, messages: null);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.InRange(result.TotalDamage, 1, 8);
    }

    [Fact]
    public void Player_attack_player_without_weapon_uses_stock_weaponless_damage_bounds()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 0,
        };

        var attacker = new Player
        {
            Name = "Brawler",
            Level = 1,
            // Strength 50 keeps the Strength damage bonus at zero so the assertion can target
            // the stock weaponless 1-4 baseline cleanly. Strength scaling on weaponless attacks is
            // covered separately by Player_attack_without_weapon_applies_stock_strength_damage_bonus.
            Strength = 50,
            Agility = 0,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var target = new Player
        {
            Name = "Target",
            MaxHP = 100,
            CurrentHP = 100,
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                target.CurrentHP = target.MaxHP;
                return CombatEngine.PlayerAttackPlayer(attacker, target, cls, weapon: null, messages: null);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.InRange(result.TotalDamage, 1, 4);
    }

    [Fact]
    public void Player_attack_with_weapon_resolves_capped_multi_swing_round()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 1,
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                return CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages: null);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.Equal(5, result.Hits + result.Misses);
        // Strength bonus: Str 90 lifts the per-swing max from 15 to 19 (+(90-50)/10).
        // Min stays 10 (((90-100)/10)*2 = -2, gated > 0). Range below reflects the corrected per-swing
        // bounds so the multi-swing capping assertion isn't conflated with damage arithmetic.
        Assert.InRange(result.TotalDamage, result.Hits * 10, result.Hits * 19);
    }

    [Fact]
    public void Player_attack_player_with_weapon_resolves_capped_multi_swing_round()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var target = new Player
        {
            Name = "Target",
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 1,
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                target.CurrentHP = target.MaxHP;
                return CombatEngine.PlayerAttackPlayer(attacker, target, cls, weapon, messages: null);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.Equal(5, result.Hits + result.Misses);
        // The same Str bonus applies in PvP: max 15 -> 19 with Str 90.
        Assert.InRange(result.TotalDamage, result.Hits * 10, result.Hits * 19);
    }

    [Fact]
    public void Player_vs_player_dodge_messages_match_stock_literals()
    {
        // PvP dodge: self "You %s %s who dodges your attack!", target
        // "%s %s at you with %s %s, but you dodge the attack!", room "%s %s at %s with %s %s,
        // but %s dodges!". The weapon's miss-verb ("lunge at") already carries the preposition.
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
        };
        var target = new Player
        {
            Name = "Target",
            MaxHP = 100000,
            CurrentHP = 100000,
            ArmourClass = 0,
            Dodge = 500,            // huge DG dodge rating → post-hit skill-dodge gate fires
            MaxEncumbrance = 100000,
        };
        var weapon = new Item { Number = 100, Name = "quarterstaff", Min = 1, Max = 1, Accy = 500, WeaponType = 1, MissMsg = 200 };
        var messages = new Dictionary<int, RoomMessage> { [200] = new RoomMessage { Number = 200, Line1 = "lunge at" } };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                target.CurrentHP = target.MaxHP;
                return CombatEngine.PlayerAttackPlayer(attacker, target, cls, weapon, messages);
            },
            combat => string.Join('\n', combat.TargetMessages).Contains("dodge the attack", StringComparison.Ordinal));

        Assert.Contains("You lunge at Target who dodges your attack!", string.Join('\n', result.Messages), StringComparison.Ordinal);
        Assert.Contains("UnitStriker lunges at you with a quarterstaff, but you dodge the attack!", string.Join('\n', result.TargetMessages), StringComparison.Ordinal);
        Assert.Contains("UnitStriker lunges at Target with a quarterstaff, but Target dodges!", string.Join('\n', result.RoomMessages), StringComparison.Ordinal);
    }

    [Fact]
    public void Player_vs_player_glance_messages_match_stock_literals()
    {
        // PvP glance (self / target / room):
        //   self:   "Your %s %s hits, but glances off %s armour!"   (verb, targetName, targetPronoun)
        //   target: "%s's %s you hits, but your armour deflects!"   (attackerName, verb)
        //   room:   "%s's %s %s hits, but glances off %s armour!"   (attackerName, verb, targetName, pronoun)
        // The verb is the attacker's first-person token used verbatim in all three —
        // distinct from the vs-monster glance ("…off its armour." with a period).
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
        };
        var target = new Player
        {
            Name = "Target",
            Gender = 0,             // Male → "his"
            MaxHP = 100000,
            CurrentHP = 100000,
            ArmourClass = 0,
            DamageResist = 9990,    // huge DR → a landed hit is absorbed to 0 damage (glance)
            MaxEncumbrance = 100000,
        };
        var weapon = new Item { Number = 100, Name = "quarterstaff", Min = 1, Max = 1, Accy = 500, WeaponType = 1, MissMsg = 200 };
        var messages = new Dictionary<int, RoomMessage> { [200] = new RoomMessage { Number = 200, Line1 = "lunge at" } };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                target.CurrentHP = target.MaxHP;
                return CombatEngine.PlayerAttackPlayer(attacker, target, cls, weapon, messages);
            },
            combat => combat.Hits > 0 && combat.TotalDamage == 0);

        Assert.Contains("Your lunge at Target hits, but glances off his armour!", string.Join('\n', result.Messages), StringComparison.Ordinal);
        Assert.Contains("UnitStriker's lunge at you hits, but your armour deflects!", string.Join('\n', result.TargetMessages), StringComparison.Ordinal);
        Assert.Contains("UnitStriker's lunge at Target hits, but glances off his armour!", string.Join('\n', result.RoomMessages), StringComparison.Ordinal);
    }

    [Fact]
    public void Weapon_attack_crit_chance_includes_quick_and_deadly_bonus()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 1,
        };

        var attacker = new Player
        {
            Level = 1,
            Strength = 65,
            Agility = 50,
            Intellect = 50,
            Charm = 50,
            MaxEncumbrance = 4800,
            Encumbrance = 0,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Speed = 100,
            StrReq = 30,
        };

        Assert.Equal(20, CombatEngine.GetWeaponQuickAndDeadlyBonus(attacker, cls, weapon));
        Assert.Equal(attacker.GetCrits() + 20, CombatEngine.GetWeaponCritChance(attacker, cls, weapon));
    }

    [Fact]
    public void Unarmed_attack_crit_chance_includes_quick_and_deadly_bonus_for_punch()
    {
        // Q&D is computed by the fighter marshal regardless of
        // attack type — once EU<200 and encum<33%, the bonus lands in the crit field
        // (the live crit chance). A high-level Mystic punching at martial-speed 1150 satisfies
        // the EU gate, so unarmed crit must layer Q&D the same way GetWeaponCritChance does.
        var cls = new CharacterClass { CombatLvl = 3 }; // Mystic-tier combat progression
        var attacker = new Player
        {
            Level = 67,
            Strength = 50,
            Agility = 90,
            Charm = 50,
            Intellect = 50,
            Willpower = 50,
            Health = 50,
            MaxEncumbrance = 4800,
            Encumbrance = 0,
        };

        // L67, agi 90, combatLvl 3 → denom = (67*5+45)*240*1500/9000 ≈ 15200
        // EU = 1150*1000/15200 ≈ 75, *0.75 (encum 0%) ≈ 56 → EU<200 triggers Q&D.
        int punchQnD = CombatEngine.GetUnarmedCritChance(attacker, cls, "punch") - attacker.GetCrits();
        Assert.True(punchQnD > 0, $"Expected Q&D > 0 for fast Mystic punch, got {punchQnD}");
        Assert.Equal(attacker.GetCrits() + punchQnD, CombatEngine.GetUnarmedCritChance(attacker, cls, "punch"));
    }

    [Fact]
    public void Unarmed_attack_crit_chance_omits_quick_and_deadly_when_encumbered()
    {
        // Stock gate: encumbrancePercent > 66 zeros out Q&D entirely.
        var cls = new CharacterClass { CombatLvl = 3 };
        var attacker = new Player
        {
            Level = 67,
            Strength = 50,
            Agility = 90,
            Charm = 50,
            Intellect = 50,
            Willpower = 50,
            Health = 50,
            MaxEncumbrance = 1000,
            Encumbrance = 800, // 80% — above the 66% Q&D ceiling
        };

        Assert.Equal(attacker.GetCrits(), CombatEngine.GetUnarmedCritChance(attacker, cls, "punch"));
    }

    [Fact]
    public void Quarterstaff_reference_has_no_quick_and_deadly_bonus_for_attack_or_bash()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 1,
        };

        var attacker = new Player
        {
            Level = 1,
            Strength = 65,
            Agility = 30,
            Intellect = 50,
            Charm = 50,
            Encumbrance = 231,
            MaxEncumbrance = 3744,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var quarterstaff = new Item
        {
            Speed = 1200,
            StrReq = 30,
        };

        Assert.Equal(0, CombatEngine.GetWeaponQuickAndDeadlyBonus(attacker, cls, quarterstaff));
        Assert.Equal(0, CombatEngine.GetWeaponQuickAndDeadlyBonus(attacker, cls, quarterstaff, isBashing: true));
    }

    [Fact]
    public void Player_attack_with_fast_weapon_uses_multiple_swings_in_one_round()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 3,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 67,
            Strength = 121,
            Agility = 130,
            Intellect = 50,
            Charm = 50,
            Encumbrance = 1249,
            MaxEncumbrance = 6564,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 0,
            WeaponType = 2,
            Speed = 67,
            StrReq = 0,
        };

        var monster = CreateMonster();
        attacker.PrepareCombatRound();
        var result = CombatEngine.PlayerAttack(attacker, monster, cls, weapon);

        Assert.Equal(5, result.Hits + result.Misses);
    }

    [Fact]
    public void Bash_attack_costs_a_full_pool_and_lands_one_swing_per_round()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 1,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 1,
            Strength = 65,
            Agility = 30,
            Intellect = 50,
            Charm = 50,
            Encumbrance = 231,
            MaxEncumbrance = 3744,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var quarterstaff = new Item
        {
            Min = 2,
            Max = 12,
            Accy = 0,
            WeaponType = 1,
            Speed = 1200,
            StrReq = 30,
        };

        // Bash EU = normal 677 ×2 = 1354, ceiled to maxStamina (1000) by the [200, maxStamina]
        // clamp. A round refills the pool to one cap (1000), so each round affords exactly one bash.
        attacker.PrepareCombatRound();
        var firstRound = CombatEngine.BashAttack(attacker, CreateMonster(), cls, quarterstaff);
        attacker.PrepareCombatRound();
        var secondRound = CombatEngine.BashAttack(attacker, CreateMonster(), cls, quarterstaff);

        Assert.Equal(1, firstRound.Hits + firstRound.Misses);
        Assert.Equal(1, secondRound.Hits + secondRound.Misses);
    }

    [Fact]
    public void Mystic_punch_uses_mystic_punch_damage_bonus()
    {
        // Stock adds the per-type damage ability (92 = PunchDmg) to punch min/max. Provide it via the
        // class and recalc so the unarmed bounds are computed the normal way (not by poking a field).
        var race = new Race { Number = 99 };
        var cls = new CharacterClass
        {
            CombatLvl = 0,
            Abilities = new Dictionary<int, int> { [92] = 250 },
        };

        var attacker = new Player
        {
            Name = "Mystic",
            Level = 1,
            Strength = 50,
            Agility = 0,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };
        attacker.RecalculateStats(race, cls, new InMemoryGameDatabase());

        var monster = CreateMonster();

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.MysticAttack(attacker, monster, cls, "punch");
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.True(result.TotalDamage >= 250, $"Expected explicit punch to use the Mystic punch bonus, but got {result.TotalDamage} damage.");
    }

    [Fact]
    public void Player_attack_with_weapon_hit_message_still_uses_message_table_verb()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();
        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 1,
            HitMsg = 77,
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [77] = new RoomMessage
            {
                Number = 77,
                Line1 = "impale",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon, messages);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains("You impale training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnitStriker impales training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("You punch training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Weapon_hit_room_message_uses_line3_third_person_possessive_not_your()
    {
        // Regression: the "nexus spear" bug. A weapon whose HitMsg carries a possessive phrase
        // ("hurl your nexus spear at") must render the ROOM broadcast from Line3 ("hurls THEIR nexus
        // spear at"), NOT a conjugated Line1 ("hurls YOUR nexus spear at"). A bystander seeing "your"
        // makes Megamud flag the line as PVP against the viewer.
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = new Player
        {
            Name = "Scottt",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();
        var weapon = new Item { Min = 10, Max = 15, Accy = 500, WeaponType = 3, HitMsg = 1208 };
        var messages = new Dictionary<int, RoomMessage>
        {
            [1208] = new RoomMessage
            {
                Number = 1208,
                Line1 = "hurl your nexus spear at",
                Line2 = "hurls the nexus spear at",
                Line3 = "hurls their nexus spear at",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon, messages);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        string selfMessages = string.Join('\n', result.Messages);
        string roomMessages = string.Join('\n', result.RoomMessages);

        // Self view keeps the 2nd-person "your" (Line1).
        Assert.Contains("You hurl your nexus spear at training dummy", selfMessages, StringComparison.Ordinal);
        // Room view is the authored 3rd-person "their" (Line3) — never "your".
        Assert.Contains("Scottt hurls their nexus spear at training dummy", roomMessages, StringComparison.Ordinal);
        Assert.DoesNotContain("your", roomMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pvp_weapon_hit_uses_per_audience_possessive_lines()
    {
        // Same triple in PvP: self = Line1 ("your"), victim = Line2 ("the"), room = Line3 ("their").
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = new Player
        {
            Name = "Scottt",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };
        var target = new Player { Name = "Victim", MaxHP = 100000, CurrentHP = 100000 };
        var weapon = new Item { Min = 10, Max = 15, Accy = 500, WeaponType = 3, HitMsg = 1208 };
        var messages = new Dictionary<int, RoomMessage>
        {
            [1208] = new RoomMessage
            {
                Number = 1208,
                Line1 = "hurl your nexus spear at",
                Line2 = "hurls the nexus spear at",
                Line3 = "hurls their nexus spear at",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                target.CurrentHP = target.MaxHP;
                return CombatEngine.PlayerAttackPlayer(attacker, target, cls, weapon, messages);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        Assert.Contains("You hurl your nexus spear at Victim", string.Join('\n', result.Messages), StringComparison.Ordinal);
        Assert.Contains("Scottt hurls the nexus spear at you", string.Join('\n', result.TargetMessages), StringComparison.Ordinal);
        Assert.Contains("Scottt hurls their nexus spear at Victim", string.Join('\n', result.RoomMessages), StringComparison.Ordinal);
    }

    [Fact]
    public void Backstab_attack_with_weapon_hit_message_uses_standard_weapon_verb_path()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            Stealth = 120,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();
        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 2,
            HitMsg = 77,
            // Backstab-capable (ability 116): qualifies for the full backstab — surprise verb + multiplier.
            Abilities = new Dictionary<int, int> { [CombatEngine.BackstabAttackValueAbilityId] = 0 },
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [77] = new RoomMessage
            {
                Number = 77,
                Line1 = "impale",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon, messages, isBackstab: true);
            },
            combat => combat.Hits > 0 && combat.Crits == 0);

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains("You surprise impale training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnitStriker surprise impales training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backstab", combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backstab_attack_miss_uses_standard_weapon_miss_verb_path()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 0,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 1,
            Strength = 10,
            Agility = 0,
            Stealth = 0,
            PartyAccuracyModifier = 0,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster(armourClass: 200, bsDefense: 200);
        var weapon = new Item
        {
            Min = 1,
            Max = 2,
            Accy = 0,
            WeaponType = 2,
            MissMsg = 78,
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [78] = new RoomMessage
            {
                Number = 78,
                Line1 = "swipe at",
            },
        };

        var result = ExecuteUntil(
            () => CombatEngine.PlayerAttack(attacker, monster, cls, weapon, messages, isBackstab: true),
            combat => combat.Misses > 0);

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains("You swipe at training dummy!", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnitStriker swipes at training dummy!", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backstab attempt fails", combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backstab_attack_uses_stock_backstab_damage_profile()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 10,
            Strength = 50,
            Agility = 200,
            Stealth = 50,
            BSMinDamage = 2,
            BSMaxDamage = 4,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 3,
            Max = 6,
            WeaponType = 2,
            // Backstab-capable (ability 116) so the full stock backstab damage profile applies.
            Abilities = new Dictionary<int, int> { [CombatEngine.BackstabAttackValueAbilityId] = 0 },
        };

        var result = ExecuteUntil(
            () => CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages: null, isBackstab: true),
            combat => combat.Hits > 0);

        // Backstab (type 4) applies NO pre-roll damage% (the +10% the old code added was wrong).
        // Profile min = (6+20+5+2)*110/100 = 36, max = (12+20+5+4)*110/100 = 45. Monster DR = 0.
        Assert.InRange(result.TotalDamage, 36, 45);
        Assert.True(result.TotalDamage > weapon.Max);
    }

    private static Player MakeNonBackstabStabber() => new()
    {
        Name = "UnitStriker",
        Level = 10,
        Strength = 50,
        Agility = 200,
        Stealth = 50,
        BSMinDamage = 2,
        BSMaxDamage = 4,
        PartyAccuracyModifier = 500,
        MaxHP = 100,
        CurrentHP = 100,
    };

    [Fact]
    public void Backstab_with_non_backstab_weapon_does_normal_damage_when_surpriseround_off()
    {
        // SURPRISEROUND OFF (surpriseRoundEnabled:false, the default): a non-backstab weapon (no ability 116)
        // deals NORMAL weapon damage — no multiplier, no "surprise" verb. (In live play the dispatcher
        // routes this to a plain normal Attack; the direct backstab-swing call here exercises the same
        // normal-damage fallback.) Same attacker/weapon as the full-backstab test (which lands 36-45).
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = MakeNonBackstabStabber();
        var weapon = new Item { Min = 3, Max = 6, WeaponType = 2, HitMsg = 77 }; // no backstab ability
        var messages = new Dictionary<int, RoomMessage> { [77] = new RoomMessage { Number = 77, Line1 = "stab" } };

        var result = ExecuteUntil(
            () => CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages, isBackstab: true, surpriseRoundEnabled: false),
            combat => combat.Hits > 0);

        // Strength 50 → ApplyStrengthDamageBonus(3,6,50) = (3,6); crit (allowed here) tops at 6*4 = 24 —
        // either way FAR below the 36+ full-backstab floor. So it is unambiguously normal weapon damage.
        Assert.True(result.TotalDamage < 36, $"expected normal damage, got {result.TotalDamage}");
        string combined = string.Join('\n', result.Messages.Concat(result.RoomMessages));
        Assert.DoesNotContain("surprise", combined, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsBackstab);
    }

    [Fact]
    public void Backstab_with_non_backstab_weapon_does_backstab_damage_when_surpriseround_on()
    {
        // SURPRISEROUND ON (surpriseRoundEnabled:true): the non-backstab weapon gets a FULL surprise
        // round — backstab DAMAGE (weaponMax*2 + level/stealth + 117/118, level-scaled) and the
        // "surprise" verb. Same attacker/weapon as the OFF test above, which lands < 36; ON must clear
        // the 36+ backstab floor.
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = MakeNonBackstabStabber();
        var weapon = new Item { Min = 3, Max = 6, WeaponType = 2, HitMsg = 77 }; // no backstab ability
        var messages = new Dictionary<int, RoomMessage> { [77] = new RoomMessage { Number = 77, Line1 = "stab" } };

        var result = ExecuteUntil(
            () => CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages, isBackstab: true, surpriseRoundEnabled: true),
            combat => combat.Hits > 0);

        // Backstab damage floor (min = weaponMin*2 + Level*2 + Stealth/10 + BSMin, ×(Level+100)/100):
        // (3*2 + 20 + 5 + 2) * 1.1 = 36.3 → 36. Crit is suppressed for a full backstab.
        Assert.True(result.TotalDamage >= 36, $"expected backstab damage, got {result.TotalDamage}");
        string combined = string.Join('\n', result.Messages.Concat(result.RoomMessages));
        Assert.Contains("surprise", combined, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.IsBackstab);
    }

    [Fact]
    public void CalculateAttack_allows_crits_for_backstab_only_with_the_override()
    {
        // AttackType.Backstab suppresses crits; the non-backstab-weapon surprise (e4=5 normal damage)
        // re-enables them via allowCritOverride. critChance 100 makes every landed hit crit when allowed.
        for (int i = 0; i < 100; i++)
        {
            var calc = CombatEngine.CalculateAttack(200, 0, 0, 10, 10, 100, CombatEngine.AttackType.Backstab);
            Assert.False(calc.IsCrit); // suppressed regardless of hit/miss
        }

        bool sawCrit = false;
        for (int i = 0; i < 100 && !sawCrit; i++)
        {
            var calc = CombatEngine.CalculateAttack(200, 0, 0, 10, 10, 100, CombatEngine.AttackType.Backstab, allowCritOverride: true);
            sawCrit |= !calc.Missed && calc.IsCrit;
        }
        Assert.True(sawCrit, "override should permit crits on a backstab-typed swing");
    }

    [Fact]
    public void Backstab_hit_chance_is_accuracy_minus_armour_class_with_no_penalty()
    {
        // Attack type 4: backstab uses hitChance = AV - AC with NO accuracy modifier
        // (the -15/-25 penalties belong to bash/smash, not backstab). 60 - 20 = 40.
        var calc = CombatEngine.CalculateAttack(60, 20, 0, 10, 20, 0, CombatEngine.AttackType.Backstab);

        Assert.Equal(40, calc.HitChance);
    }

    [Fact]
    public void Player_attack_that_hits_for_zero_damage_uses_glancing_blow_message()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();
        monster.Template.DamageResist = 999;

        var weapon = new Item
        {
            Min = 1,
            Max = 1,
            Accy = 500,
            WeaponType = 0,
            MissMsg = 78,
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [78] = new RoomMessage
            {
                Number = 78,
                Line1 = "swing at",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon, messages: messages);
            },
            combat => combat.Hits > 0 && combat.TotalDamage == 0);

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains("Your swing at training dummy hits, but glances off its armour.", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnitStriker's swing at training dummy hits, but glances off its armour.", combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Backstab_attack_never_records_critical_hits()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 250,
            Stealth = 250,
            CriticalHitBonus = 10000,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            WeaponType = 2,
        };

        bool sawBackstabHit = false;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var result = CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages: null, isBackstab: true);
            Assert.Equal(0, result.Crits);
            string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));
            Assert.DoesNotContain("critically backstab", combinedMessages, StringComparison.OrdinalIgnoreCase);

            sawBackstabHit |= result.Hits > 0;
        }

        Assert.True(sawBackstabHit);
    }

    [Fact]
    public void Normal_weapon_attack_still_records_critical_hits()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 250,
            Intellect = 250,
            Charm = 250,
            CriticalHitBonus = 10000,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 2,
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                return CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages: null);
            },
            combat => combat.Hits > 0 && combat.Crits > 0);

        Assert.True(result.Crits > 0);
        Assert.Contains("critically", string.Join('\n', result.Messages), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Monster_attack_dodge_template_fills_armed_verb_and_weapon_name()
    {
        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "kobold thief",
                Weapon = 67,
                Energy = 1000,
                HP = 100,
                Attacks =
                [
                    new MonsterAttack
                    {
                        // Accuracy >= 9 so the post-hit skill-dodge gate can fire (AV<9 forces it to 0).
                        Type = 1,
                        Accuracy = 100,
                        Percent = 100,
                        Min = 1,
                        Max = 8,
                        HitMessageId = 41,
                        DodgeMessageId = 8297,
                        MissMessageId = 8310,
                    },
                ],
            },
            DisplayName = "large kobold thief",
            MaxHP = 100,
            CurrentHP = 100,
        };
        var player = new Player
        {
            Name = "Scottt",
            MaxHP = 10000,
            CurrentHP = 10000,
            // Low AC → the to-hit roll lands, so the swing reaches the post-hit gate; a huge DG dodge
            // rating then forces a genuine skill-DODGE (outcome 3), the only outcome that renders the
            // "...but you dodge!" template. (A to-hit miss now renders the miss message instead.)
            ArmourClass = 0,
            Dodge = 500,
            MaxEncumbrance = 100000,
        };
        var items = new Dictionary<int, Item>
        {
            [67] = new Item
            {
                Number = 67,
                Name = "shortsword",
                WeaponType = 2,
            },
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [41] = new RoomMessage
            {
                Number = 41,
                Line1 = "The %s stabs you for %d damage!",
                Line2 = "The %s stabs %s for %s damage!",
            },
            [8297] = new RoomMessage
            {
                Number = 8297,
                Line3 = "The %s %s you with their %s, but you dodge!",
            },
            [8310] = new RoomMessage
            {
                Number = 8310,
                Line1 = "The %s %s %s with their %s, but %s dodges!",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                monster.ResetEnergy();
                monster.PrepareCombatRound();
                return CombatEngine.MonsterAttack(monster, player, items, messages);
            },
            combat => string.Join('\n', combat.Messages.Concat(combat.RoomMessages)).Contains("dodges", StringComparison.OrdinalIgnoreCase));

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains("The large kobold thief stabs you with their shortsword, but you dodge!", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The large kobold thief stabs Scottt with their shortsword, but Scottt dodges!", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("with their ,", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("kobold thief Scottt with", combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bash_attack_with_weapon_hit_message_uses_standard_weapon_verb_path()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = CreateMonster();
        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 1,
            HitMsg = 77,
        };
        var messages = new Dictionary<int, RoomMessage>
        {
            [77] = new RoomMessage
            {
                Number = 77,
                Line1 = "impale",
            },
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                monster.CurrentHP = monster.MaxHP;
                return CombatEngine.BashAttack(attacker, monster, cls, weapon, messages);
            },
            combat => combat.Hits > 0);

        string combinedMessages = string.Join('\n', result.Messages.Concat(result.RoomMessages));

        Assert.Contains("You impale training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UnitStriker impales training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("You bash training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("smashed training dummy", combinedMessages, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bash_attack_never_records_critical_hits()
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            Intellect = 500,
            Charm = 500,
            CriticalHitBonus = 500,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = 1,
        };

        for (int attempt = 0; attempt < 40; attempt++)
        {
            var monster = CreateMonster();
            attacker.PrepareCombatRound();
            var result = CombatEngine.BashAttack(attacker, monster, cls, weapon);
            Assert.Equal(0, result.Crits);
        }
    }

    [Theory]
    [InlineData(0, new[] { "pound", "smash", "crush", "clobber", "slam", "bludgeon" }, new[] { "whap", "beat", "strike", "thwack" })]
    [InlineData(2, new[] { "slash", "hack", "slice", "cut", "impale", "cleave" }, new[] { "pound", "smash", "crush", "bludgeon" })]
    public void Message_less_weapon_fallback_uses_correct_weapon_type_family(int weaponType, string[] expectedFamily, string[] unexpectedFamily)
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "UnitStriker",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var weapon = new Item
        {
            Min = 10,
            Max = 15,
            Accy = 500,
            WeaponType = weaponType,
            HitMsg = 0,
        };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                return CombatEngine.PlayerAttack(attacker, CreateMonster(), cls, weapon, messages: null);
            },
            combat => combat.Hits > 0);

        string hitMessages = string.Join('\n', result.Messages);

        Assert.True(
            expectedFamily.Any(verb => hitMessages.Contains(verb, StringComparison.OrdinalIgnoreCase)),
            $"Expected fallback weapon type {weaponType} to use one of [{string.Join(", ", expectedFamily)}] but got:{Environment.NewLine}{hitMessages}");
        Assert.True(
            unexpectedFamily.All(verb => !hitMessages.Contains(verb, StringComparison.OrdinalIgnoreCase)),
            $"Expected fallback weapon type {weaponType} not to use the wrong verb family, but got:{Environment.NewLine}{hitMessages}");
    }

    private static CombatResult ExecuteUntilHit(Func<CombatResult> attack)
    {
        return ExecuteUntil(attack, result => result.Hits > 0);
    }

    private static CombatResult ExecuteUntil(Func<CombatResult> attack, Func<CombatResult, bool> predicate)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var result = attack();
            if (predicate(result))
                return result;
        }

        throw new Xunit.Sdk.XunitException("Expected combat to produce the requested outcome during the retry window.");
    }

    private static MonsterInstance CreateMonster(int armourClass = 0, int bsDefense = 0)
    {
        return new MonsterInstance
        {
            Template = new Monster
            {
                Name = "dummy",
                Align = 2,
                ArmourClass = armourClass,
                BSDefense = bsDefense,
                DamageResist = 0,
                HP = 1000,
                Drops = [],
            },
            DisplayName = "training dummy",
            MaxHP = 1000,
            CurrentHP = 1000,
        };
    }
}
