using System.Threading;
using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

// Item C — the single world-coordinated combat tick. Ground truth is stock:
// the background-energy pass resolves ALL
// combat for the realm in one ordered pass on a single background thread each beat — (1) a backstab
// pre-pass fires every queued backstab first, then (2) a 60/40 coin-flip
// (a roll of 0..99 < 60) picks normal player attacks vs monster attacks.
// A backstab therefore lands before every normal swing and every monster swing that beat (bug #70),
// and the monster pass can faithfully CHOOSE which of several players to hit.
//
// We previously resolved combat per-session (GameSession.ProcessRealtimeCombatTick on each player's
// 250ms input poll), so cross-player ordering raced. This coordinator owns per-beat resolution
// instead; sessions only render. The whole beat runs under the single global WorldStateGate, which
// command processing also takes — reproducing the stock one cooperative thread, where the round
// (the round) and a user command never overlap. So no player swing or monster swing is ever skipped
// because someone was mid-command, and combat state is never mutated by a command and the round at once.
//
// Stage 1 (this commit) is a behaviour-preserving LIFT: players are visited in plain enumeration
// order, exactly reproducing the old per-session behaviour but from one place. Stage 2 inserts the
// backstab pre-pass + 60/40 ordering into ProcessCombatBeatAsync; Stage 3 adds faithful cross-player
// monster target selection. Both hook into the single per-beat pass below.
public partial class GameWorld
{
    // Poll cadence for the coordinator. The combat round itself is on the 5s grid
    // (GetNextCombatPulseUtc / NextMonsterAttackAtUtc); this 250ms cadence matches the old per-session
    // input-poll interval so a due beat is picked up with the same latency as before, and idle
    // encounter checks (CheckEncounters) run at the same frequency they used to per session.
    private static readonly TimeSpan CombatTickInterval = TimeSpan.FromMilliseconds(250);
    private int _combatTickInProgress;
    private bool _useManualCombatTicksForTests;

    private void CombatTick(object? state)
    {
        if (_useManualCombatTicksForTests)
            return;

        // Skip if the previous beat is still resolving (sequential per-player I/O can occasionally
        // exceed one 250ms slice with many players engaged); a skipped slice just defers a beat to
        // the next slice, never double-resolves.
        if (Interlocked.Exchange(ref _combatTickInProgress, 1) == 1)
            return;

        _ = RunCombatBeatAsync();
    }

