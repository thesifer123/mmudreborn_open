using System.Text;
using mmudreborn.Data;

namespace mmudreborn.Server;

/// <summary>
/// Shared help-topic lookup and rendering, used by both the in-game HELP command
/// (<see cref="CommandParser"/>) and the character-creation race/class prompt's
/// stock <c>? &lt;topic&gt;</c> path. Rendering only needs an <see cref="IGameClient"/>,
/// so every method here is static — the two callers reach the identical output.
/// </summary>
internal static class HelpTopicRenderer
{
    /// <summary>
    /// Resolves a help topic against the loaded help files, faithful to the original's
    /// Help lookup: exact match first, then the first prefix match ("war" → "warriors").
    /// Returns false when nothing matches.
    /// </summary>
    internal static bool TryResolveTopic(IGameDatabase db, string topic, out string? body, out string? resolvedTopic)
    {
        if (db.HelpTopics.TryGetValue(topic, out var exact))
        {
            body = exact;
            resolvedTopic = topic;
            return true;
        }

        foreach (var kvp in db.HelpTopics)
        {
            if (kvp.Key.StartsWith(topic, StringComparison.OrdinalIgnoreCase))
            {
                body = kvp.Value;
                resolvedTopic = kvp.Key;
                return true;
            }
        }

        body = null;
        resolvedTopic = null;
        return false;
    }

    internal static async Task RenderBodyAsync(IGameClient client, string body)
    {
        var lines = body.Split('\n');
        if (IsBorderedHelpTopic(lines))
        {
            await RenderBorderedHelp(client, lines);
            return;
        }

        await client.SendLineAsync();
        foreach (var line in lines)
            await client.SendLineAsync(line);
        await client.SendLineAsync();
    }

