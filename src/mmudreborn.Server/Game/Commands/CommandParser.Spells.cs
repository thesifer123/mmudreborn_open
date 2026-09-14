using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace mmudreborn.Server;

public partial class CommandParser
{
    // Ability 115 = the wear-off message id carried by hold/confusion spells (e.g. spell 128
    // "stunned" → 57 "You are no longer stunned."). Stored as KnockdownDescriptiveMessageId, replayed
    // by the knockdown/upkeep tick when the effect expires.
    private const int KnockdownRecoveryMessageAbilityId = 115;

    private static string GetMonsterCastDisplayName(MonsterInstance attacker)
    {
        string displayName = attacker.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = attacker.Template.Name;

        return displayName.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? displayName
            : $"The {displayName}";
    }

    private async Task HandleCast(string args)
    {
        // A KAI class casts nothing — it invokes powers. (Magery type 5)
        if (_world.Database.Classes.TryGetValue(_player.ClassId, out var castCls) && Player.UsesKai(castCls))
        {
            await _client.SendLineAsync("You may not cast... You are KAI!  You must invoke your powers.");
            return;
        }

        if (_player.MaxMana <= 0)
        {
            await _client.SendLineAsync("You have no magical ability!");
            return;
        }

        if (string.IsNullOrEmpty(args))
        {
            await _client.SendLineAsync("Cast what spell?");
            return;
        }

        if (!TryParsePlayerSpellCommand(args, out var spell, out var target, out var requestedSpellName))
        {
            await _client.SendLineAsync($"You don't know the spell '{requestedSpellName}'.");
            return;
        }

        await HandleResolvedCastAsync(spell, target);
    }

    // The INVOKE command: the KAI/Mystic counterpart to CAST. KAI "powers"
    // live in the same Spells table (Magery type 5); only the verb and the class gate differ. A non-KAI
    // caller is rejected, an argless call shows the syntax, and the resolved power runs through the same
    // effect engine as a spell (HandleResolvedCastAsync), where the silence gate switches to ability 159.
    private async Task HandleInvoke(string args)
    {
        _world.Database.Classes.TryGetValue(_player.ClassId, out var cls);
        if (cls == null || !Player.UsesKai(cls))
        {
            await _client.SendLineAsync(cls == null || cls.MageryType == 0
                ? "You have no powers to invoke!"
                : "You may not invoke... You are not KAI!  You must cast your spells.");
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: INVOKE {power} [{target}]");
            return;
        }

        if (!TryParsePlayerSpellCommand(args, out var power, out var target, out var requestedPowerName))
        {
            await _client.SendLineAsync($"You don't know the power '{requestedPowerName}'.");
            return;
        }

        await HandleResolvedCastAsync(power, target);
    }

    private async Task<bool> TryHandleDirectSpellCommand(string input)
    {
        if (!TryParsePlayerSpellCommand(input, out var spell, out var target, out _))
            return false;

        if (HasIncompleteRoomCommandPrefixConflict(input))
            return false;

        if (_player.MaxMana <= 0)
        {
            await _client.SendLineAsync("You have no magical ability!");
            return true;
        }

        await HandleResolvedCastAsync(spell, target);
        return true;
    }

    // The insufficient-resource line is "You do not have enough %s."
    // with %s composed from a second constant — "kai to invoke that power" for
    // KAI powers (spell type 5, monk classes), else "mana to cast that spell". We were sending
    // an invented "You don't have enough mana!" for both.
    private string InsufficientMagicResourceMessage()
    {
        bool usesKai = _world.Database.Classes.TryGetValue(_player.ClassId, out var cls) && Player.UsesKai(cls);
        return usesKai
            ? "You do not have enough kai to invoke that power."
            : "You do not have enough mana to cast that spell.";
    }

    private async Task HandleResolvedCastAsync(GameSpell spell, string target)
    {
        // On entry — after the afraid/KAI/silence gates but BEFORE the spell is
        // resolved or any mana/target check — every cast/invoke silently clears the resting
        // and meditating flags AND breaks sneak + hide.
        // So casting ANY spell at all (bless, heal, an offensive spell, a KAI power) reveals you and
        // breaks rest/meditation — and because the clear is ahead of the cost/target/confusion gates, a
        // cast that then fizzles still reveals. Unlike a backstab — which guards these same clears behind
        // the attack-type==4 check in the attack path and only reveals on the hit/miss resolution — a cast
        // reveals IMMEDIATELY, never on landing. (Item `use` casts go straight to the offensive resolver
        // and bypass this, so they correctly do NOT break sneak — item use has no sneak clear.)
        _player.IsResting = false;
        _player.IsMeditating = false;
        await BreakSneakAndHideForAction();

        if (RequiresExplicitSpellTarget(spell) && string.IsNullOrWhiteSpace(target))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You must specify a target for that spell!{MudAnsi.Reset}");
            return;
        }

        if (_player.CurrentMana < spell.ManaCost)
        {
            await _client.SendLineAsync(InsufficientMagicResourceMessage());
            return;
        }

        // Spell alignment restriction (abilities 97/98/112).
        // Same semantics as the item alignment gate: a Good spell rejects non-Good casters,
        // an Evil spell rejects non-Evil casters, a Neutral spell rejects non-Neutral casters.
        if (spell.Abilities.ContainsKey(ItemGoodAlignedAbilityId)
            || spell.Abilities.ContainsKey(ItemEvilAlignedAbilityId)
            || spell.Abilities.ContainsKey(ItemNeutralAlignedAbilityId))
        {
            var casterAlignment = CombatEngine.GetPlayerAlignment(_player.EvilPoints);
            bool casterIsGood = CombatEngine.IsGood(casterAlignment);
            bool casterIsEvil = CombatEngine.IsEvil(casterAlignment);
            bool casterIsNeutral = !casterIsGood && !casterIsEvil;
            bool blocked =
                (spell.Abilities.ContainsKey(ItemGoodAlignedAbilityId) && !casterIsGood)
                || (spell.Abilities.ContainsKey(ItemEvilAlignedAbilityId) && !casterIsEvil)
                || (spell.Abilities.ContainsKey(ItemNeutralAlignedAbilityId) && !casterIsNeutral);
            if (blocked)
            {
                // One generic string is printed for ANY spell-eligibility
                // failure (alignment/class/level); there is no alignment-specific message.
                await _client.SendLineAsync("You may not cast this spell.");
                return;
            }
        }

        // The once-per-round cast token
        // gates only beneficial/utility and INSTANT offensive casts. A combat-round offensive spell
        // (EnergyCost>0) is QUEUED for the round timer like melee, not cast immediately — so neither the
        // opening cast NOR a re-cast is token-rejected: it prints *Combat Engaged* (re-cast: *Combat Off*
        // first) and (re-)queues the autocombat engage, and the damage resolves on the pulse. Re-casting
        // mid-round is a stop/restart re-engage that changes nothing about the pending cast (no extra
        // mana, no immediate hit). The afraid/silence gates still apply to all casts.
        bool offensiveCombatRoundCast = IsOffensiveSpell(spell) && spell.EnergyCost > 0;
        bool isOpeningInstantHostileCast = IsOffensiveSpell(spell) && spell.EnergyCost == 0 && !_player.InCombat;
        if (!CanCastSpellThisRound(enforceRoundToken: !(offensiveCombatRoundCast || isOpeningInstantHostileCast), out var spellFailureMessage))
        {
            await _client.SendLineAsync(spellFailureMessage);
            return;
        }

        // Confusion is rolled once per typed command at the dispatcher (ProcessCommand),
        // so a typed `cast` already fumbles there — no second command-time roll here. A QUEUED offensive
        // combat cast additionally rolls at round-resolve time in ExecuteAutoPlayerCombatRound (a
        // separate round-resolve check), matching the command-then-round two-phase model.

        // Pet-creating effects, dispatched before the generic beneficial/offensive routing:
        //   summon (ability 12) spawns owned creatures;
        //   charm/enslave (ability 6) converts a monster to a pet.
        if (spell.AbilitySlots.Any(slot => slot.Key == SummonAbilityId))
        {
            await HandlePlayerSummonCastAsync(spell);
            return;
        }
        if (spell.Abilities.ContainsKey(CharmAbilityId))
        {
            await HandlePlayerCharmCastAsync(spell, target);
            return;
        }

        // Identify/detect-magic (ability 26): the spell is cast ON A CARRIED
        // ITEM and reads that item's aura magnitude (ability 28) to narrate how much magic it holds.
        // Stock carriers — "detect magic" (#24, mage/gypsy/warlock) and "song of lore" (#41, bard) —
        // are Targets=7 item-target casts whose only ability is 26, so the generic self/beneficial
        // route applied nothing and the spell silently did nothing. Route them to the item handler.
        if (spell.Abilities.ContainsKey(IdentifySpellAbilityId))
        {
            await HandleIdentifySpellCastAsync(spell, target);
            return;
        }

        if (spell.Targets == 2)
        {
            await HandleBeneficialSpellCastAsync(spell, target);
            return;
        }

        // Targets=13 — "full party area": the spell hits the caster plus every party
        // member sharing the room. Falls back to self when the caster is solo (cast healing rain
        // outside a party heals only the caster). Followers who move independently drop from the
        // party, so the room filter is also the party filter.
        if (spell.Targets == PartyAreaTargetType)
        {
            await HandleGroupBeneficialSpellCastAsync(spell);
            return;
        }

        // Targets=12 — "full attack area" (chaos storm, etc.): no target is named ("csto"),
        // the spell hits EVERY eligible target in the room and, for a combat-round spell, repeats each
        // round until OOM. Routed before the single-target offensive path (which would ask "Cast at
        // whom?").
        if (IsOffensiveSpell(spell) && spell.Targets == AreaEnemyTargetType)
        {
            await HandleAreaOffensiveSpellCastAsync(spell);
            return;
        }