    private async Task RunCombatBeatAsync()
    {
        try
        {
            await ProcessCombatBeatAsync(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            GameDiagnostics.RecordBackgroundException(ex, "tick:combat");
            if (GameDiagnostics.RethrowBackgroundExceptions) throw;
        }
        finally
        {
            Volatile.Write(ref _combatTickInProgress, 0);
        }
    }

    // One combat beat across the whole realm, ordered to match the stock background pass:
    //   1. HOUSEKEEPING for every player (prune, clear gone targets, "*Combat Off*", idle encounters).
    //   2. BACKSTAB PRE-PASS: every due backstabber's own swing, ahead of all else (#70).
    //   3. 60/40 COIN-FLIP (a roll of 0..99 < 60): the remaining players' own swings and
    //      the monster swings run in one order or the other.
    //   4. FINALIZE: reschedule the next 5s beat (deferred so the 60/40 order can't drop a beat).
    //
    // Each phase runs as a separate global pass so player swings and monster swings interleave across
    // players exactly as the two separate stock passes do. The backstabber set is snapshotted before
    // any resolution (the swing resets PendingCombatRoundAction Backstab→Attack) so a backstabber
    // swings in the pre-pass ONLY, never again in the player pass — the fire-once semantic.
    internal async Task ProcessCombatBeatAsync(DateTime now)
    {
        // The stock round runs entirely on the single cooperative thread, so no user
        // command can mutate combat state mid-round. Hold the world gate for the whole beat to
        // reproduce that exactly — every due player's own swing + every monster swing + finalize
        // resolve atomically, and a monster attack is never skipped because a player is mid-command.
        await WorldStateGate.WaitAsync();
        long gateAcquiredTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        GameDiagnostics.MarkGateAcquired("combat");
        try
        {
            await ProcessCombatBeatInnerAsync(now);
        }
        finally
        {
            GameDiagnostics.RecordGateHold("combat", gateAcquiredTimestamp);
            GameDiagnostics.MarkGateReleased();
            WorldStateGate.Release();
        }
    }

    private async Task ProcessCombatBeatInnerAsync(DateTime now)
    {
        // Out-of-realm players (in the TRAIN STATS editor) have "left the Realm" — they take no combat
        // beat at all: no own swing, no monster can pick them as a candidate, no idle encounter aggro.
        var players = _onlinePlayers.Values.Where(p => !p.IsOutOfRealm).ToList();

        // The per-player hits-this-tick counter is a fresh-per-beat accumulator that
        // spreads monster damage across a party; clear it for everyone before any swing lands this beat.
        // Also re-marshal each fighter's blinding-bright light state (stock recomputes
        // the light level per marshal) so the −10 AV/dodge penalty is current for this beat's swings.
        foreach (var player in players)
        {
            player.ResetIncomingHitsThisTick();
            RefreshBrightLightBlindness(player);
        }

        foreach (var player in players)
            await RunPlayerCombatPhaseAsync(player, CommandParser.CombatBeatPhase.Housekeeping, due: true);

        // Who takes a beat is decided ONCE, here, before any swing resolves — the beat's roster, not a
        // predicate re-read per phase. The stock round makes the same commitment: it walks the
        // engaged list it entered the round with, so a player's own action inside the round cannot remove
        // them from the monster pass that follows. Re-reading NextMonsterAttackAtUtc per phase broke that:
        // a queued combat-SPELL round re-engages its target as it resolves (ExecuteOffensiveSpellAgainstMonsterAsync
        // → EngageCombatAsync), which pushes the deadline to the NEXT pulse mid-beat. On the ~60% of beats
        // where the player pass runs first, the caster then read as "not due" and every monster attacking
        // them was silently skipped — a nuking mage took roughly 60% fewer swings than a meleer, since the
        // melee round never re-engages. Snapshotting keeps dueness invariant for the whole beat.
        var dueThisBeat = new HashSet<Player>(players.Where(p => IsCombatBeatDue(p, now)));

        var backstabbers = SelectDueBackstabbers(players, now);
        var backstabberSet = new HashSet<Player>(backstabbers);
        foreach (var player in backstabbers)
            await RunPlayerCombatPhaseAsync(player, CommandParser.CombatBeatPhase.OwnAttack, dueThisBeat.Contains(player));

        bool playerAttacksFirst = _rng.Next(0, 100) < 60;

        async Task PlayerAttackPassAsync()
        {
            foreach (var player in players)
            {
                if (!backstabberSet.Contains(player)) // backstabbers already swung in the pre-pass
                    await RunPlayerCombatPhaseAsync(player, CommandParser.CombatBeatPhase.OwnAttack, dueThisBeat.Contains(player));
            }
        }

        async Task MonsterAttackPassAsync() => await ProcessMonsterAttackPassAsync(players, dueThisBeat);

        if (playerAttacksFirst)
        {
            await PlayerAttackPassAsync();
            await MonsterAttackPassAsync();
        }
        else
        {
            await MonsterAttackPassAsync();
            await PlayerAttackPassAsync();
        }

        // Re-grant the once-per-round cast token, exactly where stock does. The round runs
        // (player swings + monster swings, in the 60/40 order mirrored above) and THEN walks every
        // terminal re-setting the cast token — SET means "may cast", and
        // a cast consumes it. So a spell fired by autocombat inside the round spends
        // the token and the post-round grant immediately hands it back: stock lets you type `blur` right
        // after a magic-missile round and it casts (dropping combat first).
        //
        // We instead pushed the block to the NEXT pulse on every cast, so the auto-repeating combat spell
        // kept the caster locked out for the whole inter-round window — "You have already cast a spell
        // this round!" for something stock allows. Granting only to players whose round actually resolved
        // keeps one-cast-per-round intact for everyone else: a manual cast still sets the block to the
        // next pulse, and a player who is not due (or out of combat entirely) clears on that 5s grid
        // rather than on this 250ms poll.
        foreach (var player in dueThisBeat)
            player.NextSpellAllowedAtUtc = DateTime.MinValue;

        foreach (var player in players)
            await RunPlayerCombatPhaseAsync(player, CommandParser.CombatBeatPhase.Finalize, dueThisBeat.Contains(player));
    }

    // The due backstabbers for this beat (queued attack type 4), factored out as a pure function so the
    // pre-pass selection is deterministically unit-testable without the telnet/session stack.
    internal static List<Player> SelectDueBackstabbers(IReadOnlyList<Player> players, DateTime now)
        => players
            .Where(p => p.PendingCombatRoundAction == PlayerCombatRoundAction.Backstab && IsCombatBeatDue(p, now))
            .ToList();

    private static bool IsCombatBeatDue(Player player, DateTime now)
        => player.NextMonsterAttackAtUtc != DateTime.MinValue && now >= player.NextMonsterAttackAtUtc;

    private async Task RunPlayerCombatPhaseAsync(Player player, CommandParser.CombatBeatPhase phase, bool due)
    {
        var client = GetClientForPlayer(player.Name);
        if (client == null)
            return; // No connected session to resolve/render for.

        // No per-player gate: the whole beat runs under WorldStateGate, so no command for this player
        // can be processing concurrently (the single-thread invariant). The phase therefore always
        // runs and is never skipped on contention.
        var parser = new CommandParser(client, this, player);
        await parser.RunCombatBeatPhaseAsync(phase, due);
    }

    // The monster-centric attack pass (Item C stage 3). Where the old
    // per-player MonsterAttack phase let a monster shared across N players swing at ALL N, stock has
    // each monster pick exactly ONE target per beat: its locked target if that player is present,
    // otherwise a cross-player spread pick. After swinging, the monster re-rolls its lock — focus on a
    // FollowPercent pass, drop-and-re-spread on an aggressive fail — so damage fans out across a party.
    private async Task ProcessMonsterAttackPassAsync(IReadOnlyList<Player> players, HashSet<Player> dueThisBeat)
    {
        foreach (var (monster, candidates) in GatherMonstersWithCandidates(players, dueThisBeat))
        {
            var target = SelectMonsterAttackTarget(monster, candidates);
            if (target != null)
                await ResolveMonsterSwingAsync(monster, target);
        }
    }

    // Each in-combat monster paired with its ordered candidate targets. A candidate is a player who is
    // due this beat, alive, not in a protected room, and currently has the monster in their incoming
    // list while sharing its room — i.e. a player the monster may legally swing at under our existing
    // aggro/engage rules. Players are visited in enumeration (terminal) order so the spread pick and the
    // last-eligible fallback are deterministic; monsters appear in first-seen order.
    private List<(MonsterInstance Monster, List<Player> Candidates)> GatherMonstersWithCandidates(
        IReadOnlyList<Player> players, HashSet<Player> dueThisBeat)
    {
        var order = new List<MonsterInstance>();
        var candidatesByMonster = new Dictionary<MonsterInstance, List<Player>>();

        foreach (var player in players)
        {
            // Dueness is the beat-start snapshot, never a fresh read: the player pass may already have
            // run and pushed this player's deadline forward (see ProcessCombatBeatInnerAsync).
            if (!dueThisBeat.Contains(player) || player.CurrentHP <= Player.DeathHP)
                continue;
            if (GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber)?.IsProtected == true)
                continue;   // monster->player attacks are hard-gated in a protected room

            foreach (var monster in player.SnapshotIncomingMonsterAttackers())
            {
                if (monster.IsDead ||
                    monster.MapNumber != player.CurrentMapNumber ||
                    monster.RoomNumber != player.CurrentRoomNumber)
                {
                    continue;
                }

                if (!candidatesByMonster.TryGetValue(monster, out var list))
                {
                    list = [];
                    candidatesByMonster[monster] = list;
                    order.Add(monster);
                }

                list.Add(player);
            }
        }

        return order.Select(monster => (monster, candidatesByMonster[monster])).ToList();
    }

