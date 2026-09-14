using mmudreborn.Data;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace mmudreborn.Game.Combat;

public class CombatEngine
{
    public const int MAX_SWINGS = 6;
    // Departing free swing:
    // evil NPCs (align 6) do not get the departing free swing against players whose EvilPoints
    // are >= 80. This is the player's EVIL POINTS, not their level —
    // the prior `playerLevel < 80` reading was a misderivation.
    public const int EvilNpcFreeAttackEvilPointsCap = 80;
    // Proactive aggression scan: an align-6 ("Evil NPC") monster
    // EXCLUDES a player from its aggro only when EvilPoints > 39 and they aren't already
    // fighting it. So it aggros Saint/Good/Neutral/Seedy (EP < 40) and spares Outlaw and worse (the
    // "evil" side). NOTE this is a TIGHTER cut than the departing free-attack cap (80) above — by
    // design an Outlaw (EP 40-79) draws a swing on the way OUT but is never aggroed while just standing.
    public const int EvilNpcAggroEvilPointsCap = 40;
    // Monster attack-slot AtkType: 1 = melee, 2 = cast the AtkAcc spell, 3 = rob.
    private const int MonsterMeleeAttackType = 1;
    private const int MonsterCastAttackType = 2;
    private const int MonsterRobAttackType = 3;
    private const int StockWeaponlessAttackMin = 1;
    private const int StockWeaponlessAttackMax = 4;
    // A NORMAL
    // attack with nothing wielded still computes energy use — with a bare-fist
    // "weapon speed" of 1200 and NO strength requirement — and the resulting EU drives the same
    // per-round swing budget as an equipped weapon. Weaponless is not a one-swing special case: the
    // attack loops gate on a 6-swing cap regardless
    // of attack type, so bare fists scale from 1 up to 6 swings/round with level, agility and
    // encumbrance exactly like a weapon does. (Stock swaps in 1800 for one state;
    // that state isn't modelled here, same as the mystic constants in GetUnarmedSwingPreview.)
    public const int StockWeaponlessSpeed = 1200;
    // Smash (weapon type 7) knockdown — both constants are FIXED values baked into the stock data
    // section (15 and 35); they are NOT
    // sysop-configurable (no numopt) and the smash sets them inline — it does NOT cast a spell.
    // Daze counter seeded to 15 on knockdown; each round/tick decrements it, recovery at 0.
    internal const int BuiltInSmashKnockdownTicks = 15;
    // While a combatant carries the smash-knockdown flag, the fighter marshal
    // subtracts this from BOTH their AV and their DV every round (the "defenseless" re-marshal). Applies
    // ONLY to the smash flag — NOT to the HoldPerson root. Symmetric: a knocked-down
    // attacker hits worse AND is hit easier.
    internal const int SmashDefenselessAvDvPenalty = 35;
    internal const int WeaponSpellProcChanceAbilityId = 114;
    internal const int WeaponSpellProcSpellAbilityId = 43;
    // Ability 116 "Alter Backstab Attack Value": PRESENCE makes a weapon backstab-able
    // (checked by BACKSTAB; daggers/short swords AND longswords/broadswords carry
    // it, the latter with a negative value; two-handers/poles/staves lack it). Its VALUE feeds the
    // backstab AV as value/2 via ApplyAbility case 116 → BSAccuracy (NOT applied in the attack profile).
    internal const int BackstabAttackValueAbilityId = 116;
    private const int MonsterAccuracyBlessAbilityId = 22;
    private const int MonsterAccuracyCurseAbilityId = 105;
    private const int MonsterAccuracyThirdAbilityId = 106;
    private const int MonsterAttackDamageAbilityId = 4;
    private const int MonsterDamageResistAbilityId = 7;
    // Ability 185 — a monster with this ability is pacified by carrying the item whose id is the
    // ability's value. See MonsterCouldAttack.
    public const int MonsterPacifierItemAbilityId = 185;

    public static bool CanMonsterRetaliate(Monster monster)
        => monster.AvgDmg > 0
           || monster.MidSpells.Count > 0
           || monster.Attacks.Any(attack => attack.Percent > 0);

    /// <summary>
    /// Faithful port of the could-a-monster-attack check (read side). Returns true if any monster in
    /// the room could attack <paramref name="player"/>, i.e. hide/sneak/etc. are blocked. Gate A:
    /// returns false outright when the player is both sneaking and just-moved (snuck in undetected,
    /// sneak + just-moved). Monsters are scanned in slot order and the carried-pacifier case
    /// short-circuits to false, so order matters — matching stock.
    /// </summary>
    /// <remarks>
    /// Exemptions in stock that reference subsystems absent here: a charmed monster named after the
    /// player (name match) — no pets/charm in this server; a monster's "inactive"
    /// flag — mapped to <see cref="MonsterInstance.IsDead"/>; and the keys array scanned
    /// alongside the inventory — this server folds keys into the inventory. Stock also keeps
    /// a ~100-tick aggro lock so a 185-monster you are FIGHTING keeps blocking hiding;
    /// that case is already covered by the in-combat gate on hide/sneak, so no lock is reproduced.
    /// </remarks>
    public static bool MonsterCouldAttack(Player player, IReadOnlyList<MonsterInstance> roomMonsters)
    {
        // Gate A — sneaking + moved-this-tick: snuck in undetected, nothing can attack you yet.
        if (player.IsSneaking && player.SneakedInThisTick)
            return false;

        foreach (var monster in roomMonsters)
        {
            if (monster.IsDead)
                continue; // empty slot / inactive

            // Charm exemptions: a monster the viewer OWNS (charm-named
            // plus an owner-name match) never counts as able to attack them, and a freshly
            // summoned/charmed monster (latch still set) hasn't oriented yet — neither blocks hiding.
            if (monster.IsCharmFresh || monster.IsOwnedBy(player.Name))
                continue;

            int pacifier = monster.GetEffectiveAbility(MonsterPacifierItemAbilityId);
            if (pacifier == 0)
                return true; // ordinary monster (no ability 185) → could attack

            // Ability-185 monster: pacified by carrying the item whose id is the ability value.
            if (player.Inventory.Contains(pacifier))
                return false; // carrying its pacifier item → safe (short-circuits the scan)

            // Otherwise this monster ignores you for hide purposes; keep scanning the next slot.
        }

        return false;
    }

    private static readonly Random _rng = new();

    // Player alignment based on Evil Points (EP)
    // Returns 0-7 based on EP thresholds
    //   EP < -200 => Saint (7)  - deep good
    //   EP < -50  => Good (6)
    //   EP < 30   => Neutral (0)
    //   EP < 40   => Seedy (1)
    //   EP < 80   => Outlaw (2)  - guards attack, knocked out
    //   EP < 120  => Criminal (3) - guards attack, killed
    //   EP < 210  => Villain (4)
    //   EP >= 210 => FIEND (5)
    public enum PlayerAlignment { Neutral = 0, Seedy = 1, Outlaw = 2, Criminal = 3, Villain = 4, FIEND = 5, Good = 6, Saint = 7 }

    public static PlayerAlignment GetPlayerAlignment(float evilPoints)
    {
        // A chain of signed comparisons
        if (evilPoints < -200) return PlayerAlignment.Saint;
        if (evilPoints < -50) return PlayerAlignment.Good;
        if (evilPoints < 30) return PlayerAlignment.Neutral;
        if (evilPoints < 40) return PlayerAlignment.Seedy;
        if (evilPoints < 80) return PlayerAlignment.Outlaw;
        if (evilPoints < 120) return PlayerAlignment.Criminal;
        if (evilPoints < 210) return PlayerAlignment.Villain;
        return PlayerAlignment.FIEND;
    }

    internal static (int WeaponMin, int WeaponMax, int Accuracy, int WeaponType) GetPlainWeaponAttackProfile(Player attacker, CharacterClass cls, Item? weapon)
    {
        if (weapon == null)
        {
            // The no-weapon type-5 path sets only EU and
            // verbs; Dmin/Dmax stay at the prologue defaults (1 and 4).
            // But AV is computed unconditionally from level/class/stats —
            // @24650-24658 — it does NOT stay at zero. Previously we returned acc=0 here, which
            // pinned weaponless hit chance to the 10 floor; stock fires weaponless attacks with
            // the full stat-derived AV (minus weapon.Accy, which doesn't exist for fists).
            var (weaponlessMin, weaponlessMax) = ApplyStrengthDamageBonus(StockWeaponlessAttackMin, StockWeaponlessAttackMax, attacker.Strength);
            // The max-damage ability (4) is added to max, accumulated across race /
            // class / equipped items / active spells). Applies to weaponless and weapon paths alike.
            weaponlessMax += attacker.MaxDamageAbility;
            return (weaponlessMin, weaponlessMax, attacker.GetBaseAccuracy(cls.CombatLvl), -1);
        }

        var (min, max) = ApplyStrengthDamageBonus(weapon.Min, weapon.Max, attacker.Strength);
        max += attacker.MaxDamageAbility;
        return (min, max, attacker.GetBaseAccuracy(cls.CombatLvl) + weapon.Accy, weapon.WeaponType);
    }

    /// <summary>
    /// The strength damage bonus:
    /// after the attack-type branch sets the weapon/unarmed min/max, the unconditional Strength
    /// adjustment runs for every fighter — both equipped weapons and bare-fist swings:
    ///   max += (Strength - 50) / 10
    ///   if ((Strength - 100) / 10 * 2 > 0): min += ((Strength - 100) / 10) * 2
    ///   if (min > max) min = max
    ///   clamp(min, max, >= 0)
    /// The max adjustment can be negative (weak fighters lose damage); the min adjustment cannot
    /// (it's gated on the doubled result being positive, so weak fighters keep their floor).
    /// </summary>
    public static (int Min, int Max) ApplyStrengthDamageBonus(int baseMin, int baseMax, int strength)
    {
        int max = baseMax + (strength - 50) / 10;
        int min = baseMin + Math.Max(0, (strength - 100) / 10 * 2);
        if (min > max) min = max;
        if (min < 0) min = 0;
        if (max < 0) max = 0;
        return (min, max);
    }

    internal static (int WeaponMin, int WeaponMax, int Accuracy, int WeaponType) GetBackstabAttackProfile(Player attacker, Item? weapon)
    {
        int weaponMin = weapon?.Min ?? attacker.GetWeaponMin();
        int weaponMax = weapon?.Max ?? attacker.GetWeaponMax();
        var (backstabMin, backstabMax) = GetStockBackstabDamageBounds(attacker, weaponMin, Math.Max(weaponMin, weaponMax));

        // The weapon's ability-116 ("Alter Backstab Attack Value") AV modifier is already folded into the
        // attacker's BSAccuracy via ApplyAbility case 116 (value/2) during RecalculateStats, and
        // GetBackstabAccuracy() reads BSAccuracy — so do NOT add it again here (it would double-count).
        return (backstabMin, backstabMax, attacker.GetBackstabAccuracy(), weapon?.WeaponType ?? -1);
    }

    // A backstab deals full backstab DAMAGE only with a backstab-capable
    // weapon — unarmed, or an EQUIPPED weapon carrying ability 116. An equipped weapon
    // WITHOUT it still surprises (backstab accuracy + reduced-AC reliable hit +
    // first-strike) but the swing is e4=5 → NORMAL weapon damage, no multiplier, no "surprise" verb.
    internal static bool WeaponEnablesBackstabDamage(Item? weapon)
        => weapon == null || weapon.Abilities.ContainsKey(BackstabAttackValueAbilityId);

    // The backstab swing profile. A FULL backstab (backstab damage + "surprise" verb + reliable backstab
    // accuracy + no crit) applies when the weapon enables it (backstab-capable or unarmed) OR, for a
    // non-backstab weapon, when the board has SURPRISEROUND ON (surpriseRoundEnabled) — in which case the
    // non-backstab-weapon sneak gets the full backstab-damage surprise round. With SURPRISEROUND OFF the
    // backstab command never engages a non-backstab weapon as a Backstab action (the dispatcher routes it
    // to a plain normal attack), so the `false` branch below is the defensive fallback only.
    internal static (int WeaponMin, int WeaponMax, int Accuracy, int WeaponType, bool FullBackstab) GetBackstabSwingProfile(Player attacker, Item? weapon, bool surpriseRoundEnabled)
    {
        if (WeaponEnablesBackstabDamage(weapon) || surpriseRoundEnabled)
        {
            var (min, max, acc, type) = GetBackstabAttackProfile(attacker, weapon);
            return (min, max, acc, type, true);
        }

        // Fallback: NORMAL weapon damage at backstab accuracy (only if a non-backstab weapon somehow
        // reaches the Backstab swing with SURPRISEROUND OFF; the dispatcher normally prevents this).
        var (normalMin, normalMax) = ApplyStrengthDamageBonus(weapon!.Min, weapon.Max, attacker.Strength);
        return (normalMin, normalMax, attacker.GetBackstabAccuracy(), weapon.WeaponType, false);
    }

    internal static (int Min, int Max) GetStockBackstabDamageBounds(Player attacker, int weaponMin, int weaponMax)
    {
        int levelAndStealthBonus = (attacker.Level * 2) + (attacker.Stealth / 10);
        long minDamage = (weaponMin * 2L) + levelAndStealthBonus + attacker.BSMinDamage;
        long maxDamage = (Math.Max(weaponMin, weaponMax) * 2L) + levelAndStealthBonus + attacker.BSMaxDamage;

        if (attacker.HasRaceStealth && !attacker.HasClassStealth)
        {
            minDamage = minDamage * 75 / 100;
            maxDamage = maxDamage * 75 / 100;
        }

        minDamage = minDamage * (attacker.Level + 100L) / 100;
        maxDamage = maxDamage * (attacker.Level + 100L) / 100;

        return ((int)Math.Clamp(minDamage, 0, int.MaxValue), (int)Math.Clamp(maxDamage, 0, int.MaxValue));
    }

    // Non-consuming EU/swing preview for a normal (non-mystic) attack. A wielded weapon uses its own
    // Speed/StrReq; bare fists use the stock weaponless constants (speed 1200, no strength requirement).
    // See StockWeaponlessSpeed.
    public static WeaponSwingPreview GetWeaponSwingPreview(Player attacker, CharacterClass cls, Item? weapon, bool isBashing = false, IGameDatabase? db = null)
    {
        int speedModifierPercent = GetWeaponSpeedModifierPercent(attacker, db);
        if (weapon != null)
            return WeaponSwingPreviewCalculator.Calculate(attacker, cls, weapon, speedModifierPercent, isBashing);

        int encumbrancePercent = WeaponSwingPreviewCalculator.CalculateEncumbrancePercent(attacker.Encumbrance, attacker.MaxEncumbrance);
        return WeaponSwingPreviewCalculator.Calculate(
            cls.CombatLvl, attacker.Level, StockWeaponlessSpeed, attacker.Agility, attacker.Strength,
            encumbrancePercent, itemStrengthRequirement: 0, speedModifierPercent, isBashing,
            attacker.GetEffectiveMaxStamina());
    }

    // The Quick & Deadly bump is computed AFTER the attack-type branch, gated only on
    // EU<200 && encum<67 && the strength-requirement penalty not having fired — never on "is a weapon
    // wielded". Bare fists at speed 1200 drop under the 200 threshold at high level/agility and earn
    // the bonus just like a fast weapon.
    public static int GetWeaponQuickAndDeadlyBonus(Player attacker, CharacterClass cls, Item? weapon, bool isBashing = false, IGameDatabase? db = null)
    {
        return GetWeaponSwingPreview(attacker, cls, weapon, isBashing, db).QuickAndDeadlyBonus;
    }

    public static int GetWeaponCritChance(Player attacker, CharacterClass cls, Item? weapon, bool isBashing = false, IGameDatabase? db = null)
    {
        return attacker.GetCrits() + GetWeaponQuickAndDeadlyBonus(attacker, cls, weapon, isBashing, db);
    }

    // Q&D is computed for all attack types; the gate is
    // EU<200 && encum<67. Unarmed paths use martial-speed constants
    // (1150/1400/1900 in GetUnarmedSwingPreview) so a low-encumbrance Mystic punching at
    // mid-level gets the same crit bump stock grants. Weapon-style crit-disabled types
    // (Backstab/Bash/Smash) ignore the chance value entirely (AttackTypeAllowsCrit @526), so
    // those callsites keep using GetCrits() directly — Q&D wouldn't change the outcome.
    public static int GetUnarmedCritChance(Player attacker, CharacterClass cls, string attackType, IGameDatabase? db = null)
    {
        return attacker.GetCrits() + GetUnarmedSwingPreview(attacker, cls, attackType, db).QuickAndDeadlyBonus;
    }

    public static int GetWeaponRoundSwings(Player attacker, CharacterClass cls, Item? weapon, bool isBashing = false, IGameDatabase? db = null)
    {
        var preview = GetWeaponSwingPreview(attacker, cls, weapon, isBashing, db);

        // A melee swing taken after the player has already acted (cast a
        // spell) this round costs +10% EU. The [200, maxStamina] clamp
        // still applies after the ×1.1.
        int energyUse = attacker.HasCastThisRound
            ? Math.Clamp(preview.RawEnergyUse * 110 / 100, WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse, attacker.GetEffectiveMaxStamina())
            : preview.EnergyUse;

        // Consume from the player's absolute stamina pool. The pool is regenerated once
        // per round by Player.PrepareCombatRound; this only spends it.
        var round = WeaponSwingPreviewCalculator.ConsumeSwingRound(energyUse, attacker.CurrentEnergy);
        attacker.CurrentEnergy = round.NextEnergyRemainder;
        return round.Swings;
    }

    // Unarmed paths (types 1/2/3): EU is computed with a martial
    // "weapon speed" constant per attack type and no
    // strength requirement, then the same enc/clamp post-processing and 6-swing loop. Mystic swings
    // therefore come from the shared stamina pool, not the legacy GetSwings(cap 10).
    // Non-consuming preview of an unarmed attack's EU/swings, used by the stats display. The live
    // path (GetUnarmedRoundSwings) reuses this then consumes the pool.
    public static WeaponSwingPreview GetUnarmedSwingPreview(Player attacker, CharacterClass cls, string attackType, IGameDatabase? db = null)
    {
        int martialSpeed = (attackType?.ToLowerInvariant()) switch
        {
            "kick" => 1400,
            "jumpkick" => 1900,
            _ => 1150,            // punch
        };

        int encumbrancePercent = WeaponSwingPreviewCalculator.CalculateEncumbrancePercent(attacker.Encumbrance, attacker.MaxEncumbrance);
        return WeaponSwingPreviewCalculator.Calculate(
            cls.CombatLvl, attacker.Level, martialSpeed, attacker.Agility, attacker.Strength,
            encumbrancePercent, itemStrengthRequirement: 0, GetWeaponSpeedModifierPercent(attacker, db), isBashing: false);
    }

    public static int GetUnarmedRoundSwings(Player attacker, CharacterClass cls, string attackType, IGameDatabase? db = null)
    {
        var preview = GetUnarmedSwingPreview(attacker, cls, attackType, db);

        int energyUse = attacker.HasCastThisRound
            ? Math.Clamp(preview.RawEnergyUse * 110 / 100, WeaponSwingPreviewCalculator.MinimumEffectiveEnergyUse, attacker.GetEffectiveMaxStamina())
            : preview.EnergyUse;

        var round = WeaponSwingPreviewCalculator.ConsumeSwingRound(energyUse, attacker.CurrentEnergy);
        attacker.CurrentEnergy = round.NextEnergyRemainder;
        return round.Swings;
    }

