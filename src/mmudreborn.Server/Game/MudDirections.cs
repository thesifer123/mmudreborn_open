using System;
using System.Collections.Generic;

namespace mmudreborn.Server;

/// <summary>
/// Shared direction lookups used by both the <see cref="CommandParser"/> and <see cref="GameWorld"/>
/// partial classes. Previously each carried its own private copy of the alias table and the
/// opposite-direction switch; this is the single source of truth.
/// </summary>
internal static class MudDirections
{
    /// <summary>
    /// Maps a movement token (abbreviation or full word, case-insensitive) to its canonical
    /// full-word direction. Includes the full words as identity entries so callers can normalize
    /// either form through a single lookup.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["n"] = "north",
        ["s"] = "south",
        ["e"] = "east",
        ["w"] = "west",
        ["ne"] = "northeast",
        ["nw"] = "northwest",
        ["se"] = "southeast",
        ["sw"] = "southwest",
        ["u"] = "up",
        ["d"] = "down",
        ["north"] = "north",
        ["south"] = "south",
        ["east"] = "east",
        ["west"] = "west",
        ["northeast"] = "northeast",
        ["northwest"] = "northwest",
        ["southeast"] = "southeast",
        ["southwest"] = "southwest",
        ["up"] = "up",
        ["down"] = "down",
    };

    /// <summary>
    /// Returns the opposite of a canonical full-word direction (the "arrive from" direction).
    /// Vertical moves map to the stock arrival wording: up→"below", down→"above". Unknown input is
    /// returned unchanged.
    /// </summary>
    public static string Opposite(string direction) => direction switch
    {
        "north" => "south",
        "south" => "north",
        "east" => "west",
        "west" => "east",
        "northeast" => "southwest",
        "northwest" => "southeast",
        "southeast" => "northwest",
        "southwest" => "northeast",
        "up" => "below",
        "down" => "above",
        _ => direction,
    };
}
