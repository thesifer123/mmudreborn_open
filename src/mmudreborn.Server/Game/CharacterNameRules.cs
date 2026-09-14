using System;
using System.Collections.Generic;

namespace mmudreborn.Server;

/// <summary>
/// The content guards a new character name must pass, beyond length and existing-character uniqueness
/// (which the caller enforces). Reproduces the stock character-creation checks:
///   - ljnvfy case 0: every character must be alphabetic ("Only ALPHA characters are allowed in a name!").
///   - the new-name handler resolves the name as a command; one that resolves is rejected with
///     "You may not use that name!" — this IS stock's reserved-word list (anything the parser recognizes,
///     e.g. north/get/kill/cast/rest), not a curated table.
///   - the name check scans the monster name file and rejects an
///     EXACT (case-insensitive) monster-name match with "You may not use &lt;name&gt; as your name."
/// </summary>
internal static class CharacterNameRules
{
    /// <summary>
    /// Returns the stock rejection message for <paramref name="candidate"/>, or null when it is allowed.
    /// <paramref name="monsterNames"/> is the full set of monster names (db.Monsters.Values.Select(Name)).
    /// </summary>
    public static string? FindViolation(string candidate, IEnumerable<string> monsterNames)
    {
        string name = (candidate ?? string.Empty).Trim();
        if (name.Length == 0)
            return "You have entered an invalid name!";

        // ALPHA characters only (the stock check is ASCII a-z/A-Z, not Unicode).
        foreach (char c in name)
        {
            if (!char.IsAsciiLetter(c))
                return "Only ALPHA characters are allowed in a name!";
        }

        // A name that resolves as a command verb (full word or a valid
        // abbreviation). Movement verbs live in a separate parser table, checked alongside the registry.
        if (CommandRegistry.MatchesCommand(name) || CommandParser.IsMovementCommand(name))
            return "You may not use that name!";

        // The monster-name scan: exact, case-insensitive match.
        foreach (var monster in monsterNames)
        {
            if (string.Equals(monster, name, StringComparison.OrdinalIgnoreCase))
                return $"You may not use {mmudreborn.Game.Player.NormalizeNamePart(name)} as your name.";
        }

        return null;
    }
}