    // Target choice for one monster: the locked target if it is present (the engaged
    // branch), otherwise the unengaged spread pick (genrdn(0,100) < 50 - 5*hits, first-pass-wins /
    // last-eligible fallback). Returns null when the monster has no one to hit this beat.
    private Player? SelectMonsterAttackTarget(MonsterInstance monster, List<Player> candidates)
    {
        if (candidates.Count == 0)
            return null;

        if (monster.HasLockedTarget)
        {
            var locked = candidates.FirstOrDefault(c => monster.IsLockedOnTarget(c.Name));
            if (locked != null)
                return locked;                 // engaged: keep focusing the locked target

            monster.ClearLockedTarget();       // locked target left/died — re-spread among the rest
        }

        // Unengaged spread pick. We deliberately do NOT re-apply the stock align-0/3/4 exclusion here:
        // our candidate set already encodes only legitimately-aggroed/engaged players, so a guard (align
        // 4) that aggroed an evil player would otherwise never swing at them before they swing back. The
        // evil-NPC fellow-evil cut (EvilPoints > 39) IS preserved (IsEligibleSpreadTarget).
        var hits = new int[candidates.Count];
        var eligible = new bool[candidates.Count];
        for (int i = 0; i < candidates.Count; i++)
        {
            hits[i] = candidates[i].IncomingHitsThisTick;
            eligible[i] = IsEligibleSpreadTarget(monster, candidates[i]);
        }

        int index = CombatEngine.ChooseUnengagedAttackTarget(hits, eligible, () => _rng.Next(0, 100));
        return index >= 0 ? candidates[index] : null;
    }

