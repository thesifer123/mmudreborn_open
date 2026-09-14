using System.Globalization;
using System.Text;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

// `stat all <monster>` — the non-stock sheet resolved AGAINST a specific monster.
//
// The plain sheet reports your numbers in the abstract: a damage band with nothing subtracted, an
// accuracy value with nothing to compare it to. Against a named target every one of those becomes a
// concrete answer — what fraction of your swings land, what the target dodges, what its damage resist
// leaves of each hit, and how many rounds the fight takes in each direction. The arithmetic is the
// engine's own (CombatEngine.ComputeHitChance / ComputeDodgeChance and the same damage ordering
// CalculateAttack uses), never a parallel formula, so the panel cannot drift from what combat rolls.
//
// Everything here is a projection of the AVERAGE case: no crit roll (the plain sheet's QnD column
// already reports crit chance), and no per-round RNG. It is a planning tool, not a simulator — for a
// rolled sample use SYSOP SIMULATE.
public partial class CommandParser
{
    private const int StatTargetMaxRoundsShown = 999;

    /// <summary>
    /// Resolve the `stat all &lt;target&gt;` argument to a monster to measure against. A live monster in
    /// the player's own room wins — that instance carries its spawned adjective, buffs and debuffs, so
    /// "how do I fare against THIS one" answers about the thing actually in front of them. Otherwise the
    /// catalog is searched by number, then exact name, then substring, and a detached instance is built
    /// purely to hold the template's effective values (it is never placed in the world).
    /// </summary>
    private bool TryResolveStatTarget(string arg, out MonsterInstance target, out string failure)
    {
        target = null!;
        failure = string.Empty;

        var inRoom = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead)
            .ToList();