    private static int GetWeaponSpeedModifierPercent(Player attacker, IGameDatabase? db)
    {
        if (db == null)
            return 100;

        int speedModifierPercent = attacker.GetActiveAbilityValue(db, 87);
        return speedModifierPercent > 0 ? speedModifierPercent : 100;
    }

    // These read the monster's EFFECTIVE abilities (template intrinsics + active-spell buffs) via
    // GetEffectiveAbility, so any buff/debuff that confers accuracy/damage/DR — not just haste/slow —
    // affects combat.
    internal static int GetEffectiveMonsterAccuracy(MonsterInstance monster, int baseAccuracy)
    {
        return baseAccuracy
            + monster.GetEffectiveAbility(MonsterAccuracyBlessAbilityId)
            + monster.GetEffectiveAbility(MonsterAccuracyCurseAbilityId)
            + monster.GetEffectiveAbility(MonsterAccuracyThirdAbilityId);
    }

    internal static (int Min, int Max) GetEffectiveMonsterDamageBounds(MonsterInstance monster, int minDamage, int maxDamage)
    {
        int damageBonus = monster.GetEffectiveAbility(MonsterAttackDamageAbilityId);
        return (minDamage + damageBonus, maxDamage + damageBonus);
    }

    internal static int GetEffectiveMonsterDamageResist(MonsterInstance monster)
    {
        // The fighter DR field = templateDR * 10, THEN
        // += monster ability 7 (added raw, NOT scaled). The attack calc later subtracts DR/10, so the
        // template DR lands as its FULL whole-point value and ability 7 as ability7/10. We return the
        // value in tenths (×10 on the template only) so the shared ScaleDamageResist(/10) at the use site
        // reproduces that — matching the player path, whose GetTotalDR() is already stored in tenths.
        // (Was `templateDR + ability7` un-scaled, which the /10 then shrank to ~1/10 strength — ~868
        // monsters had their damage-resist effectively nullified. See combat-formula-audit-2026-06-23.)
        return (monster.Template.DamageResist * 10) + monster.GetEffectiveAbility(MonsterDamageResistAbilityId);
    }

    // Smash-knockdown score = encumbrance percent + Level +
    // Strength. That field is STRENGTH (PROVEN: the strength→damage bonus
    // reads it as `(X-50)/10` max / `((X-100)/10)*2` min — the canonical STR formula, identical
    // to ApplyStrengthDamageBonus). The prior
    // comment's "Willpower" reading was a misread. The smasher knocks the target down only if the smasher's
    // score strictly exceeds the target's.
    internal static int GetPlayerSmashKnockdownScore(Player player)
    {
        int encumbrancePercent = WeaponSwingPreviewCalculator.CalculateEncumbrancePercent(player.Encumbrance, player.MaxEncumbrance);
        return encumbrancePercent + player.Level + player.Strength;
    }

    // A combatant carrying the SMASH knockdown flag (KnockdownKind.Smash) is "defenseless":
    // the fighter marshal subtracts SmashDefenselessAvDvPenalty from both their AV and DV each round. The
    // HoldPerson root (KnockdownKind.Spell) does NOT incur this — only the smash flag.
    internal static bool IsSmashDefenseless(Player? p) => p is { KnockdownKind: KnockdownKind.Smash } && p.IsKnockedDown;
    internal static bool IsSmashDefenseless(MonsterInstance? m) => m is { KnockdownKind: KnockdownKind.Smash } && m.IsKnockedDown;

    private static bool TryApplyPlayerSmashKnockdown(Player attacker, Player target, CombatResult result)
    {
        if (GetPlayerSmashKnockdownScore(attacker) <= GetPlayerSmashKnockdownScore(target))
            return false;

        target.ApplyKnockdown(KnockdownKind.Smash, BuiltInSmashKnockdownTicks);
        result.Messages.Add(GameAnsi.CombatHit($"You smashed {target.Name} to the ground!"));
        result.TargetMessages.Add(GameAnsi.CombatHit("You are smashed to the ground!"));
        result.RoomMessages.Add(GameAnsi.CombatHit($"{target.Name} is smashed to the ground defenseless!"));
        return true;
    }

    // Smashing a MONSTER uses a different threshold from the player-vs-player one: neither side gets
    // an encumbrance term. The smasher scores Strength + Level; the monster resists with its magic
    // resistance plus a flat MonsterSmashKnockdownResistBonus, and goes down only if that resist
    // score is strictly lower. (A per-instance monster level also feeds the stock resist sum, but no
    // code path ever writes that field -- it is zero for every monster -- so it is not modelled.)
    internal const int MonsterSmashKnockdownResistBonus = 30;

    internal static int GetPlayerSmashKnockdownScoreVsMonster(Player player) => player.Strength + player.Level;

    internal static int GetMonsterSmashKnockdownResistScore(MonsterInstance monster)
        => monster.Template.MagicRes + MonsterSmashKnockdownResistBonus;

    // The smasher and the rest of the room get the SAME third-person line -- there is no
    // second-person "You smashed ..." variant on the monster side, unlike player-vs-player.
    private static bool TryApplyMonsterSmashKnockdown(Player attacker, MonsterInstance monster, CombatResult result)
    {
        if (GetMonsterSmashKnockdownResistScore(monster) >= GetPlayerSmashKnockdownScoreVsMonster(attacker))
            return false;

        monster.ApplyKnockdown(KnockdownKind.Smash, BuiltInSmashKnockdownTicks);
        string knockdownLine = GameAnsi.CombatHit($"The {monster.DisplayName} is smashed to the floor defenseless!");
        result.Messages.Add(knockdownLine);
        result.RoomMessages.Add(knockdownLine);
        return true;
    }

    // Roll a weapon's on-hit spell-proc (ability 114 chance gate + ability 43 spell id). Returns the proc
    // spell id when it triggers this swing, else 0. Consumes one RNG draw exactly as stock does at the
    // per-swing hit branch, so callers must invoke it at the same point (after the hit line, before
    // retaliation) to preserve the roll order.
    private static int RollWeaponSpellProc(Item? weapon)
    {
        if (weapon == null)
            return 0;

        int procChance = weapon.Abilities.GetValueOrDefault(WeaponSpellProcChanceAbilityId);
        int spellId = weapon.ProcSpellId > 0
            ? weapon.ProcSpellId
            : weapon.Abilities.GetValueOrDefault(WeaponSpellProcSpellAbilityId);
        if (procChance <= 0 || spellId <= 0)
            return 0;

        if (_rng.Next(0, 100) >= procChance)
            return 0;

        return spellId;
    }

    private static void TryQueueWeaponSpellProc(Item? weapon, CombatResult result)
    {
        int spellId = RollWeaponSpellProc(weapon);
        if (spellId <= 0)
            return;

        // Capture the message-list lengths at this swing boundary so the command layer can fire the
        // proc cast INTERLEAVED — right after this swing's hit line, before the next swing's — matching
        // the per-swing proc call rather than a batched after-the-round pass.
        result.TriggeredWeaponProcs.Add(new TriggeredWeaponProc(
            spellId,
            result.Messages.Count,
            result.RoomMessages.Count,
            result.TargetMessages.Count));
    }

    private const int WeaponLifeStealAbilityId = 8;   // weapon "Steal Hit Points" leech

    // A connecting weapon swing whose weapon
    // carries ability 8 heals the attacker by the damage dealt that swing, capped at max HP. Gated to
    // weapon attacks — the ability lives on the weapon, so unarmed (fists/kick/jumpkick) never qualifies.
    private static void ApplyWeaponLifeSteal(Player attacker, Item? weapon, int damageDealt)
    {
        if (damageDealt <= 0 || weapon == null || !weapon.Abilities.ContainsKey(WeaponLifeStealAbilityId))
            return;
        attacker.CurrentHP = Math.Min(attacker.MaxHP, attacker.CurrentHP + damageDealt);
    }

    /// <summary>Returns true if player is good-aligned (Good or Saint).</summary>
    public static bool IsGood(PlayerAlignment align) => align >= PlayerAlignment.Good;

    /// <summary>Returns true if player is evil-aligned (Seedy or worse).</summary>
    public static bool IsEvil(PlayerAlignment align) => align >= PlayerAlignment.Seedy && align <= PlayerAlignment.FIEND;

    /// <summary>
    /// The Good / Neutral / Evil "bucket" (1 / 0 / -1) that gates alignment-restricted equipment.
    /// Worn items are re-validated only when this band changes (on an evil-point change: if the
    /// alignment band differs, items the new band disallows are stripped).
    /// </summary>
    public static int GetAlignmentBucket(float evilPoints)
    {
        var align = GetPlayerAlignment(evilPoints);
        return IsGood(align) ? 1 : IsEvil(align) ? -1 : 0;
    }

    public static string GetAlignmentName(float evilPoints)
    {
        // Stock band names: Neutral, Seedy, Outlaw, Criminal, Villain, FIEND, Good, Saint
        return GetPlayerAlignment(evilPoints) switch
        {
            PlayerAlignment.Saint => "Saint",
            PlayerAlignment.Good => "Good",
            PlayerAlignment.Neutral => "Neutral",
            PlayerAlignment.Seedy => "Seedy",
            PlayerAlignment.Outlaw => "Outlaw",
            PlayerAlignment.Criminal => "Criminal",
            PlayerAlignment.Villain => "Villain",
            PlayerAlignment.FIEND => "FIEND",
            _ => "Neutral",
        };
    }

    // Monster Align values from database:
    //   0 = Passive NPC (townsman, townswoman) - never attacks, never aggros
    //   1 = Neutral aggressive (mercenary, brigand) - attacks everyone on sight
    //   2 = Evil hostile (rats, undead, kobolds) - attacks everyone on sight
    //   3 = Neutral peaceful (animals, drunks) - does not aggro, defends self
    //   4 = Lawful Good (guards, templars, shopkeepers) - attacks evil players only
    //   5 = Wild/aggressive (wild dogs, centaurs) - attacks everyone on sight
    //   6 = Evil NPC (dark monks, duergar) - attacks good/neutral players

    /// <summary>
    /// Eligibility for the one free swing a room monster gets
    /// as a non-sneaking player departs. The roll (genrdn 0..100) must not exceed the monster's
    /// aggression % — which is the monster's <c>FollowPercent</c> field (the same
    /// value that drives pursuit and most-recent-attacker re-target). The legacy <c>Active</c> field is
    /// an in-game/enabled flag (~0), NOT aggression; gating on it disabled the free-attack entirely.
    /// Alignment decides who may swing: townsfolk/peaceful/guards (0/3/4) only when already engaged,
    /// evil NPCs (6) never against players with EvilPoints >= <see cref="EvilNpcFreeAttackEvilPointsCap"/>
    /// everyone else always (engaged or not — a passing player provokes a fresh aggro swing).
    /// </summary>
    public static bool IsEligibleDepartingFreeAttacker(int monsterAlign, int monsterAggression, bool engaged, int playerEvilPoints, int roll, int monsterType = 0)
    {
        if (roll > monsterAggression)
            return false;

        // A type-37 (summoned "angel") monster is
        // folded into the engaged-only (passive) branch alongside align 0/3/4 — it only free-swings a
        // departing player it is already fighting, never as a fresh aggressor.
        if (monsterType == SummonAngelMonsterType)
            return engaged;

        return monsterAlign switch
        {
            0 or 3 or 4 => engaged,
            6 => playerEvilPoints < EvilNpcFreeAttackEvilPointsCap,
            _ => true,
        };
    }

    // Monster Type 37: a summoned "angel" (mirrors GameWorld.SummonedAngelMonsterType).
    private const int SummonAngelMonsterType = 37;

    /// <summary>
    /// When a player's swing engages a monster,
    /// the monster re-points its single locked target to that attacker on
    /// a roll of 1..99 &lt; FollowPercent — OR unconditionally when the monster is
    /// passive-aligned (0/3/4), which always grabs the most-recent attacker. This is the server mechanic
    /// behind the MegaMUD "attack last" tactic: each swing re-rolls to steal aggro, so the last player to
    /// swing a beat gets the last roll. A type-5 monster is the exception — it grabs only when currently
    /// untargeted (<paramref name="currentlyTargeted"/> false), and is never stolen off an existing lock.
    /// (The summoned/charmed-pet exclusions are applied by
    /// the caller, which holds the per-instance owner/summoned state.)
    /// </summary>
    public static bool ShouldRetargetToAttacker(int monsterAlign, int monsterType, int monsterFollowPercent, bool currentlyTargeted, int roll)
    {
        if (monsterType == 5 && currentlyTargeted)
            return false;

        bool passiveAlwaysRetargets = monsterAlign is 0 or 3 or 4;
        return passiveAlwaysRetargets || roll < monsterFollowPercent;
    }

    /// <summary>
    /// Unengaged cross-player target pick: a monster with
    /// no locked target iterates the candidate players in terminal order and grabs the first one whose
    /// <c>genrdn(0,100) &lt; 50 − 5×hitsThisTick</c> roll passes (each incoming swing a player already
    /// took this beat lowers their odds of being picked again — capping and spreading damage across a
    /// party). If none pass, the LAST eligible candidate is the fallback (the monster always swings at
    /// someone). Candidates that are not eligible (e.g. an evil NPC's high-level non-engaged player) are
    /// skipped entirely — no roll, no fallback. Returns the chosen index, or -1 if no candidate was
    /// eligible. <paramref name="rollZeroTo100"/> supplies one genrdn(0,100) per eligible candidate.
    /// </summary>
    public static int ChooseUnengagedAttackTarget(
        IReadOnlyList<int> hitsThisTick, IReadOnlyList<bool> eligibleForRoll, Func<int> rollZeroTo100)
    {
        int fallback = -1;
        for (int i = 0; i < hitsThisTick.Count; i++)
        {
            if (!eligibleForRoll[i])
                continue;

            fallback = i;   // last eligible candidate rolled this pass
            if (rollZeroTo100() < 50 - 5 * hitsThisTick[i])
                return i;   // first pass wins
        }

        return fallback;
    }

    public enum MonsterLockUpdate { Keep, Lock, Clear }

    /// <summary>
    /// After a monster swings at a player it rolls
    /// a roll of 1..99 &lt; FollowPercent to manage its single locked target:
    /// a pass <c>Lock</c>s onto that player (focus them next beat); a failed roll on an aggressive monster
    /// (align ∉ {0,3,4}) <c>Clear</c>s the lock so it re-spreads to another player next beat — this is the
    /// engine behind party damage-spreading. A passive-aligned monster that fails the roll keeps its
    /// current lock (<c>Keep</c>). A summoned creature (type 37) never changes its lock, and a type-5
    /// monster only acquires one when currently untargeted.
    /// </summary>
    public static MonsterLockUpdate ResolvePostAttackLock(
        int monsterAlign, int monsterType, int monsterFollowPercent, bool currentlyTargeted, int roll)
    {
        if (monsterType == 37)
            return MonsterLockUpdate.Keep;                 // summoned reinforcement: lock never managed here
        if (monsterType == 5 && currentlyTargeted)
            return MonsterLockUpdate.Keep;                 // type-5 only acquires a lock when untargeted

        if (roll < monsterFollowPercent)
            return MonsterLockUpdate.Lock;

        bool aggressive = monsterAlign is not (0 or 3 or 4);
        return aggressive ? MonsterLockUpdate.Clear : MonsterLockUpdate.Keep;
    }

    // Ability 185 "Do not Attack if Item Nmbr": a guardian carrying it (value = an emblem item id)
    // never attacks a player who holds that emblem — the gang-house "safe passage" mechanic.
    // The gate lives at the SWING (an ability-185 check), so it
    // covers EVERY monster->player attack — on-sight aggro, the departing free swing, AND combat rounds —
    // not just the aggro decision. Worn emblems count: stock scans the carried inventory and the
    // keys array; this server folds keys into the inventory, so we check inventory + equipment.
    private const int DoNotAttackIfItemAbilityId = 185;

    public static bool PlayerHoldsMonsterPacifier(Monster monster, Player player)
    {
        int safePassageItemId = monster.Abilities.GetValueOrDefault(DoNotAttackIfItemAbilityId);
        return safePassageItemId > 0 &&
            (player.Inventory.Contains(safePassageItemId) || player.Equipment.ContainsValue(safePassageItemId));
    }

    /// <summary>
    /// Determines if a monster should auto-aggro a player when they enter a room.
    /// </summary>
    // Ability 57 "See Hidden": a monster that carries it is not fooled by a sneaking/hidden
    // player. The monster's pursue/act loop skips a hidden or
    // sneaked-and-moved target UNLESS it carries ability 57 — so a See-Hidden
    // monster (e.g. a Ghost) keeps chasing and swinging at the sneaker. It does NOT clear the player's
    // stealth bit; other monsters still can't see them.
    public const int SeeHiddenAbilityId = 57;

    public static bool MonsterCanSeeHidden(Monster monster) => monster.Abilities.ContainsKey(SeeHiddenAbilityId);

    /// <summary>
    /// The stock "Guard" monster class — the ONE Group value the aggression scan special-cases.
    /// Sourced from the monster DAT's "Group" column (offset 84).
    /// </summary>
    private const int GuardMonsterGroup = 5;
    private const int EvilNpcAlign = 6;