    // Test hook: drive a text-block special command against an online player through a real CommandParser,
    // exactly as the jail hit-spell (583 → text 1509 → 9624) reaches `cast 643`. Lets a test verify the
    // jail-time debuff is actually ADDED to the prisoner (the timer that drives the auto-release).
    public async Task RunTextBlockSpecialCommandOnPlayerForTests(Player player, int textBlockId)
    {
        var client = GetClientForPlayer(player.Name)
            ?? throw new InvalidOperationException($"No connected client for player '{player.Name}'.");
        var parser = new CommandParser(client, this, player);
        await parser.PerformTextBlockSpecialCommandForTests(textBlockId);
    }

    // An evil NPC (align 6) does not newly
    // grab a FELLOW-EVIL player — one with EvilPoints > 39 — unless that player is fighting it RIGHT
    // NOW. The gate is `align==6 && EvilPoints > 39 && !fighting`, where fighting means "this player's
    // autocombat target is this monster AND they are in autocombat" — a live state, not a record that
    // they once hit it. (Keying this on the never-cleared
    // engaged set let a duergar an Outlaw had hit once go on picking them for the rest of its life.)
    // That field is EvilPoints (PROVEN: the "Lawful?" prompt seeds it to
    // = -51, the EvilPoints lawful seed; it is clamped to -200/300, impossible for a Level). This was
    // previously misread as a LEVEL cut, which wrongly spared every level-40+ player (incl. good ones)
    // from evil-NPC aggro. It mirrors the main aggro gate (ShouldMonsterAggro align-6: EP < 40).
    internal static bool IsEligibleSpreadTarget(MonsterInstance monster, Player player)
        => monster.Template.Align != 6
           || player.EvilPoints < CombatEngine.EvilNpcAggroEvilPointsCap
           || (player.InCombat && ReferenceEquals(player.CombatTarget, monster));

