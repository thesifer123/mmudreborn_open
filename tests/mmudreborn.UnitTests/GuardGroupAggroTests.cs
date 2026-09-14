using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

// On-sight aggro is decided by the aggression scan, run every pulse from the
// background pass. It branches on the monster's GROUP *before* its alignment:
//
//     if (Group != 5)                            // generic branch
//         if (align != 4 && align != 0 && align != 3) { ...attack... }
//     else                                     // Group == 5 exactly -> GUARD branch
//         if (align == 6) attack when EP <  40         (hunts the good side)
//         else            attack when EP >  39         (hunts Outlaw+)
//
// So Group 5 ("Guard") hunts criminals with NO alignment filter, and align 0/3/4 monsters outside
// Group 5 never initiate at all. The field mapping was verified from the template->instance copy:
// the instance Align field <- DAT offset 174, the instance Group field <- DAT offset 84.
//
// Group 37 is NOT a law-enforcement class — it is the summoned-monster pool. The decisive
// check is the spawn data: NO room spawns MonsterType 37 (287 rooms spawn Group 5, zero spawn 37),
// and every member has a conjuring spell — Argak #609, Choira #610, Lallim #611, Sharh'Kur #612,
// Zanthus #215, huorn #279, Kai Master #448 (spell 540), guardsman #13 (spell 888 "calls for aid"),
// guardsman #538. The dismiss pass removes them by bound name when their owner
// turns Villain/FIEND, and the area cast has them forgive that owner's friendly fire. A summoned
// one is always name-bound, and the scan only considers monsters with an EMPTY bound-target name
// name, so Group 37 never reaches ShouldMonsterAggro at all.
//
// The TOWN GUARDS are Group 5, not 37: Silvermere (map 1) spawns MonsterType 5 / index 1-5 =
// guardsman #14, Sheriff Lionheart #40, elite guardsman #757 and Templar #214, all align 4 — so
// Silvermere guards engage evil-aligned players only. The world-placed Kai Master is #447 (align 3,
// Group 24, FollowPercent 0) and never initiates; #448 is only ever the summoned copy.
public sealed class GuardGroupAggroTests
{
    private static Monster Mon(int align, int group) => new() { Align = align, Group = group };
    private static Player WithEvil(int ep) => new() { EvilPoints = ep };

    // EP bands: <40 Seedy, 40-79 Outlaw, 80-119 Criminal. Guards engage at Outlaw (>=40).
    private const int GoodEp = -50;
    private const int SeedyEp = 35;
    private const int OutlawEp = 60;
    private const int CriminalEp = 100;