        // Damage / offensive-effect spell (smite carries no damage roll but still routes here)
        if (IsOffensiveSpell(spell))
        {
            // "Instant vs uses-a-combat-round" is emergent from EnergyCost, which is
            // spent from the shared stamina pool: a spell that costs a full round's stamina
            // (EnergyCost > 0, e.g. quake/harm/fireball at 1000) becomes the player's repeating round
            // action and replaces melee autocombat; an instant spell (EnergyCost == 0, e.g. rotting
            // flesh and other DoTs/debuffs) spends no stamina, so it is a one-off that does NOT take
            // over the round — the existing melee loop continues.
            bool usesCombatRound = spell.EnergyCost > 0;

            // A combat-round spell vs a monster is queued for the round timer — whether opening combat
            // or re-cast mid-combat (the latter is a stop/restart re-engage that changes nothing). Only
            // a PvP target or an instant spell falls through to the immediate-resolution path below.
            if (usesCombatRound && await TryBeginOrRefreshMonsterCombatSpellAsync(spell, target, reportMissingTarget: true))
                return;

            // emitCombatOffOnEngage: this is the manual `cast` command. Casting prints
            // "*Combat Off*" and drops autocombat BEFORE the offensive cast
            // re-engages, so an INSTANT offensive cast at a monster while already in autocombat shows a
            // "*Combat Off*"/"*Combat Engaged*" toggle (the combat-round path already does this in
            // TryBeginOrRefreshMonsterCombatSpellAsync). Pulse/proc/chain/swarm callers pass false.
            await HandleOffensiveSpellCastAsync(spell, target, allowImplicitCombatTarget: false, keepAutoCombatSpellSelected: usesCombatRound, reportMissingTarget: true, emitCombatOffOnEngage: true);
        }
        else
        {
            // Self / single-ally beneficial cast — the offensive check already excluded enemy-target
            // and harmful spells, so what remains (Targets 1/3/5/6/7/13/… self-buffs, self-heals, the
            // KAI "ways") flows through the same effect engine as a targeted beneficial cast, applied
            // to the caster. HandleBeneficialSpellCastAsync runs BOTH the duration buff and every
            // immediate effect (heal 18, mana 150, decurse 84, cure, energy 11, …) with the
            // proper messages — which the old inline branch dropped for AttType!=0 self-buffs. Targets
            // other than 2 are self-only, so force the self target (empty ⇒ self).
            await HandleBeneficialSpellCastAsync(spell, string.Empty);
        }
    }

    // A beneficial spell with a duration becomes an active spell on the
    // target (refreshing rather than stacking), then derived stats are recomputed so its abilities
    // take effect. Duration is rolled via RollPlayerCastSpellDuration — the full formula including
    // the per-level random spread and the caster duration-bonus ability.
    private void ApplyBuffSpellIfDuration(GameSpell spell, Player target, int? preRolledMagnitude = null)
    {
        // Negate-spell gate (the item's "Negate Spells" list): if a worn item on the
        // RECIPIENT lists this spell number, the whole cast fizzles on them — no buff, no immediate
        // effect — the same silent short-circuit the room-spell path uses (ApplyRoomSpellCast).
        // Keyed on the recipient's gear, so guarding here + in ApplyImmediateBeneficialEffects covers every
        // delivery path (self-cast, cast-on-another, Targets=13 healing rain, item-use, quest cast). The
        // Death Shroud (#1579) negates its seven healing spells (13/17/89/123/145/152/27) this way; before
        // this guard, negation was only enforced on the environmental room-spell path so player-cast heals
        // still landed on the wearer.
        if (_world.PlayerHasItemNegatingSpell(target, spell.Number))
            return;

        if (spell.Duration <= 0)
            return;

        int duration = RollPlayerCastSpellDuration(spell, _player.Level);

        // The rolled effect MAGNITUDE is stored in the active-spell slot;
        // the derived-stat update applies each ability at its own value, or this magnitude when
        // that value is 0. So speed (Min/Max 85, ability 87=0) ⇒ EU×0.85 and slow (125) ⇒ EU×1.25,
        // NOT the caster level. Roll min..max with the same level scaling used for damage/heal.
        // Targets=13 group casts pre-roll the magnitude once and pass it in so every party member
        // gets the same buff value (the roll is stored once and reused).
        int magnitude = preRolledMagnitude ?? RollSpellLevelScaledAmount(spell);

        if (target.AddOrRefreshActiveSpell(spell.Number, magnitude, duration))
            _world.RecalculatePlayerStats(target);
    }

    // Spell primary magnitude. This is the SAME rolled magnitude stored for every
    // cast-on-target/cast-on-user spell — buffs,
    // debuffs, poisons, knockdowns, haste/slow and heals all draw from it. It MUST honor spell.Cap:
    // the level scaling clamps at effLvl = min(casterLvl, Cap), so barkskin (Cap=20, Min/MaxBase 10,
    // Inc 10/10) lands on a flat magnitude 30 (= +3 DR) from level 20 up instead of climbing forever
    // (bug #142: it was reading +4 DR / 40 AC at level ~40 via the old levels-above-req formula, which
    // ignored Cap and grew the max band only). Consolidated onto the canonical band used for damage and
    // healing — see ComputeSpellMagnitudeBand and SpellMagnitudeBandTests.
    private int RollSpellLevelScaledAmount(GameSpell spell)
        => RollSpellMagnitude(spell, _player.Level);

    // A spell roots (HoldPerson) the target iff it carries ability 74 — true for all 41
    // stock hold/entangle/web/paralyze/knockdown spells, not just #318. The active spell's ability 74
    // then sets the target's IsRooted in RecalculateStats (movement block only; see ApplyAbility case 74).
    private static bool SpellIsKnockdown(GameSpell spell)
        => spell.Abilities.ContainsKey(ParalysisStatusAbilityId);

    // Rolls the duration applied to a freshly-cast active
    // spell, with the full formula:
    //   effLvl = min(casterLvl, spell.Cap)          // Cap<=1 means use casterLvl raw
    //   dur    = spell.Duration                     // base
    //   if DurIncLvls > 0: dur += (effLvl/DurIncLvls)*DurInc
    //   hi     = DurRand * effLvl
    //   if hi > dur: dur = random in [dur, hi]                             // random spread
    //   dur    = (casterDurationBonusPct + 100) * dur / 100                  // caster ability 166
    // Pass `casterDurationBonusPct = 0` for monster casters (ability 166 is still applied
    // to a monster cast by a user caster, but monster-vs-player flows here go through
    // RollMonsterCastSpellDuration which passes 0 because the caster has no ability-166 source).
    internal static int RollSpellDuration(GameSpell spell, int casterLevel, int casterDurationBonusPct = 0)
    {
        int effLvl = spell.Cap > 0 ? Math.Min(casterLevel, spell.Cap) : casterLevel;
        int dur = spell.Duration;
        if (spell.DurIncLvls > 0)
            dur += (effLvl / spell.DurIncLvls) * spell.DurInc;

        int hi = spell.DurRand * effLvl;
        if (hi > dur)
            dur = Random.Shared.Next(dur, hi + 1);

        if (casterDurationBonusPct != 0)
            dur = (casterDurationBonusPct + 100) * dur / 100;

        return Math.Max(dur, 0);
    }

    // Player-cast spells: looks up the caster's ability 166 ("Spell Duration Bonus %") from
    // the unified ability aggregator (race/class/equipment/carried/active spells).
    private int RollPlayerCastSpellDuration(GameSpell spell, int casterLevel)
        => RollSpellDuration(spell, casterLevel, _player.GetActiveAbilityValue(_world.Database, 166));

    // Monster-cast spells: the caster has no ability-166 source in the monster-ability model, so the
    // bonus stays 0 — matching the monster path.
    private static int RollMonsterCastSpellDuration(GameSpell spell, int casterLevel)
        => RollSpellDuration(spell, casterLevel, 0);

    private void ApplyKnockdownSpellToPlayer(GameSpell spell, Player target, int casterLevel, bool casterIsPlayer = true)
    {
        int duration = casterIsPlayer
            ? RollPlayerCastSpellDuration(spell, casterLevel)
            : RollMonsterCastSpellDuration(spell, casterLevel);
        if (duration <= 0)
            return;

        // HoldPerson (ability 74) roots the target for the spell duration: adding it as an active spell
        // makes RecalculateStats set IsRooted (movement-block only — see ApplyAbility case 74). Recovery
        // (the ability-115 wear-off message + clearing IsRooted via recalc) happens when the active spell
        // expires in ProcessActiveSpellUpkeep. No separate knockdown state needed.
        // Capped band, matching ApplyKnockdownSpellToMonster (the player-cast counterpart) and every
        // other player-cast magnitude — the old raw MinBase..MaxBase roll ignored level scaling + Cap.
        int magnitude = RollSpellLevelScaledAmount(spell);
        if (target.AddOrRefreshActiveSpell(spell.Number, magnitude, duration))
            _world.RecalculatePlayerStats(target);
    }

    private void ApplyKnockdownSpellToMonster(GameSpell spell, MonsterInstance targetMonster, int casterLevel)
    {
        // Knockdown-on-monster only fires from the player-cast path (line 686); the bonus comes
        // from _player's ability 166 aggregate.
        int duration = RollPlayerCastSpellDuration(spell, casterLevel);
        if (duration <= 0)
            return;

        // Root via the active spell's ability 74 (monster movement gate reads GetEffectiveAbility(74)).
        int magnitude = RollSpellLevelScaledAmount(spell);
        targetMonster.AddOrRefreshActiveSpell(_world.Database, spell.Number, magnitude, duration);
    }

    // Send a player melee round's swing lines INTERLEAVED with its weapon spell-procs, then redraw the
    // prompt. A weapon's proc (ability 114) fires inside the per-swing hit
    // branch — immediately after that swing's "You hit for N" line and before the next swing rolls — so a
    // Flametongue shows "You impale X / X takes N fire damage / You impale X / ..." rather than every melee
    // line followed by a batch of proc lines. CombatResult.TriggeredWeaponProcs records each proc with the
    // message-list lengths captured at its swing boundary, so we replay the swing lines in slices and fire
    // each proc cast at the right seam (caster + room + target streams stay aligned). Replaces the old
    // batched ApplyTriggeredWeaponProcSpellsAsync; reached only from ExecutePlayerCombatAction(AgainstPlayer)
    // Async (the auto-combat round), never the player's own `cast` command.
    private async Task SendCombatResultWithProcsAsync(CombatResult result, MonsterInstance? targetMonster, Player? playerTarget)
    {
        int casterSent = 0, roomSent = 0, targetSent = 0;
        bool firedAny = false;

        async Task FlushUpToAsync(int casterIdx, int roomIdx, int targetIdx)
        {
            if (casterIdx > casterSent)
            {
                await SendCombatMessagesToCurrentPlayerAsync(result.Messages.GetRange(casterSent, casterIdx - casterSent));
                casterSent = casterIdx;
            }
            if (playerTarget != null && targetIdx > targetSent)
            {
                await SendCombatMessagesToPlayerAsync(playerTarget, result.TargetMessages.GetRange(targetSent, targetIdx - targetSent));
                targetSent = targetIdx;
            }
            if (roomIdx > roomSent)
            {
                var slice = result.RoomMessages.GetRange(roomSent, roomIdx - roomSent);
                if (playerTarget != null)
                    await BroadcastCombatMessagesToObserversAsync(slice, playerTarget);
                else
                    BroadcastCombatMessagesToRoom(slice);
                roomSent = roomIdx;
            }
        }

        bool targetDiedMidProc = false;
        // This round runs on the combat pulse. The proc cast writes its lines directly via
        // _client.SendLineAsync, so hold that output: while the player is mid-command it defers onto the
        // typed-ahead queue instead of trampling the half-typed line (matching the swing lines, which
        // defer via SendCombatMessagesToCurrentPlayerAsync). No-op when the player isn't typing.
        _client.BeginHeldOutput();
        try
        {
            foreach (var proc in result.TriggeredWeaponProcs)
            {
                await FlushUpToAsync(proc.AfterCasterMessage, proc.AfterRoomMessage, proc.AfterTargetMessage);

                if (!_world.Database.Spells.TryGetValue(proc.SpellId, out var spell))
                    continue;

                // The melee swings were resolved synchronously, so a target the round killed already reads
                // IsDead/dead-HP here — matching the kill gate, no proc lands on a corpse.
                if (targetMonster != null)
                {
                    if (targetMonster.IsDead)
                        break;
                    await ExecuteOffensiveSpellAgainstMonsterAsync(spell, targetMonster, keepAutoCombatSpellSelected: false);
                    firedAny = true;
                    if (targetMonster.IsDead) { targetDiedMidProc = true; break; }
                }
                else if (playerTarget != null)
                {
                    if (playerTarget.CurrentHP <= Player.DeathHP)
                        break;
                    await ExecuteOffensiveSpellAgainstPlayerAsync(spell, playerTarget, keepAutoCombatSpellSelected: false);
                    firedAny = true;
                    if (playerTarget.CurrentHP <= Player.DeathHP) { targetDiedMidProc = true; break; }
                }
            }
        }
        finally
        {
            _client.EndHeldOutput();
        }

        // Flush any swing lines after the last proc seam (or the whole round when nothing procced) — but
        // NOT once a proc has killed the target: the death/exp output already printed, so remaining
        // pre-rolled swing lines must not trail after it. (Those swings are a quirk of the synchronous
        // resolution; the vs-monster path avoids them entirely via ResolveNormalWeaponRoundVsMonsterAsync.)
        if (!targetDiedMidProc)
            await FlushUpToAsync(result.Messages.Count, result.RoomMessages.Count, result.TargetMessages.Count);

        // A proc's damage line is sent with a bare SendLineAsync carrying no trailing prompt, unlike the
        // swing lines that reprompt via SendCombatMessagesToCurrentPlayerAsync. With no command loop to
        // redraw afterward, a trailing proc line would be the last byte on the wire and the session would
        // look frozen until the player pressed Enter. Redraw here, but only when a proc actually fired and
        // left the target alive: a proc kill already reprompts through HandleMonsterDeath's *Combat Off*,
        // and a player command holds SuppressBroadcastReprompt so the loop's own final prompt covers it.
        if (!firedAny || _player.SuppressBroadcastReprompt)
            return;

        // Skip the redraw while the player is mid-typing: the proc lines were deferred (held output) and
        // the deferred queue redraws on its own flush, so a bare prompt here would trample the command.
        if (_client.HasPendingInput)
            return;

        bool targetSurvived = targetMonster is { IsDead: false }
            || (playerTarget != null && playerTarget.CurrentHP > Player.DeathHP);
        if (targetSurvived)
            await _client.SendAsync(MudAnsi.Prompt(_player));
    }

    private bool TryParsePlayerSpellCommand(string input, out GameSpell spell, out string target, out string requestedSpellName)
    {
        string trimmed = input.Trim();
        requestedSpellName = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();

        foreach (var entry in _world.Database.Spells.Values
                     .Where(candidate => CanPlayerCastOrInvoke(candidate))
                     .SelectMany(candidate => GetSpellCommandAliases(candidate).Select(alias => (Spell: candidate, Alias: alias)))
                     .OrderByDescending(entry => entry.Alias.Length)
                     .ThenBy(entry => entry.Spell.ReqLevel)
                     .ThenBy(entry => entry.Spell.Number))
        {
            if (trimmed.Equals(entry.Alias, StringComparison.OrdinalIgnoreCase))
            {
                spell = entry.Spell;
                target = string.Empty;
                return true;
            }

            if (trimmed.Length > entry.Alias.Length
                && trimmed.StartsWith(entry.Alias, StringComparison.OrdinalIgnoreCase)
                && char.IsWhiteSpace(trimmed[entry.Alias.Length]))
            {
                spell = entry.Spell;
                target = trimmed[entry.Alias.Length..].TrimStart();
                return true;
            }
        }

        spell = new GameSpell();
        target = string.Empty;
        return false;
    }

    // A spell is castable only if the player has actually LEARNED it — not merely if their class could
    // (spells are learned from "scroll of <x>" items read into the spellbook, never auto by level).
    // PlayerKnowsSpell reads the unified learned flag set by reading a scroll (LearnSpell), by KAI
    // auto-learn at training (SyncAutomaticKaiPowers), and by quest teaching (learnspell, e.g. Kuel's
    // totem forms). Use-eligibility then mirrors the spell-eligibility check (below). Requiring the
    // LEARNED flag already blocks unreachable orphans (e.g. banish #435, taught by nothing).
    private bool CanPlayerCastOrInvoke(GameSpell spell)
        => PlayerKnowsSpell(spell.Number) && PlayerCanUseLearnedSpell(spell);

    // Spell eligibility: `if (spell.Magery != 0 && (class.MageryType != spell.Magery ||
    // class.MageryLvl < spell.MageryLvl)) return 0;` followed by the ReqLevel ("too powerful") gate. The
    // magery-type/level test is GATED on spell.Magery != 0 — a Magery-0 spell bypasses it entirely and is
    // usable by ANY class that has it in its spellbook (subject only to ReqLevel). This is how a Mystic
    // invokes the quest-taught totem forms (#838-842, Magery 0): once Kuel teaches them, the magery match
    // is irrelevant. (The old gate required a magery-type MATCH via memorize/listKai, which silently
    // blocked every learned Magery-0 power — bug #184.) Learning from a scroll/trainer is a SEPARATE
    // gate (CanPlayerMemorizeSpell) that still requires the magery match — only USE bypasses it.
    private bool PlayerCanUseLearnedSpell(GameSpell spell)
    {
        if (!_world.Database.Classes.TryGetValue(_player.ClassId, out var cls))
            return false;
        if (spell.Magery != 0 && (cls.MageryType != spell.Magery || cls.MageryLvl < spell.MageryLvl))
            return false;
        return _player.Level >= spell.ReqLevel;
    }

    private static IEnumerable<string> GetSpellCommandAliases(GameSpell spell)
    {
        yield return spell.Short;

        if (!spell.Name.Equals(spell.Short, StringComparison.OrdinalIgnoreCase))
            yield return spell.Name;
    }

    // A combat (weapon-replacing) damage spell. MinBase/MaxBase are the BASE band before per-level
    // scaling (MaxInc/MaxIncLvls), so MaxBase is often 0 or even negative for higher-tier spells whose
    // damage comes mostly from level growth — divine fury (#267) is MinBase 50 / MaxBase -70 / MaxInc 9
    // ⇒ a real 50..290 band at L40, and god's wrath (#122) is 57/30/4. Gating on raw `MaxBase > 0`
    // wrongly excluded 24 such spells (divine fury, soul rip, dragonfire, earthfist, soulstrike, blaze,
    // colour spray, forked lightning, …). Accept a positive max OR positive per-level growth.
    // NOTE: the raw-base trap cuts the other way too — MinBase is negative on eldritch storm #1058 and
    // AttType 0 means COLD, not "no element" — so this predicate still under-reports. It is deliberately
    // NOT the gate for the queued combat round any more (bugs #205/#206); see
    // TryExecutePendingCombatSpellRoundAsync, which re-casts whatever was queued.
    private static bool IsCombatSpell(GameSpell spell)
        => spell.MinBase > 0 && spell.AttType > 0 && (spell.MaxBase > 0 || spell.MaxInc > 0);

    private static bool RequiresExplicitSpellTarget(GameSpell spell)
        => IsCombatSpell(spell) && spell.Targets == 8;

    private void ClearPendingCombatSpellSelection()
    {
        _pendingCombatSpellId = 0;
        _player.PendingCombatSpellId = 0;
    }

    private void SetPendingCombatSpellSelection(int spellId)
    {
        _pendingCombatSpellId = spellId;
        _player.PendingCombatSpellId = spellId;
    }

    private bool TryResolveOffensiveSpellTarget(string target, bool allowImplicitCombatTarget, out MonsterInstance? monster, out Player? playerTarget)
    {
        monster = null;
        playerTarget = null;

        if (!string.IsNullOrWhiteSpace(target))
            monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
        else if (allowImplicitCombatTarget && _player.CombatTarget != null && !_player.CombatTarget.IsDead)
            monster = _player.CombatTarget;

        if (monster != null)
        {
            monster = ResolveMonsterProtection(monster);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(target))
            playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);
        else if (allowImplicitCombatTarget && _player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP)
            playerTarget = _player.PlayerCombatTarget;

        return playerTarget != null;
    }

    /// <summary>
    /// The line stock prints when a CAST names a target that is not in the room. The cast command
    /// resolves the target token and, on a miss, prints
    ///     ESC[1;31;40m                              // bright red
    ///     "You do not see %s here!" with the name the player typed
    /// echoing the word the player typed. "Cast at whom?" is not a stock string at all — it was
    /// invented here, and MegaMUD loops on it because it never appears in stock (ticket #9/#10).
    ///
    /// With NO target token stock does not reach this at all: it takes the no-target route and says
    /// nothing. We keep a message for that case because our offensive path requires a resolved target;
    /// it is the narrower remaining divergence and is called out in the ticket follow-up.
    /// </summary>
    private async Task SendCastTargetNotHereAsync(string target)
    {
        string typed = target.Trim();
        await _client.SendLineAsync(typed.Length == 0
            ? MudAnsi.Error("You do not see your target here!")
            : MudAnsi.Error($"You do not see {typed} here!"));
    }

    private async Task<bool> HandleOffensiveSpellCastAsync(
        GameSpell spell,
        string target,
        bool allowImplicitCombatTarget,
        bool keepAutoCombatSpellSelected,
        bool reportMissingTarget,
        int extraProjectileBudget = 0,
        bool itemSourced = false,
        bool emitCombatOffOnEngage = false)
    {
        if (!TryResolveOffensiveSpellTarget(target, allowImplicitCombatTarget, out var monster, out var playerTarget))
        {
            if (reportMissingTarget)
                await SendCastTargetNotHereAsync(target);
            return false;
        }

        // Once a target (monster OR player) resolves, "*Combat
        // Off*" and the autocombat drop run BEFORE the cast — i.e. before the can-attack / lawful /
        // resist / no-effect checks. So the caster's OWN autocombat ends the moment they cast an
        // offensive spell at a resolved target, even if that cast is then blocked or fizzles: they need
        // to know THEIR combat stopped (they're still taking hits, just not retaliating). Only an
        // unresolved target ("Cast at whom?") skips it. Stopping the loop also makes the engage inside
        // Execute* re-announce "*Combat Engaged*" (its already-engaged suppression keys off InCombat,
        // now cleared) on a cast that does proceed. Manual `cast` only; pulse/proc/chain/swarm pass false.
        bool didCombatOffToggle = emitCombatOffOnEngage && _player.InCombat;
        if (didCombatOffToggle)
        {
            await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
            _player.StopCombatLoop();
        }

        if (playerTarget != null)
        {
            if (!CanAttackPlayer(playerTarget, out var failureMessage))
            {
                if (reportMissingTarget)
                    await _client.SendLineAsync(failureMessage);
                return false;
            }

            ChargeOffensiveSpellCost(spell, itemSourced);
            // forceAnnounceEngaged: when we just emitted "*Combat Off*", the PvP re-engage must print the
            // balancing "*Combat Engaged*" (the paired toggle). The PvP path otherwise engages silently for
            // damage spells (the hit is the notice), so opening casts are unaffected. The monster path
            // already re-announces because StopCombatLoop cleared InCombat.
            await ExecuteOffensiveSpellAgainstPlayerAsync(spell, playerTarget, keepAutoCombatSpellSelected, forceAnnounceEngaged: didCombatOffToggle);
            return true;
        }

        var targetMonster = monster!;
        if (!CanAttackMonster(targetMonster, out var monsterFailureMessage))
        {
            if (reportMissingTarget)
                await _client.SendLineAsync(monsterFailureMessage);
            return false;
        }

        ChargeOffensiveSpellCost(spell, itemSourced);
        await ExecuteOffensiveSpellAgainstMonsterAsync(spell, targetMonster, keepAutoCombatSpellSelected, extraProjectileBudget);
        return true;
    }

    // Charge the caster for an offensive cast. A typed `cast` spends mana + energy and consumes the
    // once-per-round cast token (SpendSpellCast). An item-sourced cast — the `use` command firing an
    // item's ability-43 spell — costs the caster NOTHING: it's paid by the item's own charge. Item
    // use routes ability 43 through the same cast resolver as CAST but never deducts
    // mana/stamina, never adds an action delay, and never clears the cast-token bit — so an item
    // cast is independent of the spell round entirely (you can use-cast and still cast/melee that round).
    private void ChargeOffensiveSpellCost(GameSpell spell, bool itemSourced)
    {
        if (itemSourced)
            return;

        _player.CurrentMana -= spell.ManaCost;
        SpendSpellCast(spell);
    }

    private async Task<bool> TryBeginOrRefreshMonsterCombatSpellAsync(GameSpell spell, string target, bool reportMissingTarget)
    {
        bool inCombat = _player.InCombat;

        // A re-cast while already engaged may omit the target (implicit current combat target); the
        // opening cast must name one.
        if (!TryResolveOffensiveSpellTarget(target, allowImplicitCombatTarget: inCombat, out var monster, out var playerTarget))
        {
            if (reportMissingTarget)
                await SendCastTargetNotHereAsync(target);
            return true;
        }

        if (playerTarget != null)
            return false;   // PvP combat spell — handled by the caller's player path

        var targetMonster = monster!;
        if (!CanAttackMonster(targetMonster, out var monsterFailureMessage))
        {
            if (reportMissingTarget)
                await _client.SendLineAsync(monsterFailureMessage);
            return true;
        }

        // Same per-reason rejects as ExecuteOffensiveSpellAgainstMonsterAsync: the required-to-hit
        // (ability 158) mismatch has its own line; creature-type and spell-immunity report
        // "Your spell has no effect on <name>."
        if (!CanSpellSatisfyRequiredToHit(spell, targetMonster.Template))
        {
            await _client.SendLineAsync($"{MudAnsi.White}You are not using the required spell to hit this monster!{MudAnsi.Reset}");
            return true;
        }

        if (!CanSpellAffectMonsterTarget(spell, targetMonster.Template)
            || (targetMonster.Template.Abilities.GetValueOrDefault(SpellImmunityAbilityId) > 0
                && spell.ReqLevel <= targetMonster.Template.Abilities.GetValueOrDefault(SpellImmunityAbilityId)))
        {
            await _client.SendLineAsync($"{MudAnsi.White}Your spell has no effect on {targetMonster.DisplayName}.{MudAnsi.Reset}");
            return true;
        }

        // A combat-round spell is queued for the round timer, not cast immediately. Re-casting it
        // while already engaged is a "stop cast / restart cast" — emit *Combat Off* then re-engage
        // (*Combat Engaged*), keeping the SAME round timer and pending action (EngageCombatAsync keeps a
        // future NextMonsterAttackAtUtc). Nothing about the queued cast changes: no extra mana, no
        // immediate damage; it resolves on the pulse it would have. The opening cast just engages.
        if (inCombat)
            await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));

        await EngageCombatAsync(targetMonster, PlayerCombatRoundAction.Attack, announceEngaged: true);
        SetPendingCombatSpellSelection(spell.Number);
        return true;
    }

    private async Task ExecuteOffensiveSpellAgainstPlayerAsync(GameSpell spell, Player playerTarget, bool keepAutoCombatSpellSelected, bool forceAnnounceEngaged = false)
    {
        // "Cast on ending" carrier (ability 151, no harm): cast a rolled pool spell at the target instead of
        // dealing damage — same fix as the vs-monster path so a carrier (#988 "evil random", etc.)
        // procced in PvP doesn't leak its 984-987 id pool as HP damage. The rolled sub-spell handles its
        // own affect/resist/EP charge.
        if (TryResolveCarrierSpell(spell, out var carrierSub))
        {
            if (carrierSub != null && _chainDepth < MaxChainDepth)
            {
                _chainDepth++;
                try { await ExecuteOffensiveSpellAgainstPlayerAsync(carrierSub, playerTarget, keepAutoCombatSpellSelected: false); }
                finally { _chainDepth--; }
            }
            return;
        }

        if (!CanSpellAffectPlayerTarget(spell, playerTarget))
        {
            // Cast-at-player reject: "...on <name>."
            // with a period — the SAME string the monster path uses, not a "!".
            await _client.SendLineAsync($"{MudAnsi.White}Your spell has no effect on {playerTarget.Name}.{MudAnsi.Reset}");
            return;
        }

        // The evil-point rule decides: a continued cast inside our own window is free, self-defence is free
        // and opens nothing, a robbing edge is charged in full. Opens/refreshes the directed edge with
        // the charged amount (a continued cast contributes 0, so AddEvilTimer keeps the original charge
        // and the travel gate persists).
        await ApplyPvpAggressionEvilAsync(playerTarget);

        bool targetHasAntiMagic = playerTarget.HasActiveAbility(_world.Database, ClassAntiMagicAbilityId);
        if (IsOffensiveSpellResisted(spell, playerTarget.MagicResist, targetHasAntiMagic))
        {
            await _client.SendLineAsync(GameAnsi.SpellFailure($"You attempt to cast {spell.Name} at {playerTarget.Name}, but the spell is resisted."));
            await SendCombatMessagesToPlayerAsync(playerTarget, [GameAnsi.SpellFailure($"You resisted {_player.Name}'s cast of {spell.Name}.")]);

            bool alreadyResistedPvpCombat = _player.InCombat && _player.PlayerCombatTarget == playerTarget;
            if (!alreadyResistedPvpCombat)
                await EngagePvpCombatAsync(playerTarget, PlayerCombatRoundAction.Attack, announceEngaged: forceAnnounceEngaged);

            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);

            return;
        }

        if (spell.Abilities.ContainsKey(ShatterWeaponAbilityId))
        {
            // Ability 85: shatter the victim's wielded weapon (no HP damage); engage and finish.
            await SendSpellStartMessagesAsync(spell, playerTarget.Name, playerTarget);
            await ApplyShatterWeaponEffectAsync(spell, playerTarget);

            bool alreadyShatterCombat = _player.InCombat && _player.PlayerCombatTarget == playerTarget;
            if (!alreadyShatterCombat)
                await EngagePvpCombatAsync(playerTarget, PlayerCombatRoundAction.Attack, announceEngaged: forceAnnounceEngaged);
            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);
            return;
        }

        // A duration-bearing harm spell is a DoT on the target's
        // active-spell slot (rolled magnitude + duration), NO instant damage; ProcessActiveSpellUpkeep
        // drains `magnitude` HP per medium tick until it expires. Recalc so any secondary stat/AC debuff
        // the spell also carries (e.g. ice storm 106, acid rain 2) applies now and reverses on expiry.
        // dur==0 harm falls through to the instant path below. (See reference_dot_message_model.)
        if (IsDurationDamageSpell(spell))
        {
            int dotMagnitude = RollSpellMagnitude(spell, _player.Level);
            int dotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            if (playerTarget.AddOrRefreshActiveSpell(spell.Number, dotMagnitude, dotDuration))
                _world.RecalculatePlayerStats(playerTarget);

            await SendSpellStartMessagesAsync(spell, playerTarget.Name, playerTarget);
            // The target also sees the ability-115 Line3 status line ("You are
            // on fire!") on each application.
            string? pvpOngoing = ResolveSpellOngoingStatusLine(spell, playerTarget.Name);
            if (pvpOngoing != null)
                await SendCombatMessagesToPlayerAsync(playerTarget, [GameAnsi.SpellHostile(pvpOngoing)]);

            bool alreadyDotPvp = _player.InCombat && _player.PlayerCombatTarget == playerTarget;
            if (!alreadyDotPvp)
                await EngagePvpCombatAsync(playerTarget, PlayerCombatRoundAction.Attack, announceEngaged: forceAnnounceEngaged);
            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);
            return;
        }

        // HP is subtracted ONLY inside the explicit harm cases
        // of the per-ability switch — 1, 8, 17, 19, 95, exactly HarmAbilityIds. Every other ability
        // id falls through to the switch DEFAULT, which for a single-target
        // cast (Targets 0/2/6/8) does only:
        //     Duration != 0  ->  timed slot on the victim, NO damage
        //     Duration == 0  ->  flavour message only, NO damage
        // There is no default damage path anywhere in the function. So a no-harm control spell must never
        // roll MinBase..MaxBase as HP loss — that is what let `entangle` (#93: abilities 74 Hold person /
        // 52 / 115, MinBase=MaxBase=100) kill a player outright with one cast. The whole crowd-control
        // suite shares that shape and was equally lethal: hold person #66, sleep #1110/#372, stun #128,
        // song of stunning #274, fear #60/#397, terror #822, confusion #829, web #1141, chain #848,
        // wrathful curse #853, and the item procs (witchwood spear #779, darkwood staff #849).
        //
        // Applying the whole spell as a timed slot — rather than only the ability-74 root that
        // ApplyKnockdownSpellToPlayer handles — is what makes the non-root controls (fear's 60 "Flee",
        // confusion's 71) actually take effect, since the player's ability aggregation reads the slot.
        if (!HasHarmAbility(spell))
        {
            if (spell.Duration > 0)
            {
                int controlMagnitude = RollSpellLevelScaledAmount(spell);
                int controlDuration = RollPlayerCastSpellDuration(spell, _player.Level);
                if (controlDuration > 0 && playerTarget.AddOrRefreshActiveSpell(spell.Number, controlMagnitude, controlDuration))
                    _world.RecalculatePlayerStats(playerTarget);
            }

            await SendSpellStartMessagesAsync(spell, playerTarget.Name, playerTarget);

            // The victim also sees the ability-115 Line3 status line.
            string? controlOngoing = ResolveSpellOngoingStatusLine(spell, playerTarget.Name);
            if (controlOngoing != null)
                await SendCombatMessagesToPlayerAsync(playerTarget, [GameAnsi.SpellHostile(controlOngoing)]);

            bool alreadyControlPvp = _player.InCombat && _player.PlayerCombatTarget == playerTarget;
            if (!alreadyControlPvp)
                await EngagePvpCombatAsync(playerTarget, PlayerCombatRoundAction.Attack, announceEngaged: forceAnnounceEngaged);
            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);

            await TryChainOffensiveCastAsync(spell, monster: null, playerTarget);   // ability 151/164 tail
            return;
        }

        int playerSpellDamage;
        if (SpellIsSmite(spell))
        {
            // Ability 95: smite ignores the damage roll and resistances — it drops the victim just past
            // the death threshold (resulting HP = DeathHP - 1), a guaranteed kill.
            playerSpellDamage = SpellEffects.SmiteDamageVsPlayer(playerTarget.CurrentHP, Player.DeathHP);
        }
        else
        {
            // Magnitude band (ComputeSpellMagnitudeBand): min = MinBase, max grows with
            // min(level, Cap) — matches the monster path. (PvP swarm multi-projectile is not yet wired;
            // this fixes the per-hit damage scaling.)
            playerSpellDamage = RollSpellMagnitude(spell, _player.Level);

            playerSpellDamage = ApplyElementalSpellDamage(spell, playerSpellDamage,
                abil => playerTarget.GetActiveAbilityValue(_world.Database, abil));
            playerSpellDamage = ApplyOffensiveMagicResistance(spell, playerSpellDamage, playerTarget.MagicResist, targetHasAntiMagic);
        }

        await SendSpellStartMessagesAsync(spell, playerTarget.Name, playerTarget);
        playerTarget.CurrentHP -= playerSpellDamage;
        ApplyDrainToCaster(spell, playerSpellDamage);   // ability 8: leech the dealt HP onto the caster
        await SendPlayerDamageSpellMessagesAsync(spell, playerTarget, playerSpellDamage);

        if (playerTarget.CurrentHP > Player.DeathHP && SpellIsKnockdown(spell))
            ApplyKnockdownSpellToPlayer(spell, playerTarget, _player.Level);

        if (playerTarget.CurrentHP <= 0)
        {
            // Same rule as every other damage source: the drop line is the conscious → unconscious
            // transition, so a spell that kills outright emits only the killed line.
            var targetDeathMessages = new List<string>();
            if (playerTarget.CurrentHP > Player.DeathHP)
                targetDeathMessages.Add(GameAnsi.DropsToTheGround($"{playerTarget.Name} drops to the ground!"));

            if (playerTarget.CurrentHP <= Player.DeathHP)
                targetDeathMessages.Add(GameAnsi.KilledOutright("You have been killed!"));

            await SendCombatMessagesToPlayerAsync(playerTarget, targetDeathMessages);

            if (_player.InCombat && _player.PlayerCombatTarget == playerTarget)
            {
                _player.ClearCombatState();
                await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
            }
            else
            {
                ClearPendingCombatSpellSelection();
            }

            var targetSession = _world.GetClientForPlayer(playerTarget.Name)?.Session;
            if (targetSession != null)
            {
                await targetSession.HandleExternalPlayerDeathAsync();
            }
            else
            {
                playerTarget.ClearCombatState();
            }

            return;
        }

        bool alreadyPvpCombat = _player.InCombat && _player.PlayerCombatTarget == playerTarget;
        if (!alreadyPvpCombat)
            await EngagePvpCombatAsync(playerTarget, PlayerCombatRoundAction.Attack, announceEngaged: forceAnnounceEngaged);

        if (keepAutoCombatSpellSelected)
            SetPendingCombatSpellSelection(spell.Number);

        await TryChainOffensiveCastAsync(spell, monster: null, playerTarget);   // ability 151/164 chain
    }

    // Multi-projectile cap: a damage spell
    // fires at most 20 projectiles per round.
    private const int MaxSpellProjectilesPerRound = 20;

    private async Task ExecuteOffensiveSpellAgainstMonsterAsync(GameSpell spell, MonsterInstance targetMonster, bool keepAutoCombatSpellSelected, int extraProjectileBudget = 0)
    {
        // "Cast on ending" carrier (ability 151, no harm): cast a rolled pool spell at the target instead of
        // dealing damage. Runs FIRST so the carrier's own MinBase..MaxBase id pool never reaches the
        // damage path (see TryResolveCarrierSpell — the "evil random"/Soulwaste bug). Depth-guarded like
        // the chain path; the rolled sub-spell runs the full affect/resist/damage checks itself.
        if (TryResolveCarrierSpell(spell, out var carrierSub))
        {
            if (carrierSub != null && _chainDepth < MaxChainDepth)
            {
                _chainDepth++;
                try { await ExecuteOffensiveSpellAgainstMonsterAsync(carrierSub, targetMonster, keepAutoCombatSpellSelected: false); }
                finally { _chainDepth--; }
            }
            return;
        }

        // Cast rejects: a creature-type mismatch (Affects Animal/Living/…)
        // or a too-low spell level vs the monster's spell-immunity (ability 139) both report
        // "Your spell has no effect on <name>." — the required-to-hit (ability 158) mismatch reports a
        // DISTINCT line. (We previously collapsed all three into one "...no effect on <name>!" with a "!".)
        if (!CanSpellAffectMonsterTarget(spell, targetMonster.Template))
        {
            await _client.SendLineAsync($"{MudAnsi.White}Your spell has no effect on {targetMonster.DisplayName}.{MudAnsi.Reset}");
            return;
        }

        if (!CanSpellSatisfyRequiredToHit(spell, targetMonster.Template))
        {
            await _client.SendLineAsync($"{MudAnsi.White}You are not using the required spell to hit this monster!{MudAnsi.Reset}");
            return;
        }

        int monsterSpellImmunityLevel = targetMonster.Template.Abilities.GetValueOrDefault(SpellImmunityAbilityId);
        if (monsterSpellImmunityLevel > 0 && spell.ReqLevel <= monsterSpellImmunityLevel)
        {
            await _client.SendLineAsync($"{MudAnsi.White}Your spell has no effect on {targetMonster.DisplayName}.{MudAnsi.Reset}");
            return;
        }

        if (IsOffensiveSpellResisted(spell, targetMonster.EffectiveMagicResist, targetHasAntiMagic: false))
        {
            await _client.SendLineAsync(GameAnsi.SpellFailure($"You attempt to cast {spell.Name} at {targetMonster.DisplayName}, but the spell is resisted."));

            bool alreadyResistedMonsterCombat = _player.InCombat && _player.CombatTarget == targetMonster;
            await EngageCombatAsync(targetMonster, PlayerCombatRoundAction.Attack, announceEngaged: !alreadyResistedMonsterCombat);

            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);

            return;
        }

        if (SpellIsSmite(spell))
        {
            // Ability 95 vs monster: instant kill (damage = HP + 2), bypassing the damage roll.
            await SendSpellStartMessagesAsync(spell, targetMonster.DisplayName, playerTarget: null);
            int smiteDamage = SpellEffects.SmiteDamageVsMonster(targetMonster.CurrentHP);
            targetMonster.CurrentHP -= smiteDamage;
            await SendMonsterDamageSpellMessagesAsync(spell, targetMonster, smiteDamage);

            ClearPendingCombatSpellSelection();
            await HandleMonsterDeath(targetMonster, CombatEngine.CreateMonsterDeathResult(targetMonster));
            return;
        }

        // A haste/slow spell (ability 87) is a duration stat-debuff, not damage. Apply it to
        // the monster's active-spell slot at the rolled magnitude (e.g. slow stores 125 ⇒ EU×1.25)
        // and engage — no HP damage.
        if (spell.Duration > 0 && spell.Abilities.ContainsKey(HasteSlowAbilityId))
        {
            int magnitude = RollSpellLevelScaledAmount(spell);
            int debuffDuration = RollPlayerCastSpellDuration(spell, _player.Level);

            targetMonster.AddOrRefreshActiveSpell(_world.Database, spell.Number, magnitude, debuffDuration);

            await _client.SendLineAsync(GameAnsi.SpellHostile($"You cast {spell.Name} on {targetMonster.DisplayName}."));

            bool alreadyDebuffCombat = _player.InCombat && _player.CombatTarget == targetMonster;
            await EngageCombatAsync(targetMonster, PlayerCombatRoundAction.Attack, announceEngaged: !alreadyDebuffCombat);

            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);

            return;
        }

        // A duration-bearing harm spell (ability 1/8) is a DoT,
        // not an instant hit. Store it on the monster's active-spell slot at the rolled magnitude +
        // duration and engage; NO instant damage. The monster medium-tick upkeep (TickSpellUpkeepEffects
        // case 1/8) drains `magnitude` HP per tick until it expires. dur==0 harm spells fall through to
        // the instant-projectile path below. (See reference_dot_message_model.)
        if (IsDurationDamageSpell(spell))
        {
            int dotMagnitude = RollSpellMagnitude(spell, _player.Level);
            int dotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            targetMonster.AddOrRefreshActiveSpell(_world.Database, spell.Number, dotMagnitude, dotDuration);

            await SendSpellStartMessagesAsync(spell, targetMonster.DisplayName, playerTarget: null);

            bool alreadyDotCombat = _player.InCombat && _player.CombatTarget == targetMonster;
            await EngageCombatAsync(targetMonster, PlayerCombatRoundAction.Attack, announceEngaged: !alreadyDotCombat);

            if (keepAutoCombatSpellSelected)
                SetPendingCombatSpellSelection(spell.Number);

            return;
        }

        // The cast announce ("You cast X") prints once; each projectile prints its own damage line.
        await SendSpellStartMessagesAsync(spell, targetMonster.DisplayName, playerTarget: null);

        // Per-hit damage uses the magnitude band (ComputeSpellMagnitudeBand): min = MinBase, max
        // grows with min(level, Cap) — NOT the old flat MinBase..MaxBase + levels-above-req roll, which
        // under-scaled level-capped spells (e.g. meteor swarm read 20-30 at L35 instead of 20-45).
        async Task FireOneProjectileAsync()
        {
            int hit = RollSpellMagnitude(spell, _player.Level);
            hit = ApplyElementalSpellDamage(spell, hit, abil => targetMonster.GetEffectiveAbility(abil));
            hit = ApplyOffensiveMagicResistance(spell, hit, targetMonster.EffectiveMagicResist, targetHasAntiMagic: false);
            targetMonster.CurrentHP -= hit;
            ApplyDrainToCaster(spell, hit);   // ability 8: leech the dealt HP onto the caster
            await SendMonsterDamageSpellMessagesAsync(spell, targetMonster, hit);
        }

        // Same rule as the vs-player path: monster HP is subtracted only inside the
        // explicit harm cases (HarmAbilityIds); every other ability falls to the switch default, which
        // just adds a timed slot (Duration != 0) or does nothing. A no-harm control spell fires NO
        // projectiles — without this, casting entangle/hold person/sleep at a monster rolled its
        // MinBase=MaxBase=100 as ~100 HP of phantom damage. The knockdown root and the lingering-debuff
        // slot below still apply, so the spell still does its actual job.
        if (HasHarmAbility(spell))
        {
            // First projectile — its EnergyCost + ManaCost were already spent by the caller, as on every
            // single-cast path.
            await FireOneProjectileAsync();

            // The "swarm" loop: in the combat round a damage spell fires one EXTRA
            // projectile per affordable EnergyCost (stamina) + ManaCost, capped at 20 total, stopping
            // on a kill. The fixed ~1000 stamina pool / EnergyCost sets the count — meteor swarm (250) = 4,
            // magic missile (1000) = 1 — and each projectile also drains ManaCost (meteor swarm: 4×4 = 16
            // mana/round). Non-combat callers (weapon procs, chain, manual instant) pass 0 and fire once.
            for (int extra = 0; extra < extraProjectileBudget && !targetMonster.IsDead; extra++)
            {
                if (_player.CurrentEnergy < spell.EnergyCost || _player.CurrentMana < spell.ManaCost)
                    break;
                _player.CurrentEnergy -= spell.EnergyCost;
                _player.CurrentMana -= spell.ManaCost;
                await FireOneProjectileAsync();
            }
        }

        if (!targetMonster.IsDead && SpellIsKnockdown(spell))
            ApplyKnockdownSpellToMonster(spell, targetMonster, _player.Level);

        // A poison spell writes the monster's poison level to
        // max(current, magnitude) on top of the instant damage; the slow tick then drains it each
        // slow pass (TickPoison). Mirrors the player-target poison apply (max-merge, no monster cure).
        if (!targetMonster.IsDead && spell.Abilities.ContainsKey(PoisonSpellAbilityId))
        {
            int poisonMagnitude = RollSpellLevelScaledAmount(spell);
            targetMonster.PoisonLevel = Math.Max(targetMonster.PoisonLevel, poisonMagnitude);
        }

        if (targetMonster.IsDead)
        {
            ClearPendingCombatSpellSelection();
            await HandleMonsterDeath(targetMonster, CombatEngine.CreateMonsterDeathResult(targetMonster));
            return;
        }

        // An offensive duration spell (disease, plague, ...)
        // deals its instant damage AND leaves a timed slot whose stat-debuff abilities (AC 2, dmg 4,
        // DR 7, accuracy 22, dodge 34, MR 36, slow 87/68) linger on the monster — combat
        // reads them via GetEffectiveAbility. (Pure-damage and flavor-duration spells add nothing.)
        // ...or an over-time spell (disease/plague 1, drain 8, heal-over-time 18, energy 11,
        // anti-poison 20): the slot is what ProcessMonsterSpellUpkeep ticks each medium pass so the
        // DoT/HoT actually runs its course instead of landing only its instant cast damage.
        if (spell.Duration > 0 && (SpellHasLingeringMonsterDebuff(spell) || SpellHasMonsterUpkeepEffect(spell)))
        {
            int debuffMagnitude = RollSpellLevelScaledAmount(spell);
            int slotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            targetMonster.AddOrRefreshActiveSpell(_world.Database, spell.Number, debuffMagnitude, slotDuration);
        }

        bool alreadyMutualCombat = _player.InCombat && _player.CombatTarget == targetMonster;
        await EngageCombatAsync(targetMonster, PlayerCombatRoundAction.Attack, announceEngaged: !alreadyMutualCombat);

        if (keepAutoCombatSpellSelected)
            SetPendingCombatSpellSelection(spell.Number);

        await TryChainOffensiveCastAsync(spell, targetMonster, playerTarget: null);   // ability 151/164 chain
    }

    // Chain cast (ability 151 = follow-up spell number, 164 = its probability): after a landed,
    // non-lethal offensive cast, a roll of 0..99 < prob ⇒ recursively cast the chained spell at the same
    // target (the chained cast spends its own mana). Depth-guarded
    // against cascades. Only the 3 stock chain spells carry both ids (spear→, dragonfire→, thunderclap→).
    private int _chainDepth;
    private const int MaxChainDepth = 4;
    private const int ChainSpellAbilityId = 151;
    private const int ChainProbabilityAbilityId = 164;
    private const int SummonAbilityId = 12;            // summon (value = monster template id)
    private const int MaxMonsterSummonChildren = 10;   // summoner child-list size (10 slots)
    private const int MaxMonsterSpellChainDepth = 3;   // guard against ability-151 "cast on ending" cascades

    // Resolve the monster template id a summon spell (ability 12) spawns. The id is normally the
    // ability VALUE, but when that value is 0 it falls back to the rolled MinBase..MaxBase — the
    // same "value 0 → MinBase" convention the scripted-command / teleport paths already use. ~30 stock
    // summons rely on this (transform, vampire/demon/wolfman summons, the giant-cloak summons, and
    // "summon enigma lord" #1233 → MinBase 921, the hostile Enigma Lord boss for the evil quest stage 18).
    // Returns 0 for a non-summon spell or one with no usable id.
    private List<int> ResolveSummonTemplateIds(GameSpell spell) => ResolveSummonTemplateIds(spell, Random.Shared);

    // Every monster a summon cast spawns, in slot order — ONE entry per ability-12 SLOT, not one
    // per cast. All three summon loops walk the full ten ability slots and spawn a monster
    // once for each slot carrying ability 12: the self/ally path (Targets 1/2/4),
    // the area path (the `case 12` at both slot loops), and the single-target path for a
    // player cast. Each slot contributes its OWN AbilVal as the template id, falling back to the single
    // magnitude rolled before the loop when that slot's value is 0.
    //
    // A stock summon spell therefore routinely spawns SEVERAL creatures at once, and the count is carried
    // by duplicate slots: the hydra's create spell #90 carries ability 12 six times (all value 590) — which is
    // why stock spawns the hydra with a full set of six heads — and the Great Hydra's #1309 carries it
    // twice (1029) for two massive heads. In both cases the slot count matches the head's own GameLimit,
    // so the create spell fills the head population in one shot. Reading the Abilities DICTIONARY instead
    // collapses those duplicates to a single entry (last slot wins), which is why the monster paths used
    // to summon exactly one escort no matter how many the spell listed.
    //
    // Static form so the world-level CreateSpell path (FireMonsterCreateSpell, no command-parser session)
    // shares the SAME resolution — the dragon-summon-tapestry chain (#1101 → ancient tapestry #1002,
    // AbilVal 0 / MinBase 1002) only spawns through the MinBase fallback, which the spawn path used to skip.
    internal static List<int> ResolveSummonTemplateIds(GameSpell spell, Random rng)
    {
        var templateIds = new List<int>();
        if (!MonsterSpellSummonsReinforcement(spell))
            return templateIds;

        // Rolled ONCE per cast (the magnitude is computed before the slot loop and every
        // zero-value slot reuses it) — a multi-slot zero-value summon spawns one species, not several.
        int rolledFallback = spell.MaxBase > spell.MinBase
            ? rng.Next(spell.MinBase, spell.MaxBase + 1)
            : spell.MinBase;

        foreach (var (abilityId, abilityValue) in EnumerateAbilitySlots(spell))
        {
            if (abilityId != SummonAbilityId)
                continue;

            int templateId = abilityValue > 0 ? abilityValue : rolledFallback;
            if (templateId > 0)
                templateIds.Add(templateId);
        }

        return templateIds;
    }

    // GameSpell carries the ability list twice — the ordered, duplicate-preserving AbilitySlots and the
    // collapsing Abilities dictionary — and the loader fills both. Slot-order-sensitive logic must read
    // AbilitySlots, but a GameSpell built by hand (a fake, a tool) may have populated only the dictionary,
    // and silently resolving such a spell to "no abilities" would turn a summon into a no-op. Fall back to
    // the dictionary only when NO slots were recorded at all, which for loaded data means the spell has no
    // abilities either — so this never changes what real game data resolves to.
    private static IEnumerable<KeyValuePair<int, int>> EnumerateAbilitySlots(GameSpell spell)
        => spell.AbilitySlots.Count > 0 ? spell.AbilitySlots : spell.Abilities;

    // True when a spell's summon ability (12) would actually spawn a reinforcement: at least one summon
    // slot with a direct template id (AbilVal > 0), or a zero-value slot plus a valid MinBase..MaxBase id
    // pool to roll from. Reads the duplicate-preserving slot list for the same reason
    // ResolveSummonTemplateIds does — the dictionary keeps only the LAST slot, so a spell whose final
    // summon slot is 0 would read as "not a summon". The single source of the summon-route rule —
    // ResolveSummonTemplateIds and the routing audit both consume it.
    internal static bool MonsterSpellSummonsReinforcement(GameSpell spell)
    {
        bool hasZeroValueSummonSlot = false;
        foreach (var (abilityId, abilityValue) in EnumerateAbilitySlots(spell))
        {
            if (abilityId != SummonAbilityId)
                continue;
            if (abilityValue > 0)
                return true;
            hasZeroValueSummonSlot = true;
        }

        return hasZeroValueSummonSlot && spell.MinBase > 0 && spell.MaxBase >= spell.MinBase;
    }
    private const int CharmAbilityId = 6;              // charm/enslave (convert monster to a pet)
    private const int RemoveArmourAbilityId = 84;      // remove curse (strip cursed worn items)
    private const int ShatterWeaponAbilityId = 85;     // shatter the target's wielded weapon
    private const int ItemHardnessAbilityId = 86;      // weapon hardness vs shatter magnitude
    private const int ArenaRoomType = 5;               // room type 5 = combat arena (shatter-protected)
    private const int SilencedAbilityId = 79;          // silence: "Your spell fails!"
    private const int KaiSilencedAbilityId = 159;      // KAI silence: "Your power fails!"
    private const int ConfusionAbilityId = 71;         // confusion: roll 0..99 < value ⇒ action fizzles
    private const int ConfuseMessageAbilityId = 101;   // per-spell Confuse Message id (e.g. "stunned" #128 → 56 "You are stunned!")
    private const string DefaultConfuseSelfMessage = "You look around stupidly and do nothing!";
    private const string DefaultConfuseRoomMessage = "%s looks around stupidly and foams at the mouth!";

    // Ability 84 (remove curse): force-remove the target's worn items that are cursed (item
    // ability 82) or major-cursed (83) below the caster's level — bypassing the normal
    // can't-remove-cursed rule — moving each to inventory and reversing its equip abilities. Returns
    // the count removed.
    private int ApplyRemoveCurseEffect(Player target)
    {
        int casterLevel = _player.Level;
        int removed = 0;
        foreach (var slot in target.Equipment.Keys.ToList())
        {
            if (!target.Equipment.TryGetValue(slot, out var itemId) || !_world.Database.Items.TryGetValue(itemId, out var item))
                continue;

            int majorCurse = item.Abilities.GetValueOrDefault(ItemMajorCurseAbilityId);
            bool shouldRemove = item.Abilities.ContainsKey(ItemCursedAbilityId) || (majorCurse > 0 && majorCurse < casterLevel);
            if (!shouldRemove)
                continue;

            long instanceId = GetEquipmentInstanceId(target, slot, itemId);
            target.Equipment.Remove(slot);
            target.EquipmentInstanceIds.Remove(slot);
            AddItemToInventory(target, itemId, instanceId);
            ApplyEquipInstantAbilities(target, item, equipping: false);
            removed++;
        }

        if (removed > 0)
            target.RecalculateEquipment(_world.Database);
        return removed;
    }

    // Ability 85 (shatter weapon): destroy the target's wielded weapon if its hardness (item
    // ability 86) is below the rolled magnitude. In a combat arena the weapon is spared (a warning is
    // shown). Returns true if a weapon was targeted (shattered or arena-spared).
    private async Task<bool> ApplyShatterWeaponEffectAsync(GameSpell spell, Player target)
    {
        if (!target.Equipment.TryGetValue("weapon", out var weaponId) || !_world.Database.Items.TryGetValue(weaponId, out var weapon))
            return false;

        int magnitude = RollSpellLevelScaledAmount(spell);
        if (weapon.Abilities.GetValueOrDefault(ItemHardnessAbilityId) >= magnitude)
            return false;   // too hard to shatter

        var room = _world.GetRoom(target.CurrentMapNumber, target.CurrentRoomNumber);
        if (room != null && room.RoomType == ArenaRoomType)
        {
            await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellHostile($"If this wasn't a combat arena, your {weapon.Name} would have been shattered!")]);
            return true;
        }

        long instanceId = GetEquipmentInstanceId(target, "weapon", weaponId);
        target.Equipment.Remove("weapon");
        target.EquipmentInstanceIds.Remove("weapon");
        ApplyEquipInstantAbilities(target, weapon, equipping: false);
        _world.RemoveItemRuntimeState(instanceId);
        target.RecalculateEquipment(_world.Database);

        await _client.SendLineAsync(GameAnsi.SpellHostile($"You have shattered {target.Name}'s {weapon.Name}"));
        await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellHostile($"Your {weapon.Name} has been shattered!")]);
        return true;
    }

    // A "cast on ending" CARRIER: ability 151 on a spell with NO harm ability of its own. Unlike
    // the chain above (a HARM spell whose ability 151 names an explicit follow-up cast AFTER its damage), a
    // carrier deals NO direct damage — its MinBase..MaxBase is a SPELL-ID POOL and it casts ONE rolled
    // spell from that pool (or ability 151's explicit value) at the target, gated by the ability-164 probability
    // (default 100). This is the "random damage" family: #988 "evil random" → one of 984-987, #964
    // "random dmg" → one of 977-981. Returns true when `spell` IS such a carrier (so the caller SKIPS the
    // damage path); `rolledSpell` is the spell to cast, or null when the probability roll failed or the
    // pool held no valid id. Mirrors the monster resolver's carrier gate (ResolveMonsterAttackSpell,
    // the no-target carrier case). Without this gate the id pool leaked into the player damage path and
    // landed as HP loss carrying the carrier's own name — "Duhh drains the evil random of giant roc for
    // 984 damage!" when the Soulwaste weapon (#1624) procced #988.
    private bool TryResolveCarrierSpell(GameSpell spell, out GameSpell? rolledSpell)
    {
        rolledSpell = null;
        if (HasHarmAbility(spell) || !spell.Abilities.TryGetValue(ChainSpellAbilityId, out int carrierCastId))
            return false;

        int rolledId = carrierCastId > 0
            ? carrierCastId
            : (spell.MaxBase > spell.MinBase ? Random.Shared.Next(spell.MinBase, spell.MaxBase + 1) : spell.MinBase);
        if (rolledId <= 0 || !_world.Database.Spells.TryGetValue(rolledId, out var sub))
            return true; // it IS a carrier (skip the damage path) but has nothing valid to cast

        int chance = spell.Abilities.TryGetValue(ChainProbabilityAbilityId, out int prob) && prob > 0 ? prob : 100;
        if (Random.Shared.Next(0, 100) < chance)
            rolledSpell = sub;
        return true;
    }

    private async Task TryChainOffensiveCastAsync(GameSpell spell, MonsterInstance? monster, Player? playerTarget)
    {
        if (_chainDepth >= MaxChainDepth)
            return;
        if (!spell.Abilities.TryGetValue(ChainSpellAbilityId, out var chainSpellId) || chainSpellId <= 0)
            return;
        if (!spell.Abilities.TryGetValue(ChainProbabilityAbilityId, out var chainProbability) || chainProbability <= 0)
            return;
        if (Random.Shared.Next(0, 100) >= chainProbability)
            return;
        if (!_world.Database.Spells.TryGetValue(chainSpellId, out var chainSpell) || _player.CurrentMana < chainSpell.ManaCost)
            return;

        _player.CurrentMana -= chainSpell.ManaCost;
        _chainDepth++;
        try
        {
            if (monster != null && !monster.IsDead)
                await ExecuteOffensiveSpellAgainstMonsterAsync(chainSpell, monster, keepAutoCombatSpellSelected: false);
            else if (playerTarget != null && playerTarget.CurrentHP > Player.DeathHP)
                await ExecuteOffensiveSpellAgainstPlayerAsync(chainSpell, playerTarget, keepAutoCombatSpellSelected: false);
        }
        finally
        {
            _chainDepth--;
        }
    }

    // Stat-modifier abilities the monster's combat aggregation reads — a duration spell carrying any
    // of these leaves a lingering debuff slot. Mirrors the ids used by
    // GetEffectiveMonster* in CombatEngine plus haste/slow and movement-slow.
    private static readonly int[] LingeringMonsterDebuffAbilities = [2, 4, 7, 22, 34, 36, 68, 87];
    private static bool SpellHasLingeringMonsterDebuff(GameSpell spell)
        => Array.Exists(LingeringMonsterDebuffAbilities, spell.Abilities.ContainsKey);

    // Over-time abilities applied to a monster each medium tick by ProcessMonsterSpellUpkeep, mirroring
    // the monster spell-upkeep cases: 1 disease + 8 drain (HP -= magnitude),
    // 18 heal-over-time (HP += magnitude), 11 energy regen, 20 anti-poison drain. A duration spell
    // carrying any of these needs a timed slot so the effect ticks. (Poison 19 is handled separately
    // by PoisonLevel/TickPoison on the SLOW pass — not listed here, matching the cadence split.)
    private static readonly int[] MonsterUpkeepEffectAbilities = [1, 8, 18, 11, 20];
    private static bool SpellHasMonsterUpkeepEffect(GameSpell spell)
        => Array.Exists(MonsterUpkeepEffectAbilities, spell.Abilities.ContainsKey);

    private async Task<bool> TryExecutePendingCombatSpellRoundAsync()
    {
        if (_pendingCombatSpellId <= 0)
            return false;

        // The round action for a queued spell is simply to load the spell by
        // id and re-run the cast. There is NO re-validation of the spell's damage columns
        // each round — the
        // only way the channel ends is the spell id failing to load or the cast itself bailing out.
        // So the gate here must be the SAME predicate that queued the cast (IsOffensiveSpell +
        // EnergyCost > 0, see HandleSpellCastAsync/TryBeginOrRefreshMonsterCombatSpellAsync), not a
        // second, stricter one.
        //
        // Bugs #205/#206 (`esto` engages, then "*Combat Off*" a few seconds later with nothing cast):
        // the old gate was IsCombatSpell, i.e. raw `MinBase > 0 && AttType > 0`. Both halves are false
        // for real player combat spells:
        //   - MinBase/MaxBase are the BASE band BEFORE per-level scaling, so a high-tier spell whose
        //     damage comes from growth has a NEGATIVE base — eldritch storm #1058 is MinBase -30 /
        //     MinInc 4 / MaxInc 10 (a 130..400 band at L40). The MaxBase half of this trap was already
        //     fixed once (see IsCombatSpell's comment); MinBase had the identical flaw.
        //   - AttType 0 is COLD (SpellResistanceMath.GetElementResistAbilityId: 0 → resist-cold #3),
        //     not "no element", so `AttType > 0` excluded every cold spell — frost jet #5, blizzard
        //     #771, frozen spike #1003, glacial blades #1311.
        // 29 player-castable combat spells failed the gate, so the queued round cleared itself on the
        // first pulse: an area spell never cast at all (housekeeping then printed "*Combat Off*"), and
        // a single-target one silently reverted to melee after the opening cast.
        if (!_world.Database.Spells.TryGetValue(_pendingCombatSpellId, out var spell)
            || !IsOffensiveSpell(spell)
            || spell.EnergyCost <= 0)
        {
            ClearPendingCombatSpellSelection();
            return false;
        }

        if (_player.CurrentMana < spell.ManaCost)
        {
            ClearPendingCombatSpellSelection();
            await _client.SendLineAsync(InsufficientMagicResourceMessage());
            return false;
        }

        // Area attack (Targets=12): re-hit everyone in the room this round. Ends when no target remains.
        if (spell.Targets == AreaEnemyTargetType)
            return await ResolveAreaSpellRoundAsync(spell);

        // The "swarm" multi-projectile path: a single-target DAMAGE spell
        // fires up to 20 projectiles in the combat round, one per affordable EnergyCost from the
        // stamina pool. The first is fired by ExecuteOffensiveSpellAgainstMonsterAsync itself, so the
        // budget here is the EXTRA projectiles (≤19). Only damage spells (MinBase/MaxBase > 0) swarm;
        // pure debuff/utility combat spells fire once.
        bool isDamageSpell = spell.MinBase > 0 || spell.MaxBase > 0;
        int extraProjectiles = isDamageSpell ? MaxSpellProjectilesPerRound - 1 : 0;

        if (!await HandleOffensiveSpellCastAsync(
                spell,
                target: string.Empty,
                allowImplicitCombatTarget: true,
                keepAutoCombatSpellSelected: true,
                reportMissingTarget: false,
                extraProjectileBudget: extraProjectiles))
        {
            ClearPendingCombatSpellSelection();
            return false;
        }

        return true;
    }

    // ===== Area cast path (Targets=12 "Full Attack Area", e.g. chaos storm #140) =====
    // The cast hits every eligible target in the room — every attackable monster (excluding the caster's
    // own pets) plus every non-party player the caster may PvP — and, for a combat-round spell
    // (EnergyCost>0), repeats each round draining mana until OOM or the room is clear. Eligibility mirrors
    // the eligible-target count; per-target damage reuses the single-target roll/elemental/MR path.
    // The opening cast out of combat only ENGAGES ("moves to attack everyone in
    // the room!"); damage lands each subsequent round, like the single-target combat-spell open/repeat.

    private async Task HandleAreaOffensiveSpellCastAsync(GameSpell spell)
    {
        if (IsInProtectedRoom())
        {
            await _client.SendLineAsync(GameAnsi.SpellFailure("You can not attack here!"));
            return;
        }

        if (!CanInitiateHostileAction(out var hostileFailure))
        {
            await _client.SendLineAsync(hostileFailure);
            return;
        }

        var monsters = GetEligibleAreaMonsterTargets();
        if (monsters.Count == 0 && GetEligibleAreaPlayerTargets().Count == 0)
        {
            // The stock string ends in "!", not ".".
            await _client.SendLineAsync(GameAnsi.SpellFailure("Your spell has no effect in this room!"));
            return;
        }

        // A SINGLE room-level evil gate. If the sweep would
        // strike an innocent (align 0/4, EP cost > 0) and the caster is lawful or has evil warnings on,
        // refuse the whole cast once — never silently spare the innocent and blast everyone else.
        // EP-cap action block (stock): a maxed-evil caster can't blast a room that contains an innocent.
        // Cap-independent of the gain (which is already refused past the cap), like the melee gate.
        if (_world.EvilCapBlocksActions && _player.EvilPoints > Player.EvilPointGainCap
            && monsters.Any(monster => IsInnocentMonsterAlign(monster.Template.Align)))
        {
            await _client.SendLineAsync(EvilCapBlockMessage);
            return;
        }

        float worstInnocentEp = monsters
            .Select(monster => CombatEngine.GetEPCostForMonsterAttack(_player, monster.Template))
            .DefaultIfEmpty(0f)
            .Max();
        if (worstInnocentEp > 0 && !CanCommitLawfulEvilAction(worstInnocentEp, out var evilWarning))
        {
            await _client.SendLineAsync(evilWarning);
            return;
        }

        bool usesCombatRound = spell.EnergyCost > 0;

        // A combat-round AREA spell (EnergyCost>0, e.g. chaos storm #140) is QUEUED
        // for the round timer, NEVER resolved at cast time. The damage lands on the pulse via
        // ResolveOwnAttackPhaseAsync's pending-area-spell branch → TryExecutePendingCombatSpellRoundAsync
        // → ResolveAreaSpellRoundAsync, exactly like the selected single-target combat spell. This is the
        // single-target contract (TryBeginOrRefreshMonsterCombatSpellAsync) applied to the whole room:
        //   - opening cast (not yet engaged): engage and announce; first damage lands next round.
        //   - re-cast while already fighting (meleeing or already channeling the area spell): a
        //     stop/restart re-engage — *Combat Off* then *Combat Engaged* — that keeps the SAME round
        //     timer (EngageAreaCombat preserves a future NextMonsterAttackAtUtc) and changes nothing about
        //     the queued cast: no extra mana, no immediate damage.
        // Bug #198: the already-in-combat case fell straight into ResolveAreaSpellRoundAsync and dealt a
        // full round of area damage the instant you typed the cast, so an AOE mid-fight read as an instant
        // nuke instead of a timed round action.
        if (usesCombatRound)
        {
            if (_player.InCombat)
                await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));

            EngageAreaCombat(spell);
            await _client.SendLineAsync(GameAnsi.CombatEngaged("*Combat Engaged*"));
            BroadcastCombatMessagesToRoom([GameAnsi.CombatEngaged($"{_player.Name} moves to attack everyone in the room!")]);
            return;
        }

        // Instant area spell (EnergyCost==0, e.g. a room-wide DoT/debuff): a one-off resolved now, no
        // round channel — the melee/other combat loop (if any) continues unchanged.
        await ResolveAreaSpellRoundAsync(spell);
    }

    // One round of an area attack: spend mana, hit every eligible target once, then keep the spell
    // selected if anyone remains (so it repeats), else end the channel. Returns true while it continues.
    private async Task<bool> ResolveAreaSpellRoundAsync(GameSpell spell)
    {
        if (IsInProtectedRoom())
        {
            ClearPendingCombatSpellSelection();
            return false;
        }

        bool usesCombatRound = spell.EnergyCost > 0;

        _player.CurrentMana -= spell.ManaCost;
        SpendSpellCast(spell);
        if (usesCombatRound)
            _player.InCombat = true;

        var monsters = GetEligibleAreaMonsterTargets();

        // Sweeping an area attack across innocents (align 0/4
        // townsfolk/guards) is an evil act. Charge each newly-engaged innocent's evil points once (the
        // HasEngagedPlayer gate makes it once-per-engagement, like the single-target EngageCombatAsync),
        // summed into ONE "dark cloud" so a sweep over several innocents doesn't repeat the line. Marking
        // every swept monster engaged also makes them retaliate/persist as a struck target would.
        float innocentEvilPoints = 0f;
        foreach (var monster in monsters)
        {
            if (monster.HasEngagedPlayer(_player.Name))
                continue;
            innocentEvilPoints += CombatEngine.GetEPCostForMonsterAttack(_player, monster.Template);
            monster.MarkPlayerEngaged(_player.Name);
        }
        if (innocentEvilPoints > 0)
            await AddEvilPointsWithCloudAsync(innocentEvilPoints);

        // The told-flags are reset once at area-cast entry: the caster Line1
        // and room Line3 of an area cast print ONCE per cast, however many
        // targets the sweep hits; only the per-victim Line2 repeats. Bug #190: emitting them inside the
        // per-target loop spammed "You cast poison cloud on the room!" once per monster.
        var toldFlags = new SpellCastToldFlags();

        foreach (var monster in monsters)
        {
            if (monster.IsDead)
                continue;
            await ApplyAreaSpellToMonsterAsync(spell, monster, toldFlags: toldFlags);
        }

        // Re-enumerate players every round (the area cast re-runs each round), so a
        // walk-in IS struck — and clouded: ApplyAreaSpellToPlayerAsync charges the evil point/dark cloud
        // first, then damages, in one call, so a hit never lands without the matching evil charge.
        foreach (var target in GetEligibleAreaPlayerTargets())
        {
            if (target.CurrentHP <= Player.DeathHP)
                continue;
            await ApplyAreaSpellToPlayerAsync(spell, target, toldFlags);
        }

        // An instant area spell (EnergyCost==0) is a one-off; only a combat-round spell repeats.
        bool targetsRemain = GetEligibleAreaMonsterTargets().Count > 0 || GetEligibleAreaPlayerTargets().Count > 0;
        if (targetsRemain && usesCombatRound)
        {
            SetPendingCombatSpellSelection(spell.Number);
            return true;
        }

        // Nothing left to hit — drop the channel; housekeeping emits "*Combat Off*" next beat.
        ClearPendingCombatSpellSelection();
        _player.CombatTarget = null;
        _player.PlayerCombatTarget = null;
        return false;
    }

    private void EngageAreaCombat(GameSpell spell)
    {
        DateTime now = DateTime.UtcNow;
        DateTime scheduledRound = _player.NextMonsterAttackAtUtc > now
            ? _player.NextMonsterAttackAtUtc
            : _world.GetNextCombatPulseUtc(now);

        _player.InCombat = true;
        _player.CombatTarget = null;        // area attack has no single locked target
        _player.PlayerCombatTarget = null;
        _player.PendingCombatRoundAction = PlayerCombatRoundAction.Attack;
        _player.NextMonsterAttackAtUtc = scheduledRound;
        _player.NextAttackAllowedAtUtc = scheduledRound;
        _player.IsResting = false;
        SetPendingCombatSpellSelection(spell.Number);
    }

    // Eligible monster targets: every living non-pet monster in the room. Innocents (align 0/4) ARE
    // included here so the room-level evil gate in HandleAreaOffensiveSpellCastAsync can warn/refuse on
    // them (the lawful/evil-warning check is once-per-cast, not a silent per-target skip). The afraid
    // gate (CanInitiateHostileAction) is applied once by the caller.
    private List<MonsterInstance> GetEligibleAreaMonsterTargets()
        => _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead && !monster.IsOwnedBy(_player.Name))
            .ToList();

    // Eligible player targets: every other living player in the room who is NOT in the caster's party
    // (party members are excluded) and whom the caster may legally attack (PvP-level range, alignment).
    // Hidden players are NOT specially excluded here — the target count marks and hits them.
    // There is no target-side "hidden shield" and no protection for a low-evil target. The only evil
    // gate is ATTACKER-side: if the cast would be an evil act (striking an innocent), the caster's own
    // evil-warning/lawful flag refuses the whole cast ("turn off your evil warnings"), handled once in
    // HandleAreaOffensiveSpellCastAsync via CanCommitLawfulEvilAction.
    private List<Player> GetEligibleAreaPlayerTargets()
    {
        string? casterLeader = _world.GetPartyLeaderName(_player.Name);
        return _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            .Where(other => other.CurrentHP > Player.DeathHP
                && !IsSamePartyAsCaster(other, casterLeader)
                && CanAttackPlayer(other, out _))
            .ToList();
    }

    private bool IsSamePartyAsCaster(Player other, string? casterLeader)
    {
        if (string.IsNullOrEmpty(casterLeader))
            return false;   // a solo caster has no party members to spare

        string? otherLeader = _world.GetPartyLeaderName(other.Name);
        return string.Equals(otherLeader, casterLeader, StringComparison.OrdinalIgnoreCase);
    }

    // True when the player is currently channeling an area combat spell with no single target — used by
    // the combat round gate (which otherwise requires a CombatTarget/PlayerCombatTarget to fire).
    private bool HasPendingAreaCombatSpell()
        => _player.PendingCombatSpellId > 0
           && _world.Database.Spells.TryGetValue(_player.PendingCombatSpellId, out var pending)
           && pending.Targets == AreaEnemyTargetType;

    // Spell-told flags: one instance per area cast latches the caster
    // Line1 and room Line3 announcements to a single emission across the whole target sweep. A null
    // flags argument (single-target casts, forced quest casts) always announces — one target, one call.
    private sealed class SpellCastToldFlags
    {
        public bool CasterTold;
        public bool RoomTold;
    }

    private async Task ApplyAreaSpellToMonsterAsync(GameSpell spell, MonsterInstance monster, bool bypassMonsterSpellDefenses = false, SpellCastToldFlags? toldFlags = null)
    {
        // A quest-scripted `cast` is a FORCED effect: it must land regardless of the target's spell
        // defenses, because the quest depends on the outcome. The stock "kill enigma" (#1234, ReqLevel 0)
        // drops the talker Enigma Lord (#755), which otherwise carries Spell Immunity 30 specifically so a
        // player can NOT just blast it with ordinary spells before doing the quest. Normal player area
        // casts keep all three gates.
        if (!bypassMonsterSpellDefenses)
        {
            if (!CanSpellAffectMonsterTarget(spell, monster.Template)
                || !CanSpellSatisfyRequiredToHit(spell, monster.Template))
            {
                return;
            }

            int immunityLevel = monster.Template.Abilities.GetValueOrDefault(SpellImmunityAbilityId);
            if (immunityLevel > 0 && spell.ReqLevel <= immunityLevel)
                return;
        }

        // An area duration-harm spell (ability 1/8 + Duration) is a
        // DoT on each target — a timed slot with the spell's Duration and a resisted
        // magnitude (((100-resist)*mag)/100), NOT instant area damage. Store the slot and return; the
        // monster medium-tick upkeep drains it. dur==0 area spells fall through to the instant sweep below.
        // (The per-target apply/wear-off lines are added in the message pass; see reference_dot_message_model.)
        //
        // EXCEPTION: a FORCED quest cast (bypassMonsterSpellDefenses) must land its whole effect NOW — the
        // quest depends on the synchronous outcome, not a tick-delayed drain. Evil quest stage 18's
        // "kill enigma" (#1234) carries Duration 2 + damage (ability 1), which would otherwise be a DoT; routing
        // it through the DoT branch left the 3-HP talker (#755) alive past `ask Enigma retrieve`, so its
        // DeathSpell never summoned the boss (#921) and the quest stalled. Forced casts apply instantly.
        if (!bypassMonsterSpellDefenses && IsDurationDamageSpell(spell))
        {
            int dotMagnitude = RollSpellMagnitude(spell, _player.Level);
            dotMagnitude = ApplyElementalSpellDamage(spell, dotMagnitude, abil => monster.GetEffectiveAbility(abil));
            dotMagnitude = ApplyOffensiveMagicResistance(spell, dotMagnitude, monster.EffectiveMagicResist, targetHasAntiMagic: false);
            int dotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            monster.AddOrRefreshActiveSpell(_world.Database, spell.Number, dotMagnitude, dotDuration);
            return;
        }

        // "Can Not be Fully-Resisted": area spells skip the all-or-nothing resist roll and apply only the
        // partial magic-resistance reduction below.
        // Magnitude band (ComputeSpellMagnitudeBand): scales both min and max by effLvl =
        // min(casterLevel, Cap), honouring the spell's level cap — the SAME roll the single-target path
        // uses. Was an uncapped MinBase..MaxBase + (level-ReqLevel)*MaxInc roll that ignored Cap and the
        // min side, so a level-capped area spell over-scaled past its cap at high level.
        int damage = RollSpellMagnitude(spell, _player.Level);

        damage = ApplyElementalSpellDamage(spell, damage, abil => monster.GetEffectiveAbility(abil));
        damage = ApplyOffensiveMagicResistance(spell, damage, monster.EffectiveMagicResist, targetHasAntiMagic: false);

        monster.CurrentHP -= damage;
        ApplyDrainToCaster(spell, damage);
        await SendMonsterDamageSpellMessagesAsync(spell, monster, damage, toldFlags);

        if (monster.IsDead)
        {
            // Area sweep: the caster keeps channelling across kills — HandleMonsterDeath now ends only
            // the fights of players locked onto THIS monster, and an area channel holds no single target,
            // so no per-kill "*Combat Off*" fires. The single closing one comes from housekeeping.
            await HandleMonsterDeath(monster, CombatEngine.CreateMonsterDeathResult(monster));
            return;
        }

        if (SpellIsKnockdown(spell))
            ApplyKnockdownSpellToMonster(spell, monster, _player.Level);

        if (spell.Duration > 0 && SpellHasLingeringMonsterDebuff(spell))
        {
            int debuffMagnitude = RollSpellLevelScaledAmount(spell);
            int slotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            monster.AddOrRefreshActiveSpell(_world.Database, spell.Number, debuffMagnitude, slotDuration);
        }
    }

    private async Task ApplyAreaSpellToPlayerAsync(GameSpell spell, Player target, SpellCastToldFlags? toldFlags = null)
    {
        if (!CanSpellAffectPlayerTarget(spell, target))
            return;

        bool targetHasAntiMagic = target.HasActiveAbility(_world.Database, ClassAntiMagicAbilityId);

        // Evil points for opening PvP aggression are charged once, on the first round that engages a
        // given victim (mirrors EngagePvpCombatAsync / the single-target spell path).
        await ApplyPvpAggressionEvilAsync(target);

        // An area duration-harm spell is a DoT on each target player
        // (a timed slot with the spell's Duration + resisted magnitude), NOT instant area
        // damage. Store the slot, recalc so any secondary stat debuff applies, and return; the target's
        // ProcessActiveSpellUpkeep drains it per medium tick. dur==0 area spells fall through to instant.
        if (IsDurationDamageSpell(spell))
        {
            int dotMagnitude = RollSpellMagnitude(spell, _player.Level);
            dotMagnitude = ApplyElementalSpellDamage(spell, dotMagnitude, abil => target.GetActiveAbilityValue(_world.Database, abil));
            dotMagnitude = ApplyOffensiveMagicResistance(spell, dotMagnitude, target.MagicResist, targetHasAntiMagic);
            int dotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            if (target.AddOrRefreshActiveSpell(spell.Number, dotMagnitude, dotDuration))
                _world.RecalculatePlayerStats(target);
            // Each target sees the ability-115 Line3 status line on application.
            string? areaOngoing = ResolveSpellOngoingStatusLine(spell, target.Name);
            if (areaOngoing != null)
                await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellHostile(areaOngoing)]);
            return;
        }

        // Area spells cannot be fully resisted — partial MR only (no all-or-nothing resist roll).
        // Capped band (ComputeSpellMagnitudeBand), same as every other cast path — replaces the
        // uncapped MinBase..MaxBase + (level-ReqLevel)*MaxInc roll that ignored Cap and the min side.
        int damage = RollSpellMagnitude(spell, _player.Level);

        damage = ApplyElementalSpellDamage(spell, damage, abil => target.GetActiveAbilityValue(_world.Database, abil));
        damage = ApplyOffensiveMagicResistance(spell, damage, target.MagicResist, targetHasAntiMagic);

        target.CurrentHP -= damage;
        ApplyDrainToCaster(spell, damage);
        await SendPlayerDamageSpellMessagesAsync(spell, target, damage, toldFlags);

        if (target.CurrentHP <= 0)
        {
            var deaths = new List<string>();
            if (target.CurrentHP > Player.DeathHP)
                deaths.Add(GameAnsi.DropsToTheGround($"{target.Name} drops to the ground!"));
            if (target.CurrentHP <= Player.DeathHP)
                deaths.Add(GameAnsi.KilledOutright("You have been killed!"));
            await SendCombatMessagesToPlayerAsync(target, deaths);

            var targetSession = _world.GetClientForPlayer(target.Name)?.Session;
            if (targetSession != null)
                await targetSession.HandleExternalPlayerDeathAsync();
            else
                target.ClearCombatState();

            return;
        }

        if (SpellIsKnockdown(spell))
            ApplyKnockdownSpellToPlayer(spell, target, _player.Level);
    }

    private async Task SendSpellStartMessagesAsync(GameSpell spell, string targetDisplayName, Player? playerTarget)
    {
        if (!spell.Abilities.TryGetValue(SpellStartMessageAbilityId, out int messageId)
            || !_world.Database.Messages.TryGetValue(messageId, out var message))
        {
            return;
        }

        string casterLine = FormatLegacyMessage(message.Line1, targetDisplayName, spell.Name);
        if (!string.IsNullOrWhiteSpace(casterLine))
            await _client.SendLineAsync(GameAnsi.SpellHostile(casterLine));

        if (playerTarget != null)
        {
            string targetLine = FormatLegacyMessage(message.Line2, _player.Name, spell.Name, targetDisplayName);
            if (!string.IsNullOrWhiteSpace(targetLine))
                await SendCombatMessagesToPlayerAsync(playerTarget, [GameAnsi.SpellHostile(targetLine)]);

            string observerLine = FormatLegacyMessage(message.Line3, _player.Name, targetDisplayName, spell.Name);
            if (!string.IsNullOrWhiteSpace(observerLine))
                await BroadcastCombatMessagesToObserversAsync([GameAnsi.SpellHostile(observerLine)], playerTarget);

            return;
        }

        string roomLine = FormatLegacyMessage(message.Line3, _player.Name, targetDisplayName, spell.Name);
        if (!string.IsNullOrWhiteSpace(roomLine))
            BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile(roomLine)]);
    }

    // The caster Line1 is gated on its told-flag and the
    // room/observer Line3 on its own — each prints once per cast; the victim Line2 has no flag and
    // repeats per victim. toldFlags carries those latches across an area sweep; null = always announce.
    private bool ShouldTellCasterLine(SpellCastToldFlags? toldFlags)
    {
        if (toldFlags == null)
            return true;
        if (toldFlags.CasterTold)
            return false;
        toldFlags.CasterTold = true;
        return true;
    }

    private bool ShouldTellRoomLine(SpellCastToldFlags? toldFlags)
    {
        if (toldFlags == null)
            return true;
        if (toldFlags.RoomTold)
            return false;
        toldFlags.RoomTold = true;
        return true;
    }

    private async Task SendPlayerDamageSpellMessagesAsync(GameSpell spell, Player target, int damage, SpellCastToldFlags? toldFlags = null)
    {
        if (CanUseSingleTargetDamageCastMessage(spell)
            && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
        {
            string damageText = damage.ToString(CultureInfo.InvariantCulture);
            var (casterLine, targetLine, observerLine) = FormatSingleTargetDamageCastMessage(
                spell,
                message,
                casterDisplayName: _player.Name,
                targetDisplayName: target.Name,
                damage,
                damageText);

            if (!string.IsNullOrWhiteSpace(casterLine) && ShouldTellCasterLine(toldFlags))
                await _client.SendLineAsync(GameAnsi.SpellHostile(casterLine));
            if (!string.IsNullOrWhiteSpace(targetLine))
                await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellHostile(targetLine)]);
            if (!string.IsNullOrWhiteSpace(observerLine) && ShouldTellRoomLine(toldFlags))
                await BroadcastCombatMessagesToObserversAsync([GameAnsi.SpellHostile(observerLine)], target);
            return;
        }

        if (ShouldTellCasterLine(toldFlags))
            await _client.SendLineAsync(GameAnsi.SpellHostile($"Your {spell.Name} hits {target.Name} for {damage} damage!"));
        await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellHostile($"{_player.Name}'s {spell.Name} hits you for {damage} damage!")]);
        if (ShouldTellRoomLine(toldFlags))
            await BroadcastCombatMessagesToObserversAsync([GameAnsi.SpellHostile($"{_player.Name}'s {spell.Name} hits {target.Name} for {damage} damage!")], target);
    }

    private async Task SendMonsterDamageSpellMessagesAsync(GameSpell spell, MonsterInstance target, int damage, SpellCastToldFlags? toldFlags = null)
    {
        if (CanUseSingleTargetDamageCastMessage(spell)
            && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
        {
            string damageText = damage.ToString(CultureInfo.InvariantCulture);
            var (casterLine, _, roomLine) = FormatSingleTargetDamageCastMessage(
                spell,
                message,
                casterDisplayName: _player.Name,
                targetDisplayName: target.DisplayName,
                damage,
                damageText);

            if (!string.IsNullOrWhiteSpace(casterLine) && ShouldTellCasterLine(toldFlags))
                await _client.SendLineAsync(GameAnsi.SpellHostile(casterLine));
            if (!string.IsNullOrWhiteSpace(roomLine) && ShouldTellRoomLine(toldFlags))
                BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile(roomLine)]);
            return;
        }

        if (ShouldTellCasterLine(toldFlags))
            await _client.SendLineAsync(GameAnsi.SpellHostile($"Your {spell.Name} hits {target.DisplayName} for {damage} damage!"));
        if (ShouldTellRoomLine(toldFlags))
            BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile($"{_player.Name}'s {spell.Name} hits {target.DisplayName} for {damage} damage!")]);
    }

    public async Task<bool> TryExecuteMonsterMidSpellAsync(MonsterInstance attacker)
    {
        if (attacker.IsDead
            || attacker.MapNumber != _player.CurrentMapNumber
            || attacker.RoomNumber != _player.CurrentRoomNumber)
        {
            return false;
        }

        if (!TrySelectMonsterMidSpell(attacker.Template.MidSpells, out var monsterSpell)
            || !_world.Database.Spells.TryGetValue(monsterSpell.SpellId, out var spell))
        {
            return false;
        }

        string attackerDisplayName = GetMonsterCastDisplayName(attacker);

        // A triggered monster cast is FREE and is never gated on the caster's stamina. The monster
        // cast takes the attack-SLOT index and derives the cost from it:
        //     if (slot == -1) cost = 0;                            // triggered cast — no cost
        //     else            cost = the slot's AtkEng
        //     ...
        //     if (cost <= monster.energy) { ...cast... }            // the gate, trivially true at 0
        // The mid-spell picker and the CreateSpell/DeathSpell dispatcher
        // both call in with slot -1, so only an AtkType-2 ATTACK-slot cast costs anything — and it costs
        // that slot's AtkEng, which our attack loop already deducts (GetMonsterAttackEnergyCost).
        //
        // We used to charge GameSpell.EnergyCost here and refuse the cast when the pool was short. That
        // field is the PLAYER's stamina price for the spell (magic missile: 1000) and has no meaning on
        // the monster side. It went unnoticed only because the mid-spell used to run at the top of the
        // round, on a pool PrepareCombatRound had just refilled; casting at the stock point — after the
        // swings — a giant rat (1000 pool, one 1000-EU swing) could never again afford a 1000-cost spell,
        // and every 100%-chance mid-spell in the game would have gone silent on the rounds it swung.

        // Self-buff: a beneficial duration spell the monster casts on ITSELF (self/ally
        // target types 1/2 ⇒ a timed slot on itself). Covers the KAI "ways" (way of the tiger #38 ⇒
        // "You feel ferocious!"), armour spells, and self-haste (speed/frenzy). Previously only self-haste
        // (Targets 2 + ability 87) was handled here, so Targets-1 ways leaked into the player-debuff branch and
        // buffed the PLAYER instead — "tall dark monk invokes the way of the tiger on you!" (bug #62). The
        // monster's active-spell aggregation (RecomputeBuffStats/GetEffectiveAbility) applies every ability
        // the buff carries — the haste EU scaler (e.g. "speed" Min/Max 85 ⇒ EU×0.85), AC, dodge, damage.
        if (MonsterSpellTargetsSelf(spell) && spell.Duration > 0 && spell.Abilities.Count > 0)
        {
            int buffMagnitude = RollMonsterSpellMagnitude(spell, monsterSpell.Level);
            int buffDuration = RollMonsterCastSpellDuration(spell, monsterSpell.Level);

            attacker.AddOrRefreshActiveSpell(_world.Database, spell.Number, buffMagnitude, buffDuration);

            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name}.")]);
            BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name}.")]);
            return true;
        }

        // Monster summon (ability 12): the monster calls reinforcements — spawn ONE
        // monster per summon slot (see ResolveSummonTemplateIds) as a pet owned by the caster and set it
        // on the player so it joins the fight. TrySummonReinforcement carries the child-list cap (10)
        // and the alignment-AI aggro gate, and is re-evaluated per summon so a multi-slot spell cannot
        // overshoot the cap. Checked before the debuff branch so a duration summon spell isn't mistaken
        // for a player debuff.
        var summonTemplateIds = ResolveSummonTemplateIds(spell);
        if (summonTemplateIds.Count > 0)
        {
            foreach (int summonTemplateId in summonTemplateIds)
                TrySummonReinforcement(attacker, summonTemplateId);

            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name}.")]);
            BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name}.")]);
            return true;
        }

        // Area combat cast: a spell whose Targets is an
        // area category (every stock monster area spell is Targets 12 "Full Attack Area" — chaos storm,
        // ice storm, paralyze, terror, ...) lands on EVERY valid player in the room, not just the engaged
        // one. Spend the energy once (stock deducts after the count gate, but a no-target combat cast is
        // an extreme edge — the engaged player is always present here), then fan the cast out across the
        // room. Checked before the single-target debuff/damage branches so an area spell isn't collapsed
        // onto _player alone.
        if (MonsterSpellIsAreaCombatCast(spell))
        {
            await ResolveMonsterAreaCombatCastAsync(attacker, spell, monsterSpell.Level);
            return true;
        }

        // Monster casts a duration debuff at the player (slow 87, movement-slow 68, ...). The
        // duration spell becomes a timed slot on the
        // player whose abilities then apply via the player's aggregation (e.g. slow stores 125 ⇒
        // EU×1.25). This is the missing counterpart to the player-casts-slow-on-monster branch.
        if (MonsterSpellIsPlayerDebuff(spell))
        {
            if (!CanSpellAffectPlayerTarget(spell, _player))
            {
                await SendCombatMessagesToCurrentPlayerAsync([$"{MudAnsi.White}{attackerDisplayName}'s {spell.Name} has no effect on you!{MudAnsi.Reset}"]);
                return true;
            }

            bool playerResistsDebuff = _player.HasActiveAbility(_world.Database, ClassAntiMagicAbilityId);
            if (IsOffensiveSpellResisted(spell, _player.MagicResist, playerResistsDebuff))
            {
                await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellFailure($"You resisted {attackerDisplayName}'s cast of {spell.Name}.")]);
                return true;
            }

            int debuffMagnitude = RollMonsterSpellMagnitude(spell, monsterSpell.Level);
            int debuffDuration = RollMonsterCastSpellDuration(spell, monsterSpell.Level);

            // The active spell carries the root (ability 74 → IsRooted) and confusion (71) directly;
            // recalc applies them. No separate knockdown state.
            if (_player.AddOrRefreshActiveSpell(spell.Number, debuffMagnitude, debuffDuration))
                _world.RecalculatePlayerStats(_player);

            // A poison spell (ability 19, AttType 0/2/6/8) sets the target's poison level — the per-tick
            // HP drain that also blocks rest/meditate — from the ability's own value (see
            // ApplyMonsterPoisonLevel; bug #140 fix). Poison immunity (ability 21) is BOOLEAN: an immune
            // target (Kang/headdress) is skipped entirely, so it takes no drain and CAN rest; the
            // active-spell slot + ability-115 message above still fire, so it still sees "You feel ill".
            ApplyMonsterPoisonLevel(spell, _player, debuffMagnitude, _world.Database);

            if (spell.CastMessageB > 0 && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var midDebuffMsg))
            {
                // CastMsgB templates already include "The %s" — pass the raw display name.
                string midPlayerLine = FormatLegacyMessage(midDebuffMsg.Line2, attacker.DisplayName, spell.Name, _player.Name);
                string midRoomLine = FormatLegacyMessage(midDebuffMsg.Line3, attacker.DisplayName, spell.Name, _player.Name);
                if (!string.IsNullOrWhiteSpace(midPlayerLine))
                    await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile(midPlayerLine)]);
                if (!string.IsNullOrWhiteSpace(midRoomLine))
                    BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile(midRoomLine)]);
            }
            else if (spell.CastMessageB == 0)
            {
                await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name} on you!")]);
                BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name} on {_player.Name}.")]);
            }
            // else: CastMessageB references a non-existent message (the "66" no-message sentinel) — the
            // spell is deliberately silent (the invisible quest-flag / utility "temp" spells). Show nothing.

            // The target also sees the spell's ability-115 Line3 ongoing
            // status line ("You are on fire!") in addition to the cast line, on every (re)application — so a
            // monster re-casting a DoT each round shows this line each round.
            string? midOngoing = ResolveSpellOngoingStatusLine(spell, _player.Name);
            if (midOngoing != null)
                await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile(midOngoing)]);
            return true;
        }

        if (!CanUseMonsterDirectDamageSpell(spell))
            return false;

        if (!CanSpellAffectPlayerTarget(spell, _player))
        {
            await SendCombatMessagesToCurrentPlayerAsync([$"{MudAnsi.White}{attackerDisplayName}'s {spell.Name} has no effect on you!{MudAnsi.Reset}"]);
            BroadcastCombatMessagesToRoom([$"{MudAnsi.White}Your spell has no effect on {_player.Name}.{MudAnsi.Reset}"]);
            return true;
        }

        int damage = RollMonsterSpellMagnitude(spell, monsterSpell.Level);

        bool playerHasAntiMagic = _player.HasActiveAbility(_world.Database, ClassAntiMagicAbilityId);
        if (IsOffensiveSpellResisted(spell, _player.MagicResist, playerHasAntiMagic))
        {
            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellFailure($"You resisted {attackerDisplayName}'s cast of {spell.Name}.")]);
            return true;
        }

        damage = ApplyOffensiveMagicResistance(spell, damage, _player.MagicResist, playerHasAntiMagic);

        bool wasConscious = _player.CurrentHP > 0;
        _player.CurrentHP -= damage;

        var playerMessages = new List<string>();
        var roomMessages = new List<string>();
        AddMonsterDirectDamageSpellMessages(playerMessages, roomMessages, attacker, spell, damage, _player);

        if (_player.CurrentHP > Player.DeathHP && SpellIsKnockdown(spell))
            ApplyKnockdownSpellToPlayer(spell, _player, monsterSpell.Level, casterIsPlayer: false);

        // Drop line only on the conscious → unconscious-but-alive transition; an outright kill prints
        // the death lines instead (see CombatEngine.AppendPlayerKillMessages).
        if (wasConscious && _player.CurrentHP <= 0 && _player.CurrentHP > Player.DeathHP)
            playerMessages.Add(GameAnsi.DropsToTheGround($"{_player.Name} drops to the ground!"));

        if (_player.CurrentHP <= Player.DeathHP)
            playerMessages.Add(GameAnsi.KilledOutright("You have been killed!"));

        await SendCombatMessagesToCurrentPlayerAsync(playerMessages);
        BroadcastCombatMessagesToRoom(roomMessages);
        return true;
    }

    // Synchronously resolve a monster's AtkType=2 spell attack (e.g. tower of flame 209, chaos storm
    // 212) at level castLevel: applies the spell's damage to the player and returns the (player, room)
    // messages for the combat round to accumulate. Sync + message-accumulating (like the melee path) so
    // CombatEngine's swing loop can interleave it with melee. These are damage spells (single or area
    // target) — a focused offensive cast: roll + level-scale, elemental + magic resistance, then HP.
    // The "drop to the ground" / "killed" lines are added by the swing loop, not here.
    // target defaults to _player (the single-target combat path). For an area cast (DeathSpell hitting a
    // room), pass each room player as target and damageDivisor = the valid-target count: an
    // area cast divides the rolled damage by that count, so a 3-player
    // room splits the blast three ways; duration debuffs apply to all without division (damage is 0).
    // skipFullResist: the area fan-out already rolled the all-or-nothing resist at the count stage and
    // dropped resisters silently at the count stage, so the per-target apply must NOT
    // re-roll it or print "you resisted". Single-target casts keep the roll (and the message).
    // overrideMagnitude: an area cast rolls the spell magnitude ONCE (before the
    // per-target loop) and applies that same base to every target — each differing only by its own resist.
    // The area fan-out rolls once and passes it here so targets share a roll instead of re-rolling each.
    public (List<string> PlayerMessages, List<string> RoomMessages) ResolveMonsterAttackSpell(MonsterInstance attacker, int spellId, int castLevel, Player? target = null, int damageDivisor = 1, bool skipFullResist = false, int? overrideMagnitude = null, int depth = 0)
    {
        target ??= _player;
        var playerMessages = new List<string>();
        var roomMessages = new List<string>();

        if (spellId <= 0 || !_world.Database.Spells.TryGetValue(spellId, out var spell))
            return (playerMessages, roomMessages);

        string attackerDisplayName = GetMonsterCastDisplayName(attacker);

        // Summon (ability 12 — "calls for aid"): the spell spawns reinforcements, it does NOT harm the
        // target. The ability value is the reinforcement's template id. A summon is not aimed at the
        // player, so it runs BEFORE the can-affect / magic-resist gates (the summon path sets no player
        // target). Without this branch the summon fell through to the damage path and rolled
        // MinBase..MaxBase (0..0 for "calls for aid") as HP loss — "short guardsman's calls for aid hits
        // you for 0 damage!" (bug #135). The AtkHitSpell / AtkType=2 paths reach this resolver; the
        // MidSpell, CreateSpell and DeathSpell paths catch summons earlier and never get here.
        var summonTemplateIds = ResolveSummonTemplateIds(spell);
        if (summonTemplateIds.Count > 0)
        {
            foreach (int summonTemplateId in summonTemplateIds)
                TrySummonReinforcement(attacker, summonTemplateId);
            AddMonsterSummonCastMessages(spell, attacker, target, playerMessages, roomMessages);
            return (playerMessages, roomMessages);
        }

        // Past the summon gate every monster spell is aimed AT the player (damage / debuff / DoT / chain),
        // so it breaks rest+meditate just like a melee attack. This is the fix
        // for Bug #172: a resting player dropped by the deranged priest's AoE "hand of death" was left
        // "still resting" while unconscious because no spell path cleared the flag.
        target.BreakRestAndMeditate();

        // "Cast on ending" pure carrier (ability 151 with NO harm ability of its own — the "random
        // damage" family: #964 "random dmg" rolls one of 977-981 rocks/ice/fire/acid/lightning, #988
        // "evil random" one of 984-987). The carrier deals NO direct damage — its MinBase..MaxBase is a
        // SPELL-ID POOL, and ability 151 casts one rolled spell from it on the target (the no-target
        // path re-casts the rolled id, gated by the ability-164 probability, default 100).
        // Without this the id pool (977-987) fell through to the damage path and landed as ~979 HP loss —
        // an instant kill from the ivory golem / Death Mist of Ozrinom / cloaked figure on a melee hit.
        // An ability 151 that ALSO carries a harm ability (dragonfire) is a post-damage chain, not a carrier, and
        // stays on the damage path (its follow-up chain is a separate, non-lethal gap).
        if (depth < MaxMonsterSpellChainDepth
            && spell.Abilities.TryGetValue(ChainSpellAbilityId, out int castOnEndingId)
            && !HasHarmAbility(spell))
        {
            // Spell to cast: ability 151's value if set, else a roll across the MinBase..MaxBase id pool.
            int rolledSpellId = castOnEndingId > 0
                ? castOnEndingId
                : (spell.MaxBase > spell.MinBase ? Random.Shared.Next(spell.MinBase, spell.MaxBase + 1) : spell.MinBase);
            int castChance = spell.Abilities.TryGetValue(ChainProbabilityAbilityId, out int prob) && prob > 0 ? prob : 100;
            if (rolledSpellId > 0 && Random.Shared.Next(0, 100) < castChance)
                return ResolveMonsterAttackSpell(attacker, rolledSpellId, castLevel, target, damageDivisor, skipFullResist, overrideMagnitude: null, depth: depth + 1);
            return (playerMessages, roomMessages);
        }

        // Ability 148 triggers a textblock script, NEVER damage (a monster cast has no ability-148 harm
        // case). A no-harm spell carrying only 148 — the quest "text" temps like #1137 "dark mage text"
        // (MinBase=MaxBase=0) — fell through to the damage path and rolled 0 as HP loss, broadcasting
        // "The Dark Mage's dark mage text hits you for 0 damage!" to every engaged player (the chain gate
        // above recurses the parent DeathSpell temp into this "text" spell, inside the DAMAGE resolver).
        // The textblock itself is delivered on the monster-triggered chain path (CastMonsterTriggeredSpellAsync
        // runs 148 directly), so here the resolver returns silently — no resist roll, no damage line.
        if (!HasHarmAbility(spell) && spell.Abilities.ContainsKey(ScriptedCommandAbilityId))
            return (playerMessages, roomMessages);

        if (!CanSpellAffectPlayerTarget(spell, target))
        {
            playerMessages.Add($"{MudAnsi.White}{attackerDisplayName}'s {spell.Name} has no effect on you!{MudAnsi.Reset}");
            return (playerMessages, roomMessages);
        }

        bool playerHasAntiMagic = target.HasActiveAbility(_world.Database, ClassAntiMagicAbilityId);
        if (!skipFullResist && IsOffensiveSpellResisted(spell, target.MagicResist, playerHasAntiMagic))
        {
            playerMessages.Add(GameAnsi.SpellFailure($"You resisted {attackerDisplayName}'s cast of {spell.Name}."));
            return (playerMessages, roomMessages);
        }

        // Duration spells with abilities are debuffs (blind, slow, curse, etc.), not damage.
        // Apply them as timed status effects on the player, matching the mid-combat spell path.
        if (MonsterSpellIsPlayerDebuff(spell))
        {
            int debuffMagnitude = overrideMagnitude ?? RollMonsterSpellMagnitude(spell, castLevel);
            int debuffDuration = RollMonsterCastSpellDuration(spell, castLevel);

            // The active spell carries the root (ability 74 → IsRooted) and confusion (71) directly;
            // recalc applies them. No separate knockdown state.
            if (target.AddOrRefreshActiveSpell(spell.Number, debuffMagnitude, debuffDuration))
                _world.RecalculatePlayerStats(target);

            // Poison (ability 19, AttType 0/2/6/8) raises the target's poison level from the ability's own
            // value — the per-tick drain + rest/meditate block (bug #140 fix; see ApplyMonsterPoisonLevel).
            // Poison immunity (ability 21) is BOOLEAN — an immune target is skipped (no drain, can rest);
            // the active-spell slot + ability-115 message above still fire so it still sees "You feel ill".
            ApplyMonsterPoisonLevel(spell, target, debuffMagnitude, _world.Database);

            if (spell.CastMessageB > 0 && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var debuffMsg))
            {
                // CastMsgB templates already include "The %s" — pass the raw display name.
                string playerLine = FormatLegacyMessage(debuffMsg.Line2, attacker.DisplayName, spell.Name, target.Name);
                string roomLine = FormatLegacyMessage(debuffMsg.Line3, attacker.DisplayName, spell.Name, target.Name);
                if (!string.IsNullOrWhiteSpace(playerLine))
                    playerMessages.Add(GameAnsi.SpellHostile(playerLine));
                if (!string.IsNullOrWhiteSpace(roomLine))
                    roomMessages.Add(GameAnsi.SpellHostile(roomLine));
            }
            else if (spell.CastMessageB == 0)
            {
                playerMessages.Add(GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name} on you!"));
                roomMessages.Add(GameAnsi.SpellHostile($"{attackerDisplayName} casts {spell.Name} on {target.Name}."));
            }
            // else: CastMessageB references a non-existent message (the "66" no-message sentinel) — the
            // spell is deliberately silent (the invisible quest-flag / utility "temp" spells). Show nothing.

            // The target also sees the spell's ability-115 Line3 ongoing
            // status line ("You are on fire!") in addition to the cast line, on every (re)application.
            string? ongoingLine = ResolveSpellOngoingStatusLine(spell, target.Name);
            if (ongoingLine != null)
                playerMessages.Add(GameAnsi.SpellHostile(ongoingLine));
            return (playerMessages, roomMessages);
        }

        // A monster cast deals HP damage ONLY through a harm ability (1/8/17/19/95 — the cases that
        // subtract HP). A no-harm enemy spell that carries a remove-spell / dispel / cure-status
        // ability is a UTILITY cast: apply that effect, never deal MinBase as damage. Without this gate a
        // utility spell fell through to the damage path and rolled MinBase..MaxBase as HP loss — the bug
        // behind "The shambling mound's remove engulfs hits you for 234 damage!" (spell 235 carries only
        // ability 122 = remove spell 234; its Min/Max 234 is the spell-id PARAMETER, not damage).
        if (!HasHarmAbility(spell) && SpellCarriesUtilityAbility(spell))
        {
            ApplyMonsterUtilitySpellOnTarget(spell, target, attackerDisplayName, playerMessages, roomMessages);
            return (playerMessages, roomMessages);
        }

        int damage = overrideMagnitude ?? RollMonsterSpellMagnitude(spell, castLevel);

        // An area cast divides the rolled damage by the valid-target count before resists. With a
        // shared overrideMagnitude the fan-out already divided once, so the divisor is left at 1 there.
        if (damageDivisor > 1)
            damage /= damageDivisor;

        damage = ApplyElementalSpellDamage(spell, damage, abil => target.GetActiveAbilityValue(_world.Database, abil));
        damage = ApplyOffensiveMagicResistance(spell, damage, target.MagicResist, playerHasAntiMagic);

        target.CurrentHP -= damage;
        AddMonsterDirectDamageSpellMessages(playerMessages, roomMessages, attacker, spell, damage, target);

        if (target.CurrentHP > Player.DeathHP && SpellIsKnockdown(spell))
            ApplyKnockdownSpellToPlayer(spell, target, castLevel, casterIsPlayer: false);

        return (playerMessages, roomMessages);
    }

    // A monster's DeathSpell and CreateSpell both
    // dispatch here. Both call sites pass the SAME args:
    // the monster is the caster and the player-target arg is 0. Because that arg is 0, the
    // single-target branch (Targets 0/8, gated on player != 0) is a NO-OP — only the
    // self/ally types (1/2/4) and the area types (3/5/6/7/9-13)
    // actually fire. Summons (ability 12) spawn reinforcements; area offensive/debuff spells hit the
    // room; chain-cast (ability 151) recasts a follow-up. This is the DeathSpell path, run from the killer's
    // session, so the in-room player it affects is _player. (CreateSpell's world-clean subset — summon +
    // self-buff — fires at the spawn site in GameWorld, which has no player session.)
    private const int MonsterTriggeredCastLevel = 1;          // stock passes 1 as the area/level param
    private const int MaxMonsterTriggeredChainDepth = 3;      // guard against summon/chain cascades
    private static readonly int[] SelfAllyTargetTypes = { 1, 2, 4 };
    private static readonly int[] AreaTargetTypes = { 3, 5, 6, 7, 9, 10, 11, 12, 13 };

    public async Task CastMonsterTriggeredSpellAsync(MonsterInstance caster, int spellId, int chainDepth = 0,
        IReadOnlyList<Player>? areaTextBlockRecipients = null)
    {
        if (spellId <= 0 || !_world.Database.Spells.TryGetValue(spellId, out var spell))
            return;

        bool isSelfAlly = SelfAllyTargetTypes.Contains(spell.Targets);
        bool isArea = AreaTargetTypes.Contains(spell.Targets);

        // Targets 0/8 (single enemy): no player-target arg ⇒ skipped. Faithful no-op.
        if (!isSelfAlly && !isArea)
            return;

        // Capture the area recipient set at the AREA ROOT so a quest textblock reached through the chain
        // (the Targets=1 "text" spell, e.g. golem temp 578 → chain 579) is delivered to EVERY engaged
        // player, not just the killer. This mirrors stock: every quest-delivering DeathSpell temp is an
        // area spell (Targets 12 — verified across all 26 quest bosses), the area cast lands the
        // duration-1 temp on every engaged player in the room, and each holder's
        // spell-termination upkeep (ability 151) runs the chained text spell — hence its
        // ability-148 textblock — on THAT player. Captured once at the root; threaded unchanged through the chain.
        if (isArea && areaTextBlockRecipients == null)
            areaTextBlockRecipients = GetMonsterExperienceRecipients(caster);

        var summonTemplateIds = ResolveSummonTemplateIds(spell);
        bool isSummon = summonTemplateIds.Count > 0;
        bool castSomething = false;
        bool areaEffectSentMessages = false;

        // Summon (ability 12): spawn ONE reinforcement per summon slot into the room, aggressive toward
        // the killer. Both the self/ally and the area path summon this way, and both walk
        // all ten slots — see ResolveSummonTemplateIds.
        if (isSummon)
        {
            foreach (int summonTemplateId in summonTemplateIds)
                TrySummonReinforcement(caster, summonTemplateId);
            castSomething = true;
        }

        // Self/ally duration buff on the caster (KAI ways, armour, haste). Moot for a dying monster but
        // faithful; meaningful for CreateSpell. Skipped once the monster is dead-and-gone.
        if (isSelfAlly && !isSummon && !caster.IsDead && spell.Duration > 0 && spell.Abilities.Count > 0)
        {
            int buffMagnitude = RollMonsterSpellMagnitude(spell, MonsterTriggeredCastLevel);
            int buffDuration = RollMonsterCastSpellDuration(spell, MonsterTriggeredCastLevel);
            caster.AddOrRefreshActiveSpell(_world.Database, spell.Number, buffMagnitude, buffDuration);
            castSomething = true;
        }

        // Area offensive/debuff: lands on EVERY valid player in the room. Stock
        // counts the valid targets and divides damage by that count (duration debuffs apply to all,
        // undivided). Each target gets its own "on you" line via its session; the room sees the rest.
        if (isArea && !isSummon && (IsOffensiveSpell(spell) || (spell.Duration > 0 && spell.Abilities.Count > 0)))
        {
            // Same room fan-out as a combat-cast area spell: one shared roll, full damage for the stock
            // 0xc type, partial element/MR resist per target, no all-or-nothing resist.
            await ResolveMonsterAreaCombatCastAsync(caster, spell, MonsterTriggeredCastLevel);
            areaEffectSentMessages = true;
            castSomething = true;
        }

        // Ability 148: the spell triggers a textblock script rather than a combat effect — e.g.
        // gulguthra's death chain 560→561 "gulguthra portal" carries 148 → textblock 1245
        // ("text 1246 : takeitem 955 : checkability 126 14 : giveability 126 15"), a quest-reward script.
        // Run it through the shared quest-dialogue interpreter (implements text/takeitem/checkability/
        // giveability/…) — for EVERY engaged player when this chain is rooted at an area temp (so the whole
        // party that fought the boss advances), else just the killer. Each player's script self-gates on
        // stage/alignment. The textblock prints its own narrative, so suppress the generic "casts X" line.
        if (spell.Abilities.TryGetValue(ScriptedCommandAbilityId, out int triggerTextBlockId) && triggerTextBlockId > 0)
        {
            await RunTriggeredTextBlockForRecipientsAsync(triggerTextBlockId, areaTextBlockRecipients);
            areaEffectSentMessages = true;
            castSomething = true;
        }

        // Summon / self-buff announce their cast — but with the spell's actual CastMsgB room line, NOT a
        // fabricated "{name} casts {spell}." (stock never prints that). A summon/create death spell uses
        // the empty "66" sentinel → SILENT, and the summoned monster's spawn message is the only notice.
        if (castSomething && !areaEffectSentMessages)
        {
            string? announce = _world.FormatMonsterTriggeredCastAnnounce(spell, caster);
            if (announce != null)
            {
                await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile(announce)]);
                BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile(announce)]);
            }
        }

        // Chain-cast (ability 151 → follow-up spell id): the cast is repeated at the end if the
        // first didn't fail. Depth-guarded against summon/chain loops.
        if (castSomething && chainDepth < MaxMonsterTriggeredChainDepth
            && spell.Abilities.TryGetValue(ChainSpellAbilityId, out int chainSpellId) && chainSpellId > 0)
        {
            await CastMonsterTriggeredSpellAsync(caster, chainSpellId, chainDepth + 1, areaTextBlockRecipients);
        }
    }

    // Run a monster-triggered ability-148 textblock. With no recipients (a non-area-rooted chain) it runs once for
    // the acting session (_player). With recipients (an area-rooted death chain — every quest-delivering
    // DeathSpell temp is area) it runs once per engaged player, each in a CommandParser bound to THEIR
    // session so the quest verbs (giveability/checkability/takeitem/giveitem) act on that player — mirroring
    // the per-holder delivery. Each player's block self-gates on stage/alignment (wrong-stage/already-
    // advanced players no-op), so a redundant re-run (e.g. the registry backstop) is idempotent.
    private async Task RunTriggeredTextBlockForRecipientsAsync(int textBlockId, IReadOnlyList<Player>? recipients)
    {
        if (recipients == null || recipients.Count == 0)
        {
            await TryExecuteTextBlockAsync(textBlockId, triggerInput: null, clueKeywords: null);
            return;
        }

        foreach (var player in recipients)
        {
            if (ReferenceEquals(player, _player))
            {
                await TryExecuteTextBlockAsync(textBlockId, triggerInput: null, clueKeywords: null);
                continue;
            }

            var playerClient = _world.GetClientForPlayer(player.Name);
            if (playerClient == null)
                continue;

            var playerParser = new CommandParser(playerClient, _world, player);
            await playerParser.TryExecuteTextBlockAsync(textBlockId, triggerInput: null, clueKeywords: null);
        }
    }

    // Spawn a summoned reinforcement (ability 12) into the caster's room, owned by the caster.
    // Capped at the summoner child-list size (10). Spawning routes through TrySpawnMonsterInRoom,
    // which fires the summoned monster's own CreateSpell in turn.
    //
    // The summon ability places the new monster but
    // sets NO target/aggro on it — whether it attacks is decided by normal alignment AI (ShouldMonsterAggro),
    // exactly like any other monster in the room. So a hostile reinforcement (align 1/2/5/6) joins the fight
    // immediately, while a non-hostile summon spawns passive and only defends itself if attacked. This is
    // the master-assassin (740) death-spell case: its DeathSpell 902 summons the "dying master assassin"
    // (745, align 3 "Neutral [Not Hostile]"), which must NOT swing at the killer unprovoked.
    private void TrySummonReinforcement(MonsterInstance caster, int summonTemplateId)
    {
        int existingChildren = _world.GetMonstersInRoom(caster.MapNumber, caster.RoomNumber)
            .Count(m => ReferenceEquals(m.Owner, caster) && !m.IsDead);
        if (existingChildren >= MaxMonsterSummonChildren)
            return;

        if (_world.TrySpawnMonsterInRoom(caster.MapNumber, caster.RoomNumber, summonTemplateId, ignoreRoomRestrictions: true, out var summoned, out _)
            && summoned != null)
        {
            summoned.Owner = caster;
            if (CombatEngine.ShouldMonsterAggro(summoned.Template, _player))
            {
                summoned.MarkPlayerEngaged(_player.Name);
                _player.AddIncomingMonsterAttacker(summoned);
            }
        }
    }

    // A player felled by a monster's area death-throes dies through the right path: the killer running
    // this code dies via their own session (HandlePlayerDeath); anyone else dies via their remote session.
    private async Task HandleAreaSpellTargetDeathAsync(Player target)
    {
        if (target.CurrentHP > Player.DeathHP)
            return;

        if (ReferenceEquals(target, _player))
        {
            await HandlePlayerDeath();
            return;
        }

        var session = _world.GetClientForPlayer(target.Name)?.Session;
        if (session != null)
            await session.HandleExternalPlayerDeathAsync();
        else
            target.ClearCombatState();
    }

    // The mid-combat spell picker. ONE roll of 0..99 (top
    // EXCLUSIVE) is compared against each of the five MidSpellPer slots in order and the first slot with
    // `roll < Per[i]` is cast; if no slot matches, the monster casts nothing this round. The comparison is
    // STRICT and the roll includes 0, so a Per of 50 is exactly 50/100 — rolling 1..99 and testing
    // `roll <= Per` (as we did) is 50/99, and it made every mid-spell slightly more frequent than stock,
    // the hydra's head-regrow (#726, Per 50) included.
    private bool TrySelectMonsterMidSpell(IReadOnlyList<MonsterSpell> monsterSpells, out MonsterSpell selected)
    {
        int roll = Random.Shared.Next(0, 100);

        foreach (var monsterSpell in monsterSpells)
        {
            if (monsterSpell.SpellId <= 0 || monsterSpell.Percent <= 0)
                continue;

            if (roll < monsterSpell.Percent)
            {
                selected = monsterSpell;
                return true;
            }
        }

        selected = default!;
        return false;
    }

    internal static bool CanUseMonsterDirectDamageSpell(GameSpell spell)
        => spell.SpellType == 0
            && spell.Targets == 8
            && spell.MinBase > 0
            && spell.MaxBase > 0
            && spell.Abilities.ContainsKey(DamageReducedByMagicResistanceAbilityId);

    // A monster cast handles Targets {0,2,6,8} as a single-target cast on the engaged
    // player and delegates everything else to the area path. The player-offensive area categories
    // (the ones whose damage/debuff loop actually iterates room players)
    // are 3/5/9/10 (split-damage) and 11/12/13 (full-damage). Self/ally (1/2/4) and beneficial-room
    // (6/7) are NOT player-offensive and are handled by the self-buff branch / not at all. A summon
    // spell stays a summon even if area-typed. In stock V1.11p only 0xb/0xc/0xd occur.
    private static readonly int[] AreaCombatTargetTypes = { 3, 5, 9, 10, 11, 12, 13 };

    private bool MonsterSpellIsAreaCombatCast(GameSpell spell)
        => AreaCombatTargetTypes.Contains(spell.Targets)
            && !MonsterSpellTargetsSelf(spell)
            && !spell.Abilities.ContainsKey(SummonAbilityId);

    // The area path divides the rolled magnitude by the valid-target count for the "split" area types
    // (3/5/9/10) but applies it in FULL to every target for 11/12/13 ("Full Attack Area"). Stock data
    // has no 3/5/9/10 spell, so every stock monster area spell deals full, undivided damage per target.
    private static bool AreaSpellDividesDamage(int targets) => targets is 3 or 5 or 9 or 10;

    // Roll a monster spell's magnitude once: base roll + per-level Max scaling, exactly as the single-target
    // and area casts do before applying the effect. Area casts roll this ONCE and share it across every
    // target (see ResolveMonsterAreaCombatCastAsync).
    // A monster's cast damage uses the SAME magnitude band as a player cast (ComputeSpellMagnitudeBand):
    // both the min and max grow with effLvl = min(castLevel, Cap), scaling by MinInc/MinIncLvls and
    // MaxInc/MaxIncLvls. For a monster spell-attack (AtkType 2) the castLevel is the attack's AtkMax field
    // (e.g. Azrandimon's inferno #1048 is AtkAcc 1048 / AtkMax 50 → cast at level 50 → 135..620, matching
    // MME/NMR). The old formula scaled ONLY the max side by (castLevel - ReqLevel), so when a spell's
    // ReqLevel met the cast level (inferno ReqLevel 50, cast 50) it added nothing and returned the raw
    // MinBase..MaxBase roll — for inferno that band is -15..20, i.e. it could "scorch you for -6 damage"
    // (negative damage = a heal). Unifying on the band scales the min too (-15 + 3*50 = 135), so it is
    // never negative at the intended level, and matches stock for every monster caster (dragons, etc.).
    private int RollMonsterSpellMagnitude(GameSpell spell, int castLevel)
    {
        var (min, max) = ComputeSpellMagnitudeBand(spell, castLevel);
        return Random.Shared.Next(min, max + 1);
    }

    // Fan a monster's area combat cast out across the room, faithful to the area cast and its
    // valid-target count:
    //   1. The valid targets are EVERY player in the room (engagement is irrelevant — an idle bystander
    //      is hit too), minus those immune (CanSpellAffectPlayerTarget) and those who pass the per-target
    //      all-or-nothing resist roll. Resisted targets are dropped SILENTLY at the count stage (no "you
    //      resisted" line) and don't count toward the divisor.
    //   2. The magnitude is rolled ONCE and shared across every surviving target — each differs only by
    //      its own partial element/MR reduction (applied in ResolveMonsterAttackSpell).
    //   3. That one roll is split by the surviving-target count for 3/5/9/10, full for 0xb/0xc/0xd.
    // Returns true if at least one player was hit.
    private async Task<bool> ResolveMonsterAreaCombatCastAsync(MonsterInstance attacker, GameSpell spell, int castLevel)
    {
        var targets = _world.GetPlayersInRoom(attacker.MapNumber, attacker.RoomNumber)
            .Where(p => p.CurrentHP > Player.DeathHP && CanSpellAffectPlayerTarget(spell, p))
            .Where(p => !IsOffensiveSpellResisted(spell, p.MagicResist, p.HasActiveAbility(_world.Database, ClassAntiMagicAbilityId)))
            .ToList();
        if (targets.Count == 0)
            return false;

        // One shared roll for the whole room, divided once for the split types.
        int sharedMagnitude = RollMonsterSpellMagnitude(spell, castLevel);
        if (AreaSpellDividesDamage(spell.Targets))
            sharedMagnitude /= targets.Count;

        foreach (var target in targets)
        {
            var (playerMessages, roomMessages) = ResolveMonsterAttackSpell(
                attacker, spell.Number, castLevel, target, damageDivisor: 1, skipFullResist: true, overrideMagnitude: sharedMagnitude);
            await SendCombatMessagesToPlayerAsync(target, playerMessages);
            await BroadcastCombatMessagesToObserversAsync(roomMessages, target);
            await HandleAreaSpellTargetDeathAsync(target);
        }

        return true;
    }

    // A monster duration spell that debuffs the player: an enemy-target timed spell (Targets 0/8/12 —
    // NOT the self/ally types 1/2, which buff the caster, see MonsterSpellTargetsSelf) carrying ability
    // effects that isn't a pure direct-damage spell — slow 87, movement-slow 68, curses, stat
    // drains, blind, etc. A monster cast applies ANY such duration
    // spell as a timed slot on the player; the player's ability aggregation then carries every effect
    // it holds. Pure direct-damage spells stay on the instant-damage path; DoT (damage + duration) is
    // handled there too (poison level), so they're excluded here.
    internal static bool MonsterSpellIsPlayerDebuff(GameSpell spell)
        => !MonsterSpellTargetsSelf(spell)
            && spell.Duration > 0
            && spell.Abilities.Count > 0
            && !CanUseMonsterDirectDamageSpell(spell);

    // Cast-effect ability 19 ("poison"): when AttType is 0/2/6/8 the spell raises the
    // target's poison level to max(current, magnitude). That field drives the "You feel ill"
    // slow-tick HP drain AND gates rest/meditate ("too sick"). The magnitude is the POISON ability's
    // own value (the ability value), falling back to the rolled spell magnitude only when that value
    // is 0 — NOT the spell's Min/MaxBase. On a stat-drain poison like mermex poison (#331: Min/MaxBase
    // -5, the str/will/health debuff magnitude; ability 19 value 1) the old code used the -5 spell
    // magnitude, so Math.Max(0, -5) left the poison level at 0 and the victim could still rest
    // (bug #140). Immunity (ability 21) is deliberately NOT gated here: per observed v1.11p the
    // status still applies and only the per-tick damage is resisted (see the slow-tick scaling).
    // The attack calc gates poison on the spell's single-TARGET type ({0,2,6,8}),
    // NOT AttType. This matters: the iconic poisons spider/wasp/dae-asp poison (#761/#760/#797) are
    // AttType 4 but Targets 8/0 — gating on AttType silently dropped ALL their poison, so spiders never
    // poisoned. Targets {0,2,6,8} are the single-target casts; area-typed poison (poison cloud #142,
    // Targets 12) is handled by the separate area path, not here.
    private static readonly int[] PoisonSpellTargetTypes = { 0, 2, 6, 8 };
    internal static void ApplyMonsterPoisonLevel(GameSpell spell, Player target, int rolledMagnitude, Data.IGameDatabase db)
    {
        if (!spell.Abilities.TryGetValue(PoisonSpellAbilityId, out int poisonAbilityValue)
            || System.Array.IndexOf(PoisonSpellTargetTypes, spell.Targets) < 0)
            return;

        // The poison-status application is BOOLEAN-gated on poison immunity
        // (ability 21 must be absent). A target with ability 21 from ANY source (Kang race,
        // golden headdress, a buff) never has PoisonLevel raised — so it takes NO slow-tick drain and can
        // still rest. This is TRUE immunity, not a resistance that scales damage: the tell (verified live
        // on a stock server with a Kang) is that the victim CAN rest. The spell's ability-115 "You feel
        // ill" message and its active-spell slot are applied SEPARATELY and unconditionally (a different,
        // ungated case), which is why an immune target still SEES "You feel ill" on stat.
        if (target.HasActiveAbility(db, PoisonImmunityAbilityId))
            return;

        int poisonLevel = poisonAbilityValue != 0 ? poisonAbilityValue : rolledMagnitude;
        if (poisonLevel > 0)
            target.PoisonLevel = System.Math.Max(target.PoisonLevel, poisonLevel);
    }

    // Announce a monster summon ("calls for aid"), mirroring the CastMsgB handling in the debuff
    // branch above. Three cases:
    //   • CastMsgB resolves to a message (#2711 "%s takes out their whistle and %s!", #651 "The orc
    //     warleader bellows loudly for aid!") — use it. These aid/whistle lines carry only a room
    //     (Line3) variant with no distinct "you" line, so the victim is shown the room line too: the
    //     act is plainly visible to everyone present.
    //   • CastMsgB == 0 — generic "casts X" announce.
    //   • CastMsgB > 0 but missing (the "66" no-message sentinel, e.g. the guardsman's silent
    //     reinforcement-call #888) — deliberately silent, show nothing.
    private void AddMonsterSummonCastMessages(GameSpell spell, MonsterInstance attacker, Player target, List<string> playerMessages, List<string> roomMessages)
    {
        string youLine;
        string roomLine;
        if (spell.CastMessageB > 0 && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var msg))
        {
            youLine = FormatLegacyMessage(msg.Line2, attacker.DisplayName, spell.Name, target.Name);
            roomLine = FormatLegacyMessage(msg.Line3, attacker.DisplayName, spell.Name, target.Name);
            // No distinct "you" variant → show the visible room action to the victim too.
            if (string.IsNullOrWhiteSpace(youLine))
                youLine = roomLine;
        }
        else if (spell.CastMessageB == 0)
        {
            youLine = roomLine = $"{GetMonsterCastDisplayName(attacker)} casts {spell.Name}.";
        }
        else
        {
            return; // CastMsgB references the missing "66" sentinel — deliberately silent.
        }

        if (!string.IsNullOrWhiteSpace(youLine))
            playerMessages.Add(GameAnsi.SpellHostile(youLine));
        if (!string.IsNullOrWhiteSpace(roomLine))
            roomMessages.Add(GameAnsi.SpellHostile(roomLine));
    }

    private void AddMonsterDirectDamageSpellMessages(List<string> playerMessages, List<string> roomMessages, MonsterInstance attacker, GameSpell spell, int damage, Player target)
    {
        string casterDisplayName = GetMonsterCastDisplayName(attacker);

        if (CanUseSingleTargetDamageCastMessage(spell)
            && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
        {
            string damageText = damage.ToString(CultureInfo.InvariantCulture);
            // Pass the RAW display name; the formatter prepends a "The " article per template line only
            // when that line doesn't already supply one (msg 14 "%s casts ..." vs msg 201 "The %s ...").
            string rawDisplayName = string.IsNullOrWhiteSpace(attacker.DisplayName)
                ? attacker.Template.Name
                : attacker.DisplayName;
            var (_, targetLine, roomLine) = FormatSingleTargetDamageCastMessage(
                spell,
                message,
                rawDisplayName,
                target.Name,
                damage,
                damageText,
                articleCasterName: true);

            if (!string.IsNullOrWhiteSpace(targetLine))
                playerMessages.Add(GameAnsi.CombatHit(targetLine));
            if (!string.IsNullOrWhiteSpace(roomLine))
                roomMessages.Add(GameAnsi.CombatHit(roomLine));
            return;
        }

        playerMessages.Add(GameAnsi.CombatHit($"{casterDisplayName}'s {spell.Name} hits you for {damage} damage!"));
        roomMessages.Add(GameAnsi.CombatHit($"{casterDisplayName}'s {spell.Name} hits {target.Name} for {damage} damage!"));
    }

    private static bool CanUseSingleTargetDamageCastMessage(GameSpell spell)
        => spell.CastMessageB > 0;

    // articleCasterName is set ONLY by the monster caster path: a monster's name takes a "The " article
    // ("The giant rat casts harm ...") while a player caster ("HarmRedCaster casts harm ...") never does,
    // so the player PvP / player-vs-monster callers leave it false and the caster name renders verbatim.
    private static (string CasterLine, string TargetLine, string ObserverLine) FormatSingleTargetDamageCastMessage(
        GameSpell spell,
        RoomMessage message,
        string casterDisplayName,
        string targetDisplayName,
        int damage,
        string damageText,
        bool articleCasterName = false)
    {
        // For an AREA target type (Targets == 3 or 9..13) the
        // target-NAME substitution is blanked. These messages already name the victim with their own
        // wording ("engulfs your foe" / "engulfs the room" / "engulfs you"), so the explicit %s name
        // slot must render empty — otherwise the name jams into it ("A swirling mana storm<name>
        // engulfs your foe..."). Single-enemy types (0/8) keep the real name.
        if (spell.Targets == 3 || (spell.Targets >= 9 && spell.Targets <= 13))
            targetDisplayName = string.Empty;

        // MsgStyle bit 0 selects the "no spell-name slot" grammar: Line1 reads "<effect> %s[target] for
        // %d[dmg]" (e.g. msg 1150 "Lightning strikes %s for %d damage!", 2868 "A frozen spike … impales
        // %s for %d damage!"), Line2 "<effect> you for %d[dmg]", Line3 "<effect> %s[target] for %s[dmg]".
        // Both style 1 (355 spells) and style 33 (= 32|1, 15 spells) use it; styles 0/32 (bit 0 clear)
        // carry the spell name in Line1's first slot ("You summon %s upon %s for %d"). Passing the spell
        // name into a bit-0 template produced the malformed "Lightning strikes lightning hits for <target>
        // damage" — the spell name landed in the target slot and the target in the damage slot.
        if ((spell.MessageStyle & 1) != 0)
        {
            return (
                FormatLegacyMessage(message.Line1, targetDisplayName, damage),
                FormatLegacyMessage(message.Line2, damage),
                FormatLegacyMessage(message.Line3, targetDisplayName, damageText));
        }

        string CasterFor(string line) => articleCasterName
            ? ArticleCasterNameForLine(line, casterDisplayName)
            : casterDisplayName;

        return (
            FormatLegacyMessage(message.Line1, spell.Name, targetDisplayName, damage),
            FormatLegacyMessage(message.Line2, CasterFor(message.Line2), spell.Name, damage),
            FormatLegacyMessage(message.Line3, CasterFor(message.Line3), spell.Name, targetDisplayName, damageText));
    }

    // Monster cast-message templates split into two grammar families: a bare leading caster slot —
    // "%s casts %s on you" (msg 14, harm) — that needs a "The " article, and pre-articled forms —
    // "The %s casts %s on you" (msg 201, 263, …). Supply the article only when the template line lacks
    // one, and never stack two, so a plain display name ("hill giant shaman") renders "The hill giant
    // shaman ..." while one that already carries its own article ("The hill giant shaman") stays single.
    private static string ArticleCasterNameForLine(string templateLine, string casterDisplayName)
    {
        bool templateHasArticle = (templateLine ?? string.Empty).TrimStart()
            .StartsWith("the ", StringComparison.OrdinalIgnoreCase);
        bool nameHasArticle = casterDisplayName.StartsWith("the ", StringComparison.OrdinalIgnoreCase);

        if (templateHasArticle)
            return nameHasArticle ? casterDisplayName.Substring(4) : casterDisplayName;

        return nameHasArticle ? casterDisplayName : $"The {casterDisplayName}";
    }


    // Magnitude roll:
    //   effLvl = (Cap < 1 || level <= Cap) ? level : Cap
    //   max = MaxBase + (MaxInc * effLvl) / MaxIncLvls       (if MaxIncLvls > 0)
    //   min = min(MinBase + (MinInc * effLvl) / MinIncLvls, max)
    //   rolled = genrdn(0, max-min+1) + min
    // Both bands grow independently — at lvl 60, Greater Healing's max climbs from 10 to 70 while
    // Minor Healing's caps at 14 (Cap=10).
    internal static (int Min, int Max) ComputeSpellMagnitudeBand(GameSpell spell, int casterLevel)
    {
        int effLvl = spell.Cap > 0 ? Math.Min(casterLevel, spell.Cap) : casterLevel;
        int maxAdd = spell.MaxIncLvls > 0 ? (spell.MaxInc * effLvl) / spell.MaxIncLvls : 0;
        int minAdd = spell.MinIncLvls > 0 ? (spell.MinInc * effLvl) / spell.MinIncLvls : 0;
        int max = spell.MaxBase + maxAdd;
        int min = Math.Min(spell.MinBase + minAdd, max);
        return (min, max);
    }

    internal static int RollSpellMagnitude(GameSpell spell, int casterLevel)
    {
        var (min, max) = ComputeSpellMagnitudeBand(spell, casterLevel);
        return Random.Shared.Next(min, max + 1);
    }

    private bool IsOffensiveSpellResisted(GameSpell spell, int magicResistance, bool targetHasAntiMagic)
    {
        int roll = Random.Shared.Next(1, 100);
        return SpellResistanceMath.IsSpellResisted(spell.TypeOfResists, magicResistance, targetHasAntiMagic, roll);
    }

    private int ApplyOffensiveMagicResistance(GameSpell spell, int amount, int magicResistance, bool targetHasAntiMagic)
    {
        if (amount <= 0
            || !spell.Abilities.ContainsKey(DamageReducedByMagicResistanceAbilityId)
            || spell.Abilities.ContainsKey(NonMagicalSpellAbilityId))
        {
            return amount;
        }

        return SpellResistanceMath.ApplyDamageMinusMagicResistance(amount, magicResistance, targetHasAntiMagic);
    }

    // Elemental damage: a spell's element (AttType) is reduced by the target's matching resistance
    // ability via damage*(100-resist)/100, gated by spell type < 3. resistLookup(abilityId) returns the
    // target's value for that resistance ability (player abilities or monster template abilities).
    private static int ApplyElementalSpellDamage(GameSpell spell, int amount, Func<int, int> resistLookup)
    {
        int elementAbility = SpellResistanceMath.GetElementResistAbilityId(spell.AttType);
        int resist = (spell.SpellType < 3 && elementAbility != 0) ? resistLookup(elementAbility) : 0;
        return SpellResistanceMath.ApplyElementalDamage(spell.SpellType, spell.AttType, amount, resist);
    }

    private static string FormatLegacyMessage(string template, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        int index = 0;
        return Regex.Replace(template, "%[sd]", _ =>
        {
            if (index >= args.Length)
                return string.Empty;

            object value = args[index++];
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        });
    }

    private bool CanSpellAffectPlayerTarget(GameSpell spell, Player target)
    {
        if (spell.Abilities.ContainsKey(AffectsAnimalAbilityId))
            return false;

        // NOTE: poison (ability 19) is intentionally NOT blocked here by poison resistance (ability 21).
        // Observed stock applies the poison and scales its per-tick damage by resistance
        // instead of skipping it, so a poison-resistant target (e.g. Kang = 100) still gets the cast
        // message + "You feel ill." but takes 0 damage. The boolean immunity gate in the static
        // single-target path produces "no effect", which does NOT match live behavior — see slow tick.

        // Cast pre-pass: a target carrying the WARD ability (81) is
        // immune to status spells that inflict paralysis/disease — those carry the 74/75 status
        // markers (hold person, entangle, web, paralyze, knockdown, …). Mirrors the poison/21 gate
        // above. The ward is granted by the self/ally cure-status buffs (freedom/free) and innately by
        // some monsters; sets the "no effect on %s" outcome.
        if ((spell.Abilities.ContainsKey(ParalysisStatusAbilityId) || spell.Abilities.ContainsKey(DiseaseStatusAbilityId))
            && target.HasActiveAbility(_world.Database, CureStatusAbilityId))
        {
            return false;
        }

        int spellImmunityLevel = target.GetActiveAbilityValue(_world.Database, SpellImmunityAbilityId);
        return spellImmunityLevel <= 0 || spell.ReqLevel > spellImmunityLevel;
    }

    private static bool CanSpellAffectMonsterTarget(GameSpell spell, Monster target)
    {
        if (spell.Abilities.ContainsKey(AffectsAnimalAbilityId)
            && !target.Abilities.ContainsKey(AnimalAbilityId))
        {
            return false;
        }

        if (spell.Abilities.ContainsKey(AffectsLivingAbilityId)
            && target.Abilities.ContainsKey(NonLivingAbilityId))
        {
            return false;
        }

        // In both per-monster damage loops, a spell carrying ability 23
        // ("Kill Dead") only affects UNDEAD monsters. On a non-undead
        // target it prints "Your spell has no effect on <name>." and applies nothing — so turn undead's
        // ability-1 damage must NOT land here. Fixes ticket #1 (turn undead worked on non-undead).
        if (spell.Abilities.ContainsKey(KillDeadAbilityId) && !target.Undead)
        {
            return false;
        }

        if (spell.Abilities.ContainsKey(PoisonSpellAbilityId)
            && target.Abilities.ContainsKey(PoisonImmunityAbilityId))
        {
            return false;
        }

        // Ward (81) blocks paralysis/disease status spells (74/75) — see CanSpellAffectPlayerTarget.
        // ~12 stock monsters carry the ward innately (e.g. Sheriff Lionheart), making them immune to
        // hold/entangle/web/paralyze.
        if ((spell.Abilities.ContainsKey(ParalysisStatusAbilityId) || spell.Abilities.ContainsKey(DiseaseStatusAbilityId))
            && target.Abilities.ContainsKey(CureStatusAbilityId))
        {
            return false;
        }

        return true;
    }

    private static bool CanSpellSatisfyRequiredToHit(GameSpell spell, Monster target)
    {
        if (!target.Abilities.TryGetValue(RequiredToHitAbilityId, out int requiredValue))
            return true;

        return spell.Abilities.TryGetValue(RequiredToHitAbilityId, out int spellValue)
            && spellValue == requiredValue;
    }

    // Summon on a user target: spawn EACH ability-12 template as a creature
    // owned by the caster in the caster's room. A summon spell can carry several summon slots — e.g.
    // "burning summon" (#1182) spawns both the flaming waist (535) and torso (536) — so we iterate the
    // ordered ability slots (the Abilities dict would collapse the duplicate ability id). Self-cast.
    private async Task HandlePlayerSummonCastAsync(GameSpell spell)
    {
        _player.CurrentMana -= spell.ManaCost;
        await ConsumeSpellRoundAsync(spell);

        await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellBeneficial($"You cast {spell.Name}.")]);
        BroadcastCombatMessagesToRoom([GameAnsi.SpellBeneficial($"{_player.Name} casts {spell.Name}.")]);

        // One pet per summon slot, in slot order, sharing the resolver with every monster summon path —
        // a slot carrying a 0 value takes its template id from the rolled MinBase..MaxBase (summon steed
        // #1105 → 814, summon demonling #1216, the giant-cloak summons, etc.).
        int summoned = 0;
        foreach (int templateId in ResolveSummonTemplateIds(spell))
        {
            if (_world.TrySummonPlayerPet(_player, templateId, out var pet) && pet != null)
            {
                summoned++;
                await SendCombatMessagesToCurrentPlayerAsync(
                    [GameAnsi.SpellBeneficial($"The {pet.DisplayName} appears, ready to serve you!")]);
            }
        }

        if (summoned == 0)
            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellFailure("Nothing answers your call.")]);
    }

    // Charm/enslave (ability 6). On a USER target it is a no-op (a flavour
    // message — you cannot charm another player). On a MONSTER target
    // it converts the monster into a pet owned by the caster after the gate: the monster must be
    // affectable by the spell's creature-class restriction (Affects Animal/Living), must not already
    // be owned, must not be an "angel" (summoned Type 37), the caster must out-level its CharmLVL,
    // and a genrdn(1,100) < FollowPercent roll must pass. The attempt charges evil points (add_evil_
    // points base 10, id 0xb) like any aggression on the creature.
    private async Task HandlePlayerCharmCastAsync(GameSpell spell, string target)
    {
        if (!TryResolveOffensiveSpellTarget(target, allowImplicitCombatTarget: true, out var monster, out var playerTarget))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You must specify a target for that spell!{MudAnsi.Reset}");
            return;
        }

        _player.CurrentMana -= spell.ManaCost;
        await ConsumeSpellRoundAsync(spell);

        if (playerTarget != null || monster == null)
        {
            // Charm on a player is a no-op in stock (a flavour message only).
            await _client.SendLineAsync($"{MudAnsi.White}Your {spell.Name} has no effect.{MudAnsi.Reset}");
            return;
        }

        await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellHostile($"You cast {spell.Name} on the {monster.DisplayName}.")]);
        BroadcastCombatMessagesToRoom([GameAnsi.SpellHostile($"{_player.Name} casts {spell.Name} on the {monster.DisplayName}.")]);

        // Outer gate (owner==0 and the creature-class match) — no EP if it fails.
        if (monster.IsOwnedBy(_player.Name))
        {
            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellFailure($"The {monster.DisplayName} already serves you.")]);
            return;
        }
        if (!CanSpellAffectMonsterTarget(spell, monster.Template))
        {
            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellFailure($"Your {spell.Name} has no effect on the {monster.DisplayName}.")]);
            return;
        }

        // Evil points (base 10, id 11): charming charges evil points like any aggression.
        await AddEvilPointsWithCloudAsync(CombatEngine.GetEPCostForMonsterAttack(_player, monster.Template));

        // Inner gate: not already someone else's pet, not an angel, caster out-levels CharmLVL, and the
        // FollowPercent roll passes (genrdn(1,100) < FollowPercent; FollowPercent 0 ⇒ never charmable).
        bool angelType = monster.Template.Type == GameWorld.SummonedAngelMonsterType;
        bool levelOk = monster.Template.CharmLVL <= 0 || _player.Level >= monster.Template.CharmLVL;
        bool rollOk = monster.Template.FollowPercent > 0
            && Random.Shared.Next(1, 100) < monster.Template.FollowPercent;
        if (monster.HasPlayerOwner || angelType || !levelOk || !rollOk)
        {
            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellFailure($"The {monster.DisplayName} resists your spell!")]);
            return;
        }

        _world.AttachCharmedPet(monster, _player.Name);
        await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.SpellBeneficial($"The {monster.DisplayName} is now under your control!")]);
        BroadcastCombatMessagesToRoom([GameAnsi.SpellBeneficial($"The {monster.DisplayName} begins to serve {_player.Name}.")]);
    }

    // Identify on an item target (ability 26): resolve a carried/equipped item and
    // read its aura magnitude (item ability 28 / ItemMagical). The glow tier keys on that value
    // — 1 / 2-3 / 4-5 / >=6 — with a plain "no magic" line for 0 (and anything outside the tiers, e.g.
    // a negative). The room sees "<caster> casts <spell> on <item>." Mana + the cast round are spent
    // on the resolved cast like any other spell. The item-target path also handles recharge (162), but
    // NO stock spell carries it (dead in data, like banish/reagents), so only identify is wired.
    private async Task HandleIdentifySpellCastAsync(GameSpell spell, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Cast {spell.Name} on what?{MudAnsi.Reset}");
            return;
        }

        if (!TryResolveUniqueCarriedItem(target, includeEquipped: true, out var match, out var ambiguousNames))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return;
            }

            await _client.SendLineAsync("You don't have that item.");
            return;
        }

        _player.CurrentMana -= spell.ManaCost;
        await ConsumeSpellRoundAsync(spell);

        string itemName = match.Item.Name;
        int aura = match.Item.Abilities.GetValueOrDefault(ItemMagicalAbilityId);
        string detection = aura switch
        {
            1 => GameAnsi.SpellBeneficial($"{itemName} glows faintly, indicating a small amount of magic within."),
            2 or 3 => GameAnsi.SpellBeneficial($"{itemName} glows softly, indicating a good amount of magic within."),
            4 or 5 => GameAnsi.SpellBeneficial($"{itemName} glows brightly, indicating a large amount of magic within."),
            >= 6 => GameAnsi.SpellBeneficial($"You are almost blinded by the aura from {itemName}, indicating immense magical properties!"),
            _ => $"{MudAnsi.White}You detect no magic in that item!{MudAnsi.Reset}",
        };

        await _client.SendLineAsync(detection);
        BroadcastCombatMessagesToRoom([GameAnsi.SpellBeneficial($"{_player.Name} casts {spell.Name} on {itemName}.")]);
    }

    private async Task HandleBeneficialSpellCastAsync(GameSpell spell, string target)
    {
        if (!TryResolveBeneficialSpellTarget(target, out var spellTarget))
        {
            await _client.SendLineAsync("Cast on whom?");
            return;
        }

        _player.CurrentMana -= spell.ManaCost;
        await ConsumeSpellRoundAsync(spell);

        // Stock: a cast whose remove-spell list strips a spell the target has ENDS at the removal. The
        // normal cast lines still print, then the stripped spell's wear-off, but the cast's own buff and
        // every other effect are skipped (a second cast lands it). A self-cast stops at the first spell it
        // strips; a cast on a named player strips every listed spell it finds. QOL "spells" instead lets
        // the one cast both strip and apply (the path below).
        if (!_world.IsQolEnabled(QolFeature.SpellRemoves) && !_world.PlayerHasItemNegatingSpell(spellTarget, spell.Number))
        {
            var stripped = FindStockCastRemovals(spell, spellTarget, firstMatchOnly: string.IsNullOrWhiteSpace(target));
            if (stripped.Count > 0)
            {
                await SendBeneficialSpellMessagesAsync(spell, spellTarget, amount: null);
                foreach (int spellNumber in stripped)
                    ApplyDispelBySpellNumberEffect(spellTarget, spellNumber);
                return;
            }
        }

        // Cast effect model: the spell's whole ability list becomes a duration buff (once), and
        // each ability id's immediate effect is dispatched in a loop.
        ApplyBuffSpellIfDuration(spell, spellTarget);
        int? amount = ApplyImmediateBeneficialEffects(spell, spellTarget);
        await ApplyScriptedCommandSpellEffectAsync(spell, spellTarget);

        await SendBeneficialSpellMessagesAsync(spell, spellTarget, amount);
    }

    // Targets=13 — "full party area" (healing rain, group bless, songs, etc.). The cast pays its
    // cost and consumes the round once, then runs the same per-ability effect dispatch each party
    // member in the room would receive from a single-target cast. The no-target area cast
    // rolls the magnitude ONCE
    // and reuses that value for every target in the per-target loop, so we
    // pre-roll here and thread the value into both the duration buff and the immediate-effect
    // dispatch. Targets=13 specifically (case 0xd in the SpellType-Targets switch at line ~39456)
    // skips the divide-by-target-count adjustment that Targets 3/5/9/10 carry, so each recipient
    // receives the full rolled magnitude. The HP-cap clamp is per-target (SpellEffects.ApplyHeal).
    private async Task HandleGroupBeneficialSpellCastAsync(GameSpell spell)
    {
        _player.CurrentMana -= spell.ManaCost;
        await ConsumeSpellRoundAsync(spell);

        // Rolled unconditionally: the scaled band is always valid (ComputeSpellMagnitudeBand clamps
        // min to max), so the raw-column test this used to carry could only fall back to a per-target
        // re-roll and break the roll-once-share-with-everyone rule. Consumed only by the
        // immediate effects that would otherwise roll for themselves.
        int? sharedRoll = RollSpellMagnitude(spell, _player.Level);
        int? sharedBuffMagnitude = spell.Duration > 0
            ? RollSpellLevelScaledAmount(spell)
            : (int?)null;

        var recipients = GetGroupSpellRecipients();
        var heals = new List<(Player Recipient, int? Amount)>(recipients.Count);
        int totalAmount = 0;

        foreach (var recipient in recipients)
        {
            ApplyBuffSpellIfDuration(spell, recipient, sharedBuffMagnitude);
            int? amount = ApplyImmediateBeneficialEffects(spell, recipient, sharedRoll);
            heals.Add((recipient, amount));
            if (amount.HasValue)
                totalAmount += amount.Value;
        }

        // The caster's line reports ONE recipient's amount, never the group total. The success
        // message is emitted once PER TARGET from the cast loop with that target's magnitude,
        // but the caster half is behind a one-shot latch — so it prints exactly once,
        // carrying a single target's number. For an area type
        // (Targets 3 or 9..13, and healing rain is 13) the target-name %s is replaced with the empty
        // string, which is what makes message 109 read "You cast healing rain on your
        // party, healing N damage!".
        //
        // We were summing across the group, so a six-strong party reported ~6x the real figure — the
        // "hitting harder than expected / reporting weird to the caster" report. The HEALING itself was
        // always right; only this number was inflated.
        //
        // Report the rolled magnitude rather than a post-clamp figure: stock passes the
        // roll and never the amount actually absorbed, so a nearly-full-health recipient does not
        // shrink the reported number.
        await SendGroupBeneficialSpellMessagesAsync(spell, heals, sharedRoll ?? totalAmount);
    }

    // Recipients for a Targets=13 cast: the caster plus every other party member in the same room.
    // A solo caster (no party) gets only themselves, which keeps `cast rain` working outside a
    // group. The room filter is also the practical party filter — followers who move away from
    // the leader drop from the party via HandleIndependentPartyTravelCleanupAsync, so an
    // out-of-room member is either being left behind right now or has already left.
    private List<Player> GetGroupSpellRecipients()
    {
        var recipients = new List<Player> { _player };

        string? leaderName = _world.GetPartyLeaderName(_player.Name);
        if (string.IsNullOrEmpty(leaderName))
            return recipients;

        var roommates = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        foreach (var candidate in roommates)
        {
            if (candidate.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            string? candidateLeader = _world.GetPartyLeaderName(candidate.Name);
            if (string.Equals(candidateLeader, leaderName, StringComparison.OrdinalIgnoreCase))
                recipients.Add(candidate);
        }

        return recipients;
    }

    private async Task SendGroupBeneficialSpellMessagesAsync(GameSpell spell, List<(Player Recipient, int? Amount)> heals, int casterReportedAmount)
    {
        // CastMessageB on a Targets=13 spell is the party-aware template (e.g. message 109:
        // Line1 "You cast %s on your %sparty, healing %d damage!" / Line2 "%s casts %s on you,
        // healing %d damage!" / Line3 "%s casts %s on the room!"). The caster line shows the
        // total amount healed/granted across the group; per-recipient lines show that recipient's
        // own amount; the room line broadcasts once.
        if (_world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
        {
            // The success message fires per affected target: each recipient also sees the spell's
            // ability-115 Line3 "effect applied" status line ("You feel tough!") after the cast
            // line — same line the single-target beneficial path emits. Resolve per recipient (the line
            // may carry a %s name), and emit to the caster too when they are among the recipients.
            string casterLine = FormatLegacyMessage(message.Line1, spell.Name, string.Empty, casterReportedAmount);
            if (!string.IsNullOrWhiteSpace(casterLine))
                await _client.SendLineAsync(GameAnsi.SpellBeneficial(casterLine));

            foreach (var (recipient, amount) in heals)
            {
                string? recipientStatus = ResolveSpellOngoingStatusLine(spell, recipient.Name);

                if (ReferenceEquals(recipient, _player))
                {
                    if (recipientStatus != null)
                        await _client.SendLineAsync(GameAnsi.SpellBeneficial(recipientStatus));
                    continue;
                }

                string targetLine = FormatLegacyMessage(message.Line2, _player.Name, spell.Name, amount.GetValueOrDefault());
                if (!string.IsNullOrWhiteSpace(targetLine))
                    await SendCombatMessagesToPlayerAsync(recipient, [GameAnsi.SpellBeneficial(targetLine)]);
                if (recipientStatus != null)
                    await SendCombatMessagesToPlayerAsync(recipient, [GameAnsi.SpellBeneficial(recipientStatus)]);
            }

            string observerLine = FormatLegacyMessage(message.Line3, _player.Name, spell.Name);
            if (!string.IsNullOrWhiteSpace(observerLine))
            {
                var recipientNames = heals
                    .Select(entry => entry.Recipient.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var observers = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
                    .Where(player => !recipientNames.Contains(player.Name));

                string formatted = GameAnsi.SpellBeneficial(observerLine);
                foreach (var observer in observers)
                    await SendCombatMessagesToPlayerAsync(observer, [formatted]);
            }

            return;
        }

        // Fallback when the spell has no CastMessageB row defined — keep the cast observable even
        // if the database entry is missing.
        await _client.SendLineAsync(GameAnsi.SpellBeneficial($"You cast {spell.Name} on your party."));
        foreach (var (recipient, amount) in heals)
        {
            if (ReferenceEquals(recipient, _player))
                continue;

            string text = amount.HasValue
                ? $"{_player.Name}'s {spell.Name} heals you for {amount.Value} hit points."
                : $"{_player.Name} casts {spell.Name} on you.";
            await SendCombatMessagesToPlayerAsync(recipient, [GameAnsi.SpellBeneficial(text)]);
        }
    }

    // Unified spell-effect dispatch (beneficial / self subset) — mirrors the per-ability switch in
    // the cast functions. Loops the spell's ability ids and applies each immediate effect; the
    // duration buff is applied separately by ApplyBuffSpellIfDuration. Returns the primary magnitude
    // to display (the first immediate effect's amount), or null. The offensive/damage side still runs
    // through ExecuteOffensiveSpell* and will be folded into this dispatch in a later step.
    private int? ApplyImmediateBeneficialEffects(GameSpell spell, Player target, int? preRolledAmount = null)
    {
        // Negate-spell gate (see ApplyBuffSpellIfDuration): a worn item on the recipient whose "Negate
        // Spells" list contains this spell number makes the cast do nothing to them. Returning null here
        // means no HP/mana/poison/etc is applied and the caster's message shows a 0 amount.
        if (_world.PlayerHasItemNegatingSpell(target, spell.Number))
            return null;

        int? primaryAmount = null;

        // Iterate AbilitySlots (the duplicate-preserving slot list), NOT the Abilities dictionary: a spell
        // may carry the SAME ability id in several of its 10 slots, and the dictionary keeps only the last.
        // "remove forms" (#878) is ability 122 FIVE times (remove 879/858/880/881/882 — every form buff),
        // which collapses to a single {122: 882} in the dict, so the dictionary path removed only the viper
        // form and let the others stack (you could wear two forms at once). The effect engine walks all
        // ten slots, so this is also the faithful application order.
        foreach (var (abilityId, abilityValue) in spell.AbilitySlots)
        {
            switch (abilityId)
            {
                case HealingSpellAbilityId:            // 18 — heal HP
                    primaryAmount ??= ApplyHealEffect(spell, target, preRolledAmount);
                    break;
                case RestoreManaAbilityId:             // 150 — restore mana
                    primaryAmount ??= ApplyRestoreManaEffect(spell, target, preRolledAmount);
                    break;
                case ReducePoisonAbilityId:            // 20 — reduce poison level (the real cure)
                    primaryAmount ??= ApplyReducePoisonEffect(spell, target, preRolledAmount);
                    break;
                case DispelAbilityId:                  // 73 — strip buffs by ability id
                    SendDispelWearOffsToPlayer(target, ApplyDispelEffect(target, abilityValue));
                    break;
                case CureStatusAbilityId:              // 81 — strip 74/75 status buffs (cure disease)
                    SendDispelWearOffsToPlayer(target, ApplyCureStatusEffect(target));
                    break;
                case RemoveCastSpellAbilityId:         // 122 — remove a specific spell# (cast)
                case RemoveEffectSpellAbilityId:       // 153 — remove a specific spell# (effect)
                    ApplyDispelBySpellNumberEffect(target, abilityValue);
                    break;
                case RestoreEnergyAbilityId:           // 11 — restore energy/stamina pool
                    primaryAmount ??= ApplyRestoreEnergyEffect(spell, target, preRolledAmount);
                    break;
                case TeachSpellAbilityId:              // 160 — teach a spell (value = spell #)
                    SpellEffects.TeachSpell(target, abilityValue);
                    break;
                case RemoveArmourAbilityId:            // 84 — remove curse (strip cursed worn items)
                    primaryAmount ??= ApplyRemoveCurseEffect(target);
                    break;
            }
        }

        // Instant healing keys SOLELY on ability 18 (HealingSpellAbilityId), handled in the
        // switch above. There is no name-based heal: "rapid healing" (rhel #138/831/876) carries ability
        // 123 (HP-regen %) NOT 18 — it is a rest-regen buff, not an instant heal. The old
        // `Name.Contains("heal")` fallback mis-fired it as an instant MinBase(200)-HP heal on cast
        // (bug #143). Every genuine instant-heal spell in game_data carries ability 18, so dropping the
        // fallback loses no real heal while killing the phantom 200-HP cast.
        return primaryAmount;
    }

    // The ability ids ApplyImmediateBeneficialEffects acts on — the immediate (non-duration) beneficial
    // effects. Used to tell a genuinely beneficial instant spell (heal/cure/remove-spell, e.g. the black
    // cauldron #356: Heal 9999 + CurePoison + RemoveSpells, Duration 0) apart from a damage/teleport/
    // scripted spell so the item-use path applies the heal instead of mis-routing MinBase/AttType to the
    // generic damage branch ("hits you for 9999 damage").
    private static readonly int[] ImmediateBeneficialAbilityIds =
    {
        HealingSpellAbilityId, RestoreManaAbilityId, ReducePoisonAbilityId, DispelAbilityId,
        CureStatusAbilityId, RemoveCastSpellAbilityId, RemoveEffectSpellAbilityId,
        RestoreEnergyAbilityId, TeachSpellAbilityId, RemoveArmourAbilityId,
    };

    internal static bool SpellHasImmediateBeneficialEffect(GameSpell spell)
        => ImmediateBeneficialAbilityIds.Any(spell.Abilities.ContainsKey);

    // True when a spell carries a teleport destination (ability 141 dest map). Matches the gate in
    // TryResolveTriggeredSpellTeleport, as a pure/static check for the routing classifier.
    internal static bool SpellHasTeleportDestination(GameSpell spell)
        => spell.Abilities.GetValueOrDefault(TeleportMapAbilityId) > 0;

    // How the quest-script `cast <spellId>` verb (CommandParser.QuestDialogue.cs) routes a spell. This is
    // the SINGLE SOURCE OF TRUTH for that routing decision: the verb switches on it, and the data-driven
    // routing audit (QuestSpellRoutingAuditTests) consumes it to validate that every quest-referenced
    // spell's intent (its abilities) lands in a handler that matches — so hand-authored DB data can't
    // silently route a heal/teleport/utility spell into the actor-damage branch.
    internal enum QuestCastRoute
    {
        AreaEnemySweep,   // Targets 12 + harm — sweep the room's monsters (each victim's DeathSpell fires)
        PartyTeleport,    // Targets 13 + teleport — relocate the whole party in the room, then the caster
        Beneficial,       // non-offensive + an immediate beneficial effect — apply to caster (party if T13)
        DurationBuff,     // Duration>0 — buff/debuff slot on the caster (e.g. jail-time sentence)
        Chain,            // Duration 0, only an ability-151 chain — cast the rolled/linked follow-up spell instead
        Triggered,        // everything else — ExecuteTriggeredSpellByIdAsync (teleport / scripted / damage)
    }

    internal static QuestCastRoute ClassifyQuestCastRoute(GameSpell spell)
    {
        if (spell.Targets == AreaEnemyTargetType && HasHarmAbility(spell))
            return QuestCastRoute.AreaEnemySweep;
        if (spell.Targets == PartyAreaTargetType && SpellHasTeleportDestination(spell))
            return QuestCastRoute.PartyTeleport;
        if (!IsOffensiveSpell(spell) && SpellHasImmediateBeneficialEffect(spell))
            return QuestCastRoute.Beneficial;
        if (spell.Duration > 0)
            return QuestCastRoute.DurationBuff;
        // A Duration-0 spell whose only effect is the ability-151 chain (e.g. "fear random" #1181 → rolls a fear
        // spell 1178-1180) must fire that follow-up, NOT fall to the triggered-damage branch which would read
        // its MinBase (the rolled spell id) as HP damage. DurationBuff is checked first so a jail-time spell
        // (Duration>0 + ability-151 cast-on-ending) keeps its buff route — its chain fires later, on expiry.
        if (spell.Abilities.ContainsKey(ChainSpellAbilityId))
            return QuestCastRoute.Chain;
        return QuestCastRoute.Triggered;
    }

    // Mirrors the final fall-through branch of ExecuteTriggeredSpellByIdAsync (CommandParser.Movement.cs):
    // a Triggered spell that is neither a teleport (141) nor a scripted-command (148) and has a damage-
    // shaped body (MinBase..MaxBase, AttType>0) subtracts that magnitude from the ACTOR's HP. The routing
    // audit uses this to assert no non-harm quest-cast spell lands here ("hits you for N damage" on a heal).
    internal static bool TriggeredCastWouldDamageActor(GameSpell spell)
        => !SpellHasTeleportDestination(spell)
           && !spell.Abilities.ContainsKey(ScriptedCommandAbilityId)
           && spell.MinBase > 0 && spell.MaxBase >= spell.MinBase && spell.AttType > 0;

    // Cast effect 148 (scripted command): the spell runs a
    // text block as special commands on the cast target. The
    // text-block id is the ability-148 slot value, or — when that value is 0 — the rolled magnitude
    // (MinBase..MaxBase), the same value-or-roll rule every effect case uses.
    // This is how the learnable utility spells take effect: illuminate (#2, slot 0 /
    // MinBase 4012 → block 4012 = "giveitem 1085") conjures a glowing "light ball"; clean (#1202 →
    // 4053) and the class trigger/filter spells run their own teleport/strip blocks. The player-cast
    // effect loop previously matched no ability id for these, so the spell silently did nothing.
    private async Task ApplyScriptedCommandSpellEffectAsync(GameSpell spell, Player target)
    {
        // Only the caster runs the block: the target of every learnable ability-148 spell is the
        // self-cast caster (Targets 1), and PerformTextBlockAsSpecialCommandAsync acts on this session's
        // player. A cross-target beneficial scripted spell (e.g. clean on another player) is a rare edge
        // the cross-session text-block runner can't service, so skip it rather than mis-target the caster.
        if (!ReferenceEquals(target, _player))
            return;

        int textBlockId = ResolveScriptedCommandTextBlock(spell);
        if (textBlockId <= 0)
            return;

        // The effect is purely the text block (illuminate #2 → block 4012 = "giveitem 1085"),
        // which drops an UNLIT "light ball" (ItemType 6) into the pack. Casting does NOT ready it —
        // the readied-light slot is only ever set by the
        // explicit LIGHT / USE command. So illuminate gives no light by itself; the player must then
        // `use light ball` (which is why MegaMUD scripts that command right after the cast). Do not
        // auto-light here.
        await PerformTextBlockAsSpecialCommandAsync(textBlockId);
    }

    // Scripted-command magnitude resolution: the ability-148 slot value names the text block directly, but
    // a slot value of 0 means "use the per-cast rolled magnitude" (MinBase..MaxBase with level scaling).
    // illuminate/clean/pastor-box carry 0 here and rely on MinBase for the block id. Returns 0 when the
    // spell carries no ability-148 slot (the caller then leaves the spell to its other effects).
    private int ResolveScriptedCommandTextBlock(GameSpell spell)
    {
        if (!spell.Abilities.TryGetValue(ScriptedCommandAbilityId, out int slotValue))
            return 0;

        return slotValue != 0 ? slotValue : RollSpellMagnitude(spell, _player.Level);
    }

    private const int DrainAbilityId = 8;           // leech HP from target to caster
    private const int RestoreManaAbilityId = 150;   // restore/burn mana
    private const int ReducePoisonAbilityId = 20;   // reduce poison level
    private const int DispelAbilityId = 73;         // strip buffs by ability id
    private const int CureStatusAbilityId = 81;     // strip 74/75 status buffs
    private const int RemoveCastSpellAbilityId = 122;   // remove a cast by spell number
    private const int RemoveEffectSpellAbilityId = 153; // remove an effect by spell number
    private const int RestoreEnergyAbilityId = 11;      // restore energy/stamina pool
    private const int TeachSpellAbilityId = 160;        // teach a spell (value = spell #)
    private const int SmiteAbilityId = 95;              // smite / instant-kill

    // Ability 11 (restore energy): roll the level-scaled magnitude, add to the energy pool. Returns the delta.
    private int? ApplyRestoreEnergyEffect(GameSpell spell, Player target, int? preRolledAmount = null)
    {
        if (spell.MaxBase < spell.MinBase)
            return null;

        int amount = preRolledAmount ?? RollSpellMagnitude(spell, _player.Level);
        return SpellEffects.RestoreEnergy(target, amount);
    }

    // Ability 95 (smite): a spell that kills outright rather than dealing rolled damage. A smite spell
    // need not carry MinBase/AttType (its lethality is the ability, not a damage roll), so the cast
    // router treats any hostile spell carrying ability 95 as offensive even when IsCombatSpell is false.
    private static bool SpellIsSmite(GameSpell spell) => spell.Abilities.ContainsKey(SmiteAbilityId);

    // Spell targeting ("Targets"): 8 = single enemy, 12 = area enemy (both offensive);
    // 2 = single ally (defaults to self); 4 = single target whose intent is set by its effect
    // (turn/disrupt/exorcism carry a harm ability ⇒ offensive; godheal/charm don't ⇒ beneficial);
    // everything else (1/3/5/6/7/9/10/11/13/0) is self/ally. Offensiveness keys on the target type and
    // harm content, NOT on AttType — AttType is the elemental/visual type and is shared by self-buffs
    // (the KAI "ways" use AttType 4), so the old `AttType > 0` test mis-routed self-buffs and group
    // heals as attacks.
    private const int SingleEnemyTargetType = 8;      // Targets 8
    private const int AreaEnemyTargetType = 12;       // Targets 12
    private const int SelfOnlyTargetType = 1;         // Targets 1 (self-only: KAI "ways", armour spells, potions)
    private const int BeneficialOtherTargetType = 2;  // Targets 2 (single ally / self)
    private const int PartyAreaTargetType = 13;       // Targets 13 (caster + every party member in the room)

    // A monster's spell is routed by its target-type field: the
    // self/ally types — Targets 1 (the KAI "ways" like way of the tiger #38, armour spells, potions) and
    // Targets 2 (self-haste: speed/frenzy/combat fury) — buff the CASTER, while the enemy types (0
    // breath/touch/curse, 8 single, 12 area) land on the player. Summon spells (ability 12) also carry
    // Targets 1 but are reinforcement calls, not self-buffs, so they're excluded and fall to the summon path.
    private static bool MonsterSpellTargetsSelf(GameSpell spell)
        => (spell.Targets is SelfOnlyTargetType or BeneficialOtherTargetType)
            && !spell.Abilities.ContainsKey(SummonAbilityId);

    // damage 1 / drain 8 / elemental 17 / poison 19 / smite 95
    private static readonly int[] HarmAbilityIds = { 1, 8, 17, 19, 95 };
    internal static bool HasHarmAbility(GameSpell spell) => HarmAbilityIds.Any(spell.Abilities.ContainsKey);

    // Ability 18 "Alter HP" — the heal ability. Spells carrying it roll MinBase..MaxBase as
    // HP restored (mend, minor/major/greater healing, godheal, healing rain, …). A spell that carries
    // neither a harm ability nor this heal ability stores an UNRELATED magnitude in MinBase/MaxBase
    // (e.g. blur's AC bonus, illuminate's item ref), so its "damage" columns are meaningless.
    internal const int HealHpAbilityId = 18;
    internal static bool SpellHasHpRoll(GameSpell spell)
        => HasHarmAbility(spell) || spell.Abilities.ContainsKey(HealHpAbilityId);

    // A harm spell (ability 1 damage / 8
    // drain) with a Duration is damage-OVER-TIME, NOT an instant hit. The gate is on the raw Duration
    // field: ==0 → roll + subtract HP instantly; !=0 → a timed slot (the slot
    // stores the rolled magnitude + duration) with NO instant damage, then the spell upkeep
    // ticks `magnitude` HP per medium tick until it expires (silently — the visible "You are on fire!"
    // line comes from the source RE-casting each tick, see reference_dot_message_model). Poison (19) is
    // excluded here — it uses the separate PoisonLevel accumulator. Elemental (17)/smite (95) are
    // instant-only (no per-tick upkeep case).
    private static readonly int[] DotHarmAbilityIds = { 1, 8 };
    internal static bool IsDurationDamageSpell(GameSpell spell)
        => spell.Duration > 0 && DotHarmAbilityIds.Any(spell.Abilities.ContainsKey);

    // On each (re)application the affected
    // TARGET is shown the spell's ability-115 message Line3 — the ongoing/applied status line
    // ("You are on fire!", "You feel ill.", "You are blinded!") — IN ADDITION to the cast line. This is
    // what makes a DoT visible per round: a monster re-casts its DoT every combat round, so the victim
    // sees this line each round (the silent HP upkeep tick in between is the stock behavior). Returns the
    // formatted Line3, or null when the spell has no usable status line (no ability 115, the 66 silent
    // sentinel, blank Line3, or a %d damage template). Line1 of the same message is the wear-off line
    // shown at termination (ResolveSpellWearOffMessage). See reference_dot_message_model.
    private const int OngoingStatusMessageAbilityId = 115;   // "Descriptive Message"
    private string? ResolveSpellOngoingStatusLine(GameSpell spell, string targetName)
    {
        if (spell.Abilities.TryGetValue(OngoingStatusMessageAbilityId, out int descMsgId)
            && descMsgId > 0
            && _world.Database.Messages.TryGetValue(descMsgId, out var descMsg)
            && !string.IsNullOrWhiteSpace(descMsg.Line3)
            && !descMsg.Line3.Contains("%d", System.StringComparison.Ordinal))
        {
            return FormatLegacyMessage(descMsg.Line3, targetName, spell.Name, targetName);
        }

        return null;
    }

    private static bool SpellTargetsEnemyType(GameSpell spell)
        => spell.Targets is SingleEnemyTargetType or AreaEnemyTargetType;

    private static bool IsOffensiveSpell(GameSpell spell)
        => SpellTargetsEnemyType(spell)
            || (IsCombatSpell(spell) && HasHarmAbility(spell))
            || (spell.Targets != BeneficialOtherTargetType && (SpellIsSmite(spell) || spell.Abilities.ContainsKey(ShatterWeaponAbilityId)));

    // The spells a single-target cast's remove list (122 / 153) would strip from the target, in the
    // cast's slot order. firstMatchOnly = the self-cast rule (stop at the first active match).
    private static List<int> FindStockCastRemovals(GameSpell spell, Player target, bool firstMatchOnly)
    {
        var stripped = new List<int>();
        foreach (var (abilityId, spellNumber) in spell.AbilitySlots)
        {
            if (abilityId is not (RemoveCastSpellAbilityId or RemoveEffectSpellAbilityId) || spellNumber == 0)
                continue;
            if (!target.HasActiveSpell(spellNumber) || stripped.Contains(spellNumber))
                continue;

            stripped.Add(spellNumber);
            if (firstMatchOnly)
                break;
        }
        return stripped;
    }

    // Dispel-by-spell-number (abilities 122 / 153): remove the target's active buff whose spell
    // number equals the ability value, then recompute derived stats. (122 "remove cast" vs 153
    // "remove effect" differ only in the termination-routine flag; C# reverses the buff's stat
    // effects via RecalculatePlayerStats either way.) Used e.g. by cure-poison #19 (ability 122 value 794).
    private void ApplyDispelBySpellNumberEffect(Player target, int spellNumber)
    {
        if (spellNumber == 0)
            return;

        // Capture the slot before removal so a poison contribution is reversed on the way out (the
        // termination upkeep), matching the by-ability dispel path. Antidote carries this form too
        // (ability 122 → spells 794/798).
        var active = target.ActiveSpells.FirstOrDefault(s => s.SpellId == spellNumber);
        if (!target.RemoveActiveSpell(spellNumber))
            return;

        if (active != null && _world.Database.Spells.TryGetValue(spellNumber, out var source))
            SpellEffects.ReverseTerminatedPoison(target, source, active.CastLevel);

        _world.RecalculatePlayerStats(target);

        // Early-removal termination: show the removed spell's ability-115 Line1 wear-off line to the
        // target (e.g. antidote 794/798 removing a poison → "The effects of the poison wear off!").
        SendDispelWearOffsToPlayer(target, [spellNumber]);
    }

    // A monster's enemy-targeted spell that carries a non-damage utility ability (remove-spell 122/153,
    // dispel 73, cure-status 81) — the monster analogue of the player beneficial dispatch. Used to
    // route such a spell away from the direct-damage path (see ResolveMonsterAttackSpell).
    internal static bool SpellCarriesUtilityAbility(GameSpell spell)
        => spell.Abilities.ContainsKey(RemoveCastSpellAbilityId)
        || spell.Abilities.ContainsKey(RemoveEffectSpellAbilityId)
        || spell.Abilities.ContainsKey(DispelAbilityId)
        || spell.Abilities.ContainsKey(CureStatusAbilityId);

    // Audit contract for MonsterSpellRoutingAuditTests (the monster-side analogue of the quest routing
    // audit). True when a spell a monster routes through ResolveMonsterAttackSpell (an AtkType-2 attack
    // cast, a non-scripted AtkHitSpell, or an area DeathSpell/CreateSpell fanned out per-target) carries
    // NO harm ability (1/8/17/19/95) yet still escapes every non-damage gate — summon (12), ability-151 chain-
    // carrier, player-debuff, remove/dispel/cure utility — and so would land its positive MinBase as bogus
    // HP loss. This is the "The shambling mound's remove engulfs hits you for 234 damage!" (#235) /
    // "guardsman's calls for aid hits you for 0 damage!" (#135) class. The gate ORDER mirrors the branches
    // in ResolveMonsterAttackSpell, and it composes the SAME predicates those branches use (no rule
    // duplication), so a future hand-authored monster spell that would mis-route fails the build.
    internal static bool MonsterAttackSpellMisroutesAsDamage(GameSpell spell)
        => spell.MinBase > 0
            && !HasHarmAbility(spell)
            && !MonsterSpellSummonsReinforcement(spell)
            && !spell.Abilities.ContainsKey(ChainSpellAbilityId)
            && !spell.Abilities.ContainsKey(ScriptedCommandAbilityId)   // 148 runs a textblock, not damage
            && !MonsterSpellIsPlayerDebuff(spell)
            && !SpellCarriesUtilityAbility(spell);

    // Apply a monster utility spell's effects to the player target (no HP damage). The headline case is
    // the shambling mound's death "remove engulfs" (235 → ability 122 removes the engulf 234), but this
    // also covers a monster dispelling/curing a player. A removed spell surfaces its own wear-off line
    // (e.g. engulf 234's ability-115 message 365 "You are freed!"); if nothing speaks, fall back to the
    // spell's own CastMsgB, else stay silent — never the bogus "hits you for N damage" line.
    private void ApplyMonsterUtilitySpellOnTarget(GameSpell spell, Player target, string casterName, List<string> playerMessages, List<string> roomMessages)
    {
        bool spoke = false;
        bool statsDirty = false;

        // AbilitySlots, not Abilities — a utility spell can carry the same remove/dispel ability in several
        // slots (e.g. a multi-target "remove forms"); the dictionary would keep only the last. See the note
        // in ApplyImmediateBeneficialEffects.
        foreach (var (abilityId, abilityValue) in spell.AbilitySlots)
        {
            switch (abilityId)
            {
                case RemoveCastSpellAbilityId:       // 122 — remove a cast by spell number
                case RemoveEffectSpellAbilityId:     // 153 — remove an effect by spell number
                    if (abilityValue > 0 && target.RemoveActiveSpell(abilityValue))
                    {
                        statsDirty = true;
                        string? wearOff = ResolveDispelledSpellWearOffLine(abilityValue, target.Name);
                        if (!string.IsNullOrWhiteSpace(wearOff))
                        {
                            playerMessages.Add($"{MudAnsi.BrightBlue}{wearOff}{MudAnsi.Reset}");
                            spoke = true;
                        }
                    }
                    break;
                case DispelAbilityId:                // 73 — strip buffs by ability id
                    ApplyDispelEffect(target, abilityValue);
                    break;
                case CureStatusAbilityId:            // 81 — strip paralysis/disease status buffs
                    ApplyCureStatusEffect(target);
                    break;
            }
        }

        if (statsDirty)
            _world.RecalculatePlayerStats(target);

        if (!spoke && spell.CastMessageB > 0 && _world.Database.Messages.TryGetValue(spell.CastMessageB, out var msg))
        {
            string playerLine = FormatLegacyMessage(msg.Line2, casterName, spell.Name, target.Name);
            string roomLine = FormatLegacyMessage(msg.Line3, casterName, spell.Name, target.Name);
            if (!string.IsNullOrWhiteSpace(playerLine))
                playerMessages.Add(GameAnsi.SpellHostile(playerLine));
            if (!string.IsNullOrWhiteSpace(roomLine))
                roomMessages.Add(GameAnsi.SpellHostile(roomLine));
        }
    }

    // The wear-off line of a dispelled spell — its ability-115 message Line1 (skipping
    // %d-bearing templates, which aren't plain wear-off text). Returns null when none resolves.
    private string? ResolveDispelledSpellWearOffLine(int spellId, string targetName)
    {
        const int descMsgAbilityId = 115;
        if (_world.Database.Spells.TryGetValue(spellId, out var s)
            && s.Abilities.TryGetValue(descMsgAbilityId, out int msgId) && msgId > 0
            && _world.Database.Messages.TryGetValue(msgId, out var m)
            && !string.IsNullOrWhiteSpace(m.Line1)
            && !m.Line1.Contains("%d", StringComparison.Ordinal))
        {
            return FormatLegacyMessage(m.Line1.Trim(), targetName);
        }

        return null;
    }

    // Drain (ability 8): a leech spell adds the damage it deals straight onto the caster's HP,
    // clamped to the caster's max (subtract from the target, add to the caster,
    // then clamp the caster to its max). The caster gains the
    // full value regardless of overkill; the gain is silent — the spell's own cast message conveys it.
    // Returns the HP actually gained (0 when the spell isn't a drain).
    private int ApplyDrainToCaster(GameSpell spell, int damageDealt)
    {
        if (damageDealt <= 0 || !spell.Abilities.ContainsKey(DrainAbilityId))
            return 0;

        return SpellEffects.ApplyHeal(_player, damageDealt);
    }

    // Reduce poison (ability 20): roll the level-scaled magnitude, lower the poison level (floor 0).
    private int? ApplyReducePoisonEffect(GameSpell spell, Player target, int? preRolledAmount = null)
    {
        if (spell.MaxBase < spell.MinBase)
            return null;

        int amount = preRolledAmount ?? RollSpellMagnitude(spell, _player.Level);
        return SpellEffects.ReducePoison(target, amount);
    }

    // Shared active-spell scan: drop every slot whose source spell satisfies `shouldRemove`, then
    // recompute derived stats if anything went away. Mirrors the slot-walk in cure/dispel.
    private List<int> RemoveActiveSpellsBySource(Player target, System.Func<GameSpell, bool> shouldRemove)
    {
        var toRemove = target.ActiveSpells
            .Where(active => _world.Database.Spells.TryGetValue(active.SpellId, out var source) && shouldRemove(source))
            .ToList();

        foreach (var active in toRemove)
        {
            target.ActiveSpells.Remove(active);
            // Spell-termination upkeep: removing a poison spell by dispel/cure must reverse its poison
            // contribution — the same as natural expiry — or the accumulator lingers after the
            // active spell is gone, so the cure only "sort of" works (bug #112).
            if (_world.Database.Spells.TryGetValue(active.SpellId, out var source))
                SpellEffects.ReverseTerminatedPoison(target, source, active.CastLevel);
        }

        if (toRemove.Count > 0)
            _world.RecalculatePlayerStats(target);

        return toRemove.Select(a => a.SpellId).ToList();
    }

    // An EARLY removal (cure/antidote/dispel — not a
    // natural-expiry tick) still shows each removed spell's ability-115 Line1 wear-off line to the target
    // ("The effects of the poison wear off!"); it just does NOT fire the spell's ability-151 cast-on-ending (that
    // is gated on the natural-expiry flag, which only ProcessActiveSpellUpkeep passes). reprompt:false so a
    // multi-buff cure batches under the final cast message's prompt redraw. See reference_dot_message_model.
    private void SendDispelWearOffsToPlayer(Player target, IEnumerable<int> removedSpellIds)
    {
        foreach (int spellId in removedSpellIds)
        {
            string? wearOff = ResolveDispelledSpellWearOffLine(spellId, target.Name);
            if (!string.IsNullOrWhiteSpace(wearOff))
                // Same wear-off color as natural expiry: ESC[0;33m Yellow/brown,
                // not BrightBlue. Dispel/cure route through the same termination print.
                _world.SendToPlayer(target.Name, $"{MudAnsi.Yellow}{wearOff}{MudAnsi.Reset}", reprompt: false);
        }
    }

    // Cure status (ability 81): strip the target's active buffs sourced from spells carrying
    // ability 74/75 (paralysis/disease), then recompute. (The poison level is cured by ability 20.)
    private List<int> ApplyCureStatusEffect(Player target)
        => RemoveActiveSpellsBySource(target, SpellEffects.IsPoisonOrDisease);

    // Dispel (ability 73): remove the target's active buffs whose source spell carries
    // `abilityToStrip` (-1 strips all), then recompute derived stats. Mirrors the slot
    // scan in the cast path. Returns the removed spell ids (for wear-off display).
    private List<int> ApplyDispelEffect(Player target, int abilityToStrip)
        => RemoveActiveSpellsBySource(target, source => SpellEffects.ShouldDispel(source, abilityToStrip));

    // Heal (ability 18): roll the level-scaled magnitude (RollSpellMagnitude follows the
    // band-growth formula), apply ward/magic-resist, add to HP capped at max.
    // A non-null preRolledAmount skips the inner roll and reuses a magnitude rolled once at the cast
    // level — Targets=13 group casts roll once and apply the same value to every recipient (the
    // roll is stored once and reused for every target in the per-target loop).
    private int? ApplyHealEffect(GameSpell spell, Player target, int? preRolledAmount = null)
    {
        // For ability 18, Duration != 0 is a heal-OVER-TIME. That branch
        // adds a timed slot and touches HP for exactly 0 — the slot's per-tick heal is what
        // heals, and it runs in the active-spell upkeep (Player.ProcessActiveSpellUpkeep case 18;
        // ApplyBuffSpellIfDuration already registered the slot before we got here). Only Duration == 0
        // lands an instant heal. Regeneration / troll regeneration / cloak of faith etc. are HoT-only.
        if (spell.Duration != 0)
            return null;

        // NO raw MinBase/MaxBase legality test — stock has none, and those columns are the band
        // BEFORE per-level scaling, so they are routinely 0, negative, or inverted (godheal is
        // MinBase 35 / MaxBase 30). ComputeSpellMagnitudeBand does the stock min = min(min, max)
        // clamp, so the scaled band is always valid: godheal reads 45..70 at L20, 55..110 at L40.
        // The `MinBase <= 0 || MaxBase < MinBase` guard that used to sit here was a leftover from when
        // this rolled Random.Next(MinBase, MaxBase + 1) directly and had to avoid throwing; once the
        // roll moved to the scaled band the guard became a silent kill switch on 9 real heals —
        // godheal (#121/#1146, reported as "heals for 0 hp"), restoration (#1039, 600 HP at L20),
        // close wounds (#1043), merciful grace (#804), crescent moon staff (#738), annointed hands
        // (#744) and food (#316) — all of which returned null, so the cast line printed its %d as 0.
        int rolled = preRolledAmount ?? RollSpellMagnitude(spell, _player.Level);
        int healAmount = ApplyBeneficialMagicResistance(target, spell, rolled);
        return SpellEffects.ApplyHeal(target, healAmount);
    }

    // Restore mana (ability 150): roll the level-scaled magnitude, add to mana clamped to [0, max].
    // Beneficial casts carry a positive range; the negative (drain) form is offensive (later step).
    private int? ApplyRestoreManaEffect(GameSpell spell, Player target, int? preRolledAmount = null)
    {
        if (spell.MaxBase < spell.MinBase)
            return null;

        int amount = preRolledAmount ?? RollSpellMagnitude(spell, _player.Level);
        return SpellEffects.ApplyMana(target, amount);
    }

    private async Task SendBeneficialSpellMessagesAsync(GameSpell spell, Player target, int? amount)
    {
        bool selfTarget = ReferenceEquals(target, _player);
        string casterTargetName = selfTarget ? "yourself" : target.Name;

        // After the cast line, the TARGET also sees the spell's ability-115
        // Line3 "effect applied" status line — the buff analogue of "You are on fire!", e.g.
        // barkskin #34 → "You feel tough!", way of the tiger #38 → "You feel ferocious!". The offensive
        // path already emits this (ResolveSpellOngoingStatusLine callers); the beneficial self-buff path
        // dropped it, so duration buffs showed only the CastMessageB cast line. Emit it AFTER the cast
        // message on whichever exit path runs.
        string? ongoingStatus = ResolveSpellOngoingStatusLine(spell, target.Name);
        async Task EmitOngoingStatusAsync()
        {
            if (ongoingStatus == null)
                return;
            if (selfTarget)
                await _client.SendLineAsync(GameAnsi.SpellBeneficial(ongoingStatus));
            else
                await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellBeneficial(ongoingStatus)]);
        }

        if (_world.Database.Messages.TryGetValue(spell.CastMessageB, out var message))
        {
            string amountText = amount.GetValueOrDefault().ToString(CultureInfo.InvariantCulture);
            string casterLine = FormatLegacyMessage(message.Line1, spell.Name, casterTargetName, amount.GetValueOrDefault());
            string targetLine = FormatLegacyMessage(message.Line2, _player.Name, spell.Name, amount.GetValueOrDefault());
            string observerLine = FormatLegacyMessage(message.Line3, _player.Name, spell.Name, target.Name, amountText);

            if (!string.IsNullOrWhiteSpace(casterLine))
                await _client.SendLineAsync(GameAnsi.SpellBeneficial(casterLine));

            if (!selfTarget && !string.IsNullOrWhiteSpace(targetLine))
                await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellBeneficial(targetLine)]);

            if (!string.IsNullOrWhiteSpace(observerLine))
            {
                if (selfTarget)
                    BroadcastCombatMessagesToRoom([GameAnsi.SpellBeneficial(observerLine)]);
                else
                    await BroadcastCombatMessagesToObserversAsync([GameAnsi.SpellBeneficial(observerLine)], target);
            }

            await EmitOngoingStatusAsync();
            return;
        }

        if (amount.HasValue)
        {
            if (selfTarget)
            {
                await _client.SendLineAsync(GameAnsi.SpellBeneficial($"Your {spell.Name} heals you for {amount.Value} hit points."));
                await EmitOngoingStatusAsync();
                return;
            }

            await _client.SendLineAsync(GameAnsi.SpellBeneficial($"Your {spell.Name} heals {target.Name} for {amount.Value} hit points."));
            await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellBeneficial($"{_player.Name}'s {spell.Name} heals you for {amount.Value} hit points.")]);
            await BroadcastCombatMessagesToObserversAsync([GameAnsi.SpellBeneficial($"{_player.Name}'s {spell.Name} heals {target.Name} for {amount.Value} hit points.")], target);
            await EmitOngoingStatusAsync();
            return;
        }

        if (selfTarget)
        {
            await _client.SendLineAsync(GameAnsi.SpellBeneficial($"You cast {spell.Name}."));
            await EmitOngoingStatusAsync();
            return;
        }

        await _client.SendLineAsync(GameAnsi.SpellBeneficial($"You cast {spell.Name} on {target.Name}."));
        await SendCombatMessagesToPlayerAsync(target, [GameAnsi.SpellBeneficial($"{_player.Name} casts {spell.Name} on you.")]);
        await BroadcastCombatMessagesToObserversAsync([GameAnsi.SpellBeneficial($"{_player.Name} casts {spell.Name} on {target.Name}.")], target);
        await EmitOngoingStatusAsync();
    }

    private bool TryResolveBeneficialSpellTarget(string target, out Player spellTarget)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            spellTarget = _player;
            return true;
        }

        spellTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player)!;
        return spellTarget != null;
    }

    private int ApplyBeneficialMagicResistance(Player target, GameSpell spell, int amount)
    {
        if (amount <= 0 || spell.TypeOfResists <= 0 || !target.HasActiveAbility(_world.Database, 51))
            return amount;

        return Math.Max(1, amount - (target.MagicResist / 2));
    }

    // enforceRoundToken: when true, also apply the "one cast per round" gate. The afraid and
    // silence gates ALWAYS apply (afraid is checked before dispatch; every cast path checks
    // silence on every path). The round token is the part that differs by spell kind — see the caller.
    private bool CanCastSpellThisRound(bool enforceRoundToken, out string failureMessage)
    {
        // NOTE: smash-knockdown and HoldPerson do NOT block casting (movement-only gates).
        // Only fear (and confusion, handled per-action) interrupt casting.

        // Cast gate: a feared caster (the afraid flag, from an ability-60 fear buff) can't cast.
        if (_player.IsAfraid)
        {
            failureMessage = "You are too afraid!";
            return false;
        }

        // A silenced caster fails outright. Spellcasters are silenced by
        // ability 79 ("Your spell fails!"); KAI invokers by 159 ("Your power fails!").
        bool usesKai = _world.Database.Classes.TryGetValue(_player.ClassId, out var silenceCls) && Player.UsesKai(silenceCls);
        if (_player.HasActiveAbility(_world.Database, usesKai ? KaiSilencedAbilityId : SilencedAbilityId))
        {
            failureMessage = usesKai ? "Your power fails!" : "Your spell fails!";
            return false;
        }

        // One cast per combat round. This token is checked by the BENEFICIAL/utility cast
        // paths (spell match-type >= 3) and by a manual re-cast of
        // a hostile spell while already engaged — but NOT when a hostile spell OPENS combat (that path goes
        // straight to the autocombat engage). The caller passes enforceRoundToken=false for that opening case.
        if (enforceRoundToken && _player.HasCastThisRound)
        {
            // "You have already %s this round!" with the verb chosen by class type
            // ("cast a spell" / "invoked a power" for kai).
            // We hardcoded the caster wording, so a kai invoker got told they had cast a spell.
            failureMessage = usesKai
                ? "You have already invoked a power this round!"
                : "You have already cast a spell this round!";
            return false;
        }

        failureMessage = string.Empty;
        return true;
    }

    // A caster carrying a confusion debuff (ability 71, the confused
    // status) rolls 0..99 against the confusion magnitude; on a hit the cast is consumed with no
    // mana spent and no effect — the caster "looks around stupidly and does nothing" while the room sees
    // them foam at the mouth. Returns true when the cast fizzled.
    // Shared by cast, the auto-combat round, and movement: a confused actor (ability 71) rolls
    // 0..99 < magnitude and, on a hit, the action is consumed with no effect. The flavor is the
    // active confusion spell's Confuse Message (ability 101): Line1 → actor, Line2 (with %s = name) →
    // room. "Stun" spells carry msg 56 ("You are stunned!"); generic confusion carries 8489; absent ⇒
    // the hardcoded default. Magnitude comes from the aggregated ability (value-0 spells store their
    // rolled amount, so e.g. "song of stunning" reads ~100 = a near-certain lock).
    private async Task<bool> ConfusedActionConsumedAsync()
    {
        int magnitude = _player.GetActiveAbilityValue(_world.Database, ConfusionAbilityId);
        if (magnitude <= 0 || Random.Shared.Next(0, 100) >= magnitude)
            return false;

        var (selfLine, roomLine) = ResolveConfuseMessages();
        // Both confuse lines print with NO color (a plain write
        // / a bare literal), so they render in the default text color (White on palettes 0/1, Cyan on 2/3)
        // — NOT hostile/bright-red.
        await _client.SendLineAsync(GameAnsi.Neutral(selfLine, _player.PaletteId));
        BroadcastCombatMessagesToRoom([GameAnsi.Neutral(roomLine)]);
        return true;
    }

    private (string Self, string Room) ResolveConfuseMessages()
    {
        foreach (var active in _player.ActiveSpells)
        {
            if (!_world.Database.Spells.TryGetValue(active.SpellId, out var spell)
                || !spell.Abilities.ContainsKey(ConfusionAbilityId))
                continue;

            if (spell.Abilities.TryGetValue(ConfuseMessageAbilityId, out int msgId) && msgId > 0
                && _world.Database.Messages.TryGetValue(msgId, out var msg))
            {
                string self = string.IsNullOrWhiteSpace(msg.Line1) ? DefaultConfuseSelfMessage : msg.Line1.Trim();
                string room = (string.IsNullOrWhiteSpace(msg.Line2) ? DefaultConfuseRoomMessage : msg.Line2.Trim())
                    .Replace("%s", _player.Name);
                return (self, room);
            }
        }

        return (DefaultConfuseSelfMessage, DefaultConfuseRoomMessage.Replace("%s", _player.Name));
    }

    // Cast cost: deduct the spell's energy from the shared stamina pool
    // (clamped at 0), and consume the once-per-round cast token. Applies to every
    // cast. A full-pool cost (e.g. quake = 1000) drains the round; a 0-cost cast (e.g. rotting
    // flesh) leaves the pool intact so melee can continue — the "instant vs uses-a-round" behavior
    // is emergent from EnergyCost, not a spell category.
    private void SpendSpellCast(GameSpell spell)
    {
        _player.CurrentEnergy = Math.Max(0, _player.CurrentEnergy - spell.EnergyCost);
        _player.NextSpellAllowedAtUtc = _world.GetNextCombatPulseUtc();
    }

    // Beneficial / instant one-off casts also break melee autocombat — they
    // do not become a repeating per-round combat action.
    private async Task ConsumeSpellRoundAsync(GameSpell spell)
    {
        SpendSpellCast(spell);

        if (_player.InCombat)
        {
            // Casting any spell while inside autocombat ENDS it — every cast
            // branch (no-target / user / monster / item) drops autocombat and prints
            // the break line BEFORE the cast effect.
            // That prints "*Combat Off*" to the caster only (no room
            // "breaks off combat" broadcast). We stop the loop but previously skipped the message, so
            // the player silently dropped combat — MegaMud's parser never saw "*Combat Off*" and kept
            // treating the character as engaged. Emit it here (before the spell's own cast/effect line).
            _player.StopCombatLoop();
            ClearPendingCombatSpellSelection();
            await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
        }
    }

}