    // Resolve one monster's single swing against its chosen target, routed to that player's client. The
    // whole beat holds WorldStateGate, so this never races the target's own command processing and the
    // swing is never dropped. The hit counter is bumped before the swing (as stock does), then
    // the post-swing lock roll focuses or re-spreads.
    private async Task ResolveMonsterSwingAsync(MonsterInstance monster, Player target)
    {
        var client = GetClientForPlayer(target.Name);
        if (client == null)
            return;

        // The whole beat holds WorldStateGate, so no command can race this swing — the monster's
        // attack is never dropped on contention (the bug this fixes). Re-validate against earlier
        // swings THIS beat: the candidate list was snapshotted before any swing, so an earlier monster
        // may have killed the target, or a player swing may have, or the pairing drifted out of room.
        if (monster.IsDead ||
            target.CurrentHP <= Player.DeathHP ||
            monster.MapNumber != target.CurrentMapNumber ||
            monster.RoomNumber != target.CurrentRoomNumber)
        {
            return;
        }

        target.RecordIncomingHitThisTick();
        // The monster attack clears rest+meditate before the swing resolves — being attacked wakes
        // you. (Mirrored in the monster-spell path so an AoE drop can't leave you "still resting".)
        target.BreakRestAndMeditate();
        var parser = new CommandParser(client, this, target);
        await parser.ResolveSingleMonsterSwingAsync(monster);
        ApplyMonsterPostAttackLock(monster, target.Name);
    }

    // Post-swing lock roll: focus the just-hit player on a
    // FollowPercent pass, or (aggressive) drop the lock to re-spread next beat. A charmed/summoned pet
    // serves its owner and never manages a combat lock this way. Runs after EVERY monster swing at a
    // player — the monster pass and the departing free swing alike — because both are the one stock
    // monster attack. That attack also zeroes the monster's lost-target counter, so a monster that
    // is actually landing swings never lets its lock lapse (see ProcessHostileLockMisses).
    internal void ApplyMonsterPostAttackLock(MonsterInstance monster, string targetName)
    {
        monster.FollowAbandonTicks = 0;

        if (monster.HasPlayerOwner || monster.IsSummonedCreature)
            return;

        int roll = _rng.Next(1, 100);   // genrdn(1,100)
        switch (CombatEngine.ResolvePostAttackLock(
                    monster.Template.Align, monster.Template.Group, monster.Template.FollowPercent,
                    monster.HasLockedTarget, roll))
        {
            case CombatEngine.MonsterLockUpdate.Lock: monster.SetLockedTarget(targetName); break;
            case CombatEngine.MonsterLockUpdate.Clear: monster.ClearLockedTarget(); break;
        }
    }

    // A hostile lock lapses once its monster has gone 16 updates without reaching the player it names.
    // The fast monster update counts a miss on every pass where the locked player is offline or in
    // another room, and past 15 it blanks the target-name slot; any swing zeroes the count
    // (ApplyMonsterPostAttackLock). Sharing the room is neither a miss nor a reset. Without this a lock
    // never ended: a duergar that took its departing swing at an Outlaw and then failed its chase roll
    // would attack them on sight whenever they next met, because a locked monster skips the alignment
    // test. Stock keeps this count and the pet owner-follow count in the same byte, so it runs on the
    // pet pass's cadence with the same limit.
    internal void ProcessHostileLockMisses()
    {
        List<MonsterInstance> locked;
        lock (_monsterLock)
        {
            locked = _roomMonsters.Values.SelectMany(list => list)
                .Where(m => !m.IsDead && !m.HasPlayerOwner && m.HasLockedTarget).ToList();
        }

        foreach (var monster in locked)
        {
            // Exact name only — the slot holds a full name, and a prefix match could hand the lock to a
            // different, longer-named player while its real target is offline.
            if (_onlinePlayers.TryGetValue(monster.LockedTargetName!, out var target)
                && target.CurrentMapNumber == monster.MapNumber
                && target.CurrentRoomNumber == monster.RoomNumber)
            {
                continue;
            }

            if (++monster.FollowAbandonTicks > PetFollowAbandonLimit)
            {
                monster.ClearLockedTarget();
                monster.FollowAbandonTicks = 0;
            }
        }
    }