    // Group 5 holds exactly three alignments in the stock DAT: 0, 3 and 4. All of them hunt
    // criminals — the guard branch never looks at Align.
    [Theory]
    [InlineData(4)]   // guardsman #14, Sheriff Lionheart #40, Templar #214, elite guardsman #757,
                      // woodelf citizen #287/#288, woodelf lord #292
    [InlineData(0)]   // woodelf ranger #285, druid #286, guard #289, wardancer #290/#291,
                      // white hart #846, white doe #847, Ghost of Justice Darkbane #849
    [InlineData(3)]   // storm giant #638 / commander #639 / king #637, ivory golem #848/#915
    public void Guard_group_hunts_outlaws_regardless_of_its_own_alignment(int align)
    {
        var guard = Mon(align, group: 5);
        Assert.True(CombatEngine.ShouldMonsterAggro(guard, WithEvil(OutlawEp)));
        Assert.True(CombatEngine.ShouldMonsterAggro(guard, WithEvil(CriminalEp)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(3)]
    public void Guard_group_spares_law_abiding_players(int align)
    {
        var guard = Mon(align, group: 5);
        Assert.False(CombatEngine.ShouldMonsterAggro(guard, WithEvil(GoodEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(guard, WithEvil(SeedyEp))); // Seedy is below Outlaw
    }

    [Fact]
    public void Woodelf_wardancer_aggros_a_criminal_exactly_like_the_woodelf_citizen()
    {
        // The live report that started this: a woodelf citizen (#287, align 4) attacked an Outlaw+
        // player and a woodelf wardancer (#290, align 0) did not. Both are Group 5, so both must.
        var citizen = Mon(align: 4, group: 5);
        var wardancer = Mon(align: 0, group: 5);

        Assert.True(CombatEngine.ShouldMonsterAggro(citizen, WithEvil(OutlawEp)));
        Assert.True(CombatEngine.ShouldMonsterAggro(wardancer, WithEvil(OutlawEp)));

        // ...and neither touches a law-abiding player.
        Assert.False(CombatEngine.ShouldMonsterAggro(citizen, WithEvil(GoodEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(wardancer, WithEvil(GoodEp)));
    }

    [Fact]
    public void Commander_markus_and_royal_soldiers_never_initiate_on_anyone()
    {
        // Royal soldier #245 and Commander Markus #246 are Align 4, Group 9. Group != 5 puts them in
        // the generic branch, which excludes align 4 outright — they are retaliate-only and must not
        // jump anyone, however evil.
        var markus = Mon(align: 4, group: 9);

        Assert.False(CombatEngine.ShouldMonsterAggro(markus, WithEvil(GoodEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(markus, WithEvil(SeedyEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(markus, WithEvil(OutlawEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(markus, WithEvil(CriminalEp)));
    }

    [Theory]
    [InlineData(0)]   // townsfolk
    [InlineData(3)]   // peaceful creature
    [InlineData(4)]   // lawful
    public void Passive_alignments_outside_group_5_never_initiate(int align)
    {
        var npc = Mon(align, group: 9);
        Assert.False(CombatEngine.ShouldMonsterAggro(npc, WithEvil(GoodEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(npc, WithEvil(CriminalEp)));
    }

    [Theory]
    [InlineData(1)]   // neutral aggressive
    [InlineData(2)]   // evil hostile
    [InlineData(5)]   // wild aggressive
    public void Aggressive_alignments_outside_group_5_attack_everyone(int align)
    {
        var wild = Mon(align, group: 20);
        Assert.True(CombatEngine.ShouldMonsterAggro(wild, WithEvil(GoodEp)));
        Assert.True(CombatEngine.ShouldMonsterAggro(wild, WithEvil(CriminalEp)));
    }

    [Fact]
    public void Align6_evil_npc_hunts_the_good_side_and_spares_outlaws()
    {
        // Inverted band: align 6 excludes EP > 39, so it aggros Saint..Seedy and skips Outlaw+.
        var evilNpc = Mon(align: 6, group: 22);
        Assert.True(CombatEngine.ShouldMonsterAggro(evilNpc, WithEvil(GoodEp)));
        Assert.True(CombatEngine.ShouldMonsterAggro(evilNpc, WithEvil(SeedyEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(evilNpc, WithEvil(OutlawEp)));
    }

    [Fact]
    public void Group_37_is_not_a_guard_class_and_follows_its_own_alignment()
    {
        // Group 37 is the summoned pool, so it gets no criminal-hunting gate. Nothing observable
        // changes either way — no room spawns MonsterType 37, and a summoned monster is name-bound,
        // which the aggression scan skips entirely — but the classification must not drift back.
        Assert.False(CombatEngine.ShouldMonsterAggro(Mon(align: 4, group: 37), WithEvil(CriminalEp)));
        Assert.True(CombatEngine.ShouldMonsterAggro(Mon(align: 5, group: 37), WithEvil(GoodEp)));
    }

    [Fact]
    public void Silvermere_town_guards_engage_evil_only()
    {
        // Silvermere (map 1) spawns MonsterType 5 / index 1-5: guardsman #14, Sheriff Lionheart #40,
        // elite guardsman #757, Templar #214 — all Align 4, Group 5. They must hit the guard branch
        // and engage evil-aligned players ONLY, never a law-abiding one.
        var silvermereGuard = Mon(align: 4, group: 5);

        Assert.True(CombatEngine.ShouldMonsterAggro(silvermereGuard, WithEvil(OutlawEp)));
        Assert.True(CombatEngine.ShouldMonsterAggro(silvermereGuard, WithEvil(CriminalEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(silvermereGuard, WithEvil(GoodEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(silvermereGuard, WithEvil(SeedyEp)));
    }

    [Fact]
    public void World_placed_kai_master_never_initiates()
    {
        // The Kai Master you meet in the world is #447: Align 3, Group 24, FollowPercent 0. Align 3
        // outside Group 5 is excluded from the scan, so it never attacks anyone unprovoked. (#448,
        // Align 5 / Group 37, is only ever conjured by spell 540 "summon kai master".)
        var kaiMaster447 = Mon(align: 3, group: 24);

        Assert.False(CombatEngine.ShouldMonsterAggro(kaiMaster447, WithEvil(GoodEp)));
        Assert.False(CombatEngine.ShouldMonsterAggro(kaiMaster447, WithEvil(CriminalEp)));
    }

    [Fact]
    public void Emblem_bearer_is_spared_by_a_guard_that_would_otherwise_aggro()
    {
        // Ability 185 "Do not Attack if Item Nmbr" still wins over the guard branch.
        var guard = Mon(align: 0, group: 5);
        guard.Abilities[185] = 999;

        var criminal = WithEvil(CriminalEp);
        Assert.True(CombatEngine.ShouldMonsterAggro(guard, criminal));

        criminal.Inventory.Add(999);
        Assert.False(CombatEngine.ShouldMonsterAggro(guard, criminal));
    }
}
