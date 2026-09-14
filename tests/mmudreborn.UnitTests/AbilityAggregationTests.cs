using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class AbilityAggregationTests
{
    private static (Player player, InMemoryGameDatabase db, Race race, CharacterClass cls) Setup()
    {
        var db = new InMemoryGameDatabase();
        var race = new Race { Number = 1 };
        var cls = new CharacterClass { Number = 1 };
        db.Races[1] = race;
        db.Classes[1] = cls;
        var player = new Player { RaceId = 1, ClassId = 1 };
        return (player, db, race, cls);
    }

    [Fact]
    public void Sum_aggregation_adds_across_sources()
    {
        var (player, db, race, cls) = Setup();
        race.Abilities[36] = 10;   // Magic Resistance — summed
        cls.Abilities[36] = 5;
        Assert.Equal(15, player.GetActiveAbilityValue(db, 36));
    }

    [Fact]
    public void Max_set_ability_takes_max_not_sum()
    {
        var (player, db, race, cls) = Setup();
        race.Abilities[22] = 5;   // Alter AV (Bless) — max-aggregated, not summed
        cls.Abilities[22] = 8;
        Assert.Equal(8, player.GetActiveAbilityValue(db, 22));
    }

    [Fact]
    public void Active_buff_contributes_to_ability_query()
    {
        var (player, db, _, _) = Setup();
        db.Spells[700] = new GameSpell { Number = 700, Abilities = new Dictionary<int, int> { [142] = 5 } };

        Assert.Equal(0, player.GetActiveAbilityValue(db, 142));
        player.AddOrRefreshActiveSpell(700, castLevel: 10, duration: 5);
        Assert.Equal(5, player.GetActiveAbilityValue(db, 142));
    }

    // Bug #187: "AG Wail" (#821, the ancestral guardian's spell) is a fear-THEMED confusion debuff, not a
    // real fear. It carries Accuracy (ability 22, value 0 ⇒ takes the spell magnitude -20) + Confusion
    // (71 = +10), with fear-flavoured confuse/ongoing messages (101/115 → msgs 246/247: "You are too
    // afraid to do that!" / "You are deathly afraid!"). It must NOT set the real afraid flag (ability
    // 60): the victim stays able to act — only ~10% of actions fizzle from the confusion roll.
    [Fact]
    public void AG_Wail_applies_confusion_and_accuracy_debuff_but_not_real_fear()
    {
        var (player, db, race, cls) = Setup();
        db.Spells[821] = new GameSpell
        {
            Number = 821,
            MinBase = -20,
            MaxBase = -20,
            Duration = 40,
            Abilities = new Dictionary<int, int>
            {
                [22] = 0,     // Accuracy — value 0 ⇒ uses the spell magnitude (-20)
                [71] = 10,    // Confusion +10
                [101] = 246,  // Confuse-message id (fear-flavoured)
                [115] = 247,  // ongoing/wear-off message id (fear-flavoured)
            },
        };

        // A monster-cast area debuff stores the rolled magnitude (-20) as the active spell's CastLevel,
        // which fills the value-0 ability slots (here Accuracy).
        player.AddOrRefreshActiveSpell(821, castLevel: -20, duration: 40);
        player.RecalculateStats(race, cls, db);

        Assert.False(player.IsAfraid);                            // NOT real fear (carries no ability 60)
        Assert.Equal(10, player.GetActiveAbilityValue(db, 71)); // confusion magnitude +10
        Assert.Equal(-20, player.AvAbility);                      // accuracy -20 (survives the max-init)
    }

    [Fact]
    public void Suppress_ability_zeroes_the_queried_id()
    {
        var (player, db, race, _) = Setup();
        race.Abilities[36] = 20;
        // A buff carrying ability 124 whose value is 36 suppresses queries for ability 36.
        db.Spells[701] = new GameSpell { Number = 701, Abilities = new Dictionary<int, int> { [124] = 36 } };
        player.AddOrRefreshActiveSpell(701, castLevel: 1, duration: 5);

        Assert.Equal(0, player.GetActiveAbilityValue(db, 36));
    }

    [Fact]
    public void Zero_value_buff_ability_uses_the_stored_slot_magnitude()
    {
        var (player, db, _, _) = Setup();
        // speed/slow store 0 for ability 87; the rolled effect magnitude (captured at
        // cast as the spell's min..max) is applied instead. Slot magnitude 85 (speed) ⇒ ability 87 = 85.
        db.Spells[722] = new GameSpell { Number = 722, Abilities = new Dictionary<int, int> { [87] = 0 } };
        player.AddOrRefreshActiveSpell(722, castLevel: 85, duration: 5);

        Assert.Equal(85, player.GetActiveAbilityValue(db, 87));
    }

    [Fact]
    public void Haste_slow_ability_averages_across_sources_not_max_or_sum()
    {
        var (player, db, _, _) = Setup();
        // Ability 87 folds sources together by a running pairwise average, not max/sum.
        db.Spells[723] = new GameSpell { Number = 723, Abilities = new Dictionary<int, int> { [87] = 80 } };
        db.Spells[724] = new GameSpell { Number = 724, Abilities = new Dictionary<int, int> { [87] = 120 } };
        player.AddOrRefreshActiveSpell(723, castLevel: 0, duration: 5);
        player.AddOrRefreshActiveSpell(724, castLevel: 0, duration: 5);

        // 80 seeds, then (80 + 120) / 2 = 100 — not 120 (max) or 200 (sum).
        Assert.Equal(100, player.GetActiveAbilityValue(db, 87));
    }

    [Fact]
    public void Negative_av_buff_applies_via_sentinel()
    {
        var (player, db, race, cls) = Setup();
        player.Level = 10;
        player.Strength = 50;
        player.Agility = 50;
        // Curse: ability 105 with a negative value must win over the 0-default (the "=max" sentinel).
        db.Spells[702] = new GameSpell { Number = 702, Abilities = new Dictionary<int, int> { [105] = -7 } };
        player.AddOrRefreshActiveSpell(702, castLevel: 10, duration: 5);

        player.RecalculateStats(race, cls, db);
        Assert.Equal(-7, player.AvAbility);
    }

    [Fact]
    public void Carried_non_weapon_non_wearable_item_grants_passive_ability()
    {
        var (player, db, _, _) = Setup();
        // Stock V1.11p item 1677 "tissue": ItemType=3, Worn=0, Abil-0=116 (BSAccuracy), val=-10.
        db.Items[1677] = new Item
        {
            Number = 1677,
            Name = "tissue",
            ItemType = 3,   // not a weapon
            Worn = 0,       // not wearable
            Abilities = new Dictionary<int, int> { [116] = -10 },
        };
        player.Inventory.Add(1677);

        Assert.Equal(-10, player.GetActiveAbilityValue(db, 116));
    }

    [Fact]
    public void Carried_weapon_does_not_grant_passive_ability()
    {
        var (player, db, _, _) = Setup();
        // Skip rule 1: ItemType == 1 (weapon) excludes the item from the carried-bonus sweep —
        // you only get the bonus when wielded, not when stashed in inventory.
        db.Items[200] = new Item
        {
            Number = 200,
            ItemType = 1,   // weapon
            Worn = 0,
            Abilities = new Dictionary<int, int> { [116] = 10 },
        };
        player.Inventory.Add(200);

        Assert.Equal(0, player.GetActiveAbilityValue(db, 116));
    }

    // Abilities 44–49: primary-stat buffs (str/int/wis/agi/health/cha). 24 stock spells
    // carry these — bull's strength, fox's cunning, wisdom, owl's quickness, bear's endurance,
    // eagle's splendour, plus negative-magnitude curses. The ability application
    // writes the bonus into the stat fields and the secondary-stat pass re-derives Perception,
    // MagicResist, MaxEncumbrance, Stealth, etc. on top. Pre-fix, our ApplyAbility had no cases
    // for 44–49, so the buffs activated as duration spells but never actually boosted the stat.
    [Fact]
    public void Strength_buff_increases_Strength_and_MaxEncumbrance()
    {
        var (player, db, race, _) = Setup();
        player.BaseStrength = 50;
        // Bull's-strength-style buff: ability 44 with magnitude 20.
        db.Spells[800] = new GameSpell { Number = 800, Abilities = new Dictionary<int, int> { [44] = 20 } };

        player.RecalculateStats(race, new CharacterClass(), db);
        int baseMaxEncumbrance = player.MaxEncumbrance;

        player.AddOrRefreshActiveSpell(800, castLevel: 10, duration: 5);
        player.RecalculateStats(race, new CharacterClass(), db);

        Assert.Equal(70, player.Strength);                    // 50 base + 20 buff
        Assert.True(player.MaxEncumbrance > baseMaxEncumbrance,
            $"Expected MaxEncumbrance to climb with Strength; was {baseMaxEncumbrance}, now {player.MaxEncumbrance}");
    }

    [Fact]
    public void Intellect_buff_increases_Intellect_MagicResist_and_Perception()
    {
        var (player, db, race, _) = Setup();
        player.BaseIntellect = 50;
        player.BaseWillpower = 50;
        player.BaseCharm = 50;
        db.Spells[801] = new GameSpell { Number = 801, Abilities = new Dictionary<int, int> { [45] = 30 } };

        player.RecalculateStats(race, new CharacterClass(), db);
        int baseMagicResist = player.MagicResist;
        int basePerception = player.Perception;

        player.AddOrRefreshActiveSpell(801, castLevel: 10, duration: 5);
        player.RecalculateStats(race, new CharacterClass(), db);

        Assert.Equal(80, player.Intellect);
        Assert.True(player.MagicResist > baseMagicResist);
        Assert.True(player.Perception > basePerception);
    }

    [Fact]
    public void Agility_buff_increases_Agility_and_Dodge()
    {
        var (player, db, race, _) = Setup();
        player.BaseAgility = 50;
        player.BaseCharm = 50;
        db.Spells[802] = new GameSpell { Number = 802, Abilities = new Dictionary<int, int> { [47] = 25 } };

        player.RecalculateStats(race, new CharacterClass(), db);
        int baseDodge = player.Dodge;

        player.AddOrRefreshActiveSpell(802, castLevel: 10, duration: 5);
        player.RecalculateStats(race, new CharacterClass(), db);

        Assert.Equal(75, player.Agility);
        Assert.True(player.Dodge > baseDodge, $"Expected Dodge to climb with Agility; was {baseDodge}, now {player.Dodge}");
    }

    [Fact]
    public void Negative_stat_buff_curse_reduces_the_primary_below_base()
    {
        var (player, db, race, _) = Setup();
        player.BaseStrength = 60;
        // Curse-style debuff: ability 44 (Strength) with NEGATIVE magnitude — duration buff drains Str.
        db.Spells[803] = new GameSpell { Number = 803, Abilities = new Dictionary<int, int> { [44] = -25 } };

        player.AddOrRefreshActiveSpell(803, castLevel: 10, duration: 5);
        player.RecalculateStats(race, new CharacterClass(), db);

        Assert.Equal(35, player.Strength);
    }

    [Fact]
    public void Stat_buff_removed_restores_base_value()
    {
        var (player, db, race, _) = Setup();
        player.BaseAgility = 50;
        db.Spells[804] = new GameSpell { Number = 804, Abilities = new Dictionary<int, int> { [47] = 15 } };

        player.AddOrRefreshActiveSpell(804, castLevel: 10, duration: 5);
        player.RecalculateStats(race, new CharacterClass(), db);
        Assert.Equal(65, player.Agility);

        player.RemoveActiveSpell(804);
        player.RecalculateStats(race, new CharacterClass(), db);
        Assert.Equal(50, player.Agility);
    }

    [Fact]
    public void Carried_wearable_item_in_inventory_does_not_grant_passive_ability()
    {
        var (player, db, _, _) = Setup();
        // Skip rule 2: Worn != 0 (item has a wear slot) excludes it from the carried sweep —
        // wearable items must actually be worn to apply their bonuses.
        db.Items[300] = new Item
        {
            Number = 300,
            ItemType = 0,   // armor
            Worn = 11,      // torso slot
            Abilities = new Dictionary<int, int> { [116] = 10 },
        };
        player.Inventory.Add(300);

        Assert.Equal(0, player.GetActiveAbilityValue(db, 116));
    }
}
