using System.Text;

namespace mmudreborn.Server;

/// <summary>
/// Small shared text helpers used across the combat and command layers.
/// </summary>
internal static class MudText
{
    /// <summary>
    /// Collapses runs of consecutive spaces down to a single space. Used after template expansion,
    /// where an unfilled positional (e.g. an absent possessive) can leave a double space behind.
    /// </summary>
    public static string CollapseSpaces(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var builder = new StringBuilder(value.Length);
        bool previousWasSpace = false;
        foreach (char ch in value)
        {
            if (ch == ' ')
            {
                if (previousWasSpace)
                    continue;

                previousWasSpace = true;
                builder.Append(ch);
                continue;
            }

            previousWasSpace = false;
            builder.Append(ch);
        }

        return builder.ToString();
    }
}