    public static bool ShouldMonsterAggro(Monster monster, Player player)
    {
        if (PlayerHoldsMonsterPacifier(monster, player))
            return false;

        // The aggression scan runs every pulse from the background pass,
        // interleaved with the players' own swings. It branches on the monster's GROUP *before* it ever looks
        // at alignment:
        //
        //     if (Group != 5)                           // generic branch
        //         if (align != 4 && align != 0 && align != 3) { ...attack... }
        //     else                                      // Group == 5 exactly -> GUARD branch
        //         if (align == 6) attack when EP <  40
        //         else            attack when EP >  39
        //
        // Field mapping verified from the template->instance copy rather than from
        // symbol names: the instance Align field <- monster DAT offset 174, and the instance
        // Group field <- monster DAT offset 84.
        if (monster.Group == GuardMonsterGroup)
        {
            // The guard branch applies NO alignment filter, and that is the entire point of it: an
            // align-0 or align-3 Group-5 monster hunts criminals exactly like an align-4 one. It is
            // why the woodelf wardancer #290, woodelf guard #289, ranger #285 and druid #286 (all
            // align 0) must jump an Outlaw+ player just as the woodelf citizen #287 (align 4) does —
            // a woodelf guard that ignores criminals was the tell that this was wrong. Same for the
            // align-3 storm giants #637-639 and the align-0 Ghost of Justice Darkbane #849.
            //
            // Only align 6 inverts (hunts the GOOD side). No Group-5 monster in the stock DAT is
            // align 6 — Group 5 holds aligns 0, 3 and 4 only — so that arm is defensive.
            return monster.Align == EvilNpcAlign
                ? player.EvilPoints < EvilNpcAggroEvilPointsCap
                : player.EvilPoints >= EvilNpcAggroEvilPointsCap;
        }

        // Group 37 gets NO gate here. It is the summoned-monster pool, not a second guard
        // class: NO room in the game spawns MonsterType 37 (287 rooms spawn Group 5; zero spawn 37),
        // and every member has a conjuring spell -- Argak #609, Choira #610, Lallim #611, Sharh'Kur
        // #612, Zanthus #215, huorn #279, Kai Master #448 (spell 540), guardsman #13 (spell 888
        // "calls for aid"), guardsman #538. Summoned monsters are dismissed by bound
        // name when their owner turns Villain/FIEND, and the area cast has them forgive that owner's
        // friendly fire. A summoned one is always name-bound, and the aggression scan only considers
        // monsters with an EMPTY bound-target name -- so a Group-37 monster never reaches
        // this method at all. It attacks because the summon aimed it.
        //
        // This matters because the town guards ARE Group 5: Silvermere (map 1) spawns MonsterType 5 /
        // index 1-5 = guardsman #14, Sheriff Lionheart #40, elite guardsman #757, Templar #214, all
        // align 4 -> the guard branch above -> evil-aligned only. The world-placed Kai Master is #447
        // (align 3, Group 24, FollowPercent 0), which lands in the passive case below and never
        // initiates; #448 is only ever the summoned copy.
        switch (monster.Align)
        {
            case 0: // Townsfolk
            case 3: // Peaceful creature
            case 4: // Lawful
                // Excluded from the generic scan outright: these never INITIATE, they only retaliate
                // and pursue. Commander Markus #246 and royal soldier #245 are align 4 / Group 9, so
                // they land here and must never jump anyone — criminal or not. The departing free swing
                // agrees: for align 0/3/4 it swings at a departing player only when the
                // monster is ALREADY engaged with them (matching the bound-target name).
                return false;

            case 1: // Neutral aggressive
            case 2: // Evil hostile
            case 5: // Wild aggressive
                return true;

            case 6: // Evil NPC - attacks everyone up to and including Seedy; spares Outlaw and worse.
                // Align-6 excludes only EvilPoints > 39, so it aggros Saint/Good/Neutral/Seedy
                // (EP < 40) and skips the "evil" side (Outlaw+). The old gate stopped at Neutral, so
                // Seedy players were never aggroed while present yet still drew a departing free
                // swing (that one caps at 80, not 40) — the "duergar only attack when you leave"
                // report.
                return player.EvilPoints < EvilNpcAggroEvilPointsCap;

            default:
                return false;
        }
    }

    /// <summary>
    /// Calculate evil points gained for attacking a monster based on its alignment.
    /// </summary>
    public static float GetEPCostForMonsterAttack(Player player, Monster monster)
    {
        // The only progression gate is "too far to the evil side"
        // (EP > 300). There is NO "already Seedy → stop" gate — a player keeps gaining evil from
        // attacking innocents until EP > 300. (The previous IsEvil early-return stopped all gains
        // at Seedy (EP >= 30), which is why a single barmaid kill blocked every later guard/townie.)
        if (player.EvilPoints > 300)
            return 0.0f;

        // The "evil act" flag: attacking a monster
        // whose alignment is 0 (townsfolk) or 4 (lawful guards / shopkeepers / the barmaid) is an
        // evil act. Every other alignment (peaceful animals=3, aggressive/evil mobs) grants nothing.
        if (monster.Align != 0 && monster.Align != 4)
            return 0.0f;

        // The monster path passes a FLAT base of 10 —
        // there is no target-evilness ×2/×3 scaling here (that scaling exists only on the PvP
        // branch and keys off the *target's* goodness, not the attacker's alignment).
        float eps = 10.0f;

        // Floor: a good/saint attacker's first evil act bumps them up to exactly EP 10.
        if (player.EvilPoints + eps < 10)
            eps = 10.0f - player.EvilPoints;

        return eps;
    }

    /// <summary>
    /// Calculate evil points gained for attacking another player based on their alignment.
    /// </summary>
    public static float GetEPCostForPlayerAttack(Player attacker, Player target, bool recentlyAttackedBy, bool isRob = false)
    {
        // The only progression gate is "too far to the evil side"
        // (EP > 300). No attacker-already-evil gate.
        if (attacker.EvilPoints > 300)
            return 0.0f;

        float baseEPs = 0.0f;
        var targetAlign = GetPlayerAlignment(target.EvilPoints);

        // Gain EP only when striking a non-evil target (target EP < 30)
        // that hasn't already attacked you (self-defense / retaliation is not an evil act).
        if (!recentlyAttackedBy && !IsEvil(targetAlign))
        {
            // Flat base 10, scaled by how GOOD the *target* is (NOT the attacker's alignment):
            // Saint target (EP < -200) ×3, Good target (EP < -50) ×2, otherwise ×1.
            baseEPs = 10.0f;
            if (targetAlign == PlayerAlignment.Saint)
                baseEPs *= 3.0f;
            else if (targetAlign == PlayerAlignment.Good)
                baseEPs *= 2.0f;
        }

        // Rob EP adjustments (the robbing path uses a reduced base).
        if (isRob)
        {
            if (!recentlyAttackedBy)
            {
                baseEPs = Math.Min(baseEPs, 10);
            }
            else
            {
                if (IsGood(targetAlign) || targetAlign == PlayerAlignment.Neutral)
                    baseEPs = 2;
            }
        }

        // Floor: a good/saint attacker's first evil act bumps them up to exactly EP 10.
        if (baseEPs > 0.0f && attacker.EvilPoints + baseEPs < 10)
        {
            baseEPs = 10.0f - attacker.EvilPoints;
        }

        return baseEPs;
    }

    /// <summary>
    /// Evil points charged for a `rob` attempt. Identical scaling to a PvP attack,
    /// but with a base of 1
    /// instead of 10: ×2 vs a Good victim, ×3 vs a Saint victim, and 0 when the victim is already
    /// Seedy+ (EvilPoints >= 30). A good-aligned robber (negative EP)
    /// floors up to exactly 10 on their first evil act, same as the attack path. There is NO
    /// self-defence waiver — the robbing branch always charges (it never consults
    /// the should-give-evil rule). Charged on every outcome (noticed / hidden-fail / success).
    /// </summary>
    public static float GetEPCostForRob(Player robber, Player victim)
    {
        // "You have progressed too far to the evil side" — no further EP (the caller surfaces the
        // block message before we get here; returning 0 keeps the math consistent).
        if (robber.EvilPoints > 300)
            return 0.0f;

        // Robbing a Seedy+ target (EP >= 30) is "free" — stock zeroes the charge in that branch.
        if (victim.EvilPoints >= 30)
            return 0.0f;

        float baseEPs = 1.0f;
        var targetAlign = GetPlayerAlignment(victim.EvilPoints);
        if (targetAlign == PlayerAlignment.Saint)
            baseEPs = 3.0f;
        else if (targetAlign == PlayerAlignment.Good)
            baseEPs = 2.0f;

        if (robber.EvilPoints < 0 && robber.EvilPoints + baseEPs < 10)
            baseEPs = 10.0f - robber.EvilPoints;

        return baseEPs;
    }

    // The attack-type selector.
    // Enum values are the stock type numbers so the mapping stays auditable
    // against stock. PUNCH=1, KICK=2, JUMPKICK=3,
    // BACKSTAB=4, normal weapon swing=5, BASH=6, SMASH=7.
    public enum AttackType
    {
        Punch = 1,     // fists of fury (ability 29): acc 0, dmg +0%, crits allowed
        Kick = 2,      // lightning feet (30):        acc 0, dmg +33%, crits allowed
        Jumpkick = 3,  // flying feet (35):           acc 0, dmg +66%, crits allowed
        Backstab = 4,  // surprise attack:             acc 0, dmg +0%, NO crit, hit = AV - AC
        Normal = 5,    // standard weapon swing:       acc 0, dmg +0%, crits allowed
        Bash = 6,      // bash (31):                  acc -15, dmg +10% pre-roll, final x3, NO crit
        Smash = 7,     // smash (32):                 acc -25, dmg +20% pre-roll, final x5, NO crit
        Surprise = 8,  // type 8:                     acc -75, dmg +125% (no player command in V1.11p)
    }

    /// <summary>
    /// Accuracy modifier added to attacker accuracy before the hit roll.
    /// Stock: type 6=-15, 7=-25, 8=-75, all others 0.
    /// </summary>
    public static int GetAttackTypeAccuracyMod(AttackType type) => type switch
    {
        AttackType.Bash => -15,
        AttackType.Smash => -25,
        AttackType.Surprise => -75,
        _ => 0,
    };

    /// <summary>
    /// Pre-roll damage percentage applied to weapon min/max (damage = damage * (100 + pct) / 100).
    /// Stock: type 2=33, 3=66, 6=10, 7=20, 8=125, all others 0.
    /// </summary>
    public static int GetAttackTypeDamagePct(AttackType type) => type switch
    {
        AttackType.Kick => 33,
        AttackType.Jumpkick => 66,
        AttackType.Bash => 10,
        AttackType.Smash => 20,
        AttackType.Surprise => 125,
        _ => 0,
    };

    /// <summary>
    /// Smash (attack type 7) is ALWAYS exactly one swing per round. The fighter marshal
    /// overwrites the computed EU with the attacker's stamina CAP -- unlike bash
    /// (type 6), which merely doubles it -- so the round budget (one cap's worth of stamina) buys
    /// one swing and nothing more, no matter how fast the weapon is. Both the live swing loops and
    /// the `stat all` sheet read this constant so the sheet cannot over-report the row.
    /// </summary>
    public const int SmashSwingsPerRound = 1;

    /// <summary>
    /// Final damage multiplier applied AFTER damage-resist subtraction.
    /// Stock: type 6 (bash) x3, type 7 (smash) x5, all others x1.
    /// </summary>
    public static int GetAttackTypeDamageMultiplier(AttackType type) => type switch
    {
        AttackType.Bash => 3,
        AttackType.Smash => 5,
        _ => 1,
    };

    // Backstab does NOT crit (empirically confirmed against stock — L40 Ranger, bone club: backstab
    // range 142-260 / avg 209, no "critically" message, no four-figure hits). _calculate_attack
    // applies the rolled value directly as the damage, so the
    // [param[6],param[7]] IS the backstab range; a crit would set min=max*2/max=max*4 ⇒ 520-1040, which
    // stock never produces, and avg 209 == the no-crit midpoint (142+260)/2 (a crit term would push it
    // to ~288). NOTE: the raw pseudo-code LOOKS like it permits a backstab crit (the gate excludes
    // only bash/smash, and the fighter marshal loads crit>=1 for all types) — but stock play refutes
    // that, so the pseudo-code is misleading here. Bash/smash never crit either.
    private static bool AttackTypeAllowsCrit(AttackType type)
        => type is not (AttackType.Backstab or AttackType.Bash or AttackType.Smash);

    /// <summary>
    /// The to-hit chance: the clamped pre-roll hit percentage.
    /// Backstab (type 4) is the simple AV − AC line; every other type is the quadratic
    /// temp = (AV+mod)² / 140, then 100 − (AC + dodgeSkill)² / temp (temp==0 ⇒ 5). The result is
    /// clamped to [10, 99]. Excludes the negative-DG auto-hit window (that override stays at the call
    /// site because it consumes a second RNG roll). dodgeSkill is fighter[2] (the SKILL), not the DG
    /// rating used by the post-hit dodge gate.
    /// </summary>
    internal static int ComputeHitChance(AttackType attackType, int attackerAccuracy, int accuracyMod, int defenderAC, int defenderDodgeSkill)
    {
        int hitChance;
        if (attackType == AttackType.Backstab)
        {
            hitChance = attackerAccuracy - defenderAC;
        }
        else
        {
            int accTotal = attackerAccuracy + accuracyMod;
            int temp = accTotal * accTotal / 140;
            int defTotal = defenderAC + defenderDodgeSkill;
            hitChance = temp == 0 ? 5 : 100 - (defTotal * defTotal / temp);
        }

        return Math.Clamp(hitChance, 10, 99);
    }

    /// <summary>
    /// The post-hit dodge gate: the chance the landed blow is
    /// dodged outright. pdodge = AV&lt;9 ? 0 : (DG·10) / (AV/8), capped at 95, then /5 on a backstab.
    /// DG is the defender's dodge RATING (fighter[8]). The caller still gates the roll on DG &gt; 0.
    /// </summary>
    internal static int ComputeDodgeChance(AttackType attackType, int attackerAccuracy, int defenderDodge)
    {
        int dodgeChance = attackerAccuracy < 9 ? 0 : (defenderDodge * 10) / (attackerAccuracy / 8);
        if (dodgeChance > 95)
            dodgeChance = 95;
        if (attackType == AttackType.Backstab)
            dodgeChance /= 5;
        return dodgeChance;
    }

    /// <summary>
    /// Core attack calculation, matching the stock attack resolver.
    /// Two distinct dodge quantities are kept: <paramref name="defenderDodgeSkill"/> (the dodge-skill column,
    /// from abilities 24/25) feeds the to-hit formula, while <paramref name="defenderDodge"/> is the
    /// DG dodge rating (fighter[8]) that drives the negative-DG near-auto-hit window and the post-hit
    /// skill-dodge gate. Returns hit/miss/crit/glance/dodge and the final damage (damage-resist
    /// subtracted and the bash/smash multiplier already applied, in stock order). A glancing blow
    /// (`Glanced`) lands but is absorbed for 0 damage; a `Dodged` blow is fully avoided for 0 damage.
    /// </summary>
    public static CombatCalcResult CalculateAttack(int attackerAccuracy, int defenderAC, int defenderDodge,
        int weaponMin, int weaponMax, int critChance, AttackType attackType, int defenderDamageResist = 0,
        int defenderDodgeSkill = 0, bool attackerDefenseless = false, bool defenderDefenseless = false,
        bool allowCritOverride = false)
    {
        var result = new CombatCalcResult();

        // A smash-knocked-down combatant has
        // SmashDefenselessAvDvPenalty subtracted from BOTH their AV (attacker side) and DV (defender
        // side) every round. Applied here at the marshal boundary so it re-applies each beat.
        // Each is floored at 0 (`if (val < 35) val = 0; else val -= 35`), so a sub-35 AV/AC doesn't go
        // negative — Math.Max(0, …) reproduces that.
        if (attackerDefenseless)
            attackerAccuracy = Math.Max(0, attackerAccuracy - SmashDefenselessAvDvPenalty);
        if (defenderDefenseless)
            defenderAC = Math.Max(0, defenderAC - SmashDefenselessAvDvPenalty);

        int accuracyMod = GetAttackTypeAccuracyMod(attackType);
        int damagePct = GetAttackTypeDamagePct(attackType);
        // A surprise with a non-backstab weapon keeps the backstab HIT formula (AV-AC)
        // but resolves NORMAL-damage rules, which include the crit roll — so the caller can re-enable
        // crits that AttackType.Backstab would otherwise suppress.
        bool allowCrit = AttackTypeAllowsCrit(attackType) || allowCritOverride;

        // Pre-roll damage% modifier on weapon min/max (kick/jumpkick/bash/smash/surprise).
        if (damagePct != 0)
        {
            weaponMin = weaponMin * (100 + damagePct) / 100;
            weaponMax = weaponMax * (100 + damagePct) / 100;
        }

        result.Accuracy = attackerAccuracy;

        // Random roll 1-100 for the hit check.
        int hitRoll = _rng.Next(1, 100);

        int hitChance;
        if (defenderDodge < 0 && _rng.Next(0, 100) > defenderDodge + 100)
        {
            // A negative DG (set to -1 once the defender is at <=0 HP) opens a
            // near-auto-HIT window — hitChance is forced to 99 so the finishing blow lands. This is
            // a HIT path, not a miss (the swing then proceeds through the damage/glance logic below).
            hitChance = 99;
        }
        else
        {
            hitChance = ComputeHitChance(attackType, attackerAccuracy, accuracyMod, defenderAC, defenderDodgeSkill);
        }

        // Crit-chance diminishing returns above 40.
        if (critChance > 40)
            critChance = (critChance - 40) / 3 + 40;

        result.HitChance = hitChance;

        // Compare hitChance vs hitRoll.
        if (hitChance <= hitRoll)
        {
            result.Missed = true;
            return result;
        }

        // Crit roll (skipped for backstab/bash/smash).
        int rolledDamage;
        if (allowCrit && _rng.Next(0, 100) < critChance)
        {
            // Critical: range becomes [max*2, max*4].
            result.IsCrit = true;
            rolledDamage = _rng.Next(weaponMax * 2, (weaponMax * 4) + 1);
        }
        else
        {
            if (weaponMin > weaponMax)
                weaponMax = weaponMin;
            rolledDamage = _rng.Next(weaponMin, weaponMax + 1);
        }

        // Stock order: damage = roll - DR/10, THEN x3 (bash) / x5 (smash).
        int damage = (rolledDamage - ScaleDamageResist(defenderDamageResist)) * GetAttackTypeDamageMultiplier(attackType);

        // Post-hit skill-dodge gate. Scales the DG dodge rating against the attacker's
        // RAW accuracy: pdodge = AV<9 ? 0 : (DG*10)/(AV/8), capped at 95, /5 on a backstab. Only fires
        // when DG>0; a successful roll fully avoids the blow (result type 3) for 0 damage.
        int dodgeChance = ComputeDodgeChance(attackType, attackerAccuracy, defenderDodge);
        if (defenderDodge > 0 && _rng.Next(0, 100) < dodgeChance)
        {
            result.Dodged = true;
            result.Damage = 0;
            return result;
        }

        if (damage < 1)
        {
            // Result type 1: the blow lands but glances off the defender's armour for no damage.
            result.Damage = 0;
            result.Glanced = true;
        }
        else
        {
            result.Damage = damage;
        }

        return result;
    }

    /// <summary>
    /// Calculate bash damage (quest-given ability for certain classes).
    /// Bash goes through the attack resolver with type=7; damage is averaged over swings.
    /// </summary>
    public static int CalculateBashDamage(int weaponMin, int weaponMax, int swings,
        double preRollMinMod = 1.0, double preRollMaxMod = 1.0,
        double dmgMultMin = 1.0, double dmgMultMax = 1.0)
    {
        var avgDmg = (weaponMin * preRollMinMod * dmgMultMin + weaponMax * preRollMaxMod * dmgMultMax) / 2.0;
        var rndDmg = (int)(avgDmg * Math.Min(swings, MAX_SWINGS));
        return rndDmg;
    }

    // Per-round constants for a normal weapon attack vs a monster (everything that does not change
    // between swings). Built once, then ResolveNormalWeaponSwingVsMonster rolls each swing against it.
    internal readonly record struct NormalWeaponSwingProfile(
        int Swings, int WeaponMin, int WeaponMax, int Accuracy, int Crits, int DefAC, int DefDodge,
        WeaponHitPhrase HitPhrase, string MissVerb);

