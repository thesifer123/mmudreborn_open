using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class PlayerDerivedStatsTests
{
    [Fact]
    public void RecalculateStats_uses_stock_v111p_secondary_formulas()
    {
        var race = new Race
        {
            Number = 99,
            Abilities = new Dictionary<int, int>
            {
                [27] = 5,
                [36] = 4,
                [77] = 3,
                [96] = 10,
                [102] = 0,
            },
        };
        var cls = new CharacterClass
        {
            MageryType = 1,
            MageryLvl = 3,
            Abilities = new Dictionary<int, int>
            {
                [34] = 25,
                [35] = 1,
                [37] = 11,
                [38] = 12,
                [39] = 7,
                [40] = 8,
                [58] = 10,
                [69] = 5,
                [70] = 6,
                [103] = 0,
                [179] = 13,
                [180] = 14,
            },
        };
        var player = new Player
        {
            Level = 16,
            Intellect = 80,
            Willpower = 60,
            Strength = 120,
            Agility = 70,
            Health = 50,
            Charm = 90,
            CurrentHP = 100,
        };

        player.RecalculateStats(race, cls, new InMemoryGameDatabase());

        Assert.Equal(107, player.MaxMana);
        Assert.Equal(103, player.SpellCasting);
        // Secondary stats: Perception =
        // (5*Int+2*Will+Chr)/8 + ability77 ONLY. (400+120+90)/8 = 76, + ability77=3 = 79.
        // The race's Stealth(27)=5 goes to Stealth, never to Perception —
        // the 2026-06-23 audit (M2) folded it in here by misreading a stale return-value artifact,
        // which drove plate-wearing characters to negative Perception.
        Assert.Equal(79, player.Perception);
        Assert.Equal(69, player.MagicResist);
        Assert.Equal(108, player.Stealth);
        Assert.Equal(107, player.Thievery);
        Assert.Equal(128, player.Traps);
        Assert.Equal(110, player.Picklocks);
        Assert.Equal(125, player.Tracking);
        Assert.Equal(16, player.GetCrits());
        // GetDodge() returns the cached Dodge stat (42) plus the stock light-encumbrance bonus
        // : with Encumbrance=0 (the test never sets it) the bonus
        // peaks at +10. Cached Dodge field itself is still 42; the +10 is added dynamically.
        Assert.Equal(52, player.GetDodge());
        Assert.Equal(42, player.Dodge);
        // Secondary stats: MartialArts =
        // ability 34 (dodge) + baseCrit + critBonus + Charm/10 + Agility/5 + Level/5, then (+Level)*2
        // with jumpkick. The dodge result IS used (MOV ECX,EAX at 0041acdc) and baseCrit is added once
        // — some readings mis-render this as a discarded dodge + 2*baseCrit. dodge=25, baseCrit=6, critBonus=10,
        // Chm/10=9, Agl/5=14, Level/5=3 => 67, then (67+16)*2 = 166.
        Assert.Equal(166, player.MartialArts);
        Assert.Equal(7128, player.MaxEncumbrance);
    }

    [Fact]
    public void Stealth_penalties_from_armour_and_carried_gear_never_touch_Perception()
    {
        // Regression for the "-105 Perception" report: worn plate carries a big negative Stealth(27)
        // and CARRIED non-worn items (log raft -125, wooden skiff -100, large black gem -200) feed the
        // same ability pool through the stock inventory loop. Folding 27 into Perception (the retracted
        // 2026-06-23 audit M2) drove plate-wearing characters deeply negative. The
        // secondary-stat pass adds ability 77 only; ability 27 lands on Stealth.
        var db = new InMemoryGameDatabase();
        db.Items[1] = new Item { Number = 1, Name = "full plate corselet", ItemType = 2, Worn = 3, Abilities = new Dictionary<int, int> { [27] = -18 } };
        db.Items[2] = new Item { Number = 2, Name = "horned helmet", ItemType = 2, Worn = 2, Abilities = new Dictionary<int, int> { [77] = -5 } };
        db.Items[3] = new Item { Number = 3, Name = "log raft", ItemType = 2, Worn = 0, Abilities = new Dictionary<int, int> { [27] = -125 } };

        var race = new Race { Number = 99 };
        var cls = new CharacterClass { Number = 99 };
        var player = new Player
        {
            Level = 15,
            Intellect = 30,
            Willpower = 40,
            Charm = 40,
            Strength = 40,
            Agility = 40,
            Health = 40,
            CurrentHP = 100,
        };
        player.Equipment["worn-3"] = 1;
        player.Equipment["worn-2"] = 2;
        player.Inventory.Add(3);

        player.RecalculateStats(race, cls, db);

        // (5*30 + 2*40 + 40)/8 = 33, + ability77(-5) = 28. The -143 of ability 27 is invisible here.
        Assert.Equal(28, player.Perception);
        // Non-stealther: Stealth is hard-zeroed and the ability-27 pool is never applied.
        Assert.Equal(0, player.Stealth);
    }

    [Fact]
    public void RecalculateStats_folds_av_bonus_abilities_into_accuracy()
    {
        // The fighter marshal adds the AV-bonus accumulator (abilities 22/105/106,
        // =max across sources) to the player's attack value. e.g. Dark-Elf grants ability 106 = +3.
        var race = new Race { Number = 99, Abilities = new Dictionary<int, int> { [106] = 3 } };
        var cls = new CharacterClass { CombatLvl = 5 };
        var player = new Player { Level = 10, Strength = 60, Agility = 60, Charm = 60, Intellect = 60, Willpower = 60, Health = 60 };

        player.RecalculateStats(race, cls, new InMemoryGameDatabase());

        Assert.Equal(3, player.AvAbility);

        int withBonus = player.GetBaseAccuracy(cls.CombatLvl);
        player.AvAbility = 0;
        Assert.Equal(withBonus, player.GetBaseAccuracy(cls.CombatLvl) + 3);
    }

    [Fact]
    public void RecalculateStats_applies_and_removes_active_buff_spell_abilities()
    {
        var db = new InMemoryGameDatabase();
        // Buff granting +3 AC (ability 2 = Alter Defence Value).
        // Ability 2 adds the raw value to the fighter-DV
        // accumulator, so ACAbility is in display scale (the +3 lands as +3).
        db.Spells[500] = new GameSpell { Number = 500, Abilities = new Dictionary<int, int> { [2] = 3 } };

        var race = new Race { Number = 1 };
        var cls = new CharacterClass { CombatLvl = 1 };
        var player = new Player { Level = 10, Strength = 50, Agility = 50, Charm = 50, Intellect = 50, Willpower = 50, Health = 50 };

        player.RecalculateStats(race, cls, db);
        int acBase = player.ACAbility;

        Assert.True(player.AddOrRefreshActiveSpell(spellId: 500, castLevel: 10, duration: 5));
        player.RecalculateStats(race, cls, db);
        Assert.Equal(acBase + 3, player.ACAbility);

        Assert.True(player.RemoveActiveSpell(500));
        player.RecalculateStats(race, cls, db);
        Assert.Equal(acBase, player.ACAbility);
    }

    [Fact]
    public void Active_buff_with_zero_ability_value_uses_cast_level()
    {
        var db = new InMemoryGameDatabase();
        db.Spells[501] = new GameSpell { Number = 501, Abilities = new Dictionary<int, int> { [2] = 0 } };
        var race = new Race { Number = 1 };
        var cls = new CharacterClass { CombatLvl = 1 };
        var player = new Player { Level = 10, Strength = 50, Agility = 50, Charm = 50, Intellect = 50, Willpower = 50, Health = 50 };

        player.RecalculateStats(race, cls, db);
        int acBase = player.ACAbility;
        player.AddOrRefreshActiveSpell(501, castLevel: 7, duration: 5);
        player.RecalculateStats(race, cls, db);
        Assert.Equal(acBase + 7, player.ACAbility);   // value 0 -> cast level 7 used as the raw add
    }

    [Fact]
    public void AddOrRefreshActiveSpell_refreshes_instead_of_stacking()
    {
        var player = new Player();
        Assert.True(player.AddOrRefreshActiveSpell(500, castLevel: 5, duration: 10));
        Assert.True(player.AddOrRefreshActiveSpell(500, castLevel: 8, duration: 20));

        Assert.Single(player.ActiveSpells);
        Assert.Equal(8, player.ActiveSpells[0].CastLevel);
        Assert.Equal(20, player.ActiveSpells[0].RemainingDuration);
    }

    [Fact]
    public void RecalculateStats_keeps_non_skill_classes_at_zero_skills()
    {
        var player = new Player
        {
            Level = 12,
            Intellect = 65,
            Willpower = 55,
            Strength = 80,
            Agility = 70,
            Health = 50,
            Charm = 60,
            CurrentHP = 100,
        };

        player.RecalculateStats(new Race { Number = 99 }, new CharacterClass(), new InMemoryGameDatabase());

        Assert.Equal(0, player.MaxMana);
        Assert.Equal(0, player.SpellCasting);
        Assert.Equal(0, player.Stealth);
        Assert.Equal(0, player.Thievery);
        Assert.Equal(0, player.Traps);
        Assert.Equal(0, player.Picklocks);
        Assert.Equal(0, player.Tracking);
        Assert.Equal(3840, player.MaxEncumbrance);
    }

    [Fact]
    public void RecalculateStats_clamps_non_stealther_with_racial_stealth_penalty_to_zero()
    {
        // Bug #73: a Half-Ogre Warlock (no race- or class-stealth) carries a racial Stealth(27) = -15
        // penalty. Stock applies the stealth-ability bonus only to race/class stealthers, so a
        // non-stealther stays pinned at 0 — it must never display negative stealth.
        var race = new Race
        {
            Number = 99,
            Abilities = new Dictionary<int, int> { [27] = -15 },
        };
        var player = new Player
        {
            Level = 15,
            Intellect = 60,
            Willpower = 55,
            Strength = 120,
            Agility = 65,
            Health = 50,
            Charm = 40,
            CurrentHP = 100,
        };

        player.RecalculateStats(race, new CharacterClass(), new InMemoryGameDatabase());

        Assert.False(player.HasRaceStealth);
        Assert.False(player.HasClassStealth);
        Assert.Equal(0, player.Stealth);
    }

    [Fact]
    public void GrantQuestAbility_stores_a_value_zero_boolean_so_perfect_stealth_applies()
    {
        // The "supernatural stealth" assassin quest grants Perfect Stealth as `giveability 186 0` — a
        // presence-only boolean at value 0. GrantQuestAbility must STORE it (SetQuestAbilityValue would
        // drop it via its value<=0 remove path), so the recalc sets HasPerfectStealth.
        var player = new Player { Level = 30, Strength = 50, Agility = 50, Charm = 50, Intellect = 50, Willpower = 50, Health = 50 };

        player.GrantQuestAbility(186, 0);
        player.RecalculateStats(new Race { Number = 99 }, new CharacterClass(), new InMemoryGameDatabase());

        Assert.True(player.QuestAbilities.ContainsKey(186));
        Assert.True(player.HasPerfectStealth);
    }

    [Fact]
    public void SetQuestAbilityValue_with_zero_still_removes_for_the_scroll_unlearn_path()
    {
        // Guard the distinction: SetQuestAbilityValue(x, 0) must keep REMOVING (scroll-unequip relies on
        // it), so the value-0 grant fix lives in GrantQuestAbility, not by loosening this path.
        var player = new Player();
        player.GrantQuestAbility(186, 0);
        Assert.True(player.QuestAbilities.ContainsKey(186));

        player.SetQuestAbilityValue(186, 0);
        Assert.False(player.QuestAbilities.ContainsKey(186));
    }

    [Theory]
    [InlineData(2, 3, 4, 5, 6, 5)]
    [InlineData(0, 0, 0, 0, 2, 0)]
    [InlineData(0, 0, 0, 0, 3, 1)]
    public void CalculateCurrencyWeight_counts_each_denomination_as_one_third_of_a_coin(long runic, long platinum, long gold, long silver, long copper, int expected)
    {
        Assert.Equal(expected, Player.CalculateCurrencyWeight(runic, platinum, gold, silver, copper));
    }

    [Fact]
    public void RecalculateEquipment_uses_stock_currency_weight_for_encumbrance()
    {
        var player = new Player
        {
            Runic = 2,
            Platinum = 3,
            Gold = 4,
            Silver = 5,
            Copper = 6,
        };

        player.RecalculateEquipment(new InMemoryGameDatabase());

        Assert.Equal(5, player.Encumbrance);
    }

    [Fact]
    public void RecalculateStats_applies_half_of_backstab_accuracy_ability_116()
    {
        var race = new Race
        {
            Number = 99,
            Abilities = new Dictionary<int, int>
            {
                [116] = -15,
            },
        };

        var player = new Player
        {
            Level = 10,
            Agility = 50,
            CurrentHP = 100,
        };

        player.RecalculateStats(race, new CharacterClass(), new InMemoryGameDatabase());

        Assert.Equal(-7, player.BSAccuracy);
    }

    [Fact]
    public void RecalculateStats_resets_backstab_accuracy_when_ability_116_is_absent()
    {
        var player = new Player
        {
            Level = 10,
            Agility = 50,
            CurrentHP = 100,
            BSAccuracy = 99,
        };

        player.RecalculateStats(new Race { Number = 99 }, new CharacterClass(), new InMemoryGameDatabase());

        Assert.Equal(0, player.BSAccuracy);
    }

    // The secondary-stat path for magery type 5 (Kai):
    // the max-Kai resource is written as `L - 1` rather than the spellcaster
    // mana formula (Ability69 + 2*ClassMageryLvl*L + BonusMana + 6). Pin Player.CalculateMaxMana
    // so a future refactor can't silently restore the old "always 0" behaviour for Kai users.
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 4)]
    [InlineData(25, 24)]
    public void CalculateMaxMana_for_Kai_class_returns_level_minus_one(int level, int expectedKai)
    {
        var kaiClass = new CharacterClass { MageryType = Player.KaiMageryType, MageryLvl = 1 };

        Assert.Equal(expectedKai, Player.CalculateMaxMana(kaiClass, level));
    }

    // The stat-edit "done" callback: the flag defaults to 2 and only
    // drops to 0 when the character is brand-new. For post-creation
    // CP-into-stat training the recalc is called with flag 2 = preserve current
    // HP/Mana. Mirror: Player.RecalculateStats must not refill CurrentHP and must clamp (not
    // refill) CurrentMana. This pins the preserve semantics so a future refactor that "tidies"
    // the post-recalc clamping into a refill would fail loudly.
    [Fact]
    public void RecalculateStats_preserves_CurrentHP_and_clamps_CurrentMana_matching_stock_flag_2()
    {
        var race = new Race { Number = 1 };
        var cls = new CharacterClass { CombatLvl = 1, MageryType = 1, MageryLvl = 3 };
        var player = new Player
        {
            Level = 10,
            Strength = 50, Agility = 50, Charm = 50,
            Intellect = 50, Willpower = 50, Health = 50,
            MaxHP = 200,
            CurrentHP = 47,
            MaxMana = 100,
            CurrentMana = 30,
        };

        player.RecalculateStats(race, cls, new InMemoryGameDatabase());

        // CurrentHP must NOT be refilled to MaxHP — preserve flag 2 semantics.
        Assert.Equal(47, player.CurrentHP);
        // CurrentMana stays as-is when below the new MaxMana (only clamps if it would exceed).
        Assert.Equal(30, player.CurrentMana);
    }

    [Fact]
    public void RecalculateStats_clamps_CurrentMana_when_recompute_lowers_MaxMana()
    {
        // If a stat change reduces MaxMana below CurrentMana, the clamp brings it down — but
        // never below. This is the flag-2 boundary case: preserve the current value up to the
        // new max, but don't refill.
        var race = new Race { Number = 1 };
        var cls = new CharacterClass { CombatLvl = 1, MageryType = 1, MageryLvl = 1 };
        var player = new Player
        {
            Level = 2,
            Strength = 50, Agility = 50, Charm = 50,
            Intellect = 50, Willpower = 50, Health = 50,
            CurrentMana = 500,    // way above the new max
        };

        player.RecalculateStats(race, cls, new InMemoryGameDatabase());

        int expectedMax = Player.CalculateMaxMana(cls, 2); // 6 + 2*(1*2) = 10
        Assert.Equal(expectedMax, player.MaxMana);
        Assert.Equal(expectedMax, player.CurrentMana);
    }

    [Fact]
    public void CalculateMaxMana_for_spellcaster_uses_stock_mana_formula()
    {
        // Magery type 1, magery level 3, char level 10 → 6 + 2*(3*10) = 66 (matches the stock formula
        // 6 + 2*ClassMageryLvl*L). Asserting alongside Kai keeps both branches pinned in one place.
        var mage = new CharacterClass { MageryType = 1, MageryLvl = 3 };

        Assert.Equal(66, Player.CalculateMaxMana(mage, 10));
    }

    [Fact]
    public void RecalculateStats_applies_backstab_min_and_max_damage_abilities()
    {
        var race = new Race
        {
            Number = 99,
            Abilities = new Dictionary<int, int>
            {
                [117] = 5,
                [118] = 12,
            },
        };

        var player = new Player
        {
            Level = 10,
            Agility = 50,
            CurrentHP = 100,
            BSMinDamage = 99,
            BSMaxDamage = 99,
        };

        player.RecalculateStats(race, new CharacterClass(), new InMemoryGameDatabase());

        Assert.Equal(5, player.BSMinDamage);
        Assert.Equal(12, player.BSMaxDamage);
    }
}