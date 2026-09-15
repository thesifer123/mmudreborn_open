using System;

namespace mmudreborn.Server;

// QOL: a leading COUNT on the item forms of GET / DROP / GIVE / BUY / SELL / HIDE — "get 10 torch",
// "drop 10 torch", "give 10 torch to bob", "buy 5 torch", "sell 100 oaken staff", "hide 50 orc head".
// Stock only ever counts coins ("get 8 silver"), so an item count is a pure
// convenience and lives behind QolFeature.BulkQuantity (SYSOP CONFIGURE QOL qty). With the feature off
// the leading number is not special and the verbs behave exactly as they do in stock.
//
// Shape of a bulk run: each pass is one ordinary execution of the verb. The run stops at the first pass
// that cannot proceed, and reports itself in ONE summary line naming the count — "You took 3 torches."
// to the player, one matching line to the room, and one running total on the shop verbs. A run that
// moved a single copy renders as the plain stock line, because CountedItemText(1, name) is just the
// name: a plain GET/DROP/GIVE/BUY/SELL is untouched, count and all.
//
// If the run stopped on something the player should hear about — no room left on the floor, a purse
// that ran dry, a recipient who filled up — that refusal follows the summary, so the reader gets what
// happened before why it stopped. Simply running out of copies says nothing extra: you asked for 10,
// the summary already says 7.
public partial class CommandParser
{
    // Safety net only — encumbrance, coin purse, shop stock and what is actually on the floor bound a
    // run long before this does. It exists so a typo ("get 999999 torch") cannot spin the loop.
    private const int MaxBulkQuantity = 1000;

    /// <summary>
    /// Split "10 torch" into a repeat count and the item name the rest of the verb should resolve.
    /// False (leaving the target untouched) when the QOL feature is off, when there is no leading
    /// positive integer, or when nothing follows it.
    /// </summary>
    private bool TryParseBulkQuantity(string target, out int quantity, out string itemName)
    {
        quantity = 1;
        itemName = target;

        if (!_world.IsQolEnabled(QolFeature.BulkQuantity) || string.IsNullOrWhiteSpace(target))
            return false;

        var parts = target.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        // Digits only: "+5"/"-5"/"5th" are item text, not a count. An overflowing run of digits also
        // falls through to the stock name lookup rather than being clamped into a huge run.
        foreach (char c in parts[0])
            if (!char.IsAsciiDigit(c))
                return false;

        if (!int.TryParse(parts[0], out int parsed) || parsed <= 0)
            return false;

        quantity = Math.Min(parsed, MaxBulkQuantity);
        itemName = parts[1].Trim();
        return itemName.Length > 0;
    }

    /// <summary>
    /// How a bulk run names what it moved: the bare item name for a single copy (so the line is the
    /// stock one), "3 torches" for more. A leading article is dropped once a count is prepended —
    /// "a statue of thief" must read "3 statues of thief", never "3 a statue of thief".
    /// </summary>
    internal static string CountedItemText(int count, string itemName)
        => count == 1 ? itemName : $"{count} {PluralizeItemName(StripLeadingArticle(itemName))}";

    private static string StripLeadingArticle(string itemName)
    {
        foreach (string article in new[] { "a ", "an ", "the " })
        {
            if (itemName.StartsWith(article, StringComparison.OrdinalIgnoreCase))
                return itemName[article.Length..];
        }

        return itemName;
    }

    // A name built around a prepositional phrase is pluralised on its HEAD noun, not its last word:
    // "Crest of Arlysia" is many Crests, not many Arlysias. 243 catalog names are of this shape.
    // "and" is deliberately NOT in this list — it joins a compound name whose head IS the last word
    // ("black and white serpent ring"), so treating it as a separator would pluralise "black".
    private static readonly string[] HeadNounSeparators = [" of ", " with ", " from ", " on ", " in ", " for "];

    // English keeps -ves for a small closed set; everything else in -f/-fe just takes -s. The old rule
    // applied -ves to every -f, which produced "skifves", "coives", "saves" — and, for its own
    // documented example, "stafves". Matched on SUFFIX so compounds come along: bookshelf → bookshelves,
    // quarterstaff → quarterstaves. Longest first, so "shelf" wins over "self" over "elf".
    private static readonly (string Suffix, string Plural)[] IrregularPlurals =
    [
        ("potato", "potatoes"), ("tomato", "tomatoes"),
        ("shelf", "shelves"), ("staff", "staves"), ("knife", "knives"), ("thief", "thieves"),
        ("loaf", "loaves"), ("self", "selves"), ("calf", "calves"), ("half", "halves"),
        ("leaf", "leaves"), ("life", "lives"), ("wife", "wives"), ("wolf", "wolves"),
        ("elf", "elves"),
    ];

    /// <summary>
    /// Plural of an item name. Pluralises the head noun — the word before the first prepositional
    /// separator, else the last word — and leaves an already-plural name ("padded boots") alone.
    /// </summary>
    internal static string PluralizeItemName(string itemName)
    {
        // Split around the head noun: everything before it, and the tail carried through verbatim.
        int headEnd = itemName.Length;
        foreach (string separator in HeadNounSeparators)
        {
            int at = itemName.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at < headEnd)
                headEnd = at;
        }

        string beforeTail = itemName[..headEnd];
        string tail = itemName[headEnd..];

        int split = beforeTail.LastIndexOf(' ');
        string head = split < 0 ? string.Empty : beforeTail[..(split + 1)];
        string word = split < 0 ? beforeTail : beforeTail[(split + 1)..];

        if (word.Length == 0)
            return itemName;

        return head + PluralizeWord(word) + tail;
    }

    private static string PluralizeWord(string word)
    {
        string lower = word.ToLowerInvariant();

        foreach (var (suffix, plural) in IrregularPlurals)
        {
            if (lower.EndsWith(suffix, StringComparison.Ordinal))
                return word[..^suffix.Length] + plural;
        }

        return
            lower.EndsWith("ss", StringComparison.Ordinal) ? word + "es"          // brass → brasses
            : lower.EndsWith("s", StringComparison.Ordinal) ? word                // boots, gauntlets
            : lower.EndsWith("x", StringComparison.Ordinal)
                || lower.EndsWith("ch", StringComparison.Ordinal)
                || lower.EndsWith("sh", StringComparison.Ordinal)
                || lower.EndsWith("z", StringComparison.Ordinal) ? word + "es"    // torch → torches
            : lower.Length > 1 && lower.EndsWith("y", StringComparison.Ordinal)
                && !"aeiou".Contains(lower[^2]) ? word[..^1] + "ies"              // ruby → rubies
            : word + "s";                                                          // skiff → skiffs
    }
}
