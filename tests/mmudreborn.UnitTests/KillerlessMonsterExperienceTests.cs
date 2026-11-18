using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// A monster killed with no killer — a weapon-proc bleed or cast DoT ticking it out, poison — still pays
// its kill experience to the players fighting it, exactly as a combat kill would. Stock reaps it in the
// monster's medium update and shares the exp over everyone still in autocombat against it (plus any
// target-less area channeller in the room). Stock splits N+1 ways because its divisor keeps a seat for
// a killer that doesn't exist; we deliberately split N ways. We used to pay nothing at all: a monster
// bleeding out from a weapon proc gave its fighter no experience.
public sealed class KillerlessMonsterExperienceTests
{
    private const int BanditId = 225;
    private const int BleedSpellId = 353;
    private const int BanditExperience = 100;

    [Fact]
    public void A_solo_fighter_gets_the_full_kill_experience_when_a_bleed_finishes_the_monster()
    {
        var (world, bandit) = BuildBleedingBandit();
        var (fighter, client) = AddFighter(world, "Rook", bandit);

        world.AdvanceMediumTicksForTests(1);

        Assert.True(bandit.IsDead);
        Assert.Empty(world.GetMonstersInRoom(1, 1));
        Assert.Equal(BanditExperience, fighter.Experience);   // N ways, not N+1: a solo fighter is not halved
        Assert.False(fighter.InCombat);
        Assert.Null(fighter.CombatTarget);
        Assert.Contains(client.Lines, line => line.Contains($"You gain {BanditExperience} experience."));
        Assert.Contains(client.Lines, line => line.Contains("*Combat Off*"));
        // The award follows the death line.
        int death = client.Lines.FindIndex(line => line.Contains("collapses"));
        int award = client.Lines.FindIndex(line => line.Contains("You gain"));
        Assert.InRange(death, 0, award - 1);
    }

    [Fact]
    public void Two_fighters_split_the_experience_two_ways()
    {
        var (world, bandit) = BuildBleedingBandit();
        var (first, _) = AddFighter(world, "Rook", bandit);
        var (second, _) = AddFighter(world, "Wren", bandit);

        world.AdvanceMediumTicksForTests(1);

        Assert.Equal(BanditExperience / 2, first.Experience);
        Assert.Equal(BanditExperience / 2, second.Experience);
    }

    [Fact]
    public void A_bystander_not_in_autocombat_gets_nothing()
    {
        var (world, bandit) = BuildBleedingBandit();
        var (fighter, _) = AddFighter(world, "Rook", bandit);
        var bystanderClient = new RecordingGameClient();
        var bystander = new Player
        {
            Name = "Flint", CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = 100, MaxHP = 100,
            Client = bystanderClient,
        };
        world.AddPlayer(bystander);

        world.AdvanceMediumTicksForTests(1);

        Assert.Equal(BanditExperience, fighter.Experience);
        Assert.Equal(0, bystander.Experience);
        Assert.DoesNotContain(bystanderClient.Lines, line => line.Contains("You gain"));
    }

    [Fact]
    public void An_area_channeller_shares_the_kill_and_keeps_fighting()
    {
        var (world, bandit) = BuildBleedingBandit();
        var (fighter, _) = AddFighter(world, "Rook", bandit);
        var (channeller, channellerClient) = AddFighter(world, "Wren", target: null);

        world.AdvanceMediumTicksForTests(1);

        Assert.Equal(BanditExperience / 2, fighter.Experience);
        Assert.Equal(BanditExperience / 2, channeller.Experience);
        Assert.False(fighter.InCombat);
        Assert.True(channeller.InCombat);
        Assert.DoesNotContain(channellerClient.Lines, line => line.Contains("*Combat Off*"));
    }

    [Fact]
    public void A_monster_that_dies_of_poison_pays_its_fighter_too()
    {
        var (world, bandit) = BuildBleedingBandit();
        bandit.ActiveSpells.Clear();
        bandit.PoisonLevel = 5;   // 1 HP, no regen: the slow update's poison drain kills it
        var (fighter, _) = AddFighter(world, "Rook", bandit);

        world.AdvanceSlowTicksForTests(1);

        Assert.True(bandit.IsDead);
        Assert.Equal(BanditExperience, fighter.Experience);
        Assert.False(fighter.InCombat);
    }

    // A stationary bandit at 1 HP carrying a 2-per-tick bleed (harm ability 1 on a Duration spell) — the
    // next medium update bleeds it out.
    private static (GameWorld World, MonsterInstance Bandit) BuildBleedingBandit()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, MonsterType = 9 };
        db.Monsters[BanditId] = new Monster
        {
            Number = BanditId, Name = "bandit", HP = 50, Type = 3, Group = 9,
            EXP = BanditExperience, ExpMulti = 1, DeathMessage = "The bandit collapses!",
        };
        db.Spells[BleedSpellId] = new GameSpell
        {
            Number = BleedSpellId, Name = "bleed", Duration = 5, Abilities = { [1] = 0 },
        };

        var world = new GameWorld(db, new InMemoryPlayerRepository());
        Assert.True(world.TrySpawnMonsterInRoom(1, 1, BanditId, ignoreRoomRestrictions: true, out var bandit, out _));
        bandit!.CurrentHP = 1;
        Assert.True(bandit.AddOrRefreshActiveSpell(db, BleedSpellId, 2, 5));
        return (world, bandit);
    }

    // A player in the bandit's room inside autocombat. A null target is the area channeller: in combat,
    // fighting the whole room with no single monster chosen.
    private static (Player Player, RecordingGameClient Client) AddFighter(GameWorld world, string name, MonsterInstance? target)
    {
        var client = new RecordingGameClient();
        var player = new Player
        {
            Name = name, CurrentMapNumber = 1, CurrentRoomNumber = 1, CurrentHP = 100, MaxHP = 100,
            Client = client, InCombat = true, CombatTarget = target,
        };
        world.AddPlayer(player);
        return (player, client);
    }
}