    internal static NormalWeaponSwingProfile BuildNormalWeaponSwingProfileVsMonster(
        Player player, MonsterInstance monster, CharacterClass cls, Item? weapon,
        Dictionary<int, RoomMessage>? messages, IGameDatabase? db)
    {
        var (weaponMin, weaponMax, accuracy, weaponType) = GetPlainWeaponAttackProfile(player, cls, weapon);
        int crits = GetWeaponCritChance(player, cls, weapon, db: db);
        int swings = GetWeaponRoundSwings(player, cls, weapon, db: db);
        return new NormalWeaponSwingProfile(
            swings, weaponMin, weaponMax, accuracy, crits,
            monster.EffectiveArmourClass, monster.EffectiveDodge,
            GetWeaponHitPhrase(weapon, messages, weaponType),
            GetWeaponMissVerb(weapon, messages));
    }

    // Resolve ONE normal weapon swing vs a monster: roll the attack, apply melee damage + life-steal,
    // append this swing's hit/glance/dodge/miss lines to <paramref name="result"/>, then (on a landed
    // hit that left the monster alive) roll the weapon proc and apply spike retaliation — in that exact
    // RNG order. Returns the triggered proc spell id (0 if none) and whether the attacker died to spike
    // retaliation. Does NOT fire the proc and does NOT compute death rewards: the caller decides whether
    // to queue the proc (synchronous full-round path) or fire it inline and cut the round off (the async
    // per-swing orchestrator). Shared by PlayerAttack and ResolveNormalWeaponRoundVsMonsterAsync so the
    // two never drift.
    internal static (int ProcSpellId, bool AttackerDied) ResolveNormalWeaponSwingVsMonster(
        Player player, MonsterInstance monster, Item? weapon, NormalWeaponSwingProfile profile,
        CombatResult result, Dictionary<int, RoomMessage>? messages,
        IReadOnlyList<RetaliationSource>? defenderRetaliation)
    {
        var calc = CalculateAttack(profile.Accuracy + player.PartyAccuracyModifier, profile.DefAC, profile.DefDodge,
            profile.WeaponMin, profile.WeaponMax, profile.Crits, AttackType.Normal,
            GetEffectiveMonsterDamageResist(monster), defenderDodgeSkill: monster.EffectiveDodgeSkill,
            attackerDefenseless: IsSmashDefenseless(player), defenderDefenseless: IsSmashDefenseless(monster));

        if (calc.IsCrit)
            result.Crits++;

        if (!calc.Missed && calc.Damage > 0)
        {
            int damage = calc.Damage;
            monster.CurrentHP -= damage;
            ApplyWeaponLifeSteal(player, weapon, damage);
            result.TotalDamage += damage;
            result.Hits++;

            if (calc.IsCrit)
            {
                result.Messages.Add(GameAnsi.CombatHit($"You critically {profile.HitPhrase.Self} {monster.DisplayName} for {damage} damage!"));
                result.RoomMessages.Add(GameAnsi.CombatHit($"{player.Name} critically {profile.HitPhrase.Room} {monster.DisplayName} for {damage} damage!"));
            }
            else
            {
                result.Messages.Add(GameAnsi.CombatHit($"You {profile.HitPhrase.Self} {monster.DisplayName} for {damage} damage!"));
                result.RoomMessages.Add(GameAnsi.CombatHit($"{player.Name} {profile.HitPhrase.Room} {monster.DisplayName} for {damage} damage!"));
            }

            int procSpellId = monster.IsDead ? 0 : RollWeaponSpellProc(weapon);
            bool attackerDied = ApplyRetaliationToPlayerAttacker(player, defenderRetaliation, null, result, messages);
            return (procSpellId, attackerDied);
        }

        if (calc.Glanced)
        {
            result.Hits++;
            AppendVsMonsterGlanceMessages(result, player, monster, profile.MissVerb);
        }
        else if (calc.Dodged)
        {
            result.Misses++;
            AppendVsMonsterDodgeMessages(result, player, monster, profile.MissVerb);
        }
        else
        {
            result.Misses++;
            AppendVsMonsterMissMessages(result, player, monster, profile.MissVerb);
        }

        return (0, false);
    }

    public static CombatResult PlayerAttack(Player player, MonsterInstance monster, CharacterClass cls, Item? weapon, Dictionary<int, RoomMessage>? messages = null, bool isBackstab = false, IGameDatabase? db = null, IReadOnlyList<RetaliationSource>? defenderRetaliation = null, bool surpriseRoundEnabled = false)
    {
        var result = new CombatResult();

        int weaponMin;
        int weaponMax;
        int weaponType;
        int accuracy = player.GetBaseAccuracy(cls.CombatLvl) + (weapon?.Accy ?? 0);

        // Evil points for attacking an innocent monster are charged ONCE per engagement at the
        // moment combat is opened (CommandParser.EngageCombatAsync), mirroring the stock
        // already-engaged gate — not on every swing. Do not charge EP here.

        // BACKSTAB sets attack type to 4 (backstab) or 5 (backstab w/o weapon)
        // then runs the normal attack pipeline
        AttackType attackType = isBackstab ? AttackType.Backstab : AttackType.Normal;

        if (isBackstab)
        {
            (weaponMin, weaponMax, var attackerAcc, weaponType, var fullBackstab) = GetBackstabSwingProfile(player, weapon, surpriseRoundEnabled);

            // Stock: per-swing message "You {verb} {monster} for {damage} damage!"
            // Hit verb from weapon's HitMsg → Messages table (pipe-delimited, pick random). A non-backstab
            // weapon (e4=5) deals normal damage and shows NO "surprise" verb; a real backstab (e4=4) does.
            var phraseBackstab = GetWeaponHitPhrase(weapon, messages, weaponType);
            string surprisePrefix = fullBackstab ? "surprise " : string.Empty;
            // Backstab is a single attack through the standard pipeline
            attackerAcc += player.PartyAccuracyModifier;
            // Backstab: AC = (AC >> 1) + BSDefense*2.
            int defAC = monster.GetMonsterBackstabArmourClass();
            // DG dodge rating (ability 34); the backstab reduction (/5) is applied inside the gate.
            int defDodge = monster.EffectiveDodge;
            int crits = player.GetCrits();

            var calc = CalculateAttack(attackerAcc, defAC, defDodge, weaponMin, weaponMax, crits, attackType, GetEffectiveMonsterDamageResist(monster), defenderDodgeSkill: monster.EffectiveDodgeSkill, attackerDefenseless: IsSmashDefenseless(player), defenderDefenseless: IsSmashDefenseless(monster), allowCritOverride: !fullBackstab);

            if (!calc.Missed && calc.Damage > 0)
            {
                int damage = calc.Damage;
                monster.CurrentHP -= damage;
                ApplyWeaponLifeSteal(player, weapon, damage);
                result.TotalDamage = damage;
                result.Hits = 1;
                result.IsBackstab = true;
                result.Messages.Add(GameAnsi.CombatHit($"You {surprisePrefix}{phraseBackstab.Self} {monster.DisplayName} for {damage} damage!"));
                result.RoomMessages.Add(GameAnsi.CombatHit($"{player.Name} {surprisePrefix}{phraseBackstab.Room} {monster.DisplayName} for {damage} damage!"));

                if (!monster.IsDead)
                    TryQueueWeaponSpellProc(weapon, result);

                ApplyRetaliationToPlayerAttacker(player, defenderRetaliation, null, result, messages);
            }
            else if (calc.Glanced)
            {
                result.Hits = 1;
                result.IsBackstab = true;
                AppendVsMonsterGlanceMessages(result, player, monster, GetWeaponMissVerb(weapon, messages));
            }
            else if (calc.Dodged)
            {
                result.Misses = 1;
                AppendVsMonsterDodgeMessages(result, player, monster, GetWeaponMissVerb(weapon, messages));
            }
            else
            {
                result.Misses = 1;
                AppendVsMonsterMissMessages(result, player, monster, GetWeaponMissVerb(weapon, messages));
            }
        }
        else
        {
            // Synchronous full-round resolution: roll every swing up front, queuing any procs for the
            // command layer to fire afterwards. Used by tests and as the reference resolver. Production
            // normal attacks instead drive ResolveNormalWeaponSwingVsMonster swing-by-swing through
            // CommandParser.ResolveNormalWeaponRoundVsMonsterAsync so a proc can land the killing blow and
            // cut the round off exactly (no further swings / spike retaliation), which a pre-rolled loop
            // cannot express. Both share the single-swing primitive below, so they stay in lockstep.
            _ = accuracy;
            var profile = BuildNormalWeaponSwingProfileVsMonster(player, monster, cls, weapon, messages, db);
            for (int index = 0; index < profile.Swings; index++)
            {
                var (procSpellId, attackerDied) = ResolveNormalWeaponSwingVsMonster(
                    player, monster, weapon, profile, result, messages, defenderRetaliation);

                if (procSpellId > 0)
                    result.TriggeredWeaponProcs.Add(new TriggeredWeaponProc(
                        procSpellId, result.Messages.Count, result.RoomMessages.Count, result.TargetMessages.Count));

                if (monster.IsDead || attackerDied)
                    break;
            }
        }

        if (monster.IsDead)
        {
            ApplyMonsterDeathRewards(result, monster);
        }

        return result;
    }

    // ─── Retaliation (ability 72): damage-shield / thorns ──────────────────────────────────
    // When a DEFENDER takes a
    // damaging melee hit, EVERY worn item AND active spell they carry with ability 72 rolls
    // SEPARATELY for 1..AbilVal damage against the ATTACKER (a shield spell
    // whose AbilVal is 0 — e.g. shockshield/barbskin — uses its cast level instead). Sources are not
    // summed; each rolls independently. Each source's flavor comes from its ability-137 message
    // id: line1 → defender's view, line2 → attacker's view, line3 → room (%s = attacker name, %d =
    // damage); no message → "You took N damage!" to the attacker. Retaliation can kill a player
    // attacker (the caller's post-combat HP check reaps them); a monster attacker just loses HP (the
    // kill check never runs on a monster here).
    public readonly record struct RetaliationSource(int Magnitude, int MessageId);

    public static List<RetaliationSource> BuildRetaliationSources(Player defender, IGameDatabase db)
    {
        var sources = new List<RetaliationSource>();
        foreach (var (_, itemId) in defender.Equipment)
        {
            if (db.Items.TryGetValue(itemId, out var item)
                && item.Abilities.TryGetValue(72, out var mag) && mag != 0)
                sources.Add(new RetaliationSource(mag, item.Abilities.GetValueOrDefault(137)));
        }

        AddSpellRetaliationSources(sources, defender.ActiveSpells, db);
        return sources;
    }

    public static List<RetaliationSource> BuildRetaliationSources(MonsterInstance defender, IGameDatabase db)
    {
        var sources = new List<RetaliationSource>();
        // Monsters have no worn-item slots in our model; their spikes are an innate template ability.
        if (defender.Template.Abilities.TryGetValue(72, out var innate) && innate != 0)
            sources.Add(new RetaliationSource(innate, defender.Template.Abilities.GetValueOrDefault(137)));

        AddSpellRetaliationSources(sources, defender.ActiveSpells, db);
        return sources;
    }

    private static void AddSpellRetaliationSources(List<RetaliationSource> sources, IEnumerable<ActiveSpell> activeSpells, IGameDatabase db)
    {
        foreach (var active in activeSpells)
        {
            if (active.SpellId <= 0 || !db.Spells.TryGetValue(active.SpellId, out var spell))
                continue;
            if (spell.Abilities.TryGetValue(72, out var mag))
                sources.Add(new RetaliationSource(mag != 0 ? mag : active.CastLevel, spell.Abilities.GetValueOrDefault(137)));
        }
    }

    private static int RollRetaliation(IReadOnlyList<RetaliationSource>? sources, string attackerName,
        List<string>? attackerView, List<string>? defenderView, List<string> roomView,
        IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        if (sources == null || sources.Count == 0)
            return 0;

        int total = 0;
        foreach (var src in sources)
        {
            int dmg = _rng.Next(1, Math.Max(1, src.Magnitude) + 1);   // genrdn(1, AbilVal+1) = 1..AbilVal
            total += dmg;

            RoomMessage? m = null;
            if (src.MessageId > 0)
                messages?.TryGetValue(src.MessageId, out m);

            if (m != null)
            {
                if (defenderView != null && !string.IsNullOrWhiteSpace(m.Line1))
                    defenderView.Add(GameAnsi.CombatHit(FormatRetaliation(m.Line1, attackerName, dmg)));
                if (attackerView != null && !string.IsNullOrWhiteSpace(m.Line2))
                    attackerView.Add(GameAnsi.CombatHit(FormatRetaliation(m.Line2, attackerName, dmg)));
                if (!string.IsNullOrWhiteSpace(m.Line3))
                    roomView.Add(GameAnsi.CombatHit(FormatRetaliation(m.Line3, attackerName, dmg)));
            }
            else if (attackerView != null)
            {
                attackerView.Add(GameAnsi.CombatHit($"You took {dmg} damage!"));
            }
        }

        return total;
    }

    private static string FormatRetaliation(string template, string attackerName, int dmg)
        => template.Replace("%s", attackerName).Replace("%d", dmg.ToString());

    // Apply the defender's retaliation to a PLAYER attacker (defenderView = the wearer's message list,
    // or null when the defender is a monster). Returns true if it killed the attacker (stop swinging).
    private static bool ApplyRetaliationToPlayerAttacker(Player attacker, IReadOnlyList<RetaliationSource>? sources,
        List<string>? defenderView, CombatResult result, IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        int dmg = RollRetaliation(sources, attacker.Name, result.Messages, defenderView, result.RoomMessages, messages);
        if (dmg <= 0)
            return false;
        attacker.CurrentHP -= dmg;
        return attacker.CurrentHP <= Player.DeathHP;
    }

    // Apply the player defender's retaliation to a MONSTER attacker. The defender is the current
    // player, so their wearer-view line goes to result.Messages.
    private static void ApplyRetaliationToMonsterAttacker(MonsterInstance attacker, IReadOnlyList<RetaliationSource>? sources,
        CombatResult result, IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        int dmg = RollRetaliation(sources, attacker.DisplayName, null, result.Messages, result.RoomMessages, messages);
        if (dmg > 0)
            attacker.CurrentHP -= dmg;
    }

    public static CombatResult PlayerAttackPlayer(Player attacker, Player target, CharacterClass cls, Item? weapon, Dictionary<int, RoomMessage>? messages = null, bool recentlyAttackedBy = false, PlayerCombatRoundAction action = PlayerCombatRoundAction.Attack, IGameDatabase? db = null, IReadOnlyList<RetaliationSource>? defenderRetaliation = null, bool surpriseRoundEnabled = false)
    {
        return action switch
        {
            PlayerCombatRoundAction.Backstab => PlayerBackstabPlayer(attacker, target, cls, weapon, messages, recentlyAttackedBy, defenderRetaliation, surpriseRoundEnabled),
            PlayerCombatRoundAction.Punch => PlayerMysticAttackPlayer(attacker, target, cls, "punch", recentlyAttackedBy, messages, defenderRetaliation),
            PlayerCombatRoundAction.Kick => PlayerMysticAttackPlayer(attacker, target, cls, "kick", recentlyAttackedBy, messages, defenderRetaliation),
            PlayerCombatRoundAction.Jumpkick => PlayerMysticAttackPlayer(attacker, target, cls, "jumpkick", recentlyAttackedBy, messages, defenderRetaliation),
            PlayerCombatRoundAction.Bash => PlayerBashAttackPlayer(attacker, target, cls, weapon, messages, recentlyAttackedBy, db, AttackType.Bash, defenderRetaliation),
            PlayerCombatRoundAction.Smash => PlayerBashAttackPlayer(attacker, target, cls, weapon, messages, recentlyAttackedBy, db, AttackType.Smash, defenderRetaliation),
            _ => PlayerWeaponAttackPlayer(attacker, target, cls, weapon, messages, recentlyAttackedBy, db, defenderRetaliation),
        };
    }

    private static CombatResult PlayerWeaponAttackPlayer(Player attacker, Player target, CharacterClass cls, Item? weapon, Dictionary<int, RoomMessage>? messages, bool recentlyAttackedBy, IGameDatabase? db, IReadOnlyList<RetaliationSource>? defenderRetaliation = null)
    {
        // Latch consciousness BEFORE any damage lands.
        bool targetWasConscious = target.CurrentHP > 0;
        var result = new CombatResult();

        var (weaponMin, weaponMax, accuracy, weaponType) = GetPlainWeaponAttackProfile(attacker, cls, weapon);
        int crits = GetWeaponCritChance(attacker, cls, weapon, db: db);
        int targetAC = target.GetTotalAC() + target.PartyDefenceModifier;
        int targetDodge = target.GetDodge();
        int targetDamageResist = target.GetTotalDR();
        int swings = GetWeaponRoundSwings(attacker, cls, weapon, db: db);
        var phrase = GetWeaponHitPhrase(weapon, messages, weaponType);
        string missVerb = GetWeaponMissVerb(weapon, messages);

        float epCost = GetEPCostForPlayerAttack(attacker, target, recentlyAttackedBy);
        if (epCost > 0)
        {
            attacker.TryAddEvilPoints(epCost);
        }

        for (int index = 0; index < swings; index++)
        {
            var calc = CalculateAttack(accuracy + attacker.PartyAccuracyModifier, targetAC, targetDodge, weaponMin, weaponMax, crits, AttackType.Normal, targetDamageResist, defenderDodgeSkill: target.GetCombatDodgeSkill(), attackerDefenseless: IsSmashDefenseless(attacker), defenderDefenseless: IsSmashDefenseless(target));
            if (calc.IsCrit)
            {
                result.Crits++;
            }

            if (!calc.Missed && calc.Damage > 0)
            {
                int damage = calc.Damage;
                target.CurrentHP -= damage;
                // Non-stock death log: remember who last hurt this character so a death can name its
                // killer. Recorded on the DAMAGE, not the kill, because the killing blow is resolved
                // several layers up (ExecuteForcedDeath) with no attacker in hand. See Player.RecordDeath.
                target.RecordDamageSource(attacker.Name);
                ApplyWeaponLifeSteal(attacker, weapon, damage);
                result.TotalDamage += damage;
                result.Hits++;

                if (calc.IsCrit)
                {
                    result.Messages.Add(GameAnsi.CombatHit($"You critically {phrase.Self} {target.Name} for {damage} damage!"));
                    result.TargetMessages.Add(GameAnsi.CombatHit($"{attacker.Name} critically {phrase.Target} you for {damage} damage!"));
                    result.RoomMessages.Add(GameAnsi.CombatHit($"{attacker.Name} critically {phrase.Room} {target.Name} for {damage} damage!"));
                }
                else
                {
                    result.Messages.Add(GameAnsi.CombatHit($"You {phrase.Self} {target.Name} for {damage} damage!"));
                    result.TargetMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {phrase.Target} you for {damage} damage!"));
                    result.RoomMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {phrase.Room} {target.Name} for {damage} damage!"));
                }

                if (target.CurrentHP > Player.DeathHP)
                    TryQueueWeaponSpellProc(weapon, result);

                bool attackerDied = ApplyRetaliationToPlayerAttacker(attacker, defenderRetaliation, result.TargetMessages, result, messages);

                if (target.CurrentHP <= Player.DeathHP || attackerDied)
                    break;
            }
            else if (calc.Glanced)
            {
                result.Hits++;
                AppendVsPlayerGlanceMessages(result, attacker, target, missVerb);
            }
            else if (calc.Dodged)
            {
                result.Misses++;
                AppendVsPlayerDodgeMessages(result, attacker, target, missVerb, weapon);
            }
            else
            {
                result.Misses++;
                AppendVsPlayerMissMessages(result, attacker, target, missVerb);
            }
        }

        AppendPlayerKillMessages(target, result, targetWasConscious);

        return result;
    }

