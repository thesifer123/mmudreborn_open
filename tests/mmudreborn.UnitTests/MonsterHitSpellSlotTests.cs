using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// AtkHitSpell belongs to the SLOT that connected. Stock reads it inside the swing
// loop, indexed by the slot the attack roll selected — so a
// monster whose hit-spell sits on a low-probability secondary slot procs it only that often.
//
// We used to pick the first slot on the TEMPLATE carrying any hit-spell, once per round. Stock data
// breaks that assumption badly: grey spider #30 is AtkPer 95/100 with the poison ("bites", 8-12 for
// 100 ticks) only on slot 1, so it should proc on 5% of bites and was procing on 100% — a 20x
// inflation on a monster newbies meet in Newhaven. Ghoul #16 (75/100) and shade #15 (90/100) were
// inflated 4x and 10x the same way, while acid slime #5 (hit-spell on slot 0 at 100%) was unaffected,
// which is why the slime checked out clean and the spiders did not.
public sealed class MonsterHitSpellSlotTests
{
    // Percent is the stock cumulative threshold: slot 0 takes rolls <= 95, slot 1 the rest.
    private static MonsterInstance GreySpiderLike() => new()
    {
        Template = new Monster
        {
            Name = "grey spider",
            Energy = 1000,
            Attacks =
            [
                new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 95, Min = 1, Max = 1, HitSpell = 0 },
                new MonsterAttack { SlotIndex = 1, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1, HitSpell = 80 },
            ],
        },
        DisplayName = "grey spider",
        CurrentHP = 1_000_000,
        MaxHP = 1_000_000,
    };

    private static Player Target() => new()
    {
        Name = "Duhh",
        MaxHP = 1_000_000,
        CurrentHP = 1_000_000,
        ArmourClass = 0,
        Dodge = 0,
    };

    [Fact]
    public void Hit_spell_on_a_secondary_slot_procs_at_that_slots_rate_not_every_round()
    {
        var db = new InMemoryGameDatabase();
        var player = Target();

        int rounds = 20_000, damagingRounds = 0, procs = 0;
        for (int i = 0; i < rounds; i++)
        {
            player.CurrentHP = player.MaxHP;
            var monster = GreySpiderLike();
            monster.ResetEnergy();
            monster.PrepareCombatRound();

            var result = CombatEngine.MonsterAttack(monster, player, db.Items, db.Messages);
            if (result.TotalDamage <= 0)
                continue;

            damagingRounds++;
            procs += result.PendingHitSpells.Count;
        }

        Assert.True(damagingRounds > 1000, $"expected plenty of landed rounds, got {damagingRounds}");

        // Slot 1 owns rolls 96..99 out of [1,99] ⇒ ~4-5% of swings. Generous band so this pins the
        // magnitude (single digits, not 100%) without being flaky.
        double procRate = (double)procs / damagingRounds;
        Assert.InRange(procRate, 0.01, 0.12);
    }

    [Fact]
    public void Hit_spell_on_the_only_slot_still_procs_on_every_landed_round()
    {
        // The acid slime shape: one slot at 100% carrying the spell. This must NOT regress — its acid
        // fires on every landed swing in stock.
        var db = new InMemoryGameDatabase();
        var player = Target();

        int damagingRounds = 0, procs = 0;
        for (int i = 0; i < 2_000; i++)
        {
            player.CurrentHP = player.MaxHP;
            var monster = new MonsterInstance
            {
                Template = new Monster
                {
                    Name = "acid slime",
                    Energy = 1000,
                    Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1, HitSpell = 126 }],
                },
                DisplayName = "acid slime",
                CurrentHP = 1_000_000,
                MaxHP = 1_000_000,
            };
            monster.ResetEnergy();
            monster.PrepareCombatRound();

            var result = CombatEngine.MonsterAttack(monster, player, db.Items, db.Messages);
            if (result.TotalDamage <= 0)
                continue;

            damagingRounds++;
            procs += result.PendingHitSpells.Count;
            Assert.All(result.PendingHitSpells, id => Assert.Equal(126, id));
        }

        Assert.True(damagingRounds > 100, $"expected landed rounds, got {damagingRounds}");
        Assert.Equal(damagingRounds, procs);
    }

    [Fact]
    public void A_round_that_lands_nothing_owes_no_hit_spell()
    {
        var db = new InMemoryGameDatabase();
        // Accuracy 0 against a defended target: nothing connects, so nothing may proc.
        var player = new Player { Name = "Tank", MaxHP = 1_000_000, CurrentHP = 1_000_000, ArmourClass = 200, Dodge = 0 };
        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "grey spider",
                Energy = 1000,
                Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 1, Percent = 100, Min = 1, Max = 1, HitSpell = 80 }],
            },
            DisplayName = "grey spider",
            CurrentHP = 1_000_000,
            MaxHP = 1_000_000,
        };
        monster.ResetEnergy();
        monster.PrepareCombatRound();

        var result = CombatEngine.MonsterAttack(monster, player, db.Items, db.Messages);
        if (result.TotalDamage <= 0)
            Assert.Empty(result.PendingHitSpells);
    }
}