    // The light-level clamp / blinding-bright threshold: room-wide light caps at 900 ("Daylight"),
    // and an effective level strictly above 900 reads as "You are blind!" (the marshal docks
    // −10 AV / −10 dodge-skill, the same as magical blindness).
    private const int BlindingBrightLightThreshold = 900;

    // A player is "blinded by bright
    // light" when the effective light at their room exceeds 900. The light level keeps two channels: a
    // ROOM-WIDE term (room ambient + every occupant's room-light / readied source) CLAMPED to 900, plus
    // the viewer's OWN light-source ability 0xd ("Alter User Light": racial infravision, glow items),
    // added AFTER the clamp — so a celestial room (ambient 900–1000 → clamps to 900) plus any personal
    // light tips the total over 900. Recomputed each combat marshal; transient (IsBlindedByBrightLight).
    // NB: we fold readied room-light sources into neither channel here — every stock blinding-bright room
    // (the 45 Map-17 celestial rooms) has ambient ≥ 900 that already saturates the room-wide clamp on its
    // own, so they cannot change the >900 outcome; the viewer's own 0xd light is what tips it over.
    public void RefreshBrightLightBlindness(Player player)
    {
        var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (room is null)
        {
            player.IsBlindedByBrightLight = false;
            return;
        }

        int roomWide = room.Light;
        foreach (var occupant in GetPlayersInRoom(player.CurrentMapNumber, player.CurrentRoomNumber))
            roomWide += occupant.RoomIllumination;            // ability 0xe, summed over occupants
        if (roomWide > BlindingBrightLightThreshold)
            roomWide = BlindingBrightLightThreshold;           // clamp BEFORE adding the viewer channel

        int viewerUserLight = player.Illumination + SumWornUserLight(player);  // ability 0xd channel
        player.IsBlindedByBrightLight = roomWide + viewerUserLight > BlindingBrightLightThreshold;
    }

    // Sum of ability-13 ("Alter User Light") values across the player's worn + carried items. This is the
    // viewer-only light that RecalculateStats deliberately leaves out of Player.Illumination (see
    // GetWornUserLightBonus), so it must be added back here for the viewer channel.
    private int SumWornUserLight(Player player)
    {
        int bonus = 0;
        var counted = new HashSet<int>();
        foreach (var (_, itemId) in player.Equipment)
            if (counted.Add(itemId) && Database.Items.TryGetValue(itemId, out var item)
                && item.Abilities.TryGetValue(13, out int value))
                bonus += value;
        foreach (int itemId in player.Inventory)
            if (counted.Add(itemId) && Database.Items.TryGetValue(itemId, out var item)
                && item.Abilities.TryGetValue(13, out int value))
                bonus += value;
        return bonus;
    }

    // Test hooks: flip the live timer off and step beats deterministically. Mirrors the room-spell
    // manual-tick pattern (UseManualRoomSpellTicksForTests / AdvanceRoomSpellSecondsForTests).
    public void UseManualCombatTicksForTests() => _useManualCombatTicksForTests = true;

    // Restore the live 250ms combat timer. Manual mode is a one-way switch otherwise, so a test that
    // enabled it must call this (in a finally) or every later test sharing this world instance would run
    // with combat frozen.
    public void ResumeAutomaticCombatTicksForTests() => _useManualCombatTicksForTests = false;

    public void AdvanceCombatBeatsForTests(int count)
    {
        _useManualCombatTicksForTests = true;
        for (int index = 0; index < count; index++)
            ProcessCombatBeatAsync(DateTime.UtcNow).GetAwaiter().GetResult();
    }
}
