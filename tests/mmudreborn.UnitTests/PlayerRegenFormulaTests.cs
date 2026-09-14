using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// The `stat all` sheet reports regen to the player, so it must read the SAME formulas the world tick
// applies (GameWorld regen helpers). It previously invented its own — Health/5 for HP and
// SpellCasting/2 for mana — which matched nothing, and printed a clamp ceiling ("6/60", "0/30") as if
// it were a cadence. These pin the real stock-verified numbers so display and engine cannot drift.
// Model: passive HP = max(1, (Level+20)*Health/750) every 30s; resting = that base x3 every ~21s;
// mana/kai = the class rate every 30s, meditate = the same base x1 every ~15s WITHOUT the bonus.
public sealed class PlayerRegenFormulaTests
{
    private static GameWorld NewWorld(out InMemoryGameDatabase db)
    {
        db = new InMemoryGameDatabase();
        // Mystic: kai class (MageryType 5), MageryLvl 1.
        db.Classes[15] = new CharacterClass { Number = 15, Name = "Mystic", MageryType = 5, MageryLvl = 1 };
        // Mage: INT caster (MageryType 1), MageryLvl 3.
        db.Classes[12] = new CharacterClass { Number = 12, Name = "Mage", MageryType = 1, MageryLvl = 3 };
        // Warrior: pure martial, no pool at all.
        db.Classes[1] = new CharacterClass { Number = 1, Name = "Warrior", MageryType = 0, MageryLvl = 0 };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    [Fact]
    public void Level_one_hp_regen_is_the_floored_base_not_health_over_five()
    {
        // Health 30 at level 1: (1+20)*30/750 == 0 in integer math, so the max(1,…) floor applies.
        // The old panel showed Health/5 == 6, which was six times the real rate.
        var player = new Player { Name = "Goober", ClassId = 15, Level = 1, Health = 30 };

        Assert.Equal(1, GameWorld.GetPassiveHpRegenBase(player));
        Assert.Equal(1, GameWorld.GetPassiveHpRegen(player));
        Assert.Equal(3, GameWorld.GetRestHpRegen(player));   // base x3
        Assert.NotEqual(player.Health / 5, GameWorld.GetPassiveHpRegen(player));
    }

    [Fact]
    public void Hp_regen_scales_with_level_and_health_once_past_the_floor()
    {
        // (50+20)*100/750 == 9 (integer), so a level-50 character is well past the max(1,…) floor.
        var player = new Player { Name = "Big", ClassId = 1, Level = 50, Health = 100 };

        Assert.Equal(9, GameWorld.GetPassiveHpRegen(player));
        Assert.Equal(27, GameWorld.GetRestHpRegen(player));
    }

    [Fact]
    public void Hp_regen_bonus_is_a_percentage_fold_not_a_flat_add()
    {
        var player = new Player { Name = "Big", ClassId = 1, Level = 50, Health = 100, HpRegenBonus = 100 };

        // (100+100)*9/100 == 18, i.e. doubled — NOT 9 + 100.
        Assert.Equal(18, GameWorld.GetPassiveHpRegen(player));

        // A cursed heal rate drains: the fold has no lower clamp (Death Shroud #1579 is -150 total).
        player.HpRegenBonus = -150;
        Assert.True(GameWorld.GetPassiveHpRegen(player) < 0);
    }

    [Fact]
    public void Kai_class_regen_is_a_flat_rate_and_never_reads_spellcasting()
    {
        var world = NewWorld(out _);
        // A Mystic has no SpellCasting by definition (UsesSpellcasting excludes kai classes), which is
        // exactly why the old SpellCasting/2 display read 0 for every kai character.
        var player = new Player { Name = "Goober", ClassId = 15, Level = 1, Intellect = 50, Willpower = 30 };

        Assert.Equal(0, Player.CalculateSpellCasting(world.Database.Classes[15], 1, 50, 30, 30));
        Assert.Equal(1, world.GetPassiveManaRegen(player));
        Assert.Equal(1, world.GetMeditateManaRegen(player));
    }

    [Fact]
    public void Mana_caster_regen_uses_the_stock_stat_formula()
    {
        var world = NewWorld(out _);
        // Mage (MageryType 1 ⇒ INT, MageryLvl 3) at level 10 with INT 60:
        //   (10+20)*60*(3+2)/1650 == 9000/1650 == 5
        var player = new Player { Name = "Wiz", ClassId = 12, Level = 10, Intellect = 60, Willpower = 40 };

        Assert.Equal(5, world.GetPassiveManaRegen(player));
    }

    [Fact]
    public void Meditate_ignores_the_mana_regen_bonus_but_passive_applies_it()
    {
        var world = NewWorld(out _);
        var player = new Player { Name = "Wiz", ClassId = 12, Level = 10, Intellect = 60, ManaRegenBonus = 100 };

        Assert.Equal(10, world.GetPassiveManaRegen(player));    // (100+100)*5/100
        Assert.Equal(5, world.GetMeditateManaRegen(player));    // base only — the stock rule
    }

    // The mana fold has no lower clamp either, so it behaves like its HP twin: too small a
    // base swallows a positive bonus whole, -100 zeroes the trickle, and past -100 it drains.
    [Fact]
    public void Mana_regen_bonus_fold_truncates_and_has_no_lower_clamp()
    {
        var world = NewWorld(out _);
        // Base 5 — (10+20)*60*(3+2)/1650.
        var player = new Player { Name = "Wiz", ClassId = 12, Level = 10, Intellect = 60 };

        // +10% of 5 is 5.5, and integer division keeps it at 5: the bonus is worth nothing until the
        // base reaches 10. This is why a low-level Cleric readying the black flail #349 sees no change.
        player.ManaRegenBonus = 10;
        Assert.Equal(5, world.GetPassiveManaRegen(player));

        // banish #435 / draka tomb #1115: regeneration stops dead, rather than being floored at +1.
        player.ManaRegenBonus = -100;
        Assert.Equal(0, world.GetPassiveManaRegen(player));

        // frothing brown potion #1133: past -100 the trickle reverses into a drain.
        player.ManaRegenBonus = -200;
        Assert.True(world.GetPassiveManaRegen(player) < 0);
    }

    [Fact]
    public void Pure_martial_class_has_no_resource_regen()
    {
        var world = NewWorld(out _);
        var player = new Player { Name = "Grunt", ClassId = 1, Level = 20, Intellect = 40, Willpower = 40 };

        Assert.Equal(0, world.GetPassiveManaRegen(player));
    }

    [Fact]
    public void Reported_cadences_match_the_tick_scheduler()
    {
        Assert.Equal(30, GameWorld.PassiveRegenIntervalSeconds);
        Assert.Equal(21, GameWorld.RestRegenIntervalSeconds);       // fast-tick counter fires at > 20
        Assert.Equal(15, GameWorld.MeditateRegenIntervalSeconds);   // fires at > 14
    }
}
