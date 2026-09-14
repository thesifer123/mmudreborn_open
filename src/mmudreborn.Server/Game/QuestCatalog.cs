namespace mmudreborn.Game;

// Catalog of the staged ("progress-flag") quests and the set of abilities a sysop may grant.
//
// The original tracks a staged quest as a single progress-flag value stored in Player.QuestAbilities:
// the flag is one of the "Quest type" abilities (125-134) or the generic "Quest" ability (50), and
// its value is the current stage. A scan of every `giveability` in game_data."TextBlocks" confirms
// these 11 flags are the COMPLETE set of staged quests — the engine has no other progress slots.
// Everything else `giveability` touches is a one-shot reward ability (see GrantableAbilities), not a
// stage. Other stock "quests" (the Level 10 quest, kill-reward and item quests) are not flag
// quests, have no stage to set, and are intentionally absent here.
//
// CompleteValue is the authoritative max(giveability <flag> N) measured from the live data.
// MultiStep is false for "atomic" reward quests whose flag ticks internally inside a single action
// (e.g. Adult Red Dragon's flag runs 1->4 entirely within one `touch ruby`); those have no
// player-facing intermediate steps and are shown/staged as just complete / not-started.
internal static class QuestCatalog
{
    internal readonly record struct QuestInfo(string Name, int CompleteValue, bool MultiStep);

    private static readonly Dictionary<int, QuestInfo> Quests = new()
    {
        [50]  = new("Witchunter", 1, false),
        [125] = new("Ice Sorceress", 2, false),
        [126] = new("Good Alignment Quest", 31, true),
        [127] = new("Neutral Alignment Quest", 31, true),
        [128] = new("Evil Alignment Quest", 31, true),
        [129] = new("High Druid", 2, false),
        [130] = new("Champion of Blood", 2, false),
        [131] = new("Adult Red Dragon", 4, false),
        [132] = new("Wererat / Apparatus", 2, false),
        [133] = new("Phoenix Feather", 9, true),
        [134] = new("Dao Lord", 12, true),
    };

    // Short aliases accepted by `SYSOP QUEST` in place of the flag number.
    private static readonly Dictionary<string, int> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["witchunter"] = 50,
        ["icesorceress"] = 125, ["sorceress"] = 125, ["ice"] = 125,
        ["good"] = 126,
        ["neutral"] = 127,
        ["evil"] = 128,
        ["druid"] = 129, ["highdruid"] = 129,
        ["champion"] = 130, ["bishop"] = 130, ["blood"] = 130,
        ["dragon"] = 131, ["reddragon"] = 131,
        ["wererat"] = 132, ["apparatus"] = 132,
        ["phoenix"] = 133, ["feather"] = 133,
        ["dao"] = 134, ["daolord"] = 134,
    };

    // Documented quest/item reward enhancement abilities a sysop may grant directly. Everything not in
    // this set is non-grantable by default (the GRANTABILITY command requires `force` to override).
    // Quest-progress flags are deliberately excluded — they are staged via `SYSOP QUEST`.
    private static readonly HashSet<int> GrantableAbilities =
        [2, 4, 9, 22, 27, 32, 34, 57, 58, 69, 70, 117, 118, 152, 186, 187];

    /// <summary>All staged quests, ascending by flag — for the SYSOP QUEST read-only progress view.</summary>
    public static IEnumerable<KeyValuePair<int, QuestInfo>> All
        => Quests.OrderBy(entry => entry.Key);

    public static bool IsQuestFlag(int abilityId) => Quests.ContainsKey(abilityId);

    public static bool TryGet(int flag, out QuestInfo info) => Quests.TryGetValue(flag, out info);

    public static bool IsGrantable(int abilityId) => GrantableAbilities.Contains(abilityId);

    /// <summary>Resolve a quest token — a flag number ("126") or a short alias ("good") — to its flag.</summary>
    public static bool TryResolve(string token, out int flag)
    {
        token = token?.Trim() ?? string.Empty;
        if (int.TryParse(token, out flag) && Quests.ContainsKey(flag))
            return true;
        return Aliases.TryGetValue(token, out flag);
    }
}