    private static CombatResult PlayerBackstabPlayer(Player attacker, Player target, CharacterClass cls, Item? weapon, Dictionary<int, RoomMessage>? messages, bool recentlyAttackedBy, IReadOnlyList<RetaliationSource>? defenderRetaliation = null, bool surpriseRoundEnabled = false)
    {
        // Latch consciousness BEFORE any damage lands.
        bool targetWasConscious = target.CurrentHP > 0;
        var result = new CombatResult();

        var (weaponMin, weaponMax, accuracy, weaponType, fullBackstab) = GetBackstabSwingProfile(attacker, weapon, surpriseRoundEnabled);
        int crits = attacker.GetCrits();

        float epCost = GetEPCostForPlayerAttack(attacker, target, recentlyAttackedBy);
        if (epCost > 0)
        {
            attacker.TryAddEvilPoints(epCost);
        }

        int targetAC = target.GetTotalAC() + target.PartyDefenceModifier;
        // DG dodge rating; the backstab reduction (/5) is applied inside the gate.
        int targetDodge = target.GetDodge();
        var calc = CalculateAttack(accuracy + attacker.PartyAccuracyModifier, targetAC, targetDodge, weaponMin, weaponMax, crits, AttackType.Backstab, target.GetTotalDR(), defenderDodgeSkill: target.GetCombatDodgeSkill(), attackerDefenseless: IsSmashDefenseless(attacker), defenderDefenseless: IsSmashDefenseless(target), allowCritOverride: !fullBackstab);

        if (!calc.Missed && calc.Damage > 0)
        {
            int damage = calc.Damage;
            target.CurrentHP -= damage;
            target.RecordDamageSource(attacker.Name);
            ApplyWeaponLifeSteal(attacker, weapon, damage);
            result.TotalDamage = damage;
            result.Hits = 1;
            result.IsBackstab = true;
            var phrase = GetWeaponHitPhrase(weapon, messages, weaponType);
            string surprisePrefix = fullBackstab ? "surprise " : string.Empty;

            result.Messages.Add(GameAnsi.CombatHit($"You {surprisePrefix}{phrase.Self} {target.Name} for {damage} damage!"));
            result.TargetMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {surprisePrefix}{phrase.Target} you for {damage} damage!"));
            result.RoomMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {surprisePrefix}{phrase.Room} {target.Name} for {damage} damage!"));

            if (target.CurrentHP > Player.DeathHP)
                TryQueueWeaponSpellProc(weapon, result);

            ApplyRetaliationToPlayerAttacker(attacker, defenderRetaliation, result.TargetMessages, result, messages);
        }
        else if (calc.Glanced)
        {
            result.Hits = 1;
            result.IsBackstab = true;
            AppendVsPlayerGlanceMessages(result, attacker, target, GetWeaponMissVerb(weapon, messages));
        }
        else if (calc.Dodged)
        {
            result.Misses = 1;
            AppendVsPlayerDodgeMessages(result, attacker, target, GetWeaponMissVerb(weapon, messages), weapon);
        }
        else
        {
            result.Misses = 1;
            AppendVsPlayerMissMessages(result, attacker, target, GetWeaponMissVerb(weapon, messages));
        }

        AppendPlayerKillMessages(target, result, targetWasConscious);

        return result;
    }

    private static CombatResult PlayerMysticAttackPlayer(Player attacker, Player target, CharacterClass cls, string attackType, bool recentlyAttackedBy, Dictionary<int, RoomMessage>? messages = null, IReadOnlyList<RetaliationSource>? defenderRetaliation = null)
    {
        // Latch consciousness BEFORE any damage lands.
        bool targetWasConscious = target.CurrentHP > 0;
        var result = new CombatResult();

        int unarmedMin;
        int unarmedMax;
        string attackName;
        AttackType attackKind;

        switch (attackType.ToLowerInvariant())
        {
            case "kick":
                unarmedMin = attacker.GetKickMin();
                unarmedMax = attacker.GetKickMax();
                attackName = "kick";
                attackKind = AttackType.Kick;
                break;
            case "jumpkick":
                unarmedMin = attacker.GetJumpkickMin();
                unarmedMax = attacker.GetJumpkickMax();
                attackName = "jumpkick";
                attackKind = AttackType.Jumpkick;
                break;
            default:
                unarmedMin = attacker.GetPunchMin();
                unarmedMax = attacker.GetPunchMax();
                attackName = "punch";
                attackKind = AttackType.Punch;
                break;
        }

        float epCost = GetEPCostForPlayerAttack(attacker, target, recentlyAttackedBy);
        if (epCost > 0)
        {
            attacker.TryAddEvilPoints(epCost);
        }

        int swings = GetUnarmedRoundSwings(attacker, cls, attackType);
        int crits = GetUnarmedCritChance(attacker, cls, attackType);
        int targetAC = target.GetTotalAC() + target.PartyDefenceModifier;
        int targetDodge = target.GetDodge();
        int targetDamageResist = target.GetTotalDR();

        // Unarmed types 1/2/3 use the
        // SAME AV formula as the weapon path (stat-derived + the AV-bonus accumulator). Only the
        // weapon-skill term and weapon.Accy are absent. Without this base term, AV≈0 and the
        // CalculateAttack clamp pins hit chance to 10 — mystic attacks miss almost everything.
        // The party-rank to-hit modifier is skipped for
        // jumpkick (attack-type 3). The AC half (PartyDefenceModifier, folded into targetAC) still applies.
        int unarmedAccuracy = attacker.GetBaseAccuracy(cls.CombatLvl)
            + (attackKind == AttackType.Jumpkick ? 0 : attacker.PartyAccuracyModifier);
        for (int index = 0; index < Math.Min(swings, MAX_SWINGS); index++)
        {
            var calc = CalculateAttack(unarmedAccuracy, targetAC, targetDodge, unarmedMin, unarmedMax, crits, attackKind, targetDamageResist, defenderDodgeSkill: target.GetCombatDodgeSkill(), attackerDefenseless: IsSmashDefenseless(attacker), defenderDefenseless: IsSmashDefenseless(target));

            if (!calc.Missed && calc.Damage > 0)
            {
                int damage = calc.Damage;
                target.CurrentHP -= damage;
                target.RecordDamageSource(attacker.Name);
                result.TotalDamage += damage;
                result.Hits++;
                if (calc.IsCrit)
                    result.Crits++;

                if (calc.IsCrit)
                {
                    result.Messages.Add(GameAnsi.CombatHit($"You critically {attackName} {target.Name} for {damage} damage!"));
                    result.TargetMessages.Add(GameAnsi.CombatHit($"{attacker.Name} critically {ConjugateThirdPersonPhrase(attackName)} you for {damage} damage!"));
                    result.RoomMessages.Add(GameAnsi.CombatHit($"{attacker.Name} critically {ConjugateThirdPersonPhrase(attackName)} {target.Name} for {damage} damage!"));
                }
                else
                {
                    result.Messages.Add(GameAnsi.CombatHit($"You {attackName} {target.Name} for {damage} damage!"));
                    result.TargetMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {ConjugateThirdPersonPhrase(attackName)} you for {damage} damage!"));
                    result.RoomMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {ConjugateThirdPersonPhrase(attackName)} {target.Name} for {damage} damage!"));
                }

                if (ApplyRetaliationToPlayerAttacker(attacker, defenderRetaliation, result.TargetMessages, result, messages))
                    break;
            }
            else if (calc.Glanced)
            {
                result.Hits++;
                AppendVsPlayerGlanceMessages(result, attacker, target, attackName);
            }
            else if (calc.Dodged)
            {
                result.Misses++;
                AppendVsPlayerDodgeMessages(result, attacker, target, GetPlayerMissVerb());
            }
            else
            {
                result.Misses++;
                AppendVsPlayerMissMessages(result, attacker, target, GetPlayerMissVerb());
            }

            if (target.CurrentHP <= 0)
                break;
        }

        AppendPlayerKillMessages(target, result, targetWasConscious);

        return result;
    }

    private static CombatResult PlayerBashAttackPlayer(Player attacker, Player target, CharacterClass cls, Item? weapon, Dictionary<int, RoomMessage>? messages, bool recentlyAttackedBy, IGameDatabase? db, AttackType attackType = AttackType.Bash, IReadOnlyList<RetaliationSource>? defenderRetaliation = null)
    {
        // Latch consciousness BEFORE any damage lands.
        bool targetWasConscious = target.CurrentHP > 0;
        var result = new CombatResult();

        var (weaponMin, weaponMax, accuracy, weaponType) = GetPlainWeaponAttackProfile(attacker, cls, weapon);
        int crits = GetWeaponCritChance(attacker, cls, weapon, isBashing: true, db: db);
        // SMASH (attack type 7) is a single all-in swing that drains all stamina
        // (EU is overwritten with the stamina cap -- see SmashSwingsPerRound).
        int swings = attackType == AttackType.Smash
            ? SmashSwingsPerRound
            : GetWeaponRoundSwings(attacker, cls, weapon, isBashing: true, db: db);
        if (attackType == AttackType.Smash)
            attacker.CurrentEnergy = 0;
        var phrase = GetWeaponHitPhrase(weapon, messages, weaponType);
        string missVerb = GetWeaponMissVerb(weapon, messages);

        float epCost = GetEPCostForPlayerAttack(attacker, target, recentlyAttackedBy);
        if (epCost > 0)
        {
            attacker.TryAddEvilPoints(epCost);
        }

        int targetAC = target.GetTotalAC() + target.PartyDefenceModifier;
        int targetDodge = target.GetDodge();
        int targetDamageResist = target.GetTotalDR();
        for (int index = 0; index < swings; index++)
        {
            var calc = CalculateAttack(accuracy + attacker.PartyAccuracyModifier, targetAC, targetDodge, weaponMin, weaponMax, crits, attackType, targetDamageResist, defenderDodgeSkill: target.GetCombatDodgeSkill(), attackerDefenseless: IsSmashDefenseless(attacker), defenderDefenseless: IsSmashDefenseless(target));

            if (!calc.Missed && calc.Damage > 0)
            {
                int bashDamage = calc.Damage;
                target.CurrentHP -= bashDamage;
                target.RecordDamageSource(attacker.Name);
                ApplyWeaponLifeSteal(attacker, weapon, bashDamage);
                result.TotalDamage += bashDamage;
                result.Hits++;
                result.Messages.Add(GameAnsi.CombatHit($"You {phrase.Self} {target.Name} for {bashDamage} damage!"));
                result.TargetMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {phrase.Target} you for {bashDamage} damage!"));
                result.RoomMessages.Add(GameAnsi.CombatHit($"{attacker.Name} {phrase.Room} {target.Name} for {bashDamage} damage!"));

                if (attackType == AttackType.Smash && target.CurrentHP > Player.DeathHP)
                    TryApplyPlayerSmashKnockdown(attacker, target, result);

                if (target.CurrentHP > Player.DeathHP)
                    TryQueueWeaponSpellProc(weapon, result);

                bool attackerDied = ApplyRetaliationToPlayerAttacker(attacker, defenderRetaliation, result.TargetMessages, result, messages);

                if (target.CurrentHP <= Player.DeathHP || attackerDied)
                    break;
            }
            else if (calc.Glanced)
            {
                result.Hits++;
                AppendVsPlayerGlanceMessages(result, attacker, target, missVerb);
            }
            else if (calc.Dodged)
            {
                result.Misses++;
                AppendVsPlayerDodgeMessages(result, attacker, target, missVerb, weapon);
            }
            else
            {
                result.Misses++;
                AppendVsPlayerMissMessages(result, attacker, target, missVerb);
            }
        }

        AppendPlayerKillMessages(target, result, targetWasConscious);

        return result;
    }

    // Consciousness is latched BEFORE the swing loop, and after the
    // damage resolves runs:
    //     died = kill check on the victim              // true == the victim actually DIED
    //     if (!died && victim HP < 1 && wasConscious) → "<name> drops to the ground!"
    //     else if (died)                              → distribute experience (the kill
    //                                                       already printed the death lines)
    // So the drop line marks exactly one thing: the CONSCIOUS → UNCONSCIOUS-BUT-ALIVE transition.
    // A blow that kills outright skips it (you get "You have been killed!" / "<name> is dead." only),
    // and a blow landed on someone already down prints nothing at all because the latch is false.
    // We used to emit it for every HP <= 0 regardless, so a one-shot kill read
    // "You drop to the ground! / You have been killed!" when stock prints only the second line.
    private static void AppendPlayerKillMessages(Player target, CombatResult result, bool targetWasConscious)
    {
        if (target.CurrentHP > 0)
            return;

        result.TargetKilled = true;

        if (target.CurrentHP <= Player.DeathHP)
        {
            result.TargetMessages.Add(GameAnsi.KilledOutright("You have been killed!"));
            return;
        }

        if (targetWasConscious)
            result.TargetMessages.Add(GameAnsi.DropsToTheGround($"{target.Name} drops to the ground!"));
    }

    /// <summary>
    /// Mystic unarmed attack (punch, kick, or jumpkick).
    /// These go through the attack resolver with types 2 (punch/+33%), 3 (kick/+66%), 8 (jumpkick).
    /// </summary>
    public static CombatResult MysticAttack(Player player, MonsterInstance monster, CharacterClass cls, string attackType, Dictionary<int, RoomMessage>? messages = null, IReadOnlyList<RetaliationSource>? defenderRetaliation = null)
    {
        var result = new CombatResult();

        int unarmedMin, unarmedMax;
        string attackName;
        AttackType atkType;

        switch (attackType.ToLowerInvariant())
        {
            case "kick":
                unarmedMin = player.GetKickMin();
                unarmedMax = player.GetKickMax();
                attackName = "kick";
                atkType = AttackType.Kick; // type 3: +66% damage
                break;
            case "jumpkick":
                unarmedMin = player.GetJumpkickMin();
                unarmedMax = player.GetJumpkickMax();
                attackName = "jumpkick";
                atkType = AttackType.Jumpkick; // type 3: +66% damage
                break;
            default: // punch
                unarmedMin = player.GetPunchMin();
                unarmedMax = player.GetPunchMax();
                attackName = "punch";
                atkType = AttackType.Punch; // type 2: +33% damage
                break;
        }

        // Evil points are charged once per engagement at combat open (EngageCombatAsync), not per
        // swing — see PlayerAttack.

        int swings = GetUnarmedRoundSwings(player, cls, attackType);
        int crits = GetUnarmedCritChance(player, cls, attackType);
        int defAC = monster.EffectiveArmourClass;
        int defDodge = monster.EffectiveDodge;

        // Unarmed types 1/2/3 use the
        // SAME AV formula as the weapon path; only the weapon-skill and weapon.Accy terms are absent.
        // Without this, AV≈0 → hit chance pinned to the 10 floor and mystics miss almost everything.
        // The party-rank to-hit modifier is skipped for
        // jumpkick (attack-type 3). The AC half (PartyDefenceModifier, folded into defAC) still applies.
        int unarmedAccuracy = player.GetBaseAccuracy(cls.CombatLvl)
            + (atkType == AttackType.Jumpkick ? 0 : player.PartyAccuracyModifier);

        for (int i = 0; i < Math.Min(swings, MAX_SWINGS); i++)
        {
            // Each swing goes through the attack resolver with the appropriate type
            var calc = CalculateAttack(unarmedAccuracy, defAC, defDodge, unarmedMin, unarmedMax, crits, atkType, GetEffectiveMonsterDamageResist(monster), defenderDodgeSkill: monster.EffectiveDodgeSkill, attackerDefenseless: IsSmashDefenseless(player), defenderDefenseless: IsSmashDefenseless(monster));

            if (!calc.Missed && calc.Damage > 0)
            {
                int damage = calc.Damage;
                monster.CurrentHP -= damage;
                result.TotalDamage += damage;
                result.Hits++;
                if (calc.IsCrit) result.Crits++;

                // Stock: per-swing message
                if (calc.IsCrit)
                {
                    result.Messages.Add(GameAnsi.CombatHit($"You critically {attackName} {monster.DisplayName} for {damage} damage!"));
                    result.RoomMessages.Add(GameAnsi.CombatHit($"{player.Name} critically {ConjugateThirdPersonPhrase(attackName)} {monster.DisplayName} for {damage} damage!"));
                }
                else
                {
                    result.Messages.Add(GameAnsi.CombatHit($"You {attackName} {monster.DisplayName} for {damage} damage!"));
                    result.RoomMessages.Add(GameAnsi.CombatHit($"{player.Name} {ConjugateThirdPersonPhrase(attackName)} {monster.DisplayName} for {damage} damage!"));
                }

                if (ApplyRetaliationToPlayerAttacker(player, defenderRetaliation, null, result, messages))
                    break;
            }
            else if (calc.Glanced)
            {
                result.Hits++;
                AppendVsMonsterGlanceMessages(result, player, monster, attackName);
            }
            else if (calc.Dodged)
            {
                result.Misses++;
                AppendVsMonsterDodgeMessages(result, player, monster, GetPlayerMissVerb());
            }
            else
            {
                result.Misses++;
                // Unarmed: no weapon MissMsg, use generic miss verbs
                AppendVsMonsterMissMessages(result, player, monster, GetPlayerMissVerb());
            }

            if (monster.IsDead) break;
        }

        if (monster.IsDead)
        {
            ApplyMonsterDeathRewards(result, monster);
        }

        return result;
    }

