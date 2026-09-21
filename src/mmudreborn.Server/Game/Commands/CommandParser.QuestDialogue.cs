using System.Text;
using System.Text.RegularExpressions;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class CommandParser
{
    private sealed class ParsedDialogueBlock
    {
        public Dictionary<string, int> KeywordLinks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> NarrativeLines { get; } = [];
        public List<DialogueScriptLine> ScriptLines { get; } = [];
    }

    private sealed class DialogueScriptLine
    {
        public string? TriggerPhrase { get; init; }
        public List<string> Operations { get; init; } = [];
    }

    private sealed class GiveIntent
    {
        public string ItemPhrase { get; init; } = string.Empty;
        public string TargetPhrase { get; init; } = string.Empty;
        public MonsterInstance? TargetNpc { get; init; }
    }

    private enum ScriptLineOutcome
    {
        NoMatch,
        Handled,
    }

    internal readonly record struct DialogueRenderPlan(string Text, bool AppendNewLine);

    private static readonly HashSet<string> RecognizedScriptOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "race",
        "class",
        "check class",
        "levelcheck",
        "minlevel",
        "checkability",
        "testability",
        "failability",
        // `goodability` is a stock content typo for `goodaligned` — the "pledge good" turn-in
        // (TB 4355) uses it with an alignment threshold (`goodability -51 3154`) exactly where its
        // sibling "pledge neutral"/"pledge evil" lines spell `goodaligned`/`evilaligned` correctly.
        // Alias it so the good-pledge line isn't dropped as narrative. (No `evilability` exists.)
        "goodability",
        "giveability",
        "addability",
        "removeability",
        "checkitem",
        "takeitem",
        "failitem",
        "giveitem",
        "addexp",
        "learnspell",
        "text",
        "message",
        "cast",
        "teleport",
        // Action verbs added for the special-command engine. Implemented below:
        "summon",
        "random",
        "addevil",
        "givecoins",
        "checkspell",
        "maxlevel",
        "nomonsters",
        // The matched-action engine dispatches BOTH `nomonsters` and its `monsters` alias
        // (same no-monsters-present test). Stock healer buy-blocks and cleanup gates
        // (`roomitem 1630:monsters:clearitem 1630`) use the short spelling.
        "monsters",
        // Recognized so they parse as commands (not leaked as narrative) but not yet faithfully
        // implemented — uncertain stock semantics (alignment thresholds, two-arg/skill encodings) or
        // missing infra (room floor items, command delay, the Actions table). TODO: implement.
        "goodaligned",
        "evilaligned",
        "needmonster",
        "roomitem",
        "failroomitem",
        "clearitem",
        "hideitem",
        "roomtext",
        "testskill",
        "checkskill",
        "delay",
        "adddelay",
        "remoteaction",
        "test_tournament",
        "price",
    };

    // Verbs that take no numeric argument. Every OTHER recognized verb must lead with a numeric
    // id/amount (testskill/checkskill: a skill name then a number) to count as a command — otherwise
    // the matched word is just the first word of a prose line. See ScriptOperationArgsAreValid.
    //
    // nomonsters / needmonster are room-state GATES whose numeric fail-reference is OPTIONAL: stock data
    // uses both the bare form (e.g. room 15/335 "push panel:nomonsters:message 2609:teleport 1283 7…")
    // and the "nomonsters 289" form. Without listing them here the bare form fails the numeric-arg check,
    // which makes the whole script line fail to parse — so the entire command (push panel, summon healer,
    // go willow, sit throne, enter portal, …) silently does nothing. 40 stock blocks use the bare form.
    private static readonly HashSet<string> ArglessScriptOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "check class",
        "levelcheck",
        "test_tournament",
        "nomonsters",
        "monsters",
        "needmonster",
    };

    private static readonly Regex DialogueAnsiSequenceRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);

    // Leading colour-code run on a script line (see TryParseScriptLine for why it is peeled).
    private static readonly Regex LeadingDialogueAnsiSequenceRegex = new(@"^(?:\x1B\[[0-?]*[ -/]*[@-~])+", RegexOptions.Compiled);

    private async Task HandleGive(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Give what?");
            return;
        }

        string trimmedArgs = args.Trim();
        bool parsedPlayerGive = TryParsePlayerGiveTarget(trimmedArgs, out string givePhrase, out string targetPhrase);
        bool hasNpcTarget = false;
        string roomActionArgs = trimmedArgs;

        // QOL bulk count on the ITEM half of the line: "give 10 torch to bob". The coin form is stock
        // and is left alone (the currency parse inside HandleGiveToPlayerAsync still sees the raw phrase).
        int giveQuantity = 1;
        string giveItemPhrase = parsedPlayerGive ? givePhrase : trimmedArgs;
        if (TryParseBulkQuantity(giveItemPhrase, out int parsedGiveQuantity, out string giveItemName))
        {
            giveQuantity = parsedGiveQuantity;
            giveItemPhrase = giveItemName;
        }

        if (parsedPlayerGive)
        {
            var targetPlayer = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, targetPhrase, _player);
            if (targetPlayer != null)
            {
                await HandleGiveToPlayerAsync(givePhrase, targetPlayer);
                return;
            }

            hasNpcTarget = HasGiveNpcTarget(targetPhrase);
            if (hasNpcTarget)
            {
                if (TryResolveUniqueCarriedItem(giveItemPhrase, includeEquipped: true, out var carriedItem, out var ambiguousGiveItems))
                {
                    roomActionArgs = $"{carriedItem.Item.Name} to {targetPhrase}";
                }
                else if (ambiguousGiveItems != null)
                {
                    await ShowItemDisambiguationAsync(ambiguousGiveItems);
                    return;
                }
            }
        }
        else if (TryParseCurrency(trimmedArgs, out _, out _, out _, out _))
        {
            await _client.SendLineAsync("Syntax: GIVE {amount} {currency} TO {someone}");
            return;
        }

        // A give to an NPC is a quest hand-in run by a room script, so a count repeats the whole line —
        // each pass is exactly what typing it again would do, and the run stops the moment a pass goes
        // unhandled or the next copy isn't in the pack. Every other shape of GIVE runs once.
        int roomActionPasses = hasNpcTarget ? giveQuantity : 1;
        bool handledRoomAction = false;
        for (int pass = 0; pass < roomActionPasses; pass++)
        {
            if (!await TryHandleRoomAction($"give {roomActionArgs}"))
                break;

            handledRoomAction = true;

            // Re-point at the next copy: the pass just handed one over, and the rewritten line carries
            // the item's canonical name.
            if (pass + 1 < roomActionPasses)
            {
                if (!TryResolveUniqueCarriedItem(giveItemPhrase, includeEquipped: true, out var nextCopy, out _))
                    break;
                roomActionArgs = $"{nextCopy.Item.Name} to {targetPhrase}";
            }
        }

        if (handledRoomAction)
            return;

        if (parsedPlayerGive && !hasNpcTarget)
        {
            // "You do not see %s here!" — the universal target-not-in-room line,
            // echoing the typed name with "!" (matches our look/forgive/attack paths). Was a hardcoded
            // "You do not see them here." which lost the name and used the wrong punctuation.
            await _client.SendLineAsync($"You do not see {targetPhrase} here!");
            return;
        }

        if (!parsedPlayerGive)
        {
            if (!TryResolveUniqueCarriedItem(giveItemPhrase, includeEquipped: true, out _, out var ambiguousGiveItems)
                && ambiguousGiveItems != null)
            {
                await ShowItemDisambiguationAsync(ambiguousGiveItems);
                return;
            }

            if (TryFindGiveableItem(giveItemPhrase, out _, out _, out _))
            {
                await _client.SendLineAsync("Give to whom?");
                return;
            }
        }

        await _client.SendLineAsync("Your command had no effect.");
    }

    private async Task HandleGiveToPlayerAsync(string givePhrase, Player targetPlayer)
    {
        if (TryParseCurrency(givePhrase, out int amount, out _, out _, out long denominationMultiplier))
        {
            await GiveCurrencyToPlayerAsync(amount, denominationMultiplier, targetPlayer);
            return;
        }

        // QOL bulk count: "give 10 torch to bob". Parsed after the currency branch so the stock coin
        // form keeps it; the recipient's pack still gates every single copy.
        int giveQuantity = 1;
        if (TryParseBulkQuantity(givePhrase, out int parsedGiveQuantity, out string giveItemName))
        {
            giveQuantity = parsedGiveQuantity;
            givePhrase = giveItemName;
        }

        int gaveCount = 0;
        string gaveName = string.Empty;
        string? giveStopMessage = null;
        for (int pass = 0; pass < giveQuantity; pass++)
        {
            // Re-resolved every pass: each hand-over removes an entry and renumbers the pack beneath it.
            if (!TryResolveUniqueCarriedItem(givePhrase, includeEquipped: true, out var carriedItem, out var ambiguousNames))
            {
                if (ambiguousNames != null)
                {
                    await ShowItemDisambiguationAsync(ambiguousNames);
                    return;
                }

                // Handing over the last copy mid-run needs no words — the summary carries the count. A
                // run that gave nothing prints the stock refusal, so a plain GIVE is what it always was.
                if (gaveCount == 0)
                    giveStopMessage = $"You don't have {givePhrase} to give.";
                break;
            }

            var item = carriedItem.Item;

            // A refusal or a full recipient is a real stop rather than a run-out, so it prints however
            // far in we are — a bulk run that fills the recipient ends on their "cannot accept" line.
            if (!targetPlayer.ReceiveItemsEnabled)
            {
                giveStopMessage = $"{targetPlayer.Name} refuses your offer.";
                break;
            }

            if (WouldExceedEncumbrance(targetPlayer, Math.Max(0, item.Encum)))
            {
                giveStopMessage = $"{targetPlayer.Name} cannot accept your offer.";
                break;
            }

            if (!TryRemoveResolvedCarriedItem(carriedItem, out _))
            {
                giveStopMessage = $"You don't have {givePhrase} to give.";
                break;
            }

            AddItemToInventory(targetPlayer, carriedItem.ItemId, carriedItem.InstanceId);
            RecalcEquipment();
            targetPlayer.RecalculateEquipment(_world.Database);

            gaveName = item.Name;
            gaveCount++;
        }

        if (gaveCount > 0)
        {
            // One line each way for the whole run; the onlookers' line is already vague about what
            // changed hands, so it stays as it is and is sent once.
            string gaveText = CountedItemText(gaveCount, gaveName);
            await _client.SendLineAsync($"{MudAnsi.White}You just gave {gaveText} to {targetPlayer.Name}.{MudAnsi.Reset}");
            _world.SendToPlayer(targetPlayer.Name, $"{MudAnsi.White}{_player.Name} just gave you {gaveText}.{MudAnsi.Reset}");
            NotifyGiveObservers(targetPlayer, $"{MudAnsi.White}{_player.Name} just gave {targetPlayer.Name} something.{MudAnsi.Reset}");
        }

        if (giveStopMessage != null)
            await _client.SendLineAsync(giveStopMessage);
    }

    // The GIVE currency-to-player branch. Unlike items, coins are never
    // rejected for weight: the recipient's free capacity silently caps the transfer to headroom*3 coins
    // (each coin weighs 1/3 of a weight unit, matching Player.CalculateCurrencyWeight), a per-coin
    // bounce-back trims any overflow left by the coin-weight rounding, and the giver is told the amount
    // that actually stuck (which may be 0 when the recipient is already full). Only the refuse-items flag
    // blocks a transfer outright, and it is checked before funds.
    private async Task GiveCurrencyToPlayerAsync(int amount, long denominationMultiplier, Player targetPlayer)
    {
        if (amount <= 0 || denominationMultiplier <= 0)
        {
            await _client.SendLineAsync("Syntax: GIVE {amount} {currency} TO {someone}");
            return;
        }

        string unit = CurrencyDenominationBareName(denominationMultiplier);

        if (!targetPlayer.ReceiveItemsEnabled)
        {
            await _client.SendLineAsync($"{targetPlayer.Name} refuses your offer.");
            return;
        }

        // Cap to what the recipient can physically carry: free weight (headroom) * 3 coins.
        targetPlayer.RecalculateEquipment(_world.Database);
        long headroom = GetMaxCarryCapacity(targetPlayer) - targetPlayer.Encumbrance;
        long capped = headroom < 0 ? 0 : Math.Min(amount, headroom * 3);

        // Insufficient-funds check runs against the capped amount, exactly as stock does. The 5th
        // currency ("runic") uses "that many"; the base metals use "that much".
        if (GetPlayerCurrencyDenominationCount(_player, denominationMultiplier) < capped)
        {
            await _client.SendLineAsync(denominationMultiplier == CurrencyHelper.CopperPerRunic
                ? $"You do not have that many {unit}!"
                : $"You do not have that much {unit}!");
            return;
        }

        TryDeductPlayerCurrencyDenomination(_player, denominationMultiplier, capped);
        AddPlayerCurrencyDenomination(targetPlayer, denominationMultiplier, capped);

        // Bounce back any coins that tip the recipient over capacity (coin-weight is floored, so the
        // headroom*3 cap can overshoot by a coin or two). Bounded to 500 iterations like stock.
        long moved = capped;
        for (int iterations = 0;
             iterations < 500 && moved > 0
                 && GetPlayerCurrencyDenominationCount(targetPlayer, denominationMultiplier) > 0
                 && GetMaxCarryCapacity(targetPlayer) < targetPlayer.Encumbrance;
             iterations++)
        {
            moved--;
            TryDeductPlayerCurrencyDenomination(targetPlayer, denominationMultiplier, 1);
            AddPlayerCurrencyDenomination(_player, denominationMultiplier, 1);
        }

        await _client.SendLineAsync($"{MudAnsi.White}You gave {targetPlayer.Name} {moved} {unit}{MudAnsi.Reset}");
        _world.SendToPlayer(targetPlayer.Name, $"{MudAnsi.White}{_player.Name} gave you {moved} {unit}{MudAnsi.Reset}");
        NotifyGiveObservers(targetPlayer, $"{MudAnsi.White}{_player.Name} just gave {targetPlayer.Name} some coins.{MudAnsi.Reset}");
    }

    // Bare metal name stock bakes into GIVE-currency lines ("gold", not the flavored "gold crowns").
    private static string CurrencyDenominationBareName(long denominationMultiplier) => denominationMultiplier switch
    {
        CurrencyHelper.CopperPerRunic => "runic",
        CurrencyHelper.CopperPerPlatinum => "platinum",
        CurrencyHelper.CopperPerGold => "gold",
        CurrencyHelper.CopperPerSilver => "silver",
        _ => "copper",
    };

    private void NotifyGiveObservers(Player targetPlayer, string message)
    {
        foreach (var observer in _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber))
        {
            if (observer == _player || observer == targetPlayer)
                continue;

            _world.SendToPlayer(observer.Name, message);
        }
    }

    private bool TryParsePlayerGiveTarget(string args, out string givePhrase, out string targetPhrase)
    {
        givePhrase = string.Empty;
        targetPhrase = string.Empty;

        string trimmedArgs = args.Trim();
        if (trimmedArgs.Length == 0)
            return false;

        int toIndex = trimmedArgs.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIndex > 0)
        {
            givePhrase = trimmedArgs[..toIndex].Trim();
            targetPhrase = trimmedArgs[(toIndex + 4)..].Trim();
            return givePhrase.Length > 0 && targetPhrase.Length > 0;
        }

        var roomPlayers = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            .OrderByDescending(player => player.Name.Length);

        foreach (var roomPlayer in roomPlayers)
        {
            if (trimmedArgs.Length <= roomPlayer.Name.Length)
                continue;

            if (!trimmedArgs.EndsWith(roomPlayer.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            int splitIndex = trimmedArgs.Length - roomPlayer.Name.Length;
            if (splitIndex <= 0 || !char.IsWhiteSpace(trimmedArgs[splitIndex - 1]))
                continue;

            givePhrase = trimmedArgs[..splitIndex].Trim();
            targetPhrase = roomPlayer.Name;
            return givePhrase.Length > 0;
        }

        return false;
    }

    private bool HasGiveNpcTarget(string targetPhrase)
    {
        return _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead)
            .Any(monster => GetConversationNames(monster)
                .Any(name => TargetNameMatcher.MatchesWordPrefix(name, targetPhrase)));
    }

    // Resolve an item to give from inventory or worn equipment via the shared whole-word-prefix
    // matcher (inventory preferred, then worn) — never a naive substring scan.
    private bool TryFindGiveableItem(string target, out int itemId, out long instanceId, out Item item)
    {
        var matches = FindMatchingCarriedItems(target, includeEquipped: true);
        if (matches.Count > 0)
        {
            itemId = matches[0].ItemId;
            instanceId = matches[0].InstanceId;
            item = matches[0].Item;
            return true;
        }

        itemId = 0;
        instanceId = 0;
        item = new Item();
        return false;
    }

    private bool TryResolveConversationNpc(string args, out MonsterInstance npc, out string topic)
    {
        npc = null!;
        topic = string.Empty;

        string trimmedArgs = args.Trim();
        var monsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead)
            .ToList();

        foreach (var monster in monsters)
        {
            foreach (var candidateName in GetConversationNames(monster))
            {
                if (trimmedArgs.Equals(candidateName, StringComparison.OrdinalIgnoreCase))
                {
                    npc = monster;
                    return true;
                }

                if (trimmedArgs.StartsWith(candidateName + " ", StringComparison.OrdinalIgnoreCase))
                {
                    npc = monster;
                    topic = trimmedArgs[candidateName.Length..].Trim();
                    return true;
                }
            }
        }

        var words = trimmedArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int splitIndex = words.Length - 1; splitIndex >= 1; splitIndex--)
        {
            string possibleName = string.Join(' ', words.Take(splitIndex));
            string possibleTopic = string.Join(' ', words.Skip(splitIndex));

            var matches = monsters
                .Where(monster => GetConversationNames(monster)
                    .Any(name => TargetNameMatcher.MatchesWordPrefix(name, possibleName)))
                .ToList();

            // Any match resolves the target — duplicate NPCs in a room (two "large shadow guard") are
            // interchangeable dialogue partners, so pick the first rather than treating the ambiguity as
            // unresolvable. Without this, "ask gu orfeo" with two guards fell through to the topicless
            // full-string fallback (which tried to match "gu orfeo" as a bare name) → "You don't see that
            // person here." The split order is longest-name-first, so the fullest name wins before a
            // shorter prefix could swallow part of the topic.
            if (matches.Count >= 1)
            {
                npc = matches[0];
                topic = possibleTopic;
                return true;
            }
        }

        var resolvedNpc = monsters.FirstOrDefault(monster => GetConversationNames(monster)
            .Any(name => TargetNameMatcher.MatchesWordPrefix(name, trimmedArgs)));
        if (resolvedNpc == null)
            return false;

        npc = resolvedNpc;
        return true;
    }

    private static IEnumerable<string> GetConversationNames(MonsterInstance monster)
    {
        return new[] { monster.DisplayName, monster.Name }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(name => name.Length);
    }

    private ParsedDialogueBlock ParseDialogueBlock(string text)
    {
        var parsed = new ParsedDialogueBlock();

        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (line.Length == 0)
            {
                parsed.NarrativeLines.Add(string.Empty);
                continue;
            }

            if (TryParseKeywordReferenceLine(line, out var keyword, out var targetTextBlockId))
            {
                parsed.KeywordLinks[keyword] = targetTextBlockId;
                continue;
            }

            if (TryParseScriptLine(line, out var triggerPhrase, out var operations))
            {
                parsed.ScriptLines.Add(new DialogueScriptLine
                {
                    TriggerPhrase = triggerPhrase,
                    Operations = operations,
                });
                continue;
            }

            parsed.NarrativeLines.Add(line);
        }

        return parsed;
    }

    private static bool TryParseKeywordReferenceLine(string line, out string keyword, out int targetTextBlockId)
    {
        keyword = string.Empty;
        targetTextBlockId = 0;

        var match = Regex.Match(line, @"^(?<keyword>[^:\r\n]+):(?<target>\d+)$");
        if (!match.Success)
            return false;

        keyword = match.Groups["keyword"].Value.Trim();
        return keyword.Length > 0 && int.TryParse(match.Groups["target"].Value, out targetTextBlockId);
    }

    // internal for unit testing the trigger/prose split (ScriptCommandParsingTests).
    internal static bool TryParseScriptLine(string line, out string? triggerPhrase, out List<string> operations)
    {
        triggerPhrase = null;
        operations = [];

        // An embedded colour code marks the line as coloured PROSE, not a command — that guard stays.
        // But a colour code can also sit on the TRIGGER itself, where it is formatting rather than text
        // the player would ever type: the Silvermere guildmaster's orc-warlord bounty (text block 9033,
        // room 1/546 CMD) reads out of wcctext2.dat as
        //     "\x1b[0;33mgive head of orc warlord to guildmaster:takeitem 1335 1912:…"
        // and the blanket guard filed the whole line as narrative, so the trigger the quest text (block
        // 9031) tells the player to type — "give head of orc warlord to guildmaster" — matched nothing
        // and the 500g/1000xp turn-in was unreachable. Peel a LEADING escape run so the trigger compares
        // against typed input. This is the ONLY line in the 1.11p corpus the strip reaches — every other
        // coloured line either fails the verb check anyway or keeps an escape past the trigger and stays
        // prose.
        string candidate = LeadingDialogueAnsiSequenceRegex.Replace(line, string.Empty);

        if (candidate.Contains("\x1b[", StringComparison.Ordinal))
            return false;

        var parts = candidate.Split(':', StringSplitOptions.None)
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();

        if (parts.Count == 0)
            return false;

        if (IsRecognizedScriptOperation(parts[0]))
        {
            operations = parts;
            return true;
        }

        if (parts.Count > 1 && parts.Skip(1).All(IsRecognizedScriptOperation))
        {
            triggerPhrase = parts[0];
            operations = parts.Skip(1).ToList();
            return true;
        }

        return false;
    }

    // internal for unit testing the prose-vs-command gate (ScriptCommandParsingTests).
    internal static bool IsRecognizedScriptOperation(string segment)
    {
        string operation = GetScriptOperationName(segment);
        return RecognizedScriptOperations.Contains(operation)
            && ScriptOperationArgsAreValid(operation, segment);
    }

    // A recognized verb is only a command when its arguments fit the verb's signature. Otherwise the
    // matched word is just the first word of a prose narrative line (e.g. "Cast your gaze upon...",
    // "price is extremely reasonable.", "price o' one hundred crowns") and the line must stay narrative
    // rather than be parsed/swallowed as a command. Commands are "verb int int…"; the only verbs with a
    // non-numeric argument are givecoins (amount + a single denomination letter) and testskill/checkskill
    // (a skill name then numbers). A few verbs are argless.
    private static bool ScriptOperationArgsAreValid(string operation, string segment)
    {
        if (ArglessScriptOperations.Contains(operation))
            return true;

        var tokens = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // testskill/checkskill: "<verb> <skillName> <int> [<int>…]".
        if (operation is "testskill" or "checkskill")
            return tokens.Length >= 3 && AllNumericTokens(tokens, 2);

        // givecoins: "<verb> <int> [<denomLetter>]" — amount, then an optional single-letter denomination.
        if (operation == "givecoins")
            return tokens.Length >= 2 && int.TryParse(tokens[1], out _)
                && (tokens.Length == 2 || (tokens.Length == 3 && tokens[2].Length == 1 && char.IsLetter(tokens[2][0])));

        // Every other verb's arguments are all numeric ids/amounts.
        return AllNumericTokens(tokens, 1);
    }

    private static bool AllNumericTokens(string[] tokens, int startIndex)
    {
        if (startIndex >= tokens.Length)
            return false;
        for (int i = startIndex; i < tokens.Length; i++)
        {
            if (!int.TryParse(tokens[i], out _))
                return false;
        }
        return true;
    }

    private static string GetScriptOperationName(string segment)
    {
        string trimmed = segment.Trim().ToLowerInvariant();
        if (trimmed.StartsWith("check class", StringComparison.OrdinalIgnoreCase))
            return "check class";
        if (trimmed.StartsWith("levelcheck", StringComparison.OrdinalIgnoreCase))
            return "levelcheck";

        int separatorIndex = trimmed.IndexOf(' ');
        return separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;
    }

    private static bool TryResolveKeywordTarget(IReadOnlyDictionary<string, int> keywordLinks, string topic, out int targetTextBlockId)
    {
        targetTextBlockId = 0;
        if (keywordLinks.Count == 0)
            return false;

        string normalizedTopic = NormalizeDialoguePhrase(topic);

        foreach (var kvp in keywordLinks)
        {
            if (NormalizeDialoguePhrase(kvp.Key) == normalizedTopic)
            {
                targetTextBlockId = kvp.Value;
                return true;
            }
        }

        var prefixMatches = keywordLinks
            .Where(kvp =>
            {
                string normalizedKeyword = NormalizeDialoguePhrase(kvp.Key);
                return normalizedKeyword.StartsWith(normalizedTopic, StringComparison.OrdinalIgnoreCase) ||
                       normalizedTopic.StartsWith(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        if (prefixMatches.Count == 1)
        {
            targetTextBlockId = prefixMatches[0].Value;
            return true;
        }

        return false;
    }

    private async Task<bool> TryHandleRoomCommandTextBlockAsync(string command)
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null || room.CMD <= 0 || !_world.Database.TextBlocks.TryGetValue(room.CMD, out var roomCommandText))
            return false;

        var roomCommandBlock = ParseDialogueBlock(roomCommandText);
        var clueKeywords = roomCommandBlock.KeywordLinks.Keys
            .Where(keyword => !keyword.Equals("nothing", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        _pendingRoomCommandTeleportRoomDisplay = false;

        bool handled = await TryExecuteTextBlockAsync(
            room.CMD,
            command,
            clueKeywords,
            useRoomCommandMessageSemantics: true);

        if (handled && _pendingRoomCommandTeleportRoomDisplay)
        {
            _pendingRoomCommandTeleportRoomDisplay = false;
            await ShowRoom(brief: _player.BriefMode);
            await CheckEncounters();
        }

        return handled;
    }

    private int GetLinkedTextBlockId(int textBlockId)
    {
        return _world.Database.TextBlockLinks.TryGetValue(textBlockId, out var linkedTextBlockId)
            ? linkedTextBlockId
            : 0;
    }

    private async Task<bool> TryExecuteTextBlockAsync(
        int textBlockId,
        string? triggerInput,
        IReadOnlyCollection<string>? clueKeywords,
        int depth = 0,
        bool useRoomCommandMessageSemantics = false)
    {
        if (depth > 8 || !_world.Database.TextBlocks.TryGetValue(textBlockId, out var text))
            return false;

        var block = ParseDialogueBlock(text);
        int linkedTextBlockId = GetLinkedTextBlockId(textBlockId);
        bool showedNarrative = false;

        if (string.IsNullOrWhiteSpace(triggerInput) && block.NarrativeLines.Count > 0)
        {
            await SendDialogueLinesAsync(block.NarrativeLines, clueKeywords);
            showedNarrative = true;
        }

        if (block.ScriptLines.Count > 0)
        {
            bool handled = await TryExecuteScriptLinesAsync(
                block.ScriptLines,
                triggerInput,
                clueKeywords,
                depth,
                useRoomCommandMessageSemantics);
            if (handled)
                return true;
        }

        if (string.IsNullOrWhiteSpace(triggerInput))
        {
            if (linkedTextBlockId > 0 &&
                await TryExecuteTextBlockAsync(
                    linkedTextBlockId,
                    null,
                    clueKeywords,
                    depth + 1,
                    useRoomCommandMessageSemantics))
            {
                return true;
            }

            if (showedNarrative)
                return true;

            if (block.KeywordLinks.TryGetValue("nothing", out var nothingId))
                return await TryExecuteTextBlockAsync(
                    nothingId,
                    null,
                    clueKeywords,
                    depth + 1,
                    useRoomCommandMessageSemantics);

            if (linkedTextBlockId <= 0 && block.KeywordLinks.Count == 1)
                return await TryExecuteTextBlockAsync(
                    block.KeywordLinks.Values.First(),
                    null,
                    clueKeywords,
                    depth + 1,
                    useRoomCommandMessageSemantics);
        }

        return false;
    }

    // Run a text block as special-command
    // effects (NOT dialogue). The block's lines are alternatives evaluated in order — the first line
    // whose conditions pass runs its effects and stops the rest (e.g. text block 2871: line 1 teleports
    // an idol-bearer, line 2 is the no-idol fallback). That is exactly the trigger-less semantics of the
    // shared executor (TryExecuteScriptLinesAsync stops on the first Handled line), so reuse it with a
    // null trigger; blocks made entirely of verbs carry no narrative to leak. Used by the spell effect
    // ability 148 (scripted command). Guarded against re-entrancy via _specialCommandDepth, since a
    // `cast` op inside a block can route back through the triggered-spell path into another one.
    private int _specialCommandDepth;
    private const int MaxSpecialCommandDepth = 8;

    // Cast a resolved self-targeted spell on the caster (an ability-151 chain follow-up). Mirrors the quest `cast`
    // verb's self routing: a beneficial spell applies its effect; a Duration>0 spell takes a buff/debuff
    // slot (the fear chains 1178-1180 land here); anything else runs the triggered pipeline. Returns true
    // when the script line should stop (a teleport/scripted follow-up). Chain targets in stock data never
    // re-chain or fan out to area/party, so single-level handling is sufficient.
    private async Task<bool> ApplySelfCastSpellAsync(GameSpell spell)
    {
        if (!IsOffensiveSpell(spell) && SpellHasImmediateBeneficialEffect(spell))
        {
            ApplyBuffSpellIfDuration(spell, _player);
            ApplyImmediateBeneficialEffects(spell, _player);
            return false;
        }

        if (spell.Duration > 0)
        {
            ApplyBuffSpellIfDuration(spell, _player);
            return false;
        }

        var result = await ExecuteTriggeredSpellByIdAsync(spell.Number, showRoomAfterTeleport: true,
            TriggeredCastAnnounce.CastSuccess);
        return result.StopProcessing;
    }

    private async Task<bool> PerformTextBlockAsSpecialCommandAsync(int textBlockId)
    {
        if (_specialCommandDepth >= MaxSpecialCommandDepth)
            return false;
        if (textBlockId <= 0 || !_world.Database.TextBlocks.ContainsKey(textBlockId))
            return false;

        _specialCommandDepth++;
        try
        {
            await TryExecuteTextBlockAsync(
                textBlockId,
                triggerInput: null,
                clueKeywords: null,
                useRoomCommandMessageSemantics: true);
        }
        finally
        {
            _specialCommandDepth--;
        }

        return true;
    }

    // Test hook: run a text block as a special command against this player exactly as the triggered-spell
    // (ability 148) pipeline does. Used to verify the jail-application path (guard spell 583 → text 1509 →
    // 9624) actually reaches `cast 643` and ADDS the alignment-scaled jail-time debuff to the prisoner.
    public Task<bool> PerformTextBlockSpecialCommandForTests(int textBlockId)
        => PerformTextBlockAsSpecialCommandAsync(textBlockId);

    // For an ability-148 room spell the per-pulse effect runs
    // the spell's textblock through the special-command engine — the SAME engine as a room CMD
    // textblock. That gives the pulse the full vocabulary the fast pulse loop lacks: alignment/class/level
    // gates, teleport, item ops, etc. (White Forest ejection 1079, church check 1147, class filters,
    // pit escapes, …). Run it as a trigger-less special command, then — exactly like the typed room CMD
    // path (TryHandleRoomCommandTextBlockAsync) — show the destination room + check encounters when the
    // pulse relocated the player (an ejection/filter teleport), so the prompt repaints below the new room.
    public async Task<bool> RunRoomSpellSpecialCommandAsync(int textBlockId)
    {
        int mapBefore = _player.CurrentMapNumber;
        int roomBefore = _player.CurrentRoomNumber;
        _pendingRoomCommandTeleportRoomDisplay = false;

        bool handled = await PerformTextBlockAsSpecialCommandAsync(textBlockId);

        if (_pendingRoomCommandTeleportRoomDisplay
            || _player.CurrentMapNumber != mapBefore
            || _player.CurrentRoomNumber != roomBefore)
        {
            _pendingRoomCommandTeleportRoomDisplay = false;
            await ShowRoom(brief: _player.BriefMode);
            await CheckEncounters();
        }

        return handled;
    }

    private async Task<bool> TryExecuteScriptLinesAsync(
        IEnumerable<DialogueScriptLine> scriptLines,
        string? triggerInput,
        IReadOnlyCollection<string>? clueKeywords,
        int depth,
        bool useRoomCommandMessageSemantics)
    {
        var matchedLines = scriptLines
            .Where(scriptLine => scriptLine.TriggerPhrase == null ||
                (!string.IsNullOrWhiteSpace(triggerInput) && ScriptTriggerMatches(scriptLine, triggerInput, useRoomCommandMessageSemantics)))
            .ToList();

        for (int index = 0; index < matchedLines.Count; index++)
        {
            var scriptLine = matchedLines[index];

            bool hasAlternativeMatch = index < matchedLines.Count - 1;
            var outcome = await ExecuteScriptLineAsync(
                scriptLine.Operations,
                clueKeywords,
                depth,
                hasAlternativeMatch,
                useRoomCommandMessageSemantics);
            if (outcome == ScriptLineOutcome.Handled)
                return true;
        }

        return false;
    }

    private bool RemainingOperationsTriggerTeleport(IReadOnlyList<string> operations, int startIndex, int depth)
    {
        if (depth > 8)
            return false;

        for (int index = startIndex; index < operations.Count; index++)
        {
            var args = operations[index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0)
                continue;

            string opName = GetScriptOperationName(operations[index]);
            if (opName == "cast" &&
                args.Length >= 2 &&
                int.TryParse(args[1], out var spellId) &&
                _world.Database.Spells.TryGetValue(spellId, out var spell) &&
                TryResolveTriggeredSpellTeleport(spell, out _, out _, out _))
            {
                return true;
            }

            if (opName == "teleport" &&
                args.Length >= 3 &&
                int.TryParse(args[1], out var targetRoom) &&
                int.TryParse(args[2], out var targetMap) &&
                _world.GetRoom(targetMap, targetRoom) != null)
            {
                return true;
            }

            if (opName == "text" &&
                args.Length >= 2 &&
                int.TryParse(args[1], out var nestedTextBlockId) &&
                TextBlockTriggersTeleport(nestedTextBlockId, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private bool TextBlockTriggersTeleport(int textBlockId, int depth)
    {
        if (depth > 8 || !_world.Database.TextBlocks.TryGetValue(textBlockId, out var text))
            return false;

        var block = ParseDialogueBlock(text);
        if (block.ScriptLines
            .Where(scriptLine => scriptLine.TriggerPhrase == null)
            .Any(scriptLine => RemainingOperationsTriggerTeleport(scriptLine.Operations, 0, depth + 1)))
        {
            return true;
        }

        int linkedTextBlockId = GetLinkedTextBlockId(textBlockId);
        if (linkedTextBlockId > 0 && TextBlockTriggersTeleport(linkedTextBlockId, depth + 1))
            return true;

        if (block.KeywordLinks.TryGetValue("nothing", out var nothingId) &&
            TextBlockTriggersTeleport(nothingId, depth + 1))
        {
            return true;
        }

        if (linkedTextBlockId <= 0 && block.KeywordLinks.Count == 1)
            return TextBlockTriggersTeleport(block.KeywordLinks.Values.First(), depth + 1);

        return false;
    }

    private static bool IsGateOnlyScriptOperation(string opName)
    {
        return opName is
            "race" or
            "class" or
            "check class" or
            "levelcheck" or
            "minlevel" or
            "failability" or
            "checkability" or
            "testability" or
            "checkitem" or
            "failitem";
    }

    private bool ScriptTriggerMatches(DialogueScriptLine scriptLine, string triggerInput, bool requireExactPhraseMatch = false)
    {
        if (scriptLine.TriggerPhrase == null)
            return true;

        if (requireExactPhraseMatch)
        {
            // Room-CMD give turn-ins (the Silvermere bounty lives in room 1/546's CMD block 9033) match
            // by the ITEM handed over, not by exact typed phrasing. The trigger phrase can differ from the
            // item's real name — the bounty trigger is "give head of orc warlord to guildmaster" but the
            // item is "head of THE orc warlord" (#1335) — so an exact-phrase match rejects the item's own
            // name and every short form ("give head/warlord to guildmaster"). GIVE resolves the carried
            // item to its canonical name before this runs (HandleGive rewrite), so a normalized-exact
            // compare of the handed item against the line's takeitem item name fires the correct line and
            // never a sibling (giving the orc-head can't trip the warlord line). The raw quoted phrase is
            // still honoured by the exact-phrase fallback below.
            if (IsGiveCommand(scriptLine.TriggerPhrase) && IsGiveCommand(triggerInput)
                && TryMatchRoomCommandGiveTrigger(scriptLine, triggerInput))
                return true;

            return PhrasesExactlyMatch(scriptLine.TriggerPhrase, triggerInput);
        }

        if (IsGiveCommand(scriptLine.TriggerPhrase) && IsGiveCommand(triggerInput))
            return TryMatchGiveTrigger(scriptLine.TriggerPhrase, triggerInput, scriptLine.Operations);

        return PhrasesRoughlyMatch(scriptLine.TriggerPhrase, triggerInput);
    }

    /// <summary>A gate verb's condition failed. In a multi-line alternative block each line is a quest
    /// stage selected by its gates, so a failure must FALL THROUGH to the next line (return NoMatch) —
    /// the "you don't qualify" reference is shown (and the block stopped) only on the last alternative.
    /// This mirrors the race/class/goodaligned/evilaligned handlers and the stock line-alternative
    /// semantics (ref-less range gates like "testability 126 6" pick which quest stage applies).</summary>
    private async Task<ScriptLineOutcome> GateFailedAsync(string[] args, int referenceIndex, bool hasAlternativeMatch,
        IReadOnlyCollection<string>? clueKeywords, int depth, bool useRoomCommandMessageSemantics)
    {
        if (!hasAlternativeMatch && TryParseReference(args, referenceIndex, out var referenceId))
        {
            await ShowScriptReferenceAsync(referenceId, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
            return ScriptLineOutcome.Handled;
        }
        return ScriptLineOutcome.NoMatch;
    }

    private async Task<ScriptLineOutcome> ExecuteScriptLineAsync(
        IReadOnlyList<string> operations,
        IReadOnlyCollection<string>? clueKeywords,
        int depth,
        bool hasAlternativeMatch,
        bool useRoomCommandMessageSemantics)
    {
        bool applicable = false;

        for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
        {
            var operation = operations[operationIndex];
            var args = operation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length == 0)
                continue;

            string opName = GetScriptOperationName(operation);
            if (useRoomCommandMessageSemantics &&
                _player.IsUnconscious &&
                !IsGateOnlyScriptOperation(opName) &&
                RemainingOperationsTriggerTeleport(operations, operationIndex, depth))
            {
                await SendMortallyWoundedMovementMessageAsync();
                return ScriptLineOutcome.Handled;
            }

            switch (opName)
            {
                case "race":
                    if (args.Length < 2 || !int.TryParse(args[1], out var raceId))
                        return applicable ? ScriptLineOutcome.Handled : ScriptLineOutcome.NoMatch;
                    if (_player.RaceId != raceId)
                    {
                        if (!hasAlternativeMatch && TryParseReference(args, 2, out var raceFailureRef))
                        {
                            await ShowScriptReferenceAsync(raceFailureRef, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                            return ScriptLineOutcome.Handled;
                        }

                        return ScriptLineOutcome.NoMatch;
                    }
                    applicable = true;
                    break;

                case "class":
                    if (args.Length < 2 || !int.TryParse(args[1], out var classId))
                        return applicable ? ScriptLineOutcome.Handled : ScriptLineOutcome.NoMatch;
                    if (_player.ClassId != classId)
                    {
                        if (!hasAlternativeMatch && TryParseReference(args, 2, out var classFailureRef))
                        {
                            await ShowScriptReferenceAsync(classFailureRef, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                            return ScriptLineOutcome.Handled;
                        }

                        return ScriptLineOutcome.NoMatch;
                    }
                    applicable = true;
                    break;

                case "check class":
                case "levelcheck":
                    applicable = true;
                    break;

                case "minlevel":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var minimumLevel))
                        return ScriptLineOutcome.Handled;
                    if (_player.Level < minimumLevel)
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "failability":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var failAbilityId))
                        return ScriptLineOutcome.Handled;
                    // The `failability` gate is presence-based across ALL ability sources — including
                    // an ability granted by an ACTIVE SPELL. The Phoenix Feather quest relies on this timer
                    // gate: `ask Morukai components` (block 1448) casts "morukai temp" (#614 — ability 152
                    // "Rune", value 0, ~10-minute Duration), and `ask Morukai return` (block 1452:
                    // `failability 152 1850`) must stay blocked until that rune expires. GetCurrentAbilityValue
                    // reads only persistent quest/intrinsic abilities, and the rune's value is 0, so the
                    // `> 0` test alone can never see it — hence the extra active-spell presence check.
                    if (GetCurrentAbilityValue(failAbilityId) > 0 || PlayerHasActiveSpellAbility(failAbilityId))
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "checkability":
                    applicable = true;
                    if (args.Length < 3 || !int.TryParse(args[1], out var requiredAbilityId) || !int.TryParse(args[2], out var requiredAbilityValue))
                        return ScriptLineOutcome.Handled;
                    if (GetCurrentAbilityValue(requiredAbilityId) < requiredAbilityValue)
                        return await GateFailedAsync(args, 3, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "testability":
                    applicable = true;
                    if (args.Length < 3 || !int.TryParse(args[1], out var testAbilityId) || !int.TryParse(args[2], out var maximumAbilityValue))
                        return ScriptLineOutcome.Handled;
                    if (GetCurrentAbilityValue(testAbilityId) > maximumAbilityValue)
                        return await GateFailedAsync(args, 3, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "giveability":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var givenAbilityId))
                        return ScriptLineOutcome.Handled;
                    int grantedValue = args.Length > 2 && int.TryParse(args[2], out var parsedGrantedValue) ? parsedGrantedValue : 1;
                    // GrantQuestAbility (not SetQuestAbilityValue) so a presence-only boolean granted at
                    // value 0 — e.g. `giveability 186 0`, Perfect/"supernatural" Stealth — is actually
                    // stored. SetQuestAbilityValue's value<=0 path would REMOVE it. Math.Max keeps a
                    // higher existing progress-flag value from being downgraded by a 0 grant.
                    _player.GrantQuestAbility(givenAbilityId, Math.Max(_player.GetQuestAbilityValue(givenAbilityId), grantedValue));
                    RecalculatePlayerDerivedStats();
                    break;

                case "addability":
                    applicable = true;
                    if (args.Length < 3 || !int.TryParse(args[1], out var addedAbilityId) || !int.TryParse(args[2], out var addedAbilityValue))
                        return ScriptLineOutcome.Handled;
                    _player.AddQuestAbilityValue(addedAbilityId, addedAbilityValue);
                    RecalculatePlayerDerivedStats();
                    break;

                case "removeability":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var removedAbilityId))
                        return ScriptLineOutcome.Handled;
                    _player.RemoveQuestAbility(removedAbilityId);
                    RecalculatePlayerDerivedStats();
                    break;

                case "checkitem":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var checkedItemId))
                        return ScriptLineOutcome.Handled;
                    if (!PlayerCarriesItemId(checkedItemId))
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "failitem":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var failItemId))
                        return ScriptLineOutcome.Handled;
                    if (PlayerCarriesItemId(failItemId))
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "takeitem":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var takenItemId))
                        return ScriptLineOutcome.Handled;
                    // The `takeitem` verb removes the item from inventory; if the
                    // player is NOT carrying the item, it is a GATE FAILURE — FALL THROUGH to
                    // the next line, NOT a stop. Treating "item absent" as Handled (stop) broke multi-line
                    // trigger scripts whose real effect lives on a later line: the fortress trigger TB 4173
                    // ("takeitem <questitem>:random 4174" ×N → "failitem 185:random 4174") stopped at the
                    // first takeitem the player didn't have, so the `random 4174` spawn roll never ran and
                    // the Angelic Hunter / meadow mobs never appeared. GateFailedAsync falls through (and
                    // shows the optional fail-ref only on the last alternative), matching stock.
                    if (!TryRemoveCarriedItemById(takenItemId, out var removedEquipped))
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    if (removedEquipped)
                        RecalcEquipment();
                    break;

                case "giveitem":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var givenItemId))
                        return ScriptLineOutcome.Handled;
                    // A scripted giveitem is a quest grant, NOT the GET path: an item that exceeds the
                    // player's encumbrance is silently skipped, never rejected with "You cannot carry that
                    // much!" (that message and its abort belong to the inventory add / GET). This is
                    // how the AR Dragon Statue puzzle (text block 475) works: each fang insertion runs
                    // The `giveitem` verb adds to inventory; if that FAILS (the item
                    // would over-encumber the player), the item is DROPPED to the room floor via
                    // to the room — it is NOT silently lost and the turn-in is NOT blocked. So a
                    // heavy quest reward (e.g. the 2000-Encum adamantite chest 957) lands at your feet to
                    // pick up, and a 32000-Encum prop (the AR Dragon Statue carving 762) drops to the room
                    // instead of burying you. (No "You cannot carry that much!" — that belongs to GET.)
                    if (_world.Database.Items.ContainsKey(givenItemId) && !TryAcquireInventoryItem(givenItemId, out _))
                        _world.DisposeOfItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, givenItemId);
                    if (TryParseReference(args, 2, out var giveItemRef))
                        await ShowScriptReferenceAsync(giveItemRef, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                    break;

                case "addexp":
                    applicable = true;
                    if (args.Length < 2 || !long.TryParse(args[1], out var experienceDelta))
                        return ScriptLineOutcome.Handled;
                    _player.Experience = Math.Max(0, _player.Experience + experienceDelta);
                    break;

                case "learnspell":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var learnedSpellId))
                        return ScriptLineOutcome.Handled;
                    LearnSpell(learnedSpellId);   // add to the spellbook
                    break;

                case "text":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var nestedTextBlockId))
                        return ScriptLineOutcome.Handled;
                    await TryExecuteTextBlockAsync(
                        nestedTextBlockId,
                        null,
                        clueKeywords,
                        depth: depth + 1,
                        useRoomCommandMessageSemantics: useRoomCommandMessageSemantics);
                    break;

                case "message":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var messageId))
                        return ScriptLineOutcome.Handled;
                    if (useRoomCommandMessageSemantics)
                    {
                        await SendTriggeredRoomMessageAsync(messageId, line1Color: MudAnsi.White, line2Color: MudAnsi.White);
                    }
                    else if (_world.Database.Messages.TryGetValue(messageId, out var actionMessage)
                        && !string.IsNullOrWhiteSpace(actionMessage.Line2))
                    {
                        // A `message N` op whose Messages row carries a Line2 (the third-person "%s does X"
                        // line) is an ACTION message, not NPC speech: Line1 is for the actor, Line2 broadcasts
                        // to the room. The water portal (block 2778: `message 2447:teleport 637 16`) relies on
                        // this to tell the room "%s rises through the air…" before the teleport. The legacy
                        // dialogue path streamed BOTH lines to the actor, so the room stayed silent and the raw
                        // %s line leaked to the player. Single-line message rows (no Line2) keep dialogue
                        // streaming below.
                        await SendTriggeredRoomMessageAsync(messageId, line1Color: MudAnsi.White, line2Color: MudAnsi.White);
                    }
                    else
                    {
                        await ShowScriptReferenceAsync(messageId, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                    }
                    break;

                case "cast":
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var spellId))
                        return ScriptLineOutcome.Handled;

                    if (useRoomCommandMessageSemantics && _player.IsUnconscious)
                    {
                        await SendMortallyWoundedMovementMessageAsync();
                        return ScriptLineOutcome.Handled;
                    }

                    if (_world.Database.Spells.TryGetValue(spellId, out var castSpell))
                    {
                        // The routing decision is the single-source-of-truth classifier (Spells.cs) that the
                        // QuestSpellRoutingAuditTests data audit also consumes — so the engine and the audit
                        // can't drift, and hand-authored DB spells are validated against the same rules.
                        var route = ClassifyQuestCastRoute(castSpell);

                        // An area-offensive spell scripted by a quest (Targets 12 "Full Attack Area" + a
                        // harm ability) is a sweep on the room's monsters: it must strike/kill them, firing
                        // each victim's DeathSpell, NOT be mis-routed to the self-buff branch just because it
                        // also carries a Duration. Stock evil quest stage 18: "ask Enigma retrieve" runs
                        // `cast 1234` ("kill enigma", 10 dmg), which drops the 3-HP talker Enigma Lord (#755),
                        // whose DeathSpell 1233 ("summon enigma lord", id from MinBase 921) spawns the hostile
                        // boss #921 to fight. Snapshot is taken by GetEligibleAreaMonsterTargets (a copy), so a
                        // monster summoned by a death mid-sweep is not also hit by this same cast.
                        if (route == QuestCastRoute.AreaEnemySweep)
                        {
                            foreach (var monster in GetEligibleAreaMonsterTargets())
                                await ApplyAreaSpellToMonsterAsync(castSpell, monster, bypassMonsterSpellDefenses: true);
                            break;
                        }

                        // A party-area (Targets 13) teleport relocates the WHOLE party in the room, not just
                        // the caster. In the no-target cast's post-loop relocate, once the dest-room/map
                        // values are set, Targets 13 walks the caster's follower list (5 slots) and
                        // moves each member via the relocate routine; every other target type moves only the
                        // caster (stop following, then relocate). Evil quest stage 128→9 after the Duergar Lord
                        // (block 1309: `cast 582` "duergar teleport", Targets 13 → map 6/room 1398) is the
                        // reported case (bug #176) — it must pull the party through with the actor; the desert
                        // sandstorm exit (713, Targets 13) is the same shape. Snapshot the party in the room
                        // BEFORE anyone moves, relocate the followers first (so they "vanish" from the origin
                        // room while it's still the origin), then the caster falls through to the same call.
                        if (route == QuestCastRoute.PartyTeleport)
                        {
                            foreach (var member in GetGroupSpellRecipients())
                            {
                                if (ReferenceEquals(member, _player))
                                    continue;

                                var memberClient = _world.GetClientForPlayer(member.Name);
                                if (memberClient == null)
                                    continue;

                                var memberParser = new CommandParser(memberClient, _world, member);
                                await memberParser.ExecuteTriggeredSpellByIdAsync(spellId, showRoomAfterTeleport: true,
                                    TriggeredCastAnnounce.CastSuccess);
                            }
                            // caster relocated by the shared ExecuteTriggeredSpellByIdAsync call below
                        }

                        // A beneficial / utility spell (heal 18, restore-mana 150, cure-poison 20,
                        // remove-spell 122/153, remove-curse 84, …) scripted by a quest must APPLY its
                        // effect, never fall through to ExecuteTriggeredSpellByIdAsync — whose final branch
                        // reads MinBase as raw DAMAGE to the actor (the black-cauldron root). Without this,
                        // `cast 248` (yellow fungus, heal) / `cast 878` (remove forms, ability 122 ×4) / `cast 313`
                        // (bigheal, Targets 13) all DAMAGE the caster instead of helping. Targets 13 applies
                        // to the whole party in the room; everything else to the caster. The buff slot (if
                        // any) and each immediate ability are applied per beneficiary, mirroring a real cast;
                        // the textblock's own `message`/`text` verbs carry the narrative, so stay silent here.
                        if (route == QuestCastRoute.Beneficial)
                        {
                            var beneficiaries = castSpell.Targets == PartyAreaTargetType
                                ? GetGroupSpellRecipients()
                                : new List<Player> { _player };

                            foreach (var beneficiary in beneficiaries)
                            {
                                ApplyBuffSpellIfDuration(castSpell, beneficiary);
                                ApplyImmediateBeneficialEffects(castSpell, beneficiary);
                            }
                            break;
                        }

                        // A timed (Duration>0) spell cast by a script is a buff/debuff on the actor — e.g. the
                        // jail-time sentence (586/641-644), whose ability 151 "Cast on ending" fires the
                        // release spell when it expires. ExecuteTriggeredSpellByIdAsync only resolves
                        // teleport/scripted/damage spells, so route timed spells through the buff applier.
                        if (route == QuestCastRoute.DurationBuff)
                        {
                            ApplyBuffSpellIfDuration(castSpell, _player);
                            break;
                        }

                        // Chain-only spell (ability 151, Duration 0): fire the follow-up instead of damaging the
                        // caster. The linked spell id is the ability value, or — when 0 — the rolled
                        // MinBase..MaxBase (the stock value-or-roll rule). "fear random" #1181 → 1178/1179/
                        // 1180 (fear debuffs). The follow-up is a self-cast, applied by ApplySelfCastSpellAsync.
                        if (route == QuestCastRoute.Chain)
                        {
                            int linkedSpellId = castSpell.Abilities.GetValueOrDefault(ChainSpellAbilityId);
                            if (linkedSpellId <= 0)
                                linkedSpellId = Random.Shared.Next(castSpell.MinBase, castSpell.MaxBase + 1);

                            if (_world.Database.Spells.TryGetValue(linkedSpellId, out var linkedSpell)
                                && await ApplySelfCastSpellAsync(linkedSpell))
                            {
                                return ScriptLineOutcome.Handled;
                            }
                            break;
                        }
                    }

                    // The `cast` verb runs
                    // the ordinary no-target cast engine — so its result is
                    // announced with the spell's CastMsgB, exactly like a player's
                    // own cast. Not by a triggered-spell special case. See EmitCastSuccessMessagesAsync.
                    var triggeredSpellResult = await ExecuteTriggeredSpellByIdAsync(
                        spellId, showRoomAfterTeleport: true, TriggeredCastAnnounce.CastSuccess);

                    if (triggeredSpellResult.StopProcessing)
                        return ScriptLineOutcome.Handled;
                    break;

                case "teleport":
                    applicable = true;
                    if (args.Length < 3 ||
                        !int.TryParse(args[1], out var targetRoom) ||
                        !int.TryParse(args[2], out var targetMap))
                    {
                        return ScriptLineOutcome.Handled;
                    }

                    if (_world.GetRoom(targetMap, targetRoom) == null)
                        return ScriptLineOutcome.Handled;

                    _player.CurrentMapNumber = targetMap;
                    _player.CurrentRoomNumber = targetRoom;
                    _player.IsResting = false;
                    _player.IsMeditating = false;
                    _world.NotifyPlayerEnteredRoom(_player);

                    if (useRoomCommandMessageSemantics)
                    {
                        await HandleIndependentPartyTravelCleanupAsync(_player, disbandLeader: true);

                        _pendingRoomCommandTeleportRoomDisplay = true;
                    }
                    break;

                case "summon":
                    // The `summon` verb: spawn a monster into the current room (reinforcement / boss add).
                    //
                    // A REFUSED spawn aborts the REST OF THE LINE and prints NOTHING. In stock the summon
                    // handler checks whether the spawn succeeded and, if not, drops the remaining ops and
                    // reports a gate failure, so the script engine falls through to the next line of the
                    // block — exactly ScriptLineOutcome.NoMatch here. The handler parses ONLY the monster
                    // id — it has no fail-ref argument and never prints a fail message, so a refusal is
                    // silent; no stock block passes a second arg to summon, so there is nothing to print
                    // even in principle.
                    // A scripted summon is forced, which bypasses the room cap, respawn timer and area cap
                    // — but NOT the template GameLimit or the unique RegenTime gate. Those two are what
                    // TrySpawnMonsterInRoom refuses on here (CanSpawnMonster / IsRegenTimerElapsed), so
                    // the gates line up.
                    // We used to ignore the result and keep running the line, so a capped or still-cooling
                    // summon went on to fire its trailing ops (the stacked `summon`s and the closing
                    // `teleport` in the arena wave blocks 1756-1769/1817-1819, `text 1310` after
                    // `summon 465` in 1309).
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var summonMonsterId) && summonMonsterId > 0
                        && !_world.TrySpawnMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, summonMonsterId, ignoreRoomRestrictions: true, out _, out _))
                    {
                        return ScriptLineOutcome.NoMatch;
                    }
                    break;

                case "random":
                    // The `random` verb: weighted branch into a text block whose lines are "threshold:cmds"
                    // buckets (e.g. 25/50/75/100). Roll 1..100, run the first bucket the roll falls into.
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var randomBlockId) && randomBlockId > 0)
                        await ExecuteRandomBranchAsync(randomBlockId, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                    break;

                case "addevil":
                    // The `addevil` verb: add evil points to the actor (0 is a common no-op marker).
                    // Positive deltas respect IsLawful via TryAddEvilPoints; negative deltas (a
                    // "good action" reward from a quest) are always applied. The script intent —
                    // make the actor more evil — collapses into the canonical EvilPoints field.
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var addedEvil) && addedEvil != 0)
                        await AddEvilPointsWithCloudAsync(addedEvil);
                    break;

                case "givecoins":
                    // The `givecoins` verb: "givecoins <amount> <denomLetter>" (R/P/G/S/C; default copper).
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var coinAmount) && coinAmount > 0)
                    {
                        long denominationMultiplier = GetCurrencyLetterMultiplier(args.Length > 2 ? args[2] : "c");
                        AddPlayerCurrencyDenomination(_player, denominationMultiplier, coinAmount);
                        RecalcEquipment();   // currency weight feeds encumbrance
                    }
                    break;

                case "checkspell":
                    // The `checkspell` verb: "checkspell <spellId> <failRef>". It scans
                    // the target's TEN ACTIVE-SPELL slots — the same array a cast
                    // writes — for the id. So the test is "is this spell currently
                    // ACTIVE on you", NOT "have you learned it": the stock users are environmental buffs
                    // nobody ever learns — the desert pulse (spell 683/684 → block 2653/2658) asks
                    // `checkspell 711` for the waterskin buff you get by drinking, and the sea/swim blocks
                    // ask about their own breathing buffs.
                    //
                    // On failure it runs the reference through the special-command engine, so the
                    // fail-ref is a TEXTBLOCK, never a Messages row — same as testskill/checkskill. The
                    // ids collide: the desert's fail-ref 2654 is TextBlock 2654 ("failitem 1180:cast 712"
                    // = no sunstone wristband → take desert-heat damage), but Message 2654 also exists
                    // ("The leaves begin to rustle…", a Darkwood line). Resolving messages-first printed
                    // that forest line in the middle of the desert and never ran the damage block.
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var requiredSpellId))
                        return ScriptLineOutcome.Handled;
                    if (!_player.HasActiveSpell(requiredSpellId))
                    {
                        if (TryParseReference(args, 2, out var checkSpellRef))
                            await ExecuteScriptReferenceAsTextBlockAsync(checkSpellRef, clueKeywords, depth, useRoomCommandMessageSemantics);
                        return ScriptLineOutcome.Handled;
                    }
                    break;

                case "maxlevel":
                    // The `maxlevel` test (mirror of minlevel): "maxlevel <level> [failRef]".
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var maximumLevel))
                        return ScriptLineOutcome.Handled;
                    if (_player.Level > maximumLevel)
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "nomonsters":
                case "monsters":
                    // The `nomonsters` test (and its `monsters` alias): the room must be
                    // clear of monsters. The BARE form (`nomonsters`) means "empty of EVERY monster"
                    // and so must also see the room's permanent NPC boss — otherwise a boss room whose
                    // teleport is gated on the boss dying (Majestic Dragon 17/2881) ejects the party on
                    // the first room-spell pulse while the dragon is still alive. The `<id>` form keeps
                    // the permanent-NPC exclusion (it targets a specific transient monster; see below).
                    applicable = true;
                    if (RoomHasLivingMonster(includePermanentNpc: args.Length < 2))
                        return await GateFailedAsync(args, 1, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "goodability":   // stock typo alias for goodaligned (see RecognizedScriptOperations)
                case "goodaligned":
                    // Require the actor be good enough — fail if EvilPoints exceeds the
                    // threshold. "goodaligned <maxEvil> [failRef]". On failure with no ref (and when a
                    // later line could still match), fall THROUGH to the next line — this is what makes
                    // an alignment-branched block (e.g. jail script 9624: a stack of
                    // evilaligned/goodaligned lines, one per EP band) pick the right band.
                    if (args.Length < 2 || !int.TryParse(args[1], out var goodThreshold))
                        return applicable ? ScriptLineOutcome.Handled : ScriptLineOutcome.NoMatch;
                    if ((int)_player.EvilPoints > goodThreshold)
                    {
                        if (!hasAlternativeMatch && TryParseReference(args, 2, out var goodAlignedRef))
                        {
                            await ShowScriptReferenceAsync(goodAlignedRef, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                            return ScriptLineOutcome.Handled;
                        }
                        return ScriptLineOutcome.NoMatch;
                    }
                    applicable = true;
                    break;

                case "evilaligned":
                    // Require the actor be evil enough — fail if EvilPoints is below the threshold.
                    // Same fall-through-on-failure semantics as goodaligned (see above).
                    if (args.Length < 2 || !int.TryParse(args[1], out var evilThreshold))
                        return applicable ? ScriptLineOutcome.Handled : ScriptLineOutcome.NoMatch;
                    if ((int)_player.EvilPoints < evilThreshold)
                    {
                        if (!hasAlternativeMatch && TryParseReference(args, 2, out var evilAlignedRef))
                        {
                            await ShowScriptReferenceAsync(evilAlignedRef, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
                            return ScriptLineOutcome.Handled;
                        }
                        return ScriptLineOutcome.NoMatch;
                    }
                    applicable = true;
                    break;

                case "needmonster":
                    // `needmonster <monsterId> <failRef>`: require a living monster of template
                    // <monsterId> in the room, else show <failRef>. Stock data ALWAYS supplies the id —
                    // e.g. the Island of Bones portal (TB 9295): "go portal:cast 310:needmonster 945
                    // 2073:…", where 945 is the summoned mirror portal and 2073 = "You do not see a
                    // portal here." Checking for ANY monster (the old behavior) let an unrelated
                    // wanderer satisfy the gate, so a portal that was never summoned still let the
                    // player through — and a specific-prop gate passed on the wrong monster.
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var needMonsterId) && needMonsterId > 0)
                    {
                        if (!RoomHasLivingMonster(needMonsterId))
                            return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    }
                    else if (!RoomHasLivingMonster())   // bare form is not used by stock data; keep the any-monster fallback
                        return await GateFailedAsync(args, args.Length - 1, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "roomitem":
                    // The `roomitem` verb: a PURE PRESENCE
                    // GATE — scan the room's item slots and, if the item is NOT present, fail
                    // and FALL THROUGH to the next line. It NEVER places the item; the optional trailing
                    // arg is only a fail-reference to show. Both forms gate identically:
                    //  • `roomitem <id> <ref>` — portal/prop gate, e.g. Potion of Levitation (TB 1421:
                    //    `roomitem 993 1834:…:teleport 1009 9`) only fires in Mossy Cave, Waterfall (3/1),
                    //    the one room holding waterfall prop #993, else shows msg 1834 "…nothing happens.".
                    //  • `roomitem <id>` (no ref) — the sequence-puzzle gate that requires a MARKER item be
                    //    in the room to select the current stage. The earlier "no-ref PLACES the item"
                    //    reading was WRONG (stock never places here) and broke every marker machine: the
                    //    withered-tree branch puzzle (TB 4204 → summon Azrandimon 1030) and the AR Dragon
                    //    Statue fang puzzle (TB 475) both ran every line unconditionally, so the summon /
                    //    turn-in never gated correctly. Markers are placed by `giveitem` (heavy props drop
                    //    to the floor), never by roomitem.
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var roomItemId) || roomItemId <= 0
                        || !_world.Database.Items.ContainsKey(roomItemId))
                        break;
                    if (!RoomHasGateItem(roomItemId))
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "hideitem":
                    // Place an item on the room floor, hidden (revealed by search).
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var hideItemId) && hideItemId > 0
                        && _world.Database.Items.ContainsKey(hideItemId))
                        _world.HideItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, hideItemId);
                    break;

                case "clearitem":
                    // Remove an item from the room. Handles both a dynamic
                    // ground entry AND a non-gettable static placed item (e.g. the apparatus 819, which
                    // is drawn from the room's Placed field) so the prop actually disappears.
                    // Item id 0 is the rubbish-disposal sentinel ("clearitem 0", used only by the
                    // pull-lever flush scripts) → destroy every item on the floor, not a single id.
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var clearItemId))
                    {
                        if (clearItemId == 0)
                            _world.ClearAllRoomItems(_player.CurrentMapNumber, _player.CurrentRoomNumber);
                        else if (clearItemId > 0)
                            _world.ClearRoomItem(_player.CurrentMapNumber, _player.CurrentRoomNumber, clearItemId);
                    }
                    break;

                case "failroomitem":
                    // Require the item NOT be on the room floor — fail+ref if present.
                    applicable = true;
                    if (args.Length < 2 || !int.TryParse(args[1], out var failRoomItemId))
                        return ScriptLineOutcome.Handled;
                    if (RoomContainsGroundItem(failRoomItemId))
                        return await GateFailedAsync(args, 2, hasAlternativeMatch, clueKeywords, depth, useRoomCommandMessageSemantics);
                    break;

                case "roomtext":
                    // Display a text block to the whole room.
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var roomTextId) && roomTextId > 0)
                        BroadcastTextBlockToRoom(roomTextId);
                    break;

                case "testskill":
                    // The probabilistic form of the skill check — fail+ref if
                    // skillValue < genrdn(0, cap) + difficulty. genrdn(0,cap) = [0,cap-1] (top exclusive),
                    // so model it as Next(0, cap), NOT Next(0, cap+1). "testskill <name> <difficulty> [failRef]".
                    applicable = true;
                    if (args.Length < 3 || !TryGetNamedSkillCheck(args[1], out var testSkillValue, out var testSkillCap) || !int.TryParse(args[2], out var testDifficulty))
                        return ScriptLineOutcome.Handled;
                    if (testSkillValue < Random.Shared.Next(0, testSkillCap) + testDifficulty)
                    {
                        // The fail path runs the reference through the special-command engine
                        // ref) — the fail-ref is executed as a TEXTBLOCK (e.g. the Puzzle Door's 710 =
                        // "cast 452" trap lightning, or a jump-puzzle "fall" block), NOT resolved as a
                        // Messages row. Resolving messages-first would let Message 710 ("The %s slashes
                        // you…") shadow TextBlock 710 and swallow the damage.
                        if (TryParseReference(args, 3, out var testSkillRef))
                            await ExecuteScriptReferenceAsTextBlockAsync(testSkillRef, clueKeywords, depth, useRoomCommandMessageSemantics);
                        return ScriptLineOutcome.Handled;
                    }
                    break;

                case "checkskill":
                    // The deterministic form of the skill check — fail+ref if skillValue < min.
                    // "checkskill <name> <min> [failRef]".
                    applicable = true;
                    if (args.Length < 3 || !TryGetNamedSkillCheck(args[1], out var checkSkillValue, out _) || !int.TryParse(args[2], out var checkSkillMin))
                        return ScriptLineOutcome.Handled;
                    if (checkSkillValue < checkSkillMin)
                    {
                        // Same as testskill: the fail-ref runs as a textblock special command.
                        if (TryParseReference(args, 3, out var checkSkillRef))
                            await ExecuteScriptReferenceAsTextBlockAsync(checkSkillRef, clueKeywords, depth, useRoomCommandMessageSemantics);
                        return ScriptLineOutcome.Handled;
                    }
                    break;

                case "delay":
                case "adddelay":
                    // Add n fast-ticks to the player's delay counter,
                    // modeled here as the shared fixed action delay await. Both the `delay` and `adddelay`
                    // script verbs land here — the boulder-puzzle blocks (map 17 Burning Valley: "push
                    // boulder:delay 5:testskill strength 60 4235:remoteaction …") lead with `delay 5`, so
                    // omitting `delay` from RecognizedScriptOperations mis-parsed the whole line as
                    // narrative and the reveal never fired.
                    applicable = true;
                    if (args.Length >= 2 && int.TryParse(args[1], out var delayTicks) && delayTicks > 0)
                        await ApplyActionDelayAsync(delayTicks);
                    break;

                case "remoteaction":
                    // `remoteaction <room> <messageId> <actionOrder> <directionIndex>`: mark a
                    // completion action against a hidden exit in the target room (player's map),
                    // revealing it once all required actions are done, and message that room.
                    applicable = true;
                    if (args.Length >= 5
                        && int.TryParse(args[1], out var remoteRoom)
                        && int.TryParse(args[2], out var remoteMessageId)
                        && int.TryParse(args[3], out var remoteActionOrder)
                        && int.TryParse(args[4], out var remoteDirectionIndex)
                        && _world.TryCompleteRemoteActionForExit(_player.CurrentMapNumber, remoteRoom, remoteDirectionIndex, remoteActionOrder, out _))
                    {
                        await BroadcastMessageToRemoteRoom(_player.CurrentMapNumber, remoteRoom, remoteMessageId);
                    }
                    break;

                // tournament test → needs a tournament-mode global (low-priority backlog, fidelity notes);
                // price → shop/appraise price context (no game-state effect outside a shop). Recognized
                // so they don't corrupt parsing; intentionally inert.
                case "test_tournament":
                case "price":
                    applicable = true;
                    break;

                default:
                    return applicable ? ScriptLineOutcome.Handled : ScriptLineOutcome.NoMatch;
            }
        }

        return applicable ? ScriptLineOutcome.Handled : ScriptLineOutcome.NoMatch;
    }

    // The `random` verb: <textBlockId> holds "threshold:cmds" lines forming ascending probability
    // buckets that end at 100 (e.g. 25/50/75/100). Roll 1..100 and run the first bucket whose
    // threshold the roll reaches; a block with no leading thresholds falls back to a uniform pick.
    private async Task ExecuteRandomBranchAsync(int textBlockId, IReadOnlyCollection<string>? clueKeywords, int depth, bool useRoomCommandMessageSemantics)
    {
        if (depth > 8 || !_world.Database.TextBlocks.TryGetValue(textBlockId, out var text))
            return;

        var weighted = new List<(int Threshold, List<string> Operations)>();
        var unweighted = new List<List<string>>();
        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            var segments = rawLine.Split(':').Select(segment => segment.Trim()).Where(segment => segment.Length > 0).ToList();
            if (segments.Count == 0)
                continue;

            if (int.TryParse(segments[0], out var threshold))
                weighted.Add((threshold, segments.Skip(1).ToList()));
            else
                unweighted.Add(segments);
        }

        List<string>? chosen = null;
        if (weighted.Count > 0)
        {
            int roll = Random.Shared.Next(1, 100);
            foreach (var (threshold, operations) in weighted)
            {
                if (roll <= threshold)
                {
                    chosen = operations;
                    break;
                }
            }
            chosen ??= weighted[^1].Operations;
        }
        else if (unweighted.Count > 0)
        {
            chosen = unweighted[Random.Shared.Next(unweighted.Count)];
        }

        if (chosen is { Count: > 0 })
            await ExecuteScriptLineAsync(chosen, clueKeywords, depth, hasAlternativeMatch: false, useRoomCommandMessageSemantics);
    }

    // `nomonsters` / `needmonster` operate on the room's TRANSIENT monster array
    // (spawned/wandering monsters), not on Room.NPC. A room's permanent NPC (e.g. the High Druid
    // in 7/142) does not count as "a monster in the room" for these verbs — otherwise quest
    // scripts that gate on "no hostiles in the room" would never fire in rooms that intentionally
    // host the quest-giver. Filter IsPermanentNPC out to match.
    // includePermanentNpc: the BARE `nomonsters` form means "the room is EMPTY of every monster",
    // which must include the room's permanent NPC boss — e.g. the Majestic Dragon (1013) that spawns
    // via Room.NPC in 17/2881, whose room-spell teleport (TB 4227 `nomonsters:…:teleport 2980 17`)
    // must hold until the dragon is dead. The `nomonsters <id>` / `needmonster <id>` forms instead
    // target a specific TRANSIENT monster (woodelf guard 289 at 7/142) and must ignore an unrelated
    // permanent quest-giver NPC (the High Druid), so they keep the permanent-NPC exclusion.
    private bool RoomHasLivingMonster(bool includePermanentNpc = false)
        => _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Any(monster => !monster.IsDead && (includePermanentNpc || !monster.IsPermanentNPC));

    // `needmonster <id>` targets a SPECIFIC monster template (e.g. the summoned mirror portal 945).
    // Match by template id regardless of the permanent-NPC flag — a targeted check is inherently
    // specific, so a Room.NPC prop would count too.
    private bool RoomHasLivingMonster(int monsterTemplateId)
        => _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Any(monster => !monster.IsDead && monster.Template.Number == monsterTemplateId);

    private bool RoomContainsGroundItem(int itemId)
        => _world.GetVisibleGroundItems(_player.CurrentMapNumber, _player.CurrentRoomNumber).Any(g => g.ItemId == itemId)
            || _world.GetHiddenGroundItems(_player.CurrentMapNumber, _player.CurrentRoomNumber).Any(g => g.ItemId == itemId);

    // Presence test for the `roomitem <id> <ref>` gate. Covers the dynamic ground / placed items that
    // RoomHasItem knows about, PLUS items configured in the room's static HiddenItems list. The latter
    // matters for permanent non-gettable props like the Mossy Cave waterfall (#993): they're never
    // seeded into ground state, but stock still counts them as "in the room", so the levitation gate
    // passes in 3/1 and fails everywhere else.
    private bool RoomHasGateItem(int itemId)
    {
        if (_world.RoomHasItem(_player.CurrentMapNumber, _player.CurrentRoomNumber, itemId))
            return true;
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        return room != null && room.GetHiddenItemIds().Contains(itemId);
    }

    // Narrate a remoteaction's Message. Standard message convention: Line1 = to the ACTOR ("You turn
    // the book-stand…"), Line2 = to the ROOM ("%s turns the book-stand…", %s = the actor's name).
    // Substitute %s and split the lines accordingly — the previous code dumped EVERY line to the whole
    // room (actor included), so the actor saw a duplicate "%s turns…" line with a literal, unsubstituted
    // %s. Matches SendTriggeredRoomMessageAsync's actor/room split.
    private async Task BroadcastMessageToRemoteRoom(int mapNumber, int roomNumber, int messageId)
    {
        if (messageId <= 0 || !_world.Database.Messages.TryGetValue(messageId, out var message))
            return;

        string actorLine = FormatTriggeredMessageLine(message.Line1);
        if (!string.IsNullOrWhiteSpace(actorLine))
            await _client.SendLineAsync($"{MudAnsi.Green}{actorLine}{MudAnsi.Reset}");

        string roomLine = FormatTriggeredMessageLine(message.Line2);
        if (!string.IsNullOrWhiteSpace(roomLine))
            _world.BroadcastToRoom(mapNumber, roomNumber, $"{MudAnsi.Green}{roomLine}{MudAnsi.Reset}", _client);
    }

    // The `roomtext` verb: display a text block's lines to the whole room.
    private void BroadcastTextBlockToRoom(int textBlockId)
    {
        if (!_world.Database.TextBlocks.TryGetValue(textBlockId, out var text))
            return;

        foreach (var rawLine in text.Replace("\r", string.Empty).Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, $"{MudAnsi.Green}{line}{MudAnsi.Reset}");
        }
    }

    // testskill/checkskill: map a skill name → the player's value AND the per-skill
    // roll cap used by the probabilistic testskill (a roll of 0..cap-1). Stock caps: stats 150,
    // thief/cast skills 175, perception 110, current_hp 100.
    private bool TryGetNamedSkillCheck(string name, out int value, out int cap)
    {
        switch (name.ToLowerInvariant())
        {
            case "agility": value = _player.Agility; cap = 150; return true;
            case "strength": value = _player.Strength; cap = 150; return true;
            case "intellect": value = _player.Intellect; cap = 150; return true;
            case "wisdom": case "willpower": value = _player.Willpower; cap = 150; return true;
            case "health": value = _player.Health; cap = 150; return true;
            case "charm": value = _player.Charm; cap = 150; return true;
            case "spellcasting": value = _player.SpellCasting; cap = 175; return true;
            case "perception": value = _player.Perception; cap = 110; return true;
            case "stealth": value = _player.Stealth; cap = 175; return true;
            case "thievery": value = _player.Thievery; cap = 175; return true;
            case "traps": value = _player.Traps; cap = 175; return true;
            case "picklocks": value = _player.Picklocks; cap = 175; return true;
            case "tracking": value = _player.Tracking; cap = 175; return true;
            case "magicresistance": case "magicres": value = _player.MagicResist; cap = 150; return true;
            case "current_hp": case "currenthp": value = _player.CurrentHP; cap = 100; return true;
            default: value = 0; cap = 100; return false;
        }
    }

    // givecoins denomination letter → copper multiplier (R/P/G/S/C; default copper).
    private static long GetCurrencyLetterMultiplier(string denomination)
    {
        char letter = string.IsNullOrEmpty(denomination) ? 'c' : char.ToUpperInvariant(denomination[0]);
        return letter switch
        {
            'R' => CurrencyHelper.CopperPerRunic,
            'P' => CurrencyHelper.CopperPerPlatinum,
            'G' => CurrencyHelper.CopperPerGold,
            'S' => CurrencyHelper.CopperPerSilver,
            _ => 1,
        };
    }

    private async Task ShowScriptReferenceAsync(int referenceId, IReadOnlyCollection<string>? clueKeywords, int depth, bool useRoomCommandMessageSemantics = false)
    {
        if (_world.Database.Messages.TryGetValue(referenceId, out var message))
        {
            // Room-command failure messages (minlevel/checkability/etc. routing to a Messages row)
            // follow the same Line1-to-actor / Line2-broadcast-to-room split as `message N` ops, with
            // %s substituted to the player's name. NPC dialogue references keep the legacy behavior
            // of streaming all non-empty lines through the dialogue renderer.
            if (useRoomCommandMessageSemantics)
            {
                await SendTriggeredRoomMessageAsync(referenceId, line1Color: MudAnsi.White, line2Color: MudAnsi.White);
                return;
            }

            await SendDialogueLinesAsync(message.GetNonEmptyLines(), clueKeywords);
            return;
        }

        await TryExecuteTextBlockAsync(referenceId, null, clueKeywords, depth, useRoomCommandMessageSemantics);
    }

    /// <summary>Run a testskill/checkskill fail-reference the way stock does:
    /// execute it as a TEXTBLOCK. Crucially this is
    /// TEXTBLOCK-FIRST, so a number that exists as both a textblock and a Messages row resolves to the
    /// textblock — e.g. the Puzzle Door's 710 runs TextBlock 710 ("cast 452" = trap lightning) instead
    /// of being shadowed by Message 710 ("The %s slashes you…"). Only when no textblock exists do we
    /// fall back to the message (a handful of skill-fail refs, e.g. 632/4200, are message-only).</summary>
    private async Task ExecuteScriptReferenceAsTextBlockAsync(int referenceId, IReadOnlyCollection<string>? clueKeywords, int depth, bool useRoomCommandMessageSemantics)
    {
        if (_world.Database.TextBlocks.ContainsKey(referenceId))
        {
            await TryExecuteTextBlockAsync(referenceId, null, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
            return;
        }

        await ShowScriptReferenceAsync(referenceId, clueKeywords, depth + 1, useRoomCommandMessageSemantics);
    }

    private async Task SendDialogueLinesAsync(IEnumerable<string> lines, IReadOnlyCollection<string>? clueKeywords)
    {
        var renderPlans = BuildDialogueRenderPlans(lines, clueKeywords, _player.PaletteId);
        if (renderPlans.Count == 0)
            return;

        // Emit the whole dialogue block in ONE write. Inline-ANSI blocks (e.g. the wounded
        // messenger, textblock 317) bake their colour in as raw ESC sequences and rely on it
        // PERSISTING across newlines: ESC[0;32m green is set once on the first line, clue words
        // toggle to ESC[0;1;32m and back, the game note switches to ESC[0;36m, and the block resets
        // only at the very end. Sending it line-by-line through SendLineAsync would let that path's
        // bleed-guard prepend ESC[0m to every continuation line that doesn't start with its own
        // escape (the CWGamingServ "reset colour before raw-text lines" fix for *Combat Off*),
        // wiping the colour mid-paragraph. The block is self-contained — its first line leads with
        // a colour and its last plan is a trailing reset — so a single SendAsync keeps the
        // cross-line persistence intact while still never bleeding into or out of adjacent output.
        await _client.SendAsync(BuildDialogueBlockPayload(renderPlans));
    }

    // Flatten render plans into the single payload SendDialogueLinesAsync writes: each plan's text,
    // followed by CRLF when it carries one. Kept internal+static so the cross-line colour-persistence
    // contract (no interior reset injected between paragraph lines) can be unit-tested directly.
    internal static string BuildDialogueBlockPayload(IReadOnlyList<DialogueRenderPlan> renderPlans)
    {
        var builder = new StringBuilder();
        foreach (var renderPlan in renderPlans)
        {
            builder.Append(renderPlan.Text);
            if (renderPlan.AppendNewLine)
                builder.Append("\r\n");
        }

        return builder.ToString();
    }

    // Test entry point retained: callers without a player context resolve against the default
    // (stock) palette so unit tests don't have to thread palette ids through.
    internal static DialogueRenderPlan BuildDialogueRenderPlan(string line, IReadOnlyCollection<string>? clueKeywords)
        => BuildDialogueRenderPlan(line, clueKeywords, paletteId: 0);

    internal static DialogueRenderPlan BuildDialogueRenderPlan(string line, IReadOnlyCollection<string>? clueKeywords, int paletteId)
    {
        if (line.Length == 0)
            return new(string.Empty, AppendNewLine: true);

        if (line.Contains("\x1b[", StringComparison.Ordinal))
        {
            if (IsAnsiControlOnlyLine(line))
                return new(line, AppendNewLine: false);

            return new($"{line}{MudAnsi.Reset}", AppendNewLine: true);
        }

        // Stock fidelity: the long-text renderer prints textblock bytes verbatim and
        // never highlights ASK keywords — breadcrumb coloring is whatever inline ANSI the author baked
        // into the data (e.g. block 1410 wraps "Phoenix" in ESC[0;1;32m), which we render verbatim
        // above. We do NOT synthesize keyword highlights from the (noisy) ASK routing table, which
        // over-lit lore words like "Brother". We DO keep the green narrative wrap: stock NPC speech is
        // green, and wrapping each line self-colors it so the CWGamingServ ESC[0m bleed-guard can't
        // wipe the color. clueKeywords is retained on the signature for callers but no longer used.
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);
        string narrativeColor = palette.Get(GameColorRole.NpcDialogueNarrative);
        return new($"{narrativeColor}{line}{MudAnsi.Reset}", AppendNewLine: true);
    }

    internal static IReadOnlyList<DialogueRenderPlan> BuildDialogueRenderPlans(IEnumerable<string> lines, IReadOnlyCollection<string>? clueKeywords)
        => BuildDialogueRenderPlans(lines, clueKeywords, paletteId: 0);

    // The long-text renderer prints each STORED textblock record verbatim (no runtime wrap) —
    // the ~79-col wrapping in stock is baked into the data as one ~79-char record per line. Our
    // Btrieve importer rebuilds blocks from fragment pages and loses that per-record granularity, so some
    // paragraphs arrive as one very long line. Re-wrap narrative prose to the authored width to restore the
    // stock look. 79 is measured: textblock line lengths cluster at 75-79 then cliff hard at 80 (221 lines
    // at len 79, 51 at 80). Count is VISIBLE width — ANSI escapes don't occupy columns.
    private const int DialogueWrapWidth = 79;

    // Greedy word-wrap of one line to <= width VISIBLE columns. ANSI CSI escapes (ESC[...m) are atomic and
    // free (not counted, never severed); a break prefers the last space (dropped at the break), falling back
    // to a hard cut only for an unbroken run. Colour is NOT reset at a break — the active SGR carries to the
    // continuation line, matching the dialogue block's single-write colour-persistence contract.
    internal static IEnumerable<string> WrapDialogueLineToWidth(string line, int width)
    {
        if (line.Length <= width)   // raw length <= width ⇒ visible length <= width; nothing to wrap
        {
            yield return line;
            yield break;
        }

        var current = new StringBuilder();
        int visible = 0, lastSpacePos = -1, visibleAtSpace = 0, n = line.Length;
        for (int i = 0; i < n;)
        {
            char c = line[i];
            if (c == '\x1b' && i + 1 < n && line[i + 1] == '[')
            {
                int start = i;
                i += 2;
                while (i < n && !(line[i] >= '@' && line[i] <= '~')) i++;
                if (i < n) i++;
                current.Append(line, start, i - start);   // escape: emitted, not counted
                continue;
            }

            current.Append(c);
            i++;
            if (c == ' ') { lastSpacePos = current.Length - 1; visibleAtSpace = visible; }
            visible++;

            if (visible > width)
            {
                if (lastSpacePos >= 0)
                {
                    yield return current.ToString(0, lastSpacePos);          // prefix before the space
                    string remainder = current.ToString(lastSpacePos + 1, current.Length - lastSpacePos - 1);
                    current.Clear();
                    current.Append(remainder);
                    visible -= visibleAtSpace + 1;
                }
                else
                {
                    yield return current.ToString();   // unbroken run: hard cut
                    current.Clear();
                    visible = 0;
                }
                lastSpacePos = -1; visibleAtSpace = 0;
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    internal static IReadOnlyList<DialogueRenderPlan> BuildDialogueRenderPlans(IEnumerable<string> lines, IReadOnlyCollection<string>? clueKeywords, int paletteId)
    {
        List<string> bufferedLines = lines.SelectMany(line => WrapDialogueLineToWidth(line, DialogueWrapWidth)).ToList();
        if (bufferedLines.Count == 0)
            return [];

        bool hasInlineAnsi = bufferedLines.Any(line => line.Contains("\x1b[", StringComparison.Ordinal));
        if (!hasInlineAnsi)
            return bufferedLines.Select(line => BuildDialogueRenderPlan(line, clueKeywords, paletteId)).ToArray();

        List<DialogueRenderPlan> renderPlans = new(bufferedLines.Count + 1);
        string narrativeColor = GameColorPalettes.Resolve(paletteId).Get(GameColorRole.NpcDialogueNarrative);
        bool leadingColorApplied = false;
        foreach (string line in bufferedLines)
        {
            if (line.Length == 0)
            {
                renderPlans.Add(new(string.Empty, AppendNewLine: true));
                continue;
            }

            if (IsAnsiControlOnlyLine(line))
            {
                renderPlans.Add(new(line, AppendNewLine: false));
                continue;
            }

            // Open the block's first VISIBLE line in narrative green when the author didn't bake a
            // leading color into it. Inline-ANSI blocks bake toggles to bright-green for ASK keywords
            // and back to normal green (water portal block 2767: "…use my [0;1;32mwater portal[0;32m.")
            // but ASSUME a green paragraph baseline — stock sets it before printing. Block 2767's
            // opener "Seher'Sahham says, …" carries no leading escape, so it fell back to terminal-
            // default white. Lines that already start with their own escape (block 317's green opener,
            // the white ESC[0m courier note) keep the authored color untouched.
            if (!leadingColorApplied)
            {
                leadingColorApplied = true;
                if (!line.StartsWith("\x1b[", StringComparison.Ordinal))
                {
                    renderPlans.Add(new($"{narrativeColor}{line}", AppendNewLine: true));
                    continue;
                }
            }

            renderPlans.Add(new(line, AppendNewLine: true));
        }

        renderPlans.Add(new(MudAnsi.Reset, AppendNewLine: false));
        return renderPlans;
    }

    internal static bool IsAnsiControlOnlyLine(string line)
    {
        return DialogueAnsiSequenceRegex.Replace(line, string.Empty).Trim().Length == 0;
    }

    private static string NormalizeDialoguePhrase(string phrase)
    {
        return Regex.Replace(phrase.ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
    }

    private static string NormalizeQuestEntityPhrase(string phrase)
    {
        return NormalizeDialoguePhrase(phrase);
    }

    private static bool PhrasesExactlyMatch(string left, string right)
    {
        string normalizedLeft = NormalizeQuestEntityPhrase(left);
        string normalizedRight = NormalizeQuestEntityPhrase(right);

        return normalizedLeft.Length > 0 &&
               normalizedLeft.Equals(normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PhrasesRoughlyMatch(string left, string right)
    {
        string normalizedLeft = NormalizeQuestEntityPhrase(left);
        string normalizedRight = NormalizeQuestEntityPhrase(right);

        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            return false;

        if (normalizedLeft == normalizedRight)
            return true;

        if (normalizedLeft.StartsWith(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
            normalizedRight.StartsWith(normalizedLeft, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var leftWords = normalizedLeft.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightWords = normalizedRight.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var rightWordSet = rightWords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftWordSet = leftWords.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return leftWords.All(word => rightWordSet.Contains(word)) ||
               rightWords.All(word => leftWordSet.Contains(word));
    }

    // Precise room-CMD give match: the NPC target matches AND the item handed over (already resolved to
    // its canonical name by the HandleGive rewrite) NORMALIZED-EQUALS one of this line's takeitem items.
    // Exact item-name equality (not the prefix/subset leniency of TryMatchGiveTrigger) keeps sibling
    // turn-ins in the same block distinct — e.g. the orc-head line (takeitem 1101) and the warlord-head
    // line (takeitem 1335) never cross-fire even when the player carries both.
    private bool TryMatchRoomCommandGiveTrigger(DialogueScriptLine scriptLine, string triggerInput)
    {
        if (!TryParseGiveIntent(scriptLine.TriggerPhrase!, out var expectedGive)
            || !TryParseGiveIntent(triggerInput, out var actualGive))
            return false;

        bool targetMatches = expectedGive.TargetNpc != null && actualGive.TargetNpc != null
            ? ReferenceEquals(expectedGive.TargetNpc, actualGive.TargetNpc)
            : PhrasesExactlyMatch(expectedGive.TargetPhrase, actualGive.TargetPhrase);
        if (!targetMatches)
            return false;

        string normalizedItem = NormalizeQuestEntityPhrase(actualGive.ItemPhrase);
        if (normalizedItem.Length == 0)
            return false;

        foreach (var itemId in GetScriptReferencedItemIds(scriptLine.Operations))
        {
            if (_world.Database.Items.TryGetValue(itemId, out var item)
                && NormalizeQuestEntityPhrase(item.Name) == normalizedItem)
                return true;
        }

        return false;
    }

    private bool TryMatchGiveTrigger(string triggerPhrase, string triggerInput, IReadOnlyList<string> operations)
    {
        if (!TryParseGiveIntent(triggerPhrase, out var expectedGive) ||
            !TryParseGiveIntent(triggerInput, out var actualGive))
        {
            return PhrasesRoughlyMatch(triggerPhrase, triggerInput);
        }

        bool targetMatches = expectedGive.TargetNpc != null && actualGive.TargetNpc != null
            ? ReferenceEquals(expectedGive.TargetNpc, actualGive.TargetNpc)
            : PhrasesRoughlyMatch(expectedGive.TargetPhrase, actualGive.TargetPhrase);

        if (!targetMatches)
            return false;

        var referencedItemIds = GetScriptReferencedItemIds(operations);
        foreach (var itemId in referencedItemIds)
        {
            if (_world.Database.Items.TryGetValue(itemId, out var item) &&
                ItemPhraseMatches(actualGive.ItemPhrase, item.Name))
            {
                return true;
            }
        }

        return ItemPhraseMatches(actualGive.ItemPhrase, expectedGive.ItemPhrase);
    }

    private bool TryParseGiveIntent(string command, out GiveIntent intent)
    {
        intent = new GiveIntent();

        if (!IsGiveCommand(command))
            return false;

        string remainder = command[4..].Trim();
        if (remainder.Length == 0)
            return false;

        int toIndex = remainder.LastIndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (toIndex > 0)
        {
            string itemPhrase = remainder[..toIndex].Trim();
            string targetPhrase = remainder[(toIndex + 4)..].Trim();
            var targetNpc = TryFindMonsterInCurrentRoomByPhrase(targetPhrase);
            if (targetNpc != null)
            {
                intent = new GiveIntent
                {
                    ItemPhrase = itemPhrase,
                    TargetPhrase = targetPhrase,
                    TargetNpc = targetNpc,
                };
                return true;
            }
        }

        var monsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead)
            .ToList();

        foreach (var monster in monsters)
        {
            foreach (var candidateName in GetConversationNames(monster))
            {
                if (!remainder.StartsWith(candidateName + " ", StringComparison.OrdinalIgnoreCase))
                    continue;

                intent = new GiveIntent
                {
                    ItemPhrase = remainder[candidateName.Length..].Trim(),
                    TargetPhrase = candidateName,
                    TargetNpc = monster,
                };
                return true;
            }
        }

        return false;
    }

    private MonsterInstance? TryFindMonsterInCurrentRoomByPhrase(string phrase)
    {
        string normalizedPhrase = NormalizeQuestEntityPhrase(phrase);
        var monsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead)
            .ToList();

        foreach (var monster in monsters)
        {
            foreach (var candidateName in GetConversationNames(monster))
            {
                if (NormalizeQuestEntityPhrase(candidateName) == normalizedPhrase)
                    return monster;
            }
        }

        foreach (var monster in monsters)
        {
            foreach (var candidateName in GetConversationNames(monster))
            {
                if (PhrasesRoughlyMatch(candidateName, normalizedPhrase))
                    return monster;
            }
        }

        return null;
    }

    private bool HasIncompleteRoomCommandPrefixConflict(string command)
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
            return false;

        string normalizedCommand = NormalizeDialoguePhrase(command);
        if (normalizedCommand.Length == 0)
            return false;

        foreach (var exit in _world.GetTextCommandExits(room))
        {
            foreach (var phrase in exit.GetCommandPhrases(_world.Database.Messages))
            {
                if (IsIncompletePhrasePrefix(normalizedCommand, NormalizeDialoguePhrase(phrase)))
                    return true;
            }
        }

        if (room.CMD <= 0 || !_world.Database.TextBlocks.TryGetValue(room.CMD, out var roomCommandText))
            return false;

        var roomCommandBlock = ParseDialogueBlock(roomCommandText);
        foreach (var scriptLine in roomCommandBlock.ScriptLines)
        {
            if (string.IsNullOrWhiteSpace(scriptLine.TriggerPhrase))
                continue;

            if (IsIncompletePhrasePrefix(normalizedCommand, NormalizeDialoguePhrase(scriptLine.TriggerPhrase)))
                return true;
        }

        return false;
    }

    private static bool IsIncompletePhrasePrefix(string normalizedCommand, string normalizedPhrase)
    {
        if (normalizedPhrase.Length <= normalizedCommand.Length)
            return false;

        if (!normalizedPhrase.StartsWith(normalizedCommand, StringComparison.OrdinalIgnoreCase))
            return false;

        return normalizedPhrase[normalizedCommand.Length] == ' ';
    }

    private static HashSet<int> GetScriptReferencedItemIds(IReadOnlyList<string> operations)
    {
        var itemIds = new HashSet<int>();

        foreach (var operation in operations)
        {
            var args = operation.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (args.Length < 2 || !int.TryParse(args[1], out var itemId) || itemId <= 0)
                continue;

            string opName = GetScriptOperationName(operation);
            if (opName is "checkitem" or "takeitem" or "failitem")
                itemIds.Add(itemId);
        }

        return itemIds;
    }

    private static bool ItemPhraseMatches(string inputPhrase, string candidatePhrase)
    {
        string normalizedInput = NormalizeQuestEntityPhrase(inputPhrase);
        string normalizedCandidate = NormalizeQuestEntityPhrase(candidatePhrase);

        if (normalizedInput.Length == 0 || normalizedCandidate.Length == 0)
            return false;

        if (normalizedInput == normalizedCandidate)
            return true;

        if (normalizedCandidate.StartsWith(normalizedInput, StringComparison.OrdinalIgnoreCase) ||
            normalizedInput.StartsWith(normalizedCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var candidateWords = normalizedCandidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return normalizedInput.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(word => candidateWords.Contains(word));
    }

    private static bool IsGiveCommand(string text)
    {
        return text.TrimStart().StartsWith("give ", StringComparison.OrdinalIgnoreCase);
    }

    private int GetCurrentAbilityValue(int abilityId)
    {
        return abilityId switch
        {
            9 or 27 or 102 or 103 => _player.Stealth,
            13 => _player.Illumination,
            14 => _player.RoomIllumination,
            29 => _player.HasPunch ? 1 : 0,
            30 => _player.HasKick ? 1 : 0,
            31 => _player.HasBash ? 1 : 0,
            35 => _player.HasJumpkick ? 1 : 0,
            57 => _player.HasSeeHidden ? 1 : 0,
            77 => _player.Perception,
            186 => _player.HasPerfectStealth ? 1 : 0,
            _ => _player.GetQuestAbilityValue(abilityId),
        };
    }

    // True if any of the player's currently-active spells grants this ability, regardless of its
    // value. Quest ability aggregation (GetCurrentAbilityValue) sums stored magnitudes, so a rune
    // carried at value 0 (e.g. "morukai temp" #614, ability 152) is invisible to it — this is the
    // presence probe `failability` needs to honour a spell-driven quest timer.
    private bool PlayerHasActiveSpellAbility(int abilityId)
    {
        foreach (var active in _player.ActiveSpells)
        {
            if (active.SpellId > 0
                && _world.Database.Spells.TryGetValue(active.SpellId, out var spell)
                && spell.Abilities.ContainsKey(abilityId))
            {
                return true;
            }
        }

        return false;
    }

    private void RecalculatePlayerDerivedStats()
    {
        if (!_world.Database.Races.TryGetValue(_player.RaceId, out var race) ||
            !_world.Database.Classes.TryGetValue(_player.ClassId, out var cls))
        {
            return;
        }

        int currentHp = _player.CurrentHP;
        int currentMana = _player.CurrentMana;

        _player.RecalculateStats(race, cls, _world.Database);
        RecalcEquipment();

        _player.CurrentHP = Math.Min(currentHp, _player.MaxHP);
        _player.CurrentMana = Math.Min(currentMana, _player.MaxMana);
    }

    private bool TryRemoveCarriedItemById(int itemId, out bool wasEquipped)
    {
        wasEquipped = false;

        if (TryFindInventoryItemById(itemId, out int inventoryIndex, out long inventoryInstanceId)
            && TryRemoveInventoryItemAt(inventoryIndex, out _, out _))
        {
            RemoveLightStateIfNotCarried(inventoryInstanceId);
            _world.RemoveItemRuntimeState(inventoryInstanceId);
            return true;
        }

        string? equippedSlot = null;
        foreach (var (slot, equippedItemId) in _player.Equipment)
        {
            if (equippedItemId != itemId)
                continue;

            equippedSlot = slot;
            break;
        }

        if (equippedSlot == null)
            return false;

        long equippedInstanceId = GetEquipmentInstanceId(equippedSlot, itemId);
        _player.Equipment.Remove(equippedSlot);
        _player.EquipmentInstanceIds.Remove(equippedSlot);
        wasEquipped = true;
        RemoveLightStateIfNotCarried(equippedInstanceId);
        _world.RemoveItemRuntimeState(equippedInstanceId);
        return true;
    }

    private static bool TryParseReference(string[] args, int startIndex, out int referenceId)
    {
        referenceId = 0;
        return args.Length > startIndex && int.TryParse(args[startIndex], out referenceId) && referenceId > 0;
    }
}