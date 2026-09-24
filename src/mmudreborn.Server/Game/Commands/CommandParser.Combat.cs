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
    private void QueueMonsterAggro(MonsterInstance monster, List<MonsterInstance> activeAttackers, ref bool hadActiveAttackers)
    {
        if (monster.IsDead || activeAttackers.Contains(monster))
            return;

        // Idle monsters fast-tick clamp stamina to cap. Ensure no stale overshoot
        // carries into a new engagement when this monster wasn't already fighting anyone.
        if (monster.EngagedPlayerCount == 0)
            monster.RefillEnergyOutOfCombat();

        activeAttackers.Add(monster);                       // local batch-dedup copy
        _player.AddIncomingMonsterAttacker(monster);        // persist to the locked list
        // The combat-engagement contract is one monster at a time and is created by the player's
        // own command (attack/bs/punch/bash/…). An aggressive monster that aggros on us — including
        // one we walked into a fresh room with — registers as an incoming attacker so it can swing
        // at us, but it must NOT auto-fill the engaged-target slot. Doing so let a stale
        // PendingCombatRoundAction from a prior engagement carry over: e.g. "bs moaning spirit",
        // walk into a room with a skeletal warrior, and the queued backstab landed on the warrior
        // unprompted. Only assume aggro fills the target slot when the player isn't already in a
        // combat session; once InCombat is true, target changes come from the player.
        if (!_player.InCombat && (_player.CombatTarget == null || _player.CombatTarget.IsDead))
            _player.CombatTarget = monster;

        if (!hadActiveAttackers)
        {
            _player.NextMonsterAttackAtUtc = _world.GetNextCombatPulseUtc();
            if (!_player.InCombat)
                _player.NextAttackAllowedAtUtc = _player.NextMonsterAttackAtUtc;

            hadActiveAttackers = true;
        }
    }

    // Attack re-target: when this player's swing engages
    // a monster, roll to make the monster lock its single combat target (our LockedTargetName)
    // onto this player — the "attack last" mechanic that lets the most-recent attacker steal aggro. A
    // charmed/summoned creature serves its owner and never re-targets to an attacker (summoned
    // creatures are exempt); we gate that on the pet/summon ownership state. The faithful roll itself
    // lives in CombatEngine.ShouldRetargetToAttacker. Stage 3 (Item C): sets the lock the new
    // monster-centric attack pass reads; on the old per-player pass this has no observable effect.
    private void ApplyMonsterRetargetOnAttack(MonsterInstance monster)
    {
        if (monster.HasPlayerOwner || monster.IsSummonedCreature)
            return;

        int roll = Random.Shared.Next(1, 100);   // [1,99]; top EXCLUSIVE
        if (CombatEngine.ShouldRetargetToAttacker(
                monster.Template.Align, monster.Template.Group, monster.Template.FollowPercent,
                monster.HasLockedTarget, roll))
        {
            monster.SetLockedTarget(_player.Name);
        }
    }

    // As a NON-sneaking player leaves
    // a room, ONE eligible monster there takes a single free swing (rolled against its Active%). The
    // move still completes — the free attack only signals "cancel" when the swing changed
    // the player's lives (a kill), and movement separately re-checks held/stunned afterward. So we
    // abort the move only on death or a knockdown/hold/stun, never on a plain hit. Sneakers are exempt
    // (the sneak gate). Returns true when the move must be aborted (player stayed in the room).
    private async Task<bool> TryDepartingMonsterFreeAttackAsync(Room fromRoom)
    {
        if (_player.IsSysopInvisible || _player.IsSysopNoAggro)
            return false;

        // The stock monster swing refuses outright in a Protected room, so leaving a safe room draws no
        // free swing — the same hard gate that keeps every other monster attack out of it.
        if (fromRoom.IsProtected)
            return false;

        // A sneaking player is normally exempt from the departing free swing — EXCEPT
        // against a monster that can see through stealth (ability 57, See Hidden). Stock
        // keeps such a monster (e.g. a Ghost) pursuing/acting on the sneaker, so it still gets its swing —
        // which engages it and lets the existing pursuit logic follow the sneaker out. Non-see-hidden
        // monsters remain fooled.
        bool onlySeeHiddenMayAttack = _player.IsSneaking;

        var attacker = SelectDepartingFreeAttacker(fromRoom, onlySeeHiddenMayAttack);
        if (attacker == null)
            return false;

        // The free swing also engages the monster, so it registers as an incoming attacker and the
        // existing pursuit logic can follow the player into the next room.
        var activeAttackers = GetActiveIncomingMonsterAttackers();
        bool hadActiveAttackers = activeAttackers.Count > 0;
        QueueMonsterAggro(attacker, activeAttackers, ref hadActiveAttackers);

        var result = CombatEngine.MonsterAttack(
            attacker, _player, _world.Database.Items, _world.Database.Messages,
            (spellId, castLevel) => ResolveMonsterAttackSpell(attacker, spellId, castLevel),
            CombatEngine.BuildRetaliationSources(_player, _world.Database));
        await SendCombatMessagesToCurrentPlayerAsync(result.Messages);
        BroadcastCombatMessagesToRoom(result.RoomMessages);

        // The free swing IS the stock monster attack, so it ends the same way every monster swing
        // does: the lock roll (a 1-99 roll under FollowPercent copies this player's name into the monster's
        // target slot). A monster locked on you attacks you whenever you share a room — no alignment
        // test — and chases you. So an Outlaw (EP 40-79) whom an align-6 duergar never attacks on sight
        // still gets chased after the swing it takes at them on the way out; only EP 80+ skips the swing.
        _world.ApplyMonsterPostAttackLock(attacker, _player.Name);

        if (result.TargetKilled || _player.CurrentHP <= Player.DeathHP)
        {
            await HandlePlayerDeath();
            return true;  // a free swing that kills you cancels the move (lives changed)
        }

        // Movement re-check after the free attack: held ("You can't seem to move
        // anywhere!") or smash-knocked-down ("You are too stunned to move anywhere!")
        // cancels the move. Both are movement-only gates.
        if (IsMovementBlockedByStatus)
        {
            await _client.SendLineAsync(GetMovementBlockMessage());
            return true;
        }

        return false;  // plain hit or miss — the move proceeds
    }

    // One roll of 0..99; the first room monster whose aggression %
    // is >= the roll AND whose alignment makes it eligible takes the swing. The aggression % is the
    // monster's FollowPercent field — NOT the legacy Active field, which is an
    // enabled/in-game flag (~0) and left this mechanic dead (players ran through unharmed). FollowPercent
    // is the same value that governs pursuit and the most-recent-attacker re-target. Townsfolk / peaceful
    // / guards (align 0/3/4) and summoned (Group 37) monsters only swing if already LOCKED onto the
    // player; aggressive mobs swing locked-or-not; evil NPCs (align 6) are skipped against players whose
    // EvilPoints are >= 80. One swing per move.
    private MonsterInstance? SelectDepartingFreeAttacker(Room fromRoom, bool onlySeeHidden)
    {
        int roll = Random.Shared.Next(0, 100);   // [0,99]; top EXCLUSIVE

        foreach (var monster in _world.GetMonstersInRoom(fromRoom.MapNumber, fromRoom.RoomNumber))
        {
            if (monster.IsDead)
                continue;
            // A sneaking player is invisible to monsters without See Hidden — only a see-hidden monster
            // (e.g. a Ghost) may take the free swing at the departing sneaker.
            if (onlySeeHidden && !CombatEngine.MonsterCanSeeHidden(monster.Template))
                continue;
            // Charm exemptions: your own pet (charm-named +
            // owner match) never free-swings you, and a freshly summoned/charmed monster
            // hasn't oriented yet — neither takes the departing swing.
            if (monster.IsCharmFresh || monster.IsOwnedBy(_player.Name))
                continue;
            // Gang-house safe passage (ability 185): a guardian never free-swings a player holding its
            // emblem. Skip it here too so it is not even queued as an aggressor (stock gates at the swing,
            // but selecting it would leave the player needlessly "engaged" with a monster that can't hit).
            if (CombatEngine.PlayerHoldsMonsterPacifier(monster.Template, _player))
                continue;
            if (roll > monster.Template.FollowPercent)
                continue;

            // The passive-side test is the monster's target-name slot holding THIS player (the lock) —
            // not "this player has ever hit it", which never expires.
            if (CombatEngine.IsEligibleDepartingFreeAttacker(monster.Template.Align, monster.Template.FollowPercent,
                    monster.IsLockedOnTarget(_player.Name), (int)_player.EvilPoints, roll, monster.Template.Group))
                return monster;
        }

        return null;
    }

    private List<MonsterInstance> GetActiveIncomingMonsterAttackers()
    {
        _player.RemoveIncomingMonsterAttackersWhere(monster =>
            monster.IsDead ||
            monster.MapNumber != _player.CurrentMapNumber ||
            monster.RoomNumber != _player.CurrentRoomNumber);

        if (_player.CombatTarget != null &&
            (_player.CombatTarget.IsDead ||
             _player.CombatTarget.MapNumber != _player.CurrentMapNumber ||
             _player.CombatTarget.RoomNumber != _player.CurrentRoomNumber))
        {
            _player.CombatTarget = null;
        }

        return _player.SnapshotIncomingMonsterAttackers();
    }

    private async Task HandleBreak()
    {
        // BREAK independently breaks rest, meditate, autocombat,
        // dragging AND sneaking, emitting a message for whichever state warrants one. It does NOT gate
        // these together — a sneaking, non-combat player still gets "You are no longer sneaking."
        // rather than a no-op. Process each break, then fall back to "no effect" only if nothing applied.
        bool brokeSomething = false;

        // The rest and meditate flags are cleared unconditionally
        // and silently — no message, but the player does stand up / stop meditating.
        if (_player.IsResting || _player.IsMeditating)
        {
            _player.IsResting = false;
            _player.IsMeditating = false;
            brokeSomething = true;
        }

        // BREAK removes YOU from the autocombat list, ending the loop
        // you ENGAGED (melee or a repeating combat spell) regardless of whether your target is still
        // alive / present / in the room. _player.InCombat is set ONLY on player-initiated engage (attack a
        // monster/player, area combat cast) — never when a monster merely attacks you — so gating on it
        // alone is faithful: if you engaged combat you can always break out of it, whether or not the
        // other side is still pressuring you. The old "live target / incoming-attacker pressure" gate left
        // a player who engaged a now-dead/fled/departed target stuck in their loop with `break` a no-op
        // (bug #97). Break ends only YOUR loop; it does not stop monsters from attacking you (their loop).
        if (_player.InCombat)
        {
            _player.StopCombatLoop();
            _player.RemoveIncomingMonsterAttackersWhere(monster => !CombatEngine.CanMonsterRetaliate(monster.Template));
            await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
            brokeSomething = true;
        }

        // The dragging flag is cleared and "You are no longer dragging <name>." printed
        if (_world.TryStopDraggingForDragger(_player, out var dragBreakMessage))
        {
            await _client.SendLineAsync(dragBreakMessage);
            brokeSomething = true;
        }

        // The sneaking flag is cleared and "You are no longer sneaking." printed
        if (_player.IsSneaking)
        {
            _player.IsSneaking = false;
            await _client.SendLineAsync("You are no longer sneaking.");
            brokeSomething = true;
        }

        if (!brokeSomething)
            await _client.SendLineAsync("Your command had no effect.");
    }

    private async Task<bool> RestartCombatLoopIfReissuingSameMonsterTargetAsync(MonsterInstance monster)
    {
        if (!_player.InCombat || _player.CombatTarget != monster)
            return false;

        _player.StopCombatLoop();
        await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
        return true;
    }

    private async Task<bool> RestartCombatLoopIfReissuingSamePlayerTargetAsync(Player target)
    {
        if (!_player.InCombat || _player.PlayerCombatTarget != target)
            return false;

        _player.StopCombatLoop();
        await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
        return true;
    }

    private async Task EngageCombatAsync(MonsterInstance monster, PlayerCombatRoundAction openingAction, bool announceEngaged)
    {
        DateTime now = DateTime.UtcNow;
        DateTime scheduledRound = _player.NextMonsterAttackAtUtc > now
            ? _player.NextMonsterAttackAtUtc
            : _world.GetNextCombatPulseUtc(now);
        bool suppressRoomEngagedBroadcast = ShouldSuppressStealthBackstabEngagedBroadcast(openingAction);

        // Attacking an innocent monster (align 0 townsfolk / align 4 lawful guards,
        // shopkeepers, the barmaid) is an evil act, charged ONCE when you open combat on a monster
        // not already fighting you (the open-combat target gate) — never per swing,
        // and never when retaliating against a monster that aggroed YOU first. The lawful/warning gate
        // (CanAttackMonster → CanCommitLawfulEvilAction) has already cleared us by this point.
        if (!IsMonsterAlreadyFightingMe(monster))
        {
            float monsterEpGain = CombatEngine.GetEPCostForMonsterAttack(_player, monster.Template);
            if (monsterEpGain > 0)
                await AddEvilPointsWithCloudAsync(monsterEpGain);
        }

        monster.MarkPlayerEngaged(_player.Name);
        ApplyMonsterRetargetOnAttack(monster);
        _player.InCombat = true;
        _player.CombatTarget = monster;
        _player.PlayerCombatTarget = null;
        _player.AddIncomingMonsterAttacker(monster);
        _player.PendingCombatRoundAction = openingAction;
        _player.NextMonsterAttackAtUtc = scheduledRound;
        _player.NextAttackAllowedAtUtc = scheduledRound;
        _player.IsResting = false;
        // BACKSTAB / ATTACK do NOT clear stealth — the sneak/hidden flag is
        // dropped inside the swing itself (the attack calculation), not the moment the command is
        // queued. So `bs k` followed by an immediate `u` flee leaves the player still sneaking on
        // the move (the swing never fired). Stock-game capture: "bs k" → "*Combat Engaged*" → "u"
        // → "Sneaking..." — the move clearly reads the post-bs sneak state as still active.
        // The stealth clear lives in ExecutePlayerCombatActionAsync where the round actually
        // fires.

        if (!announceEngaged)
            return;

        await _client.SendLineAsync(GameAnsi.CombatEngaged("*Combat Engaged*"));
        if (!suppressRoomEngagedBroadcast)
            BroadcastCombatMessagesToRoom([GameAnsi.CombatEngaged($"{_player.Name} moves to attack {monster.DisplayName}.")]);
    }

    private async Task EngagePvpCombatAsync(Player target, PlayerCombatRoundAction openingAction, bool announceEngaged)
    {
        DateTime now = DateTime.UtcNow;
        DateTime scheduledRound = _player.NextMonsterAttackAtUtc > now
            ? _player.NextMonsterAttackAtUtc
            : _world.GetNextCombatPulseUtc(now);
        bool suppressRoomEngagedBroadcast = ShouldSuppressStealthBackstabEngagedBroadcast(openingAction);

        // Evil points for initiating PvP are charged once, here, when a new aggression opens the
        // retaliation window — not on every combat round. The evil-point rule owns that decision: a
        // continued strike inside our own window is free, self-defence is free and opens nothing, and a
        // ROBBING edge is charged in full (a rob buys no free follow-up swing).
        await ApplyPvpAggressionEvilAsync(target);

        _player.InCombat = true;
        _player.CombatTarget = null;
        _player.PlayerCombatTarget = target;
        _player.PendingCombatRoundAction = openingAction;
        _player.NextMonsterAttackAtUtc = scheduledRound;
        _player.NextAttackAllowedAtUtc = scheduledRound;
        _player.IsResting = false;
        // See EngageCombatAsync above: sneak clears in the swing, not on the queued command,
        // so an immediate flee after a bs leaves the player still stealthed.

        // In PvP, ONLY the attacker enters autocombat (the engage is
        // called for the aggressor alone). The victim is NOT auto-engaged — stock leaves them passive
        // (they take hits) until they manually attack back; the open retaliation window then lets that
        // counter-attack land without an evil-points cost. Auto-retaliating the victim was non-stock.
        // Being attacked still breaks the victim out of rest.
        target.IsResting = false;

        if (!announceEngaged)
            return;

        await _client.SendLineAsync(GameAnsi.CombatEngaged("*Combat Engaged*"));

        // A backstab from stealth strikes silently (a type-4 attack shows the
        // attacker *Combat Engaged* but sends the victim/room no "moves to attack" notice). A normal
        // opening attack announces to the victim and the room — never "*Combat Engaged*" to the victim.
        if (!suppressRoomEngagedBroadcast)
        {
            await SendCombatMessagesToPlayerAsync(
                target,
                [GameAnsi.CombatEngaged($"{_player.Name} moves to attack you!")]);
            await BroadcastCombatMessagesToObserversAsync(
                [GameAnsi.CombatEngaged($"{_player.Name} moves to attack {target.Name}!")],
                target);
        }
    }

    // A stealth surprise/backstab round hides its engagement from the victim + room (stock):
    // the attacker alone sees *Combat Engaged* and the target learns of it only when the hit lands. A
    // Backstab opening is always silent here — it is reached only for a REAL backstab (backstab-capable
    // weapon / unarmed) or, when SURPRISEROUND is ON, the non-backstab-weapon surprise round. The OFF case
    // is dispatched as a plain normal Attack (HandleBackstab), so it never reaches this branch and the
    // victim is warned by the normal "moves to attack" path.
    private bool ShouldSuppressStealthBackstabEngagedBroadcast(PlayerCombatRoundAction openingAction)
        => openingAction == PlayerCombatRoundAction.Backstab && (_player.IsSneaking || _player.IsHidden);

    private bool CanAttackPlayer(Player target, out string failureMessage)
    {
        if (!CanInitiateHostileAction(out failureMessage))
            return false;

        if (target.IsSysopInvisible || target.IsSysopNoAggro || target.IsOutOfRealm)
        {
            failureMessage = "Your command had no effect.";
            return false;
        }

        if (target == _player)
        {
            failureMessage = "Your command had no effect.";
            return false;
        }

        // Bug #186 (+ clarification): a hidden player you can't see is untargetable. The stock gate in
        // the PvP attack path — target hidden AND the
        // attacker lacks See Hidden — returns "You do not see X here!" with NO
        // combat/retaliation exemption. Hiding is only possible once you are OUT of combat (you can't hide
        // while engaged or being attacked), so "backstab → break → hide" is a legitimate PvP escape: the
        // victim must SEARCH to reveal the hider before they can strike back. Only See Hidden bypasses it.
        // This is the FIRST target gate (matching stock), so a hidden target leaks nothing — not the
        // level-range hint, the "already dead"/name line, the EP-cap block, nor the warn-on-evil prompt.
        if (target.IsHidden && !_player.HasSeeHidden)
        {
            failureMessage = $"You do not see {target.Name} here!";
            return false;
        }

        if (target.CurrentHP <= Player.DeathHP)
        {
            failureMessage = $"{target.Name} is already dead.";
            return false;
        }

        bool alreadyMutualCombat = _player.PlayerCombatTarget == target || target.PlayerCombatTarget == _player;
        bool isRetaliation = HasActivePvpRetaliationAgainst(target);
        int levelDifference = Math.Abs(_player.Level - target.Level);
        if (!alreadyMutualCombat && !isRetaliation && levelDifference > _world.PvpLevelRange)
        {
            failureMessage = $"You may only attack players within {_world.PvpLevelRange} levels of you.";
            return false;
        }

        // EP-cap action block (stock): the PvP evil-point path returns 1 when the
        // attacker is over the cap, and the attack caller aborts on that return — so a maxed-evil
        // player can't INITIATE evil aggression on a non-evil player (target EP < 30, the
        // inner gate). Retaliation/self-defense is never an evil act, so it's exempt. Cap-independent of
        // the gain (already refused past the cap), like the monster path; EVILCAPBLOCK toggles it.
        if (_world.EvilCapBlocksActions && _player.EvilPoints > Player.EvilPointGainCap
            && !isRetaliation && target.EvilPoints < 30f)
        {
            failureMessage = EvilCapBlockMessage;
            return false;
        }

        float epCost = CombatEngine.GetEPCostForPlayerAttack(_player, target, isRetaliation);
        if (!CanCommitLawfulEvilAction(epCost, out failureMessage))
            return false;

        return true;
    }

    private bool CanAttackMonster(MonsterInstance monster, out string failureMessage)
    {
        if (!CanInitiateHostileAction(out failureMessage))
            return false;

        // The evil-act gate is
        // SKIPPED ENTIRELY when the monster is already fighting this player — the short-circuit OR on
        // `sameas(monster.opponentName, player.name)` / `monster.target == player`. So RETALIATION
        // against an aggressor (e.g. a guard, align 4, that aggroed a criminal) is never barred by the
        // EP cap or the lawful "feeling of guilt" gate, and earns no fresh EP. Only INITIATING on an
        // innocent (align 0/4) monster that is NOT yet fighting you runs the gate. Without this a maxed
        // (EP>300) evil player jumped by a guard was a sitting duck — blocked from swinging back.
        bool retaliation = IsMonsterAlreadyFightingMe(monster);

        if (!retaliation && IsBlockedByEvilCap(monster.Template.Align, out failureMessage))
            return false;

        float epCost = retaliation ? 0f : CombatEngine.GetEPCostForMonsterAttack(_player, monster.Template);
        return CanCommitLawfulEvilAction(epCost, out failureMessage);
    }

    // The monster is currently fighting THIS player — its opponent-name buffer or
    // target index points at us. True both when WE engaged it (HasEngagedPlayer) and
    // when IT aggroed us (QueueMonsterAggro put it on our incoming-attacker list without taking the
    // engaged-target slot). Either way the swing-back is retaliation, not a fresh evil act.
    private bool IsMonsterAlreadyFightingMe(MonsterInstance monster)
        => monster.HasEngagedPlayer(_player.Name) || _player.HasIncomingMonsterAttacker(monster);

    // Ability 146: monster is protected by another monster type. When the protected
    // monster is attacked (melee or spell), a living protector in the room intercepts the attack.
    // The strings "%s moves to protect %s" (melee) and (spell) — note the raw bytes
    // are bare text ending in CR with NO trailing period and NO color token, so it is
    // emitted in the prompt's DEFAULT text color (White on palettes 0/1, Cyan on
    // 2/3), not the fabricated green we used to send (and without the period we used to append).
    private const int GuardianAbilityId = 146;

    private MonsterInstance ResolveMonsterProtection(MonsterInstance target)
    {
        int protectorTemplateId = target.Template.Abilities.GetValueOrDefault(GuardianAbilityId);
        if (protectorTemplateId <= 0)
            return target;

        var roomMonsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        var protector = roomMonsters.FirstOrDefault(m =>
            m != target && !m.IsDead && m.Template.Number == protectorTemplateId);

        if (protector == null)
            return target;

        string msg = GameAnsi.Neutral($"{protector.DisplayName} moves to protect {target.DisplayName}");
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, msg);
        return protector;
    }

    private Item? GetEquippedWeapon()
    {
        if (_player.Equipment.TryGetValue("weapon", out int weaponId))
            return _world.Database.Items.GetValueOrDefault(weaponId);

        return null;
    }

    // Magic-immunity gate: a monster's "Magical" requirement
    // (ability 28) is met by the attacker's Hit Magic (ability 142 — already aggregating
    // worn gear, including the wielded weapon's own 142) PLUS the wielded weapon's Magical level
    // (ability 28) covering the REMAINDER. The two ADD: a Mag-2 character wielding a Mag-3 weapon clears
    // a Mag-5 monster. An unarmed / martial strike has no weapon level to add, so only the character's
    // Hit Magic counts. (We previously took the MAX of those instead of summing, which wrongly blocked a
    // valid weapon, and collapsed every reason into one generic "Your attack has no effect!" line.)
    private const int MonsterMagicRequirementAbilityId = 28;   // "Magical"
    private const int HitMagicAbilityId = 142;                 // "Hit Magic"

    private static bool IsArmedWeaponAction(PlayerCombatRoundAction action)
        => action is PlayerCombatRoundAction.Attack or PlayerCombatRoundAction.Backstab
            or PlayerCombatRoundAction.Bash or PlayerCombatRoundAction.Smash;

    private bool CanAttackMonsterWithAction(MonsterInstance monster, PlayerCombatRoundAction action, out string failureMessage)
    {
        if (!CanAttackMonster(monster, out failureMessage))
            return false;

        // Stock order: the required-to-hit weapon-class gate (ability 158) is checked first, with its own
        // distinct message, BEFORE the magic-level gate below.
        if (!CanWeaponActionSatisfyRequiredToHit(monster.Template, action))
        {
            failureMessage = "You are not using the required weapon to hit this monster!";
            return false;
        }

        int required = monster.Template.Abilities.GetValueOrDefault(MonsterMagicRequirementAbilityId);
        if (required <= 0)
            return true;

        int hitMagic = _player.GetActiveAbilityValue(_world.Database, HitMagicAbilityId);
        if (hitMagic >= required)
            return true;

        int remaining = required - hitMagic;
        var weapon = IsArmedWeaponAction(action) ? GetEquippedWeapon() : null;
        if (weapon != null)
        {
            if (weapon.Abilities.GetValueOrDefault(MonsterMagicRequirementAbilityId) >= remaining)
                return true;

            failureMessage = "Your weapon has no effect against this monster!";
            return false;
        }

        // Unarmed / martial strike: a kick reports "feet", any other bare strike "fists"
        // ("lightning feet" + 10 → "feet").
        string limb = action is PlayerCombatRoundAction.Kick or PlayerCombatRoundAction.Jumpkick ? "feet" : "fists";
        failureMessage = $"Your {limb} have no effect against this monster!";
        return false;
    }

    private bool CanWeaponActionSatisfyRequiredToHit(Monster monster, PlayerCombatRoundAction action)
    {
        if (!monster.Abilities.TryGetValue(RequiredToHitAbilityId, out int requiredValue))
            return true;

        if (action is not (PlayerCombatRoundAction.Attack or PlayerCombatRoundAction.Backstab or PlayerCombatRoundAction.Bash or PlayerCombatRoundAction.Smash))
            return false;

        var weapon = GetEquippedWeapon();
        return weapon?.Abilities.TryGetValue(RequiredToHitAbilityId, out int weaponValue) == true
            && weaponValue == requiredValue;
    }

    private bool TryResolveMonsterTemplate(string input, out Monster? monster, out string failureMessage)
    {
        monster = null;
        failureMessage = string.Empty;

        string target = input.Trim();
        if (target.Length == 0)
        {
            failureMessage = "Syntax: SYSOP SPAWN <monster-id|name> [count]";
            return false;
        }

        if (int.TryParse(target, out var monsterId))
        {
            if (_world.Database.Monsters.TryGetValue(monsterId, out monster))
                return true;

            failureMessage = $"Monster {monsterId} was not found.";
            return false;
        }

        monster = _world.Database.Monsters.Values
            .FirstOrDefault(candidate => candidate.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (monster != null)
            return true;

        var matches = _world.Database.Monsters.Values
            .Where(candidate => TargetNameMatcher.MatchesWordPrefix(candidate.Name, target))
            .OrderBy(candidate => candidate.Number)
            .ToList();

        if (matches.Count == 0)
        {
            failureMessage = $"No monster matches '{target}'.";
            return false;
        }

        if (matches.Count == 1)
        {
            monster = matches[0];
            return true;
        }

        string preview = string.Join(", ", matches.Take(6).Select(candidate => $"{candidate.Name} (#{candidate.Number})"));
        if (matches.Count > 6)
            preview += ", ...";

        failureMessage = $"Please be more specific. Matching monsters: {preview}";
        return false;
    }

    private async Task<CombatResult> ExecutePlayerCombatActionAsync(MonsterInstance monster)
    {
        var cls = _world.Database.Classes[_player.ClassId];
        Item? weapon = null;
        if (_player.Equipment.TryGetValue("weapon", out int weapId))
            weapon = _world.Database.Items.GetValueOrDefault(weapId);

        var action = _player.PendingCombatRoundAction == PlayerCombatRoundAction.None
            ? PlayerCombatRoundAction.Attack
            : _player.PendingCombatRoundAction;

        if (!CanAttackMonsterWithAction(monster, action, out var failureMessage))
        {
            _player.StopCombatLoop();
            if (ReferenceEquals(_player.CombatTarget, monster))
                _player.CombatTarget = null;

            var failureResult = new CombatResult();
            failureResult.Messages.Add(GameAnsi.CombatReject(failureMessage, _player.PaletteId));
            failureResult.Messages.Add(GameAnsi.CombatOff("*Combat Off*"));
            await SendCombatMessagesToCurrentPlayerAsync(failureResult.Messages);
            return failureResult;
        }

        // Stealth (sneak + hidden) clears at swing time inside the attack calculation — once you
        // actually swing, your cover is blown. The cmd_* entry points only QUEUE the attack; the
        // flag is dropped here when the round actually fires, not back in EngageCombatAsync.
        _player.IsSneaking = false;
        _player.IsHidden = false;

        // A spiked monster (ability 72) damages the player on each landing melee swing.
        var monsterRetaliation = CombatEngine.BuildRetaliationSources(monster, _world.Database);

        // Normal weapon attack: resolve swing-by-swing so a weapon proc fires inside each swing and an
        // proc-landed kill cuts the round off exactly (see ResolveNormalWeaponRoundVsMonsterAsync). Engage
        // BEFORE resolving so the experience-recipient set is complete even if a mid-round proc lands the
        // kill. The other actions (backstab single-swing, bash/smash, punch/kick/jumpkick — none of which
        // multi-swing-proc vs a monster) keep the pre-rolled path + interleaved send.
        if (action == PlayerCombatRoundAction.Attack)
        {
            monster.MarkPlayerEngaged(_player.Name);
            ApplyMonsterRetargetOnAttack(monster);

            var (normalResult, deathHandledByProc) = await ResolveNormalWeaponRoundVsMonsterAsync(monster, weapon, cls, monsterRetaliation);

            _player.PendingCombatRoundAction = monster.IsDead ? PlayerCombatRoundAction.None : PlayerCombatRoundAction.Attack;

            // Melee kill: rewards were populated into normalResult, run the death once. Proc kill: the
            // proc cast already ran HandleMonsterDeath (exp credited to all engaged players) — do not
            // re-run it (would be a no-op via the death-processing latch, but also avoids a zero-reward
            // double pass).
            if (normalResult.TargetKilled && !deathHandledByProc)
                await HandleMonsterDeath(monster, normalResult);

            return normalResult;
        }

        CombatResult result = action switch
        {
            PlayerCombatRoundAction.Backstab => CombatEngine.PlayerAttack(_player, monster, cls, weapon, _world.Database.Messages, isBackstab: true, db: _world.Database, defenderRetaliation: monsterRetaliation, surpriseRoundEnabled: _world.SurpriseRoundEnabled),
            PlayerCombatRoundAction.Punch => CombatEngine.MysticAttack(_player, monster, cls, "punch", _world.Database.Messages, monsterRetaliation),
            PlayerCombatRoundAction.Kick => CombatEngine.MysticAttack(_player, monster, cls, "kick", _world.Database.Messages, monsterRetaliation),
            PlayerCombatRoundAction.Jumpkick => CombatEngine.MysticAttack(_player, monster, cls, "jumpkick", _world.Database.Messages, monsterRetaliation),
            PlayerCombatRoundAction.Bash => CombatEngine.BashAttack(_player, monster, cls, weapon, _world.Database.Messages, _world.Database, defenderRetaliation: monsterRetaliation),
            PlayerCombatRoundAction.Smash => CombatEngine.BashAttack(_player, monster, cls, weapon, _world.Database.Messages, _world.Database, CombatEngine.AttackType.Smash, monsterRetaliation),
            _ => CombatEngine.PlayerAttack(_player, monster, cls, weapon, _world.Database.Messages, db: _world.Database, defenderRetaliation: monsterRetaliation),
        };

        monster.MarkPlayerEngaged(_player.Name);
        ApplyMonsterRetargetOnAttack(monster);

        // Only backstab (type 4) is reset to normal attack mid-loop.
        // Bash/smash/punch/kick/jumpkick all persist until the player issues a new
        // command, combat ends, or the target dies. Mirroring that here keeps `smash` (and `bash`)
        // sticky across rounds instead of degrading to a regular swing after one hit.
        _player.PendingCombatRoundAction = result.TargetKilled
            ? PlayerCombatRoundAction.None
            : (action == PlayerCombatRoundAction.Backstab ? PlayerCombatRoundAction.Attack : action);

        // Swing lines and weapon spell-procs are emitted interleaved (each proc fires inside its
        // swing's hit branch). When the round itself killed the target, no proc was queued past the
        // killing blow and the dead-target guard inside skips any earlier-swing procs, so this collapses
        // to a plain swing-line flush before HandleMonsterDeath.
        await SendCombatResultWithProcsAsync(result, monster, null);

        if (result.TargetKilled)
            await HandleMonsterDeath(monster, result);

        return result;
    }

    // Drive a NORMAL weapon attack round swing-by-swing so a weapon spell-proc fires INSIDE each swing
    // (a weapon proc fires at the per-swing hit branch) and a proc that lands the
    // killing blow cuts the round off immediately — the exact stock kill behaviour. Versus the old
    // pre-rolled batch this fixes three things:
    //   (a) no further swings roll after any kill, so a dead monster deals no extra spike retaliation;
    //   (b) no swing line ever prints after the death / experience output;
    //   (c) a proc kill routes through ExecuteOffensiveSpellAgainstMonsterAsync → HandleMonsterDeath,
    //       which credits experience to EVERY engaged player (GetMonsterExperienceRecipients), so the
    //       historical "a proc lands the kill and party members silently lose exp credit" bug cannot
    //       reappear — the killer identity never gates the award.
    // Returns the accumulated round result (TargetKilled set on any kill) and whether a proc already ran
    // death handling, so the caller neither double-handles the death nor re-awards experience.
    private async Task<(CombatResult Result, bool DeathHandledByProc)> ResolveNormalWeaponRoundVsMonsterAsync(
        MonsterInstance monster, Item? weapon, CharacterClass cls, IReadOnlyList<CombatEngine.RetaliationSource>? monsterRetaliation)
    {
        var result = new CombatResult();
        var profile = CombatEngine.BuildNormalWeaponSwingProfileVsMonster(_player, monster, cls, weapon, _world.Database.Messages, _world.Database);

        int casterSent = 0, roomSent = 0;
        bool firedAnyProc = false;
        bool deathHandledByProc = false;

        for (int index = 0; index < profile.Swings; index++)
        {
            var (procSpellId, attackerDied) = CombatEngine.ResolveNormalWeaponSwingVsMonster(
                _player, monster, weapon, profile, result, _world.Database.Messages, monsterRetaliation);

            // Flush this swing's freshly-appended lines (hit/miss + any spike retaliation) before the proc.
            if (result.Messages.Count > casterSent)
            {
                await SendCombatMessagesToCurrentPlayerAsync(result.Messages.GetRange(casterSent, result.Messages.Count - casterSent));
                casterSent = result.Messages.Count;
            }
            if (result.RoomMessages.Count > roomSent)
            {
                BroadcastCombatMessagesToRoom(result.RoomMessages.GetRange(roomSent, result.RoomMessages.Count - roomSent));
                roomSent = result.RoomMessages.Count;
            }

            if (monster.IsDead)
            {
                // Melee blow killed: populate drops/exp into THIS result; the caller runs HandleMonsterDeath once.
                CombatEngine.ApplyMonsterDeathRewards(result, monster);
                break;
            }

            if (procSpellId > 0 && _world.Database.Spells.TryGetValue(procSpellId, out var procSpell))
            {
                // The proc reuses the manual-cast path, which writes its lines directly via
                // _client.SendLineAsync. This round runs on the combat pulse, so the player may be
                // mid-command — hold the proc output so it defers onto the typed-ahead queue instead of
                // trampling the half-typed line (the swing lines already defer via
                // SendCombatMessagesToCurrentPlayerAsync). No-op when the player isn't typing.
                _client.BeginHeldOutput();
                try
                {
                    await ExecuteOffensiveSpellAgainstMonsterAsync(procSpell, monster, keepAutoCombatSpellSelected: false);
                }
                finally
                {
                    _client.EndHeldOutput();
                }
                firedAnyProc = true;
                if (monster.IsDead)
                {
                    // Proc landed the kill: ExecuteOffensiveSpellAgainstMonsterAsync already ran
                    // HandleMonsterDeath (exp credited to all engaged players). Stop here — exact cutoff.
                    result.TargetKilled = true;
                    deathHandledByProc = true;
                    break;
                }
            }

            if (attackerDied)
                break;
        }

        // A proc's damage line is a bare send carrying no trailing prompt; redraw so the session is not
        // left looking frozen (mirrors SendCombatResultWithProcsAsync) — only when a proc fired, the
        // monster survived, and no command loop will reprompt. Skip while the player is mid-typing: the
        // proc lines were deferred (held output), and the deferred queue redraws on its own flush, so a
        // bare prompt here would itself trample the in-progress command.
        if (firedAnyProc && !deathHandledByProc && !monster.IsDead && !_player.SuppressBroadcastReprompt && !_client.HasPendingInput)
            await _client.SendAsync(MudAnsi.Prompt(_player));

        return (result, deathHandledByProc);
    }

    private async Task<CombatResult> ExecutePlayerCombatActionAgainstPlayerAsync(Player target)
    {
        var cls = _world.Database.Classes[_player.ClassId];
        Item? weapon = null;
        if (_player.Equipment.TryGetValue("weapon", out int weapId))
            weapon = _world.Database.Items.GetValueOrDefault(weapId);

        var action = _player.PendingCombatRoundAction == PlayerCombatRoundAction.None
            ? PlayerCombatRoundAction.Attack
            : _player.PendingCombatRoundAction;

        // Per-round maintenance: the evil-point verdict runs on EVERY attack, not just the
        // opening one, so run the same verdict here. It is normally free — the opening strike left a
        // plain self→target edge (no evil charged) and this only refreshes the window. It is NOT
        // always free: a rob landed mid-feud rewrites that edge with the robbing flags, and the next
        // swing is then charged in full and restores the plain edge.
        await ApplyPvpAggressionEvilAsync(target);

        // Stealth clears at swing time, not at engage time (see ExecutePlayerCombatActionAsync).
        _player.IsSneaking = false;
        _player.IsHidden = false;

        // The parser has already settled the evil points for this swing (with the "dark cloud" line the
        // engine's silent TryAddEvilPoints cannot emit), so tell the engine the charge is done.
        const bool evilPointsAlreadyCharged = true;
        var result = CombatEngine.PlayerAttackPlayer(_player, target, cls, weapon, _world.Database.Messages, evilPointsAlreadyCharged, action, _world.Database, CombatEngine.BuildRetaliationSources(target, _world.Database), surpriseRoundEnabled: _world.SurpriseRoundEnabled);

        // See ExecutePlayerCombatActionAsync above: only backstab (type 4) resets → normal (5)
        // between rounds; bash/smash/punch/kick/jumpkick stay sticky.
        _player.PendingCombatRoundAction = result.TargetKilled
            ? PlayerCombatRoundAction.None
            : (action == PlayerCombatRoundAction.Backstab ? PlayerCombatRoundAction.Attack : action);

        // Swing lines (to attacker, target, and observers) interleaved with weapon spell-procs — see
        // SendCombatResultWithProcsAsync. Collapses to a plain flush when the round killed the target.
        await SendCombatResultWithProcsAsync(result, null, target);

        if (result.TargetKilled)
        {
            _player.ClearCombatState();
            await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.CombatOff("*Combat Off*")]);

            var targetSession = _world.GetClientForPlayer(target.Name)?.Session;
            if (targetSession != null)
            {
                await targetSession.HandleExternalPlayerDeathAsync();
            }
            else
            {
                target.ClearCombatState();
            }
        }

        return result;
    }

    private async Task HandleAttack(string target)
    {
        if (await IsTooAfraidToAttackAsync())
            return;

        if (string.IsNullOrEmpty(target))
        {
            // ATTACK with no target: break-and-restart autocombat. Re-engage
            // the current target, or do nothing at all when there's none — never "Attack what?".
            await RestartAutocombatWithoutTargetAsync(PlayerCombatRoundAction.Attack);
            return;
        }

        var monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
        if (monster != null)
        {
            await EngageMonsterAttackAsync(monster, PlayerCombatRoundAction.Attack);
            return;
        }

        var playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);
        if (playerTarget == null)
        {
            await _client.SendLineAsync($"Your command had no effect.");
            return;
        }

        await EngagePvpAttackAsync(playerTarget, PlayerCombatRoundAction.Attack);
    }

    // Afraid gate (from a fear buff, ability 60): a feared player can't start
    // any attack ("You are too afraid!"). Shared by attack/backstab/bash/smash.
    private async Task<bool> IsTooAfraidToAttackAsync()
    {
        // NOTE: smash-knockdown and HoldPerson do NOT block attacking (movement-only gates),
        // so there is no knockdown/root check here — a knocked-down/rooted player still swings (the smash
        // defenseless AV/DV penalty is applied in the marshal). Only fear (and confusion) interrupt.
        if (!_player.IsAfraid)
            return false;

        await _client.SendLineAsync("You are too afraid!");
        return true;
    }

    // Shared engage-monster epilogue used by attack/bash/smash/punch/kick/jumpkick after each
    // verb's own target-resolution and weapon-check logic. Runs: ResolveMonsterProtection →
    // CanAttackMonsterWithAction hostility gate → ClearPendingCombatSpellSelection → restart-
    // detection → EngageCombatAsync. Always emits either the hostility-failure line or the
    // (re-)engage; caller just `return`s after calling.
    private async Task EngageMonsterAttackAsync(MonsterInstance monster, PlayerCombatRoundAction action)
    {
        monster = ResolveMonsterProtection(monster);

        if (!CanAttackMonsterWithAction(monster, action, out var hostilityFailureMessage))
        {
            // Must carry the LinePreamble (via CombatReject), NOT a raw SendLineAsync. Sent mid-combat-
            // round, a bare reject line with no \x1b[79D\x1b[K framing crashes Megamud's combat parser
            // ("Crash detected in game parsing routines"). This mirrors the in-round twin's CombatReject.
            await _client.SendLineAsync(GameAnsi.CombatReject(hostilityFailureMessage, _player.PaletteId));
            return;
        }

        bool alreadyMutualCombat = _player.InCombat && _player.CombatTarget == monster;
        ClearPendingCombatSpellSelection();
        bool restartedCombat = await RestartCombatLoopIfReissuingSameMonsterTargetAsync(monster);
        await EngageCombatAsync(monster, action, announceEngaged: restartedCombat || !alreadyMutualCombat);
    }

    // PvP counterpart of EngageMonsterAttackAsync: CanAttackPlayer gate → restart-detection →
    // EngagePvpCombatAsync. Same shape — every melee verb's PvP tail looked exactly like this.
    private async Task EngagePvpAttackAsync(Player playerTarget, PlayerCombatRoundAction action)
    {
        if (!CanAttackPlayer(playerTarget, out var failureMessage))
        {
            // CombatReject (LinePreamble-framed), never raw — see EngageMonsterAttackAsync.
            await _client.SendLineAsync(GameAnsi.CombatReject(failureMessage, _player.PaletteId));
            return;
        }

        bool alreadyPvpCombat = _player.InCombat && _player.PlayerCombatTarget == playerTarget;
        ClearPendingCombatSpellSelection();
        bool restartedPvpCombat = await RestartCombatLoopIfReissuingSamePlayerTargetAsync(playerTarget);
        await EngagePvpCombatAsync(playerTarget, action, announceEngaged: restartedPvpCombat || !alreadyPvpCombat);
    }

    // A combat verb typed with no target argument: the command
    // simply breaks-and-restarts autocombat and returns
    // 1 (handled). With a live target that re-engages it (the *Combat Off* / *Combat Engaged*
    // flip); with no target the restart finds nothing and the command ends SILENTLY.
    // There is no "Attack what?" / "Backstab what?" / "There is no  here to …" string in the
    // stock — a bare combat verb with nothing to hit produces no output at all.
    private async Task RestartAutocombatWithoutTargetAsync(PlayerCombatRoundAction action)
    {
        if (_player.CombatTarget != null && !_player.CombatTarget.IsDead)
            await EngageMonsterAttackAsync(_player.CombatTarget, action);
        else if (_player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP)
            await EngagePvpAttackAsync(_player.PlayerCombatTarget, action);
    }

    // A non-attack action that
    // interrupts the player's swing loop (rest, meditate, open / picklock / bash a door) ends their
    // autocombat and prints "*Combat Off*" — but only when they were actually attacking
    // (only when actually inside autocombat). Stopping the loop does NOT clear the chosen target
    // or incoming attackers (that is a separate call). This form emits no "%s breaks off
    // combat." room broadcast, just the caller's own *Combat Off*.
    private async Task BreakAutocombatAsync()
    {
        if (!_player.InCombat)
            return;
        _player.StopCombatLoop();
        await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
    }

    private async Task HandleBackstab(string target)
    {
        if (await IsTooAfraidToAttackAsync())
            return;

        // BACKSTAB with no target: identical to a bare attack
        // (break-and-restart autocombat). Combat breaks stealth so backstab is off the table; the
        // current target is re-engaged with a normal swing, and with no target it ends silently —
        // there is no "Backstab what?" string in stock.
        if (string.IsNullOrEmpty(target))
        {
            await HandleAttack("");
            return;
        }

        if (!_player.IsSneaking && !_player.IsHidden)
        {
            // If not hidden or sneaking, treat as normal attack.
            await HandleAttack(target);
            return;
        }

        if (_player.InCombat)
        {
            await HandleAttack(target);
            return;
        }

        var monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
        if (monster != null)
        {
            monster = ResolveMonsterProtection(monster);

            // Non-backstab weapon (lacks the backstab ability): SURPRISEROUND ON → it still gets a full
            // backstab-damage surprise round (Backstab action, silent); OFF → it degrades to a plain
            // NORMAL attack (Attack action — normal damage, victim warned), and we say why. A real
            // backstab weapon / unarmed always backstabs. (See GameWorld.SurpriseRoundEnabled.)
            var monsterOpening = ResolveBackstabOpeningAction(GetEquippedWeaponForAttack());
            if (monsterOpening == PlayerCombatRoundAction.Attack)
                await _client.SendLineAsync($"{MudAnsi.White}You cannot backstab with this weapon.{MudAnsi.Reset}");

            if (!CanAttackMonsterWithAction(monster, monsterOpening, out var hostilityFailureMessage))
            {
                // CombatReject (LinePreamble-framed), never raw — see EngageMonsterAttackAsync.
                await _client.SendLineAsync(GameAnsi.CombatReject(hostilityFailureMessage, _player.PaletteId));
                return;
            }

            ClearPendingCombatSpellSelection();
            await EngageCombatAsync(monster, monsterOpening, announceEngaged: true);
            return;
        }

        var playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);
        if (playerTarget == null)
        {
            await _client.SendLineAsync($"You don't see {target} here.");
            return;
        }

        // Same gating as the monster path (non-backstab weapon: ON → surprise round, OFF → normal attack).
        var playerOpening = ResolveBackstabOpeningAction(GetEquippedWeaponForAttack());
        if (playerOpening == PlayerCombatRoundAction.Attack)
            await _client.SendLineAsync($"{MudAnsi.White}You cannot backstab with this weapon.{MudAnsi.Reset}");

        if (!CanAttackPlayer(playerTarget, out var failureMessage))
        {
            // CombatReject (LinePreamble-framed), never raw — see EngageMonsterAttackAsync.
            await _client.SendLineAsync(GameAnsi.CombatReject(failureMessage, _player.PaletteId));
            return;
        }

        ClearPendingCombatSpellSelection();
        await EngagePvpCombatAsync(playerTarget, playerOpening, announceEngaged: true);
    }

    // The `backstab` command's opening action for the equipped weapon. A backstab-capable weapon (or
    // unarmed) always backstabs. A non-backstab weapon gets a full surprise round (Backstab) only when
    // SURPRISEROUND is ON; otherwise the command degrades to a plain normal Attack (normal damage, the
    // victim is warned) — the "deny it" half of the toggle.
    private PlayerCombatRoundAction ResolveBackstabOpeningAction(Item? weapon)
        => CanUseWeaponForBackstab(weapon) || _world.SurpriseRoundEnabled
            ? PlayerCombatRoundAction.Backstab
            : PlayerCombatRoundAction.Attack;

    private Item? GetEquippedWeaponForAttack()
    {
        if (!_player.Equipment.TryGetValue("weapon", out int weaponId))
            return null;

        return _world.Database.Items.GetValueOrDefault(weaponId);
    }

    // With no weapon equipped an unarmed backstab is allowed;
    // an equipped weapon must carry ability 116. Weapon TYPE is irrelevant — daggers/short
    // swords AND longswords/broadswords carry it (the longsword's value is a negative AV penalty);
    // only true two-handers/poles/staves lack it. (The stock "two-handed weapons still get a shadow
    // backstab" exploit is the bug we intentionally do NOT replicate.)
    private static bool CanUseWeaponForBackstab(Item? weapon)
        => CombatEngine.WeaponEnablesBackstabDamage(weapon);

    private async Task HandleMysticAttack(string args, string attackType, string? fullCommand = null)
    {
        attackType = attackType.ToLowerInvariant();
        fullCommand ??= string.IsNullOrEmpty(args) ? attackType : $"{attackType} {args}";

        if (!HasMysticAttackAbility(attackType))
        {
            // JUMPKICK/KICK/PUNCH return "not handled" when
            // the caster lacks the mystic ability; the caller then re-tries
            // the raw input as a room exit / text command before giving up. So "jump west" at a bridge
            // crosses it even for a non-mystic, because the jumpkick verb yields and the text-command
            // exit ("jump west") matches. Mirror that fall-through here before the error.
            if (await TryHandleRoomAction(fullCommand))
                return;

            // KICK/PUNCH/JUMPKICK simply return 0 when the caster lacks the ability
            // (there is NO "you don't know the first thing about kicking/…" string — only smash/bash
            // have one). The dispatcher then prints the generic fall-through line. So a class without
            // the ability gets "Your command had no effect.", target present or not.
            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        var action = attackType switch
        {
            "kick" => PlayerCombatRoundAction.Kick,
            "jumpkick" => PlayerCombatRoundAction.Jumpkick,
            _ => PlayerCombatRoundAction.Punch,
        };

        // With the ability and no argument: break-and-restart autocombat,
        // re-engaging the current target (or nothing at all) — never "There is no  here to kick."
        if (string.IsNullOrEmpty(args))
        {
            await RestartAutocombatWithoutTargetAsync(action);
            return;
        }

        var monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, args);
        if (monster != null)
        {
            // Re-issuing a combat command at the same target prints *Combat Off* / *Combat Engaged*
            // for attack/bash; punch/kick/jumpkick must match so the action-switch is visible.
            await EngageMonsterAttackAsync(monster, action);
            return;
        }

        var playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, args, _player);
        if (playerTarget == null)
        {
            // A named target not in the room resolves to nothing → the
            // command returns 0 → the dispatcher yields to a matching room exit / text command (e.g.
            // "jump west" at a bridge), otherwise prints "Your command had no effect."
            if (await TryHandleRoomAction(fullCommand))
                return;

            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        await EngagePvpAttackAsync(playerTarget, action);
    }

    private bool HasMysticAttackAbility(string attackType)
    {
        return attackType switch
        {
            "kick" => _player.HasKick,
            "jumpkick" => _player.HasJumpkick,
            _ => _player.HasPunch,
        };
    }

    /// <summary>
    /// Bashing a door hurts you sometimes. BASH runs this roll AFTER the
    /// already-open / bashed / failed branch closes (the roll sits at the same brace depth as that
    /// if-else), so it applies to ALL THREE door outcomes — including simply walking into a door
    /// that was already open — and NOT to the pre-roll rejections or the monster-bash path:
    ///     if (roll of 0..99 &lt; 25)             // 25%; top EXCLUSIVE
    ///         dmg = roll of 1..3                // again top-exclusive
    ///         player HP -= dmg
    ///         print "You take %d damage for bashing the door!"
    /// The literal says "door" even when you bashed a GATE — the string takes only %d, there is no
    /// door-noun argument. It carries no colour of its own, appended to the same user buffer as the
    /// outcome line and flushed together.
    /// DEVIATION: stock does not check for death here — it leaves you sitting at &lt;= 0 HP until
    /// the next tick resolves it. Every other self-damage site in this engine settles death at the
    /// point of damage, and leaving a player alive at &lt;= 0 HP breaks that invariant, so we resolve
    /// it immediately. Same end state, just not deferred.
    /// </summary>
    internal static bool TryRollBashRecoilDamage(Random rng, out int damage)
    {
        damage = 0;
        if (rng.Next(0, 100) >= 25)      // roll of 0..99 < 25; top EXCLUSIVE
            return false;

        damage = rng.Next(1, 4);         // genrdn(1,4) = [1,3], top EXCLUSIVE
        return true;
    }

    private async Task ApplyBashRecoilDamageAsync()
    {
        if (!TryRollBashRecoilDamage(Random.Shared, out int damage))
            return;

        _player.CurrentHP -= damage;
        await _client.SendLineAsync(GameAnsi.Neutral($"You take {damage} damage for bashing the door!", _player.PaletteId));

        if (_player.CurrentHP <= 0)
            await HandlePlayerDeath();
    }

    private async Task HandleBash(string args)
    {
        if (await IsTooAfraidToAttackAsync())
            return;

        // BASH with no argument (just "bash"): break-and-restart autocombat
        // and return — BEFORE any door, ability, or weapon check. So a bare `bash` re-engages the
        // current target or, with nothing to hit, does nothing at all (no "you don't know"/"need a
        // weapon"/"there is no  here" message). It also does not switch the running attack type.
        if (string.IsNullOrWhiteSpace(args))
        {
            await RestartAutocombatWithoutTargetAsync(PlayerCombatRoundAction.Bash);
            return;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        var exit = room == null || string.IsNullOrWhiteSpace(args)
            ? null
            : _world.FindVisibleExit(_player, room, args);

        if (room != null && exit?.IsBarrierExit == true)
        {
            // Bashing a door/gate is a loud action — it breaks the basher's sneak/hide (success or fail).
            await BreakSneakAndHideForAction();

            // Once the argument resolves to an exit direction, the door branch
            // clears the sneak flag then breaks the basher's autocombat
            // BEFORE the bash roll — so it fires for every door outcome (bashed, failed,
            // already-open, can't-bash, or the delay-wait), not just a successful bash.
            await BreakAutocombatAsync();

            var outcome = _world.TryBashExit(_player, room, args, out var doorMessage);
            switch (outcome)
            {
                case BashExitOutcome.Bashed:
                    await _client.SendLineAsync(doorMessage);
                    _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                        GetBashExitObserverMessage(exit, _player.Name, succeeded: true), _client);
                    // A 2-tick action delay only on a real bash attempt (roll made), success or fail.
                    await ApplyActionDelayAsync(BashPicklockFastTicks);
                    break;

                case BashExitOutcome.Failed:
                    // NOT an error-red line. The room broadcast sits behind
                    // the bare line preamble ESC[79D ESC[K with NO SGR at all —
                    // flushed to the room, then "Your attempts to bash through fail!"
                    // is appended to the now-empty user buffer with no colour of its own. So it renders
                    // in the prompt's default text colour, not red. (BrightRed in this command belongs
                    // only to the two ability/weapon rejections, which sit behind a
                    // preamble + ESC[1;31m.)
                    await _client.SendLineAsync(GameAnsi.Neutral(doorMessage, _player.PaletteId));
                    _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                        GetBashExitObserverMessage(exit, _player.Name, succeeded: false), _client);
                    await ApplyActionDelayAsync(BashPicklockFastTicks);
                    break;

                case BashExitOutcome.AlreadyOpen:
                    // Carries its own White color — send as-is, no error red, no broadcast, no delay.
                    await _client.SendLineAsync(doorMessage);
                    break;

                default: // NotBashable — pre-roll rejection: no broadcast, no delay.
                    await _client.SendLineAsync(doorMessage.Contains('\x1b') ? doorMessage : MudAnsi.Error(doorMessage));
                    break;
            }

            if (outcome is BashExitOutcome.Bashed or BashExitOutcome.Failed or BashExitOutcome.AlreadyOpen)
                await ApplyBashRecoilDamageAsync();

            return;
        }

        if (!_player.HasBash)
        {
            // The no-ability string is "the first thing about bashing!".
            await _client.SendLineAsync(MudAnsi.Error("You don't know the first thing about bashing!"));
            return;
        }

        string target = args;

        MonsterInstance? monster = null;
        Player? playerTarget = null;
        if (!string.IsNullOrEmpty(target))
            monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
        else if (_player.CombatTarget != null)
            monster = _player.CombatTarget;

        if (monster == null)
        {
            if (!string.IsNullOrEmpty(target))
                playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);
            else if (_player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP)
                playerTarget = _player.PlayerCombatTarget;
        }

        if ((monster != null || playerTarget != null) && GetEquippedWeapon() is not { IsWeapon: true })
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You need a weapon to bash with!{MudAnsi.Reset}");
            return;
        }

        if (monster != null)
        {
            await EngageMonsterAttackAsync(monster, PlayerCombatRoundAction.Bash);
            return;
        }

        if (playerTarget == null)
        {
            // A named target not present in the room resolves to nothing →
            // the command returns 0 → the dispatcher falls through to room-exit/text matching and
            // otherwise prints "Your command had no effect." (no "There is no  here to bash." string).
            if (await TryHandleRoomAction($"bash {args}"))
                return;

            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        await EngagePvpAttackAsync(playerTarget, PlayerCombatRoundAction.Bash);
    }

    private async Task HandleSmash(string args)
    {
        if (await IsTooAfraidToAttackAsync())
            return;

        // SMASH (ability 32): a weapon power-attack (type 7, x5 damage). Unlike bash it
        // has no door/exit-breaking behaviour. The ability is checked FIRST, regardless of target —
        // so even a bare `smash` from a class without it prints the smashing message.
        if (!_player.HasSmash)
        {
            await _client.SendLineAsync(MudAnsi.Error("You don't know the first thing about smashing!"));
            return;
        }

        // With the ability and no argument: break-and-restart autocombat
        // (re-engage the current target, or nothing) — never "There is no  here to smash."
        if (string.IsNullOrWhiteSpace(args))
        {
            await RestartAutocombatWithoutTargetAsync(PlayerCombatRoundAction.Smash);
            return;
        }

        string target = args;

        MonsterInstance? monster = null;
        Player? playerTarget = null;
        if (!string.IsNullOrEmpty(target))
            monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
        else if (_player.CombatTarget != null)
            monster = _player.CombatTarget;

        if (monster == null)
        {
            if (!string.IsNullOrEmpty(target))
                playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);
            else if (_player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP)
                playerTarget = _player.PlayerCombatTarget;
        }

        if (monster != null)
        {
            // See HandleAttack / HandleBash: re-issue at the same target prints the Off/Engaged flip.
            await EngageMonsterAttackAsync(monster, PlayerCombatRoundAction.Smash);
            return;
        }

        if (playerTarget == null)
        {
            // Named target not in room → return 0 → dispatcher fall-through →
            // "Your command had no effect." (no "There is no  here to smash." string in stock).
            if (await TryHandleRoomAction($"smash {args}"))
                return;

            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        await EngagePvpAttackAsync(playerTarget, PlayerCombatRoundAction.Smash);
    }

    private async Task HandleUse(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync(MudAnsi.Error("Syntax: USE {Item to use} [{target}]"));
            return;
        }

        // The item lookup hands back the resolved item AND the uses left in
        // the player's own item slot, and an item found with 0 left is rejected right there — before
        // the use-eligibility check, before the worn/readied gate, before any light/target dispatch — with "There are
        // no more uses in %s.". It matters for a RetainAfterUses item, which is NOT
        // destroyed at 0 but sits spent until the daily cleanup recharges it: without this gate a spent
        // black flail #349 kept firing its use-spell for free and drove its counter negative (bug #218).
        // Unlimited items (UseCount -1/0) are never charge-checked, matching the charge-deduct skip.
        var chargedUseItem = ResolveCarriedUseItem(args, out _, out _);
        if (chargedUseItem != null
            && chargedUseItem.Value.Item.UseCount > 0
            && GetRemainingItemCharges(chargedUseItem.Value) <= 0)
        {
            await _client.SendLineAsync($"There are no more uses in {chargedUseItem.Value.Item.Name}.");
            return;
        }

        if (TryResolveUniqueUseLightSource(args, out string resolvedLightName, out var ambiguousLightNames))
        {
            await HandleLight(resolvedLightName);
            return;
        }

        if (ambiguousLightNames != null)
        {
            await ShowItemDisambiguationAsync(ambiguousLightNames);
            return;
        }

        // Items with ability 43 (One Time Cast) can be used to trigger their spell.
        if (await TryUseItemAbilityAsync(args))
            return;

        // USE and READ share the same no-target handler: a
        // Link-to-Spell (ability 42) learn scroll is learned via `use` exactly like `read`. Do this
        // before the key-on-door resolver so `use scroll of X` learns the spell instead of falling
        // through to "You can't use that there."
        if (await TryLearnSpellScrollAsync(args.Trim()))
            return;

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return;
        }

        if (!TryResolveKeyUse(args, room, out var exit, out var keyMatch, out var failureMessage, out var ambiguousKeyNames))
        {
            if (ambiguousKeyNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousKeyNames);
                return;
            }

            // TryResolveKeyUse yields "You can't use that there." only when the input named no visible
            // exit — i.e. this was a plain `use <item>`, never an item-on-door use. Mirror the stock
            // resolve-then-gate flow for a bare item that is not a light/cast/learn item:
            //   * a carried item still runs the use-eligibility check + the readied/worn gate; if
            //     it passes but has no use-effect, the handler returns 0 with no output → stay silent;
            //   * a name that is NOT carried tries a room special-command, then prints the
            //     stock "You don't have %s." (on the first argument word).
            // The item-on-exit key-miss message ("You do not have X.") is preserved for when an exit WAS named.
            if (failureMessage == "You can't use that there.")
            {
                var inertMatch = ResolveCarriedUseItem(args, out _, out var inertAmbiguousNames);
                if (inertAmbiguousNames != null)
                {
                    await ShowItemDisambiguationAsync(inertAmbiguousNames);
                    return;
                }

                if (inertMatch != null)
                {
                    // Carried: apply the stock gates. If it passes (worn/wielded/non-equippable) it simply
                    // has no use-effect, so we stay silent like the handler returning 0.
                    await TryApplyUseGatesAsync(inertMatch.Value);
                    return;
                }

                if (await TryHandleRoomAction($"use {args.Trim()}"))
                    return;

                string firstWord = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                await _client.SendLineAsync($"You don't have {firstWord}.");
                return;
            }

            await _client.SendLineAsync(failureMessage);
            return;
        }

        // USE runs its item gates BEFORE any target resolution: the item lookup
        // resolves the key whether worn or in the backpack (flag 4 = no worn/unworn filter), then
        // the use-eligibility check and the worn/readied gate fire, and only
        // then does it parse the direction and enter the no-target handler. So an unworn worn-TYPE key gets
        // "You must be wearing that item to use it!" whether or not it would have fit this lock — the
        // gate never sees the door. (Glowing red amulet #494 unlocks only while worn; carried keys
        // #177/#496 are Worn=0 and unaffected.) Shared with the non-key `use` paths.
        if (await TryApplyUseGatesAsync(keyMatch!.Value))
            return;

        // The no-target handler routes every "the direction HAS an exit, but this key does not open
        // it" case to one line: "The %s doesn't seem to fit that lock.", named after the
        // item used. That covers a non-door exit, a door with no lock, and a door keyed to a different
        // item — the final `else` after both the door (type 2) and gate (type 7/11) key tests.
        if (exit == null || !exit.IsBarrierExit || exit.RequiredKeyItemId <= 0
            || keyMatch.Value.ItemId != exit.RequiredKeyItemId)
        {
            await _client.SendLineAsync($"The {keyMatch.Value.Item.Name} doesn't seem to fit that lock.");
            return;
        }

        if (!_world.TryUnlockExit(_player, room, exit.Direction, out var message))
        {
            await _client.SendLineAsync(message);
            return;
        }

        await _client.SendLineAsync(message);

        // No room broadcast: the key branches print to the actor and never
        // broadcast. Only PICKLOCK announces ("You see %s pick the lock on the %s..."),
        // so a key unlock is silent to bystanders.

        // A successful key-on-door action runs
        // the charge deduction just like any other use-spell item. Limited-use keys (UseCount>0, e.g. the
        // single-use "dark temple key" #496) lose a charge and, at 0, are destroyed with the usual
        // "It's uses gone..." message; unlimited keys (UseCount 0) are no-ops here (bug #158).
        // ConsumeCarriedItemChargeAsync handles both an inventory key and a WORN key-item (e.g. the
        // single-use "glowing red amulet" #494, Worn slot 8) — auto-unequipping it when destroyed.
        await ConsumeCarriedItemChargeAsync(keyMatch.Value);
    }

    // Resolve "<item> [target]" from `args` exactly like the stock item lookup: chop
    // trailing words off the RIGHT one at a time and stop at the LONGEST leading word-run that names ANY
    // carried item (an exact name match wins within a length via NarrowToBestMatches). Stock resolves
    // the item FIRST and only afterwards decides whether it can be used, so we do NOT skip a matching
    // length just because that item carries no use-spell ("use white key s" must stop at the exact
    // "white key", not widen to the "white" prefix and collide with "white parchment deed"). A multi-word
    // item name still wins over a trailing target ("use silverwood staff bob" → item "silverwood staff",
    // target "bob"); a bare "use staff" leaves no target. Returns null when nothing carried matches and
    // sets `ambiguousNames` when the longest match is genuinely ambiguous (caller shows the list).
    private CarriedItemMatch? ResolveCarriedUseItem(string args, out string targetArg, out IReadOnlyList<string>? ambiguousNames)
    {
        ambiguousNames = null;
        targetArg = string.Empty;

        var words = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int take = words.Length; take >= 1; take--)
        {
            var matches = FindMatchingCarriedItems(string.Join(' ', words.Take(take)), includeEquipped: true, retryShorter: false);
            if (matches.Count == 0)
                continue;

            var distinctNames = matches.Select(m => m.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctNames.Count > 1)
            {
                ambiguousNames = distinctNames;
                return null;
            }

            targetArg = string.Join(' ', words.Skip(take));
            return matches[0];
        }

        return null;
    }

    // USE gates, run for EVERY resolved item before dispatch: the use-eligibility check
    // (class/race/level/alignment) → "You may not use that item!"; then the readied/worn requirement — a
    // weapon (ItemType 1) must be the wielded weapon and a worn-slot item (Worn>0) must actually be worn,
    // else "You must have that item readied / be wearing that item to use it." Non-equippable items
    // (potions ItemType 5, wands, scrolls, keys with Worn=0) are exempt and fire from the backpack.
    // (Bug #148: ItemType 5 was once grouped with weapons here, failing `use <potion>` from the pack.)
    // Returns true when a gate tripped (message sent, caller stops); false when the item passes.
    private async Task<bool> TryApplyUseGatesAsync(CarriedItemMatch carried)
    {
        var item = carried.Item;

        if (!CanPlayerUseItem(_player, item))
        {
            await _client.SendLineAsync("You may not use that item!");
            return true;
        }

        // Stock branches on Worn, NOT on item type: a Worn of 0 takes the readied
        // path (a weapon-type item must be the readied weapon), and ANY non-zero Worn takes the
        // worn path (item must occupy one of the 20 equipment slots). So an item that is both
        // weapon-typed and worn-slotted reports "wearing", not "readied". Both lines end in '!'
        // matching "You may not use that item!" above.
        if (item.Worn > 0)
        {
            if (!carried.IsEquipped)
            {
                await _client.SendLineAsync("You must be wearing that item to use it!");
                return true;
            }
        }
        else if (item.IsWieldedWeapon && !carried.IsEquipped)
        {
            await _client.SendLineAsync("You must have that item readied to use it!");
            return true;
        }

        return false;
    }

    private async Task<bool> TryUseItemAbilityAsync(string args)
    {
        // Resolve "<item> [target]" the way the stock item lookup does (see
        // ResolveCarriedUseItem). A resolved key/inert item (UseSpellId<=0) is handed back to HandleUse.
        var match = ResolveCarriedUseItem(args, out string targetArg, out var ambiguousNames);
        if (ambiguousNames != null)
        {
            await ShowItemDisambiguationAsync(ambiguousNames);
            return true;
        }

        if (match == null)
            return false;

        // Item resolved by name, but it carries no use-spell (e.g. a key): hand it back to HandleUse so
        // the key-on-door / "you can't use that" paths get a crack at it — never fall through here to a
        // shorter, looser name match.
        if (match.Value.Item.UseSpellId <= 0)
            return false;

        if (!_world.Database.Spells.TryGetValue(match.Value.Item.UseSpellId, out var spell))
            return false;

        var carried = match.Value;

        // USE gates: the use-eligibility check then the readied/worn requirement, run
        // before the use-spell fires. Shared with HandleUse's inert-item path — see TryApplyUseGatesAsync.
        if (await TryApplyUseGatesAsync(carried))
            return true;

        // USE clears the rest and meditate flags when it acts — but
        // NOT the sneak bit: using an item does NOT break sneak/hide (unlike casting,
        // which clears it). There is also NO in-combat block
        // and NO once-per-round cast-token gate on USE — so an item cast stays hidden, works mid-fight,
        // and is independent of the spell round. This is exactly what makes the off-guard strike potent.
        _player.IsResting = false;
        _player.IsMeditating = false;

        if (IsOffensiveSpell(spell))
        {
            // An item's
            // ability-43 offensive spell resolves through the SAME pipeline as a typed `cast` — identical
            // target resolution, hostility gates, hit/resist/damage formulas, messages, and death handling.
            // The only differences are it's paid by the item charge (no mana/energy) and it's a one-shot,
            // so it never becomes the repeating per-round combat action (keepAutoCombatSpellSelected:false)
            // and resolves immediately. The instant off-guard strike is faithful — the cast
            // applies spell damage inline with no action delay or round gate, same as the cast command.
            bool hit = await HandleOffensiveSpellCastAsync(
                spell,
                targetArg,
                allowImplicitCombatTarget: false,
                keepAutoCombatSpellSelected: false,
                reportMissingTarget: true,
                itemSourced: true);

            // Missing/invalid target or a blocked hostility gate already reported — don't burn a charge.
            if (!hit)
                return true;
        }
        else
        {
            await ApplyItemUseSpellEffectAsync(spell, targetArg);
        }

        await ConsumeCarriedItemChargeAsync(carried);

        // No synchronous save here: GameSession marks the acting player dirty after every command, so
        // write-behind persists this off the world gate. A direct SavePlayer blocks the gate for the whole
        // DB round-trip (~hundreds of ms on prod) on a spammable command — the off-guard `use` items were
        // exactly that, showing up as ~900ms source=command gate holds in SYSOP DIAG.
        return true;
    }

    // Fire a manual item-use spell (item ability 43): a beneficial duration spell applies directly
    // (no mana cost), anything else routes through the triggered-spell pipeline. Consumes the
    // once-per-round cast token like a regular cast. Shared by `use` and `eat`/`drink`.
    private async Task ApplyItemUseSpellEffectAsync(GameSpell spell, string targetArg = "")
    {
        // Who the effect lands on. USE parses "<item> [target]" and a player-targeted
        // use routes through the SAME targeting a typed `cast` gets —
        // the offensive branch in TryUseItemAbilityAsync already honours that. The beneficial branch
        // discarded targetArg entirely and applied everything to _player, so `use platinum mace <ally>`
        // (item 348 → spell 778, a Heal) healed the USER instead of the ally: "works properly on self,
        // does not work when cast on others". Empty/own-name resolves back to self, so eat/drink (which
        // never names a target) is unaffected.
        if (!TryResolveBeneficialSpellTarget(targetArg, out var effectTarget))
        {
            await _client.SendLineAsync("Cast on whom?");
            return;
        }

        // A "cast on ending" carrier (ability 151, no harm) used as a self/utility item — e.g. quaffing a
        // rainbow potion (#1665 → #1161): roll one spell from its MinBase..MaxBase POOL and apply THAT
        // through this SAME self-cast path, so each pool member lands in the right handler — #1162
        // (Inflict-Damage + Alter-AV, Dur 10) as a lingering self-DoT, #1163 (Confusion + Slowness,
        // Dur 30) as a lingering debuff, #1164 (Heal + Heal-Mana, Dur 0) as an instant heal — instead of
        // the pool id (1162-1164) falling through as a raw ~1163 damage number. Depth-guarded like the
        // offensive carrier gate. The cast-round token is set by the recursive call (or here when nothing
        // valid rolled).
        if (TryResolveCarrierSpell(spell, out var carrierSub))
        {
            if (carrierSub != null && _chainDepth < MaxChainDepth)
            {
                _chainDepth++;
                try { await ApplyItemUseSpellEffectAsync(carrierSub, targetArg); }
                finally { _chainDepth--; }
            }
            else
            {
                _player.NextSpellAllowedAtUtc = _world.GetNextCombatPulseUtc();
            }
            return;
        }

        // A SELF-targeted duration-harm spell (harm ability 1/8 + Duration > 0) applied to the drinker is a
        // self-DoT, NOT an instant hit — e.g. a rainbow potion rolling #1162 (Dur 10 Inflict-Damage drain +
        // ability 22 "Alter AV" −20, an accuracy debuff). Add it to the player's own active-spell slot at the
        // rolled magnitude/duration so ProcessActiveSpellUpkeep drains it over its life and RecalculatePlayerStats
        // applies (and later reverses) the lingering accuracy/stat debuff — mirroring the cast-at-player DoT
        // branch. Without this the self-DoT fell through to the
        // instant-damage path below, collapsing the whole over-time effect into a single ~MinBase hit. (Reached
        // only via the carrier recursion above; a real item's own offensive-DoT use-spell routes through the
        // offensive resolver in USE, not here.)
        if (IsDurationDamageSpell(spell))
        {
            int dotMagnitude = RollSpellMagnitude(spell, _player.Level);
            int dotDuration = RollPlayerCastSpellDuration(spell, _player.Level);
            if (_player.AddOrRefreshActiveSpell(spell.Number, dotMagnitude, dotDuration))
                _world.RecalculatePlayerStats(_player);

            await SendSpellStartMessagesAsync(spell, _player.Name, _player);
            string? ongoing = ResolveSpellOngoingStatusLine(spell, _player.Name);
            if (ongoing != null)
                await _client.SendLineAsync(GameAnsi.SpellHostile(ongoing));

            _player.NextSpellAllowedAtUtc = _world.GetNextCombatPulseUtc();
            return;
        }

        // A non-offensive spell that carries a duration buff OR an immediate beneficial effect (heal,
        // cure-poison, remove-spell, restore mana/energy, …) is a self-cast — apply its effects exactly
        // like the cast command does. Gating only on Duration > 0 mis-routed an instant beneficial spell
        // (e.g. the black cauldron #356: Heal 9999, Duration 0, MinBase/MaxBase 9999, AttType 4) into the
        // ExecuteTriggeredSpellByIdAsync damage fall-through, which read MinBase as raw damage and dealt
        // ~9999 — the exact opposite of the intended heal. Teleport/scripted/pure-damage item spells carry
        // none of these abilities and still route through the triggered-spell pipeline below.
        if (!IsOffensiveSpell(spell) && (spell.Duration > 0 || SpellHasImmediateBeneficialEffect(spell)))
        {
            ApplyBuffSpellIfDuration(spell, effectTarget);
            int? amount = ApplyImmediateBeneficialEffects(spell, effectTarget);
            await ApplyScriptedCommandSpellEffectAsync(spell, effectTarget);
            await SendBeneficialSpellMessagesAsync(spell, effectTarget, amount);
        }
        else
        {
            await ExecuteTriggeredSpellByIdAsync(spell.Number, showRoomAfterTeleport: true,
                TriggeredCastAnnounce.CastSuccess);
        }

        // Item use consumes the cast-round token (same delay as regular spells).
        _player.NextSpellAllowedAtUtc = _world.GetNextCombatPulseUtc();
    }

    // The uses left in the player's own item slot (the item lookup reads the per-slot
    // charge arrays for the backpack and for worn gear). A slot with no per-instance state has
    // never been used, so it still holds the item's starting UseCount (-1 = unlimited).
    private int GetRemainingItemCharges(CarriedItemMatch match)
        => _world.TryGetItemRuntimeState(match.InstanceId, out var state) && state.RemainingCharges.HasValue
            ? state.RemainingCharges.Value
            : match.Item.UseCount;

    // Deduct a charge from a just-used carried item: limited-use
    // items (UseCount > 0) lose a charge and, at 0 charges, are destroyed unless the reusable flag
    // (RetainAfterUses) is set — auto-unequipping first if worn. Shared by `use`/`eat`/`drink`.
    private async Task ConsumeCarriedItemChargeAsync(CarriedItemMatch match)
    {
        if (match.Item.UseCount <= 0)
            return;

        long instanceId = match.IsEquipped
            ? GetEquipmentInstanceId(match.EquipmentSlot!, match.ItemId)
            : _player.InventoryInstanceIds[match.InventoryIndex];

        var runtimeState = _world.GetOrCreateItemRuntimeState(instanceId);
        runtimeState.RemainingCharges = (runtimeState.RemainingCharges ?? match.Item.UseCount) - 1;

        if (runtimeState.RemainingCharges <= 0 && !match.Item.RetainAfterUses)
        {
            if (match.IsEquipped)
            {
                _player.Equipment.Remove(match.EquipmentSlot!);
                _player.EquipmentInstanceIds.Remove(match.EquipmentSlot!);
                RecalcEquipment();
            }
            else
            {
                TryRemoveInventoryItemAt(match.InventoryIndex, out _, out _);
            }
            _world.RemoveItemRuntimeState(instanceId);
            await SendItemDestructionMessagesAsync(match.Item);
        }
    }

    private bool TryResolveKeyUse(
        string args,
        Room room,
        out RoomExitDefinition? exit,
        out CarriedItemMatch? keyMatch,
        out string failureMessage,
        out IReadOnlyList<string>? ambiguousItemNames)
    {
        exit = null;
        keyMatch = null;
        failureMessage = "Use what on what?";
        ambiguousItemNames = null;

        string trimmedArgs = args.Trim();
        bool foundAnyVisibleExit = false;
        string? missingItemSelector = null;

        for (int splitIndex = trimmedArgs.LastIndexOf(' '); splitIndex > 0; splitIndex = trimmedArgs.LastIndexOf(' ', splitIndex - 1))
        {
            string itemSelector = trimmedArgs[..splitIndex].Trim();
            string exitSelector = NormalizeUseExitSelector(trimmedArgs[(splitIndex + 1)..]);
            if (string.IsNullOrWhiteSpace(itemSelector) || string.IsNullOrWhiteSpace(exitSelector))
                continue;

            var candidateExit = _world.FindVisibleExit(_player, room, exitSelector);
            if (candidateExit == null)
                continue;

            foundAnyVisibleExit = true;
            missingItemSelector = itemSelector;

            if (!TryFindCarriedItemForKeyUse(itemSelector, candidateExit.RequiredKeyItemId, out keyMatch, out ambiguousItemNames))
            {
                if (ambiguousItemNames != null)
                    return false;

                continue;
            }

            exit = candidateExit;
            failureMessage = string.Empty;
            return true;
        }

        if (foundAnyVisibleExit && !string.IsNullOrWhiteSpace(missingItemSelector))
            failureMessage = $"You do not have {missingItemSelector}.";
        else
            failureMessage = "You can't use that there.";

        return false;
    }

    private bool TryFindCarriedItemForKeyUse(string selector, int preferredItemId, out CarriedItemMatch? keyMatch, out IReadOnlyList<string>? ambiguousNames)
    {
        keyMatch = null;
        ambiguousNames = null;

        // USE resolves the key with neither
        // the only-unworn nor the only-worn filter, so the scan matches an item whether it is
        // worn or in the backpack (worn items stay in the same item list, flagged by the worn-slot
        // array). That's why a worn key-item like the glowing red amulet #494 resolves at all — verified
        // against stock. FindMatchingCarriedItems(includeEquipped: true) mirrors this and applies the
        // stock exact-name-wins narrowing used elsewhere. (USE then enforces the
        // worn/readied requirement separately — see HandleUse.)
        var matches = FindMatchingCarriedItems(selector, includeEquipped: true);
        if (matches.Count == 0)
            return false;

        var distinctNames = matches
            .Select(m => m.Item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctNames.Count > 1)
        {
            ambiguousNames = distinctNames;
            return false;
        }

        // Same name, possibly multiple stacks/slots — prefer the exit's required key id when it appears
        // in the narrowed set; otherwise take the first match (inventory before equipment).
        CarriedItemMatch chosen = matches[0];
        if (preferredItemId > 0)
        {
            foreach (var candidate in matches)
            {
                if (candidate.ItemId == preferredItemId)
                {
                    chosen = candidate;
                    break;
                }
            }
        }

        keyMatch = chosen;
        return true;
    }

    private bool TryResolveUniqueUseLightSource(string selector, out string resolvedItemName, out IReadOnlyList<string>? ambiguousNames)
    {
        resolvedItemName = string.Empty;
        ambiguousNames = null;

        // Stock USE resolves the item across EVERY carried item first (2+ different matches → the
        // "be more specific" list), and only a light-source item (ItemType 6) used with no target is
        // handed to LIGHT.
        var match = ResolveCarriedUseItem(selector, out string targetArg, out ambiguousNames);
        if (match == null || targetArg.Length > 0 || match.Value.Item.ItemType != LightSourceItemType)
            return false;

        resolvedItemName = match.Value.Item.Name;
        return true;
    }

    private static string NormalizeUseExitSelector(string selector)
    {
        string normalized = selector.Trim();
        if (normalized.StartsWith("on ", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[3..].Trim();

        return normalized;
    }

    private async Task HandleLock(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Lock what?");
            return;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return;
        }

        if (!_world.TryLockExit(_player, room, args, out var message, out bool lockDoorPresent))
        {
            await _client.SendLineAsync(message);
            // LOCK clears sneak/hide the moment it acts on a real door — a failed lock
            // (already locked / no key) still gives you away; only "no door that way" is free.
            if (lockDoorPresent)
                await BreakSneakAndHideForAction();
            return;
        }

        await _client.SendLineAsync(message);
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            $"{_player.Name} locks something nearby.", _client);
        await BreakSneakAndHideForAction();
    }

    private async Task HandlePicklock(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        // PICKLOCK with an argument breaks the picker's autocombat
        // BEFORE the skill roll or door lookup — so attempting to pick a lock ends your attack loop even
        // if you lack the skill or there's no door that way.
        await BreakAutocombatAsync();

        if (_player.Picklocks <= 0)
        {
            await _client.SendLineAsync($"{MudAnsi.White}Your skill fails you this time.{MudAnsi.Reset}");
            return;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return;
        }

        bool picked = _world.TryPicklockExit(_player, room, args, out var message, out bool doorPresent);
        await _client.SendLineAsync(message);
        if (picked)
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{_player.Name} fiddles with a nearby lock.", _client);

        // A pick attempt at a real door costs a 2-tick delay whether it succeeds or
        // fails ("no door that way" is free) AND clears the sneak and hide
        // bits — even a failed pick gives you away. So sneaking in and picking a lock breaks your sneak.
        if (doorPresent)
        {
            await BreakSneakAndHideForAction();
            await ApplyActionDelayAsync(BashPicklockFastTicks);
        }
    }

    // DISARM: the command parser reads the third word as the direction, so the literal
    // word "trap" between the verb and the direction is required — only "disarm trap <direction>"
    // works. Other shapes silently no-op in stock; we surface a syntax hint instead.
    private async Task HandleDisarm(string args)
    {
        var parts = (args ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            // With NO argument, "disarm" is the unready path
            // (remove the readied weapon) -- not a trap prompt.
            await UnreadyReadiedWeaponAsync();
            return;
        }

        if (!parts[0].Equals("trap", StringComparison.OrdinalIgnoreCase) || parts.Length < 2)
        {
            return;
        }

        string selector = string.Join(' ', parts.Skip(1));

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return;
        }

        _world.TryDisarmExit(_player, room, selector, out var message, out var triggeredSpellTrap);
        // A non-direction selector (e.g. "disarm trap green") resolves to no message -- silent, like stock.
        if (!string.IsNullOrEmpty(message))
            await _client.SendLineAsync(message);

        // Botched a spell-trap disarm (type 24): the trap fires its spell on the would-be disarmer,
        // exactly as if traversed (the exit-type-24 trigger path).
        if (triggeredSpellTrap != null)
            await ApplyTriggeredExitSpellEffectsAsync(triggeredSpellTrap);

        if (_player.CurrentHP <= Player.DeathHP)
            await HandlePlayerDeath();
    }

    // The unready branch, reached by bare "disarm": remove the
    // currently readied weapon. A cursed (82) / major-curse (83) weapon can't be removed ("You cannot
    // remove %s."); with a weapon readied it prints "You now have no weapon readied." + the room
    // "removes" line and unapplies the weapon's abilities; with NO weapon readied it does nothing.
    private async Task UnreadyReadiedWeaponAsync()
    {
        if (!_player.Equipment.TryGetValue("weapon", out int weaponId)
            || !_world.Database.Items.TryGetValue(weaponId, out var weapon))
            return; // no weapon readied -> silent

        if (weapon.Abilities.ContainsKey(ItemCursedAbilityId) || weapon.Abilities.ContainsKey(ItemMajorCurseAbilityId))
        {
            await _client.SendLineAsync($"You cannot remove {weapon.Name}.");
            return;
        }

        long instanceId = GetEquipmentInstanceId("weapon", weaponId);
        _player.Equipment.Remove("weapon");
        _player.EquipmentInstanceIds.Remove("weapon");
        AddItemToInventory(weaponId, instanceId);
        ApplyEquipInstantAbilities(weapon, equipping: false);

        await _client.SendLineAsync($"{MudAnsi.Yellow}You now have no weapon readied.{MudAnsi.Reset}");
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            $"{MudAnsi.Yellow}{_player.Name} removes {weapon.Name}.{MudAnsi.Reset}", _client);

        RecalcEquipment();
    }

    // The four phases of one combat beat, mirroring the stock background pass so the
    // world coordinator (GameWorld.Combat.cs) can resolve them in the faithful order: a backstab
    // pre-pass, then a 60/40 coin-flip between the player's own swings and the
    // monster swings, with reschedule deferred to the end. See ProcessRealtimeCombatTickAsync for the
    // bundled single-player composition.
    public enum CombatBeatPhase
    {
        // Prune dead/out-of-room attackers + chosen targets, emit "*Combat Off*", run idle encounter
        // checks. Order-insensitive across players; runs once per player per beat before the swings.
        Housekeeping,
        // The player's OWN swing: backstab in the pre-pass, otherwise normal attack.
        OwnAttack,
        // Incoming monster swings against the player.
        MonsterAttack,
        // Reschedule the next 5s grid beat (or end combat). Deferred so it runs after BOTH the player
        // and monster passes, regardless of the 60/40 order, leaving each player due across the beat.
        Finalize,
    }

    // `due` is the coordinator's beat-start snapshot of whether this player takes the beat at all. It is
    // passed in rather than re-derived per phase because a player's own action can push their deadline
    // forward as it resolves (a combat-spell round re-engages its target), and that must not retroactively
    // excuse them from the monster pass — see ProcessCombatBeatInnerAsync.
    public async Task RunCombatBeatPhaseAsync(CombatBeatPhase phase, bool due)
    {
        switch (phase)
        {
            case CombatBeatPhase.Housekeeping: await RunCombatHousekeepingPhaseAsync(); break;
            case CombatBeatPhase.OwnAttack: await ResolveOwnAttackPhaseAsync(due); break;
            case CombatBeatPhase.MonsterAttack: await ResolveMonsterAttackPhaseAsync(due); break;
            case CombatBeatPhase.Finalize: FinalizeCombatBeat(due); break;
        }
    }

    // The bundled single-player beat (housekeeping → own swing → monster swings → finalize), composed
    // from the same phase methods the coordinator runs globally. Behaviour-equivalent to a single
    // player resolving alone; kept for any non-coordinated caller and as the equivalence reference.
    // Dueness is snapshotted once up front, exactly as the coordinator does.
    public async Task ProcessRealtimeCombatTickAsync()
    {
        bool due = IsCombatBeatDue();
        await RunCombatHousekeepingPhaseAsync();
        await ResolveOwnAttackPhaseAsync(due);
        await ResolveMonsterAttackPhaseAsync(due);
        FinalizeCombatBeat(due);
    }

    private bool IsCombatBeatDue()
        => _player.NextMonsterAttackAtUtc != DateTime.MinValue && DateTime.UtcNow >= _player.NextMonsterAttackAtUtc;

    private bool IsInProtectedRoom()
        => _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)?.IsProtected == true;

    // Phase 1: prune, clear dead/out-of-room chosen targets, emit the player's "*Combat Off*", and run
    // idle encounter checks. (Lifted from the head of the former monolithic tick.)
    private async Task RunCombatHousekeepingPhaseAsync()
    {
        int activeMonsterAttackerCount = PruneInactiveMonsterAttackers(_player);

        // WHY the chosen target went away decides whether the player hears "*Combat Off*" at all.
        // A target that DIED ends combat with the toggle; a target that merely LEFT (walked/was
        // dragged out, or disconnected) ends it in silence — see the emit site below for the
        // details. Track both so a beat that clears one of each still reports the death.
        bool clearedByDeparture = false;
        bool clearedByDeath = false;

        if (_player.CombatTarget != null)
        {
            bool targetOutOfRoom =
                _player.CombatTarget.MapNumber != _player.CurrentMapNumber ||
                _player.CombatTarget.RoomNumber != _player.CurrentRoomNumber;

            if (_player.CombatTarget.IsDead || targetOutOfRoom)
            {
                if (_player.CombatTarget.IsDead)
                    clearedByDeath = true;
                else
                    clearedByDeparture = true;

                _player.CombatTarget = null;
                if (!_player.InCombat && activeMonsterAttackerCount == 0 && _player.PlayerCombatTarget == null)
                    _player.NextMonsterAttackAtUtc = DateTime.MinValue;
            }
        }

        if (_player.PlayerCombatTarget != null)
        {
            bool targetDisconnected = _world.GetClientForPlayer(_player.PlayerCombatTarget.Name) == null;
            bool targetOutOfRoom =
                _player.PlayerCombatTarget.CurrentMapNumber != _player.CurrentMapNumber ||
                _player.PlayerCombatTarget.CurrentRoomNumber != _player.CurrentRoomNumber;
            bool targetDefeated = _player.PlayerCombatTarget.CurrentHP <= Player.DeathHP;

            if (targetDisconnected || targetOutOfRoom || targetDefeated)
            {
                if (targetDefeated)
                    clearedByDeath = true;
                else
                    clearedByDeparture = true;

                _player.PlayerCombatTarget = null;
                if (!_player.InCombat && activeMonsterAttackerCount == 0)
                    _player.NextMonsterAttackAtUtc = DateTime.MinValue;
            }
        }

        // "*Combat Engaged*" / "*Combat Off*" are the PLAYER'S session messages, scoped to their own
        // engagement contract — the target they chose with attack/bs/punch/bash/etc. The stock
        // timing is delayed to the next pulse (NextMonsterAttackAtUtc). See [[combat-off-timing]].
        if (_player.InCombat &&
            _player.CombatTarget == null &&
            _player.PlayerCombatTarget == null &&
            !HasPendingAreaCombatSpell())   // an area channel (chaos storm) is in combat with no single target
        {
            if (_player.NextMonsterAttackAtUtc == DateTime.MinValue || DateTime.UtcNow >= _player.NextMonsterAttackAtUtc)
            {
                _player.StopCombatLoop();
                if (activeMonsterAttackerCount == 0)
                    _player.NextMonsterAttackAtUtc = DateTime.MinValue;
                else
                    // Attackers remain but the player's chosen target is gone: defer their next swing to
                    // the next beat so monsters don't also swing on the combat-off beat (the old monolith
                    // returned before the monster branch here). They resume next beat.
                    ScheduleNextMonsterRound(_player);

                // A target that walked out (or dropped link) ends the loop SILENTLY; a target that
                // died still prints the toggle.
                //
                // The monster attack guards its whole body with
                //     else if (*(int *)(iVar3 + 200) == piVar4[4])      // player room == monster room
                // and has NO else — a monster in another room means the function does nothing and says
                // nothing. The player-target twin is the same: the mover's side calls
                // the room-change kill calls, and stopping the loop
                // only unlinks you from the autocombat array; it never emits
                // the break line. The only two break-line emissions inside the
                // attack loop are for the attacker being unconscious/dead and for an
                // RM_PROTECTED room (@25827). Death is a different path and DOES print the toggle,
                // which is why clearedByDeath still emits — that also keeps the closing toggle on an
                // area sweep that kills everything (no chosen target, so neither flag is set).
                //
                // The extra toggle wedged MegaMUD. Across four live captures, kill-driven toggles were
                // survived and re-engaged every time; the one time a target was dragged out of the room
                // ("<target> just left to the north.") this fired and the client issued no further
                // command for the rest of the session.
                if (!clearedByDeparture || clearedByDeath)
                    await SendCombatMessagesToCurrentPlayerAsync([GameAnsi.CombatOff("*Combat Off*")]);
            }

            return;
        }

        if (!_player.InCombat && !IsActiveTarget(_player))
            await CheckEncounters();
    }

    // Phase 2: the player's own swing. Dispatch follows stock exactly: the round is
    // decided by WHICH target the player chose, never by who else happens to be hitting them.
    // The auto-combat record is read and branches on the USER-target slot
    // first (`!= -1` → PvP), falling through to the monster-target slot otherwise — there
    // is no incoming-attacker test anywhere in it. Monster-combat additionally needs a non-protected
    // room (the auto round is suppressed entirely there); PvP has no protected-room gate here (the
    // round itself re-checks, ExecuteAutoPlayerCombatRound).
    // No reschedule here — that is deferred to Finalize so the 60/40 order can't lose the beat.
    private async Task ResolveOwnAttackPhaseAsync(bool due)
    {
        if (!due)
            return;

        // Area combat spell (chaos storm etc.): the caster channels it at the whole room with no single
        // CombatTarget, so fire the round here directly (the target-gated paths below would skip it).
        if (_player.InCombat && HasPendingAreaCombatSpell())
        {
            if (!IsInProtectedRoom())
                await ExecuteAutoPlayerCombatRound();
            return;
        }

        int activeMonsterAttackerCount = PruneInactiveMonsterAttackers(_player);

        // PvP FIRST, matching the stock user-slot-before-monster-slot branch (bug #214): a monster that
        // is chewing on the attacker — an arena mob, or an aggro picked up before they switched to a
        // player target — must not eat their round. This branch used to sit behind an
        // `activeMonsterAttackerCount > 0 → return`, so any incoming monster silently swallowed EVERY
        // PvP swing: the victim took nothing, the attacker saw no hit/miss line at all, and the queued
        // backstab was never consumed (PendingCombatRoundAction stayed Backstab), so it repeated round
        // after round. The two chosen-target fields are mutually exclusive (EngagePvpCombatAsync clears
        // CombatTarget, EngageCombatAsync clears PlayerCombatTarget), so this never steals a monster round.
        if (_player.InCombat && _player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP)
        {
            await ExecuteAutoPlayerCombatRound();
            return;
        }

        if (activeMonsterAttackerCount > 0)
        {
            if (IsInProtectedRoom())
                return;

            if (_player.InCombat && _player.CombatTarget != null && !_player.CombatTarget.IsDead)
                await ExecuteAutoPlayerCombatRound();
        }
    }

    // Phase 3: incoming monster swings against the player. Skipped in a protected room
    // (monster→player attacks are hard-gated there). No reschedule — deferred to Finalize.
    private async Task ResolveMonsterAttackPhaseAsync(bool due)
    {
        if (!due)
            return;

        int activeMonsterAttackerCount = PruneInactiveMonsterAttackers(_player);
        if (activeMonsterAttackerCount == 0)
            return;

        // See [[protected-room-combat-gate]]: a monster that pursued the player into a safe room stays
        // queued but can never land a blow.
        if (IsInProtectedRoom())
            return;

        // Bundled-solo composition: this player is the only candidate, so every incoming attacker swings
        // at them. Item C's coordinator drives the faithful cross-player version (one target per monster
        // per beat) via ResolveSingleMonsterSwingAsync directly; see GameWorld.Combat.cs.
        foreach (var attacker in _player.SnapshotIncomingMonsterAttackers())
        {
            if (await ResolveSingleMonsterSwingAsync(attacker))
                return;   // player died
        }
    }

    // One monster's swing against THIS player — the per-attacker body lifted out of the loop above so the
    // world coordinator (GameWorld.Combat.cs) can resolve a monster's single chosen target per beat
    // Returns true if the swing killed the player (the caller must stop). The coordinator
    // confirms the player is due, alive, and in a non-protected room and holds their CombatGate before
    // calling; the bundled-solo loop above does the same gating once for all attackers.
    public async Task<bool> ResolveSingleMonsterSwingAsync(MonsterInstance attacker)
    {
        attacker.PrepareCombatRound();

        // When a monster engages the player in
        // melee this beat it clears the victim's SNEAK and HIDE bits — and
        // stops their pending exit — BEFORE the swing rounds. A monster that successfully attacks you
        // drops your stealth, whether or not the blow connects (bug #144: snuck into the slums entrance,
        // a guard engaged, but stealth persisted so the player snuck right back out). Reaching this swing
        // already means the monster passed the can-attack/aggro gates (the snuck-in-undetected grace,
        // MonsterCouldAttack Gate A, keeps a freshly-snuck-in player off every incoming list that beat),
        // so this is exactly the stock engage point. Silent, like stock — the (Sneaking) statline drops.
        _player.IsSneaking = false;
        _player.IsHidden = false;

        // A smash-knocked-down or held monster still attacks (movement-only gates) — it is merely
        // defenseless (AV/DV penalty applied in the marshal). No skip-attack gate anywhere in this round.
        //
        // Round order below mirrors the stock monster attack: the attack loop (melee swings / AtkType-2 casts,
        // plus each landed swing's AtkHitSpell) runs first, and the mid-combat spell fires once at the end
        // — see the note at the bottom of this method.

        // AtkType=2 spell attacks (e.g. tower of flame / chaos storm) resolve synchronously via the
        // delegate, applying damage + accumulating messages into the same result. An AREA-typed attack
        // spell (an area monster cast) instead hits every player in the room from a single
        // shared roll, so the delegate just records it (returning no inline messages) and we fan it out
        // across the whole room after the swing. (The swing loop's per-player HP check therefore won't
        // see the area damage mid-loop, but stock area attack spells are single, high-energy casts.)
        var areaAttackCasts = new List<(int SpellId, int CastLevel)>();
        var scriptedAttackCasts = new List<int>();
        var monResult = CombatEngine.MonsterAttack(attacker, _player, _world.Database.Items, _world.Database.Messages,
            (spellId, castLevel) =>
            {
                if (_world.Database.Spells.TryGetValue(spellId, out var castSpell))
                {
                    // A pure scripted-command attack spell (ability 148, no harm) triggers a textblock —
                    // it must NOT fall through to the damage resolver (which would read MinBase as bogus HP
                    // loss and drop the script): the water spirit's AtkType-2 "remove mask" #525 → textblock
                    // 990 takes the player's mask, it does not deal 1 damage. Mirrors the AtkHitSpell ability-148
                    // handling (TryFireMonsterAtkHitSpellAsync). Running a textblock is async, so defer it
                    // past the synchronous swing like an area cast. A harm spell that ALSO carries 148
                    // (death ray/fist of death) keeps its damage path here.
                    if (!HasHarmAbility(castSpell) && castSpell.Abilities.ContainsKey(ScriptedHitSpellAbilityId))
                    {
                        scriptedAttackCasts.Add(spellId);
                        return (new List<string>(), new List<string>());
                    }
                    if (MonsterSpellIsAreaCombatCast(castSpell))
                    {
                        areaAttackCasts.Add((spellId, castLevel));
                        return (new List<string>(), new List<string>());
                    }
                }
                return ResolveMonsterAttackSpell(attacker, spellId, castLevel);
            },
            CombatEngine.BuildRetaliationSources(_player, _world.Database));

        await SendCombatMessagesToCurrentPlayerAsync(monResult.Messages);
        BroadcastCombatMessagesToRoom(monResult.RoomMessages);

        foreach (var (spellId, castLevel) in areaAttackCasts)
        {
            if (_world.Database.Spells.TryGetValue(spellId, out var areaSpell))
                await ResolveMonsterAreaCombatCastAsync(attacker, areaSpell, castLevel);
        }

        foreach (int spellId in scriptedAttackCasts)
            await ExecuteTriggeredSpellByIdAsync(spellId, showRoomAfterTeleport: true,
                TriggeredCastAnnounce.CastSuccess);

        if (monResult.TargetKilled || _player.CurrentHP <= Player.DeathHP)
        {
            await HandlePlayerDeath();
            return true;
        }

        // AtkHitSpell: a melee slot that LANDS a hit also casts its hit-spell on the victim. The
        // guardsman's hit-spell is #583 "jail" — its trigger-text script beats-down/hauls a criminal off
        // to jail once their HP is low (the script's testskill current_hp gate). Fires only on a connect.
        if (await TryFireMonsterAtkHitSpellAsync(attacker, monResult))
            return true;   // the hit-spell killed/finished the player (death handled inside)

        // The mid-combat spell (chosen by the mid-spell picker)
        // is cast ONCE at the END of the attack round — AFTER the swing loop has
        // run its course, not instead of it. The loop exits on any of: the 6-attack cap, the monster
        // running out of stamina for another swing, or the player dying; the cast is suppressed only by
        // the last of those. The suppression flag is set in exactly three places (an AtkType-2
        // cast reporting a kill, an AtkHitSpell cast reporting a kill, and a kill check firing
        // after a melee swing) and every one of them means the same thing — a cast reports a kill only
        // on the kill + experience path, i.e. the cast killed the player.
        // Merely knocking the player unconscious does NOT suppress it (stock drops them to the ground
        // and keeps going), so the two death returns above are the whole gate.
        //
        // We used to cast the mid-spell at the TOP of the round and return, so it REPLACED that round's
        // swings: a hydra regrowing a head (#726, 50%/round) did not also bite you, and every mid-spell
        // caster in the game hit for roughly half its stock damage. The cadence — once per round — is the
        // same either way, so this does not change how fast a hydra regrows its heads.
        if (await TryExecuteMonsterMidSpellAsync(attacker) && _player.CurrentHP <= 0)
        {
            await HandlePlayerDeath();
            return true;
        }

        return false;
    }

    private const int ScriptedHitSpellAbilityId = 148;   // "Trigger Text Block"

    // Cast the attacker's melee AtkHitSpell on this player after a landed swing (per-slot hit-spell
    // on a successful melee hit) — the monster analog of the player weapon spell-proc (ProcSpellId /
    // ability 114). Dispatches by spell kind:
    //   • scripted trigger-text (ability 148) — the jail script (#583) / pyramid teleport (#686) — runs
    //     through the triggered-spell pipeline (text-block special command against the victim);
    //   • everything else (poison/knockdown/stun/drain/disease/burn …) resolves through the shared
    //     monster-cast pipeline (ResolveMonsterAttackSpell), the same path AtkType=2 spell attacks use,
    //     which applies the spell's damage / timed debuff / poison to the victim.
    // Approximation: fires the FIRST hit-spell-bearing slot once per landed round rather than tracking
    // exactly which slot connected (stock monsters carry a single uniform hit-spell, so this matches).
    // Returns true if the player died as a result (caller stops the swing loop).
    private async Task<bool> TryFireMonsterAtkHitSpellAsync(MonsterInstance attacker, CombatResult result)
    {
        // Fire the hit-spell of every slot that actually CONNECTED, in swing order — CombatEngine
        // records them on the result as it resolves each swing.
        //
        // This used to take the first slot on the template carrying any hit-spell, once per round,
        // justified by "stock monsters carry a single uniform hit-spell". That premise is false: many
        // stock monsters park the hit-spell on a low-probability SECONDARY slot, and stock indexes
        // it by the slot the attack roll selected. The grey spider (#30, AtkPer 95/100 with the spell
        // only on slot 1) should land its poison on 5% of bites; picking it off the template fired it
        // on 100% of them — a 20x inflation of an 8-12 poison for 100 ticks, on a monster newbies meet
        // in Newhaven. Ghouls (75/100) and shades (90/100) were inflated 4x and 10x the same way.
        foreach (int hitSpellId in result.PendingHitSpells)
        {
            if (!_world.Database.Spells.TryGetValue(hitSpellId, out var hitSpell))
                continue;

            if (hitSpell.Abilities.ContainsKey(ScriptedHitSpellAbilityId))
            {
                await ExecuteTriggeredSpellByIdAsync(hitSpellId, showRoomAfterTeleport: true,
                    TriggeredCastAnnounce.CastSuccess);
            }
            else
            {
                // AtkHitSpell carries no per-slot cast level in the data; resolve at base magnitude.
                var (playerMessages, roomMessages) = ResolveMonsterAttackSpell(attacker, hitSpellId, castLevel: 1);
                await SendCombatMessagesToCurrentPlayerAsync(playerMessages);
                BroadcastCombatMessagesToRoom(roomMessages);
            }

            if (_player.CurrentHP <= Player.DeathHP)
            {
                await HandlePlayerDeath();
                return true;
            }
        }

        return false;
    }

    // Phase 4: reschedule the next 5s grid beat for a player still fighting, or end their loop. Runs
    // after BOTH the player and monster passes (whichever 60/40 order), so a player stays "due" across
    // the whole beat. Gated on the beat-start snapshot, so a deadline already pushed forward during the
    // beat (a combat-spell round re-engaging its target) still lands here — ScheduleNextMonsterRound is
    // idempotent on the 5s grid, so it re-derives the same pulse that push chose.
    private void FinalizeCombatBeat(bool due)
    {
        if (!due)
            return;

        int activeMonsterAttackerCount = PruneInactiveMonsterAttackers(_player);

        bool stillFighting = _player.InCombat
            || activeMonsterAttackerCount > 0
            || (_player.CombatTarget != null && !_player.CombatTarget.IsDead)
            || (_player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP);

        if (stillFighting)
            ScheduleNextMonsterRound(_player);
        else
            _player.NextMonsterAttackAtUtc = DateTime.MinValue;
    }

    // Drop monster attackers that have died or left the player's room, and clear a CombatTarget /
    // PvP target that is no longer present. Returns the live incoming-attacker count. (Lifted from
    // the former per-session GameSession helper.)
    private static int PruneInactiveMonsterAttackers(Player p)
    {
        p.RemoveIncomingMonsterAttackersWhere(monster =>
            monster.IsDead ||
            monster.MapNumber != p.CurrentMapNumber ||
            monster.RoomNumber != p.CurrentRoomNumber);

        if (p.CombatTarget != null &&
            (p.CombatTarget.IsDead ||
             p.CombatTarget.MapNumber != p.CurrentMapNumber ||
             p.CombatTarget.RoomNumber != p.CurrentRoomNumber))
        {
            p.CombatTarget = null;
        }

        return p.IncomingMonsterAttackerCount;
    }

    private static bool IsActiveTarget(Player p)
        => PruneInactiveMonsterAttackers(p) > 0 ||
           (p.CombatTarget != null && !p.CombatTarget.IsDead) ||
           (p.PlayerCombatTarget != null && p.PlayerCombatTarget.CurrentHP > 0);

    private void ScheduleNextMonsterRound(Player p)
    {
        var nextRoundAt = _world.GetNextCombatPulseUtc();
        p.NextMonsterAttackAtUtc = nextRoundAt;
        if (p.InCombat)
            p.NextAttackAllowedAtUtc = nextRoundAt;
    }

    public async Task<bool> ExecuteAutoPlayerCombatRound()
    {
        if (!_player.InCombat)
            return false;

        // In-combat energy refill: refill the stamina pool by one cap
        // with overshoot, once per combat round, before the player's action spends it. Out-of-combat
        // refill (clamp, no overshoot) is handled by the world tick (GameWorld.WorldTick).
        _player.PrepareCombatRound();

        // Confusion check: a confused fighter may "look around stupidly and do nothing"
        // instead of acting this round (per-round roll vs the confusion magnitude).
        // Snapshot the combat-output counter before the round acts. SendCombatMessagesToCurrentPlayerAsync
        // bumps it (and reprompts per line); raw spell/confusion lines sent straight to the client do not.
        // A combat round fires from the async combat beat with no command loop behind it, so a branch that
        // emits ONLY raw lines (a selected combat-spell's damage line, an area sweep, a "look around
        // stupidly" confusion line) leaves no prompt on the wire and the session looks frozen until the
        // player presses Enter. We reprompt at the end only when the counter is unchanged, so the melee
        // path (already reprompts per line) and a spell that scored a kill (its death line goes through the
        // combat-send path) never get a doubled prompt. The melee-weapon-proc trailing line is handled
        // separately in SendCombatResultWithProcsAsync — there the melee swing already bumped the
        // counter, so this guard intentionally defers to that path.
        long combatOutputSeqBefore = System.Threading.Interlocked.Read(ref _player.CombatOutputSeq);

        bool acted;
        if (await ConfusedActionConsumedAsync())
        {
            acted = true;
        }
        // A smash-knocked-down/held player keeps auto-attacking (movement-only gates); they are just
        // defenseless (AV/DV penalty in the marshal). No skip-attack gate. Confusion handles interrupts.
        else if (await TryExecutePendingCombatSpellRoundAsync())
        {
            acted = true;
        }
        else if (_player.CombatTarget != null && !_player.CombatTarget.IsDead)
        {
            var monster = _player.CombatTarget;
            var result = await ExecutePlayerCombatActionAsync(monster);
            acted = result.Messages.Count > 0;
        }
        else if (_player.PlayerCombatTarget != null && _player.PlayerCombatTarget.CurrentHP > Player.DeathHP)
        {
            var target = _player.PlayerCombatTarget;

            // Re-validate at the point of the swing, exactly as the monster coordinator already does
            // (GameWorld.Combat.ResolveMonsterSwingAsync): the target pointer was captured when this
            // beat's housekeeping ran, and a lot can happen before our phase-2 slot — another attacker
            // can kill them, and their death recall then teleports them somewhere else at full HP. The
            // CurrentHP test above cannot catch that: a respawned victim reads as perfectly healthy.
            // Without the room re-check the stale pointer buys one more full round of swings against
            // someone who is already standing in the (protected) death-recall room.
            if (target.CurrentMapNumber != _player.CurrentMapNumber
                || target.CurrentRoomNumber != _player.CurrentRoomNumber
                || IsInProtectedRoom())
            {
                _player.PlayerCombatTarget = null;
                acted = false;
            }
            else
            {
                var result = await ExecutePlayerCombatActionAgainstPlayerAsync(target);
                acted = result.Messages.Count > 0 || result.TargetMessages.Count > 0;
            }
        }
        else
        {
            acted = false;
        }

        if (acted
            && !_player.SuppressBroadcastReprompt
            && System.Threading.Interlocked.Read(ref _player.CombatOutputSeq) == combatOutputSeqBefore)
        {
            await _client.SendAsync(MudAnsi.Prompt(_player));
        }

        return acted;
    }

    private async Task HandleMonsterDeath(MonsterInstance monster, CombatResult result)
    {
        if (!_world.TryBeginMonsterDeathProcessing(monster))
            return;

        var engagedPlayers = GetMonsterExperienceRecipients(monster);
        // The quest payload is an area DeathSpell, not an experience share — it reaches everyone the
        // monster engaged who is still in the room, including a member who broke combat (bug #231).
        var questCreditPlayers = GetMonsterKillCreditRecipients(monster);

        // Bug #229: a kill ends autocombat ONLY for players whose chosen target WAS this monster.
        // A monster kill walks
        // the terminals and stops the loop only where the terminal is combatting that monster —
        // that tests the terminal's autocombat MONSTER slot (record+4) against the dead monster. Sharing
        // the experience credit is NOT the test. Two cases this used to break, because every experience
        // recipient was stopped unconditionally:
        //   - a "roomer" channelling an area spell (an area cast leaves the monster slot empty, so our
        //     CombatTarget is null): a party member's kill cleared PendingCombatSpellId and InCombat, so
        //     the sweep died mid-round with monsters still standing;
        //   - a party member meleeing a DIFFERENT monster: someone else's kill ended their fight.
        // Snapshot it here — the RemoveIncomingMonsterAttacker sweep further down nulls CombatTarget on
        // every player in the room before the award loop runs.
        var lockedOntoMonster = engagedPlayers
            .Where(player => ReferenceEquals(player.CombatTarget, monster))
            .Select(player => player.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Stock death sequence:
        // 1. Currency drops to ground ("1 silver drops to the ground." / "5 copper drop to the ground.")
        // 2. Death message ("The kobold thief falls to the ground with a shrill cry.")
        // 3. Experience gained
        // 4. *Combat Off*

        // Stock death output only announces currency; carried items still drop into the room.
        var roomMessages = new List<string>();
        if (result.RunicDropped > 0)
        {
            roomMessages.Add(FormatMonsterCurrencyDropMessage(result.RunicDropped, "runic"));
        }
        if (result.PlatinumDropped > 0)
        {
            roomMessages.Add(FormatMonsterCurrencyDropMessage(result.PlatinumDropped, "platinum"));
        }
        if (result.GoldDropped > 0)
        {
            roomMessages.Add(FormatMonsterCurrencyDropMessage(result.GoldDropped, "gold"));
        }
        if (result.SilverDropped > 0)
        {
            roomMessages.Add(FormatMonsterCurrencyDropMessage(result.SilverDropped, "silver"));
        }
        if (result.CopperDropped > 0)
        {
            roomMessages.Add(FormatMonsterCurrencyDropMessage(result.CopperDropped, "copper"));
        }

        // Drop currency on ground without exchanging denominations.
        if (result.RunicDropped > 0 || result.PlatinumDropped > 0 || result.GoldDropped > 0 || result.SilverDropped > 0 || result.CopperDropped > 0)
        {
            _world.DropCurrencyInRoom(
                _player.CurrentMapNumber,
                _player.CurrentRoomNumber,
                result.RunicDropped,
                result.PlatinumDropped,
                result.GoldDropped,
                result.SilverDropped,
                result.CopperDropped);
        }

        // After currency drops to the room and flushes to
        // the killer, the dying monster casts its DeathSpell
        // 21133) BEFORE the death message prints (@21136). e.g. slaver leader summons reinforcements,
        // gulguthra's death-throes confuse the room. Flush the currency lines first so the order matches.
        if (monster.Template.DeathSpell > 0)
        {
            await SendCombatMessagesToCurrentPlayerAsync(roomMessages);
            BroadcastCombatMessagesToRoom(roomMessages);
            roomMessages = new List<string>();
            await CastMonsterTriggeredSpellAsync(monster, monster.Template.DeathSpell);
        }

        // Drop items on ground without a separate combat line; stock fidelity only announces currency.
        foreach (var dropId in result.Drops)
        {
            if (!_world.Database.Items.ContainsKey(dropId))
                continue;

            // QUESTALLPARTY (sysop QoL): a main-quest "drops to ground then turn in" item is handed to each
            // engaged player who is on that quest step and still needs it, instead of dropping a single copy —
            // so a party of N doesn't have to kill the monster N times to give each member their own turn-in.
            // When nobody needs it, the stock single ground drop below still happens. Off by default.
            if (_world.QuestDropToAllParty && await TryDistributeQuestPartyDropAsync(dropId, questCreditPlayers))
                continue;

            // Monster loot goes through the room-disposal path, so a full room
            // spills the hoard into adjacent rooms instead of destroying it.
            _world.DisposeOfItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, dropId,
                uses: monster.Template.GetDropUses(dropId));
        }

        // Death message
        if (!string.IsNullOrEmpty(result.DeathMessage))
            roomMessages.Add(result.DeathMessage);

        await SendCombatMessagesToCurrentPlayerAsync(roomMessages);

        BroadcastCombatMessagesToRoom(roomMessages);

        // Quest-on-kill: stock fires the killed boss's quest script as part of the kill, after
        // the death message and before the base kill exp is awarded. The boss's DeathSpell is an
        // AREA spell that delivers the quest to EVERY engaged player in the room (see ApplyQuestKillProgress).
        // Being an area cast, its reach is NOT the experience split's autocombat test (bug #231) — a member
        // who broke combat but is still standing in the room is inside the blast. Each script self-gates on
        // stage/alignment, so non-quest kills and wrong-stage players are silent no-ops.
        await ApplyQuestKillProgressAsync(monster, questCreditPlayers);

        long totalExperienceAward = _world.ScaleMonsterExperienceAward(result.ExpGained);
        long experienceShare = totalExperienceAward <= 0
            ? 0
            : (totalExperienceAward + engagedPlayers.Count - 1) / engagedPlayers.Count;

        foreach (var roomPlayer in _world.GetPlayersInRoom(monster.MapNumber, monster.RoomNumber))
            roomPlayer.RemoveIncomingMonsterAttacker(monster);

        foreach (var engagedPlayer in engagedPlayers)
        {
            // Only a player who was locked onto THIS monster ends their fight (and hears "*Combat Off*").
            // An area sweep (chaos/mana storm) kills several monsters in one round while the caster keeps
            // channelling: they hold no single target, so no felled monster toggles their combat off — the
            // single closing "*Combat Off*" comes from combat housekeeping once the sweep runs dry.
            bool endsThisPlayersFight = lockedOntoMonster.Contains(engagedPlayer.Name);
            if (endsThisPlayersFight)
                engagedPlayer.StopCombatLoop();

            // A positive exp gain is refused outright when the player has
            // already progressed past the level-ahead gate (sysop LevelAheadCap). The kill still ends
            // combat; only the exp award is withheld, with the stock "progressed too far" notice.
            if (experienceShare > 0 && _world.IsBlockedByLevelAheadCap(engagedPlayer))
            {
                var blockedMessages = new List<string>
                {
                    GameAnsi.Experience("You have progressed too far without training to acquire anymore experience!", engagedPlayer.PaletteId)
                };
                if (endsThisPlayersFight)
                    blockedMessages.Add(GameAnsi.CombatOff("*Combat Off*"));
                await SendCombatMessagesToPlayerAsync(engagedPlayer, blockedMessages);
                continue;
            }

            engagedPlayer.Experience += experienceShare;
            AddGangExperience(engagedPlayer, experienceShare);

            var awardMessages = new List<string> { GameAnsi.Experience($"You gain {experienceShare} experience.", engagedPlayer.PaletteId) };
            if (endsThisPlayersFight)
                awardMessages.Add(GameAnsi.CombatOff("*Combat Off*"));
            await SendCombatMessagesToPlayerAsync(engagedPlayer, awardMessages);
        }

        _world.RemoveDeadMonster(monster);
    }

    private static string FormatMonsterCurrencyDropMessage(int amount, string currencyName)
        => $"{amount} {currencyName} {(amount == 1 ? "drops" : "drop")} to the ground.";

    // Experience distribution, monster branch.
    // A terminal takes a share when it is STILL INSIDE AUTOCOMBAT — the test reads
    // the live autocombat array, which `break` removes you from — and either
    //   (A) carries this monster in its autocombat MONSTER slot (record+4), or
    //   (B) carries NO target at all (both target slots empty) while standing in the same
    //       room/map: the area channeller, who fights the whole room instead of one monster.
    // The killer is counted first (the divisor starts at 1) and is awarded unconditionally —
    // the award loop short-circuits the autocombat test for the killer.
    //
    // Bug #231: this used the monster's STICKY engagement mark (HasEngagedPlayer), which survives a
    // `break` — so a player who told their client to disable combat and started walking out still
    // collected a share when the rest of the group finished the kill. Engagement is what makes a monster
    // remember and retaliate (and what charges evil points); it is not what earns experience. Being
    // ATTACKED is not enough either: InCombat is set only by a player-initiated engage, never by a
    // monster swinging at you, which is exactly the autocombat-array membership stock tests.
    private List<Player> GetMonsterExperienceRecipients(MonsterInstance monster)
    {
        var recipients = new List<Player>();

        foreach (var player in _world.GetPlayersInRoom(monster.MapNumber, monster.RoomNumber))
        {
            // The killer always shares, in autocombat or not.
            if (ReferenceEquals(player, _player))
            {
                recipients.Add(player);
                continue;
            }

            if (!player.InCombat)
                continue;

            bool lockedOntoThisMonster = ReferenceEquals(player.CombatTarget, monster);
            bool fightsTheWholeRoom = player.CombatTarget == null && player.PlayerCombatTarget == null;
            if (lockedOntoThisMonster || fightsTheWholeRoom)
                recipients.Add(player);
        }

        // The killer isn't in the room list when the blow landed from outside it (a lingering DoT tick
        // after they moved). They still get the kill.
        if (recipients.Count == 0)
            recipients.Add(_player);

        return recipients;
    }

    /// <summary>
    /// Who the kill's QUEST payload reaches — a wider net than the experience split. The stock delivery is
    /// the dying monster's DeathSpell, an AREA cast over the room (see ApplyQuestKillProgressAsync), so it
    /// is not gated on autocombat membership the way experience distribution is: a party member who broke
    /// combat but is standing right there is still inside the blast. Kept on the monster's engagement mark.
    /// </summary>
    private List<Player> GetMonsterKillCreditRecipients(MonsterInstance monster)
    {
        var roomPlayers = _world.GetPlayersInRoom(monster.MapNumber, monster.RoomNumber)
            .Where(player => player.PlayerCombatTarget == null)
            .ToList();

        var engagedPlayers = roomPlayers
            .Where(player => monster.HasEngagedPlayer(player.Name))
            .ToList();

        if (engagedPlayers.Count == 0)
        {
            engagedPlayers = roomPlayers
                .Where(player =>
                    ReferenceEquals(player.CombatTarget, monster)
                    || (player.InCombat && player.HasIncomingMonsterAttacker(monster)))
                .ToList();
        }

        if (engagedPlayers.Count == 0)
            engagedPlayers.Add(_player);

        return engagedPlayers;
    }

    public void BroadcastCombatMessagesToRoom(IReadOnlyList<string> roomMessages)
    {
        if (roomMessages.Count == 0)
            return;

        _world.BroadcastToRoomSequence(_player.CurrentMapNumber, _player.CurrentRoomNumber, roomMessages, _client);
    }

    public Task SendCombatMessagesToCurrentPlayerAsync(IReadOnlyList<string> messages)
        => SendCombatMessagesToPlayerAsync(_player, messages);

    private async Task SendCombatMessagesToPlayerAsync(Player recipient, IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
            return;

        var recipientClient = _world.GetClientForPlayer(recipient.Name);
        if (recipientClient == null)
            return;

        // Signal that combat output landed on this player — the session's silent-meditation exit
        // watches CombatOutputSeq to interrupt on a real hit (replaces the old printedCombat return).
        System.Threading.Interlocked.Increment(ref recipient.CombatOutputSeq);

        var formattedMessages = messages
            .Select(message => message.StartsWith(MudAnsi.LinePreamble, StringComparison.Ordinal)
                ? message
                : $"{MudAnsi.LinePreamble}{message}")
            .ToList();

        bool suppress = recipient.SuppressBroadcastReprompt;

        foreach (var output in formattedMessages)
        {
            if (recipientClient.TryDeferBroadcastLine(output, reprompt: !suppress))
                continue;

            if (recipientClient.HasPendingInput)
                await recipientClient.PrepareForBroadcastAsync(suppress);

            await recipientClient.SendLineAsync(output);

            if (suppress)
                continue;

            await recipientClient.SendAsync(MudAnsi.Prompt(recipient));
        }
    }

    private async Task BroadcastCombatMessagesToObserversAsync(IReadOnlyList<string> roomMessages, Player excludedPlayer)
    {
        if (roomMessages.Count == 0)
            return;

        var observers = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            .Where(player => !player.Name.Equals(excludedPlayer.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var observer in observers)
            await SendCombatMessagesToPlayerAsync(observer, roomMessages);
    }

    private bool CanInitiateHostileAction(out string failureMessage)
    {
        failureMessage = string.Empty;

        if (_player.IsUnconscious)
        {
            failureMessage = $"{MudAnsi.BrightRed}You may not do that while you are mortally wounded!{MudAnsi.Reset}";
            return false;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room?.IsProtected == true)
        {
            failureMessage = "You are overcome with a feeling of guilt and break off your attack.";
            return false;
        }

        return true;
    }

    private bool CanCommitLawfulEvilAction(float epCost, out string failureMessage)
    {
        failureMessage = string.Empty;

        if (_player.WarnOnEvilEnabled && epCost > 0)
        {
            failureMessage = $"{MudAnsi.BrightYellow}To do this action, you must turn off your evil warnings.{MudAnsi.Reset}";
            return false;
        }

        if (_player.IsLawful && epCost > 0)
        {
            failureMessage = "You are overcome with a feeling of guilt and break off your attack.";
            return false;
        }

        return true;
    }

    // Action side: a player over the EP cap is BLOCKED from attacking an
    // innocent monster (align 0 townsfolk / 4 lawful guards, shopkeepers, the barmaid) — the cap-gate's
    // cap rejection also gates the swing in the stock attack loop. Keyed on the
    // innocent align DIRECTLY, not on the EP gain: the gain is already refused past the cap (cost funcs
    // return 0 / TryAddEvilPoints freezes), so an epCost-based check would never fire. Stock-only — the
    // EVILCAPBLOCK server toggle lets a board drop it (modern), in which case the attack proceeds.
    internal const string EvilCapBlockMessage = "You have progressed too far to the evil side to do this action.";

    internal static bool IsInnocentMonsterAlign(int align) => align == 0 || align == 4;

    private bool IsBlockedByEvilCap(int monsterAlign, out string failureMessage)
    {
        failureMessage = string.Empty;
        if (_world.EvilCapBlocksActions
            && _player.EvilPoints > Player.EvilPointGainCap
            && IsInnocentMonsterAlign(monsterAlign))
        {
            failureMessage = EvilCapBlockMessage;
            return true;
        }
        return false;
    }

    // "A dark cloud passes over you" prints every time a player's
    // evil points actually increase. Centralised here so monster aggression, PvP, offensive spells,
    // and quest scripts all surface the same stock feedback. Negative deltas (good deeds) apply
    // silently. Returns whether evil points changed.
    private async Task<bool> AddEvilPointsWithCloudAsync(float delta)
    {
        int alignmentBucketBefore = CombatEngine.GetAlignmentBucket(_player.EvilPoints);
        if (!_player.TryAddEvilPoints(delta))
            return false;

        // Colour verified byte-for-byte against stock: a markup slot is printed
        // immediately before the string, and that slot holds the markup
        //     \x1b[[  \x1b[1;30m  | \x08 \x08 \x08 \x08]
        // whose inner SGR is ESC[1;30m — BRIGHT BLACK (dark grey), fitting for a dark cloud. Unlike
        // "You have been killed!" this line carries NO ESC[79D ESC[K frame, just the colour. It used
        // to go out as a bare SendLineAsync, so it inherited the prompt's default text colour and
        // rendered White on palettes 0/1.
        if (delta > 0)
            await _client.SendLineAsync($"{MudAnsi.BrightBlack}A dark cloud passes over you{MudAnsi.Reset}");

        // A band change strips worn items the new alignment forbids.
        RevalidateWornItemsAfterAlignmentChange(_player, alignmentBucketBefore);

        return true;
    }

    public async Task HandlePlayerDeath()
    {
        // The combat messages that caused this death are sent through SendCombatMessagesToPlayerAsync,
        // which DEFERS them onto the client's broadcast queue when the player has typed-ahead input
        // (HasPendingInput). The death sequence below (the "saved"/"killed" lines + ShowRoom) writes
        // straight to the client via SendLineAsync, bypassing that queue — so with typeahead the
        // killing blow's text would otherwise flush AFTER the death-room render (out of order). Drain
        // the queue first so the cause-of-death output always precedes the death/teleport output.
        await _client.FlushDeferredBroadcastLinesAsync();

        _player.IsSneaking = false;
        _player.IsHidden = false;
        _player.ClearKnockdown();

        // WCCMMHLP.MSG: "Once you reach 0 HP, you will drop to the ground, mortally wounded"
        // "Once your HP reaches a certain negative number, usually -15, you will die."
        if (_player.CurrentHP <= Player.DeathHP)
        {
            await ExecuteForcedDeath();
        }
        else
        {
            // Mortally wounded — unconscious but not dead yet
            // CombatEngine already showed "You drop to the ground!" to the player
            // Stop the player's own combat loop, but preserve incoming attackers so
            // monsters can continue their normal cadence while the player is down.
            _player.StopCombatLoop();
            _player.CombatTarget = null;
            _player.PlayerCombatTarget = null;

            // "%s drops to the ground!" — broadcast to room
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                GameAnsi.DropsToTheGround($"{_player.Name} drops to the ground!"), _client);
        }
    }

    private async Task HandleSuicide()
    {
        if (!await VerifySuicideRerollPasswordAsync())
            return;

        // SUICIDE: with more than one life you lose a life and recall to
        // the respawn room; on your last life it is a permadeath. It never runs the combat-death
        // combat-death "miracle saved"/loot path, and there is NO realm broadcast -- only a room line.
        if (_player.Lives > 1)
        {
            int deathMap = _player.CurrentMapNumber;
            int deathRoom = _player.CurrentRoomNumber;

            // Suicide drops your corpse loot into the current room first -- all droppable
            // items + currency, keeping only Loyal (100) / Major-Curse (83) items and returning
            // DestroyOnDeath items to their rightful place -- then you lose a life and recall.
            await DropCorpseLootAsync(deathMap, deathRoom, isPermadeath: false);

            _player.Lives--;

            // With-period line + lives count. %d is not pluralized.
            await _client.SendLineAsync($"{MudAnsi.BrightRed}After a LONG thought, you take your own life.{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You now have {_player.Lives} lives remaining.{MudAnsi.Reset}");

            // Room sees the death (current room, after the loot drop and before recall).
            _world.BroadcastToRoom(deathMap, deathRoom,
                $"{MudAnsi.BrightRed}{_player.Name} took their own life!{MudAnsi.Reset}", _client);

            // Recall to the respawn room and full-restore. No room redisplay.
            var (respawnMap, respawnRoom) = _world.ResolveDeathRespawn(_player, deathMap, deathRoom);
            _player.CurrentMapNumber = respawnMap;
            _player.CurrentRoomNumber = respawnRoom;
            _player.CurrentHP = _player.MaxHP;
            _player.CurrentMana = _player.MaxMana;
            _player.IsAided = false;
            _player.IsResting = false;
            _player.IsMeditating = false;
            _player.IsSneaking = false;
            _player.IsHidden = false;
            _player.ClearCombatState();
            _world.CleanupPartyForDeath(_player);
            _world.NotifyPlayerEnteredRoom(_player);
            _world.PlayerRepo.SavePlayer(_player);
            return;
        }

        // Last life: no-period line, then permadeath (drops loot, deletes the character, back to menu).
        // The permadeath branch broadcasts nothing to the room -- only the loot drop is seen there.
        await _client.SendLineAsync($"{MudAnsi.BrightRed}After a LONG thought, you take your own life{MudAnsi.Reset}");
        await ExecuteForcedDeath(allowPermadeath: true, suppressOutcomeText: true);
    }

    private async Task HandleReroll()
    {
        if (!await VerifySuicideRerollPasswordAsync())
            return;

        // Kept experience: requires sysop setting > 0 AND player has KeepMode enabled.
        int keepPercent = _world.RerollKeepExperiencePercent;
        long keptExperience = (_player.KeepMode && keepPercent > 0)
            ? (_player.Experience * keepPercent) / 100
            : 0;

        string bbsUserId = _player.BbsUserId;
        bool preservedIsSysop = _player.IsSysop;
        bool preservedToptenDisabled = _player.IsToptenDisabled;
        string preservedSuicideRerollPassword = _player.SuicideRerollPassword;
        string playerName = _player.Name;
        int deathMap = _player.CurrentMapNumber;
        int deathRoom = _player.CurrentRoomNumber;

        // Suicide permadeath branch: corpse loot drops to the room, the
        // character is deleted, THEN the no-period "take your own life" line is shown and the player is
        // dropped to the menu. The permadeath branch does NOT broadcast "took their own life!" to the
        // room (only the loot drop is seen there). Reroll performs its own deletion (with pending-reroll
        // state), so it drops the loot directly rather than routing through ExecuteForcedDeath.
        await DropCorpseLootAsync(deathMap, deathRoom, isPermadeath: true);

        // Save pending reroll state keyed to the BBS account. The old given name is preserved so the new
        // character's name field is pre-filled with it (still editable) on the create screen.
        _world.PlayerRepo.SavePendingReroll(bbsUserId, playerName, keptExperience, preservedIsSysop, preservedToptenDisabled, preservedSuicideRerollPassword);

        // A rerolling gang leader dissolves the gang entirely; a rerolling member just drops out of it.
        if (_world.DisbandGangIfLeader(_player) == null && !string.IsNullOrWhiteSpace(_player.Gang))
            _world.PlayerRepo.RemovePlayerFromGang(playerName, _player.Gang);

        // Delete the player record to free the character name. This also clears the account→character link
        // automatically: the link is the deleted row's BbsUserId, so a next login by this account finds no
        // player and is routed into character creation (the pending-reroll flow stamps the new character's
        // BbsUserId). No BBS-side link to clear.
        _world.PlayerRepo.DeletePlayer(playerName);

        // Remove from the online world and clear client player reference.
        _world.RemovePlayer(_player, save: false);
        _client.Player = null;

        // Realm departure line kept for reroll as a QOL touch (suicide has no realm broadcast).
        _world.BroadcastToRealm(
            $"{MudAnsi.White}{playerName} has rerolled and left the Realm.{MudAnsi.Reset}",
            reprompt: true);

        // The line prints only after the character has been deleted, then the menu shows.
        await _client.SendLineAsync($"{MudAnsi.BrightRed}After a LONG thought, you take your own life{MudAnsi.Reset}");

        if (keptExperience > 0)
            await _client.SendLineAsync($"{MudAnsi.White}You will retain {keptExperience:N0} experience points on your next character.{MudAnsi.Reset}");

        ReturningToMenu = true;
    }

    // suppressOutcomeText: the caller has already printed its own death flavor (e.g. suicide's
    // "After a LONG thought..." line) and handles the room broadcast, so skip the combat-death
    // "miracle saved" / permadeath announcements and the realm broadcast. Loot drop and the
    // mechanical death (life loss, deletion, respawn) still run.
    private async Task ExecuteForcedDeath(bool allowPermadeath = true, bool suppressOutcomeText = false)
    {
        _player.IsSneaking = false;
        _player.IsHidden = false;
        _player.IsAided = false;
        _player.ClearKnockdown();
        _player.ClearCombatState();
        _world.CleanupPartyForDeath(_player);

        int deathMap = _player.CurrentMapNumber;
        int deathRoom = _player.CurrentRoomNumber;

        // Non-stock death log, recorded before the arena-exempt bail below so a no-op arena recall is NOT
        // logged as a death -- it costs no life and drops no loot, so it is not one. The killer comes from
        // Player.LastDamageSourceName, stamped by whichever damage site landed the blow.
        if (!_world.IsArenaDeathExempt(deathMap, deathRoom))
            _world.RecordPlayerDeath(_player, deathMap, deathRoom);

        // Arena/collision branch: a death in a type-5 arena
        // room while arena combat-mode is OFF ("death does not count") is a no-op recall — no life
        // lost, no loot dropped, buffs/poison left intact (spells are only cleared in the real-death
        // branch). The player is restored and bounced to the recall room.
        if (_world.IsArenaDeathExempt(deathMap, deathRoom))
        {
            await ReviveInArenaAsync(deathMap, deathRoom);
            return;
        }

        bool isArenaRoom = _world.IsArenaRoom(deathMap, deathRoom);

        // A death is announced to BOTH sides, ahead of the spell termination
        // and the loot drop:
        //   ESC[79D ESC[K + ESC[1;31;40m  "You have been killed!"  → to the victim
        //   ESC[1;31;40m                   "<name> is dead."       → to the room (victim
        //                                                                excluded — they got their own)
        // The victim half already reaches them from whichever damage source landed the killing blow
        // (CombatEngine.AppendPlayerKillMessages for PvP, the monster-attack marshal, the monster-spell
        // path), so only the ROOM half is added here — emitting it centrally too would double it.
        // Without this the room heard nothing at all: a PvP kill showed the killer their swings,
        // *Combat Off*, and then silence.
        //
        // A fatal blow prints NO "drops to the ground" in stock: that line is
        // the MORTALLY-WOUNDED announcement, and the PvP attack only calls it on the `not killed`
        // branch. So killed outright ⇒ "<name> is dead."; knocked to 0..-14 ⇒ "drops to the ground!".
        //
        // Runs for an arena death too (the arena only skips the LOOT block), hence its position above
        // the isArenaRoom branch and below the arena-exempt no-op return. Suicide is a
        // separate path with its own wording, hence the suppressOutcomeText guard.
        if (!suppressOutcomeText)
        {
            _world.BroadcastToRoom(deathMap, deathRoom,
                GameAnsi.IsDead($"{_player.Name} is dead."), _client);
        }

        // Immediately after the death lines,
        // the autocombat of everyone still targeting the corpse is broken.
        // ClearCombatState above only unwinds the VICTIM's own side, so without this an attacker keeps a
        // stale target pointer and gets one more full round in — landing on the victim after they have
        // respawned, and inside the protected death-recall room at that.
        _world.KillAutocombatAgainstPlayer(_player);

        // Death step 1: terminate every active spell/buff (10 slots) and clear the
        // poison level on death — you never respawn still buffed or still bleeding venom. Crucially
        // the termination runs per slot, so each one ANNOUNCES its
        // wear-off line in yellow ("The boils vanish from your body." under the death lines). We used
        // to Clear() the collection silently and swallow every one of them.
        _world.TerminateActiveSpellsOnDeath(_player);

        // A real death dismisses
        // the player's summoned pets (charmed creatures revert to wild). Not run on an arena no-loss death.
        _world.DismissPlayerPets(_player.Name);

        // Lives -= 1, then lives < 1 ⇒ permadeath. On the last life everything drops
        // regardless of the keep-on-death abilities (100/83), so decide before the drop loop below.
        if (_player.Lives > 0)
            _player.Lives--;
        bool isPermadeath = allowPermadeath && _player.Lives < 1;

        // Death imposes NO experience penalty (audited):
        // the stock death cost is a life + dropped loot only. (The only flat XP strip in stock is
        // an unrelated ability-removal event.) So we deliberately do NOT dock experience here.

        // The corpse-loot drop block (currency + inventory + keys +
        // worn gear) is skipped entirely for a type-5 arena room — even when arena combat-mode is ON
        // and the death counts, your gear stays with you; only a life is lost. Outside an arena the
        // full drop runs. The ability-155 "revive" text block on a carried item is captured
        // during the inventory sweep and replayed after a successful revive.
        int reviveTextBlock = isArenaRoom ? 0 : await DropCorpseLootAsync(deathMap, deathRoom, isPermadeath);

        // Lives < 1 ⇒ permadeath — the character is gone (no respawn). The corpse loot dropped
        // above stays in the death room for others.
        if (isPermadeath)
        {
            await PerformPermadeathAsync(suppressOutcomeText);
            return;
        }

        // Revive branch: announce the life loss only when the character survives.
        if (!suppressOutcomeText)
        {
            await _client.SendLineAsync("But, due to a miracle, you have been saved.");
            await _client.SendLineAsync($"You have {_player.Lives} lives left.");
        }

        var (respawnMap, respawnRoom) = _world.ResolveDeathRespawn(_player, deathMap, deathRoom);

        _player.CurrentMapNumber = respawnMap;
        _player.CurrentRoomNumber = respawnRoom;
        _player.CurrentHP = _player.MaxHP;
        _player.CurrentMana = _player.MaxMana;
        _player.IsAided = false;
        _player.IsResting = false;
        _player.IsMeditating = false;
        _world.NotifyPlayerEnteredRoom(_player);

        // Persist the respawned state before any client-side auto-disconnect/reconnect
        // can reload a stale pre-death character record.
        _world.PlayerRepo.SavePlayer(_player);

        await ShowRoom(brief: _player.BriefMode);

        // A carried item with ability 155 caches a text-block id
        // that is replayed as a special command once the character is back on its feet (e.g. the
        // tournament badge's on-revive script). Runs after the revive, never on permadeath.
        if (reviveTextBlock != 0)
            await PerformTextBlockAsSpecialCommandAsync(reviveTextBlock);
    }

    // Corpse-loot drop: currency + carried + worn items go to the death room,
    // honoring DestroyOnDeath (vanishes to its "rightful place") and, on a survivable death, the
    // keep-on-death abilities. Returns the ability-155 revive text-block id captured off a
    // carried item (0 if none). Shared by combat death, last-life suicide, and reroll.
    private async Task<int> DropCorpseLootAsync(int deathMap, int deathRoom, bool isPermadeath)
    {
        int reviveTextBlock = 0;
        var returnedNames = new List<string>();
        var retainedInventoryIds = new List<int>();
        var retainedInventoryInstanceIds = new List<long>();
        EnsureItemInstanceAlignment();
        for (int index = 0; index < _player.Inventory.Count; index++)
        {
            int itemId = _player.Inventory[index];
            long instanceId = _player.InventoryInstanceIds[index];
            _world.Database.Items.TryGetValue(itemId, out var item);
            if (item != null && item.Abilities.TryGetValue(ItemReviveTextBlockAbilityId, out var textBlock) && textBlock != 0)
                reviveTextBlock = textBlock;
            if (!isPermadeath && item != null && ItemIsRetainedOnDeath(item))
            {
                retainedInventoryIds.Add(itemId);
                retainedInventoryInstanceIds.Add(instanceId);
                continue;
            }
            // A DestroyOnDeath item is NOT dropped
            // to the floor — it vanishes privately with "Your <item> has returned to its rightful
            // place." (quest items, talismans, etc. never litter the world or get looted).
            if (item != null && item.DestroyOnDeath)
            {
                returnedNames.Add(item.Name);
                continue;
            }
            // Drop chain: the death room (with its recursive spill into adjacent
            // rooms), then the victim's 20-room movement trail, then the overflow room. Loot only
            // "returns to its rightful place" when every tier is full — it never silently vanishes
            // because the room underfoot ran out of slots.
            if (!_world.DisposeOfCorpseItem(_player, deathMap, deathRoom, itemId, instanceId) && item != null)
                returnedNames.Add(item.Name);
        }
        _player.Inventory.Clear();
        _player.InventoryInstanceIds.Clear();
        _player.Inventory.AddRange(retainedInventoryIds);
        _player.InventoryInstanceIds.AddRange(retainedInventoryInstanceIds);

        foreach (var (slot, itemId) in _player.Equipment)
        {
            long instanceId = GetEquipmentInstanceId(slot, itemId);
            // Ability 100 "Loyal Item" and ability 83
            // "Major Curse" both retain the item across death. Major
            // Curse items are still unequipped per Nightmare Redux notes -- they move to inventory.
            _world.Database.Items.TryGetValue(itemId, out var item);
            if (!isPermadeath && item != null && ItemIsRetainedOnDeath(item))
            {
                _player.Inventory.Add(itemId);
                _player.InventoryInstanceIds.Add(instanceId);
                continue;
            }
            // Worn DestroyOnDeath items also vanish to their rightful
            // place rather than dropping.
            if (item != null && item.DestroyOnDeath)
            {
                returnedNames.Add(item.Name);
                continue;
            }
            // Drop chain: the death room (with its recursive spill into adjacent
            // rooms), then the victim's 20-room movement trail, then the overflow room. Loot only
            // "returns to its rightful place" when every tier is full — it never silently vanishes
            // because the room underfoot ran out of slots.
            if (!_world.DisposeOfCorpseItem(_player, deathMap, deathRoom, itemId, instanceId) && item != null)
                returnedNames.Add(item.Name);
        }
        _player.Equipment.Clear();
        _player.EquipmentInstanceIds.Clear();

        // Corpse loot drops SILENTLY. Each carried/worn item goes to
        // the room-disposal path and each coin pile to the room's currency accumulators with no output
        // anywhere in the block — the items simply appear in the room, and onlookers learn what fell by
        // looking. (The per-denomination "%s gold drop to the ground." lines belong to the MONSTER kill,
        // a different path: a MONSTER announces its coins, a PLAYER corpse announces
        // nothing.) We used to broadcast a comma-joined dump of every dropped item name, which is not a
        // stock string in any form — there is no "<items> drop to the ground." format at all.

        // One private line per DestroyOnDeath item that returned to its rightful place.
        foreach (var returnedName in returnedNames)
            await _client.SendLineAsync($"Your {returnedName} has returned to its rightful place.");

        long totalCopper = CurrencyHelper.ToCopper(_player);
        if (totalCopper > 0)
        {
            _world.DropCurrencyInRoom(deathMap, deathRoom, _player.Runic, _player.Platinum, _player.Gold, _player.Silver, _player.Copper);
            _player.Runic = 0;
            _player.Platinum = 0;
            _player.Gold = 0;
            _player.Silver = 0;
            _player.Copper = 0;
        }

        // Worn gear was just stripped, so recompute derived stats from base + (now-empty) equipment.
        // Without this the corpse keeps the items' bonuses -- e.g. drop a +10 Acc item on suicide,
        // re-loot and re-equip it, and the bonus would stack. Idempotent full recompute.
        _player.RecalculateEquipment(_world.Database);

        return reviveTextBlock;
    }

    // Arena/collision branch: restore to the collision HP preset (we use
    // full HP/mana), recall to the death-respawn room, and announce with the colliseum message. No
    // life is lost, no loot drops, and the on-revive ability-155 text block is NOT run (stock skips it here).
    private async Task ReviveInArenaAsync(int deathMap, int deathRoom)
    {
        var (respawnMap, respawnRoom) = _world.ResolveDeathRespawn(_player, deathMap, deathRoom);

        _player.CurrentMapNumber = respawnMap;
        _player.CurrentRoomNumber = respawnRoom;
        _player.CurrentHP = _player.MaxHP;
        _player.CurrentMana = _player.MaxMana;
        _player.IsAided = false;
        _player.IsResting = false;
        _player.IsMeditating = false;

        await _client.SendLineAsync("But, because you were in a colliseum, you have been saved.");
        await _client.SendLineAsync($"You have {_player.Lives} lives left.");

        _world.NotifyPlayerEnteredRoom(_player);
        _world.PlayerRepo.SavePlayer(_player);
        await ShowRoom(brief: _player.BriefMode);
    }

    // Permadeath branch (lives < 1): the character is deleted, removed from any
    // gang, taken off the online world, and the player is kicked back to the menu. The corpse loot was
    // already dropped to the death room by ExecuteForcedDeath.
    private async Task PerformPermadeathAsync(bool suppressOutcomeText = false)
    {
        string playerName = _player.Name;

        // A fallen character is recorded in the Hall of Fame before deletion.
        _world.PlayerRepo.SaveHallOfFameEntry(new mmudreborn.Data.Models.HallOfFameEntry
        {
            PlayerName = _player.Name,
            LastName = _player.LastName ?? string.Empty,
            Level = _player.Level,
            RaceId = _player.RaceId,
            ClassId = _player.ClassId,
            Experience = _player.Experience,
            Alignment = (int)Math.Round(_player.EvilPoints),
        });

        // Combat permadeath announces the end; a voluntary suicide already printed its own line and
        // room broadcast, so it suppresses this (and the realm broadcast) -- suicide just
        // drops you to the menu after "After a LONG thought, you take your own life".
        if (!suppressOutcomeText)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You have no lives remaining!{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Your adventure has come to an end -- your character is gone forever.{MudAnsi.Reset}");
        }

        // A gang leader's permadeath dissolves the gang entirely; a member's just drops them out of it.
        if (_world.DisbandGangIfLeader(_player) == null && !string.IsNullOrWhiteSpace(_player.Gang))
            _world.PlayerRepo.RemovePlayerFromGang(playerName, _player.Gang);

        _world.PlayerRepo.DeletePlayer(playerName);
        _world.RemovePlayer(_player, save: false);
        _client.Player = null;

        if (!suppressOutcomeText)
        {
            _world.BroadcastToRealm(
                $"{MudAnsi.BrightRed}{playerName} has perished, never to return to the Realm.{MudAnsi.Reset}",
                reprompt: true);

            await _client.SendLineAsync();
            await _client.SendLineAsync($"{MudAnsi.BrightYellow}Return to the main menu to create a new character.{MudAnsi.Reset}");
            await _client.SendLineAsync();
        }

        ReturningToMenu = true;
    }

    private void AddGangExperience(long gained)
    {
        AddGangExperience(_player, gained);
    }

    private static void AddGangExperience(Player player, long gained)
    {
        if (gained <= 0)
            return;

        if (string.IsNullOrWhiteSpace(player.Gang))
            return;

        player.GangExperience += gained;
    }

    // Port of the stock `forgive` command.
    // The caller (the victim of a PvP attack) forgives a named attacker who is present in the room:
    // the directed retaliation edge (attacker → caller) is cleared and the EP that attack charged is
    // refunded to THE ATTACKER, then the attacker's worn-item gating is recalculated. The attacker
    // must be in the caller's room (target resolution searches the room).
    private async Task HandleForgive(string args)
    {
        var name = (args ?? string.Empty).Trim();
        if (name.Length == 0)
            return;   // no argument → no-op

        var target = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, name, _player);
        if (target == null)
        {
            await _client.SendLineAsync($"You do not see {name} here!");
            return;
        }

        if (_world.EvilTimers.TryForgive(target.Name, _player.Name, out float refunded))
        {
            // The charged amount comes straight off the attacker's EvilPoints (raw, not via
            // the evil-point gain path); positive EP = evil, so this moves the attacker toward good.
            int alignmentBucketBefore = CombatEngine.GetAlignmentBucket(target.EvilPoints);
            if (refunded != 0f)
                target.EvilPoints -= refunded;
            // A band change strips items the new alignment forbids.
            RevalidateWornItemsAfterAlignmentChange(target, alignmentBucketBefore);

            _world.SendToPlayer(target.Name, "The gods have forgiven you for your action.", reprompt: true);
            await _client.SendLineAsync($"The gods have forgiven {target.Name} for {target.HisHer_Lower} action.");
        }
        else
        {
            await _client.SendLineAsync($"The gods refuse to forgive {target.Name} for {target.HisHer_Lower} actions.");
        }
    }

}
