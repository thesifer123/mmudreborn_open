using System.Collections.Concurrent;
using System.Text.Json;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

public partial class GameWorld
{
    // ── Unique-monster regen timer (the spawn gate) ──────────────────────
    // Stock only timer-gates monsters whose template limit is exactly 1 AND whose RegenTime
    // (in HOURS) is non-zero — i.e. uniques like the chimera (313, 13h) or huge basilisk
    // (312, 9h). Everything else (slime beast/cave worm/mummy with RegenTime 0, or non-unique
    // lair fauna) is NOT gated and keeps the immediate spawn-on-enter / lair cadence.
    private static bool IsTimerRegenMonster(Monster template)
        => template.GameLimit == 1 && template.RegenTime > 0;

    // True when a timer-gated monster is eligible to (re)spawn: either it has no recorded death this
    // session (no last-death stamp skips the gate) or enough time has elapsed.
    // Mirrors the spawn: threshold = RegenTime*60 minutes ± a jitter of base/8 (~12.5%),
    // re-rolled on every attempt, compared against minutes since the last death.
    private bool IsRegenTimerElapsed(Monster template)
    {
        if (!IsTimerRegenMonster(template))
            return true;
        if (!_monsterRegenLastDeathUtc.TryGetValue(template.Number, out var lastDeathUtc))
            return true;

        double baseMinutes = template.RegenTime * 60.0;
        // lngrnd(0, base/4) - base/8  →  uniform in [-base/8, +base/8)  (±12.5%).
        double jitterMinutes = _rng.NextDouble() * (baseMinutes / 4.0) - (baseMinutes / 8.0);
        double thresholdMinutes = baseMinutes + jitterMinutes;
        return (DateTime.UtcNow - lastDeathUtc).TotalMinutes >= thresholdMinutes;
    }

    // The kill stamps the template's last-death date/time on death for any
    // LIMITED monster (MaxSpawn != 0). The spawn reads that ONE stamp for BOTH gates it
    // drives: the RegenTime respawn cooldown (timer-uniques) AND the guaranteed-first-drop override
    // (any limited monster — see ShouldGuaranteeFirstDrop). So we record every GameLimit>0 death, not
    // just timer-gated uniques; IsRegenTimerElapsed still no-ops for non-timer monsters, so the extra
    // ledger entries never affect respawn timing.
    private void RecordLimitedMonsterDeath(Monster template)
    {
        if (template.GameLimit <= 0)
            return;

        _monsterRegenLastDeathUtc[template.Number] = DateTime.UtcNow;
        PersistMonsterRegenState();
    }

    // Guaranteed-first-drop gate: a LIMITED monster (GameLimit/MaxSpawn
    // != 0) whose template has NO recorded death yet — its first-ever spawn — carries ALL of its loot at
    // 100%, bypassing the per-item drop-% roll (RollCarriedTreasure). The same last-death stamp
    // (_monsterRegenLastDeathUtc) that gates the RegenTime respawn cooldown gates this: once
    // the monster first dies the stamp is set and every later spawn rolls normally. So a boss's FIRST
    // kill always yields its full drop table (the speed-run "first clear"), regardless of per-item %.
    internal bool ShouldGuaranteeFirstDrop(Monster template)
        => template.GameLimit > 0 && !_monsterRegenLastDeathUtc.ContainsKey(template.Number);

    /// <summary>One spent first-kill drop: a limited monster that has already died at least once here.</summary>
    public readonly record struct FirstDropLedgerEntry(int MonsterNumber, string Name, DateTime DiedAtUtc);

    // ── SYSOP FIRSTDROP support ───────────────────────────────────────────────────────────────
    // The ledger below is per-REALM by construction: every realm is its own game process against its
    // own database, and the stamp lives in that database's ServerSettings (MonsterRegenDeaths). Nothing
    // about it is shared or aggregated across realms, so arming/spending on Main never touches PvP.

    /// <summary>True once this limited monster has died here — its guaranteed first-kill drop is spent.</summary>
    public bool IsFirstDropSpent(int monsterNumber)
        => _monsterRegenLastDeathUtc.ContainsKey(monsterNumber);

    /// <summary>Every limited monster whose guaranteed first-kill drop has been spent, newest death first.</summary>
    public List<FirstDropLedgerEntry> GetSpentFirstDrops()
    {
        var entries = new List<FirstDropLedgerEntry>();
        foreach (var kvp in _monsterRegenLastDeathUtc)
        {
            string name = Database.Monsters.TryGetValue(kvp.Key, out var template) ? template.Name : "(unknown monster)";
            entries.Add(new FirstDropLedgerEntry(kvp.Key, name, kvp.Value));
        }

        entries.Sort((left, right) => right.DiedAtUtc.CompareTo(left.DiedAtUtc));
        return entries;
    }

    /// <summary>Limited templates in this realm that still have their guaranteed first kill available.</summary>
    public int CountArmedFirstDrops()
        => Database.Monsters.Values.Count(template => ShouldGuaranteeFirstDrop(template));

    /// <summary>
    /// Re-arm one monster's guaranteed first-kill drop (SYSOP FIRSTDROP RESET). Drops the last-death
    /// stamp, so its next spawn carries its whole drop table again. Because the guarantee is decided at
    /// SPAWN, any instance already standing in the world was rolled without it — those are re-rolled in
    /// place here so the sysop doesn't have to wait out a respawn to see the effect.
    /// Returns false when the monster had no stamp (already armed, or never limited).
    /// </summary>
    public bool RearmFirstDrop(int monsterNumber, out int liveInstancesRerolled)
    {
        liveInstancesRerolled = 0;
        if (!_monsterRegenLastDeathUtc.TryRemove(monsterNumber, out _))
            return false;

        PersistMonsterRegenState();
        liveInstancesRerolled = RerollLiveInstancesForFirstDrop(number => number == monsterNumber);
        return true;
    }

    /// <summary>
    /// Re-arm EVERY monster's guaranteed first-kill drop (SYSOP FIRSTDROP RESET ALL) — a new "first
    /// clear" season for this realm. Returns how many spent stamps were cleared.
    /// </summary>
    public int RearmAllFirstDrops(out int liveInstancesRerolled)
    {
        int cleared = _monsterRegenLastDeathUtc.Count;
        _monsterRegenLastDeathUtc.Clear();
        PersistMonsterRegenState();
        liveInstancesRerolled = RerollLiveInstancesForFirstDrop(_ => true);
        return cleared;
    }

    // Re-roll carried treasure at the guarantee for every LIVING instance whose template just became
    // armed again. Dead instances are skipped: a dead lair mob has already handed its loot to the room,
    // and a dead primary NPC re-rolls on revive (EnsureRoomNpcPresent) through the same gate.
    private int RerollLiveInstancesForFirstDrop(Func<int, bool> matchesTemplate)
    {
        int rerolled = 0;
        foreach (var list in _roomMonsters.Values)
        {
            // Snapshot to a local: another thread may mutate the list mid-scan.
            var snapshot = list;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var monster = snapshot[i];
                if (monster.IsDead || !matchesTemplate(monster.Template.Number))
                    continue;
                if (!ShouldGuaranteeFirstDrop(monster.Template))
                    continue;

                monster.RollCarriedTreasure(guaranteeAllDrops: true);
                rerolled++;
            }
        }

