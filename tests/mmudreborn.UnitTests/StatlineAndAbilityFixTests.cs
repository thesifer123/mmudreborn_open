using System.Collections.Generic;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Unit coverage for the 2026-06-10 stock-parity fixes: ability 76 mute, ability 99 frail (DR%),
// ability 10 blur (ACBlur), monster poison upkeep (TickPoison), and the custom statline renderer.
public sealed class StatlineAndAbilityFixTests
{
    private static (Player player, InMemoryGameDatabase db, Race race, CharacterClass cls) Setup()
    {
        var db = new InMemoryGameDatabase();
        var race = new Race { Number = 1 };
        var cls = new CharacterClass { Number = 1 };
        db.Races[1] = race;
        db.Classes[1] = cls;
        var player = new Player
        {
            RaceId = 1,
            ClassId = 1,
            Level = 10,
            Strength = 50,
            Agility = 50,
            Intellect = 50,
            Willpower = 50,
            Health = 50,
            Charm = 50,
        };
        return (player, db, race, cls);
    }

    [Fact]
    public void Ability76_sets_mute_flag()
    {
        var (player, db, race, cls) = Setup();
        player.RecalculateStats(race, cls, db);
        Assert.False(player.IsMuted);

        race.Abilities[76] = 1; // Mute
        player.RecalculateStats(race, cls, db);
        Assert.True(player.IsMuted);
    }

    [Fact]
    public void Ability99_scales_damage_resist_by_percent()
    {
        var (player, db, race, cls) = Setup();
        race.Abilities[99] = -50; // "frail" — halve DR
        player.RecalculateStats(race, cls, db);

        Assert.Equal(-50, player.DRPercent);
        player.DamageResist = 100; // base DR set after equipment recompute
        Assert.Equal(50, player.GetTotalDR()); // ((-50 + 100) * 100) / 100
    }

    [Fact]
    public void Ability10_populates_blur_ac()
    {
        var (player, db, race, cls) = Setup();
        race.Abilities[10] = 10; // Protective Shield / blur
        player.RecalculateStats(race, cls, db);

        Assert.Equal(10, player.ACBlur);
        // No worn armour ⇒ blur divisor is 1, so the full blur value lands on AC.
        Assert.Equal(1, player.BlurArmourDivisor);
        Assert.Equal(player.ACAbility + 10, player.GetBlurAC());
    }

    [Fact]
    public void Blur_resets_between_recalcs()
    {
        var (player, db, race, cls) = Setup();
        race.Abilities[10] = 10;
        player.RecalculateStats(race, cls, db);
        Assert.Equal(10, player.ACBlur);

        race.Abilities.Remove(10);
        player.RecalculateStats(race, cls, db);
        Assert.Equal(0, player.ACBlur); // no accumulation across recalcs
    }

    [Fact]
    public void Monster_poison_drains_hp_once_per_interval()
    {
        var monster = new MonsterInstance
        {
            Template = new Monster { Number = 5, Name = "rat", HP = 50 },
            CurrentHP = 50,
            MaxHP = 50,
            PoisonLevel = 8,
        };

        Assert.True(monster.TickPoison(30) == false); // first drain, not dead
        Assert.Equal(42, monster.CurrentHP);

        // Subsequent ticks within the interval do not drain again.
        for (int i = 0; i < 29; i++)
            monster.TickPoison(30);
        Assert.Equal(42, monster.CurrentHP);

        monster.TickPoison(30); // next interval boundary
        Assert.Equal(34, monster.CurrentHP);
    }

    [Fact]
    public void Monster_poison_reports_death_when_it_kills()
    {
        var monster = new MonsterInstance
        {
            Template = new Monster { Number = 5, Name = "rat", HP = 50 },
            CurrentHP = 5,
            MaxHP = 50,
            PoisonLevel = 8,
        };

        Assert.True(monster.TickPoison(30)); // drain kills the monster
        Assert.True(monster.IsDead);
    }

    [Fact]
    public void Statline_substitutes_vital_variables()
    {
        var p = new Player
        {
            CurrentHP = 30,
            MaxHP = 100,
            CurrentMana = 5,
            MaxMana = 20,
            Experience = 1000,
            ExpForNextLevelCached = 1500,
        };

        Assert.Equal("30/100", Statline.Substitute(p, "%h/%H"));
        Assert.Equal("5/20", Statline.Substitute(p, "%m/%M"));
        Assert.Equal("1000 500", Statline.Substitute(p, "%x %X")); // 1500 - 1000 = 500
        Assert.Equal("100%", Statline.Substitute(p, "100%%"));     // literal percent
    }