    /// <summary>
    /// Detects bordered help topics (class/race detail pages) by checking for
    /// lines with 2-space indent and trailing blue ANSI escape — the pattern
    /// the MDB data uses for content that the real BBS renders inside CP437
    /// box-drawing borders.
    /// </summary>
    internal static bool IsBorderedHelpTopic(string[] lines)
    {
        int count = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("  ") && EndsWithBlueBorder(line))
                count++;
        }
        return count >= 2;
    }

    private static bool IsBlueBorderSequence(string sequence)
    {
        return sequence == "\x1b[0;34m" || sequence == "\x1b[34m";
    }

    private static bool EndsWithBlueBorder(string text)
    {
        return text.EndsWith("\x1b[0;34m", StringComparison.Ordinal)
            || text.EndsWith("\x1b[34m", StringComparison.Ordinal);
    }

    private static int LastBlueBorderIndex(string text)
    {
        int full = text.LastIndexOf("\x1b[0;34m", StringComparison.Ordinal);
        int shortForm = text.LastIndexOf("\x1b[34m", StringComparison.Ordinal);
        return Math.Max(full, shortForm);
    }

    /// <summary>
    /// Detects whether a bordered help topic has a multi-column layout
    /// (race detail pages with Statistics/Abilities sub-table).
    /// Multi-column topics have internal \x1b[0;34m escapes mid-line
    /// that mark column separator positions.
    /// </summary>
    private static bool IsMultiColumnTopic(string[] lines)
    {
        foreach (var line in lines)
        {
            if (!line.StartsWith("  ") || !EndsWithBlueBorder(line))
                continue;
            string content = line.Substring(2);
            int lastBlue = LastBlueBorderIndex(content);
            if (lastBlue <= 0)
                continue;

            int firstBlue = content.IndexOf("\x1b[0;34m", StringComparison.Ordinal);
            int firstShortBlue = content.IndexOf("\x1b[34m", StringComparison.Ordinal);
            if (firstBlue < 0 || (firstShortBlue >= 0 && firstShortBlue < firstBlue))
                firstBlue = firstShortBlue;

            if (firstBlue >= 0 && firstBlue < lastBlue)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detects an indent line: starts with 2+ spaces but the content after
    /// stripping 2 has 10+ more leading spaces (horizontal separator area
    /// in multi-column layouts).
    /// </summary>
    private static bool IsIndentLine(string line)
    {
        if (!line.StartsWith("  "))
            return false;
        string after = line.Substring(2);
        int spaces = 0;
        foreach (char c in after)
        {
            if (c == ' ') spaces++;
            else break;
        }
        return spaces >= 10;
    }

    /// <summary>
    /// Replaces internal column separator patterns in a bordered content line.
    /// The MDB data uses \x1b[0;34m followed by spaces where the real BBS
    /// renders blue ║ characters. This tracks ANSI color state and replaces
    /// the first space after entering blue mode with ║.
    /// Only processes separators BEFORE the trailing \x1b[0;34m (outer border).
    /// </summary>
    private static string AddInternalColumnBorders(string content)
    {
        int lastBluePos = LastBlueBorderIndex(content);
        if (lastBluePos <= 0) return content;

        var sb = new StringBuilder(content.Length + 10);
        bool inBlue = false;
        int i = 0;

        while (i < lastBluePos)
        {
            if (content[i] == '\x1b' && i + 1 < content.Length && content[i + 1] == '[')
            {
                int seqStart = i;
                i += 2;
                while (i < content.Length && !char.IsLetter(content[i]))
                    i++;
                if (i < content.Length)
                    i++;

                string seq = content[seqStart..i];
                sb.Append(seq);

                // Track color state: Blue sets inBlue, any other SGR clears it
                if (IsBlueBorderSequence(seq))
                    inBlue = true;
                else if (seq.EndsWith('m'))
                    inBlue = false;
                // Cursor movements (C, D, etc.) don't change color

                // After blue escape or cursor movement while in blue,
                // replace first following space with ║
                if (inBlue && i < lastBluePos && content[i] == ' ')
                {
                    sb.Append('║');
                    i++;
                }
            }
            else
            {
                sb.Append(content[i]);
                i++;
            }
        }

        sb.Append(content[lastBluePos..]);
        return sb.ToString();
    }

    /// <summary>
    /// Calculates the visible (printed) width of a string, ignoring ANSI escape
    /// sequences and accounting for cursor-forward (ESC[nC) movements.
    /// </summary>
    private static int VisibleWidth(string s)
    {
        int width = 0;
        int i = 0;
        while (i < s.Length)
        {
            if (s[i] == '\x1b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                string param = "";
                while (i < s.Length && !char.IsLetter(s[i]))
                {
                    param += s[i];
                    i++;
                }
                if (i < s.Length)
                {
                    char terminal = s[i];
                    i++;
                    if (terminal == 'C' && int.TryParse(param, out int n))
                        width += n;
                }
            }
            else
            {
                width++;
                i++;
            }
        }
        return width;
    }

    private static string PadVisibleRight(string text, int targetWidth)
    {
        int visibleWidth = VisibleWidth(text);
        if (visibleWidth >= targetWidth)
            return text;

        return text + new string(' ', targetWidth - visibleWidth);
    }

    private static string StripTrailingBlueBorderMarker(string text)
    {
        // Strip trailing blue color codes first
        if (text.EndsWith("\x1b[0;34m", StringComparison.Ordinal))
            text = text[..^7];
        else if (text.EndsWith("\x1b[34m", StringComparison.Ordinal))
            text = text[..^5];
        else if (text.EndsWith("[0;34m", StringComparison.Ordinal))
            text = text[..^6];
        else if (text.EndsWith("[34m", StringComparison.Ordinal))
            text = text[..^4];

        // Replace trailing cursor positioning sequences like \x1b[12C with spaces
        while (text.Length > 3 && text.EndsWith("C", StringComparison.Ordinal))
        {
            int pos = text.Length - 2;
            while (pos > 0 && char.IsDigit(text[pos]))
                pos--;
            if (pos > 0 && text[pos] == '[' && text[pos - 1] == '\x1b')
            {
                // Extract the number of columns
                string numStr = text.Substring(pos + 1, text.Length - pos - 2);
                if (int.TryParse(numStr, out int columns))
                {
                    // Replace the escape sequence with spaces
                    text = text[..(pos - 1)] + new string(' ', columns);
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return text;
    }

    /// <summary>
    /// Renders a bordered help topic with CP437 box-drawing characters,
    /// matching the original output.
    /// Simple topics: ╔═══╗ / ║ content ║ / ╠═══╣ / ╚═══╝.
    /// Multi-column topics (races): adds internal ║ separators,
    /// ╠═══╗ / ╠═══╦═══╣ indent separators, ╚═══╩═══╩═══╝ bottom.
    /// Border is 77 chars wide (1 + 75 + 1), all in blue (ESC[0;34m).
    /// </summary>
    private static async Task RenderBorderedHelp(IGameClient client, string[] lines)
    {
        const string Blue = "\x1b[0;34m";

        // Calculate inner width from the max visible content width across all bordered lines.
        // Content line format: "  " + content + trailing Blue. We strip "  " prefix,
        // then the rendered line is "║ " + content + "║", so InnerWidth = maxContentVis + 2.
        int maxContentVis = 0;
        foreach (var l in lines)
        {
            if (l.StartsWith("  ") && EndsWithBlueBorder(l))
            {
                string content = l.Substring(2);
                int vis = VisibleWidth(content);
                if (vis > maxContentVis)
                    maxContentVis = vis;
            }
        }
        // InnerWidth = space(1) + maxContentVis + 0 for the trailing ║ which is outside
        // Total line: ║(1) + space(1) + contentVis + ║(1) = maxContentVis + 3
        // Border:     ╔(1) + InnerWidth×═ + ╗(1) = InnerWidth + 2
        // Must match: InnerWidth + 2 = maxContentVis + 3 → InnerWidth = maxContentVis + 1
        int innerWidth = maxContentVis > 0 ? maxContentVis + 1 : 75;
        string horizontalBar = new string('═', innerWidth);

        bool multiColumn = IsMultiColumnTopic(lines);

        // For multi-column topics, find internal column separator positions
        // by scanning for internal \x1b[0;34m markers in content lines.
        int col1Width = 0;  // visible chars before first internal ║
        int col2Width = 0;  // visible chars between first and second internal ║
        if (multiColumn)
        {
            foreach (var l in lines)
            {
                if (!l.StartsWith("  ") || !EndsWithBlueBorder(l)) continue;
                string content = l.Substring(2);
                // Find all blue escape positions
                var bluePositions = new List<(int Position, int Length)>();
                int index = 0;
                while (index < content.Length)
                {
                    if (content[index] == '\x1b' && index + 1 < content.Length && content[index + 1] == '[')
                    {
                        int seqStart = index;
                        index += 2;
                        while (index < content.Length && !char.IsLetter(content[index]))
                            index++;
                        if (index < content.Length)
                            index++;

                        string seq = content[seqStart..index];
                        if (IsBlueBorderSequence(seq))
                            bluePositions.Add((seqStart, seq.Length));
                    }
                    else
                    {
                        index++;
                    }
                }
                if (bluePositions.Count >= 3) // 2 internal + 1 trailing
                {
                    col1Width = VisibleWidth(content[..bluePositions[0].Position]);
                    int afterFirst = bluePositions[0].Position + bluePositions[0].Length;
                    if (afterFirst <= bluePositions[1].Position)
                        col2Width = VisibleWidth(content[afterFirst..bluePositions[1].Position]);
                    break;
                }
            }
        }

        // Clear screen + top border
        await client.SendLineAsync($"\x1b[2J{Blue}╔{horizontalBar}╗");

        // Find first content line (skip header lines: reset, clear screen, etc.)
        int startIdx = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("  "))
            {
                startIdx = i;
                break;
            }
        }

        int indentCount = 0;

        for (int i = startIdx; i < lines.Length; i++)
        {
            string line = lines[i];

            if (string.IsNullOrEmpty(line))
            {
                // Empty line: separator or bottom border
                bool hasMoreContent = false;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    if (!string.IsNullOrEmpty(lines[j]))
                    {
                        hasMoreContent = true;
                        break;
                    }
                }

                if (hasMoreContent)
                    await client.SendLineAsync($"{Blue}╠{horizontalBar}╣");
                else if (multiColumn && col1Width > 0 && col2Width > 0)
                {
                    // Bottom border with ╩ at each column separator position
                    // col1Width visible chars + 1 for ║ space = col1Width + 1 ═ before first ╩
                    int seg1 = col1Width + 1; // includes the leading space after ║
                    int seg2 = Math.Max(0, col2Width - 1);
                    int seg3 = innerWidth - seg1 - 1 - seg2 - 1; // remaining after two ╩
                    await client.SendLineAsync($"{Blue}╚{new string('═', seg1)}╩{new string('═', seg2)}╩{new string('═', seg3)}╝");
                }
                else
                    await client.SendLineAsync($"{Blue}╚{horizontalBar}╝");
            }
            else if (multiColumn && IsIndentLine(line))
            {
                // Indent line in multi-column topic: horizontal separator for inner table
                // Strip all leading spaces to get the content portion
                string stripped = line.TrimStart(' ');
                int totalSpaces = line.Length - stripped.Length;

                string separator;
                if (indentCount == 0)
                {
                    // First indent: top of inner stats box ╠═══╗
                    separator = $"╠{new string('═', totalSpaces - 2)}╗";
                }
                else
                {
                    // Second indent: column split ╠═══╦═══╣
                    int seg1 = col1Width + 1;
                    separator = $"╠{new string('═', seg1)}╦{new string('═', totalSpaces - 2 - seg1 - 1)}╣";
                }

                // Content after the indent gets internal ║ + outer right ║
                string contentPart = StripTrailingBlueBorderMarker(AddInternalColumnBorders(stripped));
                int contentWidth = Math.Max(0, innerWidth - VisibleWidth(separator));
                contentPart = PadVisibleRight(contentPart, contentWidth);
                await client.SendLineAsync($"{Blue}{separator} {contentPart}{Blue}║");

                indentCount++;
            }
            else if (line.StartsWith("  "))
            {
                // Regular content line
                string content = line.Substring(2);
                if (multiColumn)
                    content = AddInternalColumnBorders(content);
                content = StripTrailingBlueBorderMarker(content);
                content = PadVisibleRight(content, Math.Max(0, innerWidth - 1));

                await client.SendLineAsync($"{Blue}║ {content}{Blue}║");
            }
        }

        await client.SendLineAsync();
    }
}