    /// <summary>
    /// bash attack (All Classes have this).
    /// Uses the attack resolver with type=6, no crits allowed.
    /// </summary>
    public static CombatResult BashAttack(Player player, MonsterInstance monster, CharacterClass cls, Item? weapon, Dictionary<int, RoomMessage>? messages = null, IGameDatabase? db = null, AttackType attackType = AttackType.Bash, IReadOnlyList<RetaliationSource>? defenderRetaliation = null)
    {
        var result = new CombatResult();

        var (weaponMin, weaponMax, accuracy, weaponType) = GetPlainWeaponAttackProfile(player, cls, weapon);

        // Evil points are charged once per engagement at combat open (EngageCombatAsync), not per
        // swing — see PlayerAttack.

        int defAC = monster.EffectiveArmourClass;
        int defDodge = monster.EffectiveDodge;
        int crits = GetWeaponCritChance(player, cls, weapon, isBashing: true, db: db);
        // SMASH (attack type 7) is a single all-in swing that spends the attacker's
        // entire stamina pool (EU is set to the stamina cap --
        // see SmashSwingsPerRound).
        int swings = attackType == AttackType.Smash
            ? SmashSwingsPerRound
            : GetWeaponRoundSwings(player, cls, weapon, isBashing: true, db: db);
        if (attackType == AttackType.Smash)
            player.CurrentEnergy = 0;
        var phrase = GetWeaponHitPhrase(weapon, messages, weaponType);
        string missVerb = GetWeaponMissVerb(weapon, messages);

        // Bash is a single attack through the resolver with type 6
        // Crits are disabled for the bash type
        for (int index = 0; index < swings; index++)
        {
            var calc = CalculateAttack(accuracy + player.PartyAccuracyModifier, defAC, defDodge, weaponMin, weaponMax, crits, attackType, GetEffectiveMonsterDamageResist(monster), defenderDodgeSkill: monster.EffectiveDodgeSkill, attackerDefenseless: IsSmashDefenseless(player), defenderDefenseless: IsSmashDefenseless(monster));

            if (!calc.Missed && calc.Damage > 0)
            {
                int bashDamage = calc.Damage;
                monster.CurrentHP -= bashDamage;
                ApplyWeaponLifeSteal(player, weapon, bashDamage);
                result.TotalDamage += bashDamage;
                result.Hits++;
                result.Messages.Add(GameAnsi.CombatHit($"You {phrase.Self} {monster.DisplayName} for {bashDamage} damage!"));
                result.RoomMessages.Add(GameAnsi.CombatHit($"{player.Name} {phrase.Room} {monster.DisplayName} for {bashDamage} damage!"));

                bool attackerDied = ApplyRetaliationToPlayerAttacker(player, defenderRetaliation, null, result, messages);

                // Knockdown lives in the damaging-hit branch only: a miss, dodge or zero-damage glance
                // never knocks the monster down, and neither does the blow that kills it.
                if (attackType == AttackType.Smash && !monster.IsDead)
                    TryApplyMonsterSmashKnockdown(player, monster, result);

                if (monster.IsDead || attackerDied)
                    break;
            }
            else if (calc.Glanced)
            {
                result.Hits++;
                AppendVsMonsterGlanceMessages(result, player, monster, missVerb);
            }
            else if (calc.Dodged)
            {
                result.Misses++;
                AppendVsMonsterDodgeMessages(result, player, monster, missVerb);
            }
            else
            {
                result.Misses++;
                AppendVsMonsterMissMessages(result, player, monster, missVerb);
            }
        }

        if (monster.IsDead)
        {
            ApplyMonsterDeathRewards(result, monster);
        }

        return result;
    }

    internal static CombatResult CreateMonsterDeathResult(MonsterInstance monster)
    {
        var result = new CombatResult();
        ApplyMonsterDeathRewards(result, monster);
        return result;
    }

    internal static void ApplyMonsterDeathRewards(CombatResult result, MonsterInstance monster)
    {
        result.TargetKilled = true;
        result.DeathMessage = GetDeathMessage(monster);
        result.Drops.AddRange(monster.CarriedDropItemIds);
        result.GoldDropped = monster.CarriedGold;
        result.SilverDropped = monster.CarriedSilver;
        result.CopperDropped = monster.CarriedCopper;
        result.PlatinumDropped = monster.CarriedPlatinum;
        result.RunicDropped = monster.CarriedRunic;
        // Kill experience is Experience x ExpMulti, not Experience alone. BOTH template
        // fields are read and multiplied before the total is distributed:
        //     total = Experience * ExpMultiplier
        //     distribute(total, ...)
        // (three call sites do this identically.)
        //
        // We paid the base only, so all 193 templates with a multiplier above 1 underpaid. It hides
        // early — giant rat, kobold thief and most starter mobs are x1, so their awards looked right —
        // and grows badly with level: cave bear #80 is 100 x3 = 300, Zanthus the Lich #215 is
        // 50,000 x5,000, Tyrannosaur #514 is 65,000 x9,999. Bug #207.
        //
        // A PLAIN product, deliberately un-clamped. Six templates carry EXP > 0 with a multiplier of
        // 0 — Remik of the Ebon Blade, Aeriana the Pure, the hooded man, the beautiful woman (20,000
        // base), the ghostly figure, the feline prisoner — all quest/NPC figures. Stock multiplies
        // straight through, so those award NOTHING, which is plainly the intent: they must not be
        // farmable. Clamping the multiplier up to 1 would quietly turn them into exp pinatas.
        result.ExpGained = ComputeMonsterKillExperience(monster);
        monster.ClearCarriedTreasure();
    }

    /// <summary>Kill experience for a monster: Experience x ExpMulti (see the call site for the
    /// details). Exposed so the multiplier rule can be pinned without driving a whole combat round.</summary>
    internal static long ComputeMonsterKillExperience(MonsterInstance monster)
        => (long)monster.Template.EXP * (long)monster.Template.ExpMulti;

    // Stock attack verb sets by weapon type
    // WeaponType 0 = 1-Handed Blunt, 1 = 2-Handed Blunt, 2 = 1-Handed Sharp, 3 = 2-Handed Sharp, 4 = Natural
    private static readonly string[][] BluntVerbs = new[]
    {
        new[] { "pound", "smash", "crush" },
        new[] { "bludgeon", "smash", "crush" },
        new[] { "smash", "clobber", "slam" },
    };
    private static readonly string[][] SharpVerbs = new[]
    {
        new[] { "slash", "impale", "hack" },
        new[] { "impale", "skewer", "slash" },
        new[] { "hack", "chop", "cut" },
        new[] { "slice", "cut", "slash" },
        new[] { "cleave", "chop", "cut" },
    };
    private static readonly string[] NaturalVerbs = new[] { "claw", "bite", "strike", "attack" };

    /// <summary>Get a random attack verb for a monster based on weapon type (stock verb sets).</summary>
    private static string GetMonsterAttackVerb(int weaponType)
    {
        return weaponType switch
        {
            0 or 1 => BluntVerbs[_rng.Next(BluntVerbs.Length)][_rng.Next(3)],  // Blunt
            2 or 3 => SharpVerbs[_rng.Next(SharpVerbs.Length)][_rng.Next(3)],  // Sharp
            _ => NaturalVerbs[_rng.Next(NaturalVerbs.Length)],                  // Natural/None
        };
    }

    /// <summary>Pluralize an attack verb (add 's' or 'es').</summary>
    private static string PluralVerb(string verb)
    {
        if (verb.EndsWith("sh") || verb.EndsWith("ch")) return verb + "es";
        return verb + "s";
    }

    // The attack resolver subtracts defender DR scaled by 10.
    private static int ScaleDamageResist(int rawDr)
    {
        if (rawDr <= 0)
            return 0;

        return rawDr / 10;
    }

    // Result type 1: the blow lands but is fully absorbed by armour (0 damage).
    private static string GetGlancingBlowMessage(string attackerPossessive, string missVerb, string targetName, string armourPronoun)
    {
        return $"{attackerPossessive} {missVerb} {targetName} hits, but glances off {armourPronoun} armour.";
    }

    // ── Miss-branch message helpers ─────────────────────────────────────────────────────────
    // 8 player-attack functions used to inline the same three message-pair (vs monster) or
    // message-triple (vs player) blocks for glance/dodge/miss. These wrap exactly those Add
    // calls — every site's `else if (calc.Glanced) ... else if (calc.Dodged) ... else ...`
    // now reads as three one-liners. Counter bookkeeping (result.Hits/Misses, IsBackstab,
    // etc.) stays at the call site because backstab uses `=1` while normal uses `++`.

    private static void AppendVsMonsterGlanceMessages(CombatResult result, Player player, MonsterInstance monster, string missVerb)
    {
        result.Messages.Add(GameAnsi.CombatGlance(GetGlancingBlowMessage("Your", missVerb, monster.DisplayName, "its")));
        result.RoomMessages.Add(GameAnsi.CombatGlance(GetGlancingBlowMessage(GetPossessiveName(player.Name), missVerb, monster.DisplayName, "its")));
    }

    private static void AppendVsMonsterDodgeMessages(CombatResult result, Player player, MonsterInstance monster, string missVerb)
    {
        result.Messages.Add(GameAnsi.Dodge(GetAttackerDodgeSelfMessage(missVerb, monster.DisplayName)));
        result.RoomMessages.Add(GameAnsi.Dodge(GetAttackerDodgeRoomMessage(player.Name, missVerb, monster.DisplayName)));
    }

    private static void AppendVsMonsterMissMessages(CombatResult result, Player player, MonsterInstance monster, string missVerb)
    {
        result.Messages.Add(GameAnsi.CombatMiss($"You {missVerb} {monster.DisplayName}!"));
        result.RoomMessages.Add(GameAnsi.CombatMiss($"{player.Name} {ConjugateThirdPersonPhrase(missVerb)} {monster.DisplayName}!"));
    }

    // PvP glance literals (self / target / room):
    //   self:   "Your %s %s hits, but glances off %s armour!"   args: verb, targetName, targetPronoun
    //   target: "%s's %s you hits, but your armour deflects!"   args: attackerName, verb
    //   room:   "%s's %s %s hits, but glances off %s armour!"   args: attackerName, verb, targetName, targetPronoun
    // The verb is the attacker's first-person attack token — the SAME `missVerb`
    // used by the dodge/miss builders — used verbatim even in the third-person target/room strings.
    // The trailing pronoun is the *target's* gendered possessive ('his'/
    // 'her', else 'their'). These differ from the vs-monster glance ("…off its armour."
    // with a period) — distinct strings, so PvP gets its own builder.
    private static void AppendVsPlayerGlanceMessages(CombatResult result, Player attacker, Player target, string missVerb)
    {
        string pronoun = GetPossessivePronoun(target);
        result.Messages.Add(GameAnsi.CombatGlance($"Your {missVerb} {target.Name} hits, but glances off {pronoun} armour!"));
        result.TargetMessages.Add(GameAnsi.CombatGlance($"{GetPossessiveName(attacker.Name)} {missVerb} you hits, but your armour deflects!"));
        result.RoomMessages.Add(GameAnsi.CombatGlance($"{GetPossessiveName(attacker.Name)} {missVerb} {target.Name} hits, but glances off {pronoun} armour!"));
    }

    // The player's gendered possessive pronoun ('her'/'his'). Player gender is binary
    // here (0=Male, 1=Female), so the 'their'/'its' fallbacks for unknown gender never apply.
    private static string GetPossessivePronoun(Player player) => player.Gender == 1 ? "her" : "his";

    // PvP dodge literals (self / target / room):
    //   self:   "You %s %s who dodges your attack!"
    //   target: "%s %s at you with %s %s, but you dodge the attack!"
    //   room:   "%s %s at %s with %s %s, but %s dodges!"
    // The "with %s %s" is indefinite-article + weapon name. missVerb already carries the preposition
    // ("lunge at"), so ConjugateThirdPersonPhrase("lunge at") = "lunges at" reproduces "%s at you …"
    // verbatim. An unarmed attacker (no weapon) drops the "with …" clause.
    private static void AppendVsPlayerDodgeMessages(CombatResult result, Player attacker, Player target, string missVerb, Item? weapon = null)
    {
        string thirdVerb = ConjugateThirdPersonPhrase(missVerb);
        string withClause = weapon != null ? $" with {IndefiniteArticle(weapon.Name)}{weapon.Name}" : string.Empty;

        result.Messages.Add(GameAnsi.Dodge(GetAttackerDodgeSelfMessage(missVerb, target.Name)));
        result.TargetMessages.Add(GameAnsi.Dodge($"{attacker.Name} {thirdVerb} you{withClause}, but you dodge the attack!"));
        result.RoomMessages.Add(GameAnsi.Dodge($"{attacker.Name} {thirdVerb} {target.Name}{withClause}, but {target.Name} dodges!"));
    }

    // "a quarterstaff" / "an axe" — indefinite article for the dodge weapon clause.
    private static string IndefiniteArticle(string noun)
        => !string.IsNullOrEmpty(noun) && "aeiouAEIOU".Contains(noun[0]) ? "an " : "a ";

    private static void AppendVsPlayerMissMessages(CombatResult result, Player attacker, Player target, string missVerb)
    {
        result.Messages.Add(GameAnsi.CombatMiss($"You {missVerb} {target.Name}!"));
        result.TargetMessages.Add(GameAnsi.CombatMiss($"{attacker.Name} {ConjugateThirdPersonPhrase(missVerb)} you!"));
        result.RoomMessages.Add(GameAnsi.CombatMiss($"{attacker.Name} {ConjugateThirdPersonPhrase(missVerb)} {target.Name}!"));
    }

    private static int ClampMonsterDamageRoll(int rawDamage, int minDamage, int maxDamage)
    {
        if (rawDamage <= 0)
            return 0;

        if (minDamage > maxDamage)
            (minDamage, maxDamage) = (maxDamage, minDamage);

        return Math.Clamp(rawDamage, minDamage, maxDamage);
    }

    internal static bool IsRealMonsterAttackType(int type)
        => type is >= MonsterMeleeAttackType and <= MonsterRobAttackType;

    private static MonsterAttack? SelectMonsterAttack(IReadOnlyList<MonsterAttack> attacks)
    {
        // Per-swing selection re-rolls past any slot whose AtkType
        // is 0 (`while (cVar1 == '\0')`) — type 0 is an empty/disabled slot, never an attack. Our
        // loader keeps such a slot when it carries leftover Min/Max damage, so an empty slot with a
        // stray HitMessage (e.g. the barmaid's slot 1 → message 14 "You cast %s ...") was being rolled
        // and rendered as a phantom hit dealing its garbage damage. Type-0 slots are skipped
        // UNCONDITIONALLY — a monster whose slots are ALL type 0 (e.g. Inquisitor Fulgore #532,
        // align-2 with leftover 3-14/8-21 damage) exhausts its swings and lands nothing. Bug #191:
        // an earlier hasRealAttack fallback let such all-empty monsters melee with their leftover
        // numbers; it is gone — type 0 is never an attack, full stop.
        int roll = _rng.Next(1, 100);

        foreach (var attack in attacks.OrderBy(candidate => candidate.SlotIndex))
        {
            if (attack.Percent <= 0)
                continue;

            if (!IsRealMonsterAttackType(attack.Type))
                continue;

            if (roll <= attack.Percent)
                return attack;
        }

        return null;
    }

    internal static int GetMonsterAttackEnergyCost(MonsterInstance monster, MonsterAttack attack)
    {
        int energy = attack.Energy > 0 ? attack.Energy : monster.GetCombatEnergyCap();

        // Per-swing EU is scaled by the monster's haste/slow ability
        // (87) — slow ⇒ >100% (fewer swings), haste ⇒ <100% (more) — then clamped to the template
        // `energy` field (offset 122) = its energy cap. So clamping to
        // GetCombatEnergyCap() (= Template.Energy) is exact, not an approximation.
        int percent = monster.HasteSlowPercent;
        if (percent > 0 && percent != 100)
            energy = Math.Min(monster.GetCombatEnergyCap(), energy * percent / 100);

        return Math.Max(1, energy);
    }

    /// <summary>
    /// Runs one monster combat round against the player: up to MAX_SWINGS swings, each picked by
    /// AtkPer and gated by the monster's energy pool, branching on AtkType — 1 = melee swing,
    /// 2 = spell attack (resolved via <paramref name="resolveSpellAttack"/>), 3 = rob (unused in data).
    /// Damage is applied in-place and all messages accumulate into the returned result; the caller
    /// sends them.
    /// </summary>
    /// <param name="monster">Attacking monster; its energy pool is spent per swing.</param>
    /// <param name="player">Defending player; HP is reduced in-place.</param>
    /// <param name="items">Item table, used for weapon-type attack verbs.</param>
    /// <param name="messages">Room-message table, for per-attack hit/dodge/miss templates.</param>
    /// <param name="resolveSpellAttack">Synchronously resolves an AtkType=2 spell attack: casts spell
    /// (spellId, castLevel) on the player, applies its damage, and returns its (player, room) messages
    /// to accumulate. Sync so the swing loop can interleave melee and casts without going async.</param>
    public enum MonsterVsMonsterOutcome { Miss, Dodge, Glance, Hit, Kill }

    public readonly record struct MonsterVsMonsterResult(MonsterVsMonsterOutcome Outcome, int Damage);

    /// <summary>
    /// One monster swings at another (used by player-pets vs
    /// hostile monsters). Reuses the shared <see cref="CalculateAttack"/> math: the attacker's
    /// effective accuracy + damage band vs the defender's effective AC / dodge / DR. Applies the
    /// damage and reports the outcome so the caller can render the stock third-person room lines
    /// ("X just attacked Y.", glanced/dodged/missed/killed). Single swing per call, matching stock.
    /// </summary>
    public static MonsterVsMonsterResult MonsterVsMonsterAttack(MonsterInstance attacker, MonsterInstance defender)
    {
        if (attacker.IsDead || defender.IsDead || !CanMonsterRetaliate(attacker.Template))
            return new MonsterVsMonsterResult(MonsterVsMonsterOutcome.Miss, 0);

        int defAC = defender.EffectiveArmourClass;
        int defDodge = defender.EffectiveDodge;
        int defDodgeSkill = defender.EffectiveDodgeSkill;
        int defDR = GetEffectiveMonsterDamageResist(defender);

        int accuracy;
        int minDmg;
        int maxDmg;
        if (attacker.Template.Attacks.Count > 0)
        {
            var attack = SelectMonsterAttack(attacker.Template.Attacks);
            if (attack == null)
                return new MonsterVsMonsterResult(MonsterVsMonsterOutcome.Miss, 0);
            (minDmg, maxDmg) = GetEffectiveMonsterDamageBounds(attacker, attack.Min, attack.Max);
            accuracy = GetEffectiveMonsterAccuracy(attacker, attack.Accuracy);
        }
        else
        {
            int defaultMax = Math.Max(1, (int)(attacker.Template.AvgDmg * 2));
            (minDmg, maxDmg) = GetEffectiveMonsterDamageBounds(attacker, 1, defaultMax);
            accuracy = GetEffectiveMonsterAccuracy(attacker, 0);
        }

        var calc = CalculateAttack(accuracy, defAC, defDodge, minDmg, maxDmg, 0, AttackType.Normal, defDR, defenderDodgeSkill: defDodgeSkill, attackerDefenseless: IsSmashDefenseless(attacker), defenderDefenseless: IsSmashDefenseless(defender));
        if (calc.Missed)
            return new MonsterVsMonsterResult(calc.Dodged ? MonsterVsMonsterOutcome.Dodge : MonsterVsMonsterOutcome.Miss, 0);
        if (calc.Glanced)
            return new MonsterVsMonsterResult(MonsterVsMonsterOutcome.Glance, 0);

        int damage = Math.Max(0, calc.Damage);
        defender.CurrentHP -= damage;
        return defender.IsDead
            ? new MonsterVsMonsterResult(MonsterVsMonsterOutcome.Kill, damage)
            : new MonsterVsMonsterResult(MonsterVsMonsterOutcome.Hit, damage);
    }

