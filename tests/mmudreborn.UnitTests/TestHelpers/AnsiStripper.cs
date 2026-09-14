using System.Text.RegularExpressions;

namespace mmudreborn.UnitTests.TestHelpers;

internal static partial class AnsiStripper
{
    public static string StripPreserveWhitespace(string text)
    {
        return AnsiRegex().Replace(text, string.Empty);
    }

    public static string Strip(string text)
    {
        return StripPreserveWhitespace(text).Trim();
    }

    [GeneratedRegex("\\x1b\\[[0-9;?]*[A-Za-z]")]
    private static partial Regex AnsiRegex();
}