    [Fact]
    public void Statline_substitutes_colour_and_resting_codes()
    {
        var p = new Player { IsResting = true };
        Assert.Equal("\x1b[1mHP\x1b[0m", Statline.Substitute(p, "%BHP%N")); // bold / normal
        Assert.Equal("\x1b[31mX", Statline.Substitute(p, "%f1X"));          // fg colour 1 = red
        Assert.Contains("(Resting)", Statline.Substitute(p, "%r"));
    }

    [Fact]
    public void Statline_render_dispatches_on_mode()
    {
        var p = new Player { CurrentHP = 30, MaxHP = 100, StatlineMode = Statline.ModeOff };
        Assert.EndsWith(":", Statline.Render(p, "STANDARD"));

        p.StatlineMode = Statline.ModeOn;
        Assert.Equal("STANDARD", Statline.Render(p, "STANDARD"));

        p.StatlineMode = Statline.ModeCustom;
        p.CustomStatline = "HP:%h";
        Assert.Contains("HP:30", Statline.Render(p, "STANDARD"));

        // Custom mode with no template configured falls back to the standard prompt.
        p.CustomStatline = "";
        Assert.Equal("STANDARD", Statline.Render(p, "STANDARD"));
    }

    [Fact]
    public void Blur_divided_by_heaviest_worn_armour_weight_class()
    {
        var (player, db, race, cls) = Setup();
        race.Abilities[10] = 12; // blur
        // A type-9 (heaviest) worn armour ⇒ ÷4; a type-4 worn ⇒ ÷2 — the heaviest wins.
        db.Items[200] = new Item { Number = 200, ArmourType = 9, ArmourClass = 0 };
        db.Items[201] = new Item { Number = 201, ArmourType = 4, ArmourClass = 0 };
        player.Equipment["body"] = 200;
        player.Equipment["legs"] = 201;
        player.RecalculateEquipment(db);

        Assert.Equal(4, player.BlurArmourDivisor);
        Assert.Equal(player.ACAbility + 12 / 4, player.GetBlurAC()); // 12 ÷ 4 = 3
    }

    [Fact]
    public void Display_armour_rating_carries_ability2_and_blur_on_ac_ability7_on_dr_no_frail()
    {
        // The armour rating (verified against stock):
        // the stat-screen AC carries ability 2 (natural armour) + blur; the stat-screen DR carries
        // ability 7 (toughness). This is the SAME ability→channel mapping combat uses; the display only
        // omits the frail (99) DR% scaling, which is a combat-fighter-only step.
        var p = new Player
        {
            ArmourClass = 5,      // display scale (Σ item AC bytes / 10)
            DamageResist = 30,    // ×10 scale (Σ item DR bytes) ⇒ /10 = 3 on display
            DRAbility = -10,      // ability 7 (toughness) → DR (in tenths); also scaled by frail in combat
            ACBlur = 12,
            BlurArmourDivisor = 2, // blur contribution = 12 ÷ 2 = 6 → AC
            ACAbility = 7,        // ability 2 natural armour → AC (display AND combat)
            DRPercent = -50,      // frail — combat DR only, NOT on display
        };

        int displayAc = p.GetDisplayArmourRating(out int displayDr);
        Assert.Equal(5 + 7 + 6, displayAc);      // 18 — worn AC + ability 2 + blur
        Assert.Equal((30 + -10) / 10, displayDr); // 2 — (worn DR + ability 7) / 10, no frail

        // Combat AC matches the display AC exactly; combat DR additionally applies the frail %.
        Assert.Equal(5 + (7 + 6), p.GetTotalAC());                 // 18
        Assert.Equal(((-50 + 100) * (30 + -10)) / 100, p.GetTotalDR()); // 10
    }

    [Fact]
    public void Display_armour_rating_clamps_ac_at_zero()
    {
        // Ability 2 (natural armour, e.g. a curse) drives AC negative; the armour rating clamps the
        // return at 0. (Ability 7 lands on DR now, so it can't pull AC down.)
        var p = new Player { ArmourClass = 2, DamageResist = 0, ACAbility = -20, ACBlur = 0 };
        int displayAc = p.GetDisplayArmourRating(out _);
        Assert.Equal(0, displayAc); // 2 + (-20) = -18 → clamped to 0 (the stock return clamp)
    }

