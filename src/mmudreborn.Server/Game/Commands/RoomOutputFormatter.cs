namespace mmudreborn.Server;

public static class RoomOutputFormatter
{
    public static IReadOnlyList<string> WrapEntryList(string prefix, IReadOnlyList<string> entries, string separator = ", ", int width = 79)
    {
        if (entries.Count == 0)
            return Array.Empty<string>();

        var lines = new List<string>();
        var line = prefix;
        string lineBreakSeparator = GetLineBreakSeparator(separator);
        int lineBreakSeparatorVisibleLength = GetVisibleLength(lineBreakSeparator);

        for (int index = 0; index < entries.Count; index++)
        {
            string entry = entries[index];
            bool isLastEntry = index == entries.Count - 1;
            string joiner = line == prefix ? string.Empty : separator;
            string candidate = line + joiner + entry;
            int maxWidth = isLastEntry ? width : width - lineBreakSeparatorVisibleLength;

            if (GetVisibleLength(candidate) > maxWidth && line != prefix)
            {
                lines.Add(line + lineBreakSeparator);
                line = entry;
                continue;
            }

            line = candidate;
        }

        lines.Add(line);
        return lines;
    }

    public static int GetVisibleLength(string text)
    {
        int visible = 0;
        bool inEscape = false;

        foreach (char ch in text)
        {
            if (!inEscape)
            {
                if (ch == '\u001b')
                {
                    inEscape = true;
                    continue;
                }

                visible++;
                continue;
            }

            if (char.IsLetter(ch))
                inEscape = false;
        }

        return visible;
    }

    private static string GetLineBreakSeparator(string separator)
    {
        int resetIndex = separator.LastIndexOf(MudAnsi.Reset, StringComparison.Ordinal);
        if (resetIndex < 0)
            return separator.TrimEnd();

        string beforeReset = separator[..resetIndex].TrimEnd();
        return beforeReset + separator[resetIndex..];
    }
}