        var live = inRoom.FirstOrDefault(monster => monster.DisplayName.Equals(arg, StringComparison.OrdinalIgnoreCase))
            ?? inRoom.FirstOrDefault(monster => monster.Template.Name.Equals(arg, StringComparison.OrdinalIgnoreCase))
            ?? inRoom.FirstOrDefault(monster => monster.DisplayName.Contains(arg, StringComparison.OrdinalIgnoreCase))
            ?? inRoom.FirstOrDefault(monster => monster.Template.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
        if (live != null)
        {
            target = live;
            return true;
        }

        Monster? template = null;
        if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int monsterNumber))
            _world.Database.Monsters.TryGetValue(monsterNumber, out template);

        template ??= _world.Database.Monsters.Values.FirstOrDefault(m => m.Name.Equals(arg, StringComparison.OrdinalIgnoreCase))
            ?? _world.Database.Monsters.Values.FirstOrDefault(m => m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));

        if (template == null)
        {
            failure = $"You know of no '{arg}' to measure yourself against.";
            return false;
        }

        target = MonsterInstance.Create(template, _player.CurrentMapNumber, _player.CurrentRoomNumber);
        return true;
    }

    // A monster display name as a heading: stock names are lower-case mid-sentence forms ("acid slime"),
    // and a heading wants the leading capital. Names that already carry one ("The Grey Lord") are left be.
    private static string StatTargetHeadingName(MonsterInstance target)
    {
        string name = target.DisplayName;
        return string.IsNullOrEmpty(name) ? name : char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static string StatTargetPercent(int percent) => $"{percent}%";

    // Projected damage per round. Below 10 it keeps one decimal: a slot that lands 3% of the time for 6
    // damage really does average 0.2, and rounding that to a flat "0" reads as "harmless" next to a
    // rounds-to-kill column that plainly disagrees.
    private static string StatTargetDamage(double damage)
    {
        if (damage <= 0.0)
            return "0";

        return damage < 10.0
            ? damage.ToString("0.#", CultureInfo.InvariantCulture)
            : ((int)Math.Round(damage, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
    }

    // Rounds for `damagePerRound` to chew through `hitPoints`. A projection that deals nothing never
    // finishes, which is worth saying out loud rather than printing a huge number.
    private static string StatTargetRoundsToKill(int hitPoints, double damagePerRound)
    {
        if (damagePerRound <= 0.0)
            return "never";

        int rounds = (int)Math.Ceiling(hitPoints / damagePerRound);
        return rounds > StatTargetMaxRoundsShown
            ? $"{StatTargetMaxRoundsShown}+"
            : rounds.ToString(CultureInfo.InvariantCulture);
    }

    private static string StatTargetLine(params (int Start, string Text, string Color)[] segments)
    {
        var sb = new StringBuilder();
        int visiblePosition = 0;

        foreach (var (start, text, color) in segments)
        {
            if (start > visiblePosition)
                sb.Append(' ', start - visiblePosition);

            sb.Append(color).Append(text).Append(MudAnsi.Reset);
            visiblePosition = start + text.Length;
        }

        return sb.ToString();
    }

    private static (int Start, string Text, string Color) StatTargetLabel(int start, string text)
        => (start, text, MudAnsi.Green);

    private static (int Start, string Text, string Color) StatTargetValue(int end, string text)
        => (Math.Max(0, end - text.Length + 1), text, MudAnsi.Cyan);

    /// <summary>
    /// The target's own defensive numbers — the inputs every column below is computed from, shown so a
    /// surprising percentage can be traced back to the value that produced it.
    /// </summary>
    private async Task ShowStatTargetHeaderAsync(MonsterInstance target)
    {
        string g = MudAnsi.Green;
        string reset = MudAnsi.Reset;

        await _client.SendLineAsync(string.Empty);
        await _client.SendLineAsync($"{g}Versus {MudAnsi.BrightCyan}{target.DisplayName}{reset}{g}:{reset}");
        await _client.SendLineAsync(StatTargetLine(
            StatTargetLabel(0, "HP:"),
            StatTargetValue(12, target.MaxHP.ToString(CultureInfo.InvariantCulture)),
            StatTargetLabel(16, "AC:"),
            StatTargetValue(26, target.EffectiveArmourClass.ToString(CultureInfo.InvariantCulture)),
            StatTargetLabel(30, "DR:"),
            StatTargetValue(40, (CombatEngine.GetEffectiveMonsterDamageResist(target) / 10).ToString(CultureInfo.InvariantCulture)),
            StatTargetLabel(44, "Dodge:"),
            StatTargetValue(57, target.EffectiveDodge.ToString(CultureInfo.InvariantCulture)),
            StatTargetLabel(61, "MagicRes:"),
            StatTargetValue(76, target.EffectiveMagicResist.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>
    /// Your attacks, resolved against the target. Hit% and Dodge% come straight from the engine's own
    /// gates, and Min/Max are what survives the target's damage resist. Avg/Rnd folds both percentages
    /// into the band, so it is the damage you should EXPECT from a round rather than the damage a round
    /// deals when everything connects — which is what makes Rounds a usable "can I take this" number.
    /// </summary>
    private async Task ShowStatTargetAttacksAsync(CharacterClass cls, Item? weapon, MonsterInstance target)
    {
        string g = MudAnsi.Green;
        string reset = MudAnsi.Reset;

        await _client.SendLineAsync(string.Empty);
        await _client.SendLineAsync($"{g}Your Attacks:{reset}");
        await _client.SendLineAsync(StatTargetLine(
            StatTargetLabel(0, "Type"),
            StatTargetLabel(12, "Swings"),
            StatTargetLabel(21, "Accy"),
            StatTargetLabel(28, "Hit%"),
            StatTargetLabel(35, "Dodge%"),
            StatTargetLabel(44, "Min"),
            StatTargetLabel(50, "Max"),
            StatTargetLabel(56, "Avg/Rnd"),
            StatTargetLabel(66, "Rounds")));

        int targetDamageResist = CombatEngine.GetEffectiveMonsterDamageResist(target);

        foreach (var row in BuildAllStatsAttackRows(cls, weapon))
        {
            // A backstab is scored against the monster's HALVED AC plus twice its BSDefense
            // (GetMonsterBackstabArmourClass), not its ordinary armour class — the same value the live
            // backstab path passes to CalculateAttack.
            int defenceAc = row.AttackType == CombatEngine.AttackType.Backstab
                ? target.GetMonsterBackstabArmourClass()
                : target.EffectiveArmourClass;

            int hitPercent = CombatEngine.ComputeHitChance(
                row.AttackType,
                row.Accuracy,
                CombatEngine.GetAttackTypeAccuracyMod(row.AttackType),
                defenceAc,
                target.EffectiveDodgeSkill);

            // The dodge gate only rolls when the defender's dodge RATING is positive, so a zero-dodge
            // monster never dodges no matter what the percentage formula would produce.
            int dodgePercent = target.EffectiveDodge > 0
                ? CombatEngine.ComputeDodgeChance(row.AttackType, row.Accuracy, target.EffectiveDodge)
                : 0;

            var (minDamage, maxDamage) = ProjectAttackBoundsAgainst(row, targetDamageResist);
            double swings = EffectiveProjectedSwings(row.AverageSwings);
            double landRate = (hitPercent / 100.0) * (1.0 - (dodgePercent / 100.0));
            double perRound = swings * landRate * (((minDamage + maxDamage) / 2.0) + row.ExtraDamagePerHit);

            await _client.SendLineAsync(StatTargetLine(
                StatTargetLabel(0, row.Name),
                StatTargetValue(17, row.SwingDisplay),
                StatTargetValue(24, row.Accuracy.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(31, StatTargetPercent(hitPercent)),
                StatTargetValue(40, StatTargetPercent(dodgePercent)),
                StatTargetValue(46, minDamage.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(52, maxDamage.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(62, StatTargetDamage(perRound)),
                StatTargetValue(71, StatTargetRoundsToKill(target.MaxHP, perRound))));
        }
    }

    /// <summary>
    /// Your damage spells against the target. Land% is the complement of the stock resist roll (a spell the
    /// target cannot resist always lands), and the damage columns are what survives its magic resistance
    /// and its resistance to the spell's own element — the same two reductions the cast applies.
    /// </summary>
    private async Task ShowStatTargetSpellsAsync(MonsterInstance target)
    {
        string g = MudAnsi.Green;
        string reset = MudAnsi.Reset;

        await _client.SendLineAsync(string.Empty);
        await _client.SendLineAsync($"{g}Your Spells:{reset}");
        await _client.SendLineAsync(StatTargetLine(
            StatTargetLabel(0, "Short Name"),
            StatTargetLabel(13, "Casts"),
            StatTargetLabel(21, "Diff"),
            StatTargetLabel(28, "Land%"),
            StatTargetLabel(37, "Min"),
            StatTargetLabel(43, "Max"),
            StatTargetLabel(49, "Avg/Cast")));

        var spellRows = BuildAllStatsSpellRows();
        if (spellRows.Count == 0)
        {
            await _client.SendLineAsync($"{g}None known.{reset}");
            return;
        }

        foreach (var spellRow in spellRows)
        {
            int landPercent = 100 - GetMonsterSpellResistPercent(spellRow.Spell, target);

            string minText = string.Empty, maxText = string.Empty, averageText = string.Empty;
            if (spellRow.MinDamage.HasValue && spellRow.MaxDamage.HasValue)
            {
                int minDamage = ApplyStatTargetSpellReductions(spellRow.Spell, spellRow.MinDamage.Value, target);
                int maxDamage = ApplyStatTargetSpellReductions(spellRow.Spell, spellRow.MaxDamage.Value, target);
                minText = minDamage.ToString(CultureInfo.InvariantCulture);
                maxText = maxDamage.ToString(CultureInfo.InvariantCulture);
                averageText = StatTargetDamage(((minDamage + maxDamage) / 2.0) * (landPercent / 100.0));
            }

            await _client.SendLineAsync(StatTargetLine(
                StatTargetLabel(0, spellRow.ShortName),
                StatTargetValue(17, spellRow.Casts.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(24, spellRow.Diff.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(32, StatTargetPercent(landPercent)),
                StatTargetValue(39, minText),
                StatTargetValue(45, maxText),
                StatTargetValue(56, averageText)));
        }
    }

    // The chance the target resists the spell outright, as a percentage. Mirrors
    // SpellResistanceMath.IsSpellResisted: only a TypeOfResists of 2 (or 1 against an anti-magic target,
    // which a monster is not on this path) can be resisted at all, and the roll is 1..100 against half
    // the target's magic resistance, capped at 196.
    private static int GetMonsterSpellResistPercent(GameSpell spell, MonsterInstance target)
    {
        if (spell.TypeOfResists != 2)
            return 0;

        return Math.Clamp(Math.Min(target.EffectiveMagicResist, 196) / 2, 0, 100);
    }

    // The two damage reductions a landed spell still passes through: the magic-resistance shave (only for
    // spells flagged as reducible) and the target's resistance to the spell's element.
    private int ApplyStatTargetSpellReductions(GameSpell spell, int amount, MonsterInstance target)
    {
        int reduced = ApplyOffensiveMagicResistance(spell, amount, target.EffectiveMagicResist, targetHasAntiMagic: false);
        reduced = ApplyElementalSpellDamage(spell, reduced, target.GetEffectiveAbility);
        return Math.Max(0, reduced);
    }

    /// <summary>
    /// The other direction: what the target does to YOU. One row per real attack slot, with the slot's
    /// selection chance, how many swings its energy cost buys from the target's pool, its chance to land
    /// on your armour class, YOUR chance to dodge a landed blow, and what your damage resist leaves.
    /// </summary>
    private async Task ShowStatTargetIncomingAsync(MonsterInstance target)
    {
        string g = MudAnsi.Green;
        string reset = MudAnsi.Reset;

        var slots = target.Template.Attacks
            .Where(attack => CombatEngine.IsRealMonsterAttackType(attack.Type) && attack.Percent > 0)
            .OrderBy(attack => attack.SlotIndex)
            .ToList();

        // Headed by the monster's own name rather than a pronoun: on a sheet that already has a "Your
        // Attacks" table, "Its Attacks" is one glance away from being read as another of yours.
        await _client.SendLineAsync(string.Empty);
        await _client.SendLineAsync($"{g}{StatTargetHeadingName(target)} Attacks:{reset}");

        if (slots.Count == 0)
        {
            // An Energy-0 or all-empty-slot monster never swings — worth stating plainly, because a blank
            // table would read as missing data rather than as a harmless creature.
            await _client.SendLineAsync($"{g}It has no attack that can land on you.{reset}");
            return;
        }

        await _client.SendLineAsync(StatTargetLine(
            StatTargetLabel(0, "Slot"),
            StatTargetLabel(12, "Pick%"),
            StatTargetLabel(21, "Swings"),
            StatTargetLabel(30, "Accy"),
            StatTargetLabel(37, "Hit%"),
            StatTargetLabel(44, "Dodge%"),
            StatTargetLabel(53, "Min"),
            StatTargetLabel(59, "Max"),
            StatTargetLabel(65, "Avg/Rnd")));

        int playerAc = _player.GetTotalAC() + _player.PartyDefenceModifier;
        int playerDodgeSkill = _player.GetCombatDodgeSkill();
        int playerDodge = _player.GetDodge();
        int playerDamageResist = _player.GetTotalDR() / 10;
        int energyCap = target.GetCombatEnergyCap();

        double incomingPerRound = 0.0;
        // The slot roll is a run of ASCENDING thresholds tested against ONE roll, first match wins, so a
        // slot's own share is its threshold minus the highest threshold before it — and whatever is left
        // above the last threshold is a round where the target picks nothing and swings at all.
        int previousThreshold = 0;

        foreach (var attack in slots)
        {
            int pickPercent = Math.Max(0, Math.Min(100, attack.Percent) - previousThreshold);
            previousThreshold = Math.Max(previousThreshold, Math.Min(100, attack.Percent));

            int accuracy = CombatEngine.GetEffectiveMonsterAccuracy(target, attack.Accuracy);
            int hitPercent = CombatEngine.ComputeHitChance(
                CombatEngine.AttackType.Normal, accuracy, 0, playerAc, playerDodgeSkill);
            int dodgePercent = playerDodge > 0
                ? CombatEngine.ComputeDodgeChance(CombatEngine.AttackType.Normal, accuracy, playerDodge)
                : 0;

            var (rawMin, rawMax) = CombatEngine.GetEffectiveMonsterDamageBounds(target, attack.Min, attack.Max);
            int minDamage = Math.Max(0, rawMin - playerDamageResist);
            int maxDamage = Math.Max(minDamage, rawMax - playerDamageResist);

            int energyCost = CombatEngine.GetMonsterAttackEnergyCost(target, attack);
            int swings = Math.Clamp(energyCap / Math.Max(1, energyCost), 0, WeaponSwingPreviewCalculator.MaxWeaponSwingsPerRound);

            double landRate = (hitPercent / 100.0) * (1.0 - (dodgePercent / 100.0));
            double slotPerRound = swings * landRate * ((minDamage + maxDamage) / 2.0);
            incomingPerRound += (pickPercent / 100.0) * slotPerRound;

            await _client.SendLineAsync(StatTargetLine(
                StatTargetLabel(0, StatTargetSlotName(attack)),
                StatTargetValue(16, StatTargetPercent(pickPercent)),
                StatTargetValue(26, swings.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(33, accuracy.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(40, StatTargetPercent(hitPercent)),
                StatTargetValue(49, StatTargetPercent(dodgePercent)),
                StatTargetValue(55, minDamage.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(61, maxDamage.ToString(CultureInfo.InvariantCulture)),
                StatTargetValue(71, StatTargetDamage(slotPerRound))));
        }

        // The whole-round expectation weights each slot by how often it is picked. It is an estimate: the
        // target re-rolls the slot for EVERY swing, so a real round can mix slots, and this treats a round
        // as belonging to one slot. Slots differ mainly in damage rather than energy cost, so the two land
        // close together — but it is a projection, and saying so beats implying precision it lacks.
        await _client.SendLineAsync(StatTargetLine(
            StatTargetLabel(0, "Incoming/Rnd:"),
            StatTargetValue(20, StatTargetDamage(incomingPerRound)),
            StatTargetLabel(24, "Rounds to drop you:"),
            StatTargetValue(48, StatTargetRoundsToKill(Math.Max(1, _player.CurrentHP), incomingPerRound))));
    }

    // A readable label for an attack slot: its hit-message verb when the data has one (the message
    // templates read "The %s bites you for %d damage!", so the word after the name is the slot's flavour),
    // otherwise the slot number. Falls back cleanly for monsters whose slots carry no message.
    private string StatTargetSlotName(MonsterAttack attack)
    {
        if (attack.HitMessageId > 0
            && _world.Database.Messages.TryGetValue(attack.HitMessageId, out var message)
            && !string.IsNullOrWhiteSpace(message.Line1))
        {
            string[] words = message.Line1.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // "The %s <verb> ..." — the verb is the third word; anything shorter has no verb slot.
            if (words.Length >= 3 && words[0].Equals("The", StringComparison.OrdinalIgnoreCase))
            {
                string verb = words[2].Trim(',', '!', '.');
                if (verb.Length > 0 && verb.All(char.IsLetter))
                    return verb.Length > 11 ? verb[..11] : verb;
            }
        }

        return $"slot {attack.SlotIndex}";
    }
}
