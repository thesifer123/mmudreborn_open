using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class CombatEngineMonsterDamageTests
{
    // A monster whose template Energy is 0 has NO energy pool: the energy update caps current
    // energy at the Energy field (0), so it can never afford a swing and the attack zeroes every
    // attack. Such a monster never deals damage — proactively OR in retaliation — even with a full attack
    // slot and a huge HP bar. This is Balthazar (#263, Energy 0, 9999 HP, atk 25-125): you can hit him all
    // day and he never hits back. Regression for the bug where GetCombatEnergyCap substituted a default
    // 1000-point pool for Energy==0 and made every such NPC (barmaids/healers/shops/Balthazar) fight.
    [Fact]
    public void Energy_zero_monster_never_swings_even_with_an_attack_slot()
    {
        var player = new Player { Name = "Target", CurrentHP = 5000, MaxHP = 5000, ArmourClass = 0, Agility = 0, DamageResist = 0 };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "Balthazar",
                Energy = 0,
                HP = 9999,
                Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 1000, Percent = 100, Min = 25, Max = 125, Energy = 200 }],
            },
            DisplayName = "Balthazar",
            CurrentHP = 9999,
            MaxHP = 9999,
        };

        Assert.Equal(0, monster.GetCombatEnergyCap());

        for (int round = 0; round < 100; round++)
        {
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player);
            Assert.Equal(0, result.Hits);
            Assert.Equal(0, result.TotalDamage);
        }

        Assert.Equal(5000, player.CurrentHP);
    }

    [Fact]
    public void Monster_attack_percentages_choose_a_single_cumulative_attack_slot()
    {
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 500,
            MaxHP = 500,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "test beast",
                Energy = 1000,
                DamageResist = 1,
                Attacks =
                [
                    new MonsterAttack
                    {
                        SlotIndex = 0,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 80,
                        Min = 2,
                        Max = 2,
                    },
                    new MonsterAttack
                    {
                        SlotIndex = 1,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 98,
                        Min = 7,
                        Max = 7,
                    },
                    new MonsterAttack
                    {
                        SlotIndex = 2,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 100,
                        Min = 11,
                        Max = 11,
                    },
                ],
            },
            DisplayName = "test beast",
            CurrentHP = 12,
            MaxHP = 12,
        };

        var observedDamages = new HashSet<int>();

        for (int attempt = 0; attempt < 500; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player);

            Assert.InRange(result.Hits, 0, 1);

            if (result.Hits == 0)
                continue;

            Assert.Contains(result.TotalDamage, new[] { 2, 7, 11 });
            Assert.DoesNotContain(result.TotalDamage, new[] { 9, 13, 18, 20 });
            observedDamages.Add(result.TotalDamage);
        }

        Assert.Equal(3, observedDamages.Count);
    }

    [Fact]
    public void Monster_attack_never_selects_an_empty_AtkType0_slot_when_a_real_attack_exists()
    {
        // Repro of the live barmaid (monster 248): a real melee attack in slot 0 (AtkType 1) plus an
        // empty slot 1 (AtkType 0) that still carries leftover Min/Max and a stray spell HitMessage
        // (14: "You cast %s at %s for %d damage!"). The empty slot must never be rolled — otherwise
        // the player sees a phantom "You cast barmaid ..." hit for the empty slot's garbage damage.
        // The swing loop re-rolls past AtkType==0 slots.
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var messages = new Dictionary<int, RoomMessage>
        {
            [8465] = new RoomMessage { Number = 8465, Line1 = "%s smacks you for %d damage!", Line2 = "%s smacks %s for %s damage!" },
            [14] = new RoomMessage { Number = 14, Line1 = "You cast %s at %s for %d damage!", Line2 = "%s casts %s on you for %d damage!" },
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "barmaid",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 10, Min = 1, Max = 2, HitMessageId = 8465 },
                    new MonsterAttack { SlotIndex = 1, Type = 0, Accuracy = 500, Percent = 100, Min = 100, Max = 140, HitMessageId = 14 },
                ],
            },
            DisplayName = "barmaid",
            CurrentHP = 50,
            MaxHP = 50,
        };

        for (int attempt = 0; attempt < 400; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player, items: null, messages: messages);

            foreach (var line in result.Messages.Concat(result.RoomMessages))
                Assert.DoesNotContain("cast", line, StringComparison.OrdinalIgnoreCase);

            // The empty slot's Min/Max is 100-140; the real slot deals at most 2. Any damage above 2
            // means the phantom AtkType=0 slot was selected.
            Assert.True(result.TotalDamage <= 2, $"Empty AtkType=0 slot dealt phantom damage {result.TotalDamage}.");
        }
    }

    [Fact]
    public void Monster_with_only_empty_AtkType0_slots_never_attacks()
    {
        // Bug #191 repro: Inquisitor Fulgore (#532) is align-2 (aggros on sight) but BOTH his attack
        // slots are AtkType 0 with leftover damage (3-14, 8-21). The stock swing loop re-rolls
        // past type-0 slots unconditionally, so an all-type-0 monster lands zero swings — he engages
        // but can never hit. An earlier hasRealAttack fallback let him melee with the leftover numbers.
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "Inquisitor Fulgore",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack { SlotIndex = 0, Type = 0, Accuracy = 125, Percent = 75, Min = 3, Max = 14, Energy = 500, HitMessageId = 995 },
                    new MonsterAttack { SlotIndex = 1, Type = 0, Accuracy = 125, Percent = 100, Min = 8, Max = 21, Energy = 1000, HitMessageId = 996, HitSpell = 318 },
                ],
            },
            DisplayName = "Inquisitor Fulgore",
            CurrentHP = 100,
            MaxHP = 100,
        };

        for (int attempt = 0; attempt < 400; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player);

            Assert.Equal(0, result.Hits);
            Assert.Equal(0, result.TotalDamage);
            Assert.Empty(result.Messages);
            Assert.Empty(result.RoomMessages);
        }
    }

    [Theory]
    // roll above Active% — no free swing regardless of alignment.
    [InlineData(2, 50, 80, true, 51, false)]   // aggressive, engaged, roll 51 > Active 50
    [InlineData(2, 50, 80, false, 80, false)]  // aggressive, roll 80 > Active 50
    // aggressive mobs (align 1/2/5) swing whether or not already engaged, when roll <= Active.
    [InlineData(2, 50, 80, false, 50, true)]   // roll == Active boundary passes
    [InlineData(1, 45, 80, false, 0, true)]
    [InlineData(5, 45, 80, true, 10, true)]
    // townsfolk / peaceful / guards (0/3/4) only swing if already engaged.
    [InlineData(4, 100, 80, false, 0, false)]  // guard, not engaged — no swing even at Active 100
    [InlineData(4, 100, 80, true, 0, true)]    // guard, engaged — swings
    [InlineData(0, 100, 80, true, 0, true)]    // townsfolk engaged
    [InlineData(3, 100, 80, false, 0, false)]  // peaceful, not engaged
    // evil NPCs (align 6) are skipped against players with EvilPoints >= 80.
    [InlineData(6, 100, 79, false, 0, true)]   // EvilPoints 79 < 80 — eligible
    [InlineData(6, 100, 80, false, 0, false)]  // EvilPoints 80 — excluded
    [InlineData(6, 100, 200, true, 0, false)]  // high EvilPoints — excluded even if engaged
    public void IsEligibleDepartingFreeAttacker_matches_stock_alignment_and_chance_rules(
        int align, int active, int playerEvilPoints, bool engaged, int roll, bool expected)
    {
        Assert.Equal(expected, CombatEngine.IsEligibleDepartingFreeAttacker(align, active, engaged, playerEvilPoints, roll));
    }

    [Theory]
    // A type-37 (summoned "angel") monster is folded into the engaged-only branch regardless of
    // its alignment — it only free-swings a departing player it is already fighting.
    [InlineData(2, false, false)]  // aggressive align, NOT engaged → no swing (angel override)
    [InlineData(2, true, true)]    // aggressive align, engaged → swings
    [InlineData(1, false, false)]  // would normally always-swing, but angel gates on engagement
    public void IsEligibleDepartingFreeAttacker_folds_type37_angel_into_engaged_only(int align, bool engaged, bool expected)
    {
        // roll 0 <= aggression 100, EvilPoints 0 — only the type override decides the outcome.
        Assert.Equal(expected, CombatEngine.IsEligibleDepartingFreeAttacker(align, 100, engaged, 0, 0, monsterGroup: 37));
    }

    [Fact]
    public void Monster_to_hit_miss_does_not_render_as_a_dodge()
    {
        // Repro of "The small kobold thief stabs you with their shortsword, but you dodge!" on EVERY
        // avoided swing. Stock renders a to-hit miss (outcome 0) and a
        // skill-dodge (outcome 3) as DIFFERENT messages — only the dodge says "...but you dodge!".
        // Here the player's DG dodge rating is 0 (MaxEncumbrance 0 + Dodge 0 → GetDodge()==0), so the
        // post-hit skill-dodge gate can never fire; every avoided swing is a pure to-hit miss and must
        // NOT mention dodging. A high dodge-SKILL forces the to-hit roll to miss most swings.
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 100000,
            MaxHP = 100000,
            ArmourClass = 0,
            Dodge = 0,
            MaxEncumbrance = 0,        // GetDodge() short-circuits to Dodge (==0): no skill-dodge ever
            DodgeSkillAbility = 250,   // huge to-hit denominator → almost every swing misses
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "kobold thief",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 50, Percent = 100, Min = 1, Max = 1 },
                ],
            },
            DisplayName = "kobold thief",
            CurrentHP = 50,
            MaxHP = 50,
        };

        int avoidedLines = 0;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player, items: null, messages: null);

            foreach (var line in result.Messages.Concat(result.RoomMessages))
            {
                // A to-hit miss renders "The kobold thief lunges at you!" — never "dodge".
                Assert.DoesNotContain("dodge", line, StringComparison.OrdinalIgnoreCase);
                if (line.Contains("lunges", StringComparison.OrdinalIgnoreCase))
                    avoidedLines++;
            }
        }

        Assert.True(avoidedLines > 0, "Expected to-hit miss messages to be exercised (DG=0 → no skill-dodge).");
    }

    [Fact]
    public void Monster_dodge_message_fills_the_attack_verb_instead_of_an_empty_slot()
    {
        // Repro of "barmaid  you, but you dodge out of the way!": a dodge template that leads with
        // "<monster> <verb> you" (placeholders [%s name, %s verb]) but has no "with their %s" weapon
        // clause used to render the verb slot as empty. The verb must come from the hit template
        // ("smacks") or weapon type, matching the explicit-weapon branch.
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Agility = 250,   // very high dodge so almost every swing misses -> dodge message path
            Dodge = 250,
            DamageResist = 0,
        };

        var messages = new Dictionary<int, RoomMessage>
        {
            [100] = new RoomMessage { Number = 100, Line1 = "%s smacks you for %d damage!", Line2 = "%s smacks %s for %s damage!" },
            [101] = new RoomMessage
            {
                Number = 101,
                Line1 = "%s %s you, but your armour deflects the blow!",
                Line2 = "%s %s %s, but %s armour deflects the blow!",
                Line3 = "%s %s you, but you dodge out of the way!",
            },
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "barmaid",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 1, Percent = 100, Min = 1, Max = 1, HitMessageId = 100, DodgeMessageId = 101, MissMessageId = 101 },
                ],
            },
            DisplayName = "barmaid",
            CurrentHP = 50,
            MaxHP = 50,
        };

        int dodgeLinesSeen = 0;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player, items: null, messages: messages);

            foreach (var line in result.Messages.Concat(result.RoomMessages))
            {
                if (!line.Contains("dodge out of the way", StringComparison.OrdinalIgnoreCase)
                    && !line.Contains("deflects the blow", StringComparison.OrdinalIgnoreCase))
                    continue;

                dodgeLinesSeen++;
                // The verb must be present, and the monster name must never sit directly against the
                // pronoun with the verb gap collapsed to a double space.
                Assert.Contains("smacks", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("barmaid  you", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("barmaid  Target", line, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(dodgeLinesSeen > 0, "Expected the dodge/deflect message path to be exercised.");
    }

    [Fact]
    public void Monster_dodge_message_does_not_double_a_baked_in_verb_prefix()
    {
        // Repro of "The wight slashesclaws at you, but you dodge out of the way!" (msg 8308 L3).
        // Most stock dodge templates bake the verb into the text behind a GLUED adverb-prefix slot
        // ("%sclaws") rather than a standalone verb slot ("%s claws"). The previous fix injected the
        // hit-template verb ("slashes") into that prefix, producing the doubled "slashesclaws". The
        // glued slot must stay empty so only the baked-in verb survives.
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Agility = 250,   // very high dodge so almost every swing misses -> dodge message path
            Dodge = 250,
            DamageResist = 0,
        };

        var messages = new Dictionary<int, RoomMessage>
        {
            [100] = new RoomMessage { Number = 100, Line1 = "%s slashes you for %d damage!", Line2 = "%s slashes %s for %s damage!" },
            [101] = new RoomMessage
            {
                Number = 101,
                Line1 = "The %s %sclaws at %s, %sbut %s dodges out of the way!",
                Line2 = "The %s %sclaws at %s, %sbut %s dodges out of the way!",
                Line3 = "The %s %sclaws at you, %sbut you dodge out of the way!",
            },
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "wight",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 1, Percent = 100, Min = 1, Max = 1, HitMessageId = 100, DodgeMessageId = 101, MissMessageId = 101 },
                ],
            },
            DisplayName = "wight",
            CurrentHP = 50,
            MaxHP = 50,
        };

        int dodgeLinesSeen = 0;
        for (int attempt = 0; attempt < 400; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player, items: null, messages: messages);

            foreach (var line in result.Messages.Concat(result.RoomMessages))
            {
                if (!line.Contains("out of the way", StringComparison.OrdinalIgnoreCase))
                    continue;

                dodgeLinesSeen++;
                // The baked-in verb must survive intact, with no injected verb glued in front of it.
                Assert.Contains("claws at", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("slashesclaws", line, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("slashes claws", line, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.True(dodgeLinesSeen > 0, "Expected the dodge message path to be exercised.");
    }

    [Fact]
    public void Monster_attack_ability_4_adds_to_attack_damage_bounds()
    {
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 500,
            MaxHP = 500,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "test beast",
                Energy = 1000,
                Abilities = new Dictionary<int, int>
                {
                    [4] = 5,
                },
                Attacks =
                [
                    new MonsterAttack
                    {
                        SlotIndex = 0,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 100,
                        Min = 2,
                        Max = 2,
                    },
                ],
            },
            DisplayName = "test beast",
            CurrentHP = 12,
            MaxHP = 12,
        };

        var result = ExecuteUntilHit(() =>
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            return CombatEngine.MonsterAttack(monster, player);
        });

        Assert.Equal(7, result.TotalDamage);
    }

    [Fact]
    public void Monster_attack_without_ability_4_keeps_base_attack_damage_bounds()
    {
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 500,
            MaxHP = 500,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "test beast",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack
                    {
                        SlotIndex = 0,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 100,
                        Min = 2,
                        Max = 2,
                    },
                ],
            },
            DisplayName = "test beast",
            CurrentHP = 12,
            MaxHP = 12,
        };

        var result = ExecuteUntilHit(() =>
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            return CombatEngine.MonsterAttack(monster, player);
        });

        Assert.Equal(2, result.TotalDamage);
    }

    [Fact]
    public void Monster_attack_can_spend_multiple_energy_swings_in_one_round()
    {
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 500,
            MaxHP = 500,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "test beast",
                Energy = 6,
                Attacks =
                [
                    new MonsterAttack
                    {
                        SlotIndex = 0,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 100,
                        Min = 2,
                        Max = 2,
                        Energy = 3,
                    },
                ],
            },
            DisplayName = "test beast",
            CurrentHP = 12,
            MaxHP = 12,
        };

        monster.ResetEnergy();
        monster.PrepareCombatRound();

        var result = CombatEngine.MonsterAttack(monster, player);

        Assert.NotEmpty(result.Messages);
        Assert.Equal(0, monster.CurrentEnergy);
    }

    [Fact]
    public void Monster_attack_carries_energy_remainder_into_the_next_round()
    {
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 500,
            MaxHP = 500,
            ArmourClass = 0,
            Agility = 0,
            DamageResist = 0,
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "test beast",
                Energy = 5,
                Attacks =
                [
                    new MonsterAttack
                    {
                        SlotIndex = 0,
                        Type = 1,
                        Accuracy = 500,
                        Percent = 100,
                        Min = 2,
                        Max = 2,
                        Energy = 3,
                    },
                ],
            },
            DisplayName = "test beast",
            CurrentHP = 12,
            MaxHP = 12,
        };

        monster.ResetEnergy();

        // Round 1: pool 5, EU 3 -> one swing, 2 energy carried over. Energy spent per swing is
        // deterministic regardless of hit/miss, so assert on the carried remainder (the point of the
        // test) rather than Hits+Misses, which MonsterAttack doesn't track for misses.
        monster.PrepareCombatRound();
        CombatEngine.MonsterAttack(monster, player);
        Assert.Equal(2, monster.CurrentEnergy);

        // Round 2: carried 2 + cap 5 = 7 -> two swings (6 spent), 1 energy left.
        monster.PrepareCombatRound();
        CombatEngine.MonsterAttack(monster, player);
        Assert.Equal(1, monster.CurrentEnergy);
    }

    [Fact]
    public void Player_recalculate_equipment_uses_equipped_item_stats_instead_of_stale_values()
    {
        var armour = new Item
        {
            Number = 9001,
            Name = "test plate",
            ArmourClass = 40,
            DamageResist = 5,
        };

        var database = new Fakes.InMemoryGameDatabase();
        database.Items[armour.Number] = armour;

        var player = new Player
        {
            ArmourClass = 999,
            DamageResist = 999,
            Equipment = new Dictionary<string, int>
            {
                ["body"] = armour.Number,
            },
        };

        player.RecalculateEquipment(database);

        // ArmourClass is now stored in the fighter (display) scale: worn-sum / 10.
        // armour.ArmourClass is the raw AC byte (40), so the player ends up with 4.
        Assert.Equal(armour.ArmourClass / 10, player.ArmourClass);
        Assert.Equal(armour.DamageResist, player.DamageResist);
    }

    [Fact]
    public void RecalculateEquipment_divides_worn_ac_sum_by_ten_matching_stock_fighter_field()
    {
        // The fighter marshal sums the AC bytes across all 20 worn slots,
        // then divides by 10. The /10 is applied ONCE on the accumulated sum, not
        // per-item — odd/even ones digits in the per-item bytes share the same truncation as the
        // stock would.
        var helm = new Item { Number = 1, Name = "helm", ArmourClass = 27 };
        var body = new Item { Number = 2, Name = "body", ArmourClass = 38 };
        var legs = new Item { Number = 3, Name = "legs", ArmourClass = 9 };
        var database = new Fakes.InMemoryGameDatabase();
        database.Items[helm.Number] = helm;
        database.Items[body.Number] = body;
        database.Items[legs.Number] = legs;

        var player = new Player
        {
            Equipment = new Dictionary<string, int>
            {
                ["head"] = helm.Number,
                ["body"] = body.Number,
                ["legs"] = legs.Number,
            },
        };

        player.RecalculateEquipment(database);

        // 27 + 38 + 9 = 74 → /10 (single divide on sum) = 7.
        Assert.Equal(7, player.ArmourClass);
    }

    [Fact]
    public void GetTotalAC_combines_worn_and_natural_ac_in_display_scale()
    {
        // The fighter AC is worn (already /10) plus the ACAbility accumulator,
        // which is summed RAW (no ×10). C# mirrors that here.
        var armour = new Item { Number = 10, Name = "plate", ArmourClass = 230 };
        var database = new Fakes.InMemoryGameDatabase();
        database.Items[armour.Number] = armour;

        var player = new Player
        {
            Equipment = new Dictionary<string, int> { ["body"] = armour.Number },
        };
        player.RecalculateEquipment(database);

        // Without natural armour: 230 / 10 = 23.
        Assert.Equal(23, player.ArmourClass);
        Assert.Equal(23, player.GetTotalAC());

        // Simulate ability 2 contributing +5 AC (e.g. troll race natural armour).
        player.ACAbility = 5;
        Assert.Equal(28, player.GetTotalAC());
    }

    private static CombatResult ExecuteUntilHit(Func<CombatResult> attack)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var result = attack();
            if (result.Hits > 0)
                return result;
        }

        throw new Xunit.Sdk.XunitException("Expected combat to hit during the retry window.");
    }

    [Fact]
    public void Monster_haste_slow_percent_reflects_active_buff_and_clears_on_expiry()
    {
        var db = new InMemoryGameDatabase();
        db.Spells[59] = new GameSpell { Number = 59, Abilities = new Dictionary<int, int> { [87] = 0 } };  // slow
        var monster = new MonsterInstance { Template = new Monster { Name = "rat", Energy = 1000 } };

        Assert.Equal(100, monster.HasteSlowPercent);

        // slow stores its rolled magnitude (125) as the ability-87 value -> EU x1.25.
        monster.AddOrRefreshActiveSpell(db, spellId: 59, magnitude: 125, duration: 2);
        Assert.Equal(125, monster.HasteSlowPercent);

        monster.TickActiveSpells(db);              // duration 2 -> 1, still active
        Assert.Equal(125, monster.HasteSlowPercent);
        monster.TickActiveSpells(db);              // 1 -> 0, expires
        Assert.Equal(100, monster.HasteSlowPercent);
        Assert.Empty(monster.ActiveSpells);
    }

    [Fact]
    public void Slowed_monster_spends_more_energy_per_swing_and_makes_fewer_swings()
    {
        var db = new InMemoryGameDatabase();
        db.Spells[59] = new GameSpell { Number = 59, Abilities = new Dictionary<int, int> { [87] = 0 } };

        var player = new Player { Name = "Target", CurrentHP = 5000, MaxHP = 5000, ArmourClass = 0, Agility = 0, DamageResist = 0 };

        static MonsterInstance MakeBeast() => new()
        {
            Template = new Monster
            {
                Name = "test beast",
                Energy = 1000,
                Attacks = [new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1, Energy = 300 }],
            },
            DisplayName = "test beast",
            CurrentHP = 50,
            MaxHP = 50,
        };

        // Normal: EU 300, a 1000-energy round -> 3 swings (900 spent), 100 left.
        var normal = MakeBeast();
        normal.PrepareCombatRound();
        CombatEngine.MonsterAttack(normal, player);
        Assert.Equal(100, normal.CurrentEnergy);

        // Slowed (ability 87 = 125): EU 300 -> 375, so the same 1000-energy round -> only 2 swings
        // (750 spent), 250 left. Fewer swings than the unslowed beast.
        player.CurrentHP = player.MaxHP;
        var slowed = MakeBeast();
        slowed.AddOrRefreshActiveSpell(db, spellId: 59, magnitude: 125, duration: 5);
        Assert.Equal(125, slowed.HasteSlowPercent);
        slowed.PrepareCombatRound();
        CombatEngine.MonsterAttack(slowed, player);
        Assert.Equal(250, slowed.CurrentEnergy);
    }

    [Fact]
    public void Monster_buff_confers_arbitrary_abilities_to_combat_not_just_haste_slow()
    {
        var db = new InMemoryGameDatabase();
        // A buff granting a damage bonus (ability 2, summed) and an accuracy/bless bonus (22, MAX-set).
        db.Spells[800] = new GameSpell { Number = 800, Abilities = new Dictionary<int, int> { [2] = 5, [22] = 10 } };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "ogre",
                Energy = 1000,
                DamageResist = 3,
                Abilities = new Dictionary<int, int> { [2] = 1, [22] = 4 },
            },
        };

        // Before the buff: template intrinsics only.
        Assert.Equal(1, monster.GetEffectiveAbility(2));
        Assert.Equal(4, monster.GetEffectiveAbility(22));

        monster.AddOrRefreshActiveSpell(db, spellId: 800, magnitude: 0, duration: 1);

        // The effective-ability aggregation handles any ability, with the right rule per id:
        Assert.Equal(6, monster.GetEffectiveAbility(2));      // 1 (template) + 5 (buff) — summed
        Assert.Equal(10, monster.GetEffectiveAbility(22));  // max(4 template, 10 buff) — MAX-set

        // And it clears back to the template intrinsics when the buff expires.
        monster.TickActiveSpells(db);
        Assert.Empty(monster.ActiveSpells);
        Assert.Equal(1, monster.GetEffectiveAbility(2));
        Assert.Equal(4, monster.GetEffectiveAbility(22));
    }

    [Fact]
    public void Monster_buff_modifies_effective_defense_ac_dodge_and_magic_resist()
    {
        var db = new InMemoryGameDatabase();
        // Monster DV/DG/MR incorporate abilities 2 / 34 / 36 from template, items, AND buffs.
        db.Spells[801] = new GameSpell { Number = 801, Abilities = new Dictionary<int, int> { [2] = 7, [34] = 3, [36] = 20 } };
        var monster = new MonsterInstance
        {
            Template = new Monster { Name = "golem", Energy = 1000, ArmourClass = 40, BSDefense = 5, MagicRes = 10 },
        };

        Assert.Equal(40, monster.EffectiveArmourClass);
        Assert.Equal(0, monster.EffectiveDodge);           // DG = ability 34 only (BSDefense is NOT dodge)
        Assert.Equal(10, monster.EffectiveMagicResist);
        // BSDefense is the backstab defense: backstab AC = (AC>>1) + BSDefense*2 = (40>>1) + 5*2 = 30.
        Assert.Equal(30, monster.GetMonsterBackstabArmourClass());

        monster.AddOrRefreshActiveSpell(db, spellId: 801, magnitude: 0, duration: 5);

        Assert.Equal(47, monster.EffectiveArmourClass);    // 40 + 7 (ability 2)
        Assert.Equal(3, monster.EffectiveDodge);           // 0 + 3 (ability 34)
        Assert.Equal(30, monster.EffectiveMagicResist);    // 10 + 20 (ability 36)
        Assert.Equal(33, monster.GetMonsterBackstabArmourClass()); // (47>>1) + 5*2 = 33
    }

    [Fact]
    public void Monster_create_rolls_carried_treasure_at_spawn()
    {
        var monster = MonsterInstance.Create(CreateTreasureMonsterTemplate(), mapNumber: 1, roomNumber: 1);

        Assert.Equal([4242], monster.CarriedDropItemIds);
        Assert.Equal(1, monster.CarriedGold);
        Assert.Equal(1, monster.CarriedSilver);
        Assert.Equal(1, monster.CarriedCopper);
        Assert.Equal(1, monster.CarriedPlatinum);
        Assert.Equal(1, monster.CarriedRunic);
    }

    [Fact]
    public void Monster_death_rewards_consume_carried_treasure_once_and_allow_permanent_reroll()
    {
        var template = CreateTreasureMonsterTemplate();
        var monster = MonsterInstance.Create(template, mapNumber: 1, roomNumber: 1, isPermanentNPC: true);
        var cls = new CharacterClass { CombatLvl = 20 };
        var attacker = new Player
        {
            Name = "TreasureTester",
            Level = 50,
            Strength = 90,
            Agility = 90,
            PartyAccuracyModifier = 1000,
            MaxHP = 100,
            CurrentHP = 100,
        };
        var weapon = new Item { Min = 5, Max = 5, Accy = 1000, WeaponType = 1 };

        var result = ExecuteUntil(
            () =>
            {
                attacker.PrepareCombatRound();
                return CombatEngine.PlayerAttack(attacker, monster, cls, weapon, messages: null);
            },
            combat => combat.TargetKilled);

        Assert.Equal([4242], result.Drops);
        Assert.Equal(1, result.GoldDropped);
        Assert.Equal(1, result.SilverDropped);
        Assert.Equal(1, result.CopperDropped);
        Assert.Equal(1, result.PlatinumDropped);
        Assert.Equal(1, result.RunicDropped);
        Assert.Empty(monster.CarriedDropItemIds);
        Assert.Equal(0, monster.CarriedGold);

        monster.CurrentHP = monster.MaxHP;
        monster.RollCarriedTreasure();

        Assert.Equal([4242], monster.CarriedDropItemIds);
        Assert.Equal(1, monster.CarriedGold);
    }

    [Fact]
    public void Mystic_unarmed_swings_come_from_the_stamina_pool_not_legacy_get_swings()
    {
        var player = new Player
        {
            Name = "Monk",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
            CurrentEnergy = 1000,
        };
        var cls = new CharacterClass { CombatLvl = 15 };
        var monster = new MonsterInstance
        {
            Template = new Monster { Name = "dummy", HP = 100000, ArmourClass = 0, BSDefense = 0 },
            DisplayName = "dummy",
            CurrentHP = 100000,
            MaxHP = 100000,
        };

        var result = CombatEngine.MysticAttack(player, monster, cls, "punch");

        // Energy model: a high-level mystic's unarmed EU floors to 200, so a full 1000 pool yields 5
        // swings (not GetSwings' up-to-10), and the shared stamina pool is drained.
        Assert.Equal(5, result.Hits + result.Misses);
        Assert.Equal(0, player.CurrentEnergy);
    }

    [Theory]
    [InlineData("punch")]
    [InlineData("kick")]
    [InlineData("jumpkick")]
    public void Mystic_unarmed_attack_uses_stat_derived_base_accuracy_not_zero(string attackType)
    {
        // Regression: CombatEngine.MysticAttack used to pass only PartyAccuracyModifier (usually 0)
        // to CalculateAttack, so AV≈0 → hit chance pinned to the 10 floor (~9% hits). The stock
        // fighter marshal computes the same stat-derived AV for
        // unarmed types 1/2/3 as for weapons. Against a monster with realistic AC, a high-level
        // mystic with strong stats should land most of their swings, not whiff almost every time.
        var player = new Player
        {
            Name = "Monk",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
            CurrentEnergy = 1000,
        };
        var cls = new CharacterClass { CombatLvl = 15 };

        int totalHits = 0;
        int totalSwings = 0;
        for (int trial = 0; trial < 50; trial++)
        {
            player.CurrentEnergy = 1000;
            var monster = new MonsterInstance
            {
                Template = new Monster { Name = "dummy", HP = 100000, ArmourClass = 30, BSDefense = 0 },
                DisplayName = "dummy",
                CurrentHP = 100000,
                MaxHP = 100000,
            };

            var result = CombatEngine.MysticAttack(player, monster, cls, attackType);
            totalHits += result.Hits;
            totalSwings += result.Hits + result.Misses;
        }

        // With AV ≈ 321 vs AC 30, computed hit chance is ~99%. Even allowing for randomness, the
        // hit rate must be far above the 10 floor that the bug forced. Threshold is generous to
        // avoid flakes; the bug produced ~10%, the fix produces ~99%.
        double hitRate = (double)totalHits / totalSwings;
        Assert.True(hitRate > 0.75, $"Expected hit rate > 75% with base accuracy, got {hitRate:P0} ({totalHits}/{totalSwings}).");
    }

    [Fact]
    public void Party_rank_accuracy_modifier_applies_to_punch_but_is_gated_off_for_jumpkick()
    {
        // The party front/back-rank to-hit
        // modifier is added for every attack type EXCEPT jumpkick (attack-type 3), where the to-hit
        // half is skipped (the defence half still applies). A weak attacker carrying a huge front-rank
        // accuracy bonus should land PUNCH almost every swing, but their JUMPKICK must stay at the
        // hit floor because the bonus is gated off.
        var cls = new CharacterClass { CombatLvl = 1 };

        (int hits, int swings) Run(string attackType)
        {
            int hits = 0, swings = 0;
            for (int trial = 0; trial < 50; trial++)
            {
                var player = new Player
                {
                    Name = "Weakling",
                    Level = 1,
                    Strength = 10,
                    Agility = 10,
                    MaxHP = 100,
                    CurrentHP = 100,
                    CurrentEnergy = 1000,
                    PartyAccuracyModifier = 100000, // exaggerated front-rank bonus so it dominates the roll
                };
                var monster = new MonsterInstance
                {
                    Template = new Monster { Name = "dummy", HP = 1000000, ArmourClass = 50, BSDefense = 0 },
                    DisplayName = "dummy",
                    CurrentHP = 1000000,
                    MaxHP = 1000000,
                };

                var result = CombatEngine.MysticAttack(player, monster, cls, attackType);
                hits += result.Hits;
                swings += result.Hits + result.Misses;
            }

            return (hits, swings);
        }

        var (punchHits, punchSwings) = Run("punch");
        var (jumpHits, jumpSwings) = Run("jumpkick");

        double punchRate = (double)punchHits / punchSwings;
        double jumpRate = (double)jumpHits / jumpSwings;

        // Punch gets the +100000 → hit chance pinned to ~99%. Jumpkick gates it off → a weak attacker
        // vs AC 50 stays near the 10% floor. Thresholds are generous to avoid RNG flakes.
        Assert.True(punchRate > 0.75, $"Punch should hit with the party bonus, got {punchRate:P0} ({punchHits}/{punchSwings}).");
        Assert.True(jumpRate < 0.30, $"Jumpkick must NOT get the party bonus, got {jumpRate:P0} ({jumpHits}/{jumpSwings}).");
    }

    private static Monster CreateTreasureMonsterTemplate() => new()
    {
        Name = "treasure rat",
        HP = 1,
        EXP = 7,
        Gold = 1,
        Silver = 1,
        Copper = 1,
        Platinum = 1,
        Runic = 1,
        Drops = [new MonsterDrop { ItemId = 4242, Percent = 100 }],
    };

    private static CombatResult ExecuteUntil(Func<CombatResult> attack, Func<CombatResult, bool> predicate)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var result = attack();
            if (predicate(result))
                return result;
        }

        throw new Xunit.Sdk.XunitException("Expected combat predicate to pass during the retry window.");
    }

    // After the attack-type branch sets weapon /
    // unarmed min/max, the Strength damage adjustment applies UNCONDITIONALLY to every fighter.
    // Verified against stock:
    //   max += (Str - 50) / 10
    //   if (((Str - 100) / 10) * 2 > 0): min += ((Str - 100) / 10) * 2
    //   if (min > max) min = max
    //   clamp(min, max) >= 0
    // These cases lock in the corner behaviour.
    [Theory]
    [InlineData(50, 10, 20, 10, 20)]      // Str 50: no bonus, no penalty.
    [InlineData(80, 10, 20, 10, 23)]      // Str 80: max += (80-50)/10 = +3; min bonus ((80-100)/10)*2 = -4, gated >0 so skipped.
    [InlineData(100, 10, 20, 10, 25)]     // Str 100: max += +5; min bonus 0, gated >0 so skipped.
    [InlineData(150, 10, 20, 20, 30)]     // Str 150: max += +10; min += ((150-100)/10)*2 = +10.
    [InlineData(200, 10, 20, 30, 35)]     // Str 200: max += +15; min += +20. Still min<max ✓.
    [InlineData(30, 10, 20, 10, 18)]      // Str 30: max += (30-50)/10 = -2; min bonus -14 (skipped). min<=max ✓.
    [InlineData(50, 4, 1, 1, 1)]          // Pathological inputs (max<min): final min clamped down to max.
    [InlineData(0, 5, 10, 5, 5)]          // Str 0: max += -5 = 5; min bonus skipped. min<=max (5==5) ✓.
    [InlineData(0, 0, 5, 0, 0)]           // Str 0 + base max 5: max += -5 = 0; min stays 0; clamped to 0/0.
    public void ApplyStrengthDamageBonus_matches_stock_formula(int strength, int baseMin, int baseMax, int expectedMin, int expectedMax)
    {
        var (min, max) = CombatEngine.ApplyStrengthDamageBonus(baseMin, baseMax, strength);

        Assert.Equal(expectedMin, min);
        Assert.Equal(expectedMax, max);
    }

    // GetPlainWeaponAttackProfile is the single point where the weapon-attack path now picks up
    // the Strength bonus (previously it was only wired for Mystic unarmed via ComputeUnarmedBounds).
    // A Warrior with Str 150 wielding a Min=10 Max=20 weapon should swing for 20/30, not 10/20.
    [Fact]
    public void Plain_weapon_attack_profile_applies_Strength_damage_bonus_for_strong_wielders()
    {
        var weapon = new Item { Min = 10, Max = 20, Accy = 0, WeaponType = 0 };
        var cls = new CharacterClass { CombatLvl = 4 };
        var strongAttacker = new Player { Strength = 150, Agility = 100, Level = 20 };

        var (min, max, _, _) = CombatEngine.GetPlainWeaponAttackProfile(strongAttacker, cls, weapon);

        Assert.Equal(20, min);
        Assert.Equal(30, max);
    }

    // Conversely, a weak wielder loses damage — stock applies the max penalty unconditionally even
    // when the result is negative (then clamped to >= 0). This regression guards against silently
    // restoring the old "no Str adjustment" behaviour.
    [Fact]
    public void Plain_weapon_attack_profile_applies_Strength_damage_penalty_for_weak_wielders()
    {
        var weapon = new Item { Min = 10, Max = 20, Accy = 0, WeaponType = 0 };
        var cls = new CharacterClass { CombatLvl = 4 };
        var weakAttacker = new Player { Strength = 30, Agility = 100, Level = 20 };

        var (min, max, _, _) = CombatEngine.GetPlainWeaponAttackProfile(weakAttacker, cls, weapon);

        Assert.Equal(10, min);
        Assert.Equal(18, max);
    }

    // The non-backstab branch
    // adds a light-encumbrance bonus to BOTH accuracy and the FighterDodge field when encum < 33%
    // and CurrentHP > 0. The bonus scales as `(15 - encum/10)` for AV and `(10 - encum/10)` for
    // Dodge, peaking at 15/10 when fully unencumbered and falling to 0 at the 33% threshold.
    // Tests pin the exact values across the gate, peak, mid, and edge cases.
    [Theory]
    [InlineData(0, 15)]       // Fully unencumbered: peak bonus.
    [InlineData(100, 14)]     // Encum 10%: 15 - 1 = 14.
    [InlineData(500, 10)]     // Encum 50%: gate fails -> 0; but test expects bonus 0 + base accuracy.
    public void GetBaseAccuracy_applies_light_encumbrance_bonus_below_33_percent_threshold(int encumbrance, int expectedAccuracyDelta)
    {
        // baseline player profile that produces a stable GetBaseAccuracy reading.
        var unburdened = new Player { Strength = 50, Agility = 50, Level = 1, CurrentHP = 100, MaxEncumbrance = 1000, Encumbrance = 0 };
        var burdened = new Player { Strength = 50, Agility = 50, Level = 1, CurrentHP = 100, MaxEncumbrance = 1000, Encumbrance = encumbrance };

        int unburdenedAccuracy = unburdened.GetBaseAccuracy(combatLevel: 1);
        int burdenedAccuracy = burdened.GetBaseAccuracy(combatLevel: 1);

        // Bonus delta = unburdened peak (15) minus burdened bonus (expectedAccuracyDelta means the
        // burdened player's actual bonus value); the difference between the two reads should equal
        // 15 - expectedAccuracyDelta. Encum >= 33% gives 0 bonus (delta == 15).
        int expectedDelta = 15 - (encumbrance >= 330 ? 0 : expectedAccuracyDelta);
        Assert.Equal(expectedDelta, unburdenedAccuracy - burdenedAccuracy);
    }

    // GetBaseAccuracy returns 0 bonus when the player has 0 HP — even a fully-unencumbered corpse
    // doesn't get the unburdened bonus.
    [Fact]
    public void GetBaseAccuracy_skips_encumbrance_bonus_when_unconscious()
    {
        var unconscious = new Player { Strength = 50, Agility = 50, Level = 1, CurrentHP = 0, MaxEncumbrance = 1000, Encumbrance = 0 };
        var alive = new Player { Strength = 50, Agility = 50, Level = 1, CurrentHP = 100, MaxEncumbrance = 1000, Encumbrance = 0 };

        // Alive + unburdened gets +15 over unconscious + unburdened.
        Assert.Equal(15, alive.GetBaseAccuracy(combatLevel: 1) - unconscious.GetBaseAccuracy(combatLevel: 1));
    }

    [Theory]
    [InlineData(0, 10)]       // Fully unencumbered: +10 to Dodge.
    [InlineData(100, 9)]      // Encum 10%: +9.
    [InlineData(320, 7)]      // Encum 32%: still under gate, +7 (10 - 32/10 = 10 - 3 = 7).
    [InlineData(330, 0)]      // Encum 33%: gate trips, no bonus.
    [InlineData(500, 0)]      // Encum 50%: no bonus.
    public void GetDodge_applies_light_encumbrance_bonus_below_33_percent_threshold(int encumbrance, int expectedDodgeBonus)
    {
        var player = new Player { CurrentHP = 100, Dodge = 50, MaxEncumbrance = 1000, Encumbrance = encumbrance };

        int dodge = player.GetDodge();

        Assert.Equal(50 + expectedDodgeBonus, dodge);
    }

    // Unconscious players return -1 from GetDodge regardless of encumbrance — matches the existing
    // behaviour the negative-DG near-auto-hit window relies on.
    [Fact]
    public void GetDodge_returns_negative_one_for_unconscious_players_even_unburdened()
    {
        var unconscious = new Player { CurrentHP = 0, Dodge = 50, MaxEncumbrance = 1000, Encumbrance = 0 };

        Assert.Equal(-1, unconscious.GetDodge());
    }

    // When the player carries the blind status flag
    // (set via ability 107), the fighter's AV
    // drops by 10. C# routes ability 107 -> Player.IsBlinded, then GetBaseAccuracy /
    // GetBackstabAccuracy subtract 10 when that flag is set.
    [Fact]
    public void GetBaseAccuracy_applies_blind_AV_penalty()
    {
        var sighted = new Player { Strength = 50, Agility = 50, Level = 1, CurrentHP = 100, MaxEncumbrance = 1000, Encumbrance = 500 };
        var blinded = new Player { Strength = 50, Agility = 50, Level = 1, CurrentHP = 100, MaxEncumbrance = 1000, Encumbrance = 500, IsBlinded = true };

        Assert.Equal(10, sighted.GetBaseAccuracy(combatLevel: 1) - blinded.GetBaseAccuracy(combatLevel: 1));
    }

    [Fact]
    public void GetBackstabAccuracy_applies_blind_AV_penalty()
    {
        var sighted = new Player { Stealth = 40, Agility = 60, BSAccuracy = 0, AvAbility = 0 };
        var blinded = new Player { Stealth = 40, Agility = 60, BSAccuracy = 0, AvAbility = 0, IsBlinded = true };

        Assert.Equal(10, sighted.GetBackstabAccuracy() - blinded.GetBackstabAccuracy());
    }

    [Fact]
    public void GetBackstabAccuracy_includes_the_stock_second_agility_half_term()
    {
        // Backstab AV = (Stealth+Agility)/2 + Agility/2. The second
        // Agility/2 was missing, halving agility's contribution and crushing the backstab rate.
        var player = new Player { Stealth = 40, Agility = 60, BSAccuracy = 0, AvAbility = 0 };

        // (40+60)/2 + 60/2 = 50 + 30 = 80 (was 50 before the fix).
        Assert.Equal(80, player.GetBackstabAccuracy());
    }

    // Defender-side counterpart to the AV penalty above. When
    // the blind status flag is set, fighter[2] (the dodge-skill column, to-hit denominator) also
    // gets −10. C# closes this via Player.GetCombatDodgeSkill, which folds EffectiveDodgeSkill
    // (abilities 24/25 + ShadowStealth presence bonus) with the blind penalty. PvP and
    // monster-vs-player CombatEngine callsites pass it as defenderDodgeSkill:.
    [Fact]
    public void GetCombatDodgeSkill_applies_blind_dodge_skill_penalty()
    {
        var sighted = new Player();
        var blinded = new Player { IsBlinded = true };

        Assert.Equal(10, sighted.GetCombatDodgeSkill() - blinded.GetCombatDodgeSkill());
        Assert.Equal(-10, blinded.GetCombatDodgeSkill());
    }

    // EffectiveDodgeSkill must include the +10 ShadowStealth (ability 9) presence bonus that the
    // Stock grants it on ability-9 presence. ApplyAbility flips HasShadowStealth alongside
    // the existing Stealth += value, so a player with ability 9 from any source picks up both.
    [Fact]
    public void EffectiveDodgeSkill_adds_ten_when_player_has_ShadowStealth()
    {
        var plain = new Player { DodgeSkillAbility = 5 };
        var stealthy = new Player { DodgeSkillAbility = 5, HasShadowStealth = true };

        Assert.Equal(5, plain.EffectiveDodgeSkill);
        Assert.Equal(15, stealthy.EffectiveDodgeSkill);
    }

    // The fighter marshal adds the ability-4 ("MaxDmg")
    // accumulator to max. The accumulator is populated by ApplyAbility case 4 (just += value)
    // and includes contributions from race, class, equipped items, and active spells. In the C# model
    // ability 4 now flows into Player.MaxDamageAbility via ApplyAbility, and GetPlainWeaponAttackProfile
    // adds it to max for BOTH the equipped-weapon path AND the weaponless fallback.
    [Fact]
    public void Plain_weapon_attack_profile_adds_MaxDamageAbility_to_max_for_equipped_weapon()
    {
        var weapon = new Item { Min = 10, Max = 20, Accy = 0, WeaponType = 0 };
        var cls = new CharacterClass { CombatLvl = 4 };
        // Strength 50 isolates the Str-bonus from this test; +7 MaxDamageAbility comes from
        // (hypothetically) a ring carrying Abil=4, AbilVal=7 that ApplyAbility has accumulated.
        var attacker = new Player { Strength = 50, Agility = 100, Level = 20, MaxDamageAbility = 7 };

        var (min, max, _, _) = CombatEngine.GetPlainWeaponAttackProfile(attacker, cls, weapon);

        Assert.Equal(10, min);
        Assert.Equal(27, max); // 20 + Str bonus (0) + MaxDamageAbility (7)
    }

    [Fact]
    public void Plain_weapon_attack_profile_adds_MaxDamageAbility_to_max_for_weaponless_path()
    {
        var cls = new CharacterClass { CombatLvl = 4 };
        var attacker = new Player { Strength = 50, Agility = 100, Level = 20, MaxDamageAbility = 3, CurrentHP = 100, MaxEncumbrance = 1000, Encumbrance = 0 };

        var (min, max, _, _) = CombatEngine.GetPlainWeaponAttackProfile(attacker, cls, weapon: null);

        Assert.Equal(1, min);      // Stock weaponless min, no Str bonus at Str 50.
        Assert.Equal(7, max);      // Stock weaponless max 4 + Str bonus (0) + MaxDamageAbility (3).
    }

    [Fact]
    public void Plain_weapon_attack_profile_uses_stat_derived_accuracy_for_weaponless_path()
    {
        // Regression: GetPlainWeaponAttackProfile used to return Accuracy=0 for the weaponless
        // branch, claiming stock "leaves the shared weaponless fighter defaults in place." That's
        // wrong: the fighter marshal initialises AV to 0, but then
        // overwrites it with the full stat-derived AV for every non-backstab type. Unarmed normal
        // attacks (mystics post-backstab, anyone who unequipped their weapon, etc.) must fire with
        // the same AV the weapon path uses, minus the weapon-skill/weapon.Accy terms.
        var cls = new CharacterClass { CombatLvl = 4 };
        var attacker = new Player { Strength = 80, Agility = 90, Level = 20 };

        var (_, _, accuracy, _) = CombatEngine.GetPlainWeaponAttackProfile(attacker, cls, weapon: null);

        Assert.Equal(attacker.GetBaseAccuracy(cls.CombatLvl), accuracy);
        Assert.True(accuracy > 0, $"Expected stat-derived AV, got {accuracy}.");
    }

    [Fact]
    public void Weaponless_normal_attack_hits_at_a_sane_rate_against_realistic_AC()
    {
        // Regression for the same bug as above, observed end-to-end through PlayerAttack: with the
        // old Accuracy=0 weaponless path, hit chance pinned to the 10 floor and bare-fisted strikes
        // missed ~90% of the time even at Level 50 vs AC 30. Mirrors the mystic-accuracy test below.
        var player = new Player
        {
            Name = "Brawler",
            Level = 50,
            Strength = 90,
            Agility = 90,
            MaxHP = 100,
            CurrentHP = 100,
            CurrentEnergy = 1000,
        };
        var cls = new CharacterClass { CombatLvl = 15 };

        int totalHits = 0;
        int totalSwings = 0;
        for (int trial = 0; trial < 50; trial++)
        {
            player.CurrentEnergy = 1000;
            var monster = new MonsterInstance
            {
                Template = new Monster { Name = "dummy", HP = 100000, ArmourClass = 30, BSDefense = 0 },
                DisplayName = "dummy",
                CurrentHP = 100000,
                MaxHP = 100000,
            };

            var result = CombatEngine.PlayerAttack(player, monster, cls, weapon: null);
            totalHits += result.Hits;
            totalSwings += result.Hits + result.Misses;
        }

        double hitRate = (double)totalHits / totalSwings;
        Assert.True(hitRate > 0.75, $"Expected hit rate > 75% with base accuracy, got {hitRate:P0} ({totalHits}/{totalSwings}).");
    }

    // Negative MaxDamageAbility (some items carry Abil 4 with a negative value to penalise a wielder)
    // should still be additive — the stock accumulator is signed and just sums every source.
    [Fact]
    public void Plain_weapon_attack_profile_respects_negative_MaxDamageAbility()
    {
        var weapon = new Item { Min = 2, Max = 5, Accy = 0, WeaponType = 0 };
        var cls = new CharacterClass { CombatLvl = 1 };
        // bleeding main-gauche (item 1520) carries ability 4 with value -5 in the live data; a
        // Str-50 wielder ends up with max = 5 + 0 (Str) + (-5) = 0. The min/max integrity check
        // doesn't apply here because the Strength clamp ran upstream; weapon max can land at 0.
        var attacker = new Player { Strength = 50, Agility = 100, Level = 20, MaxDamageAbility = -5 };

        var (min, max, _, _) = CombatEngine.GetPlainWeaponAttackProfile(attacker, cls, weapon);

        Assert.Equal(2, min);
        Assert.Equal(0, max); // 5 + (-5)
    }

    [Fact]
    public void Monster_glance_is_dark_red_while_dodge_and_miss_are_cyan()
    {
        // Stock prints the no-damage outcomes with TWO different colour
        // prefixes: the GLANCE (type 1, "...but your armour deflects the blow!") is preceded by
        // ESC[79D ESC[K + ESC[0;31m (DarkRed), while the DODGE (type 3) and plain
        // MISS (type 0) are preceded by ESC[79D ESC[K + ESC[0;36m (Cyan).
        // Regression: the glance used to be emitted through GameAnsi.Dodge, painting it cyan.
        const string DarkRed = "\x1b[0;31m";
        const string Cyan = "\x1b[0;36m";

        var messages = new Dictionary<int, RoomMessage>
        {
            [100] = new RoomMessage { Number = 100, Line1 = "%s smacks you for %d damage!", Line2 = "%s smacks %s for %s damage!" },
            [101] = new RoomMessage
            {
                Number = 101,
                Line1 = "%s %s you, but your armour deflects the blow!",
                Line2 = "%s %s %s, but %s armour deflects the blow!",
                Line3 = "%s %s you, but you dodge out of the way!",
            },
            [102] = new RoomMessage
            {
                Number = 102,
                Line1 = "%s %s %s, but %s dodges out of the way!",
                Line2 = "%s %s at you!",
                Line3 = "%s %s at %s!",
            },
        };

        var monster = new MonsterInstance
        {
            Template = new Monster
            {
                Name = "barmaid",
                Energy = 1000,
                Attacks =
                [
                    new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 1, Percent = 100, Min = 1, Max = 1, HitMessageId = 100, DodgeMessageId = 101, MissMessageId = 102 },
                ],
            },
            DisplayName = "barmaid",
            CurrentHP = 50,
            MaxHP = 50,
        };

        // High DamageResist forces damage <= 0 on a landed blow -> glance; high Agility/Dodge
        // produces skill-dodges; the accuracy roll produces plain misses. Sample enough rounds to
        // see all three.
        var player = new Player
        {
            Name = "Target",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Agility = 120,
            Dodge = 120,
            DamageResist = 500,
        };

        int glanceLines = 0;
        int dodgeOrMissLines = 0;

        for (int attempt = 0; attempt < 600; attempt++)
        {
            player.CurrentHP = player.MaxHP;
            monster.ResetEnergy();
            monster.PrepareCombatRound();
            var result = CombatEngine.MonsterAttack(monster, player, items: null, messages: messages);

            foreach (var line in result.Messages.Concat(result.RoomMessages))
            {
                if (line.Contains("armour deflects the blow", StringComparison.Ordinal))
                {
                    glanceLines++;
                    Assert.Contains(DarkRed, line, StringComparison.Ordinal);
                    Assert.DoesNotContain(Cyan, line, StringComparison.Ordinal);
                }
                else if (line.Contains("dodge", StringComparison.OrdinalIgnoreCase) || line.Contains(" at ", StringComparison.Ordinal))
                {
                    dodgeOrMissLines++;
                    Assert.Contains(Cyan, line, StringComparison.Ordinal);
                    Assert.DoesNotContain(DarkRed, line, StringComparison.Ordinal);
                }
            }
        }

        Assert.True(glanceLines > 0, "Expected the glance ('armour deflects the blow') path to be exercised.");
        Assert.True(dodgeOrMissLines > 0, "Expected the dodge/miss path to be exercised.");
    }

    [Fact]
    public void Monster_glance_without_dodge_message_record_uses_stock_fallback_literals()
    {
        // 49 of the 2272 live monster attack slots in stock carry AtkDodgeMsg = 0 (kobold thief
        // slot 1, skeleton, zombie, giant rat...). For those, stock
        // falls back to its own literals rather than an authored record:
        //   player: "%s's %s hits you, but your armour deflects."
        //   room:   "%s's %s hits %s, but glances off %s armour."
        // %s #2 is the fighter verb token, which the monster marshal
        // fills from the monster's WEAPON: MissMsg -> Line2 ("swings at"), else HitMsg -> Line2,
        // else empty. The name is bare (no "The ") and the first character is uppercased.
        const string DarkRed = "\x1b[0;31m";

        var messages = new Dictionary<int, RoomMessage>
        {
            [100] = new RoomMessage { Number = 100, Line1 = "%s smacks you for %d damage!", Line2 = "%s smacks %s for %s damage!" },
            // The weapon's MissMsg record: Line2 is the 3rd-person token the fallback reads.
            [8286] = new RoomMessage { Number = 8286, Line1 = "swing at", Line2 = "swings at", Line3 = "swings at" },
        };

        var items = new Dictionary<int, Item>
        {
            [69] = new Item { Number = 69, Name = "knife", WeaponType = 2, MissMsg = 8286 },
        };

        var player = new Player
        {
            Name = "Target",
            CurrentHP = 5000,
            MaxHP = 5000,
            ArmourClass = 0,
            Gender = 0,          // male -> possessive "his"
            Agility = 1,
            Dodge = 1,
            DamageResist = 500,  // force damage <= 0 on a landed blow -> glance
        };

        static MonsterInstance BuildMonster(int weaponId) => new MonsterInstance
        {
            Template = new Monster
            {
                Name = "kobold thief",
                Energy = 1000,
                Weapon = weaponId,
                Attacks =
                [
                    // DodgeMessageId = 0 -> no authored record -> the stock fallback path.
                    new MonsterAttack { SlotIndex = 0, Type = 1, Accuracy = 500, Percent = 100, Min = 1, Max = 1, HitMessageId = 100, DodgeMessageId = 0, MissMessageId = 0 },
                ],
            },
            DisplayName = "kobold thief",
            CurrentHP = 50,
            MaxHP = 50,
        };

        static (string? Self, string? Room) RunUntilGlance(MonsterInstance monster, Player player, Dictionary<int, Item> items, Dictionary<int, RoomMessage> messages)
        {
            for (int attempt = 0; attempt < 400; attempt++)
            {
                player.CurrentHP = player.MaxHP;
                monster.ResetEnergy();
                monster.PrepareCombatRound();
                var result = CombatEngine.MonsterAttack(monster, player, items, messages);

                string? self = result.Messages.FirstOrDefault(l => l.Contains("armour deflects", StringComparison.Ordinal));
                string? room = result.RoomMessages.FirstOrDefault(l => l.Contains("glances off", StringComparison.Ordinal));
                if (self != null && room != null)
                    return (self, room);
            }
            return (null, null);
        }

        // --- armed monster: verb token = MissMsg Line2 ---
        var (armedSelf, armedRoom) = RunUntilGlance(BuildMonster(69), player, items, messages);
        Assert.NotNull(armedSelf);
        Assert.NotNull(armedRoom);
        Assert.Contains(DarkRed, armedSelf!, StringComparison.Ordinal);
        Assert.Contains(DarkRed, armedRoom!, StringComparison.Ordinal);
        // Bare name, capitalised, literal "'s" — no "The " article (the stock literal has none).
        Assert.Contains("Kobold thief's swings at hits you, but your armour deflects.", armedSelf!, StringComparison.Ordinal);
        Assert.Contains("Kobold thief's swings at hits Target, but glances off his armour.", armedRoom!, StringComparison.Ordinal);
        Assert.DoesNotContain("The kobold", armedSelf!, StringComparison.Ordinal);

        // --- weaponless monster: stock zeroes the token buffer, leaving the double space ---
        var (bareSelf, bareRoom) = RunUntilGlance(BuildMonster(0), player, items, messages);
        Assert.NotNull(bareSelf);
        Assert.NotNull(bareRoom);
        Assert.Contains("Kobold thief's  hits you, but your armour deflects.", bareSelf!, StringComparison.Ordinal);
        Assert.Contains("Kobold thief's  hits Target, but glances off his armour.", bareRoom!, StringComparison.Ordinal);
    }
}