        return rerolled;
    }

    private sealed class PersistedMonsterRegenDeath
    {
        public int Number { get; init; }
        public DateTime DiedAtUtc { get; init; }
    }

    private void LoadPersistedMonsterRegenState()
    {
        _monsterRegenLastDeathUtc.Clear();

        string json = PlayerRepo.GetServerSettingText(MonsterRegenStateSettingKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return;

        try
        {
            var entries = JsonSerializer.Deserialize<List<PersistedMonsterRegenDeath>>(json) ?? [];
            foreach (var entry in entries)
            {
                if (entry.Number > 0)
                    _monsterRegenLastDeathUtc[entry.Number] = entry.DiedAtUtc;
            }
        }
        catch (JsonException)
        {
            // Corrupt blob: drop it and rewrite a clean (empty) one.
            PersistMonsterRegenState();
        }
    }

    private void PersistMonsterRegenState()
    {
        var entries = _monsterRegenLastDeathUtc
            .Select(kvp => new PersistedMonsterRegenDeath { Number = kvp.Key, DiedAtUtc = kvp.Value })
            .OrderBy(entry => entry.Number)
            .ToList();

        PlayerRepo.SetServerSettingText(MonsterRegenStateSettingKey, JsonSerializer.Serialize(entries));
    }

    public DateTime? GetRoomLairSpawnDeadline(int mapNumber, int roomNumber)
    {
        var key = (Map: mapNumber, Room: roomNumber);
        if (!_roomNextLairSpawnAtUtc.TryGetValue(key, out var deadline))
            return null;

        if (deadline <= DateTime.UtcNow)
        {
            _roomNextLairSpawnAtUtc.TryRemove(key, out _);
            return null;
        }

        return deadline;
    }

    // The room's primary NPC (Room.NPC) respawns on player entry, not on a timer, so it has no
    // countdown here; the regen clock reflects only the lair/pressure vacancy timer.
    public DateTime? GetNextRoomSpawnDeadline(int mapNumber, int roomNumber)
        => GetRoomLairSpawnDeadline(mapNumber, roomNumber);

    private List<MonsterMovementAnnouncement> ProcessMonsterMovement()
    {
        var movers = new List<(MonsterInstance Monster, (int Map, int Room) FromKey, RoomExitDefinition Exit, (int Map, int Room) To)>();
        var announcements = new List<MonsterMovementAnnouncement>();

        // The medium monster update throttles ambient wander to 3 rolled move-attempts realm-wide per
        // medium tick, so roaming stays sparse. A Guard (Group 5) may move past the
        // budget when it is "dirty" (recently changed), so it is tracked separately from the cap.
        int wanderBudget = 0;
        const int WanderBudgetPerTick = 3;

        // Snapshot first: MoveMonster mutates _roomMonsters, so we can't relocate mid-iteration. The
        // wander DECISION (Group eligibility, the aggression roll, and the budget spend) happens here,
        // faithfully; the chosen relocation is applied in the second loop.
        foreach (var kvp in _roomMonsters)
        {
            foreach (var monster in kvp.Value)
            {
                if (monster.IsDead) continue;
                if (monster.IsPermanentNPC || IsRoomBoundMonster(monster.Template)) continue;
                // A monster with a locked pursuit target chases (the pursuit path); it doesn't ambient-wander.
                if (monster.HasLockedTarget) continue;
                if (IsMonsterMovementSkipped(monster)) continue;

                // Monster movement hard-blocks Stationary (Type 3) from relocating at all.
                if (monster.Template.Type == 3) continue;

                // Ambient-wander ELIGIBILITY keys on the GROUP/class field, NOT the
                // Solo/Leader/Follower/Stationary Type: Group 0 and 2 never wander; Guard (5) patrols with
                // priority and NO aggression roll; every other group rolls genrdn(0,100) < (100 - aggression)/2
                // — i.e. LOW-aggression monsters wander MORE, and a 100-aggression lurker never ambient-wanders.
                int group = monster.Template.Group;
                if (group == 0 || group == 2) continue;

                // A monster that has prey to fight in its room stays and bites rather than strolling off.
                // (Stock reaches the same result via the in-combat flag once aggro engages; our aggro is
                // command-driven, so we check for an engageable target directly to avoid mobs abandoning
                // a standing player before the player acts.)
                if (MonsterHasAttackTargetInRoom(monster, kvp.Key)) continue;

                if (wanderBudget >= WanderBudgetPerTick) continue;

                bool guard = group == 5;
                if (!guard)
                {
                    int wanderChance = (100 - monster.Template.FollowPercent) / 2;
                    if (_rng.Next(0, 100) >= wanderChance) continue;   // genrdn(0,100) < chance
                }

                // Stock spends a budget slot on every rolled attempt, whether or not the move lands.
                wanderBudget++;

                // Group-formation anchor: a Follower (or subordinate Leader) does not move on its own
                // while a dominant leader of its group shares the room — it waits to be dragged instead.
                if (IsAnchoredToGroupLeader(monster)) continue;

                var room = GetRoom(kvp.Key.Map, kvp.Key.Room);
                if (room == null) continue;

                // The random-direction pick is what an ambient wander sees, and it is
                // NARROWER than "every exit": only types 0 (Normal), 2 (Key), 5 (Action), 7 (Door),
                // 11 (Gate), 19 (BlockGuard) and 24 (SpellTrap) are candidates. A chase is not so
                // limited — it follows the player's own exit — which is why the two paths pick
                // differently but gate identically.
                var chosenExit = PickWanderExit(room);
                if (chosenExit == null) continue;

                // Anti-backtrack: never immediately reverse the last move.
                if (string.Equals(chosenExit.Direction, monster.LastMoveDirection, StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = (Map: chosenExit.TargetMap, Room: chosenExit.TargetRoom);
                var targetRoom = GetRoom(target.Map, target.Room);
                if (targetRoom == null) continue;
                // The gates every relocation shares: destination compatibility, the
                // exit-type switch, and the hard 15-slot physical occupancy (NOT the lair MaxRegen cap
                // — a monster may enter any room with a free slot).
                if (!CanMonsterEnterRoomByMovement(monster.Template, targetRoom, chosenExit)) continue;
                if (!CanMonsterTraverseExitType(monster.Template, chosenExit, GetLiveDoorLockState(chosenExit))) continue;
                if (IsRoomPhysicallyFull(target)) continue;

                movers.Add((monster, kvp.Key, chosenExit, target));
            }
        }

        // `moved` tracks monsters already relocated this pass (a Leader's drag can move a monster that
        // also picked its own exit above); such a monster is not moved twice.
        var moved = new HashSet<MonsterInstance>();
        foreach (var (monster, fromKey, exit, to) in movers)
        {
            if (moved.Contains(monster)) continue;
            // The room/occupancy may have shifted since the decision (an earlier Leader dragged this
            // monster, or filled the destination).
            if (monster.MapNumber != fromKey.Map || monster.RoomNumber != fromKey.Room) continue;

            // The mover fires the exit's trap on the crossing monster BEFORE it reaches for the
            // destination slot, so a room that turns out to be full does not spare it.
            ApplyMonsterExitTrap(monster, exit);
            if (IsRoomPhysicallyFull(to)) continue;

            moved.Add(monster);
            announcements.Add(MoveMonster(monster, fromKey, to, exit.Direction));

            // A Leader pulls its group along in the same direction (the drag).
            DragGroupFollowers(monster, fromKey, to, exit, moved, announcements);
        }

        return announcements;
    }

    // True when the monster has someone to fight in its current room: a player it has already engaged
    // (or that is actively attacking it), or any present player it would aggro on sight per its Align.
    // Used to suppress idle self-wandering — a monster that could be biting a player should not stroll
    // away. Sysop-invisible / no-aggro players are not valid targets (they are excluded from all aggro).
    private bool MonsterHasAttackTargetInRoom(MonsterInstance monster, (int Map, int Room) roomKey)
    {
        foreach (var player in GetPlayersInRoom(roomKey.Map, roomKey.Room))
        {
            if (player.IsSysopInvisible || player.IsSysopNoAggro || player.IsOutOfRealm)
                continue;

            if (monster.HasEngagedPlayer(player.Name))
                return true;

            if (player.InCombat && player.CombatTarget == monster)
                return true;

            if (CombatEngine.ShouldMonsterAggro(monster.Template, player))
                return true;
        }

        return false;
    }

    // Test seam: would this monster stay to fight (and thus skip self-wandering) given who is in its
    // current room right now? Mirrors the guard ProcessMonsterMovement applies to each wander candidate.
    internal bool MonsterWouldStayToFightForTests(MonsterInstance monster)
        => MonsterHasAttackTargetInRoom(monster, (monster.MapNumber, monster.RoomNumber));

    // Test seam: run one medium-tick ambient-wander pass (applies moves + drags), so tests can drive
    // wander/anchor/drag without the full tick.
    internal void RunMonsterWanderForTests() => ProcessMonsterMovement();

    // Group-formation ANCHOR (self-initiated moves only): a Follower,
    // or a subordinate Leader, does not move on its own while a Leader of its own group (same GroupMatch,
    // GroupMatch) shares its room. A Follower yields to ANY same-group leader; a Leader yields only
    // to a STRONGER one (higher ExpMulti). Dragged moves bypass this (they route straight
    // through MoveMonster), matching the stock "being dragged" flag.
    private bool IsAnchoredToGroupLeader(MonsterInstance mover)
    {
        int type = mover.Template.Type;
        if (type != 1 && type != 2)                 // only Leaders (1) and Followers (2) anchor
            return false;

        foreach (var other in GetMonstersInRoom(mover.MapNumber, mover.RoomNumber))
        {
            if (ReferenceEquals(other, mover) || other.IsDead)
                continue;
            if (other.Template.Type != 1)           // the anchor must itself be a Leader
                continue;
            if (other.Template.GroupMatch != mover.Template.GroupMatch)
                continue;
            // A Follower anchors to any same-group leader; a Leader only to a stronger leader.
            if (type == 1 && !(mover.Template.ExpMulti < other.Template.ExpMulti))
                continue;
            return true;
        }
        return false;
    }

    internal bool IsAnchoredToGroupLeaderForTests(MonsterInstance mover) => IsAnchoredToGroupLeader(mover);

    // Group-formation DRAG (after a self-initiated Leader move): the
    // Leader (Type 1) pulls same-group members out of its ORIGIN room in the SAME direction — every
    // Follower (2), and every weaker Leader (1) with lower ExpMulti — up to its MaxFollowers cap
    // (MaxFollowers). Dragged members bypass the anchor (they are pulled) but still obey the destination
    // group-compatibility and 15-slot capacity gates. Returns the relocation announcements.
    private List<MonsterInstance> DragGroupFollowers(
        MonsterInstance leader, (int Map, int Room) from, (int Map, int Room) to, RoomExitDefinition exit,
        HashSet<MonsterInstance> alreadyMoved, List<MonsterMovementAnnouncement> announcementSink)
    {
        var draggedMonsters = new List<MonsterInstance>();
        if (leader.Template.Type != 1 || leader.Template.MaxFollowers <= 0)
            return draggedMonsters;

        var destRoom = GetRoom(to.Map, to.Room);
        if (destRoom == null)
            return draggedMonsters;

        // Snapshot the origin room — MoveMonster mutates it as each follower is pulled.
        foreach (var m in GetMonstersInRoom(from.Map, from.Room).ToList())
        {
            if (draggedMonsters.Count >= leader.Template.MaxFollowers)
                break;
            if (ReferenceEquals(m, leader) || m.IsDead || alreadyMoved.Contains(m))
                continue;
            if (m.Template.GroupMatch != leader.Template.GroupMatch)
                continue;
            bool follower = m.Template.Type == 2;
            bool weakerLeader = m.Template.Type == 1 && m.Template.ExpMulti < leader.Template.ExpMulti;
            if (!follower && !weakerLeader)
                continue;
            // The drag re-enters the mover (with its leader flag set), so every one of its gates
            // still applies to a dragged member: never a Stationary, the destination compatibility and
            // exit-type switch, the exit's trap, and a free physical slot at the far end.
            if (m.Template.Type == 3 || !CanMonsterEnterRoomByMovement(m.Template, destRoom, exit))
                continue;
            if (!CanMonsterTraverseExitType(m.Template, exit, GetLiveDoorLockState(exit)))
                continue;

            ApplyMonsterExitTrap(m, exit);
            if (IsRoomPhysicallyFull(to))
                break;

            alreadyMoved.Add(m);
            draggedMonsters.Add(m);
            announcementSink.Add(MoveMonster(m, from, to, exit.Direction));
        }
        return draggedMonsters;
    }

    // Mirrors the roomCap math in TryGenerateLairMonster @367-368 so wandering / pursuit paths can
    // share a single source of truth for the per-room population limit. Permanent NPCs are not part
    // of the lair cap (they have their own slot).
    internal static int GetRoomLairCap(Room room) =>
        Math.Max(0, Math.Min(room.MaxRegen, MaxMonstersPerRoom));

    internal static bool IsRoomAtLairCap(Room room, int aliveLairMonsters)
    {
        int cap = GetRoomLairCap(room);
        return cap > 0 && aliveLairMonsters >= cap;
    }

    // The stock room record has exactly 15 monster occupancy slots; adding fails
    // when none is free, so a room can PHYSICALLY never hold more than 15 monsters regardless of how many
    // spawn, wander in, are chased in, or are dragged in. This is the hard anti-pileup ceiling — distinct
    // from the per-lair MaxRegen spawn cap, and (unlike our old min(MaxRegen,15)) it applies even to
    // MaxRegen-0 rooms (corridors/towns), which is what stops a kited mob-train from crashing the board.
    internal const int MaxRoomOccupancy = MaxMonstersPerRoom;

    private int CountRoomOccupancy((int Map, int Room) roomKey)
        => _roomMonsters.TryGetValue(roomKey, out var list) ? list.Count(m => !m.IsDead) : 0;

    private bool IsRoomPhysicallyFull((int Map, int Room) roomKey)
        => CountRoomOccupancy(roomKey) >= MaxRoomOccupancy;

    // Living offspring of a lair, counted from the live monster set wherever they currently are. Stock
    // keeps this as the room "active count", decremented at death on the monster's LAIR room
    // — never on the room of death and never on movement — so a monster lured/wandered
    // out of its lair STILL counts against it. We recompute it from live objects each spawn check rather
    // than maintaining a +1/-1 counter: a recomputed count cannot drift, so we reproduce the stock lair
    // cap WITHOUT ever reproducing the "killing the regen" stuck-counter bug. This is what prevents the
    // runaway (kite monsters out → lair refills → repeat): the lair only frees a slot when an offspring
    // actually dies.
    private int CountLivingLairOffspring(int lairMap, int lairRoom)
    {
        int count = 0;
        foreach (var list in _roomMonsters.Values)
        {
            // Snapshot to a local to avoid enumerating a list another thread may mutate mid-scan.
            var snapshot = list;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var m = snapshot[i];
                if (!m.IsDead && m.IsBackgroundSpawn && m.HomeMapNumber == lairMap && m.HomeRoomNumber == lairRoom)
                    count++;
            }
        }
        return count;
    }

    // Area-wide living population for the area cap (kept on the control room): every living lair-spawned
    // monster whose LAIR belongs to this control-room cluster, wherever it currently roams. Like the lair
    // count it is recomputed from the live set (no drift), and only death frees a slot. The control room
    // and its cluster are same-map.
    private int CountLivingAreaPopulation(int map, int controlRoomNumber)
    {
        int count = 0;
        foreach (var list in _roomMonsters.Values)
        {
            var snapshot = list;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var m = snapshot[i];
                if (m.IsDead || !m.IsBackgroundSpawn || m.HomeMapNumber != map)
                    continue;
                var lair = GetRoom(m.HomeMapNumber, m.HomeRoomNumber);
                if (lair != null && lair.ControlRoom == controlRoomNumber)
                    count++;
            }
        }
        return count;
    }

    private const int MonsterRootedAbilityId = 74;
    private const int MonsterMovementSlowAbilityId = 68;

    private bool IsMonsterMovementSkipped(MonsterInstance monster)
    {
        if (monster.IsKnockedDown)
            return true;

        if (monster.GetEffectiveAbility(MonsterRootedAbilityId) != 0)
            return true;

        return monster.GetEffectiveAbility(MonsterMovementSlowAbilityId) != 0 && _rng.Next(100) <= 49;
    }

    public List<MonsterInstance> ResolveMonsterPursuit(Player player, Room fromRoom, RoomExitDefinition exit, IReadOnlyList<MonsterInstance> attackers)
    {
        var followed = new List<MonsterInstance>();
        if (attackers.Count == 0)
            return followed;

        var destinationRoom = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (destinationRoom == null)
            return followed;

        var from = (fromRoom.MapNumber, fromRoom.RoomNumber);
        var to = (destinationRoom.MapNumber, destinationRoom.RoomNumber);

        // A Leader's drag can move a monster that is itself in the attacker list; `moved` stops it being
        // pursued a second time.
        var moved = new HashSet<MonsterInstance>();
        foreach (var monster in attackers)
        {
            if (moved.Contains(monster))
                continue;

            if (!CanMonsterPursueThroughExit(monster, player, from, destinationRoom, exit))
                continue;

            if (IsMonsterMovementSkipped(monster))
                continue;

            // Group-formation anchor: a Follower (or subordinate Leader) won't chase independently while
            // its group leader is present — it is dragged along when the leader moves, not before.
            if (IsAnchoredToGroupLeader(monster))
                continue;

            if (_rng.Next(100) >= monster.Template.FollowPercent)
                continue;

            // The chase roll has passed, so the mover is entered — and its exit-type switch fires the
            // exit's trap on the chaser BEFORE the destination slot is claimed. A monster that declined
            // to chase never reaches this, and a full destination does not spare one that did.
            ApplyMonsterExitTrap(monster, exit);

            // Gated only by the hard 15-slot physical occupancy: a chased monster may enter any
            // room with a free slot. This bounds a kited mob-train (the crash vector) without letting
            // the destination's MaxRegen block legitimate pursuit.
            if (IsRoomPhysicallyFull(to))
                continue;

            moved.Add(monster);
            MoveMonsterPursuit(monster, player, from, to, exit.Direction);
            followed.Add(monster);

            // A Leader drags its group along the chase. Dragged members relocate
            // via the generic MoveMonster path, so announce their movement lines here.
            var dragAnnouncements = new List<MonsterMovementAnnouncement>();
            var draggedMonsters = DragGroupFollowers(monster, from, to, exit, moved, dragAnnouncements);
            if (dragAnnouncements.Count > 0)
                AnnounceMonsterMovements(dragAnnouncements);
            followed.AddRange(draggedMonsters);
        }

        return followed;
    }

    private BackgroundGenerationStats ProcessBackgroundMonsterGeneration()
    {
        if (_onlinePlayers.IsEmpty)
            return default;

        var now = DateTime.UtcNow;

        int scheduledTrueLairRoomsProcessed = 0;
        int scheduledTrueLairRoomsScanned = 0;
        int spawnAttempts = 0;
        int monstersSpawned = 0;
        var activeTrueLairRooms = GetActiveTrueLairSpawnRoomSnapshot();
        if (activeTrueLairRooms.Count > 0)
        {
            int start = Math.Min(_activeTrueLairSpawnCursor, activeTrueLairRooms.Count - 1);
            int scanLimit = Math.Min(MaxScheduledTrueLairRoomScansPerTick, activeTrueLairRooms.Count);
            int scanned = 0;

            for (; scanned < scanLimit && scheduledTrueLairRoomsProcessed < MaxScheduledTrueLairRoomsPerTick; scanned++)
            {
                var roomKey = activeTrueLairRooms[(start + scanned) % activeTrueLairRooms.Count];
                scheduledTrueLairRoomsScanned++;

                if (!_roomNextLairSpawnAtUtc.TryGetValue(roomKey, out var deadline) || deadline > now)
                    continue;

                spawnAttempts++;
                monstersSpawned += TryGenerateLairMonster(roomKey.Map, roomKey.Room, pressurePlayerCount: 0, isAdjacentRoom: false, now, maxTrueLairSpawns: 1);
                scheduledTrueLairRoomsProcessed++;
            }

            _activeTrueLairSpawnCursor = (start + scanned) % activeTrueLairRooms.Count;
        }

        var playerRooms = _onlinePlayers.Values
            .GroupBy(player => (Map: player.CurrentMapNumber, Room: player.CurrentRoomNumber))
            .Select(group => (group.Key.Map, group.Key.Room, PlayerCount: group.Count()))
            .ToList();

        int adjacentRoomsChecked = 0;

        foreach (var playerRoom in playerRooms)
        {
            spawnAttempts++;
            monstersSpawned += TryGenerateLairMonster(playerRoom.Map, playerRoom.Room, playerRoom.PlayerCount, isAdjacentRoom: false, now, maxTrueLairSpawns: 1);

            var room = GetRoom(playerRoom.Map, playerRoom.Room);
            if (room == null)
                continue;

            foreach (var exit in room.GetExits().Values)
            {
                if (_rng.Next(100) >= AdjacentSpawnChancePercent)
                    continue;

                adjacentRoomsChecked++;
                spawnAttempts++;
                monstersSpawned += TryGenerateLairMonster(exit.Map, exit.Room, playerRoom.PlayerCount, isAdjacentRoom: true, now, maxTrueLairSpawns: 1);
            }
        }

        return new BackgroundGenerationStats(
            activeTrueLairRooms.Count,
            scheduledTrueLairRoomsScanned,
            scheduledTrueLairRoomsProcessed,
            playerRooms.Count,
            adjacentRoomsChecked,
            spawnAttempts,
            monstersSpawned);
    }

    private int TryGenerateLairMonster(int mapNumber, int roomNumber, int pressurePlayerCount, bool isAdjacentRoom, DateTime now, int? maxTrueLairSpawns = null, bool announce = true)
    {
        var room = GetRoom(mapNumber, roomNumber);
        if (room == null || !CanRoomGenerateLairMonsters(room))
            return 0;

        var key = (Map: mapNumber, Room: roomNumber);
        if (!IsRoomLairSpawnReady(room, key, now))
            return 0;

        var spawnedAnnouncements = new List<SpawnedMonsterAnnouncement>();
        int totalSpawned = 0;

        // Lazy room init — and the primary NPC it may place — runs in its own critical section, so its
        // arrival line is broadcast even when one of the cap checks below bails out early. A spawn the
        // stock would have announced must not be swallowed by a later early return.
        MonsterInstance? lazyPrimary = null;
        lock (_monsterLock)
        {
            if (!_roomMonsters.TryGetValue(key, out var initialMonsters))
            {
                initialMonsters = [];
                _roomMonsters[key] = initialMonsters;
                lazyPrimary = EnsurePermanentNpcInRoom(room, key, initialMonsters);
            }
        }

        if (lazyPrimary != null && announce)
            AnnounceSpawnedMonsters([new SpawnedMonsterAnnouncement(key.Map, key.Room, lazyPrimary)]);

        lock (_monsterLock)
        {
            var roomMonsters = _roomMonsters.GetValueOrDefault(key) ?? [];

            // Hard physical ceiling first (15-slot occupancy): never exceed the room's 15 slots,
            // regardless of MaxRegen or how full this lair's roster is.
            if (IsRoomPhysicallyFull(key))
                return 0;

            // Lair spawn cap: count this lair's LIVING OFFSPRING wherever they roam (not just who is
            // standing here right now), so luring them away never frees a spawn slot — only death does.
            int aliveLairMonsters = CountLivingLairOffspring(mapNumber, roomNumber);
            int roomCap = Math.Max(0, room.MaxRegen);
            if (aliveLairMonsters >= roomCap)
                return 0;

            // Area-wide cap (refuse once the control room's living count reaches its Area Max). A
            // cluster of rooms anchored on a control room shares one population budget ("Max Regen" per
            // area); recomputed from the live set so it bounds the whole cluster without drift.
            if (room.ControlRoom != 0)
            {
                var controlRoom = GetRoom(mapNumber, room.ControlRoom);
                if (controlRoom is { MaxArea: > 0 }
                    && CountLivingAreaPopulation(mapNumber, room.ControlRoom) >= controlRoom.MaxArea)
                    return 0;
            }

            // Stock alignment (see the lair-regen deep-dive note):
            // the room's regen timer is written only when a monster
            // is REMOVED, never on spawn attempts. Spawn attempts read the timer (via
            // IsRoomLairSpawnReady) but don't reset it. Owners of `_roomNextLairSpawnAtUtc` writes
            // are ScheduleLairRespawnAfterVacancy (death/removal only, never movement) and the seeder.
            // Previously this branch CLEARED the deadline on success (letting the next world tick
            // re-spawn immediately) and SET it on a soft failure — both diverge from stock and
            // turned an empty forest into a fully-stocked one within a few ticks of player
            // presence.
            if (room.RoomType == TrueLairSpawnRoomType)
            {
                int spawnLimit = maxTrueLairSpawns.GetValueOrDefault(BackgroundSpawnCycleCap);
                int attempts = Math.Min(spawnLimit, roomCap - aliveLairMonsters);
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    if (IsRoomPhysicallyFull(key))
                        break;
                    if (!TrySpawnOneLairMonster(room, key, roomMonsters, out var spawnedMonster))
                        break;

                    totalSpawned++;
                    aliveLairMonsters++;
                    spawnedAnnouncements.Add(new SpawnedMonsterAnnouncement(key.Map, key.Room, spawnedMonster));
                    if (aliveLairMonsters >= roomCap)
                        break;
                }
            }
            else if (ShouldSpawnPressureRoom(room, aliveLairMonsters, pressurePlayerCount, isAdjacentRoom))
            {
                if (TrySpawnOneLairMonster(room, key, roomMonsters, out var spawnedMonster))
                {
                    totalSpawned++;
                    spawnedAnnouncements.Add(new SpawnedMonsterAnnouncement(key.Map, key.Room, spawnedMonster));
                }
            }
        }

        if (announce)
            AnnounceSpawnedMonsters(spawnedAnnouncements);
        return totalSpawned;
    }

    // Walking into a Lair (RoomType 3) that has
    // NO player already in it fills its full monster set on the spot (spawning until the
    // cap/timer/physical-slot limit is hit), BEFORE the room is shown. The fill happens in the entry
    // gate before the mover joins the room, so no one is a broadcast recipient — spawn SILENTLY; the
    // monsters simply appear in the room description the player is about to see. Other room types
    // (Arena type 2, Normal type 0) do NOT enter-fill — they rely on the background pressure driver.
    private void FillTrueLairOnEnter(Room room, Player enteringPlayer)
    {
        if (room.RoomType != TrueLairSpawnRoomType || !CanRoomGenerateLairMonsters(room))
            return;

        // First-player gate: only fill when the entering player is the first one here. If someone was
        // already in the room, they would have triggered the fill on their own entry.
        if (GetPlayersInRoom(room.MapNumber, room.RoomNumber).Count > 1)
            return;

        TryGenerateLairMonster(
            room.MapNumber,
            room.RoomNumber,
            pressurePlayerCount: 0,
            isAdjacentRoom: false,
            DateTime.UtcNow,
            maxTrueLairSpawns: Math.Max(0, room.MaxRegen),
            announce: false);
    }

    private bool TrySpawnOneLairMonster(Room room, (int Map, int Room) roomKey, List<MonsterInstance> roomMonsters, out MonsterInstance instance)
    {
        instance = null!;
        var candidates = GetLairMonsterCandidates(room);

        if (candidates.Count == 0)
            return false;

        const int maxAttempts = 8;
        for (int i = 0; i < maxAttempts; i++)
        {
            var template = candidates[_rng.Next(candidates.Count)];
            if (!CanSpawnMonster(template))
                continue;

            // The spawn applies the unique RegenTime gate to every spawn path, lairs
            // included; skip a timer mob still cooling down (no-op for ordinary lair fauna).
            if (!IsRegenTimerElapsed(template))
                continue;

            instance = MonsterInstance.Create(template, roomKey.Map, roomKey.Room, isBackgroundSpawn: true, guaranteeAllDrops: ShouldGuaranteeFirstDrop(template));
            roomMonsters.Add(instance);
            AdjustGlobalCount(template.Number, 1);
            return true;
        }

        return false;
    }

    private void AnnounceSpawnedMonsters(IReadOnlyList<SpawnedMonsterAnnouncement> spawnedMonsters)
    {
        foreach (var spawned in spawnedMonsters)
        {
            if (GetPlayersInRoom(spawned.Map, spawned.Room).Count == 0)
                continue;

            var template = spawned.Instance.Template;
            // The spawn line self-colours from BrightYellow, inserting Green only
            // after a substituted name — so do NOT wrap it in a Green base here.
            string? spawnMsg = FormatMonsterSpawnMessage(template, spawned.Instance.DisplayName);
            if (spawnMsg == null)
                continue; // blank silence-sentinel MoveMsg → spawn with no arrival line (faithful)
            BroadcastToRoom(spawned.Map, spawned.Room,
                $"{spawnMsg}{MudAnsi.Reset}",
                reprompt: true,
                prependLineBreak: true);
        }
    }

    internal IReadOnlyList<Monster> GetLairMonsterCandidates(Room room)
    {
        // When the room's "by Number" is set, the spawn places THAT specific
        // monster and short-circuits the group/index random pick (the group scan only runs when
        // no explicit number is passed). We honour the same override, EXCEPT when by_number == the room's perm NPC
        // — there the on-enter NPC spawn already places it, and stock likewise gates that
        // case so the lair doesn't double it. Otherwise we'd spawn 21 stock
        // Silvermere rooms (newbie tunnels) wrong/random/empty; with it they spawn the designed mob
        // (acid slime / giant rat / orc rogue / hanging cocoon).
        // Effective override: by_number forces a spawn UNLESS it equals the room's perm NPC (the
        // NPC-on-enter path owns that, mirroring the stock gate). Fold the NPC
        // decision into the value so the cache key fully determines the result (two rooms sharing
        // group/index but differing only in NPC must not collide).
        int effectiveByNumber = (room.ByNumber > 0 && room.ByNumber != room.NPC) ? room.ByNumber : 0;
        var key = new LairCandidateKey(room.MonsterType, room.MinIndex, room.MaxIndex, effectiveByNumber);

        lock (_lairCandidateCacheLock)
        {
            if (_lairCandidateCache.TryGetValue(key, out var cached))
                return cached;

            Monster[] candidates;
            if (effectiveByNumber > 0
                && Database.Monsters.TryGetValue(effectiveByNumber, out var specific))
            {
                candidates = [specific];
            }
            else
            {
                candidates = Database.Monsters.Values
                    .Where(monster => !IsRoomBoundMonster(monster)
                                   && monster.Group == key.MonsterType
                                   && monster.GroupIndex >= key.MinIndex
                                   && monster.GroupIndex <= key.MaxIndex)
                    .ToArray();
            }

            _lairCandidateCache[key] = candidates;
            return candidates;
        }
    }

    private bool IsRoomLairSpawnReady(Room room, (int Map, int Room) roomKey, DateTime now)
    {
        if (room.RoomType == InstantPressureSpawnRoomType)
            return true;

        return !_roomNextLairSpawnAtUtc.TryGetValue(roomKey, out var nextAllowedAt) || now >= nextAllowedAt;
    }

    private bool ShouldSpawnPressureRoom(Room room, int aliveMonsterCount, int pressurePlayerCount, bool isAdjacentRoom)
    {
        if (room.RoomType is not AmbientPressureSpawnRoomType and not InstantPressureSpawnRoomType)
            return false;

        if (pressurePlayerCount <= 0)
            return false;

        // Stock alignment (deep-dive note "Adjacent room phase repeats the same split after a
        // low-probability adjacent gate"): adjacent rooms run through the same RoomType-based
        // pressure logic as the player's own room. The previous "isAdjacentRoom → 100% if empty"
        // shortcut was the dominant cause of the forest-fills-up-instantly bug: with 10 exits and
        // a 6% outer adjacent gate, an empty adjacent room would auto-spawn at 6% per exit per
        // tick (~60% chance per tick of SOME adjacent gaining a monster). Stock applies the
        // ordinary AmbientPressure (5%) / InstantPressure (90%) chance on both paths.
        int chance = GetCurrentRoomPressureSpawnChancePercent(room, aliveMonsterCount, pressurePlayerCount);
        return chance > 0 && _rng.Next(100) < chance;
    }

    private void CleanupMonstersInInvalidRooms()
    {
        foreach (var kvp in _roomMonsters)
        {
            var room = GetRoom(kvp.Key.Map, kvp.Key.Room);
            if (room == null)
                continue;

            var invalid = kvp.Value
                .Where(m => !m.IsDead
                    && !m.IsPermanentNPC
                    && !m.WasForcePlaced            // death-summons / reinforcements / quest adds / pets stay put
                    && m.EngagedPlayerCount == 0    // never pull a monster out from under an active fight
                    && !m.HasPlayerOwner
                    && !IsMonsterAllowedInRoom(m.Template, room))
                .ToList();

            foreach (var monster in invalid)
            {
                kvp.Value.Remove(monster);
                AdjustGlobalCount(monster.Template.Number, -1);
            }
        }
    }

    internal TimeSpan GetRoomSpawnDelay(Room room)
    {
        if (room.RoomType == NoLairSpawnDelayRoomType)
            return TimeSpan.Zero;

        // Per-realm default (loaded from this world's own serversettings; sysop MINWAIT persists it).
        return room.Delay > 0 ? TimeSpan.FromMinutes(room.Delay) : TimeSpan.FromMinutes(LairRegenDefaultMinutes);
    }

    internal static bool CanRoomGenerateLairMonsters(Room room)
    {
        return room.MaxRegen > 0
            && IsBackgroundSpawnRoomType(room.RoomType)
            && room.MonsterType > 0
            // ByNumber spawns a specific monster independent of the index range, so a room with
            // by_number set but an empty index window (e.g. the Narrow Stone Tunnels 1/2320-2323, idx
            // [0..0]) still generates — without this they stayed permanently empty instead of spawning
            // their designed acid slimes.
            && (room.MinIndex > 0 || room.MaxIndex > 0 || room.ByNumber > 0);
    }

    internal static bool IsBackgroundSpawnRoomType(int roomType)
    {
        return roomType is AmbientPressureSpawnRoomType or InstantPressureSpawnRoomType or TrueLairSpawnRoomType;
    }

    internal static int GetCurrentRoomPressureSpawnChancePercent(Room room, int aliveMonsterCount, int playerCount)
    {
        if (room.RoomType is not AmbientPressureSpawnRoomType and not InstantPressureSpawnRoomType)
            return 0;

        if (playerCount <= 0)
            return 0;

        if (aliveMonsterCount < playerCount)
            return room.RoomType == InstantPressureSpawnRoomType
                ? InstantPressureSpawnChancePercent
                : AmbientPressureSpawnChancePercent;

        return playerCount * 2 > aliveMonsterCount
            ? OvercrowdedPressureSpawnChancePercent
            : 0;
    }

    internal static bool IsUnboundMonsterCompatibleWithRoom(Monster template, Room room)
    {
        return room.MonsterType > 0 && template.Group == room.MonsterType;
    }

    private void SeedBackgroundLairSpawnDeadlines()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var room in Database.Rooms.Values)
        {
            if (!CanRoomGenerateLairMonsters(room))
                continue;

            var key = (room.MapNumber, room.RoomNumber);
            _roomNextLairSpawnAtUtc.TryAdd(key, now + GetInitialLairSpawnOffset(room));
        }
    }

    internal void RefreshRoomLairSpawnDeadline(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        var room = GetRoom(mapNumber, roomNumber);
        if (room == null || !CanRoomGenerateLairMonsters(room))
        {
            _roomNextLairSpawnAtUtc.TryRemove(key, out _);
            return;
        }

        _roomNextLairSpawnAtUtc[key] = DateTime.UtcNow + GetRoomSpawnDelay(room);
    }

    internal void ForceRoomLairSpawnReady(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        var room = GetRoom(mapNumber, roomNumber);
        if (room == null || !CanRoomGenerateLairMonsters(room))
        {
            _roomNextLairSpawnAtUtc.TryRemove(key, out _);
            return;
        }

        _roomNextLairSpawnAtUtc[key] = DateTime.MinValue;
    }

    // Test hook: clear a unique monster's recorded death so its RegenTime gate is immediately
    // satisfied (the next room entry/init may respawn it), without waiting out real hours.
    internal void ForceMonsterRegenReadyForTests(int monsterNumber)
        => _monsterRegenLastDeathUtc.TryRemove(monsterNumber, out _);

    // Test hook: drop a room's cached monster list WITHOUT respawning anything (unlike the sysop
    // ResetMonsterGenerationRoom, which re-seeds the room). This reproduces the state the medium tick
    // leaves behind when it sweeps an empty room entry — the precondition for the lazy
    // GetMonstersInRoom -> SpawnRoomMonsters init path that must still announce its primary NPC.
    internal void RemoveRoomMonsterCacheForTests(int mapNumber, int roomNumber)
    {
        lock (_monsterLock)
        {
            if (_roomMonsters.TryRemove((mapNumber, roomNumber), out var list))
            {
                foreach (var monster in list.Where(m => !m.IsDead))
                    AdjustGlobalCount(monster.Template.Number, -1);
            }
        }
    }

    // Test hook: true when this monster has a recorded death still inside its RegenTime window.
    internal bool IsMonsterRegenPendingForTests(int monsterNumber)
        => Database.Monsters.TryGetValue(monsterNumber, out var template)
           && !IsRegenTimerElapsed(template);

    // Test hook: run one background spawn pass (the live cadence calls this from the world tick).
    internal void RunBackgroundMonsterGenerationForTests() => ProcessBackgroundMonsterGeneration();

    // Test hook: run the invalid-room sweep (the live cadence runs it every 10 medium ticks).
    internal void RunInvalidRoomCleanupForTests() => CleanupMonstersInInvalidRooms();

    // Test hook: count a lair's living offspring wherever they roam (the home-bound spawn-cap count).
    internal int CountLivingLairOffspringForTests(int lairMap, int lairRoom)
        => CountLivingLairOffspring(lairMap, lairRoom);

    // Test hook: count a control-room cluster's living population (the area-cap count).
    internal int CountLivingAreaPopulationForTests(int map, int controlRoom)
        => CountLivingAreaPopulation(map, controlRoom);

    // Test hook: relocate a monster to another room (simulates a kited/lured mob leaving its lair),
    // exercising the real MoveMonster path so the lair-binding / death-only-respawn invariants hold.
    internal void RelocateMonsterForTests(MonsterInstance monster, int toMap, int toRoom)
    {
        lock (_monsterLock)
            MoveMonster(monster, (monster.MapNumber, monster.RoomNumber), (toMap, toRoom), "north");
    }

    private void ScheduleLairRespawnAfterVacancy(MonsterInstance monster, DateTime? now = null)
    {
        if (!monster.IsBackgroundSpawn)
            return;

        var room = GetRoom(monster.HomeMapNumber, monster.HomeRoomNumber);
        if (room == null || !CanRoomGenerateLairMonsters(room))
            return;

        ScheduleRoomLairSpawnDeadline(room, now ?? DateTime.UtcNow);
    }

    private void ScheduleRoomLairSpawnDeadline(Room room, DateTime now)
    {
        // Death/vacancy unconditionally sets the cooldown to `now + Delay`. The previous MIN
        // semantics (`existing <= deadline ? existing : deadline`) preserved a past initial-seed
        // value over the fresh death timestamp, so death effectively didn't gate respawn — the
        // room read as "ready" the next tick. Stock writes the timer to the current minute
        // on monster removal regardless of prior value, which matches direct overwrite here.
        var key = (room.MapNumber, room.RoomNumber);
        _roomNextLairSpawnAtUtc[key] = now + GetRoomSpawnDelay(room);
    }

    private TimeSpan GetInitialLairSpawnOffset(Room room)
    {
        TimeSpan spawnDelay = GetRoomSpawnDelay(room);
        if (spawnDelay <= TimeSpan.Zero)
            return TimeSpan.Zero;

        ulong hash = ComputeDeterministicRoomSeed(room.MapNumber, room.RoomNumber);
        long offsetTicks = (long)(hash % (ulong)spawnDelay.Ticks);
        return TimeSpan.FromTicks(offsetTicks);
    }

    private static ulong ComputeDeterministicRoomSeed(int mapNumber, int roomNumber)
    {
        ulong seed = ((ulong)(uint)mapNumber << 32) | (uint)roomNumber;
        seed ^= seed >> 33;
        seed *= 0xff51afd7ed558ccdUL;
        seed ^= seed >> 33;
        seed *= 0xc4ceb9fe1a85ec53UL;
        seed ^= seed >> 33;
        return seed;
    }

    // Per-player spawn-bubble refresh: the cheap membership check and, when the player has crossed a
    // bubble edge, the BFS that recomputes the bubble. The BFS reads ONLY immutable room/exit catalog
    // data, so it never needs the WorldStateGate — but it is called from the move/enter path which DOES
    // hold the gate, so a wide BFS freezes the whole world for its duration (observed: a 106ms move
    // stall under the old radius-30 BFS). In production (BackgroundSpawnBubbleEnabled) the gated caller
    // does only the membership check + a dictionary insert, and a background worker runs the BFS off the
    // gate, taking only _activeSpawnRoomsLock to swap the result in. With the worker disabled (tests) the
    // BFS runs inline, preserving the deterministic synchronous behavior the harness relies on. `force`
    // always rebuilds synchronously — rare admin ops (world reset, live radius change) that need the
    // bubble consistent the instant they return.
    private void RefreshPlayerSpawnBubbleIfNeeded(Player player, bool force = false)
    {
        if (!force)
        {
            var currentRoomKey = (player.CurrentMapNumber, player.CurrentRoomNumber);
            lock (_activeSpawnRoomsLock)
            {
                if (_activeSpawnRoomsByPlayer.TryGetValue(player.Name, out var existingRooms)
                    && existingRooms.Contains(currentRoomKey))
                    return;
            }

            if (BackgroundSpawnBubbleEnabled)
            {
                // Coalesces: repeated edge crossings before the worker runs collapse to one refresh of
                // the player's latest position (the worker reads the live room when it actually runs).
                _pendingSpawnBubbleRefresh[player.Name] = 0;
                return;
            }
        }

        RebuildPlayerSpawnBubble(player);
    }

    // The actual BFS + result swap. Safe to call without the WorldStateGate: BuildSpawnBubble reads only
    // the immutable room/exit catalog, and the swap is guarded by _activeSpawnRoomsLock.
    private void RebuildPlayerSpawnBubble(Player player)
    {
        var currentRoomKey = (player.CurrentMapNumber, player.CurrentRoomNumber);
        var playerRooms = BuildSpawnBubble(currentRoomKey, ActiveLairSpawnRoomRadius);

        lock (_activeSpawnRoomsLock)
        {
            _activeSpawnRoomsByPlayer[player.Name] = playerRooms;
            RebuildActiveSpawnRoomsLocked();
        }
    }

    // ── Off-gate spawn-bubble refresh worker ──────────────────────────────────────────────────────
    // OFF by default so unit/integration tests keep synchronous, deterministic bubble updates (the BFS
    // runs inline in NotifyPlayerEnteredRoom). The production host turns it on right after constructing
    // the world, moving the BFS off the move-command's gate hold.
    public bool BackgroundSpawnBubbleEnabled { get; set; }

    private readonly ConcurrentDictionary<string, byte> _pendingSpawnBubbleRefresh = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _spawnBubbleCts;
    private Task? _spawnBubbleLoopTask;
    private static readonly TimeSpan SpawnBubbleRefreshInterval = TimeSpan.FromMilliseconds(500);

    private void StartSpawnBubbleRefresh()
    {
        if (!BackgroundSpawnBubbleEnabled)
            return;

        _spawnBubbleCts = new CancellationTokenSource();
        _spawnBubbleLoopTask = Task.Run(() => RunSpawnBubbleRefreshLoopAsync(_spawnBubbleCts.Token));
    }

    private void StopSpawnBubbleRefresh()
    {
        _spawnBubbleCts?.Cancel();
        try
        {
            _spawnBubbleLoopTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Cancellation surfaces as a faulted/cancelled task on Wait — expected on shutdown.
        }

        _spawnBubbleCts?.Dispose();
        _spawnBubbleCts = null;
        _spawnBubbleLoopTask = null;
    }

    private async Task RunSpawnBubbleRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SpawnBubbleRefreshInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                    break;
                DrainPendingSpawnBubbleRefreshes();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                GameDiagnostics.RecordBackgroundException(ex, "spawn-bubble");
                if (GameDiagnostics.RethrowBackgroundExceptions) throw;
            }
        }
    }

    // Drains queued refreshes, running each BFS off the WorldStateGate. A player who logged off since
    // being queued is skipped (RemovePlayerSpawnBubble already dropped their bubble and pending mark).
    private void DrainPendingSpawnBubbleRefreshes()
    {
        if (_pendingSpawnBubbleRefresh.IsEmpty)
            return;

        foreach (var name in _pendingSpawnBubbleRefresh.Keys.ToList())
        {
            if (!_pendingSpawnBubbleRefresh.TryRemove(name, out _))
                continue;
            if (!_onlinePlayers.TryGetValue(name, out var player))
                continue;

            RebuildPlayerSpawnBubble(player);
        }
    }

    private HashSet<(int Map, int Room)> BuildSpawnBubble((int Map, int Room) center, int maxDistance)
    {
        var visited = new HashSet<(int Map, int Room)>();
        if (GetRoom(center.Map, center.Room) == null)
            return visited;

        var queue = new Queue<((int Map, int Room) RoomKey, int Distance)>();
        visited.Add(center);
        queue.Enqueue((center, 0));

        while (queue.Count > 0)
        {
            var (roomKey, distance) = queue.Dequeue();
            if (distance >= maxDistance)
                continue;

            var room = GetRoom(roomKey.Map, roomKey.Room);
            if (room == null)
                continue;

            foreach (var exit in room.GetExits().Values)
            {
                var next = (exit.Map, exit.Room);
                if (visited.Contains(next) || GetRoom(next.Map, next.Room) == null)
                    continue;

                visited.Add(next);
                queue.Enqueue((next, distance + 1));
            }
        }

        return visited;
    }

    private List<(int Map, int Room)> GetActiveTrueLairSpawnRoomSnapshot()
    {
        lock (_activeSpawnRoomsLock)
            return _activeTrueLairSpawnRooms.ToList();
    }

    private void RemovePlayerSpawnBubble(string playerName)
    {
        _pendingSpawnBubbleRefresh.TryRemove(playerName, out _);
        lock (_activeSpawnRoomsLock)
        {
            if (_activeSpawnRoomsByPlayer.Remove(playerName))
                RebuildActiveSpawnRoomsLocked();
        }
    }

    private void RebuildActiveSpawnRoomsLocked()
    {
        _activeSpawnRooms.Clear();
        _activeTrueLairSpawnRooms.Clear();
        foreach (var playerRooms in _activeSpawnRoomsByPlayer.Values)
        {
            _activeSpawnRooms.UnionWith(playerRooms);
            foreach (var roomKey in playerRooms)
            {
                var room = GetRoom(roomKey.Map, roomKey.Room);
                if (room?.RoomType == TrueLairSpawnRoomType && CanRoomGenerateLairMonsters(room))
                    _activeTrueLairSpawnRooms.Add(roomKey);
            }
        }

        if (_activeTrueLairSpawnCursor >= _activeTrueLairSpawnRooms.Count)
            _activeTrueLairSpawnCursor = 0;
    }

    /// <summary>
    /// Place a room's primary NPC (Room.NPC) during lazy room initialisation. Returns the instance it
    /// created, or null when nothing spawned — the CALLER must announce it once it has dropped
    /// _monsterLock, because a spawn is never silent (see below).
    ///
    /// Stock ends EVERY successful spawn — on every call path, primary NPC
    /// included — with the arrival block: the colour prefix → format the template's MoveMsg →
    /// broadcast to the room → the entry-movement noise. There is no silent spawn path. The only
    /// monsters that arrive without a line are the ones whose MoveMsg points at a PRESENT-but-BLANK
    /// message record (the #66 sentinel shared by 321 templates — cutpurse and the other sneaks);
    /// FormatMonsterSpawnMessage already returns null for exactly those.
    ///
    /// This path used to spawn with no broadcast at all, so a guardian golem (#68, MoveMsg 8430 "A
    /// guardian golem stomps in from %s.") could materialise beside a standing player in total
    /// silence, and was only revealed by the next room description.
    /// </summary>
    /// <param name="bypassOccupancyGate">
    /// Set only by the deliberate out-of-band room restore (sysop `SYSOP RESET &lt;room&gt;` / test setup),
    /// which must rebuild the room to its pristine state whether or not anyone is standing in it. Organic
    /// callers leave this false so the entry requirement below holds.
    /// </param>
    private MonsterInstance? EnsurePermanentNpcInRoom(Room room, (int Map, int Room) key, List<MonsterInstance> roomMonsters, bool bypassOccupancyGate = false)
    {
        if (room.NPC <= 0 || !Database.Monsters.TryGetValue(room.NPC, out var npcTemplate))
            return null;

        // The "present" latch is THIS room's primary being alive — here, or anywhere it chased a player
        // off to (a locked Room.NPC follows its target out; see CanMonsterPursueThroughExit). A different
        // room's primary that chased in does not count.
        if (roomMonsters.Any(m => m.IsPermanentNPC && m.Template.Number == npcTemplate.Number)
            || IsRoomPrimaryAliveAway(room, npcTemplate.Number))
            return null;

        // A primary NPC NEVER materialises in front of a player who is merely standing in the room.
        // Room.NPC reaches the spawn from exactly two places in the whole engine:
        // the room-entry gate — a player ENTERING, before the mover joins the room — and the
        // boot-time preload, which walks every room before anyone is
        // online. The medium-tick background spawner never passes it (it only ever passes
        // the room group / index window / by-number), and the "present" latch is
        // cleared only by the kill on the primary's death. So the primary appears on entry,
        // or it was already there — confirmed in game by camping a 1h-RegenTime unique through its
        // whole window without it returning.
        //
        // This lazy path is our stand-in for the boot pass, so it may only populate a room nobody is
        // in. With a player present the entry path (EnsureRoomNpcPresent) owns the decision, which is
        // also why this check sits ahead of the RegenTime gate — that gate re-rolls its jitter on every
        // evaluation, and this path can be reached many times a second.
        if (!bypassOccupancyGate && GetPlayersInRoom(key.Map, key.Room).Count > 0)
            return null;

        if (!CanSpawnMonster(npcTemplate))
            return null;

        // A unique timer mob killed less than RegenTime ago must not reappear when the room is
        // (re)initialized either; on first-ever init there is no recorded death so it spawns.
        if (!IsRegenTimerElapsed(npcTemplate))
            return null;

        var npc = MonsterInstance.Create(npcTemplate, key.Map, key.Room, isPermanentNPC: true, guaranteeAllDrops: ShouldGuaranteeFirstDrop(npcTemplate));
        roomMonsters.Add(npc);
        AdjustGlobalCount(npcTemplate.Number, 1);
        return npc;
    }

    private bool IsRoomBoundMonster(Monster template)
    {
        return _boundNpcRoomsByMonsterNumber.ContainsKey(template.Number);
    }

    private static bool IsAtHomeRoom(MonsterInstance monster)
        => monster.MapNumber == monster.HomeMapNumber && monster.RoomNumber == monster.HomeRoomNumber;

    // True when this room's primary NPC is alive in some OTHER room — it locked onto a player and chased
    // them out. Stock's "primary present" latch is cleared only by the primary's death, so while it roams
    // the room must not spawn a second one (a GameLimit-0 primary would otherwise double up). Caller holds
    // _monsterLock.
    private bool IsRoomPrimaryAliveAway(Room room, int npcNumber)
    {
        foreach (var kvp in _roomMonsters)
        {
            if (kvp.Key.Map == room.MapNumber && kvp.Key.Room == room.RoomNumber)
                continue;

            var snapshot = kvp.Value;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var m = snapshot[i];
                if (!m.IsDead && m.IsPermanentNPC && m.Template.Number == npcNumber
                    && m.HomeMapNumber == room.MapNumber && m.HomeRoomNumber == room.RoomNumber)
                    return true;
            }
        }
        return false;
    }

    private bool CanMonsterPursueThroughExit(MonsterInstance monster, Player player, (int Map, int Room) from, Room destinationRoom, RoomExitDefinition exit)
    {
        if (monster.IsDead)
            return false;

        // A room's own NPC (the Room.NPC primary, or any template pinned to NPC rooms) stays home UNTIL it
        // is locked onto the player who is leaving — then it chases like any other monster. Stock has no
        // stay-home rule at all: its fast update moves every monster whose target-name slot is set,
        // primaries included (the duergar lord follows an Outlaw he just swung at out of his office, as
        // long as the door is open). Our pin still holds for ambient wander and a Leader's drag.
        bool roomNpc = monster.IsPermanentNPC || IsRoomBoundMonster(monster.Template);
        if (roomNpc && !monster.IsLockedOnTarget(player.Name))
            return false;

        if (monster.MapNumber != from.Map || monster.RoomNumber != from.Room)
            return false;

        if (monster.Template.FollowPercent <= 0 || !exit.HasDestination || exit.IsRemoteAction)
            return false;

        // Monster Type: 0=Solo, 1=Leader, 2=Follower, 3=Stationary. Only
        // a Stationary monster is barred from chasing an engaged target out of its room; Solo — the default,
        // mobile majority (e.g. a Ghost, 308) — pursues like Leaders/Followers do. (This gate previously also
        // blocked Solo, so most monsters could never follow a fleeing player — bug #153.)
        if (monster.Template.Type == 3)
            return false;

        return CanMonsterEnterRoomByMovement(monster.Template, destinationRoom, exit, ignoreRoomPin: roomNpc)
            && CanMonsterTraverseExitType(monster.Template, exit, GetLiveDoorLockState(exit));
    }

    // ── The one mover ───────────────────────────────────────
    // Every monster relocation funnels through it: ambient wander (the medium pass),
    // pursuit of a locked target (the fast pass), and a Leader's drag (its own tail call with the
    // leader flag set). So its destination precondition and exit-type switch bind all three identically.
    // They live here as one gate all of our paths call — they used to be two ad-hoc filters that had
    // drifted apart, which is how a chase leaked through a searchable exit (bug #234).

    private const int GuardMonsterGroup = 5;       // patrols, and passes locked doors/gates
    private const int SummonedMonsterGroup = 37;   // summoned/conjured, not a guard
    private const int RoamingMonsterGroup = 38;    // ignores every destination restriction

    // The mover's destination precondition, which guards the whole switch:
    //     dest.MonsterType == monster.Group
    //  || Group 38
    //  || (Group 37 && dest has no flags && dest.RoomType != 5)
    //  || (Group 5 && dest is PATROL && exit type != 19)
    // Note there is NO "MonsterType > 0" guard in stock: a Group-0 monster may enter a Group-0 room.
    // That test is right for SPAWNING (IsUnboundMonsterCompatibleWithRoom, which keeps its own rule) but
    // was never the mover's.
    private bool CanMonsterEnterRoomByMovement(Monster template, Room destinationRoom, RoomExitDefinition exit, bool ignoreRoomPin = false)
    {
        if (IsSysopTestingRoom(destinationRoom.MapNumber, destinationRoom.RoomNumber))
            return true;

        // Local design, no stock counterpart: an NPC pinned to a room list never wanders or gets dragged
        // out of it (ambient wander refuses it outright, so this is reached via a Leader's drag). The one
        // exception is a chase: a pinned NPC LOCKED onto a fleeing player passes ignoreRoomPin and takes
        // the stock destination rule below, because stock has no pin to begin with.
        if (!ignoreRoomPin && _boundNpcRoomsByMonsterNumber.TryGetValue(template.Number, out var boundRooms))
            return boundRooms.Contains((destinationRoom.MapNumber, destinationRoom.RoomNumber));

        return IsMovementDestinationCompatible(template, destinationRoom, exit);
    }

    // The stock rule on its own, with none of our local special-casing, so it can be pinned by unit test.
    internal static bool IsMovementDestinationCompatible(Monster template, Room destinationRoom, RoomExitDefinition exit)
    {
        int group = template.Group;

        if (destinationRoom.MonsterType == group)
            return true;

        if (group == RoamingMonsterGroup)
            return true;

        if (group == SummonedMonsterGroup)
            return destinationRoom.Attributes == 0 && destinationRoom.RoomType != ArenaRoomType;

        // The PATROL room flag is guard pathing and nothing else: a Guard may step outside its own
        // MonsterType only into PATROL rooms, and never through a BlockGuard (type 19) exit.
        if (group == GuardMonsterGroup)
            return destinationRoom.IsPatrollable && exit.ExitType != RoomExitType.BlockGuard;

        return false;
    }

    // The mover's exit-type switch, on the exit's imported lock field.
    internal static bool CanMonsterTraverseExitType(Monster template, RoomExitDefinition exit)
        => CanMonsterTraverseExitType(template, exit, exit.LockStateRaw);

    // The same switch with the door/gate lock field as it stands NOW (see GetLiveDoorLockState). Stock
    // reads that field live from the room record, where OPEN writes 0, CLOSE writes 1 and a lock or the
    // relock timer writes 2; every imported door/gate row carries 2, its starting state. Testing only the
    // import kept every monster but a guard shut out of a door a player had just opened — nothing ever
    // chased or wandered through an open door. doorLockState is read for Door/Gate exits only.
    internal static bool CanMonsterTraverseExitType(Monster template, RoomExitDefinition exit, int doorLockState)
    {
        int group = template.Group;
        switch (exit.ExitType)
        {
            // cases 1 / 3 / 4 / 6 / 8 / 0xc all hard-"return '\0'" — the monster does not move. A
            // searchable exit is impassable to monsters PERMANENTLY, not merely while unrevealed: the
            // switch never reads the Para1 reveal bitfield, so a chase cannot leak through one behind a
            // player who just found it (bug #234 — saracens followed 12/2089 -> 12/2088 west).
            case RoomExitType.Spell:
            case RoomExitType.Item:
            case RoomExitType.Toll:
            case RoomExitType.Hidden:
            case RoomExitType.ChangeMap:
            case RoomExitType.RemoteAction:
                return false;

            // case 2 — blocked while the Key exit's lock field (Para2) is non-zero. Opening
            // writes 1 over 2 and the relock writes 2 back, so an UNLOCKED barrier is still non-zero:
            // monsters never follow a player through a lock they just watched him pick. Only a
            // never-lockable exit (field 0) passes, and every live Key row is 2.
            case RoomExitType.Key:
                return exit.LockStateRaw == 0 || group == RoamingMonsterGroup;

            // cases 7 / 11 — the same test on the Door/Gate lock field, with Guards exempt as well: a
            // guard chases you through a door. Here the field is live: an OPEN door passes anyone.
            case RoomExitType.Door:
            case RoomExitType.Gate:
                return doorLockState == 0 || group == GuardMonsterGroup || group == RoamingMonsterGroup;

            // Everything else has no case and falls through to the move — Normal, Action, Trap, Text,
            // BlockGuard, Class/Race/Level/Alignment, Cast, Ability, SpellTrap. The class/race/level/
            // alignment/toll denials are entry-gate checks on the PLAYER path and are not
            // consulted here, and case 22 (Cast) casts only on a user, never on a passing monster.
            default:
                return true;
        }
    }

    // A door/gate's lock field as it stands now, in stock's encoding: 0 open, 1 closed, 2 locked. The
    // runtime open/unlocked state (shared by both sides of the door) is the source of truth; any other
    // exit type reports its imported field unchanged.
    private int GetLiveDoorLockState(RoomExitDefinition exit)
    {
        if (exit.ExitType is not (RoomExitType.Door or RoomExitType.Gate))
            return exit.LockStateRaw;

        var state = GetExitState(exit);
        RefreshTimedExitState(exit, state);
        return state.Access.IsOpen ? 0 : state.Access.IsUnlocked ? 1 : 2;
    }

    // Random-direction pick: walk directions 0-9, keep only the exit types an
    // ambient wander may use, and pick by reservoir — the first candidate is taken outright, each later
    // one replaces it on a roll of 0..99 < 40. Returns null when the room offers none.
    private static readonly RoomExitType[] WanderableExitTypes =
    [
        RoomExitType.Normal, RoomExitType.Key, RoomExitType.Action, RoomExitType.Door,
        RoomExitType.Gate, RoomExitType.BlockGuard, RoomExitType.SpellTrap,
    ];

    private RoomExitDefinition? PickWanderExit(Room room)
    {
        RoomExitDefinition? picked = null;
        foreach (var exit in room.GetExitDefinitions().Values.OrderBy(def => def.DirectionIndex))
        {
            if (!exit.HasDestination || Array.IndexOf(WanderableExitTypes, exit.ExitType) < 0)
                continue;

            if (picked == null || _rng.Next(0, 100) < 40)
                picked = exit;
        }

        return picked;
    }

    // Exit types 9 (Trap) and 24 (SpellTrap) fire the exit's trap on the CROSSING MONSTER.
    // Armed state is the live Para2 — {0,2,3} armed, 1 disarmed — and the roll is
    // over [Para1/2, Para1].
    //
    // The two cases are the same code apart from one clamp, and that clamp decides life or death:
    //   Trap      — no clamp, so HP goes negative and the monster is reaped; a chaser can die in the trap.
    //   SpellTrap — clamps at the floor, so it NEVER kills; stock leaves the monster sitting at 0 HP.
    // Stock reads Para1 as a damage value in BOTH, but on a spell-trap Para1 is the SPELL id (905
    // "poison darts", 851 "bridge trigger spell"), so those exits roll 425-905 and always clamp. That
    // reads as a copy-paste of the trap case, but nothing documents it as a known bug, so it is ported
    // as stock has it rather than "fixed" on a hunch.
    //
    // The clamp target differs by one because the death predicates do: stock reaps at HP < 0 and so
    // leaves a live monster at 0, while MonsterInstance.IsDead is HP <= 0. Clamping to 1 is what
    // preserves the observable rule — a spell-trap never kills.
    private void ApplyMonsterExitTrap(MonsterInstance monster, RoomExitDefinition exit)
    {
        if (!exit.IsDisarmableTrapExit || exit.Para1 <= 0 || IsTrapCurrentlyDisarmed(exit))
            return;

        monster.CurrentHP -= _rng.Next(exit.Para1 / 2, exit.Para1 + 1);

        if (exit.IsSpellTrapExit)
        {
            if (monster.CurrentHP < 1)
                monster.CurrentHP = 1;
            return;
        }

        // A trap kill has no killer to credit, exactly like the poison-upkeep death: loot drops, the
        // death line goes to the room, no XP. Deferred to the next medium tick so the monster's own
        // movement lines land first, which is also when stock reaps it (the medium pass's
        // leading dead-monster check).
        if (monster.IsDead)
            _pendingExitTrapKills.Enqueue(monster);
    }

    private readonly System.Collections.Concurrent.ConcurrentQueue<MonsterInstance> _pendingExitTrapKills = new();

    private void ReapExitTrapKilledMonsters()
    {
        while (_pendingExitTrapKills.TryDequeue(out var monster))
            KillMonsterFromUpkeep(monster);
    }

    private bool IsMonsterAllowedInRoom(Monster template, Room room)
    {
        if (IsSysopTestingRoom(room.MapNumber, room.RoomNumber))
            return true;

        if (_boundNpcRoomsByMonsterNumber.TryGetValue(template.Number, out var boundRooms))
            return boundRooms.Contains((room.MapNumber, room.RoomNumber));

        return IsUnboundMonsterCompatibleWithRoom(template, room);
    }

    private MonsterMovementAnnouncement MoveMonster(MonsterInstance monster, (int Map, int Room) from, (int Map, int Room) to, string direction)
    {
        // A lair monster wandering away does NOT free its spawn slot or arm the lair's respawn timer —
        // it still counts against its lair (CountLivingLairOffspring) until it dies. Only death/removal
        // frees the slot (the lair count and regen timer are touched on the kill, never on a move).
        if (_roomMonsters.TryGetValue(from, out var fromList))
        {
            fromList.Remove(monster);
            if (fromList.Count == 0)
                _roomMonsters.TryRemove(from, out _);
        }

        if (!_roomMonsters.ContainsKey(to))
            _roomMonsters[to] = [];
        _roomMonsters[to].Add(monster);

        monster.MapNumber = to.Map;
        monster.RoomNumber = to.Room;
        // The mover writes the direction on EVERY relocation, so a
        // dragged follower is anti-backtrack-limited next tick just like one that moved on its own.
        monster.LastMoveDirection = direction;
        monster.RecordMovementTrail(to.Map, to.Room);

        string name = string.IsNullOrWhiteSpace(monster.DisplayName)
            ? monster.Template.Name
            : monster.DisplayName;

        return new MonsterMovementAnnouncement(
            from.Map,
            from.Room,
            to.Map,
            to.Room,
            direction,
            name,
            monster.Template.ExitMessage,
            monster.Template.EntranceMessage);
    }

    private void AnnounceMonsterMovements(IReadOnlyList<MonsterMovementAnnouncement> movements)
    {
        foreach (var movement in movements)
        {
            string fromDirection = GetOppositeDirection(movement.Direction);

            // Wandering monster movement is ALWAYS the generic line — it
            // never consults the per-monster MoveMsg (that's the spawn-only path).
            BroadcastToRoom(movement.FromMap, movement.FromRoom,
                $"{MudAnsi.Green}{FormatMonsterDepartureMessage(movement.Name, movement.Direction)}{MudAnsi.Reset}",
                reprompt: true,
                prependLineBreak: true);

            BroadcastToRoom(movement.ToMap, movement.ToRoom,
                $"{MudAnsi.Green}{FormatMonsterArrivalMessage(movement.Name, fromDirection)}{MudAnsi.Reset}",
                reprompt: true,
                prependLineBreak: true);

            BroadcastAdjacentMovementNoise(
                movement.ToMap,
                movement.ToRoom,
                excludeRooms: new[]
                {
                    (movement.FromMap, movement.FromRoom),
                    (movement.ToMap, movement.ToRoom),
                });
        }
    }

    private void MoveMonsterPursuit(MonsterInstance monster, Player player, (int Map, int Room) from, (int Map, int Room) to, string direction)
    {
        // Pursuit (kiting) does not free the lair slot or arm its timer either — death-only, as above.
        if (_roomMonsters.TryGetValue(from, out var fromList))
        {
            fromList.Remove(monster);
            if (fromList.Count == 0)
                _roomMonsters.TryRemove(from, out _);
        }

        if (!_roomMonsters.ContainsKey(to))
            _roomMonsters[to] = [];
        _roomMonsters[to].Add(monster);

        monster.MapNumber = to.Map;
        monster.RoomNumber = to.Room;
        monster.LastMoveDirection = direction;
        monster.RecordMovementTrail(to.Map, to.Room);

        string name = string.IsNullOrWhiteSpace(monster.DisplayName)
            ? monster.Template.Name
            : monster.DisplayName;

        // A monster chasing a player into a room prints the SAME generic movement line
        // as a wander (verified in stock: "nasty skeletal warrior moves into the room from the west").
        // There is no "follows <player>" message and the per-monster MoveMsg is not used here.
        BroadcastToRoom(from.Map, from.Room,
            $"{MudAnsi.Green}{FormatMonsterDepartureMessage(name, direction)}{MudAnsi.Reset}",
            reprompt: true,
            prependLineBreak: true);

        BroadcastToRoom(to.Map, to.Room,
            $"{MudAnsi.Green}{FormatMonsterArrivalMessage(name, GetOppositeDirection(direction))}{MudAnsi.Reset}",
            reprompt: true,
            prependLineBreak: true);
    }

    // The mover broadcasts every monster room change — wander OR chasing a player —
    // with these hardcoded generic strings (no per-monster MoveMsg lookup). Up/down use the
    // "above/below" + "upwards/downwards" variants; the MoveMsg flavor text is used only on SPAWN
    // never on movement.
    private static string FormatMonsterDepartureMessage(string name, string travelDirection)
    {
        string tail = travelDirection switch
        {
            "up" => "just left upwards.",
            "down" => "just left downwards.",
            _ => $"just left to the {travelDirection}.",
        };
        return $"{MudAnsi.BrightYellow}{name}{MudAnsi.Green} {tail}";
    }

    private static string FormatMonsterArrivalMessage(string name, string fromDirection)
    {
        // fromDirection is GetOppositeDirection(travel): up→"below", down→"above".
        string tail = fromDirection switch
        {
            "below" => "moves into the room from below.",
            "above" => "moves into the room from above.",
            _ => $"moves into the room from the {fromDirection}.",
        };
        return $"{MudAnsi.BrightYellow}{name}{MudAnsi.Green} {tail}";
    }

    // The spawn line is prefixed BrightYellow (ends
    // in ESC[1;33m); the rest-of-line Green (ESC[0;32m) is emitted ONLY right
    // after a dynamically substituted name. The substitution is selected by the monster flag
    // = DescTxt (the flavour-adjective list):
    //   • DescTxt != 0 — flavour-named (e.g. the dwarven royal guards "fierce/happy/thin …"): the
    //     (flavoured) name fills the message's first %s, then the line switches to Green. "A %s stomps
    //     into the area." → BrightYellow "A fierce dwarven royal guard" + Green " stomps into the area."
    //   • DescTxt == 0 — name baked into the message, the lone %s is the source ("A barmaid walks in
    //     from %s."). No name is substituted, so NO Green is emitted and the WHOLE line stays
    //     BrightYellow (faithful to stock).
    // Tiers when there is no usable MoveMsg fall back to a substituted name (name BrightYellow, rest Green).
    // The spawn-arrival line:
    //   MoveMsg == 0                       → "<name> just arrived from nowhere." (the no-message default)
    //   MoveMsg → message with room text   → that custom entrance line ("A %s crawls in from %s.", …)
    //   MoveMsg → PRESENT-but-BLANK message → SILENT: stock prints nothing at all.
    // The third case is the universal blank "silence" sentinel (message 1/66/121/…): 321 monsters point
    // MoveMsg at #66 alone (e.g. the death-spell-summoned "dying master assassin" #745) so they spawn
    // with no arrival line — verified byte-for-byte in stock wccmsg2.dat (record for #66 is all blank).
    // Stock drops NO content messages, so a non-zero MoveMsg that resolves to no text is always that
    // blank sentinel; we return null (no line) rather than the stock "moves into the room from nowhere"
    // fallback, which only ever fired for a genuinely-missing record (none exist in stock data).
    // Returns null when the spawn must be silent — callers skip the broadcast.
    internal static string? FormatMonsterSpawnMessage(Monster template, string name)
    {
        if (template.MoveMsg != 0)
        {
            // The spawn tests the record and its Line1
            // separately: `if (msg == NULL || Line1 == '\0') { if (msg == NULL) print generic }`. So an
            // ABSENT record still gets an arrival line, and only a PRESENT-but-blank one is silent.
            if (template.MoveMsgMissing)
                return $"{MudAnsi.BrightYellow}{name}{MudAnsi.Green} moves into the room from nowhere.";

            if (string.IsNullOrEmpty(template.EntranceMessage))
                return null; // blank silence sentinel → no spawn line at all

            string body = template.DescTxt != 0
                ? SubstituteSpawnNameToken(template.EntranceMessage, name, "nowhere")
                : template.EntranceMessage.Replace("%s", "nowhere", StringComparison.Ordinal);
            return MudAnsi.BrightYellow + body;
        }

        return $"{MudAnsi.BrightYellow}{name}{MudAnsi.Green} just arrived from nowhere.";
    }

    public void BroadcastAdjacentMovementNoise(
        int mapNumber,
        int roomNumber,
        IEnumerable<(int Map, int Room)>? excludeRooms = null,
        IGameClient? except = null)
    {
        var excluded = excludeRooms != null
            ? new HashSet<(int Map, int Room)>(excludeRooms)
            : [];

        if (!_incomingMovementExitsByTargetRoom.TryGetValue((mapNumber, roomNumber), out var incomingExits))
            return;

        foreach (var incoming in incomingExits)
        {
            // Only the exit types the entry-movement switch accepts carry sound (see
            // IncomingMovementExit). A hidden passage, keyed door, spell portal or action exit stays
            // silent, so movement does not leak through them.
            if (!incoming.CarriesMovementNoise)
                continue;

            var roomKey = (incoming.Map, incoming.Room);
            if (excluded.Contains(roomKey))
                continue;

            var hearers = GetPlayersInRoom(incoming.Map, incoming.Room);
            foreach (var hearer in hearers)
            {
                if (except?.Player != null && hearer.Name.Equals(except.Player.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!CanHearAdjacentMovement(hearer))
                    continue;

                SendToPlayer(
                    hearer.Name,
                    FormatAdjacentMovementNoise(incoming.Direction),
                    reprompt: true,
                    prependLineBreak: true);
            }
        }
    }

    /// <summary>
    /// Adjacent-room movement noise is NOT a perception check — it fires every time. We rolled
    /// Clamp(25 + Perception, 25, 95) against d100, so a listener silently missed anywhere from 5% to
    /// 75% of the movement next door. The entry-movement display contains no randomness at all:
    /// it walks the ten exits and broadcasts unconditionally, with no excluded user, so everyone
    /// in the neighbouring room hears it. Confirmed live against a stock board — five consecutive
    /// moves in the adjacent room produced five "You hear movement to the northeast." lines.
    /// The unconscious guard is kept deliberately: a player who is out cold hearing footsteps is our
    /// own small concession, not something stock models either way.
    /// </summary>
    private static bool CanHearAdjacentMovement(Player player) => !player.IsUnconscious;

    private static string FormatAdjacentMovementNoise(string direction)
    {
        return direction switch
        {
            "up" => $"{MudAnsi.Magenta}You hear movement above you!{MudAnsi.Reset}",
            "down" => $"{MudAnsi.Magenta}You hear movement below you!{MudAnsi.Reset}",
            _ => $"{MudAnsi.Magenta}You hear movement to the {direction}.{MudAnsi.Reset}"
        };
    }

    // The first %s becomes the (already-BrightYellow) monster name, after which the line switches to
    // Green; a second %s (a source/direction) is filled verbatim. Mirrors the stock move/spawn colours
    // (name ESC[1;33m, rest ESC[0;32m). Used only when the message templates the name (DescTxt != 0).
    private static string SubstituteSpawnNameToken(string template, string name, string source)
    {
        int first = template.IndexOf("%s", StringComparison.Ordinal);
        if (first < 0)
            return template;

        string result = template[..first] + name + MudAnsi.Green + template[(first + 2)..];
        int second = result.IndexOf("%s", StringComparison.Ordinal);
        if (second >= 0)
            result = result[..second] + source + result[(second + 2)..];
        return result;
    }

    private static string GetOppositeDirection(string direction) => MudDirections.Opposite(direction);
}