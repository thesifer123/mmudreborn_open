using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Locks in the engine-level guarantees that let the command layer reproduce the stock weapon
// proc-kill cutoff (the proc fires inside each swing's hit branch, and a
// proc that lands the kill stops the swing loop):
//   * ResolveNormalWeaponSwingVsMonster resolves EXACTLY ONE swing per call, so the async orchestrator
//     fully controls when the round stops — no swing rolls after a proc kill, hence no extra spike
//     retaliation and no swing line after the death/exp output.
//   * The proc is NOT rolled on the killing melee blow (no proc lands on a corpse), so a melee kill and
//     a proc kill never both fire / never double-handle the death.
public sealed class CombatEngineProcCutoffTests
{
    private const int RetaliationAbility = 72;

    private static (Player player, CharacterClass cls, Item weapon) MakeAttacker()
    {
        var cls = new CharacterClass { CombatLvl = 50 };
        var player = new Player
        {
            Name = "Hero",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 100000,
            MaxHP = 5000,
            CurrentHP = 5000,
            CurrentEnergy = 1000,
        };
        // 100% on-hit proc of spell 318; big damage band so a swing can kill a low-HP target.
        var weapon = new Item
        {
            Number = 209,
            Name = "huge darkwood club",
            Min = 50,
            Max = 50,
            Accy = 100000,
            WeaponType = 0,
            Abilities = new Dictionary<int, int>
            {
                [CombatEngine.WeaponSpellProcChanceAbilityId] = 100,
                [CombatEngine.WeaponSpellProcSpellAbilityId] = 318,
            },
        };
        return (player, cls, weapon);
    }

    [Fact]
    public void Killing_melee_blow_does_not_roll_the_weapon_proc()
    {
        var (player, cls, weapon) = MakeAttacker();

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var monster = new MonsterInstance
            {
                Template = new Monster { Name = "kobold", Abilities = [], Drops = [], ArmourClass = 0, BSDefense = 0 },
                DisplayName = "kobold",
                CurrentHP = 5,    // one 50-damage swing kills outright
                MaxHP = 5,
            };

            var profile = CombatEngine.BuildNormalWeaponSwingProfileVsMonster(player, monster, cls, weapon, null, null);
            var result = new CombatResult();
            var (procSpellId, _) = CombatEngine.ResolveNormalWeaponSwingVsMonster(player, monster, weapon, profile, result, null, null);

            if (!monster.IsDead)
                continue;   // swing missed; retry until it lands the kill

            // The blow killed the monster, so the proc must NOT have been rolled (no proc on a corpse) —
            // this is what stops a melee kill and a proc kill from both firing / double-handling death.
            Assert.Equal(0, procSpellId);
            Assert.Empty(result.TriggeredWeaponProcs);
            return;
        }

        throw new Xunit.Sdk.XunitException("Expected a killing swing within the retry window.");
    }

    [Fact]
    public void Surviving_hit_rolls_the_weapon_proc()
    {
        var (player, cls, weapon) = MakeAttacker();

        for (int attempt = 0; attempt < 200; attempt++)
        {
            var monster = new MonsterInstance
            {
                Template = new Monster { Name = "ogre", Abilities = [], Drops = [], ArmourClass = 0, BSDefense = 0 },
                DisplayName = "ogre",
                CurrentHP = 5000,   // survives a single 50-damage swing
                MaxHP = 5000,
            };

            var profile = CombatEngine.BuildNormalWeaponSwingProfileVsMonster(player, monster, cls, weapon, null, null);
            var result = new CombatResult();
            var (procSpellId, _) = CombatEngine.ResolveNormalWeaponSwingVsMonster(player, monster, weapon, profile, result, null, null);

            if (result.Hits == 0 || monster.IsDead)
                continue;

            // Alive after the hit → the 100%-chance proc triggered and reports its spell id to the caller.
            Assert.Equal(318, procSpellId);
            return;
        }

        throw new Xunit.Sdk.XunitException("Expected a non-lethal hit within the retry window.");
    }

    [Fact]
    public void Each_primitive_call_resolves_exactly_one_swing_of_spike_retaliation()
    {
        // A spiked monster (ability 72) damages the attacker on each landing swing. Because the
        // primitive resolves ONE swing per call, the orchestrator that stops calling it the instant a
        // proc kills the monster cannot incur a single extra spike — the cutoff is exact.
        var db = new InMemoryGameDatabase();
        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "spiked golem",
                Abilities = new() { [RetaliationAbility] = 5 },
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

        var (player, cls, weapon) = MakeAttacker();
        var profile = CombatEngine.BuildNormalWeaponSwingProfileVsMonster(player, monster, cls, weapon, db.Messages, db);

        bool exercised = false;
        for (int attempt = 0; attempt < 200 && !exercised; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            var result = new CombatResult();
            var (_, attackerDied) = CombatEngine.ResolveNormalWeaponSwingVsMonster(player, monster, weapon, profile, result, db.Messages, sources);
            Assert.False(attackerDied);

            if (result.Hits == 0)
                continue;   // a miss draws no spike; retry for a landed swing

            exercised = true;
            int lost = player.MaxHP - player.CurrentHP;
            // Exactly one swing landed, so at most ONE spike roll (1..5) was applied — never more.
            Assert.InRange(lost, 1, 5);
        }

        Assert.True(exercised, "Expected at least one landed swing to drive a single spike.");
    }
}
