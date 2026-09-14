using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

/// <summary>
/// A NORMAL attack with nothing wielded is NOT a one-swing special case in stock.
/// The fighter marshal takes the no-weapon branch and computes
/// energy use with the bare-fist "weapon speed" 1200 and no strength requirement;
/// the resulting EU then feeds the same per-round loop as an equipped weapon
/// (both attack loops gate on the same 6-swing cap), so bare fists
/// scale from 1 up to 6 swings a round with level, agility and encumbrance.
/// </summary>
public sealed class WeaponlessSwingBudgetTests
{
    [Fact]
    public void Weaponless_energy_use_matches_the_stock_1200_speed_constant()
    {
        var cls = new CharacterClass { CombatLvl = 5 };
        var player = new Player { Level = 50, Agility = 90, Strength = 90 };

        var weaponless = CombatEngine.GetWeaponSwingPreview(player, cls, weapon: null);
        // A weapon whose Speed IS the weaponless constant, with no StrReq, must price identically —
        // proving the no-weapon branch feeds the energy calculation rather than short-circuiting.
        var equivalentWeapon = CombatEngine.GetWeaponSwingPreview(
            player, cls, new Item { Speed = CombatEngine.StockWeaponlessSpeed, StrReq = 0 });

        Assert.Equal(1200, CombatEngine.StockWeaponlessSpeed);
        Assert.Equal(equivalentWeapon.RawEnergyUse, weaponless.RawEnergyUse);
        Assert.Equal(equivalentWeapon.EnergyUse, weaponless.EnergyUse);

        // denom = ((50*5)+45)*(90+150)*1500/9000 = 11800 → raw EU = 1200*1000/11800 = 101,
        // ×(0/2+75)% = 75, then floored to the stock minimum of 200.
        Assert.Equal(75, weaponless.RawEnergyUse);
        Assert.Equal(WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse, weaponless.EnergyUse);
    }

    [Theory]
    // Level 1 with no agility: raw EU 1066 → ×75% = 799, one swing out of a 1000 pool.
    [InlineData(1, 0, 0, 1)]
    // EU falls as level/CombatLvl/agility rise, buying more swings out of the same pool.
    [InlineData(10, 3, 40, 2)]
    [InlineData(14, 3, 60, 3)]
    [InlineData(20, 4, 70, 5)]
    // Deep into the level curve the EU sits on the 200 floor: 1000/200 = 5 swings from one refill.
    [InlineData(50, 5, 90, 5)]
    public void Weaponless_swings_scale_with_level_and_agility(int level, int combatLvl, int agility, int expectedSwings)
    {
        var cls = new CharacterClass { CombatLvl = combatLvl };
        var player = new Player { Level = level, Agility = agility, Strength = 50 };
        player.PrepareCombatRound();

        Assert.Equal(expectedSwings, CombatEngine.GetWeaponRoundSwings(player, cls, weapon: null));
    }

    [Fact]
    public void Weaponless_swings_never_exceed_the_stock_six_per_round_cap()
    {
        // The attack loop ends the round past six swings, so six is the ceiling no
        // matter how cheap the EU gets. A carried remainder on top of the refill is what reaches it —
        // the floored EU of 200 only buys 5 out of a bare 1000 pool.
        var cls = new CharacterClass { CombatLvl = 9 };
        var player = new Player { Level = 200, Agility = 300, Strength = 300, CurrentEnergy = 100_000 };

        Assert.Equal(WeaponSwingPreviewCalculator.MaxWeaponSwingsPerRound,
            CombatEngine.GetWeaponRoundSwings(player, cls, weapon: null));
    }

    [Fact]
    public void Weaponless_swings_consume_the_shared_stamina_pool()
    {
        var cls = new CharacterClass { CombatLvl = 5 };
        var player = new Player { Level = 50, Agility = 90, Strength = 90 };
        player.PrepareCombatRound();

        int swings = CombatEngine.GetWeaponRoundSwings(player, cls, weapon: null);

        Assert.Equal(5, swings);
        // 1000 - 5×200; the leftover carries into the next round exactly like a weapon's.
        Assert.Equal(0, player.CurrentEnergy);
    }

