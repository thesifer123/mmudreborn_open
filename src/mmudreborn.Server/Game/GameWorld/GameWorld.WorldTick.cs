using System.Diagnostics;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

public partial class GameWorld
{
    public void Start()
    {
        SeedBackgroundLairSpawnDeadlines();

        _fastTimer = new Timer(FastTick, null, FastTickInterval, FastTickInterval);
        _mediumTimer = new Timer(MediumTick, null, MediumTickInterval, MediumTickInterval);
        _slowTimer = new Timer(SlowTick, null, SlowTickInterval, SlowTickInterval);
        _roomSpellTimer = new Timer(RoomSpellTick, null, RoomSpellTimerInterval, RoomSpellTimerInterval);
        _combatTimer = new Timer(CombatTick, null, CombatTickInterval, CombatTickInterval);
        StartPlayerWriteBehind();
        StartSpawnBubbleRefresh();
        StartRealmBus();
        InitOnlinePresence();
        if (!RuntimeConfiguration.IsQuietTestLoggingEnabled())
            Console.WriteLine("World simulation started.");
    }

    // Test-only: FREEZE world-time by stopping ALL real background timers (fast/medium/slow world ticks,
    // the combat round timer, and the room-spell pulse) so a test drives only what it advances manually.
    // Without this a background real tick can race a seeded counter (double-applying regen), be skipped by
    // the tick re-entrancy guard, wander a monster into the room, or run combat rounds that kill the test
    // player before an assertion — all of which make tests non-deterministic under full-suite load. Use
    // for tests that don't specifically verify real-time tick/combat cadence; re-arm with Resume.
    public void PauseWorldTickTimersForTests()
    {
        _fastTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _mediumTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _slowTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _combatTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _roomSpellTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    public void ResumeWorldTickTimersForTests()
    {
        _fastTimer?.Change(FastTickInterval, FastTickInterval);
        _mediumTimer?.Change(MediumTickInterval, MediumTickInterval);
        _slowTimer?.Change(SlowTickInterval, SlowTickInterval);
        _combatTimer?.Change(CombatTickInterval, CombatTickInterval);
        _roomSpellTimer?.Change(RoomSpellTimerInterval, RoomSpellTimerInterval);
    }

    // Manual-clock hooks mirroring the three independent stock timers. Each advances only its own
    // tick body, so a test drives exactly the cadence the behavior under test lives on.
    public void AdvanceFastTicksForTests(int count)
    {
        for (int index = 0; index < count; index++)
            FastTick(null);
    }

    public void AdvanceMediumTicksForTests(int count)
    {
        for (int index = 0; index < count; index++)
            MediumTick(null);
    }

    public void AdvanceSlowTicksForTests(int count)
    {
        for (int index = 0; index < count; index++)
            SlowTick(null);
    }

    private int GetEvilPointForgivenessCycleTicks()
    {
        return EvilPointForgivenessCycleMinutes <= 0
            ? 0
            : EvilPointForgivenessCycleMinutes * SlowTicksPerMinute;
    }

    private void ResetPlayerEvilPointForgivenessTimer(string playerName)
    {
        _playerNextEvilPointForgivenessTick.TryRemove(playerName, out _);

        int cycleTicks = GetEvilPointForgivenessCycleTicks();
        if (cycleTicks > 0)
            _playerNextEvilPointForgivenessTick[playerName] = _slowTickCount + cycleTicks;
    }

    private void ResetAllEvilPointForgivenessTimers()
    {
        _playerNextEvilPointForgivenessTick.Clear();

        int cycleTicks = GetEvilPointForgivenessCycleTicks();
        if (cycleTicks <= 0)
            return;

        foreach (var playerName in _onlinePlayers.Keys)
            _playerNextEvilPointForgivenessTick[playerName] = _slowTickCount + cycleTicks;
    }

    private static bool ResetDailyEvilPointForgivenessWindow(Player player, int currentDayNumber)
    {
        if (player.LastEvilPointForgivenessDayNumber == currentDayNumber)
            return false;

        if (player.LastEvilPointForgivenessDayNumber == 0 && player.EvilPointsForgivenToday == 0)
            return false;

        player.LastEvilPointForgivenessDayNumber = currentDayNumber;
        player.EvilPointsForgivenToday = 0;
        return true;
    }

    private void ProcessEvilPointForgiveness(Player player, DateTime now)
    {
        int currentDayNumber = DateOnly.FromDateTime(now).DayNumber;
        if (ResetDailyEvilPointForgivenessWindow(player, currentDayNumber))
            PlayerRepo.SavePlayer(player);

        // Forgiveness drives EvilPoints DOWN toward the Saint floor (-220), not just toward 0 — a player
        // who plays clean drifts neutral -> good -> saint over time (mudinfo evil/legal spec). Nothing
        // left to forgive only once they have bottomed out at the floor.
        //
        // The floor is the scale's -220 unless the realm runs MINEPS and this player set their own
        // minimum (`set mineps`), in which case the drift stops there instead — see
        // GetEvilPointForgivenessFloorFor. A player already at or under their minimum is simply done:
        // the tick is dropped, so they hold the standing they earned instead of being forgiven out of
        // their gear while they script.
        float forgivenessFloor = GetEvilPointForgivenessFloorFor(player);

        if (MaxEvilPointsForgivenPerDay <= 0 || EvilPointForgivenessAmount <= 0
            || player.EvilPoints <= forgivenessFloor)
        {
            _playerNextEvilPointForgivenessTick.TryRemove(player.Name, out _);
            return;
        }

        int cycleTicks = GetEvilPointForgivenessCycleTicks();
        if (cycleTicks <= 0)
        {
            _playerNextEvilPointForgivenessTick.TryRemove(player.Name, out _);
            return;
        }

        int nextForgivenessTick = _playerNextEvilPointForgivenessTick.GetOrAdd(player.Name, _ => _slowTickCount + cycleTicks);
        if (_slowTickCount < nextForgivenessTick)
            return;

        _playerNextEvilPointForgivenessTick[player.Name] = _slowTickCount + cycleTicks;

        float remainingDailyAllowance = MaxEvilPointsForgivenPerDay - player.EvilPointsForgivenToday;
        if (remainingDailyAllowance <= 0)
            return;

        // Only forgive down to the floor (so the per-cycle amount shrinks as the player approaches it and
        // never overshoots).
        float forgiveness = Math.Min(player.EvilPoints - forgivenessFloor, EvilPointForgivenessAmount);
        forgiveness = Math.Min(forgiveness, remainingDailyAllowance);
        if (forgiveness <= 0)
            return;

        int alignmentBucketBefore = CombatEngine.GetAlignmentBucket(player.EvilPoints);
        player.EvilPoints = Math.Max(forgivenessFloor, player.EvilPoints - forgiveness);
        player.EvilPointsForgivenToday += forgiveness;
        player.LastEvilPointForgivenessDayNumber = currentDayNumber;

        // As forgiveness drifts the player Neutral→Good
        // (etc.), strip the worn items the new alignment forbids ("Your <item> has been removed."). Only
        // when the band actually changed — and the per-player CommandParser carries the unequip helpers.
        if (CombatEngine.GetAlignmentBucket(player.EvilPoints) != alignmentBucketBefore)
        {
            var client = GetClientForPlayer(player.Name);
            if (client != null)
                new CommandParser(client, this, player).RevalidateWornItemsAfterAlignmentChange(player, alignmentBucketBefore);
        }

        PlayerRepo.SavePlayer(player);
    }

    public void UseManualRoomSpellTicksForTests()
    {
        _useManualRoomSpellTicksForTests = true;
        _roomSpellTestNowUtc = DateTime.UtcNow;
    }

    public void AdvanceRoomSpellSecondsForTests(int count)
    {
        if (count <= 0)
            return;

        _useManualRoomSpellTicksForTests = true;
        _roomSpellTestNowUtc ??= DateTime.UtcNow;

        for (int index = 0; index < count; index++)
        {
            _roomSpellTestNowUtc = _roomSpellTestNowUtc.Value.AddSeconds(1);
            _ = RoomSpellTick(_roomSpellTestNowUtc.Value);
        }
    }

    // Run a background-timer tick body under the single global WorldStateGate. Commands and the combat
    // beat already mutate world/player state under this gate ("one cooperative thread"); the
    // background ticks (regen, poison, active-spell upkeep, room-spell casts, monster movement/spawn)
    // MUST hold it too or they race a command mid-mutation and throw "Collection was modified" (e.g. a
    // tick removing an expired ActiveSpell while a command enumerates the same list). Gate-hold time is
    // not recorded here — each tick has its own duration profiling; only the fault is captured, tagged
    // with `source` so SYSOP DIAG points at the culprit subsystem.
    private void RunGatedTickBody(string source, Action body)
    {
        try
        {
            WorldStateGate.Wait();
            GameDiagnostics.MarkGateAcquired(source);
            try { body(); }
            finally
            {
                GameDiagnostics.MarkGateReleased();
                WorldStateGate.Release();
            }
        }
        catch (Exception ex)
        {
            GameDiagnostics.RecordBackgroundException(ex, source);
            if (GameDiagnostics.RethrowBackgroundExceptions) throw;
        }
    }

    private void RoomSpellTick(object? state)
    {
        if (_useManualRoomSpellTicksForTests)
            return;

        int concurrentTicks = System.Threading.Interlocked.Increment(ref _roomSpellTickInProgress);
        if (concurrentTicks > 1)
            GameDiagnostics.RecordRoomSpellTickOverlap(concurrentTicks);

        try
        {
            RunGatedTickBody("tick:room-spell", () =>
            {
                var now = DateTime.UtcNow;
                bool profileTick = GameDiagnostics.WorldTickProfilingEnabled;
                long tickStart = profileTick ? Stopwatch.GetTimestamp() : 0;
                var stats = RoomSpellTick(now);

                if (profileTick)
                {
                    long elapsedTicks = Stopwatch.GetTimestamp() - tickStart;
                    int elapsedMs = elapsedTicks <= 0
                        ? 0
                        : (int)Math.Min(int.MaxValue, elapsedTicks * 1000 / Stopwatch.Frequency);
                    GameDiagnostics.RecordRoomSpellTickMeasurement(
                        now,
                        elapsedMs,
                        stats.PlayersProcessed,
                        stats.ActiveSpellRooms,
                        stats.PulsesTriggered,
                        concurrentTicks);
                }
            });
        }
        finally
        {
            System.Threading.Interlocked.Decrement(ref _roomSpellTickInProgress);
        }
    }

    private RoomSpellTickStats RoomSpellTick(DateTime now)
    {
        int playersProcessed = 0;
        int activeSpellRooms = 0;
        int pulsesTriggered = 0;

        foreach (var player in _onlinePlayers.Values)
        {
            playersProcessed++;
            var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
            if (room?.Spell > 0)
                activeSpellRooms++;

            if (ProcessRoomSpellTick(player, now))
                pulsesTriggered++;
        }

        return new RoomSpellTickStats(playersProcessed, activeSpellRooms, pulsesTriggered);
    }

    // Fast background pass (1s): rest/meditate regen counters per online character.
    private void FastTick(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _fastTickInProgress, 1) == 1)
            return;