    private static (MonsterInstance monster, InMemoryGameDatabase db) MonsterWithSpell(
        int spellId, int ability, int magnitude, int currentHp = 50)
    {
        var db = new InMemoryGameDatabase();
        db.Spells[spellId] = new GameSpell
        {
            Number = spellId,
            Duration = 5,
            Abilities = new Dictionary<int, int> { [ability] = magnitude },
        };
        var monster = new MonsterInstance
        {
            Template = new Monster { Number = 7, Name = "wretch", HP = 50 },
            CurrentHP = currentHp,
            MaxHP = 50,
            MaxEnergy = 100,
            CurrentEnergy = 10,
        };
        monster.ActiveSpells.Add(new ActiveSpell { SpellId = spellId, CastLevel = magnitude, RemainingDuration = 5 });
        return (monster, db);
    }

    [Fact]
    public void Monster_disease_drains_hp_each_upkeep_tick()
    {
        var (monster, db) = MonsterWithSpell(900, ability: 1, magnitude: 6); // disease
        Assert.False(monster.TickSpellUpkeepEffects(db));
        Assert.Equal(44, monster.CurrentHP);
        Assert.False(monster.TickSpellUpkeepEffects(db)); // ticks again each pass
        Assert.Equal(38, monster.CurrentHP);
    }

    [Fact]
    public void Monster_heal_over_time_restores_hp_capped_at_max()
    {
        var (monster, db) = MonsterWithSpell(901, ability: 18, magnitude: 8, currentHp: 46); // heal-over-time
        Assert.False(monster.TickSpellUpkeepEffects(db));
        Assert.Equal(50, monster.CurrentHP); // 46 + 8 clamped to MaxHP 50
    }

    [Fact]
    public void Monster_drain_reports_death_when_it_kills()
    {
        var (monster, db) = MonsterWithSpell(902, ability: 8, magnitude: 9, currentHp: 5); // drain
        Assert.True(monster.TickSpellUpkeepEffects(db));
        Assert.True(monster.IsDead);
    }

    [Fact]
    public void Monster_anti_poison_drains_poison_level()
    {
        var (monster, db) = MonsterWithSpell(903, ability: 20, magnitude: 3); // anti-poison
        monster.PoisonLevel = 5;
        Assert.False(monster.TickSpellUpkeepEffects(db));
        Assert.Equal(2, monster.PoisonLevel);
    }

    // --- Prompt resource (MA / Kai / none) ---

    [Fact]
    public void Non_caster_carries_no_mana_even_with_a_mana_bonus_item()
    {
        var (player, db, race, cls) = Setup();
        cls.MageryType = 0; cls.MageryLvl = 0; // pure martial (e.g. Thief)
        race.Abilities[69] = 10;               // a +10 MaxMana item/buff
        player.RecalculateStats(race, cls, db);

        Assert.Null(player.MagicResourceLabel);
        Assert.Equal(0, player.MaxMana);       // phantom mana discarded — a thief is not a mana user
        Assert.DoesNotContain("MA=", GameAnsi.Prompt(player));
        Assert.DoesNotContain("MA=", MudAnsi.Prompt(player));
    }

    [Fact]
    public void Mana_caster_shows_MA_with_current_mana()
    {
        var (player, db, race, cls) = Setup();
        cls.MageryType = 1; cls.MageryLvl = 2; // mana caster
        player.RecalculateStats(race, cls, db);
        player.CurrentMana = 7;                // spent down from max

        Assert.Equal("MA", player.MagicResourceLabel);
        Assert.True(player.MaxMana > 0);
        string prompt = MudAnsi.Prompt(player);
        Assert.Contains("/MA=", prompt);
        Assert.Contains("7", prompt);          // CURRENT mana, not max
    }

    [Fact]
    public void Kai_class_shows_Kai_label_only_from_level_two()
    {
        var (player, db, race, cls) = Setup();
        cls.MageryType = 5; cls.MageryLvl = 1; // kai (Mystic)

        player.Level = 1;
        player.RecalculateStats(race, cls, db);
        Assert.Equal("KAI", player.MagicResourceLabel);
        Assert.Equal(0, player.MaxMana);                       // 0 kai at level 1
        Assert.DoesNotContain("KAI=", MudAnsi.Prompt(player)); // hidden until they have ≥1 kai

        player.Level = 2;
        player.RecalculateStats(race, cls, db);
        player.CurrentMana = 1;
        Assert.True(player.MaxMana >= 1);
        string prompt = MudAnsi.Prompt(player);
        Assert.Contains("/KAI=", prompt);
        Assert.DoesNotContain("MA=", prompt);                  // kai classes never show "MA"
    }
}
