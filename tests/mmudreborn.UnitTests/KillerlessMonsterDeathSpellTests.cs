using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A monster that dies with no killer — bled out by a weapon proc or a cast DoT, poisoned, dropped by an
// exit trap — still casts its DeathSpell. Stock's kill routine casts it on every monster death and never
// passes the killer, so a quest boss that bleeds out still delivers its quest to the players fighting it,
// and a monster that summons on death still summons. We used to skip the DeathSpell on these deaths.
public sealed class KillerlessMonsterDeathSpellTests
{
    private const int BossId = 300;
    private const int SummonedId = 301;
    private const int BleedSpellId = 900;
    private const int QuestTempSpellId = 901;
    private const int QuestTextSpellId = 902;
    private const int SummonDeathSpellId = 903;
    private const int ExplosionSpellId = 904;
    private const int ExplosionDamage = 7;
    private const int QuestTextBlockId = 950;
    private const int QuestFlag = 133;

    [Fact]
    public void A_quest_boss_that_bleeds_out_delivers_its_quest_to_the_player_fighting_it()
    {
        var (world, boss) = BuildBleedingBoss(QuestTempSpellId);
        var (fighter, client) = AddPlayer(world, "Rook", inCombatWith: boss);

        world.AdvanceMediumTicksForTests(1);

        Assert.True(boss.IsDead);
        Assert.Equal(1, fighter.GetQuestAbilityValue(QuestFlag));
        Assert.Contains(client.Lines, line => line.Contains("You gain 100 experience."));
    }

    [Fact]
    public void A_boss_that_explodes_on_death_still_explodes_when_it_bleeds_out_before_its_death_line()
    {
        var (world, boss) = BuildBleedingBoss(ExplosionSpellId);
        var (fighter, client) = AddPlayer(world, "Rook", inCombatWith: boss);

        world.AdvanceMediumTicksForTests(1);

        Assert.True(boss.IsDead);
        Assert.Equal(100 - ExplosionDamage, fighter.CurrentHP);
        // Stock order: the DeathSpell is cast before the death line, and the exp comes last.
        int blast = client.Lines.FindIndex(line => line.Contains("damage"));
        int death = client.Lines.FindIndex(line => line.Contains("The boss collapses!"));
        int award = client.Lines.FindIndex(line => line.Contains("You gain"));
        Assert.True(blast >= 0 && blast < death && death < award, string.Join(" | ", client.Lines));
    }

    [Fact]
    public void Every_fighter_gets_the_quest_and_a_bystander_does_not()
    {
        var (world, boss) = BuildBleedingBoss(QuestTempSpellId);
        var (first, _) = AddPlayer(world, "Rook", inCombatWith: boss);
        var (second, _) = AddPlayer(world, "Wren", inCombatWith: boss);
        var (bystander, bystanderClient) = AddPlayer(world, "Flint", inCombatWith: null, inCombat: false);

        world.AdvanceMediumTicksForTests(1);

        Assert.Equal(1, first.GetQuestAbilityValue(QuestFlag));
        Assert.Equal(1, second.GetQuestAbilityValue(QuestFlag));
        Assert.Equal(0, bystander.GetQuestAbilityValue(QuestFlag));
        Assert.DoesNotContain(bystanderClient.Lines, line => line.Contains("A dying curse rings out."));
    }

    [Fact]
    public void A_boss_that_bleeds_out_with_only_a_bystander_present_gives_nobody_the_quest()
    {
        var (world, boss) = BuildBleedingBoss(QuestTempSpellId);
        var (bystander, _) = AddPlayer(world, "Flint", inCombatWith: null, inCombat: false);

        world.AdvanceMediumTicksForTests(1);

        Assert.True(boss.IsDead);
        Assert.Equal(0, bystander.GetQuestAbilityValue(QuestFlag));
    }

    [Fact]
    public void A_monster_that_summons_on_death_still_summons_when_it_bleeds_out_in_front_of_its_fighter()
    {
        var (world, boss) = BuildBleedingBoss(SummonDeathSpellId);
        AddPlayer(world, "Rook", inCombatWith: boss);

        world.AdvanceMediumTicksForTests(1);

        Assert.True(boss.IsDead);
        Assert.Contains(world.GetMonstersInRoom(1, 1), monster => monster.Template.Number == SummonedId && !monster.IsDead);
    }