    [Fact]
    public void Weaponless_round_with_an_empty_pool_yields_no_swings()
    {
        // When the pool cannot cover one EU the round produces no swing
        // ("You must rest before you may attack"). Bare fists are budgeted the same as a weapon.
        var cls = new CharacterClass { CombatLvl = 5 };
        var player = new Player { Level = 50, Agility = 90, Strength = 90, CurrentEnergy = 0 };

        Assert.Equal(0, CombatEngine.GetWeaponRoundSwings(player, cls, weapon: null));
    }

    [Fact]
    public void Weaponless_bash_doubles_energy_use_like_a_weapon_bash()
    {
        // Attack type 6 doubles EU after the branch, independent of what is wielded.
        var cls = new CharacterClass { CombatLvl = 5 };
        var normal = new Player { Level = 50, Agility = 90, Strength = 90 };
        var bashing = new Player { Level = 50, Agility = 90, Strength = 90 };
        normal.PrepareCombatRound();
        bashing.PrepareCombatRound();

        var normalPreview = CombatEngine.GetWeaponSwingPreview(normal, cls, weapon: null);
        var bashPreview = CombatEngine.GetWeaponSwingPreview(bashing, cls, weapon: null, isBashing: true);

        Assert.Equal(normalPreview.RawEnergyUse * 2, bashPreview.RawEnergyUse);
        Assert.True(CombatEngine.GetWeaponRoundSwings(bashing, cls, weapon: null, isBashing: true)
            <= CombatEngine.GetWeaponRoundSwings(normal, cls, weapon: null));
    }

    [Fact]
    public void Weaponless_quick_and_deadly_bonus_applies_when_energy_use_is_cheap()
    {
        // The Q&D bump is computed AFTER the attack-type branch, gated on EU<200 &&
        // encum<67 && no strength-requirement penalty — never on "is a weapon wielded".
        var cls = new CharacterClass { CombatLvl = 5 };
        var player = new Player { Level = 50, Agility = 90, Strength = 90 };

        var preview = CombatEngine.GetWeaponSwingPreview(player, cls, weapon: null);

        // raw EU 75, encumbrance 0 → min(20, 200-75 + (90-50)/10) = 20.
        Assert.Equal(20, preview.QuickAndDeadlyBonus);
        Assert.Equal(20, CombatEngine.GetWeaponQuickAndDeadlyBonus(player, cls, weapon: null));
        Assert.Equal(player.GetCrits() + 20, CombatEngine.GetWeaponCritChance(player, cls, weapon: null));
    }

    [Fact]
    public void Weaponless_swings_shrink_as_encumbrance_rises()
    {
        // EU ×= (encumbrancePercent/2 + 75)/100 — a loaded-down brawler swings less often.
        var cls = new CharacterClass { CombatLvl = 4 };
        var light = new Player { Level = 20, Agility = 70, Strength = 50, MaxEncumbrance = 1000, Encumbrance = 0 };
        var heavy = new Player { Level = 20, Agility = 70, Strength = 50, MaxEncumbrance = 1000, Encumbrance = 1000 };
        light.PrepareCombatRound();
        heavy.PrepareCombatRound();

        int lightSwings = CombatEngine.GetWeaponRoundSwings(light, cls, weapon: null);
        int heavySwings = CombatEngine.GetWeaponRoundSwings(heavy, cls, weapon: null);

        Assert.True(heavySwings < lightSwings, $"expected fewer swings at full encumbrance ({heavySwings} vs {lightSwings})");
    }

    [Fact]
    public void Weaponless_attack_vs_monster_resolves_the_whole_multi_swing_round()
    {
        var cls = new CharacterClass { CombatLvl = 5 };
        var attacker = new Player
        {
            Name = "Brawler",
            Level = 50,
            Strength = 50,
            Agility = 90,
            PartyAccuracyModifier = 500,
            MaxHP = 100,
            CurrentHP = 100,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster { Name = "dummy", Align = 2, ArmourClass = 0, BSDefense = 0, DamageResist = 0, HP = 100000, Drops = [] },
            DisplayName = "training dummy",
            MaxHP = 100000,
            CurrentHP = 100000,
        };

        attacker.PrepareCombatRound();
        var result = CombatEngine.PlayerAttack(attacker, monster, cls, weapon: null, messages: null);

        // Five swings resolve, each landing/missing on its own roll — not the single swing the old
        // `weapon == null ? 1` guard produced.
        Assert.Equal(5, result.Hits + result.Misses);
    }
}