        try { RunGatedTickBody("tick:fast", FastTickImpl); }
        finally
        {
            System.Threading.Volatile.Write(ref _fastTickInProgress, 0);
        }
    }

    private void FastTickImpl()
    {
        foreach (var player in _onlinePlayers.Values)
        {
            ProcessRestRegenTick(player);
            ProcessMeditateRegenTick(player);
        }
    }

    // Slow background pass (30s): natural HP/mana regen, poison, evil-point forgiveness.
    private void SlowTick(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _slowTickInProgress, 1) == 1)
            return;

        try { RunGatedTickBody("tick:slow", SlowTickImpl); }
        finally
        {
            System.Threading.Volatile.Write(ref _slowTickInProgress, 0);
        }
    }

    private void SlowTickImpl()
    {
        _slowTickCount++;
        var now = DateTime.UtcNow;

        foreach (var player in _onlinePlayers.Values)
        {
            // Out of the Realm (TRAIN STATS editor): the character is suspended — no poison drain, no
            // regen, no forgiveness tick. They re-enter fully when the editor closes.
            if (player.IsOutOfRealm)
                continue;

            ProcessEvilPointForgiveness(player, now);

            // Poison ticks once per 30s slow pass (not the
            // medium tick) — "You feel ill", HP -= PoisonLevel, then the kill check.
            if (player.PoisonLevel > 0)
            {
                bool wasUnconscious = player.IsUnconscious;
                // Raw HP -= PoisonLevel, with NO resistance scaling.
                // Poison immunity (ability 21) is BOOLEAN and gates poison at APPLICATION
                // (ability 19 → ApplyMonsterPoisonLevel), so an immune target never reaches
                // PoisonLevel > 0 and never gets here — there is nothing to scale. (The earlier graded
                // (100-resist)/100 model wrongly let poison apply to a Kang and then blocked its rest;
                // live v1.11p shows a Kang takes 0 damage AND can rest = never poisoned, and the
                // "You feel ill" it still sees is the spell's separate ability-115 message, not this tick.)
                player.CurrentHP -= player.PoisonLevel;
                SendToPlayer(player.Name, $"{MudAnsi.BrightRed}You feel ill.{MudAnsi.Reset}");
                if (player.CurrentHP <= Player.DeathHP)
                {
                    ProcessWorldTickDeath(player, $"{MudAnsi.BrightRed}{MudAnsi.BgBlack}You have succumbed to the poison!{MudAnsi.Reset}", "poison");
                    continue;
                }
                if (player.CurrentHP <= 0 && !wasUnconscious)
                {
                    player.IsSneaking = false;
                    player.IsHidden = false;
                    player.DropMortallyWoundedKeepingAttackers();
                    SendToPlayer(player.Name, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                    // The drop line broadcasts with no excluded user, so it
                    // drop line reaches the WHOLE room, not just the person going down.
                    SendToOthersInRoom(player, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                    continue;
                }
            }

            // An unconscious player BLEEDS -1 HP
            // per SLOW (30s) tick while unaided (death via the kill check), or recovers +1 HP per tick while
            // aided. This is the unconscious branch of the same slow pass whose conscious else-branch is
            // the natural regen below. (Was wrongly on the 3s medium tick — players bled out 10x too fast,
            // ~45s from 0 to -15 instead of the stock ~7.5 min; aid recovery was likewise 10x too fast.)
            if (player.IsUnconscious)
            {
                if (!player.IsAided)
                {
                    if (player.ApplyBleedTick())
                        ProcessWorldTickDeath(player, $"{MudAnsi.BrightRed}{MudAnsi.BgBlack}You have bled to death!{MudAnsi.Reset}", "bleeding out");
                }
                else
                {
                    player.ApplyAidRecoveryTick();
                    StopDraggingTargetIfRecovered(player);
                }
                continue;
            }

            // Natural regen: ((Level+20)*Health)/750 (min 1) × HP-regen
            // bonus, applied once per 30s slow tick. The ×3 rest path and the meditate path live on
            // the fast tick; this is the baseline trickle. It applies REGARDLESS of combat —
            // the slow pass has no fighting gate (only poison/unconscious, handled above via
            // `continue`, preempt it) — so HP and mana keep climbing during a fight at the 30s rate.
            int hpBefore = player.CurrentHP;
            int manaBefore = player.CurrentMana;
            if (player.CurrentHP < player.MaxHP)
            {
                // Base = max(1, (Level+20)*Health/750) — the max(1,…)
                // floors the BASE only. The HP-regen bonus is then folded as (bonus+100)*base/100 with NO
                // lower clamp (only the upper curHP<=maxHP clamp exists). So a strongly negative heal-rate
                // (ability 123 — cursed gear like the Death Shroud #1579, total -150) yields a negative
                // amount and DRAINS HP each 30s tick. The old Math.Max(1,…) after the fold was a divergence
                // that turned the curse into a +1 heal.
                int regen = GetPassiveHpRegen(player);
                player.CurrentHP = Math.Min(player.MaxHP, player.CurrentHP + regen);

                // There is no death check in the conscious regen branch — a drained player simply drops
                // below 0 and the unconscious-bleed branch (handled above) finishes them on later ticks.
                // Route the drop through the same transition poison uses so nobody sits alive at negative HP
                // (and so an overshoot past DeathHP in one big-drain tick still dies instead of getting stuck
                // below the unconscious window CurrentHP<=DeathHP where the bleed branch no longer fires).
                if (regen < 0 && player.CurrentHP <= 0)
                {
                    if (player.CurrentHP <= Player.DeathHP)
                    {
                        ProcessWorldTickDeath(player, $"{MudAnsi.BrightRed}{MudAnsi.BgBlack}Your lifeforce withers away!{MudAnsi.Reset}", "a withering lifeforce");
                        continue;
                    }
                    player.IsSneaking = false;
                    player.IsHidden = false;
                    player.DropMortallyWoundedKeepingAttackers();
                    SendToPlayer(player.Name, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                    // The drop line broadcasts with no excluded user, so it
                    // drop line reaches the WHOLE room, not just the person going down.
                    SendToOthersInRoom(player, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                    continue;
                }
            }
            // Inside the same curMana<maxMana gate, the rolled amount is
            // added unconditionally and then clamped low (to 0) and high (to MaxMana) — there is no
            // "only if positive" test, so a negative mana-regen bonus drains the pool instead of being
            // ignored. Unlike the HP branch a drained pool has no further consequence; it just floors at 0.
            if (player.CurrentMana < player.MaxMana)
            {
                int regen = GetPassiveManaRegen(player);
                if (regen != 0)
                    player.CurrentMana = Math.Clamp(player.CurrentMana + regen, 0, player.MaxMana);
            }

            // The prompt is re-emitted after the natural trickle (same as the
            // rest/meditate fast paths) so a player who is just sitting SEES HP/mana climb without
            // pressing enter. Only reprompt when something actually changed, to avoid redrawing the
            // line every 30s for players already at full.
            if (player.CurrentHP != hpBefore || player.CurrentMana != manaBefore)
                RepromptPlayer(player.Name);
        }

        // Periodically flush ground items/currency so a hard kill/crash loses at most a few minutes of
        // dropped loot (graceful Stop() and nightly cleanup also persist).
        if (_slowTickCount % GroundStatePersistSlowTicks == 0)
            PersistRoomGroundState();

        // Replenish finite shop stock on the slow tick.
        ProcessShopRestocks(now);

        // Persist depleted shop stock on the same cadence as ground state (only when something changed),
        // so purchases survive a hard kill/crash with at most a few minutes of loss (Stop() also persists).
        if (_slowTickCount % GroundStatePersistSlowTicks == 0 && _shopStockDirty)
            PersistShopStock();

        // Proactive health alerting: surface lag/overload/errors to sysops without anyone polling.
        RunHealthMonitor(now);

        // Self-healing presence: republish the web online-players roster every slow pass so it recovers
        // from any missed event or transient DB write failure, and reaps ghosts after a hard crash. The
        // event-driven writes (enter/leave/train/invis) keep it instant; this is just the safety net.
        PublishOnlinePresence();
    }

    // Fast rest path: the rest counter fires when > 20 ⇒ HP regen ≈ every
    // 20 fast (1s) ticks, at the natural-regen amount ×3, only while resting and HP < max.
    // NOTE: stock only prints "RESTING - REST REGEN %d" when a per-user display flag is set;
    // it is OFF by default, so stock players never see it. We have no
    // such toggle, so we apply the regen silently rather than spamming the line every ~20s.
    private void ProcessRestRegenTick(Player player)
    {
        if (!player.IsResting)
        {
            player.RestRegenTicks = 0;
            return;
        }

        // Safety net for Bug #172: a resting player who is knocked unconscious is no longer resting in the
        // stock (being hit clears the rest flag). Any damage path that bypassed that hook is caught here —
        // stop resting and never regen HP while down, so you can't heal back up through a knockout.
        if (player.CurrentHP <= 0)
        {
            player.BreakRestAndMeditate();
            return;
        }

        if (++player.RestRegenTicks <= RestRegenFastTicks)
            return;

        player.RestRegenTicks = 0;
        if (player.InCombat)
            return;

        if (player.CurrentHP < player.MaxHP)
        {
            // Base×3 for the rest bonus, then the same unclamped
            // (bonus+100)*regen/100 fold — no lower floor (see slow path). Cursed heal-rate drains while
            // resting too. If it pushes CurrentHP to/below 0 the guard at the top of this method breaks the
            // rest on the next fast tick, and the always-on passive slow tick runs the unconscious/death
            // transition — so no extra death handling is needed here.
            int regen = GetRestHpRegen(player);
            player.CurrentHP = Math.Min(player.MaxHP, player.CurrentHP + regen);
        }

        // Re-emit the prompt on every rest cycle (unconditionally,
        // outside the HP-changed branch) so the player SEES their HP climb without pressing enter
        // (bug #99). The "RESTING - REST REGEN %d" line stays gated behind the off-by-default display
        // flag; only the prompt refresh is unconditional.
        RepromptPlayer(player.Name);
    }

    // Fast meditate path: the meditate counter fires when > 14 ⇒ mana regen ≈
    // every 15 fast (1s) ticks at the base rate ×1 (the slow tick regenerates mana independently);
    // the player wakes automatically at full mana ("You awake from deep meditation.").
    // NOTE: like rest, the per-tick "MEDITATE - MANA REGEN %d" line is gated behind an off-by-default
    // per-user display flag in stock, so we apply the regen silently. The wake-at-full line below is
    // always shown (stock shows it unconditionally).
    private void ProcessMeditateRegenTick(Player player)
    {
        if (!player.IsMeditating)
        {
            player.MeditateRegenTicks = 0;
            return;
        }

        // Same as rest (Bug #172): a knocked-out player isn't meditating — stop and don't regen mana.
        if (player.CurrentHP <= 0)
        {
            player.BreakRestAndMeditate();
            return;
        }

        if (++player.MeditateRegenTicks <= MeditateRegenFastTicks)
            return;

        player.MeditateRegenTicks = 0;
        if (player.InCombat)
            return;

        if (player.CurrentMana < player.MaxMana)
        {
            // The meditate path adds the BASE mana-regen
            // amount only — unlike the passive 30s path, it does NOT scale by the
            // mana-regen bonus. The bonus-scaled passive trickle still runs alongside this, so
            // meditating is always net-positive; the extra meditate pulses simply aren't bonus-amplified.
            int regen = GetMeditateManaRegen(player);
            if (regen > 0)
                player.CurrentMana = Math.Min(player.MaxMana, player.CurrentMana + regen);
        }

        if (player.CurrentMana >= player.MaxMana)
        {
            player.IsMeditating = false;
            SendToPlayer(player.Name, "You awake from deep meditation.");
        }

        // Re-emit the prompt on every meditate cycle so the player
        // SEES their mana climb (and the wake) without pressing enter (bug #99, same as rest). The
        // "MEDITATE - MANA REGEN %d" line stays gated behind the off-by-default display flag.
        RepromptPlayer(player.Name);
    }

    // Medium background pass (3s): inherits the old WorldTick period. Light burnout,
    // active-spell upkeep, knockdown, monster movement/regen/respawn, background generation,
    // ambient broadcasts, timed-exit expiration, daily-cleanup gate.
    private void MediumTick(object? state)
    {
        if (System.Threading.Interlocked.Exchange(ref _mediumTickInProgress, 1) == 1)
        {
            GameDiagnostics.RecordWorldTickSkipped();
            return;
        }

        try { RunGatedTickBody("tick:medium", MediumTickImpl); }
        finally
        {
            System.Threading.Volatile.Write(ref _mediumTickInProgress, 0);
        }
    }

    private void MediumTickImpl()
    {
        _mediumTickCount++;
        bool profileTick = GameDiagnostics.WorldTickProfilingEnabled;
        long tickStart = profileTick ? Stopwatch.GetTimestamp() : 0;
        long phaseStart = tickStart;
        int timedExitMs = 0;
        int monsterLockMs = 0;
        int monsterBuffMs = 0;
        int deadMonsterMs = 0;
        int movementMs = 0;
        int movementAnnounceMs = 0;
        int cleanupMs = 0;
        int lairMs = 0;
        int ambientMs = 0;
        int persistMs = 0;
        int playerUpkeepMs = 0;
        List<MonsterMovementAnnouncement> movementAnnouncements = [];
        BackgroundGenerationStats backgroundStats = default;
        var now = DateTime.UtcNow;

        ProcessTimedExitExpirations(now);
        if (profileTick)
            timedExitMs = MarkPhase(ref phaseStart);

        long monsterLockStart = profileTick ? Stopwatch.GetTimestamp() : 0;
        lock (_monsterLock)
        {
            if (profileTick)
                phaseStart = Stopwatch.GetTimestamp();

            List<MonsterInstance>? upkeepKills = null;
            foreach (var kvp in _roomMonsters)
            {
                foreach (var monster in kvp.Value)
                {
                    ProcessMonsterKnockdownTick(monster);

                    if (!monster.IsDead && monster.ActiveSpells.Count > 0)
                    {
                        // Apply over-time spell effects FIRST, then age the
                        // slots (mirrors the player order in ProcessActiveSpellUpkeep). A drain death is
                        // deferred — skip the rest of this monster's upkeep.
                        if (monster.TickSpellUpkeepEffects(Database))
                        {
                            (upkeepKills ??= new List<MonsterInstance>()).Add(monster);
                            continue;
                        }
                        monster.TickActiveSpells(Database);
                    }

                    // HP regen by per-monster rate, clamped to maxHP,
                    // once per fixed slow-update cycle (NOT every tick — see MonsterHpRegenIntervalTicks).
                    monster.TickHpRegen(MonsterHpRegenIntervalTicks);

                    // Drain monster poison on the same slow pass. Defer
                    // any resulting death until after this enumeration — RemoveDeadMonster mutates the list.
                    if (monster.TickPoison(MonsterHpRegenIntervalTicks))
                        (upkeepKills ??= new List<MonsterInstance>()).Add(monster);
                }
            }
            if (upkeepKills != null)
                foreach (var monster in upkeepKills)
                    KillMonsterFromUpkeep(monster);
            if (profileTick)
                monsterBuffMs = MarkPhase(ref phaseStart);

            foreach (var kvp in _roomMonsters)
            {
                var deadMonsters = kvp.Value.Where(m => m.IsDead).ToList();
                foreach (var dead in deadMonsters)
                {
                    // Primary NPCs (Room.NPC) respawn only when a player enters the room
                    // (EnsureRoomNpcPresent), never on a tick timer. Leave the dead instance in
                    // place for in-room revival on the next entry.
                    if (dead.IsPermanentNPC)
                        continue;

                    ScheduleLairRespawnAfterVacancy(dead, now);
                    kvp.Value.Remove(dead);
                }

                if (kvp.Value.Count == 0)
                    _roomMonsters.TryRemove(kvp.Key, out _);
            }
            if (profileTick)
                deadMonsterMs = MarkPhase(ref phaseStart);

            // The medium background pass runs the monster update on EVERY medium tick
            // (3s), and that pass resets the realm-wide 3-attempt wander budget and rolls
            // ambient wander. Our medium tick is also 3s, so wander must run every tick -- the old %5
            // gate made roaming 5x too sparse (guards never seen entering/leaving, no "You hear
            // movement..." cues). Unlike room spells, monster updates are NOT every-other-gated.
            // Monsters the previous tick's exit traps killed are reaped here, before this tick moves
            // anything — matching the monster update, which opens with a dead-monster reap.
            ReapExitTrapKilledMonsters();
            movementAnnouncements = ProcessMonsterMovement();
            if (profileTick)
                movementMs = MarkPhase(ref phaseStart);

            if (_mediumTickCount % 10 == 0)
            {
                CleanupMonstersInInvalidRooms();
                if (profileTick)
                    cleanupMs = MarkPhase(ref phaseStart);
            }
        }
        if (profileTick)
        {
            monsterLockMs = ElapsedMilliseconds(monsterLockStart, Stopwatch.GetTimestamp());
            phaseStart = Stopwatch.GetTimestamp();
        }

        AnnounceMonsterMovements(movementAnnouncements);
        if (profileTick)
            movementAnnounceMs = MarkPhase(ref phaseStart);

        // Player-pets: advance the follow-abandon counter for separated pets and let in-room pets
        // assist their owner against hostile monsters (faithful monster-vs-monster slice).
        ProcessPlayerPetsTick(now);

        // Stock parity: the spawn driver runs on its own "Monster Generation Rate" timer
        // (in seconds), NOT every medium tick. Running it each 3s medium tick spawned ~5x too
        // fast ("never relents"). Gate it to MonsterGenerationRateSeconds (default 5s, configurable),
        // counted in medium ticks so the test harness's manual AdvanceMediumTicks still drives it.
        int mediumTicksPerGeneration = Math.Max(1, MonsterGenerationRateSeconds / (int)MediumTickInterval.TotalSeconds);
        if (_mediumTickCount % mediumTicksPerGeneration == 0)
        {
            backgroundStats = ProcessBackgroundMonsterGeneration();
        }
        if (profileTick)
            lairMs = MarkPhase(ref phaseStart);

        // Area "ambient flavor" (Silvermere "drunken chorus", Darkwood forest noises, …) is NOT a
        // room-name bucket and NOT the dormant random-event path (which reads each
        // monster's own textblock, which in stock is always the naming-adjective list → never fires).
        // It is a ROOM SPELL: a room's Spell (918 "silvermere spell" / 915 "darkwood forest spell" / …)
        // carries ability 148 -> textblock "random <tb>" -> a weighted "<threshold>:message <id>" table.
        // It is handled in ApplyRoomSpellPulse (room-spell pulse, below), so it's area-tied via room.Spell
        // and correctly absent where Spell==0 (e.g. Newhaven). The old name-keyword ProcessAmbientBroadcasts
        // was a fabrication that fired "drunken chorus" in Newhaven by matching "street"; removed.

        foreach (var player in _onlinePlayers.Values)
        {
            // Per-character medium work. Poison, natural HP/mana regen and evil-point forgiveness
            // moved to the slow tick; rest/meditate regen moved to the fast tick (stock cadence).
            player.ClearAidIfRecovered();
            StopDraggingTargetIfRecovered(player);
            if (ProcessActiveSpellUpkeep(player))
                continue;
            ProcessPlayerKnockdownTick(player);
            player.RecentMoveCount = 0;
            player.SneakedInThisTick = false;
            player.RecentlySpotted = false;

            // Unconscious players bleed / recover on the SLOW tick, not here.
            // On the medium tick they only skip the out-of-combat stamina refill.
            if (player.IsUnconscious)
                continue;

            if (!player.InCombat)
                player.RefillStaminaOutOfCombat();
        }

        if (now >= _nextCleanupAtUtc)
            RunDailyCleanup(now);

        if (profileTick)
        {
            playerUpkeepMs = MarkPhase(ref phaseStart);
            int totalMs = ElapsedMilliseconds(tickStart, Stopwatch.GetTimestamp());
            GameDiagnostics.RecordWorldTickMeasurement(
                now,
                _mediumTickCount,
                totalMs,
                timedExitMs,
                monsterLockMs,
                monsterBuffMs,
                deadMonsterMs,
                movementMs,
                movementAnnounceMs,
                cleanupMs,
                lairMs,
                ambientMs,
                persistMs,
                playerUpkeepMs,
                movementAnnouncements.Count,
                backgroundStats.ActiveTrueLairRooms,
                backgroundStats.TrueLairRoomsScanned,
                backgroundStats.TrueLairRoomsProcessed,
                backgroundStats.PlayerRooms,
                backgroundStats.AdjacentRoomsChecked,
                backgroundStats.SpawnAttempts,
                backgroundStats.MonstersSpawned);
        }

        static int MarkPhase(ref long startTimestamp)
        {
            long endTimestamp = Stopwatch.GetTimestamp();
            int elapsedMs = ElapsedMilliseconds(startTimestamp, endTimestamp);
            startTimestamp = endTimestamp;
            return elapsedMs;
        }

        static int ElapsedMilliseconds(long startTimestamp, long endTimestamp)
        {
            long elapsedTicks = endTimestamp - startTimestamp;
            if (elapsedTicks <= 0)
                return 0;

            long elapsedMs = elapsedTicks * 1000 / Stopwatch.Frequency;
            return elapsedMs > int.MaxValue ? int.MaxValue : (int)elapsedMs;
        }
    }

    private void ProcessTimedExitExpirations(DateTime now)
    {
        var processedAccessKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ((mapNumber, roomNumber, direction), state) in _roomExitStates)
        {
            var room = GetRoom(mapNumber, roomNumber);
            var exit = room?.GetExit(direction);
            if (exit == null)
                continue;

            // Exit types 9/24: silently re-arm a disarmed trap when its timer
            // fires (no broadcast). Per-state, so it runs before the per-access-key dedup below.
            RefreshTrapReArm(state, now);

            // Exit type 6: a search-revealed hidden exit silently re-hides when
            // its timer fires. Also per-state (per direction), no broadcast.
            RefreshHiddenExitRehide(exit, state, now);

            string accessKey = GetExitAccessKey(exit);
            if (!processedAccessKeys.Add(accessKey))
                continue;

            bool wasLocked = !state.Access.IsUnlocked;
            if (!RefreshTimedExitState(exit, state, now))
                continue;

            if (!wasLocked && !state.Access.IsUnlocked)
                BroadcastTimedExitRelock(exit);
        }
    }

    private bool ProcessRoomSpellTick(Player player, DateTime now)
    {
        var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (room == null || room.Spell <= 0)
        {
            // A no-spell room PAUSES the pulse schedule, it does NOT clear it (see
            // NotifyPlayerEnteredRoom): clearing here would let a player dodge the room spell by
            // stepping onto the bank and back. The schedule is cleaned up on disconnect.
            return false;
        }

        TimeSpan pulseInterval = GetRoomSpellPulseInterval(room);

        var state = _playerRoomSpellStates.GetOrAdd(
            player.Name,
            _ => new PlayerRoomSpellState
            {
                SpellId = room.Spell,
                NextPulseAtUtc = now.Add(pulseInterval),
            });

        if (state.SpellId != room.Spell)
        {
            state.SpellId = room.Spell;
            state.PulseCount = 0;
            state.NextPulseAtUtc = now.Add(pulseInterval);
            return false;
        }

        if (now < state.NextPulseAtUtc)
            return false;

        state.NextPulseAtUtc = GetNextRoomSpellPulseAtUtc(state.NextPulseAtUtc, pulseInterval, now);
        ApplyRoomSpellPulse(player, room, state);
        return true;
    }

    private bool ApplyRoomSpellPulse(Player player, Room room, PlayerRoomSpellState state)
    {
        if (!Database.Spells.TryGetValue(room.Spell, out var spell))
            return false;

        // Most room spells (magma heat, swamp poison, temple fire, etc. — 263+ Lava Tube-style
        // rooms alone) don't carry ability 148 (Trigger Text Block); they're meant to cast
        // directly each pulse like a plain room cast. Only the trigger/dispatcher
        // spells (Silver River, desert, sea biomes, ...) use the textblock pattern. Route
        // accordingly: textblock → script ops; otherwise → cast the room.Spell straight at the
        // player so its abilities (damage, descriptive message, …) actually fire.
        string? textBlock = null;
        if (spell.Abilities.TryGetValue(RoomSpellTriggerTextBlockAbility, out var textBlockId)
            && textBlockId > 0)
        {
            Database.TextBlocks.TryGetValue(textBlockId, out textBlock);
        }

        if (textBlock == null)
        {
            // Direct-cast room spells (magma heat, magma drip, …) return a Reprompt outcome when
            // damage lands so the player's command prompt repaints below the BrightRed damage
            // line. StopProcessing means the player died / dropped — don't reprompt over that.
            var outcome = ApplyRoomSpellCast(player, room.Spell);
            if (outcome == RoomSpellCastOutcome.Reprompt)
                RepromptPlayer(player.Name);
            return true;
        }

        if (room.Spell == SilverRiverSpellId && PlayerHasSilverRiverFloatation(player))
            return false;

        // Gated / scripted room spells carry the full special-command vocabulary — alignment/class/level
        // gates, teleport, item ops — that the simple fast loop below does NOT evaluate. The fast loop
        // flat-splits on ':' and only understands message/cast/random/failitem, so for these blocks it
        // would skip every gate and unconditionally run the trailing `cast`/`teleport` op on EVERY player
        // (e.g. White Forest noise 1079 burning good paladins mid-quest and never ejecting evil intruders;
        // church check 1147 ignoring its nomonsters/evil gate). Stock runs the ability-148 pulse through
        // the text-block special-command engine — the same engine as a room CMD textblock — so route gated
        // blocks there. The fast loop stays for the simple ambient/biome spells (Silvermere/Darkwood/Silver
        // River: message/cast/random) to preserve their per-pulse reprompt + Silver River coloring.
        if (RoomSpellTextBlockNeedsScriptEngine(textBlock))
        {
            var scriptClient = GetClientForPlayer(player.Name);
            if (scriptClient != null)
            {
                new CommandParser(scriptClient, this, player)
                    .RunRoomSpellSpecialCommandAsync(textBlockId)
                    .GetAwaiter().GetResult();
            }

            return false;
        }

        var operations = textBlock
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool shouldRepromptPlayer = false;

        foreach (var operation in operations)
        {
            var args = operation.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (args.Length == 0)
                continue;

            switch (args[0].ToLowerInvariant())
            {
                case "failitem":
                    if (args.Length >= 2 && int.TryParse(args[1], out var itemId) && PlayerHasItem(player, itemId))
                        return false;
                    break;

                case "message":
                    if (args.Length >= 2 && int.TryParse(args[1], out var messageId))
                    {
                        string? lineColor = null;
                        if (room.Spell == SilverRiverSpellId && messageId == SilverRiverBashMessageId)
                        {
                            state.PulseCount++;
                            lineColor = ShouldUseSilverRiverPrimaryColor(state.PulseCount)
                                ? MudAnsi.White
                                : MudAnsi.BrightBlue;
                        }

                        shouldRepromptPlayer |= SendRoomSpellMessage(player, messageId, lineColor, repromptPlayer: false);
                    }
                    break;

                case "cast":
                    if (args.Length >= 2 && int.TryParse(args[1], out var spellId))
                    {
                        // Silent damage: a textblock room spell shows its flavor via the `message`
                        // op above (e.g. the river bash), so the cast op must not also print a
                        // numeric "You take N damage!" line — stock shows only the one flavor line.
                        // The `cast <id>` op runs through the no-target cast, NOT the room cast,
                        // so the room-cast Targets/Duration gates do NOT apply here. The Silver River
                        // bash (#754, Targets 1) reaches the player only because of this.
                        var castOutcome = ApplyRoomSpellCast(player, spellId, announceDamage: false, roomCastGates: false);
                        if (castOutcome == RoomSpellCastOutcome.StopProcessing)
                            return true;

                        shouldRepromptPlayer |= castOutcome == RoomSpellCastOutcome.Reprompt;
                    }
                    break;

                case "random":
                    // The `random <tb>` op: weighted-random pick from the target textblock. This is the
                    // AREA-AMBIENT mechanism — a room's Spell (e.g. 918 "silvermere spell" / 915
                    // "darkwood forest spell") carries ability 148 -> textblock "random 9056"/"9055"
                    // -> a weighted `message <id>` table (town/forest flavor). Area-tied via room.Spell,
                    // monster-independent, and absent where Spell==0 (e.g. Newhaven).
                    if (args.Length >= 2 && int.TryParse(args[1], out var randomTextBlockId))
                        shouldRepromptPlayer |= ResolveRandomRoomSpellTextBlock(player, randomTextBlockId, depth: 0);
                    break;
            }
        }

        if (shouldRepromptPlayer)
            RepromptPlayer(player.Name);

        return false;
    }

    // The fast pulse loop in ApplyRoomSpellPulse understands only these four ops. A textblock that uses
    // ANY other verb (a gate like evilaligned/goodaligned/class/minlevel/nomonsters, a teleport, an item
    // op, …) must instead run through the full special-command engine so its gates are honored, rather
    // than have the fast loop silently skip the gate and run the trailing effect on everyone.
    private static readonly HashSet<string> FastPathRoomSpellOps =
        new(StringComparer.OrdinalIgnoreCase) { "message", "cast", "random", "failitem" };

    internal static bool RoomSpellTextBlockNeedsScriptEngine(string textBlock)
    {
        if (string.IsNullOrEmpty(textBlock))
            return false;

        foreach (var line in textBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var op in line.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int space = op.IndexOf(' ');
                string name = space < 0 ? op : op[..space];
                if (name.Length > 0 && !FastPathRoomSpellOps.Contains(name))
                    return true;
            }
        }

        return false;
    }

    // The `random <tb>` op: roll 0..99 ONCE, then
    // scan the target textblock's newline-separated "<threshold>:<command>" lines in order and run the
    // FIRST line whose threshold exceeds the roll. Thresholds ascend to 100; a leading "<n>:addexp 0"
    // is the n% "nothing happens" slot (e.g. Silvermere 9056 starts "77:addexp 0", so ~77% no output).
    // Returns whether a message landed (so the caller can reprompt).
    private bool ResolveRandomRoomSpellTextBlock(Player player, int textBlockId, int depth)
    {
        if (depth > 4)
            return false; // guard against pathological textblock cycles
        if (textBlockId <= 0
            || !Database.TextBlocks.TryGetValue(textBlockId, out var block)
            || string.IsNullOrEmpty(block))
        {
            return false;
        }

        string? command = SelectWeightedRoomSpellCommand(block, _rng.Next(0, 100)); // genrdn(0,100) = [0,99], top exclusive
        return command != null && RunRoomSpellCommandOps(player, command, depth);
    }

    // Pure weighted-line selector: scan "<threshold>:<command>" lines in
    // order and return the command-part of the FIRST line whose threshold exceeds the roll, else null.
    // Thresholds ascend to 100; a leading "<n>:addexp 0" yields "addexp 0" for roll<n (the no-op slot).
    internal static string? SelectWeightedRoomSpellCommand(string block, int roll)
    {
        foreach (var rawLine in block.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = rawLine.IndexOf(':');
            if (colon <= 0)
                continue;
            if (!int.TryParse(rawLine.AsSpan(0, colon), out var threshold))
                continue;
            if (roll >= threshold)
                continue; // not this slot; keep scanning the ascending table
            return rawLine[(colon + 1)..].Trim();
        }

        return null;
    }

    // Run one ':'-separated command op-list selected from a weighted random table (the part after the
    // winning "<threshold>:"). Supports the ops these ambient tables use: message (flavor line), cast
    // (effect spell), and a nested random (Darkwood 9055 has "...message 2654:random 9069"). addexp is
    // the negligible/"nothing" slot and is a no-op here. Mirrors the matched-action engine.
    private bool RunRoomSpellCommandOps(Player player, string command, int depth)
    {
        bool shouldReprompt = false;
        foreach (var op in command.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var args = op.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (args.Length == 0)
                continue;

            switch (args[0].ToLowerInvariant())
            {
                case "message":
                    if (args.Length >= 2 && int.TryParse(args[1], out var messageId))
                        shouldReprompt |= SendRoomSpellMessage(player, messageId, lineColor: null, repromptPlayer: false);
                    break;

                case "cast":
                    if (args.Length >= 2 && int.TryParse(args[1], out var spellId))
                    {
                        // The textblock `cast` op is a no-target cast, so no room-cast gates (see the
                        // sibling op in ApplyRoomSpellPulse).
                        var outcome = ApplyRoomSpellCast(player, spellId, announceDamage: false, roomCastGates: false);
                        if (outcome == RoomSpellCastOutcome.StopProcessing)
                            return shouldReprompt;
                        shouldReprompt |= outcome == RoomSpellCastOutcome.Reprompt;
                    }
                    break;

                case "random":
                    if (args.Length >= 2 && int.TryParse(args[1], out var nestedTextBlockId))
                        shouldReprompt |= ResolveRandomRoomSpellTextBlock(player, nestedTextBlockId, depth + 1);
                    break;
            }
        }

        return shouldReprompt;
    }

    public void RecalculatePlayerStats(Player player)
    {
        if (!Database.Races.TryGetValue(player.RaceId, out var race) ||
            !Database.Classes.TryGetValue(player.ClassId, out var cls))
        {
            return;
        }

        int currentHp = player.CurrentHP;
        int currentMana = player.CurrentMana;

        player.RecalculateStats(race, cls, Database);
        player.RecalculateEquipment(Database);

        player.CurrentHP = Math.Min(currentHp, player.MaxHP);
        player.CurrentMana = Math.Min(currentMana, player.MaxMana);
    }

    // ---- Regen formulas, single source of truth -------------------------------------------------
    // The `stat all` sheet reports regen to the player, so it MUST read the same numbers the tick
    // applies. These helpers exist so the display cannot drift from the engine again: it used to
    // invent its own (Health/5, SpellCasting/2) values that matched nothing, and reported a clamp
    // ceiling ("6/60") as if it were a cadence.

    // Cadences, straight off the tick scheduler.
    internal static int PassiveRegenIntervalSeconds => (int)SlowTickInterval.TotalSeconds;          // 30s
    internal static int RestRegenIntervalSeconds => RestRegenFastTicks + 1;                          // fires at >20 ⇒ ~21s
    internal static int MeditateRegenIntervalSeconds => MeditateRegenFastTicks + 1;                  // fires at >14 ⇒ ~15s

    // Base = max(1, (Level+20)*Health/750). The max(1,…) floors
    // the BASE only — the bonus fold below has no lower clamp, so cursed heal-rate gear drains HP.
    internal static int GetPassiveHpRegenBase(Player player)
        => Math.Max(1, ((player.Level + 20) * player.Health) / 750);

    // The HP-regen bonus (ability 123) folds in as a percentage, NOT a flat add.
    internal static int ApplyHpRegenBonus(Player player, int amount)
        => player.HpRegenBonus != 0 ? (player.HpRegenBonus + 100) * amount / 100 : amount;

    // Passive HP per 30s tick.
    internal static int GetPassiveHpRegen(Player player)
        => ApplyHpRegenBonus(player, GetPassiveHpRegenBase(player));

    // Resting HP per ~21s fast-tick cycle: the same base ×3, then the same bonus fold.
    internal static int GetRestHpRegen(Player player)
        => ApplyHpRegenBonus(player, GetPassiveHpRegenBase(player) * 3);

    // Passive mana/kai per 30s tick, including the mana-regen bonus (ability 145) fold.
    // The fold is `amount = ((bonus + 100) * amount) / 100` — integer division
    // and, exactly like the HP fold above, NO lower clamp. So the bonus is worth nothing until the base
    // reaches 10 (a +10% item on a 4/30s caster still yields 4), a -100 source zeroes regen outright,
    // and anything past -100 turns the trickle into a drain. Stock data carries all three cases: the
    // black flail #349 at +10, banish #435 and the draka tomb #1115 at -100, the frothing brown potion
    // #1133 at -200. The Math.Max(1,…) that used to sit here floored every one of those at +1, which
    // handed a banished caster mana back — the same divergence GetPassiveHpRegen already documents.
    internal int GetPassiveManaRegen(Player player)
    {
        int regen = GetBaseManaRegenRate(player);
        if (player.ManaRegenBonus != 0)
            regen = (player.ManaRegenBonus + 100) * regen / 100;
        return regen;
    }

    // Meditate mana per ~15s fast-tick cycle: the base rate ×1 and — unlike the passive path —
    // WITHOUT the mana-regen bonus.
    internal int GetMeditateManaRegen(Player player)
        => GetBaseManaRegenRate(player);

    private int GetBaseManaRegenRate(Player player)
    {
        if (!Database.Classes.TryGetValue(player.ClassId, out var cls))
            return 0;
        if (cls.MageryType == Player.KaiMageryType)
            return 1;
        if (cls.MageryLvl <= 0)
            return 0;

        int manaStat = cls.MageryType switch
        {
            1 => player.Intellect,
            2 => player.Willpower,
            3 => (player.Willpower + player.Intellect) / 2,
            4 => player.Charm,
            _ => 0,
        };
        if (manaStat <= 0)
            return 0;

        return ((player.Level + 20) * manaStat * (cls.MageryLvl + 2)) / 1650;
    }

    private bool ProcessActiveSpellUpkeep(Player player)
    {
        if (player.ActiveSpells.Count == 0)
            return false;

        foreach (var active in player.ActiveSpells.ToList())
        {
            if (!Database.Spells.TryGetValue(active.SpellId, out var upkeepSpell))
                continue;

            foreach (var (abil, abilVal) in upkeepSpell.Abilities)
            {
                int value = abilVal != 0 ? abilVal : active.CastLevel;
                switch (abil)
                {
                    case 1:
                    case 8:
                        if (ApplyUpkeepDamageOverTime(player, upkeepSpell, value))
                            return true;
                        break;
                    case 18:
                        if (value > 0)
                            player.CurrentHP = Math.Min(player.MaxHP, player.CurrentHP + value);
                        break;
                    case 20:
                        if (value > 0)
                            player.PoisonLevel = Math.Max(0, player.PoisonLevel - value);
                        break;
                    case 11:
                        if (value > 0)
                            player.CurrentEnergy = Math.Min(player.GetEffectiveMaxStamina(), player.CurrentEnergy + value);
                        break;
                    case 150:
                        player.CurrentMana = Math.Clamp(player.CurrentMana + value, 0, player.MaxMana);
                        break;
                    case 60:
                        if (value > 0 && _rng.Next(0, 100) < value)
                            FleePlayer(player);
                        break;
                }
            }
        }

        bool anyExpired = false;
        foreach (var active in player.ActiveSpells.ToList())
        {
            active.RemainingDuration--;
            if (active.RemainingDuration > 0)
                continue;

            player.ActiveSpells.Remove(active);
            anyExpired = true;
            if (Database.Spells.TryGetValue(active.SpellId, out var expiredSpell))
            {
                // Spell-termination upkeep, ability 19: when a poison spell's
                // duration runs out, subtract the magnitude it applied back off the poison level (clamped
                // >=0) so the poison finally "runs its course". The reversible stat/HP buffs stock also
                // unwinds here are re-derived by RecalculatePlayerStats below; only the imperative poison
                // accumulator lives outside the recalc and so needs this explicit reversal. The
                // same reversal runs on dispel/cure (SpellEffects.ReverseTerminatedPoison callers) so a
                // poison removed early clears the accumulator too. A duration-less poison (no active spell
                // to expire) persists until cured — also faithful.
                SpellEffects.ReverseTerminatedPoison(player, expiredSpell, active.CastLevel);
                var wearOffMessage = ResolveSpellWearOffMessage(expiredSpell, player.Name);
                if (!string.IsNullOrEmpty(wearOffMessage))
                    // Every "...wear off!" line prints
                    // with a fixed preamble ending in ESC[0;33m = Yellow/brown (same as
                    // *Combat Off*), NOT BrightBlue. (Verified byte-for-byte.)
                    SendToPlayer(player.Name, $"{MudAnsi.Yellow}{wearOffMessage}{MudAnsi.Reset}");
                // Ability 151 "Cast on ending": when a timed buff/debuff expires, cast its linked
                // spell on the bearer. This is the jail auto-release backbone — the alignment-scaled
                // "jail time" debuff (586/641-644) carries 151 → "exit jail" (585), which chains to a
                // teleport out to the slums.
                ApplyCastOnEndingEffect(player, expiredSpell);
            }
        }

        if (anyExpired)
            RecalculatePlayerStats(player);
        return false;
    }

    private const int CastOnEndingAbilityId = 151;   // "Cast on ending"

    // Resolve and apply the "cast on ending" follow-up of an expired timed spell. The only stock effect
    // reached this way that needs world action is a teleport (the jail release: 585 → text 9113 → cast
    // 929 → Teleport Room 1076). We follow the linked spell's chain to its teleport destination and move
    // the bearer there; non-teleport follow-ups are left to the (already-applied) buff lifecycle.
    private void ApplyCastOnEndingEffect(Player player, GameSpell expiredSpell)
    {
        if (!expiredSpell.Abilities.TryGetValue(CastOnEndingAbilityId, out int linkedSpellId))
            return;

        // Value-0 fallback: a 0-valued "cast on ending" slot resolves to the rolled MinBase..MaxBase
        // (same convention as scripted-command spells, ability 148). Spell 935 "sysop jail time" relies on this
        // (ability 151 = 0, MinBase 929 "exit jail" → Teleport 1/1076), so without it a sysop-jail timer
        // would expire WITHOUT releasing the prisoner. The alignment debuffs (641-644/586) set 151 directly.
        if (linkedSpellId <= 0)
            linkedSpellId = expiredSpell.MaxBase >= expiredSpell.MinBase && expiredSpell.MinBase > 0
                ? _rng.Next(expiredSpell.MinBase, expiredSpell.MaxBase + 1)
                : 0;

        if (linkedSpellId <= 0)
            return;

        if (!TryResolveChainedSpellTeleport(linkedSpellId, depth: 0, out int targetMap, out int targetRoom, out int terminalSpellId))
            return;

        // The terminal teleport spell of the chain is a real cast, so it shows its CastMsgB BEFORE the
        // ability-140 teleport relocates the player — for the jail release that is #929 "exit jail" carrying
        // message 1700 ("You are let out of jail by the guards, and released in the slums!" / room
        // "%s is dragged out of jail by the guards!"). Our resolver folds 585→textblock 9113→cast 929 into a
        // direct teleport, so emit that cast message here against the ORIGINAL room (the jail) before moving
        // them — without it the prisoner was relocated silently (bug: released with no message).
        EmitChainedReleaseMessage(player, terminalSpellId);

        player.CurrentMapNumber = targetMap;
        player.CurrentRoomNumber = targetRoom;
        player.IsResting = false;
        player.IsMeditating = false;
        NotifyPlayerEnteredRoom(player);
    }

    // Emit a chained-cast terminal spell's CastMsgB (Line2 → the bearer, Line3 → their current room) as the
    // stock would when that spell casts. Used by the cast-on-ending jail-release chain; silent when the
    // terminal spell carries no message (the "66" sentinel / a raw `teleport` script op with no spell).
    private void EmitChainedReleaseMessage(Player player, int terminalSpellId)
    {
        if (terminalSpellId <= 0
            || !Database.Spells.TryGetValue(terminalSpellId, out var spell)
            || spell.CastMessageB <= 0
            || !Database.Messages.TryGetValue(spell.CastMessageB, out var message))
            return;

        var releasedClient = GetClientForPlayer(player.Name);

        string youLine = FormatRoomSpellMessage((message.Line2 ?? string.Empty).Trim(), player.Name);
        if (!string.IsNullOrWhiteSpace(youLine))
            SendToPlayer(player.Name, $"{MudAnsi.White}{youLine}{MudAnsi.Reset}");

        string roomLine = FormatRoomSpellMessage((message.Line3 ?? string.Empty).Trim(), player.Name);
        if (!string.IsNullOrWhiteSpace(roomLine))
            BroadcastToRoom(player.CurrentMapNumber, player.CurrentRoomNumber,
                $"{MudAnsi.White}{roomLine}{MudAnsi.Reset}", releasedClient);
    }

    // Follow a spell to the room/map it ultimately teleports to: a direct Teleport Room (140) / Teleport
    // Map (141), or a Trigger Text Block (148) whose script contains a `cast <id>` / `teleport <room>
    // <map>` op (recursed). Returns false if no reachable destination. Depth-guarded.
    private bool TryResolveChainedSpellTeleport(int spellId, int depth, out int targetMap, out int targetRoom, out int terminalSpellId)
    {
        targetMap = 0;
        targetRoom = 0;
        terminalSpellId = 0;
        if (depth > 6 || !Database.Spells.TryGetValue(spellId, out var spell))
            return false;

        if (spell.Abilities.TryGetValue(140, out int roomAbility))   // 140 = Teleport Room
        {
            targetRoom = roomAbility > 0
                ? roomAbility
                : (spell.MaxBase >= spell.MinBase && spell.MinBase > 0 ? _rng.Next(spell.MinBase, spell.MaxBase + 1) : 0);
            targetMap = spell.Abilities.TryGetValue(141, out int mapAbility) && mapAbility > 0 ? mapAbility : 1;   // 141 = Teleport Map
            terminalSpellId = spellId;   // the spell that actually casts the teleport — carries the CastMsgB
            return targetRoom > 0 && GetRoom(targetMap, targetRoom) != null;
        }

        if (spell.Abilities.TryGetValue(148, out int textBlockId) && textBlockId > 0   // 148 = Trigger Text Block
            && Database.TextBlocks.TryGetValue(textBlockId, out var text))
        {
            foreach (var rawLine in text.Replace("\r", "", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var op in rawLine.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = op.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0].Equals("cast", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(parts[1], out int chainedId)
                        && TryResolveChainedSpellTeleport(chainedId, depth + 1, out targetMap, out targetRoom, out terminalSpellId))
                    {
                        return true;
                    }
                    if (parts.Length >= 3 && parts[0].Equals("teleport", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(parts[1], out int tRoom) && int.TryParse(parts[2], out int tMap)
                        && GetRoom(tMap, tRoom) != null)
                    {
                        targetMap = tMap;
                        targetRoom = tRoom;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    // Spell termination shows the spell's Descriptive Message (ability 115)
    // Line1 as the wear-off text — e.g. blur (#68) → "The effects of blur wear off." A "%d" line is a
    // damage cast template, not a wear-off message, so it doesn't count. A spell with no usable DescMsg
    // expires SILENTLY (returns null) — stock shows nothing. The old generic "Your X wears off"
    // fallback fabricated a line stock never emits, which leaked the invisible quest-flag / utility
    // "temp" spells (e.g. dread mystic temp #603 → "Your dread mystic temp wears off.").
    /// <summary>
    /// Terminate every active spell slot on death, announcing each one's wear-off line, and clear the
    /// poison accumulator. The death path walks all ten slots right after the death
    /// announcement:
    ///     for (slot = 0; slot &lt; 10; slot++)
    ///         if (slot is occupied) {
    ///             load the spell; clear the slot triple (id / magnitude / duration);
    ///             run the termination upkeep;                   // prints the wear-off line
    ///         }
    ///     poison level = 0;
    /// so a dying player really does see "The boils vanish from your body." under the death lines.
    /// We used to clear ActiveSpells silently, which swallowed every one of those lines.
    /// The colour is the same ESC[0;33m yellow/brown natural expiry and dispel already use
    /// (the termination preamble).
    /// </summary>
    public void TerminateActiveSpellsOnDeath(Player player)
    {
        var expired = player.ActiveSpells.ToList();

        player.ActiveSpells.Clear();
        player.PoisonLevel = 0;

        foreach (var active in expired)
        {
            if (!Database.Spells.TryGetValue(active.SpellId, out var spell))
                continue;

            SpellEffects.ReverseTerminatedPoison(player, spell, active.CastLevel);
            var wearOff = ResolveSpellWearOffMessage(spell, player.Name);
            if (!string.IsNullOrEmpty(wearOff))
                SendToPlayer(player.Name, $"{MudAnsi.Yellow}{wearOff}{MudAnsi.Reset}");
        }

        if (expired.Count > 0)
            RecalculatePlayerStats(player);
    }

    private string? ResolveSpellWearOffMessage(GameSpell spell, string playerName)
    {
        const int descMsgAbilityId = 115;
        if (spell.Abilities.TryGetValue(descMsgAbilityId, out int descMsgId)
            && descMsgId > 0
            && Database.Messages.TryGetValue(descMsgId, out var descMsg)
            && !string.IsNullOrWhiteSpace(descMsg.Line1)
            && !descMsg.Line1.Contains("%d", StringComparison.Ordinal))
        {
            return FormatRoomSpellMessage(descMsg.Line1.Trim(), playerName);
        }

        return null;
    }

    private bool ApplyUpkeepDamageOverTime(Player player, GameSpell spell, int value)
    {
        if (value <= 0)
            return false;

        bool wasUnconscious = player.IsUnconscious;
        player.CurrentHP -= value;

        // Routine spell upkeep, abilities 1/8 (verified byte-identical across two
        // 12.1.2): the per-tick active-spell DoT is SILENT — it only does `HP -= magnitude` then
        // a prompt redraw so the HP drop shows. It prints NO line and NO damage
        // number. The per-tick "You are on fire!" a player sees comes from RE-APPLICATION, not this
        // tick: a fire/acid/poison-cloud ROOM re-casts on its occupants every medium tick
        // and each re-cast prints the spell's ability-115 Line3; poison prints its
        // own per-slow-tick "You feel ill." on the slow pass. So the ongoing line belongs on
        // the (re)application path, not here. The old fabricated "You writhe in agony from X." line was
        // never emitted by stock.
        RepromptPlayer(player.Name);

        if (player.CurrentHP <= Player.DeathHP)
        {
            ProcessWorldTickDeath(player, $"{MudAnsi.BrightRed}{MudAnsi.BgBlack}You have succumbed to {spell.Name}!{MudAnsi.Reset}", spell.Name);
            return true;
        }
        if (player.CurrentHP <= 0 && !wasUnconscious)
        {
            player.IsSneaking = false;
            player.IsHidden = false;
            player.DropMortallyWoundedKeepingAttackers();
            SendToPlayer(player.Name, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
            // The drop line broadcasts with no excluded user, so it
            // drop line reaches the WHOLE room, not just the person going down.
            SendToOthersInRoom(player, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
        }
        return false;
    }

    private void FleePlayer(Player player)
    {
        var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (room == null)
            return;

        var exits = room.GetExits()
            .Where(e => !e.Value.Door && e.Value.Map > 0 && e.Value.Room > 0)
            .ToList();
        if (exits.Count == 0)
            return;

        var (direction, dest) = exits[_rng.Next(exits.Count)];
        var fleeingClient = GetClientForPlayer(player.Name);

        BroadcastToRoom(player.CurrentMapNumber, player.CurrentRoomNumber,
            $"{MudAnsi.White}{player.Name} flees to the {direction} in terror!{MudAnsi.Reset}", fleeingClient);

        player.ClearCombatState();
        player.CurrentMapNumber = dest.Map;
        player.CurrentRoomNumber = dest.Room;
        player.IsResting = false;
        player.IsMeditating = false;
        player.IsHidden = false;
        player.IsSneaking = false;
        NotifyPlayerEnteredRoom(player);

        BroadcastToRoom(dest.Map, dest.Room,
            $"{MudAnsi.White}{player.Name} runs in, terrified!{MudAnsi.Reset}", fleeingClient);
        SendToPlayer(player.Name, $"{MudAnsi.BrightRed}In a blind panic, you flee to the {direction}!{MudAnsi.Reset}");

        if (fleeingClient?.Session != null)
            _ = fleeingClient.Session.ShowCurrentRoomAsync();
    }

    private enum RoomSpellEffectKind { Damage, Teleport, Effects }

    // `roomCastGates` = this cast really entered through the room-cast path, so its
    // Targets 0/2/6/8 + Duration 0 gates apply. False for a textblock `cast` op, which stock runs
    // as a no-target cast and which therefore keeps the looser damage test.
    private static RoomSpellEffectKind ClassifyRoomSpell(GameSpell spell, bool roomCastGates)
        => spell.Abilities.ContainsKey(TeleportMapAbilityId) ? RoomSpellEffectKind.Teleport
            : (roomCastGates ? IsRoomCastInstantDamageEligible(spell) : IsRoomSpellDamageEligible(spell))
                ? RoomSpellEffectKind.Damage
            : RoomSpellEffectKind.Effects;

    // `announceDamage` controls whether a damage cast prints a numeric damage line. The stock room-spell
    // path shows ONLY the spell's cast message and applies HP
    // silently — no "You take N damage!". A textblock room spell (silver river, etc.) shows its flavor
    // via a separate `message` op, so its `cast` op must stay silent to match stock (the player sees
    // only "The river bashes you up against some rocks!", not an extra damage line).
    private RoomSpellCastOutcome ApplyRoomSpellCast(Player player, int spellId, bool announceDamage = true, bool roomCastGates = true)
    {
        if (spellId <= 0 || !Database.Spells.TryGetValue(spellId, out var spell))
            return RoomSpellCastOutcome.None;

        // Scan worn equipment for an item whose Negate Spells list
        // (10 entries) contains this spell number. Any match short-circuits the
        // cast — the magma amulet / phoenix feather negate magma heat & temple-of-fire fire,
        // desert gear blocks sandstorms, holy medallion suppresses undead-targeted casts, etc.
        // No reprompt or message: the pulse is silently dropped, matching the way
        // wearing the amulet feels in-game ("I'm in the lava tube and nothing's hitting me").
        if (PlayerHasItemNegatingSpell(player, spellId))
            return RoomSpellCastOutcome.None;

        // Before ANY ability applies, TypeOfResists gates a
        // full-resist roll — TOR 2 = always resistable (arena slow/confusion); TOR 1 = resistable
        // only when the target carries ability 51 Anti Magic (who thereby also shrugs the arena
        // BUFFS); TOR 0 = unresistable. genrdn(1,100) <= min(MagicRes/2, 97) skips the whole cast.
        // (Textblock-trigger room spells route through the script path above, not here — stock
        // applies this gate there too; left as-is to avoid changing trigger-room behavior.)
        if (spell.TypeOfResists == 2
            || (spell.TypeOfResists == 1 && player.GetActiveAbilityValue(Database, RoomSpellAntiMagicAbilityId) != 0))
        {
            if (_rng.Next(1, 100) <= Math.Min(player.MagicResist / 2, 97))
                return RoomSpellCastOutcome.None;
        }

        return ClassifyRoomSpell(spell, roomCastGates) switch
        {
            RoomSpellEffectKind.Teleport => ApplyRoomSpellTeleport(player, spell),
            RoomSpellEffectKind.Damage => ApplyRoomSpellDamage(player, spell, announceDamage),
            _ => ApplyRoomSpellEffects(player, spell),
        };
    }

    // Single direct room-spell cast (negate/resist gates included) against a player — used by the
    // bit5 cast-on-entry path in NotifyPlayerEnteredRoom, and as the unit-test seam.
    internal RoomSpellCastOutcome CastRoomSpellOnPlayer(Player player, int spellId)
        => ApplyRoomSpellCast(player, spellId);

    private const int RoomSpellHealHpAbilityId = 18;
    private const int RoomSpellHealManaAbilityId = 150;
    private const int RoomSpellDispelAbilityId = 73;
    private const int RoomSpellRemoveFormsAbilityId = 81;   // strips spells carrying 74/75
    private const int RoomSpellScatterItemsAbilityId = 157; // relocates the room's loot
    private const int RoomSpellDescMessageAbilityId = 115;  // per-pulse status line (Line3)
    private const int RoomSpellNonMagicalFlagAbilityId = 144; // data flag, explicit stock no-op
    private const int RoomSpellAntiMagicAbilityId = 51;

    // The NON-damage ability application
    // (bug backlog "room-spell non-damage abilities silently skipped"; audit 2026-07-05).
    // Magnitude = uniform MinBase..MaxBase, rolled once per cast (@73556-73572: genrdn(0,(max-min)+1)+min;
    // SpellType-3 room casts take no elemental reduction). Then per ability slot, the spell-level
    // Duration decides the delivery:
    //   • Duration > 0 → ONE active-spell slot (refresh-in-place: same
    //     spell already active = overwrite magnitude + reset duration, never stack). Its abilities fold
    //     in via stat recalc with value = AbilVal, or the rolled magnitude when AbilVal==0 — so #484
    //     inn rest carries AbilVal 200 → HpRegenBonus, #412 mana drain re-rolls -100..-25 each 6s pulse,
    //     the arena buffs (#823-833) ride the same slot mechanism and expire ~Duration after leaving.
    //   • Duration == 0 → instant: 18 heal (capped at MaxHP, @73139-73157), 150 mana heal, 73 dispel,
    //     81 strip-forms, 157 scatter the room's gettable loot.
    // Output: the CastMsgB Line2 cast line ("The room casts …") and the ability-115
    // Line3 status — every stock non-damage room spell references blank sentinel 66 for both (dropped on
    // import → naturally silent) EXCEPT #833's 115=1112 "You are affected by a mana flux!".
    private RoomSpellCastOutcome ApplyRoomSpellEffects(Player player, GameSpell spell)
    {
        int min = Math.Min(spell.MinBase, spell.MaxBase);
        int max = Math.Max(spell.MinBase, spell.MaxBase);
        int magnitude = _rng.Next(min, max + 1);

        bool wantsDurationSlot = false;
        bool effectApplied = false;

        foreach (var (abilityId, abilityValue) in spell.Abilities)
        {
            int value = abilityValue != 0 ? abilityValue : magnitude;
            switch (abilityId)
            {
                case RoomSpellDescMessageAbilityId:       // status line handled below
                case RoomSpellNonMagicalFlagAbilityId:    // explicit stock no-op
                case RoomSpellTriggerTextBlockAbility:    // script pulses route through ApplyRoomSpellPulse
                    break;

                case RoomSpellHealHpAbilityId when spell.Duration == 0:
                    if (value > 0 && player.CurrentHP < player.MaxHP)
                    {
                        player.CurrentHP = Math.Min(player.MaxHP, player.CurrentHP + value);
                        effectApplied = true;
                    }
                    break;

                case RoomSpellHealManaAbilityId when spell.Duration == 0:
                {
                    int newMana = Math.Clamp(player.CurrentMana + value, 0, player.MaxMana);
                    effectApplied |= newMana != player.CurrentMana;
                    player.CurrentMana = newMana;
                    break;
                }

                case RoomSpellDispelAbilityId when spell.Duration == 0:
                    // Magnitude -1 strips ALL active spells; magnitude N strips the
                    // spells that themselves contain ability N (the scan reads the
                    // raw Abil[10] array, so N==0 matches any spell with an UNUSED slot — which is why
                    // the stock strip-all rooms #310/#891/#1115 all roll 0/0: everything short of a
                    // 10-ability spell is stripped).
                    effectApplied |= RemoveActiveSpellsWithTermination(player, active =>
                        value == -1
                        || (Database.Spells.TryGetValue(active.SpellId, out var held)
                            && (held.Abilities.ContainsKey(value) || (value == 0 && held.Abilities.Count < 10))));
                    break;

                case RoomSpellRemoveFormsAbilityId when spell.Duration == 0:
                    // Strips only spells carrying ability 74 or 75 (the forms pair).
                    effectApplied |= RemoveActiveSpellsWithTermination(player, active =>
                        Database.Spells.TryGetValue(active.SpellId, out var held)
                        && (held.Abilities.ContainsKey(74) || held.Abilities.ContainsKey(75)));
                    break;

                case RoomSpellScatterItemsAbilityId:
                    // Move the room's GETTABLE ground items
                    // (fixtures stay) and all coin piles to room <value> on the SAME
                    // map — the Great Pyramid "cleanup" (#691 → random 1239-1278), lich scatter
                    // (#1218 → 1-10), solo mage scatter (#1376 → 3247). It never touches players.
                    effectApplied |= ScatterRoomLoot(player.CurrentMapNumber, player.CurrentRoomNumber, value);
                    break;

                default:
                    if (spell.Duration > 0)
                        wantsDurationSlot = true;
                    break;
            }
        }

        if (wantsDurationSlot && player.AddOrRefreshRoomSpell(spell.Number, magnitude, spell.Duration))
        {
            RecalculatePlayerStats(player);
            effectApplied = true;
        }

        if (!effectApplied)
            return RoomSpellCastOutcome.None;

        // Cast line (CastMsgB Line2, "The room" in the caster slot) then the
        // ability-115 Line3 ongoing-status line, in the SpellType-3 bright-cyan info color. Blank
        // sentinel 66 was dropped on import, so TryGetValue misses = silent, matching stock bytes.
        bool printed = false;
        if (spell.CastMessageB > 0 && Database.Messages.TryGetValue(spell.CastMessageB, out var castMsg)
            && !string.IsNullOrWhiteSpace(castMsg.Line2))
        {
            printed = true;
            SendToPlayer(player.Name, $"{MudAnsi.BrightCyan}{FormatRoomCastLine(castMsg.Line2, spell.Name, magnitude)}{MudAnsi.Reset}");
        }
        if (spell.Abilities.TryGetValue(RoomSpellDescMessageAbilityId, out int descMsgId) && descMsgId > 0
            && Database.Messages.TryGetValue(descMsgId, out var descMsg)
            && !string.IsNullOrWhiteSpace(descMsg.Line3))
        {
            printed = true;
            SendToPlayer(player.Name, $"{MudAnsi.BrightCyan}{descMsg.Line3}{MudAnsi.Reset}");
        }

        return printed ? RoomSpellCastOutcome.Reprompt : RoomSpellCastOutcome.None;
    }

    // Sequential legacy-format fill for the room cast line: caster slot = "The room" (a stock
    // string), then the spell name, then the magnitude for a trailing %d.
    private static string FormatRoomCastLine(string template, string spellName, int magnitude)
    {
        string line = template;
        foreach (string arg in new[] { "The room", spellName })
        {
            int at = line.IndexOf("%s", StringComparison.Ordinal);
            if (at < 0)
                break;
            line = line.Remove(at, 2).Insert(at, arg);
        }
        return line.Replace("%d", magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // Dispel path shared with slot expiry: remove every matching active spell with the full
    // termination side effects (poison reversal, yellow
    // wear-off line, cast-on-ending chain), then one stat recalc.
    private bool RemoveActiveSpellsWithTermination(Player player, Func<ActiveSpell, bool> shouldRemove)
    {
        bool removedAny = false;
        foreach (var active in player.ActiveSpells.ToList())
        {
            if (!shouldRemove(active))
                continue;

            player.ActiveSpells.Remove(active);
            removedAny = true;
            if (Database.Spells.TryGetValue(active.SpellId, out var removedSpell))
            {
                SpellEffects.ReverseTerminatedPoison(player, removedSpell, active.CastLevel);
                var wearOffMessage = ResolveSpellWearOffMessage(removedSpell, player.Name);
                if (!string.IsNullOrEmpty(wearOffMessage))
                    SendToPlayer(player.Name, $"{MudAnsi.Yellow}{wearOffMessage}{MudAnsi.Reset}");
                ApplyCastOnEndingEffect(player, removedSpell);
            }
        }

        if (removedAny)
            RecalculatePlayerStats(player);
        return removedAny;
    }

    // Relocate the source room's gettable ground items (both item arrays;
    // non-gettable fixtures stay) and every coin pile to the destination room on the same map.
    private bool ScatterRoomLoot(int mapNumber, int fromRoom, int destRoom)
    {
        if (destRoom <= 0 || destRoom == fromRoom || GetRoom(mapNumber, destRoom) == null)
            return false;

        bool moved = false;

        var items = GetVisibleGroundItems(mapNumber, fromRoom);
        for (int i = items.Count - 1; i >= 0; i--)   // descending: removals don't shift earlier indexes
        {
            if (!Database.Items.TryGetValue(items[i].ItemId, out var item) || !item.Gettable)
                continue;
            var (itemId, instanceId) = PickUpGroundItemWithInstance(mapNumber, fromRoom, items[i].Index);
            if (itemId > 0)
            {
                // Already lifted off the source floor — spill rather than drop, so a full destination
                // cannot silently consume the item mid-move.
                DisposeOfItemInRoom(mapNumber, destRoom, itemId, instanceId);
                moved = true;
            }
        }

        var coins = PickUpGroundCurrency(mapNumber, fromRoom);
        if (coins.Runic + coins.Platinum + coins.Gold + coins.Silver + coins.Copper > 0)
        {
            DropCurrencyInRoom(mapNumber, destRoom, coins.Runic, coins.Platinum, coins.Gold, coins.Silver, coins.Copper);
            moved = true;
        }

        return moved;
    }

    internal bool PlayerHasItemNegatingSpell(Player player, int spellId)
    {
        if (spellId <= 0)
            return false;

        foreach (int itemId in player.Equipment.Values)
        {
            if (Database.Items.TryGetValue(itemId, out var item)
                && item.NegatedSpellNumbers.Contains(spellId))
            {
                return true;
            }
        }

        return false;
    }

    private RoomSpellCastOutcome ApplyRoomSpellTeleport(Player player, GameSpell spell)
    {
        int targetMap = spell.Abilities.GetValueOrDefault(TeleportMapAbilityId);
        if (targetMap <= 0)
            return RoomSpellCastOutcome.None;

        int targetRoom = spell.Abilities.GetValueOrDefault(TeleportRoomAbilityId);
        if (targetRoom <= 0)
        {
            if (spell.MinBase <= 0 || spell.MaxBase < spell.MinBase)
                return RoomSpellCastOutcome.None;

            var candidates = new List<int>();
            for (int roomNumber = spell.MinBase; roomNumber <= spell.MaxBase; roomNumber++)
            {
                if (GetRoom(targetMap, roomNumber) != null)
                    candidates.Add(roomNumber);
            }

            if (candidates.Count == 0)
                return RoomSpellCastOutcome.None;

            targetRoom = candidates[_rng.Next(candidates.Count)];
        }
        else if (GetRoom(targetMap, targetRoom) == null)
        {
            return RoomSpellCastOutcome.None;
        }

        player.CurrentMapNumber = targetMap;
        player.CurrentRoomNumber = targetRoom;
        player.IsResting = false;
        player.IsMeditating = false;
        player.IsSneaking = false;
        player.IsHidden = false;
        NotifyPlayerEnteredRoom(player);
        RepromptPlayer(player.Name);
        return RoomSpellCastOutcome.StopProcessing;
    }

    // The room cast rolls damage and dispatches every ability the spell carries.
    // Direct-damage abilities (1 HP damage / 17 elemental) route to ApplyRoomSpellDamage; everything
    // else (buff/regen/dispel/heal/scatter — "inn rest" abil 123, "mana rgen drain" 145, …) routes to
    // ApplyRoomSpellEffects, which ports the non-damage switch. Gating on a damage ability
    // keeps MinBase/MaxBase from being misread as a damage roll for effect spells (e.g. #691 cleanup's
    // MinBase 1239 is a ROOM NUMBER).
    private static readonly int[] RoomSpellDamageAbilityIds = [1, 8, 17];

    // The per-ability room-cast dispatcher. EVERY effect case is wrapped in
    // the same two gates, and we had neither, which is why a room/exit cast could deal damage the stock
    // engine never deals:
    //
    //   if (Targets == 0 || 2 || 6 || 8)          // NOT 1/3/5/9-13
    //       if (Duration == 0) { apply instantly; }
    //       else               { timed = 1; }     // a TIMED SLOT instead
    //
    // So an instant hit needs BOTH an eligible target type and Duration 0. Ability 17 (elemental)
    // is the one exception: its case keeps the Targets gate but has NO duration gate and never sets
    // the timed flag, so it always lands instantly and never leaves a slot.
    //
    // This is the root of the reported "stop mud drown hits you for 1 damage!": "exit muddy water" (#681)
    // is Targets 1 / Duration 1, so stock takes NO hit points from it at all — it becomes a 1-second
    // slot whose ability 151 fires the follow-up on expiry. Reading its filler MinBase as damage was invented.
    //
    // Live blast radius of adding the gate: the Targets test excludes nothing (every damaging stock room
    // spell is already 0/2/6/8), and the Duration test only moves "envelops" #830 (ability 1, Duration
    // 10, 2 rooms on map 16) off the per-pulse instant hit and onto the duration slot stock gives it —
    // ApplyRoomSpellEffects already models that slot, so it becomes the damage-over-time it should be.
    private static readonly int[] RoomCastInstantTargetTypes = [0, 2, 6, 8];
    private const int RoomSpellElementalDamageAbilityId = 17;   // no duration gate

    // "Is this spell damage-shaped at all" — a valid MinBase..MaxBase band plus a damage ability. This
    // is the no-target-cast side test, and it must stay free of the room-cast gates: those belong to
    // the room-cast path only. The Silver River bash (#754 "battered", Targets 1) is the proof — it is
    // cast by the `cast 754` op inside room spell #753's textblock, i.e. as a no-target cast, and
    // gating it on Targets 0/2/6/8 stops the river bashing anyone.
    internal static bool IsRoomSpellDamageEligible(GameSpell spell)
    {
        if (spell.MinBase <= 0 || spell.MaxBase < spell.MinBase)
            return false;

        return RoomSpellDamageAbilityIds.Any(spell.Abilities.ContainsKey);
    }

    // The room-cast side test: damage-shaped AND inside the room-cast dispatcher's two gates. Used where
    // stock really does enter through the room cast — a room's own Spell field, the cast-on-entry bit,
    // and exit type 22 — never for a textblock `cast` op.
    internal static bool IsRoomCastInstantDamageEligible(GameSpell spell)
    {
        if (!IsRoomSpellDamageEligible(spell))
            return false;

        if (Array.IndexOf(RoomCastInstantTargetTypes, spell.Targets) < 0)
            return false;

        // Ability 17 (elemental) keeps the Targets gate but has no duration test and never defers to a slot.
        return spell.Abilities.ContainsKey(RoomSpellElementalDamageAbilityId) || spell.Duration == 0;
    }

    // Stock per-element room-spell damage line. Used when the spell's DescMsg (ability 115) is
    // missing or lacks a "%d" damage template. Every entry leads with "You" so Megamud (and other
    // clients) don't misparse the line as a monster hit. AttType values follow the same map as
    // SpellResistanceMath.GetElementResistAbilityId.
    private static readonly Dictionary<int, string> RoomSpellDamageTemplatesByAttType = new()
    {
        [0] = "You are chilled to the bone for {0} damage!",
        [1] = "You are seared by the flames for {0} damage!",
        [6] = "You are wracked by poison for {0} damage!",
    };

    internal string FormatRoomSpellDamageLine(GameSpell spell, int damage)
    {
        // The stock room cast shows the spell's own cast message. The player-targeted line is the
        // spell's CastMsgB — e.g. magma heat (#526 → message 1553 "You are seared by the flames for
        // %d damage!") — with %d → the rolled damage. This is the authoritative stock data; prefer it
        // over the synthetic per-element template below (which only ever coincidentally matched).
        if (TryFormatRoomSpellDamageMessage(spell.CastMessageB, damage, out var castMsgLine))
            return castMsgLine;

        // Secondary data path: DescMsg (ability 115) → Messages row → Line1 with %d.
        const int descMsgAbilityId = 115;
        if (spell.Abilities.TryGetValue(descMsgAbilityId, out int descMsgId)
            && descMsgId > 0
            && Database.Messages.TryGetValue(descMsgId, out var descMsg))
        {
            if (!string.IsNullOrWhiteSpace(descMsg.Line1) && descMsg.Line1.Contains("%d", StringComparison.Ordinal))
                return descMsg.Line1.Replace("%d", damage.ToString(System.Globalization.CultureInfo.InvariantCulture));

            // Some rows only carry a Line3 (e.g. ice storm "You are freezing!" with no damage
            // template); append the damage in a player-perspective wrapper.
            if (!string.IsNullOrWhiteSpace(descMsg.Line3))
                return $"{descMsg.Line3} ({damage} damage)";
        }

        if (RoomSpellDamageTemplatesByAttType.TryGetValue(spell.AttType, out var template))
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, damage);

        return $"You take {damage} damage!";
    }

    // Resolve a spell cast-message id (e.g. CastMsgB) to a player-facing damage line: the first
    // non-empty of Line1/Line2 with its "%d" placeholder replaced by the rolled damage. Returns false
    // when the message is missing or carries no "%d" (a flavor-only line that isn't a damage template),
    // so the caller can fall through to its other sources.
    private bool TryFormatRoomSpellDamageMessage(int messageId, int damage, out string line)
    {
        line = string.Empty;
        if (messageId <= 0 || !Database.Messages.TryGetValue(messageId, out var message))
            return false;

        string template = !string.IsNullOrWhiteSpace(message.Line1) ? message.Line1
            : !string.IsNullOrWhiteSpace(message.Line2) ? message.Line2
            : string.Empty;

        if (string.IsNullOrWhiteSpace(template) || !template.Contains("%d", StringComparison.Ordinal))
            return false;

        line = template.Replace("%d", damage.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    private RoomSpellCastOutcome ApplyRoomSpellDamage(Player player, GameSpell spell, bool announceDamage = true)
    {
        if (!IsRoomSpellDamageEligible(spell))
            return RoomSpellCastOutcome.None;

        bool wasUnconscious = player.IsUnconscious;
        int damage = _rng.Next(spell.MinBase, spell.MaxBase + 1);
        int elementAbility = mmudreborn.Game.Combat.SpellResistanceMath.GetElementResistAbilityId(spell.AttType);
        int elementResist = (spell.SpellType < 3 && elementAbility != 0) ? player.GetActiveAbilityValue(Database, elementAbility) : 0;
        damage = mmudreborn.Game.Combat.SpellResistanceMath.ApplyElementalDamage(spell.SpellType, spell.AttType, damage, elementResist);
        if (spell.TypeOfResists > 0)
            damage = Math.Max(1, damage - (player.MagicResist / 2));

        // The cast-result message dispatcher emits a per-pulse damage line before
        // applying the HP delta — without this the player sees their HP drop with no explanation.
        // Megamud parses room-spell damage from this line, and it must look like a player-
        // perspective spell line (not a monster hit) — Megamud added "magma heat" to its monster
        // DB when the line started with the spell name. Always lead with "You" or another
        // unambiguous player-side phrase. Preference order:
        //   1) DescMsg (ability 115) Line1 carrying "%d" — sprintf-style damage template baked
        //      into the message row, the native stock format
        //   2) Per-AttType "You are ..." stock template, so missing data still produces a
        //      flavored line (magma heat etc. have DescMsg=66 which isn't populated in the live
        //      Messages table)
        //   3) Generic fallback for elements we haven't pinned
        if (damage > 0 && announceDamage)
        {
            SendToPlayer(player.Name, $"{MudAnsi.BrightRed}{FormatRoomSpellDamageLine(spell, damage)}{MudAnsi.Reset}");
        }

        player.CurrentHP -= damage;

        if (player.CurrentHP <= Player.DeathHP)
        {
            ProcessWorldTickDeath(player, null, spell.Name);
            return RoomSpellCastOutcome.StopProcessing;
        }

        if (player.CurrentHP <= 0 && !wasUnconscious)
        {
            player.IsSneaking = false;
            player.IsHidden = false;
            player.DropMortallyWoundedKeepingAttackers();
            SendToPlayer(player.Name, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
            SendToOthersInRoom(player, GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
            return RoomSpellCastOutcome.StopProcessing;
        }

        return RoomSpellCastOutcome.Reprompt;
    }

    // Send `message` to every player in `player`'s current room EXCEPT `player` themselves.
    // Used by the room-spell tick where the affected player and the observers need different
    // wording (self: "You drop…", room: "Name drops…").
    private void SendToOthersInRoom(Player player, string message)
    {
        foreach (var observer in GetPlayersInRoom(player.CurrentMapNumber, player.CurrentRoomNumber)
            .Where(candidate => !candidate.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase)))
        {
            SendToPlayer(observer.Name, message);
        }
    }

    private bool SendRoomSpellMessage(Player player, int messageId, string? lineColor, bool repromptPlayer)
    {
        if (messageId <= 0 || !Database.Messages.TryGetValue(messageId, out var message))
            return false;

        bool sentPlayerLine = false;

        if (!string.IsNullOrWhiteSpace(message.Line1))
        {
            SendToPlayer(
                player.Name,
                ApplyRoomSpellMessageColor(FormatRoomSpellMessage(message.Line1, player.Name), lineColor),
                reprompt: repromptPlayer);
            sentPlayerLine = true;
        }

        if (!string.IsNullOrWhiteSpace(message.Line2))
        {
            string roomMessage = ApplyRoomSpellMessageColor(FormatRoomSpellMessage(message.Line2, player.Name), lineColor);
            SendToOthersInRoom(player, roomMessage);
        }

        return sentPlayerLine;
    }

    private void ProcessPlayerKnockdownTick(Player player)
    {
        if (!player.IsKnockedDown)
            return;

        KnockdownKind kind = player.KnockdownKind;
        int descriptiveMessageId = player.KnockdownDescriptiveMessageId;
        if (!player.TickKnockdown())
            return;

        if (kind == KnockdownKind.Smash)
        {
            var playerClient = GetClientForPlayer(player.Name);
            SendToPlayer(player.Name, $"{MudAnsi.BrightWhite}Slightly dazed you get back onto your feet.{MudAnsi.Reset}");
            BroadcastToRoom(player.CurrentMapNumber, player.CurrentRoomNumber,
                $"{MudAnsi.BrightWhite}Slightly dazed {player.Name} rises from the floor.{MudAnsi.Reset}",
                playerClient,
                reprompt: true);
            return;
        }

        if (descriptiveMessageId > 0
            && Database.Messages.TryGetValue(descriptiveMessageId, out var message)
            && !string.IsNullOrWhiteSpace(message.Line1))
        {
            SendToPlayer(player.Name, message.Line1.Trim(), reprompt: true);
        }
    }

    private void ProcessMonsterKnockdownTick(MonsterInstance monster)
    {
        // A dead monster never gets back up (its instance can linger — a Room.NPC primary waits in the
        // list for revival, and a fresh kill is only removed at the end of the death sequence).
        if (monster.IsDead || !monster.IsKnockedDown)
            return;

        KnockdownKind kind = monster.KnockdownKind;
        if (!monster.TickKnockdown())
            return;

        if (kind == KnockdownKind.Smash)
        {
            BroadcastToRoom(monster.MapNumber, monster.RoomNumber,
                $"{MudAnsi.BrightWhite}Slightly dazed the {monster.DisplayName} rises from the floor.{MudAnsi.Reset}",
                reprompt: true);
        }
    }

    // A monster that dies from poison upkeep (no direct
    // killer in the combat loop) still drops its carried treasure to the room, announces its death, and
    // is removed/regen-scheduled. No XP is awarded — the upkeep tick has no killer to credit. Reuses the
    // same death-reward roll as a combat kill so loot is identical.
    private void KillMonsterFromUpkeep(MonsterInstance monster)
    {
        if (!TryBeginMonsterDeathProcessing(monster))
            return;

        int map = monster.MapNumber;
        int room = monster.RoomNumber;
        var death = mmudreborn.Game.Combat.CombatEngine.CreateMonsterDeathResult(monster);

        if (death.RunicDropped > 0 || death.PlatinumDropped > 0 || death.GoldDropped > 0 ||
            death.SilverDropped > 0 || death.CopperDropped > 0)
        {
            DropCurrencyInRoom(map, room, death.RunicDropped, death.PlatinumDropped,
                death.GoldDropped, death.SilverDropped, death.CopperDropped);
        }

        // The monster kill hands every carried item to the room-DISPOSAL path, not a plain
        // room add — so a boss's hoard spills into adjacent rooms when the floor beneath it is
        // full rather than evaporating. This matters exactly where it hurts most: a party wipes on an
        // Adult Red Dragon, their gear fills the lair, the survivor lands the kill, and the drop has
        // nowhere to go.
        foreach (var dropId in death.Drops)
        {
            if (Database.Items.ContainsKey(dropId))
                DisposeOfItemInRoom(map, room, dropId, uses: monster.Template.GetDropUses(dropId));
        }

        if (!string.IsNullOrEmpty(death.DeathMessage))
            BroadcastToRoom(map, room, death.DeathMessage, reprompt: true);

        RemoveDeadMonster(monster);
    }

    // `killer` names what to record in the non-stock death log. These deaths have no attacker to blame —
    // poison, bleeding out, a room spell — so the cause is passed in rather than read off
    // Player.LastDamageSourceName (which would credit whoever last hit you, possibly minutes earlier).
    private void ProcessWorldTickDeath(Player player, string? deathMessage, string? killer = null)
    {
        int deathMap = player.CurrentMapNumber;
        int deathRoom = player.CurrentRoomNumber;
        RecordPlayerDeath(player, deathMap, deathRoom, killer);

        StopDraggingForPlayer(player);
        CleanupPartyForDeath(player);

        if (!string.IsNullOrWhiteSpace(deathMessage))
            SendToPlayer(player.Name, deathMessage);

        // Arena/collision branch: a death in a type-5 arena room while
        // arena combat-mode is OFF ("death does not count") is a no-op recall — no life lost, no loot
        // dropped, buffs/poison left intact. (The on-revive ability-155 text block runs only from the
        // command-driven death path, ExecuteForcedDeath, where a per-player command parser exists.)
        if (IsArenaDeathExempt(deathMap, deathRoom))
        {
            ReviveInArena(player, deathMap, deathRoom);
            return;
        }

        bool isArenaRoom = IsArenaRoom(deathMap, deathRoom);

        // Death step 1: terminate every active spell/buff and clear poison on death.
        bool hadBuffs = player.ActiveSpells.Count > 0;
        player.ActiveSpells.Clear();
        player.PoisonLevel = 0;
        if (hadBuffs)
            RecalculatePlayerStats(player);

        // A real death dismisses
        // summoned pets (charmed creatures revert to wild). Not run on an arena no-loss death.
        DismissPlayerPets(player.Name);

        // Lives -= 1, then lives < 1 ⇒ permadeath; on the last life everything drops regardless
        // of the keep-on-death abilities (100/83), so decide before the drop loop below.
        if (player.Lives > 0)
            player.Lives--;
        bool isPermadeath = player.Lives < 1;

        // Death imposes NO experience penalty — the stock death cost is a life
        // + dropped loot only. So we deliberately do NOT dock experience here either.

        // Stock skips the entire corpse-loot drop block for a type-5 arena room (even when arena
        // combat-mode is ON and the death counts): your gear stays with you, only a life is lost.
        if (!isArenaRoom)
        {
            var retainedInventoryIds = new List<int>();
            var retainedInventoryInstanceIds = new List<long>();
            var returnedNames = new List<string>();
            for (int index = 0; index < player.Inventory.Count; index++)
            {
                int itemId = player.Inventory[index];
                long instanceId = index < player.InventoryInstanceIds.Count
                    ? player.InventoryInstanceIds[index]
                    : CreateItemInstance(itemId);
                Database.Items.TryGetValue(itemId, out var item);
                if (!isPermadeath && item != null
                    && (item.Abilities.ContainsKey(100) || item.Abilities.ContainsKey(83)))
                {
                    retainedInventoryIds.Add(itemId);
                    retainedInventoryInstanceIds.Add(instanceId);
                    continue;
                }
                // DestroyOnDeath items vanish to their rightful place
                // (a private message) rather than dropping to the floor.
                if (item != null && item.DestroyOnDeath)
                {
                    returnedNames.Add(item.Name);
                    continue;
                }
                // Drop chain: death room (recursive spill into adjacent rooms) ->
                // the victim's 20-room movement trail -> the overflow room. Loot only "returns to its
                // rightful place" when every tier is full.
                if (!DisposeOfCorpseItem(player, deathMap, deathRoom, itemId, instanceId) && item != null)
                    returnedNames.Add(item.Name);
            }
            player.Inventory.Clear();
            player.InventoryInstanceIds.Clear();
            player.Inventory.AddRange(retainedInventoryIds);
            player.InventoryInstanceIds.AddRange(retainedInventoryInstanceIds);

            foreach (var (slot, itemId) in player.Equipment)
            {
                long instanceId = player.EquipmentInstanceIds.TryGetValue(slot, out var existingInstanceId)
                    ? existingInstanceId
                    : CreateItemInstance(itemId);
                Database.Items.TryGetValue(itemId, out var item);
                if (!isPermadeath && item != null
                    && (item.Abilities.ContainsKey(100) || item.Abilities.ContainsKey(83)))
                {
                    player.Inventory.Add(itemId);
                    player.InventoryInstanceIds.Add(instanceId);
                    continue;
                }
                if (item != null && item.DestroyOnDeath)
                {
                    returnedNames.Add(item.Name);
                    continue;
                }
                // Drop chain: death room (recursive spill into adjacent rooms) ->
                // the victim's 20-room movement trail -> the overflow room. Loot only "returns to its
                // rightful place" when every tier is full.
                if (!DisposeOfCorpseItem(player, deathMap, deathRoom, itemId, instanceId) && item != null)
                    returnedNames.Add(item.Name);
            }
            player.Equipment.Clear();
            player.EquipmentInstanceIds.Clear();

            foreach (var returnedName in returnedNames)
                SendToPlayer(player.Name, $"Your {returnedName} has returned to its rightful place.");

            long totalCopper = CurrencyHelper.ToCopper(player);
            if (totalCopper > 0)
            {
                DropCurrencyInRoom(deathMap, deathRoom, player.Runic, player.Platinum, player.Gold, player.Silver, player.Copper);
                player.Runic = 0;
                player.Platinum = 0;
                player.Gold = 0;
                player.Silver = 0;
                player.Copper = 0;
            }
        }

        // Lives < 1 ⇒ permadeath. The character is deleted (corpse loot stays in the death room),
        // removed from any gang and the online world, and the bleeding-out player is disconnected.
        if (isPermadeath)
        {
            string deadName = player.Name;
            SendToPlayer(deadName, $"{MudAnsi.BrightRed}You have no lives remaining!{MudAnsi.Reset}");
            SendToPlayer(deadName, $"{MudAnsi.BrightRed}Your adventure has come to an end -- your character is gone forever.{MudAnsi.Reset}");

            // Record the fallen character in the Hall of Fame before deletion.
            PlayerRepo.SaveHallOfFameEntry(new Data.Models.HallOfFameEntry
            {
                PlayerName = player.Name,
                LastName = player.LastName ?? string.Empty,
                Level = player.Level,
                RaceId = player.RaceId,
                ClassId = player.ClassId,
                Experience = player.Experience,
                Alignment = (int)Math.Round(player.EvilPoints),
            });

            // A gang leader's permadeath dissolves the gang entirely; a member's just drops them out of it.
            if (DisbandGangIfLeader(player) == null && !string.IsNullOrWhiteSpace(player.Gang))
                PlayerRepo.RemovePlayerFromGang(deadName, player.Gang);

            var deadClient = GetClientForPlayer(deadName);
            PlayerRepo.DeletePlayer(deadName);
            RemovePlayer(player, save: false);
            if (deadClient != null)
                deadClient.Player = null;

            BroadcastToRealm($"{MudAnsi.BrightRed}{deadName} has perished, never to return to the Realm.{MudAnsi.Reset}", reprompt: true);
            DisconnectOnlineCharacter(deadName, "Your character has died permanently.");
            return;
        }

        SendToPlayer(player.Name, "But, due to a miracle, you have been saved.");
        SendToPlayer(player.Name, $"You have {player.Lives} lives left.");

        var (respawnMap, respawnRoom) = ResolveDeathRespawn(player, deathMap, deathRoom);

        player.CurrentMapNumber = respawnMap;
        player.CurrentRoomNumber = respawnRoom;
        player.CurrentHP = player.MaxHP;
        player.CurrentMana = player.MaxMana;
        player.IsAided = false;
        player.ClearKnockdown();
        player.ClearCombatState();
        player.IsResting = false;
        player.IsMeditating = false;

        NotifyPlayerEnteredRoom(player);
        PlayerRepo.SavePlayer(player);
    }

    // Arena/collision branch: restore HP/mana, recall to the
    // death-respawn room, and announce with the colliseum message. No life lost, no loot dropped.
    private void ReviveInArena(Player player, int deathMap, int deathRoom)
    {
        var (respawnMap, respawnRoom) = ResolveDeathRespawn(player, deathMap, deathRoom);

        player.CurrentMapNumber = respawnMap;
        player.CurrentRoomNumber = respawnRoom;
        player.CurrentHP = player.MaxHP;
        player.CurrentMana = player.MaxMana;
        player.IsAided = false;
        player.ClearKnockdown();
        player.ClearCombatState();
        player.IsResting = false;
        player.IsMeditating = false;

        SendToPlayer(player.Name, "But, because you were in a colliseum, you have been saved.");
        SendToPlayer(player.Name, $"You have {player.Lives} lives left.");

        NotifyPlayerEnteredRoom(player);
        PlayerRepo.SavePlayer(player);
    }

    // The room cast runs from the medium character update every other medium tick (a
    // global flag toggles each call), so every room with a Spell pulses at the
    // same world-wide cadence — there is NO per-room interval. The earlier code mis-read
    // Room.Delay as seconds, but Delay is the lair-respawn minutes field (the only other
    // call site, GameWorld.Monsters.GetRoomSpawnDelay, correctly treats it as minutes).
    // Standard MajorBBS medium tick is ~5s, so room spells fire ~every 10s globally; that's
    // the empirical "feel" Silver River was hand-tuned to match too (its 4s override is gone
    // now that the per-room mismatch is removed).
    private static TimeSpan GetRoomSpellPulseInterval(Room room) => RoomSpellPulseInterval;

    private static DateTime GetNextRoomSpellPulseAtUtc(DateTime currentPulseAtUtc, TimeSpan pulseInterval, DateTime now)
    {
        var nextPulseAtUtc = currentPulseAtUtc;
        do
        {
            nextPulseAtUtc = nextPulseAtUtc.Add(pulseInterval);
        }
        while (nextPulseAtUtc <= now);

        return nextPulseAtUtc;
    }

    private static bool ShouldUseSilverRiverPrimaryColor(int pulseCount)
    {
        return pulseCount == 1 || (pulseCount - 1) % 5 == 0;
    }

    private static string FormatRoomSpellMessage(string text, string playerName)
    {
        return text.Replace("%s", playerName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ApplyRoomSpellMessageColor(string text, string? lineColor)
    {
        return string.IsNullOrWhiteSpace(lineColor)
            ? text
            : $"{lineColor}{text}{MudAnsi.Reset}";
    }

    private DateTime GetCurrentRoomSpellTimeUtc()
    {
        if (_useManualRoomSpellTicksForTests)
        {
            _roomSpellTestNowUtc ??= DateTime.UtcNow;
            return _roomSpellTestNowUtc.Value;
        }

        return DateTime.UtcNow;
    }

    // "Starting Cleanup" / "Cleanup Complete": daily maintenance.
    // Recharges items with ability 121, sweeps Del@Maint (ability 119) ground items.
    private const int RechargeAtCleanupAbilityId = 121;
    private const int DeleteAtCleanupAbilityId = 119;

    public void RunDailyCleanup(DateTime? nowOverride = null)
    {
        var now = nowOverride ?? DateTime.UtcNow;
        _nextCleanupAtUtc = CalculateNextCleanupUtc(DailyCleanupTime);
        _lastCleanupAtUtc = now;
        PlayerRepo.SetServerSettingText("LastCleanupUtc", now.ToString("o"));

        Console.WriteLine($"Starting daily cleanup at {now:yyyy-MM-dd HH:mm:ss} UTC");
        foreach (var player in GetAllOnlinePlayers())
            SendToPlayer(player.Name, $"{MudAnsi.BrightYellow}Daily maintenance is starting...{MudAnsi.Reset}");

        int recharged = RechargeOnlinePlayerItems();
        var (deletedGround, hiddenGround) = ProcessCleanupGroundItems();
        // AFTER the Del@Maint sweep, so a placed item the sweep just deleted is put straight back — the
        // order a stock board got for free by running cleanup and the game re-init at the same nightly
        // event. See RestoreStaticPlacedGroundItems for the details (bug #228).
        int restoredPlaced = RestoreStaticPlacedGroundItems();
        ProcessGangHouseTax(now);

        Console.WriteLine($"Cleanup complete: {recharged} items recharged, {deletedGround} ground items deleted, {hiddenGround} hidden, {restoredPlaced} placed items restored.");
        foreach (var player in GetAllOnlinePlayers())
            SendToPlayer(player.Name, $"{MudAnsi.BrightYellow}Daily maintenance complete.{MudAnsi.Reset}");
    }

    // Called at login: catch a character up on a cleanup that fired while they were offline. The stamp
    // is what makes this a CATCH-UP and not a refill — it ran on every single login before, so a spent
    // ability-121 item (the black flail #349, ten casts) came back full every time its owner
    // reconnected, which made its charges effectively unlimited (bug #218).
    public void RechargePlayerItemsIfNeeded(Player player)
    {
        if (_lastCleanupAtUtc == DateTime.MinValue)
            return;

        if (player.LastCleanupAppliedUtc >= _lastCleanupAtUtc)
            return;

        RechargePlayerItemCharges(player);
        player.LastCleanupAppliedUtc = _lastCleanupAtUtc;
    }

    private int RechargeOnlinePlayerItems()
    {
        int recharged = 0;

        foreach (var player in GetAllOnlinePlayers())
        {
            recharged += RechargePlayerItemCharges(player);
            // Stamp the players the cleanup reached directly, so their next login doesn't recharge
            // them a second time for the same cleanup.
            player.LastCleanupAppliedUtc = _lastCleanupAtUtc;
            PlayerRepo.SavePlayer(player);
        }

        return recharged;
    }

    private int RechargePlayerItemCharges(Player player)
    {
        int recharged = 0;
        EnsurePlayerItemInstanceAlignment(player);

        for (int i = 0; i < player.Inventory.Count; i++)
        {
            if (!Database.Items.TryGetValue(player.Inventory[i], out var item))
                continue;
            if (!item.Abilities.ContainsKey(RechargeAtCleanupAbilityId))
                continue;
            if (item.UseCount <= 0)
                continue;

            long instanceId = player.InventoryInstanceIds[i];
            if (_itemRuntimeStates.TryGetValue(instanceId, out var state) && state.RemainingCharges.HasValue)
            {
                state.RemainingCharges = item.UseCount;
                recharged++;
            }
        }

        foreach (var (slot, itemId) in player.Equipment)
        {
            if (!Database.Items.TryGetValue(itemId, out var item))
                continue;
            if (!item.Abilities.ContainsKey(RechargeAtCleanupAbilityId))
                continue;
            if (item.UseCount <= 0)
                continue;

            if (player.EquipmentInstanceIds.TryGetValue(slot, out long instanceId)
                && _itemRuntimeStates.TryGetValue(instanceId, out var state)
                && state.RemainingCharges.HasValue)
            {
                state.RemainingCharges = item.UseCount;
                recharged++;
            }
        }

        return recharged;
    }

    // Nightly maintenance ground pass: dropped loot (player drops, un-looted monster death-drops) is
    // auto-HIDDEN — it stays on the ground but is found via `search` rather than left in plain sight —
    // while Del@Maint items (ability 119) are deleted. Static/placed game items (IsStaticSeeded —
    // quest weapons, signs, etc.) and a room's static placed currency are part of the game world and
    // are NEVER hidden by maintenance. Gang-house rooms are skipped entirely (no hide, no delete).
    // Returns (deleted, hidden-events) counts for the cleanup log.
    private (int Deleted, int Hidden) ProcessCleanupGroundItems()
    {
        int deleted = 0;
        int hidden = 0;

        lock (_groundItemLock)
        {
            var roomKeys = _roomGroundItems.Keys.ToList();
            foreach (var key in roomKeys)
            {
                var room = GetRoom(key.Map, key.Room);
                if (room != null && room.IsGangHouse)
                    continue;

                if (!_roomGroundItems.TryGetValue(key, out var items))
                    continue;

                for (int index = items.Count - 1; index >= 0; index--)
                {
                    GroundItemEntry entry = items[index];
                    bool delAtMaint = Database.Items.TryGetValue(entry.ItemId, out var item)
                        && item.Abilities.ContainsKey(DeleteAtCleanupAbilityId);
                    if (delAtMaint)
                    {
                        items.RemoveAt(index);
                        deleted++;
                        continue;
                    }

                    // Auto-hide visible, non-static dropped loot only (init-only entry → replace).
                    // Static/placed game items stay visible like always.
                    if (!entry.IsHidden && !entry.IsStaticSeeded)
                    {
                        items[index] = new GroundItemEntry
                        {
                            ItemId = entry.ItemId,
                            IsHidden = true,
                            InstanceId = entry.InstanceId,
                            IsStaticSeeded = false,
                        };
                        hidden++;
                    }
                }

                if (items.Count == 0)
                    _roomGroundItems.TryRemove(key, out _);
            }
        }

        // Hide dropped ground currency, but keep a room's static placed currency (room.GroundCurrency)
        // visible — it is part of the game world. Hide only the copper above that placement amount.
        foreach (var key in _roomGroundCurrency.Keys.ToList())
        {
            var room = GetRoom(key.Map, key.Room);
            if (room != null && room.IsGangHouse)
                continue;

            long staticCopper = room == null ? 0 : Math.Max(0, room.GroundCurrency);

            _roomGroundCurrency.AddOrUpdate(
                key,
                _ => (GroundCurrencyStacks.Empty, GroundCurrencyStacks.Empty),
                (_, current) =>
                {
                    long visibleCopper = current.Visible.TotalCopper;
                    if (visibleCopper <= staticCopper)
                        return current; // all visible is (at most) the static placement — leave it

                    long droppedCopper = visibleCopper - staticCopper;
                    hidden++;
                    return (
                        GroundCurrencyStacks.FromCopperNormalized(staticCopper),
                        current.Hidden.Add(GroundCurrencyStacks.FromCopperNormalized(droppedCopper)));
                });
        }

        if (deleted > 0 || hidden > 0)
            PersistRoomGroundState();

        return (deleted, hidden);
    }
}