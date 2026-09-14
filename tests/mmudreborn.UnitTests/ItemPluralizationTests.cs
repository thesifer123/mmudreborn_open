using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// The bulk QOL lines ("You dropped 7 wooden skiffs.") name what a run moved, so the plural has to hold
// up across the whole 1950-name item catalog — not just the tidy cases. The original rule sent every
// -f to -ves and produced "skifves", "coives", "saves", and, for the very example its own comment
// claimed ("oaken staff" → "oaken staves"), "stafves".
public class ItemPluralizationTests
{
    // English keeps -ves for a closed set; everything else in -f/-fe simply takes -s.
    [Theory]
    [InlineData("wooden skiff", "wooden skiffs")]        // the reported line
    [InlineData("chain coif", "chain coifs")]
    [InlineData("adamantite chainmail coif", "adamantite chainmail coifs")]
    [InlineData("bank safe", "bank safes")]
    [InlineData("knife", "knives")]
    [InlineData("fine throwing knife", "fine throwing knives")]
    [InlineData("shelf", "shelves")]
    public void Words_in_f_take_s_unless_they_are_one_of_the_irregulars(string name, string expected)
    {
        Assert.Equal(expected, CommandParser.PluralizeItemName(name));
    }

    // The irregulars match on SUFFIX so compounds come along for the ride, and longer suffixes win
    // ("bookshelf" is shelf/shelves, not elf/elves).
    [Theory]
    [InlineData("oaken staff", "oaken staves")]
    [InlineData("quarterstaff", "quarterstaves")]
    [InlineData("obsidian runestaff", "obsidian runestaves")]
    [InlineData("thunderstaff", "thunderstaves")]
    [InlineData("Kai battle-staff", "Kai battle-staves")]
    [InlineData("bookshelf", "bookshelves")]
    [InlineData("potato", "potatoes")]
    public void Irregular_plurals_match_on_suffix_longest_first(string name, string expected)
    {
        Assert.Equal(expected, CommandParser.PluralizeItemName(name));
    }

    // A name built around a prepositional phrase pluralises its HEAD noun. 243 catalog names are of
    // this shape, and every one of them used to pluralise the wrong word.
    [Theory]
    [InlineData("Crest of Arlysia", "Crests of Arlysia")]
    [InlineData("Sword of Ozrinom", "Swords of Ozrinom")]
    [InlineData("songsheet of life", "songsheets of life")]
    [InlineData("scroll of prot. from evil", "scrolls of prot. from evil")]
    [InlineData("mantle of the woodsman", "mantles of the woodsman")]
    [InlineData("pig on a spit", "pigs on a spit")]
    [InlineData("scroll with black seal", "scrolls with black seal")]
    public void Prepositional_names_pluralize_the_head_noun(string name, string expected)
    {
        Assert.Equal(expected, CommandParser.PluralizeItemName(name));
    }

    // "and" joins a compound whose head IS the last word, so it must NOT act as a separator —
    // otherwise "black and white serpent ring" would pluralise "black".
    [Theory]
    [InlineData("black and white serpent ring", "black and white serpent rings")]
    [InlineData("rope and grapple", "rope and grapples")]
    public void And_does_not_split_a_compound_name(string name, string expected)
    {
        Assert.Equal(expected, CommandParser.PluralizeItemName(name));
    }

    // Regular rules, including names that are already plural and must be left exactly alone.
    [Theory]
    [InlineData("torch", "torches")]
    [InlineData("ruby", "rubies")]
    [InlineData("brass", "brasses")]
    [InlineData("ninjato", "ninjatos")]
    [InlineData("padded boots", "padded boots")]
    [InlineData("leather gauntlets", "leather gauntlets")]
    [InlineData("astral robes", "astral robes")]
    public void Regular_and_already_plural_names(string name, string expected)
    {
        Assert.Equal(expected, CommandParser.PluralizeItemName(name));
    }

    // A single copy renders as the bare stock line; a count drops a leading article, so the line reads
    // "3 statues of thief" and never "3 a statue of thief".
    [Theory]
    [InlineData(1, "wooden skiff", "wooden skiff")]
    [InlineData(7, "wooden skiff", "7 wooden skiffs")]
    [InlineData(1, "a statue of thief", "a statue of thief")]
    [InlineData(3, "a statue of thief", "3 statues of thief")]
    [InlineData(2, "the Crest of Arlysia", "2 Crests of Arlysia")]
    public void Counted_text_drops_the_article_only_when_it_counts(int count, string name, string expected)
    {
        Assert.Equal(expected, CommandParser.CountedItemText(count, name));
    }
}