    [Fact]
    public void A_monster_that_summons_on_death_still_summons_when_it_bleeds_out_alone()
    {
        var (world, boss) = BuildBleedingBoss(SummonDeathSpellId);

        world.AdvanceMediumTicksForTests(1);

        Assert.True(boss.IsDead);
        Assert.Contains(world.GetMonstersInRoom(1, 1), monster => monster.Template.Number == SummonedId && !monster.IsDead);
    }

    [Fact]
    public void A_quest_boss_killed_by_poison_delivers_its_quest_too()
    {
        var (world, boss) = BuildBleedingBoss(QuestTempSpellId);
        boss.ActiveSpells.Clear();
        boss.PoisonLevel = 5;   // 1 HP, no regen: the slow update's poison drain kills it
        var (fighter, _) = AddPlayer(world, "Rook", inCombatWith: boss);

        world.AdvanceSlowTicksForTests(1);

        Assert.True(boss.IsDead);
        Assert.Equal(1, fighter.GetQuestAbilityValue(QuestFlag));
    }

    // A stationary boss at 1 HP carrying a 2-per-tick bleed (harm ability 1 on a Duration spell), so the next
    // medium update bleeds it out. Its DeathSpell is either the quest chain stock uses — an area "temp"
    // (Targets 12) that chains (151) to a "text" spell whose 148 runs the quest textblock — a summon, or
    // an area blast.
    private static (GameWorld World, MonsterInstance Boss) BuildBleedingBoss(int deathSpellId)
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, MonsterType = 9 };
        db.Monsters[BossId] = new Monster
        {
            Number = BossId, Name = "boss", HP = 50, Type = 3, Group = 9,
            EXP = 100, ExpMulti = 1, DeathMessage = "The boss collapses!", DeathSpell = deathSpellId,
        };
        db.Monsters[SummonedId] = new Monster { Number = SummonedId, Name = "dying echo", HP = 20, Type = 3, Group = 9 };
        db.Spells[BleedSpellId] = new GameSpell
        {
            Number = BleedSpellId, Name = "bleed", Duration = 5, Abilities = { [1] = 0 },
        };
        db.Spells[QuestTempSpellId] = new GameSpell
        {
            Number = QuestTempSpellId, Name = "boss temp", Targets = 12, Duration = 1,
            Abilities = { [151] = QuestTextSpellId },
        };
        db.Spells[QuestTextSpellId] = new GameSpell
        {
            Number = QuestTextSpellId, Name = "boss text", Targets = 1,
            Abilities = { [148] = QuestTextBlockId },
        };
        db.TextBlocks[QuestTextBlockId] = $"A dying curse rings out.\ngiveability {QuestFlag} 1";
        db.Spells[SummonDeathSpellId] = new GameSpell
        {
            Number = SummonDeathSpellId, Name = "last breath", Targets = 1,
            Abilities = { [12] = SummonedId },
        };

        db.Spells[ExplosionSpellId] = new GameSpell
        {
            Number = ExplosionSpellId, Name = "death throes", Targets = 12, AttType = 1,
            MinBase = ExplosionDamage, MaxBase = ExplosionDamage, Abilities = { [1] = 0 },
        };

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        Assert.True(world.TrySpawnMonsterInRoom(1, 1, BossId, ignoreRoomRestrictions: true, out var boss, out _));
        boss!.CurrentHP = 1;
        Assert.True(boss.AddOrRefreshActiveSpell(db, BleedSpellId, 2, 5));
        return (world, boss);
    }

    private static (Player Player, RecordingGameClient Client) AddPlayer(
        GameWorld world, string name, MonsterInstance? inCombatWith, bool inCombat = true)
    {
        var client = new RecordingGameClient();
        var player = new Player
        {
            Name = name, CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = 100, MaxHP = 100,
            Client = client, InCombat = inCombat, CombatTarget = inCombatWith,
        };
        client.Player = player;
        world.AddPlayer(player);
        return (player, client);
    }
}