    public static CombatResult MonsterAttack(MonsterInstance monster, Player player, Dictionary<int, Item>? items = null, IReadOnlyDictionary<int, RoomMessage>? messages = null,
        Func<int, int, (List<string> Player, List<string> Room)>? resolveSpellAttack = null, IReadOnlyList<RetaliationSource>? defenderRetaliation = null)
    {
        var result = new CombatResult();
        if (!CanMonsterRetaliate(monster.Template))
            return result;

        // A monster with ability 185 ("Do not Attack if Item Nmbr")
        // makes NO swing against a player holding the matching emblem — it bails and drops autocombat.
        // This is the gang-house safe-passage gate, and it sits at the swing so it also suppresses the
        // departing free attack and ongoing combat rounds, not merely the on-sight aggro decision.
        if (PlayerHoldsMonsterPacifier(monster.Template, player))
            return result;

        bool wasConscious = player.CurrentHP > 0;
        int playerAC = player.GetTotalAC() + player.PartyDefenceModifier;
        int playerDodge = player.GetDodge();
        int playerDR = ScaleDamageResist(player.GetTotalDR());

        // Resolve weapon type for attack verbs
        int weaponType = -1; // -1 = natural/no weapon
        if (monster.Template.Weapon > 0 && items != null &&
            items.TryGetValue(monster.Template.Weapon, out var monWeapon))
        {
            weaponType = monWeapon.WeaponType;
        }

        // Stock format: "The %s %s you for %d damage!" — but a display name that ALREADY leads with an
        // article takes it as given rather than doubling it. Stock has one such monster, #251 "The Grey
        // Lord", which read "The The Grey Lord swings at you"; the monster-cast lines have always
        // applied this rule (GetMonsterCastDisplayName), and the melee lines must match.
        string monName = monster.DisplayName.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? monster.DisplayName
            : $"The {monster.DisplayName}";

        if (monster.Template.Attacks.Count == 0)
        {
            monster.CurrentEnergy = Math.Max(0, monster.CurrentEnergy - monster.GetCombatEnergyCap());

            int defaultMax = Math.Max(1, (int)(monster.Template.AvgDmg * 2));
            var (defaultMinDamage, defaultMaxDamage) = GetEffectiveMonsterDamageBounds(monster, 1, defaultMax);
            var calc = CalculateAttack(GetEffectiveMonsterAccuracy(monster, 0), playerAC, playerDodge, defaultMinDamage, defaultMaxDamage, 0, AttackType.Normal, defenderDodgeSkill: player.GetCombatDodgeSkill(), attackerDefenseless: IsSmashDefenseless(monster), defenderDefenseless: IsSmashDefenseless(player));
            int rawDamage = ClampMonsterDamageRoll(calc.Damage, defaultMinDamage, defaultMaxDamage);
            int damage = calc.Missed ? 0 : Math.Max(0, rawDamage - playerDR);

            if (damage > 0)
            {
                player.CurrentHP -= damage;
                player.RecordDamageSource(monName);   // death log — see the PvP site above
                result.TotalDamage = damage;
                result.Hits = 1;
                string verb = PluralVerb(GetMonsterAttackVerb(weaponType));
                result.Messages.Add(GameAnsi.CombatHit($"{monName} {verb} you for {damage} damage!"));
                result.RoomMessages.Add(GameAnsi.CombatHit($"{monName} {verb} {player.Name} for {damage} damage!"));

                ApplyRetaliationToMonsterAttacker(monster, defenderRetaliation, result, messages);

                if (wasConscious && player.CurrentHP <= 0)
                {
                    result.Messages.Add(GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                    // The drop line broadcasts with no excluded user, so the
                    // whole room sees it. Without this the drop was private to the person going down.
                    result.RoomMessages.Add(GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                    wasConscious = false;
                }
            }
            else if (calc.Dodged)
            {
                // Skill-dodge (outcome 3): "...but you dodge!"
                result.Messages.Add(GameAnsi.Dodge(GetPlayerDodgeMessage(monName, weaponType, items, monster)));
                result.RoomMessages.Add(GameAnsi.Dodge(GetObserverDodgeMessage(monName, items, monster, player)));
            }
            else
            {
                // Plain to-hit miss (outcome 0): the attack lands nowhere, NO dodge clause.
                result.Messages.Add(GameAnsi.CombatMiss(GetPlayerMissMessage(monName, weaponType, items, monster)));
                result.RoomMessages.Add(GameAnsi.CombatMiss(GetObserverMissMessage(monName, items, monster, player)));
            }
        }
        else
        {
            for (int swingIndex = 0; swingIndex < MAX_SWINGS && player.CurrentHP > Player.DeathHP; swingIndex++)
            {
                var attack = SelectMonsterAttack(monster.Template.Attacks);
                if (attack == null)
                    break;

                int energyCost = GetMonsterAttackEnergyCost(monster, attack);
                if (monster.CurrentEnergy < energyCost)
                    break;

                monster.CurrentEnergy -= energyCost;

                // The monster attack loop: AtkType selects the action — 1 = melee swing,
                // 2 = cast the spell in AtkAcc (its AtkAcc/AtkMin/AtkMax are spell-id/success%/level,
                // NOT melee fields), 3 = rob (unused in data). Casts resolve synchronously so a round
                // can interleave melee and spells across swings.
                if (attack.Type == MonsterCastAttackType)
                {
                    if (resolveSpellAttack != null)
                    {
                        var (castPlayerMessages, castRoomMessages) = resolveSpellAttack(attack.Accuracy, attack.Max);
                        result.Messages.AddRange(castPlayerMessages);
                        result.RoomMessages.AddRange(castRoomMessages);

                        // See AppendPlayerKillMessages: the drop line marks the conscious →
                        // unconscious-but-alive transition only. A blow that takes the player straight
                        // past DeathHP is a kill, and stock prints the death lines for that instead.
                        if (wasConscious && player.CurrentHP <= 0)
                        {
                            if (player.CurrentHP > Player.DeathHP)
                                result.Messages.Add(GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                                // The drop line broadcasts with no excluded user, so the
                                // whole room sees it. Without this the drop was private to the person going down.
                                result.RoomMessages.Add(GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                            wasConscious = false;
                        }
                    }
                }
                else if (attack.Type == MonsterRobAttackType)
                {
                    break;
                }
                else
                {
                    var (attackMinDamage, attackMaxDamage) = GetEffectiveMonsterDamageBounds(monster, attack.Min, attack.Max);
                    int attackAccuracy = GetEffectiveMonsterAccuracy(monster, attack.Accuracy);
                    var calc = CalculateAttack(attackAccuracy, playerAC, playerDodge, attackMinDamage, attackMaxDamage, 0, AttackType.Normal, defenderDodgeSkill: player.GetCombatDodgeSkill(), attackerDefenseless: IsSmashDefenseless(monster), defenderDefenseless: IsSmashDefenseless(player));
                    int rawDamage = ClampMonsterDamageRoll(calc.Damage, attackMinDamage, attackMaxDamage);
                    int damage = calc.Missed ? 0 : Math.Max(0, rawDamage - playerDR);

                    if (damage > 0)
                    {
                        player.CurrentHP -= damage;
                        player.RecordDamageSource(monName);
                        result.TotalDamage += damage;
                        result.Hits++;
                        // The hit-spell is read from THIS slot
                        // inside the swing loop, so it procs only when this slot is the one that landed.
                        if (attack.HitSpell > 0)
                            result.PendingHitSpells.Add(attack.HitSpell);
                        if (!TryAddMonsterHitMessages(result, attack, monster, player, damage, messages))
                        {
                            string verb = PluralVerb(GetMonsterAttackVerb(weaponType));
                            result.Messages.Add(GameAnsi.CombatHit($"{monName} {verb} you for {damage} damage!"));
                            result.RoomMessages.Add(GameAnsi.CombatHit($"{monName} {verb} {player.Name} for {damage} damage!"));
                        }

                        ApplyRetaliationToMonsterAttacker(monster, defenderRetaliation, result, messages);

                        if (wasConscious && player.CurrentHP <= 0)
                        {
                            result.Messages.Add(GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                            // The drop line broadcasts with no excluded user, so the
                            // whole room sees it. Without this the drop was private to the person going down.
                            result.RoomMessages.Add(GameAnsi.DropsToTheGround($"{player.Name} drops to the ground!"));
                            wasConscious = false;
                        }
                    }
                    else
                    {
                        // Stock renders a to-hit miss (outcome 0), a
                        // skill-dodge (outcome 3) and a glance (outcome 1) as THREE distinct messages.
                        // Only the dodge says "...but you dodge!"; a plain miss must not.
                        var outcome = calc.Dodged ? MonsterNoDamageOutcome.Dodged
                            : calc.Missed ? MonsterNoDamageOutcome.Missed
                            : MonsterNoDamageOutcome.Glanced;

                        if (!TryAddMonsterNoDamageMessages(result, attack, monster, player, outcome, items, messages))
                        {
                            if (outcome == MonsterNoDamageOutcome.Glanced)
                            {
                                AppendMonsterGlanceFallbackMessages(result, monster, player, items, messages);
                            }
                            else if (outcome == MonsterNoDamageOutcome.Dodged)
                            {
                                result.Messages.Add(GameAnsi.Dodge(GetPlayerDodgeMessage(monName, weaponType, items, monster)));
                                result.RoomMessages.Add(GameAnsi.Dodge(GetObserverDodgeMessage(monName, items, monster, player)));
                            }
                            else
                            {
                                result.Messages.Add(GameAnsi.CombatMiss(GetPlayerMissMessage(monName, weaponType, items, monster)));
                                result.RoomMessages.Add(GameAnsi.CombatMiss(GetObserverMissMessage(monName, items, monster, player)));
                            }
                        }
                    }
                }
            }
        }

        if (player.CurrentHP <= 0)
        {
            result.TargetKilled = true;
            if (player.CurrentHP <= Player.DeathHP)
            {
                result.Messages.Add(GameAnsi.KilledOutright("You have been killed!"));
            }
        }

        return result;
    }

    private static bool TryAddMonsterHitMessages(CombatResult result, MonsterAttack attack, MonsterInstance monster, Player player, int damage, IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        if (attack.HitMessageId <= 0 || messages == null || !messages.TryGetValue(attack.HitMessageId, out var template))
            return false;

        bool addedPlayerMessage = TryAddFormattedCombatMessage(result.Messages, template.Line1, TemplateMonsterName(template.Line1, monster), damage);
        bool addedRoomMessage = TryAddFormattedCombatMessage(result.RoomMessages, template.Line2, TemplateMonsterName(template.Line2, monster), player.Name, damage.ToString(CultureInfo.InvariantCulture));
        return addedPlayerMessage || addedRoomMessage;
    }

    private enum MonsterNoDamageOutcome { Glanced, Dodged, Missed }

    // Stock distinguishes three no-damage outcomes, drawing from two
    // monster-attack message records — the dodge record (AtkDodgeMsg) and the miss record
    // (AtkMissMsg). Each record holds three lines (player-glance/room-glance/player-dodge for the
    // dodge record; room-dodge/player-miss/room-miss for the miss record):
    //   GLANCE (type 1): dodge.Line1 (player, "armour deflects"), dodge.Line2 (room)
    //   DODGE  (type 3): dodge.Line3 (player, "...but you dodge!"), miss.Line1 (room, "...dodges")
    //   MISS   (type 0): miss.Line2  (player, plain "...!"),         miss.Line3 (room)
    // The previous code routed both miss and dodge through dodge.Line3, so a plain to-hit miss read
    // "...but you dodge!" on every swing (the kobold-thief repro).
    private static bool TryAddMonsterNoDamageMessages(
        CombatResult result,
        MonsterAttack attack,
        MonsterInstance monster,
        Player player,
        MonsterNoDamageOutcome outcome,
        IReadOnlyDictionary<int, Item>? items,
        IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        if (messages == null)
            return false;

        RoomMessage? dodge = attack.DodgeMessageId > 0 && messages.TryGetValue(attack.DodgeMessageId, out var d) ? d : null;
        RoomMessage? miss = attack.MissMessageId > 0 && messages.TryGetValue(attack.MissMessageId, out var m) ? m : null;

        bool addedPlayer = false;
        bool addedRoom = false;

        // The three outcomes are coloured from TWO different prefixes: the glance (type 1) prints
        // ESC[0;31m (DarkRed), while the dodge (type 3) and the plain miss (type 0)
        // print ESC[0;36m (Cyan). Verified byte-for-byte against stock: the
        // "armour deflects the blow" branch carries the first prefix and
        // the dodge/miss branches the second.
        Func<string, string> style = outcome == MonsterNoDamageOutcome.Glanced
            ? GameAnsi.CombatGlance
            : GameAnsi.Dodge;

        bool AddPlayer(string? line) => line != null && TryAddFormattedMonsterNoDamageMessage(result.Messages, line, style, attack, monster, player, items, messages, MessageAudience.Player);
        bool AddRoom(string? line) => line != null && TryAddFormattedMonsterNoDamageMessage(result.RoomMessages, line, style, attack, monster, player, items, messages, MessageAudience.Room);

        switch (outcome)
        {
            case MonsterNoDamageOutcome.Glanced:
                addedPlayer = AddPlayer(dodge?.Line1);
                addedRoom = AddRoom(dodge?.Line2);
                break;
            case MonsterNoDamageOutcome.Dodged:
                addedPlayer = AddPlayer(dodge?.Line3);
                addedRoom = AddRoom(miss?.Line1);
                break;
            case MonsterNoDamageOutcome.Missed:
                addedPlayer = AddPlayer(miss?.Line2);
                addedRoom = AddRoom(miss?.Line3);
                break;
        }

        return addedPlayer || addedRoom;
    }

    private enum MessageAudience
    {
        Player,
        Room,
    }

    /// <summary>
    /// The monster name to substitute into an AUTHORED message template. Stock templates carry the
    /// article themselves — "The %s bites you for %d damage!" (msg 27), "The %s %slunges at %s!"
    /// (msg 8294) — so the name that fills the slot must be bare, which it almost always is. Stock has
    /// one monster whose own name leads with the article, #251 "The Grey Lord", and a substituted or
    /// summoned display name can too; those rendered "The The Grey Lord bites you for 12 damage!".
    /// Same rule the monster-cast lines use (ArticleCasterNameForLine): drop the name's article when the
    /// template already supplies one, keep it when the template starts at the bare name.
    /// </summary>
    private static string TemplateMonsterName(string? templateLine, MonsterInstance monster)
    {
        string name = monster.DisplayName;
        if (!name.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
            return name;

        return (templateLine ?? string.Empty).TrimStart().StartsWith("the ", StringComparison.OrdinalIgnoreCase)
            ? name[4..]
            : name;
    }

    private static bool TryAddFormattedCombatMessage(List<string> output, string template, params object?[] args)
    {
        return TryAddFormattedStyledMessage(output, template, GameAnsi.CombatHit, args);
    }

    private static bool TryAddFormattedMonsterNoDamageMessage(
        List<string> output,
        string template,
        Func<string, string> style,
        MonsterAttack attack,
        MonsterInstance monster,
        Player player,
        IReadOnlyDictionary<int, Item>? items,
        IReadOnlyDictionary<int, RoomMessage> messages,
        MessageAudience audience)
    {
        return TryAddFormattedStyledMessage(output, template, style, BuildMonsterNoDamageMessageArgs(template, attack, monster, player, items, messages, audience));
    }

    private static object?[] BuildMonsterNoDamageMessageArgs(
        string template,
        MonsterAttack attack,
        MonsterInstance monster,
        Player player,
        IReadOnlyDictionary<int, Item>? items,
        IReadOnlyDictionary<int, RoomMessage> messages,
        MessageAudience audience)
    {
        string monsterName = TemplateMonsterName(template, monster);
        int placeholderCount = CountFormatPlaceholders(template);

        // The second %s is only an attack-verb slot when it stands alone (e.g. msg 8466
        // "%s %s you, but you dodge out of the way!"). Most stock dodge/miss templates bake the
        // verb into the text behind a glued adverb-prefix slot — e.g. msg 8308 L3
        // "The %s %sclaws at you, %sbut you dodge out of the way!" — where that %s is an
        // (almost always empty) adverb prefix. Injecting a verb there produced the doubled
        // "The wight slashesclaws at you, ...". When the slot is glued, leave it blank.
        bool verbSlot = SecondPlaceholderIsVerbSlot(template);

        if (UsesExplicitMonsterWeaponPlaceholder(template))
        {
            string verb = verbSlot ? GetMonsterMessageAttackVerb(attack, monster, items, messages) : string.Empty;
            string weaponName = GetMonsterWeaponName(monster, items);

            return placeholderCount switch
            {
                3 => [monsterName, verb, weaponName],
                4 => audience == MessageAudience.Player
                    ? [monsterName, verb, "you", weaponName]
                    : [monsterName, verb, player.Name, weaponName],
                5 => audience == MessageAudience.Player
                    ? [monsterName, verb, "you", weaponName, "you"]
                    : [monsterName, verb, player.Name, weaponName, player.Name],
                _ => [monsterName, verb, player.Name, weaponName, player.Name],
            };
        }

        // A standalone second placeholder (template 8466 L3 "%s %s you, but you dodge out of the
        // way!") is a real verb slot; leaving it blank produced "barmaid  you, but you dodge out of
        // the way!". GetMonsterMessageAttackVerb pulls the verb from the hit template ("smacks") or
        // the weapon type. A glued slot keeps its baked-in verb and gets an empty prefix instead.
        string attackVerb = verbSlot ? GetMonsterMessageAttackVerb(attack, monster, items, messages) : string.Empty;

        return audience == MessageAudience.Player
            ? placeholderCount switch
            {
                <= 1 => [monsterName],
                2 => [monsterName, attackVerb],
                3 => [monsterName, attackVerb, string.Empty],
                4 => [monsterName, attackVerb, string.Empty, string.Empty],
                _ => [monsterName, attackVerb, string.Empty, string.Empty, string.Empty],
            }
            : placeholderCount switch
            {
                <= 1 => [monsterName],
                2 => [monsterName, attackVerb],
                3 => [monsterName, attackVerb, player.Name],
                4 => [monsterName, attackVerb, player.Name, string.Empty],
                _ => [monsterName, attackVerb, player.Name, string.Empty, player.Name],
            };
    }

    private static bool UsesExplicitMonsterWeaponPlaceholder(string template)
    {
        return template.Contains("with their %s", StringComparison.OrdinalIgnoreCase)
            || template.Contains("with his %s", StringComparison.OrdinalIgnoreCase)
            || template.Contains("with her %s", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountFormatPlaceholders(string template)
    {
        int count = 0;
        for (int index = 0; index < template.Length - 1; index++)
        {
            if (template[index] == '%' && template[index + 1] is 's' or 'd')
            {
                count++;
                index++;
            }
        }

        return count;
    }

    /// <summary>
    /// True when the SECOND format placeholder is a standalone "%s" attack-verb slot (followed by
    /// whitespace/punctuation/end), e.g. msg 8466 "%s %s you, ...". False when it is glued to a
    /// following letter (e.g. "%sclaws" in msg 8308), which marks an adverb-prefix slot whose verb
    /// is already baked into the template — injecting a verb there yields "slashesclaws".
    /// </summary>
    private static bool SecondPlaceholderIsVerbSlot(string template)
    {
        int seen = 0;
        for (int i = 0; i + 1 < template.Length; i++)
        {
            if (template[i] != '%' || template[i + 1] is not ('s' or 'd'))
                continue;

            if (++seen == 2)
            {
                if (template[i + 1] != 's')
                    return false; // a %d is never a verb slot
                int after = i + 2;
                return after >= template.Length || !char.IsLetter(template[after]);
            }

            i++; // skip the placeholder's second char
        }

        return false; // fewer than two placeholders: index 1 is never used as a verb
    }

    private static string GetMonsterMessageAttackVerb(MonsterAttack attack, MonsterInstance monster, IReadOnlyDictionary<int, Item>? items, IReadOnlyDictionary<int, RoomMessage> messages)
    {
        if (attack.HitMessageId > 0
            && messages.TryGetValue(attack.HitMessageId, out var hitTemplate)
            && TryExtractMonsterAttackVerb(hitTemplate.Line1, out string verb))
        {
            return verb;
        }

        return PluralVerb(GetMonsterAttackVerb(GetMonsterWeaponType(monster, items)));
    }

    private static bool TryExtractMonsterAttackVerb(string template, out string verb)
    {
        verb = string.Empty;
        if (string.IsNullOrWhiteSpace(template))
            return false;

        var match = Regex.Match(template, @"%s\s+(?<verb>[^%]*?)\s+you\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        string candidate = MudText.CollapseSpaces(match.Groups["verb"].Value).Trim();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('%', StringComparison.Ordinal))
            return false;

        verb = candidate;
        return true;
    }

    private static string GetMonsterWeaponName(MonsterInstance monster, IReadOnlyDictionary<int, Item>? items)
    {
        return monster.Template.Weapon > 0
            && items != null
            && items.TryGetValue(monster.Template.Weapon, out var weapon)
            && !string.IsNullOrWhiteSpace(weapon.Name)
                ? weapon.Name
                : "weapon";
    }

    private static int GetMonsterWeaponType(MonsterInstance monster, IReadOnlyDictionary<int, Item>? items)
    {
        return monster.Template.Weapon > 0
            && items != null
            && items.TryGetValue(monster.Template.Weapon, out var weapon)
                ? weapon.WeaponType
                : -1;
    }

    private static bool TryAddFormattedStyledMessage(List<string> output, string template, Func<string, string> formatter, params object?[] args)
    {
        string formatted = FormatMessageTemplate(template, args);
        if (string.IsNullOrWhiteSpace(formatted))
            return false;

        output.Add(formatter(formatted));
        return true;
    }

    private static string FormatMessageTemplate(string template, params object?[] args)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var builder = new StringBuilder(template.Length + 32);
        int argIndex = 0;

        for (int index = 0; index < template.Length; index++)
        {
            if (template[index] == '%' && index + 1 < template.Length)
            {
                char marker = template[index + 1];
                if (marker is 's' or 'd')
                {
                    string replacement = argIndex < args.Length
                        ? Convert.ToString(args[argIndex], CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty;
                    builder.Append(replacement);
                    argIndex++;
                    index++;
                    continue;
                }
            }

            builder.Append(template[index]);
        }

        return MudText.CollapseSpaces(builder.ToString()).Trim();
    }

    private static string GetPossessiveName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        return name.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            ? $"{name}'"
            : $"{name}'s";
    }

    // Miss verbs: fallback generic verbs when the weapon has no MissMsg defined
    // Stock format: "You {verb} {monster}!" where verb includes "at" (e.g., "swing at")
    private static readonly string[] PlayerMissVerbs = ["swing at", "lunge at", "swipe at"];

    private static string GetPlayerMissVerb()
    {
        return PlayerMissVerbs[_rng.Next(PlayerMissVerbs.Length)];
    }

    private static string ConjugateThirdPersonPhrase(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return phrase;

        int spaceIndex = phrase.IndexOf(' ');
        if (spaceIndex < 0)
            return ConjugateThirdPersonVerb(phrase);

        string firstWord = phrase[..spaceIndex];
        string remainder = phrase[spaceIndex..];
        return ConjugateThirdPersonVerb(firstWord) + remainder;
    }

    private static string ConjugateThirdPersonVerb(string verb)
    {
        if (string.IsNullOrWhiteSpace(verb))
            return verb;

        if (verb.EndsWith('y') && verb.Length > 1 && !"aeiou".Contains(char.ToLowerInvariant(verb[^2])))
            return verb[..^1] + "ies";

        if (verb.EndsWith("sh", StringComparison.OrdinalIgnoreCase) ||
            verb.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
            verb.EndsWith('s') ||
            verb.EndsWith('x') ||
            verb.EndsWith('z') ||
            verb.EndsWith('o'))
        {
            return verb + "es";
        }

        return verb + "s";
    }

    /// <summary>
    /// Get the miss verb for a weapon from its MissMsg → Messages table lookup.
    /// Messages Line1 contains the 1st-person miss verb (e.g., "lunge at", "swipe at").
    /// Falls back to generic miss verbs if no weapon or no MissMsg defined.
    /// </summary>
    private static string GetWeaponMissVerb(Item? weapon, Dictionary<int, RoomMessage>? messages)
    {
        if (weapon != null && weapon.MissMsg > 0 && messages != null &&
            messages.TryGetValue(weapon.MissMsg, out var msg) &&
            !string.IsNullOrWhiteSpace(msg.Line1))
        {
            return msg.Line1.Trim();
        }
        return GetPlayerMissVerb();
    }

    /// <summary>
    /// A weapon's hit phrase in three grammatical persons, all picked at ONE aligned pipe-index
    /// from the weapon's HitMsg → Messages triple:
    ///   Line1 = attacker/self  ("hurl your nexus spear at")   — 2nd-person "your"
    ///   Line2 = victim/target  ("hurls the nexus spear at")   — the "you" recipient's view
    ///   Line3 = room bystander ("hurls their nexus spear at") — 3rd-person "their"
    /// Reading all three at the same index keeps the chosen variant consistent across audiences,
    /// and — crucially — lets a bystander see "their" instead of a conjugated-but-still-2nd-person
    /// "your", which Megamud otherwise mis-parses as a PVP action against the viewer.
    /// </summary>
    internal readonly record struct WeaponHitPhrase(string Self, string Target, string Room);

    /// <summary>
    /// Pick a weapon's hit phrase (self / victim / room) from its HitMsg → Messages table lookup.
    /// Line1/Line2/Line3 carry parallel pipe-delimited variants in the three persons. Falls back to
    /// conjugating the self phrase when an authored 3rd-person line is absent, and to weapon-type
    /// verb arrays when the weapon has no HitMsg. Called ONCE per round so every swing reads alike.
    /// </summary>
    private static WeaponHitPhrase GetWeaponHitPhrase(Item? weapon, Dictionary<int, RoomMessage>? messages, int weaponType)
    {
        if (weapon != null && weapon.HitMsg > 0 && messages != null &&
            messages.TryGetValue(weapon.HitMsg, out var msg) &&
            !string.IsNullOrWhiteSpace(msg.Line1))
        {
            var self = SplitVerbVariants(msg.Line1);
            if (self.Length > 0)
            {
                int i = _rng.Next(self.Length);
                var victim = SplitVerbVariants(msg.Line2);
                var room = SplitVerbVariants(msg.Line3);
                string selfPhrase = self[i];
                string victimPhrase = i < victim.Length ? victim[i] : ConjugateThirdPersonPhrase(selfPhrase);
                string roomPhrase = i < room.Length ? room[i] : ConjugateThirdPersonPhrase(selfPhrase);
                return new WeaponHitPhrase(selfPhrase, victimPhrase, roomPhrase);
            }
        }

        string verb = weapon == null ? "punch" : GetPlayerAttackVerb(weaponType);
        string third = ConjugateThirdPersonPhrase(verb);
        return new WeaponHitPhrase(verb, third, third);
    }

    private static string[] SplitVerbVariants(string? line)
        => string.IsNullOrWhiteSpace(line)
            ? []
            : line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Player attack verb sets by weapon type, matching imported item WeaponType values.
    // WeaponType: 0 = 1H Blunt, 1 = 2H Blunt, 2 = 1H Sharp, 3 = 2H Sharp, others = Natural/Fists
    private static readonly string[] PlayerBluntVerbs = ["pound", "smash", "crush", "clobber", "slam", "bludgeon"];
    private static readonly string[] PlayerSharpVerbs = ["slash", "hack", "slice", "cut", "impale", "cleave"];
    private static readonly string[] PlayerNaturalVerbs = ["whap", "beat", "strike", "smack", "thwack", "clobber"];

    /// <summary>Get a random player attack verb based on weapon type (stock verb sets).</summary>
    private static string GetPlayerAttackVerb(int weaponType)
    {
        return weaponType switch
        {
            0 or 1 => PlayerBluntVerbs[_rng.Next(PlayerBluntVerbs.Length)],
            2 or 3 => PlayerSharpVerbs[_rng.Next(PlayerSharpVerbs.Length)],
            _ => PlayerNaturalVerbs[_rng.Next(PlayerNaturalVerbs.Length)],
        };
    }

    /// <summary>
    /// Build the player-dodge message matching the stock format:
    /// "The {monster} lunges at you with their {weapon}, but you dodge!"
    /// For monsters without weapons: "The {monster} lunges at you, but you dodge!"
    /// </summary>
    private static string GetPlayerDodgeMessage(string monName, int weaponType, Dictionary<int, Item>? items, MonsterInstance monster)
    {
        if (monster.Template.Weapon > 0 && items != null &&
            items.TryGetValue(monster.Template.Weapon, out var w))
        {
            // Stock format: "The nasty kobold thief lunges at you with their shortsword, but you dodge!"
            return $"{monName} lunges at you with their {w.Name}, but you dodge!";
        }
        // Natural/no weapon: "The lashworm lunges at you, but you dodge!"
        return $"{monName} lunges at you, but you dodge!";
    }

    private static string GetObserverDodgeMessage(string monName, Dictionary<int, Item>? items, MonsterInstance monster, Player player)
    {
        if (monster.Template.Weapon > 0 && items != null &&
            items.TryGetValue(monster.Template.Weapon, out var w))
        {
            return $"{monName} lunges at {player.Name} with their {w.Name}, but {player.Name} dodges!";
        }

        return $"{monName} lunges at {player.Name}, but {player.Name} dodges!";
    }

    /// <summary>
    /// A plain to-hit MISS (outcome type 0) — the same lead as the dodge message
    /// but WITHOUT the ", but you dodge!" clause. Stock fallback string "%s %s you with %s!".
    /// Distinguishing this from a skill-dodge is what keeps every avoided swing from reading
    /// "...but you dodge!".
    /// </summary>
    private static string GetPlayerMissMessage(string monName, int weaponType, Dictionary<int, Item>? items, MonsterInstance monster)
    {
        if (monster.Template.Weapon > 0 && items != null &&
            items.TryGetValue(monster.Template.Weapon, out var w))
        {
            return $"{monName} lunges at you with their {w.Name}!";
        }

        return $"{monName} lunges at you!";
    }

    private static string GetObserverMissMessage(string monName, Dictionary<int, Item>? items, MonsterInstance monster, Player player)
    {
        if (monster.Template.Weapon > 0 && items != null &&
            items.TryGetValue(monster.Template.Weapon, out var w))
        {
            return $"{monName} lunges at {player.Name} with their {w.Name}!";
        }

        return $"{monName} lunges at {player.Name}!";
    }

    /// <summary>
    /// The monster-side attack token kept in the fighter struct. The marshal fills it
    ///
    /// from the monster's EQUIPPED WEAPON — NOT from the attack slot:
    ///   weapon.MissMsg -> Messages.Line2   (3rd-person, e.g. "swings at")
    ///   weapon.HitMsg  -> Messages.Line2   (only when the weapon carries no MissMsg record)
    ///   ""                                 (monster carries no weapon — the buffer is zeroed)
    /// The sibling fields are Line1 (1st person) and Line3 (3rd person, room),
    /// plus the weapon name; all four are read only by the no-message-record fallbacks.
    /// Stock MissMsg Line2 values carry no pipe variants, but HitMsg lines do ("slashes|impales|
    /// hacks"), so pick one the same way every other verb site does rather than printing the raw
    /// pipe form, which is never displayed verbatim anywhere in stock.
    /// </summary>
    private static string GetMonsterWeaponVerbToken(
        MonsterInstance monster,
        Dictionary<int, Item>? items,
        IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        if (monster.Template.Weapon <= 0 || items == null || messages == null ||
            !items.TryGetValue(monster.Template.Weapon, out var weapon))
        {
            return string.Empty;
        }

        string? line = null;
        if (weapon.MissMsg > 0 && messages.TryGetValue(weapon.MissMsg, out var missMsg) &&
            !string.IsNullOrWhiteSpace(missMsg.Line2))
        {
            line = missMsg.Line2;
        }
        else if (weapon.HitMsg > 0 && messages.TryGetValue(weapon.HitMsg, out var hitMsg) &&
                 !string.IsNullOrWhiteSpace(hitMsg.Line2))
        {
            line = hitMsg.Line2;
        }

        var variants = SplitVerbVariants(line);
        return variants.Length == 0 ? string.Empty : variants[_rng.Next(variants.Length)];
    }

    /// <summary>
    /// The glance (no-damage) lines a monster attack prints when its slot has NO AtkDodgeMsg record
    /// — 49 of the 2272 live attack slots in stock, including the kobold thief, skeleton, zombie and
    /// giant rat. In the glance branch both lines are prefixed with
    /// DarkRed and first-character-uppercased:
    ///   player: "%s's %s hits you, but your armour deflects."
    ///           args: monster name, verb token
    ///   room:   "%s's %s hits %s, but glances off %s armour."
    ///           args: monster name, verb token, player name, player possessive
    /// The name is the BARE monster name, not "The {name}" — authored message records supply their
    /// own "The " prefix, these literals do not, which is exactly why stock uppercases the first
    /// character here. Reproduced verbatim, warts included: a weaponless monster leaves the verb
    /// token empty and stock prints the resulting double space ("Skeleton's  hits you, ...").
    /// </summary>
    private static void AppendMonsterGlanceFallbackMessages(
        CombatResult result,
        MonsterInstance monster,
        Player player,
        Dictionary<int, Item>? items,
        IReadOnlyDictionary<int, RoomMessage>? messages)
    {
        string verb = GetMonsterWeaponVerbToken(monster, items, messages);
        string pronoun = GetPossessivePronoun(player);

        result.Messages.Add(GameAnsi.CombatGlance(
            UpperFirstCharacter($"{monster.DisplayName}'s {verb} hits you, but your armour deflects.")));
        result.RoomMessages.Add(GameAnsi.CombatGlance(
            UpperFirstCharacter($"{monster.DisplayName}'s {verb} hits {player.Name}, but glances off {pronoun} armour.")));
    }

    // Uppercase the first byte of the assembled line, used by the
    // fallback combat literals that carry no leading article of their own.
    private static string UpperFirstCharacter(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // Player-as-attacker dodge text, matching the stock outcome-3 strings.
    // PvE self view: "You swing at X who dodges your attack!"
    private static string GetAttackerDodgeSelfMessage(string missVerb, string targetName)
        => $"You {missVerb} {targetName} who dodges your attack!";

    // Room view of an attacker's blow being dodged: "Bob swings at X but it dodges."
    private static string GetAttackerDodgeRoomMessage(string actorName, string missVerb, string targetName)
        => $"{actorName} {ConjugateThirdPersonPhrase(missVerb)} {targetName} but it dodges.";

    /// <summary>Get wound status description string from current/max HP (stock format).</summary>
    public static string GetWoundLevel(int currentHP, int maxHP)
    {
        if (currentHP <= 0) return "mortally wounded";
        double pct = (double)currentHP / maxHP;
        return pct switch
        {
            >= 1.0 => "unwounded",
            >= 0.8 => "slightly wounded",
            >= 0.6 => "moderately wounded",
            >= 0.4 => "heavily wounded",
            >= 0.2 => "severely wounded",
            >= 0.1 => "critically wounded",
            _ => "very critically wounded",
        };
    }

    /// <summary>Get health description for look/scan (stock format).</summary>
    public static string GetHealthDescription(int currentHP, int maxHP)
    {
        if (currentHP >= maxHP) return "healthy";
        return GetWoundLevel(currentHP, maxHP);
    }

    /// <summary>
    /// Get death message for a monster. Uses the imported stock death message if available,
    /// otherwise falls back to a generic message.
    /// </summary>
    // A monster's death line is the Line3 of its dedicated DeathMsg
    // message, broadcast to the room — but ONLY when the DeathMsg id is non-zero and resolves to a
    // NULL`. There is NO generic fallback: a monster with DeathMsg==0, or one pointing at a missing or
    // blank message (e.g. the stock blank sentinels 1/66/121, the same ones item DestructMsg uses),
    // dies with NO death line — silently (you still see your hit + the exp award, just no death text).
    // So when LinkMonsterDeathMessages left Template.DeathMessage empty, emit nothing rather than
    // inventing an English line stock never shows.
    public static string GetDeathMessage(MonsterInstance monster)
        => monster.Template.DeathMessage ?? string.Empty;
}

public class CombatResult
{
    public int TotalDamage { get; set; }
    public int Hits { get; set; }
    public int Misses { get; set; }
    public int Crits { get; set; }
    public bool TargetKilled { get; set; }
    public bool IsBackstab { get; set; }
    public List<string> Messages { get; set; } = [];
    public List<string> TargetMessages { get; set; } = [];
    public List<string> RoomMessages { get; set; } = [];
    /// <summary>
    /// AtkHitSpell ids owed by this round, one entry per swing that actually dealt damage, in swing
    /// order. The hit-spell of the slot that CONNECTED is the one that fires —
    /// read inside the swing loop, indexed by the slot the attack roll
    /// selected — so a monster whose hit-spell sits on a low-probability secondary slot procs it only
    /// as often as that slot is chosen. The caller fires these (the cast pipeline is async, this
    /// engine is not); see CommandParser.TryFireMonsterAtkHitSpellAsync.
    /// </summary>
    public List<int> PendingHitSpells { get; set; } = [];
    /// <summary>Death message for killed monster (stock: shown after loot drops, before exp).</summary>
    public string? DeathMessage { get; set; }
    public List<int> Drops { get; set; } = [];
    public long ExpGained { get; set; }
    public int GoldDropped { get; set; }
    public int SilverDropped { get; set; }
    public int CopperDropped { get; set; }
    public int PlatinumDropped { get; set; }
    public int RunicDropped { get; set; }
    /// <summary>
    /// Weapon spell-procs queued during the swing loop, in swing order. Each records the spell to
    /// cast PLUS the message-list lengths captured at the moment the triggering swing landed, so the
    /// command layer can replay the swing lines and the proc casts INTERLEAVED — stock fires the
    /// weapon proc (ability 114) inside the per-swing hit branch, right after
    /// that swing's "You hit for N" line and before the next swing, not batched after the round.
    /// </summary>
    public List<TriggeredWeaponProc> TriggeredWeaponProcs { get; set; } = [];
}

/// <summary>A weapon proc-spell queued at a swing boundary (see CombatResult.TriggeredWeaponProcs).</summary>
public readonly record struct TriggeredWeaponProc(
    int SpellId,
    int AfterCasterMessage,
    int AfterRoomMessage,
    int AfterTargetMessage);

/// <summary>
/// Result from a single CalculateAttack call (the attack resolver output).
/// Stock layout: result type, damage, damage copy, dr, accuracy.
/// </summary>
public class CombatCalcResult
{
    public int Damage { get; set; }
    public bool Missed { get; set; }
    public bool IsCrit { get; set; }
    /// <summary>The blow landed but was fully absorbed by armour for 0 damage (result type 1).</summary>
    public bool Glanced { get; set; }
    public int HitChance { get; set; }
    public int Accuracy { get; set; }
    /// <summary>The blow would have landed but the defender's dodge rating avoided it (result type 3).</summary>
    public bool Dodged { get; set; }
}
