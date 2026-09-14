using System.Text;
using mmudreborn.Game;

namespace mmudreborn.Server;

/// <summary>
/// Renders a player's prompt according to their StatlineMode:
///   0 = OFF (minimal ":" prompt), 1 = ON (standard [HP/MA] prompt), 2 = FULL (same statline),
///   3 = CUSTOM, 4 = FULL CUSTOM. The custom modes substitute the stock
///   %-variables (see `help statline custom`).
/// </summary>
public static class Statline
{
    public const int ModeOff = 0;
    public const int ModeOn = 1;
    public const int ModeFull = 2;
    public const int ModeCustom = 3;
    public const int ModeFullCustom = 4;

    /// <summary>
    /// Build the prompt string for a player. <paramref name="standardPrompt"/> is the already-built
    /// ON/FULL prompt (palette-aware via GameAnsi, plain via MudAnsi); we only override it for the
    /// OFF and CUSTOM modes so both prompt builders share one code path.
    /// </summary>
    public static string Render(Game.Player player, string standardPrompt)
    {
        switch (player.StatlineMode)
        {
            case ModeOff:
                // A 0-mode statline is just the bare prompt terminator.
                return $"{MudAnsi.LinePreamble}:";

            case ModeCustom:
            case ModeFullCustom:
                if (string.IsNullOrEmpty(player.CustomStatline))
                    return standardPrompt; // no template configured yet → fall back to the standard line
                return MudAnsi.LinePreamble + Substitute(player, player.CustomStatline) + MudAnsi.Reset;

            default: // ModeOn / ModeFull — the standard prompt
                return standardPrompt;
        }
    }

    /// <summary>
    /// Substitute the custom-statline %-variables. Unknown escapes pass through literally.
    /// Variables (per `help statline custom`):
    ///   %h/%H cur/max HP · %m/%M cur/max mana · %c wealth on hand · %x/%X exp / exp-to-next ·
    ///   %r resting tag · %w warning flag · %fN/%bN fg/bg colour (N=0-7) · %B bold · %N normal ·
    ///   %U underline · %L blink · %R reverse · %d default colours · %n line-break · %% literal '%'.
    /// </summary>
    public static string Substitute(Game.Player player, string template)
    {
        var sb = new StringBuilder(template.Length + 16);
        for (int i = 0; i < template.Length; i++)
        {
            char c = template[i];
            if (c != '%' || i == template.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            char code = template[++i];
            switch (code)
            {
                case 'h': sb.Append(player.CurrentHP); break;
                case 'H': sb.Append(player.MaxHP); break;
                case 'm': sb.Append(player.CurrentMana); break;
                case 'M': sb.Append(player.MaxMana); break;
                case 'c': sb.Append(CurrencyHelper.ToCopper(player)); break;
                case 'x': sb.Append(player.Experience); break;
                case 'X': sb.Append(System.Math.Max(0, player.ExpForNextLevelCached - player.Experience)); break;
                case 'r': if (player.IsResting) sb.Append(" (Resting) "); break;
                case 'w': /* message-waiting/warning flag — not tracked in this model */ break;
                case 'B': sb.Append("\x1b[1m"); break;  // bold
                case 'N': sb.Append("\x1b[0m"); break;  // normal
                case 'U': sb.Append("\x1b[4m"); break;  // underline
                case 'L': sb.Append("\x1b[5m"); break;  // blink
                case 'R': sb.Append("\x1b[7m"); break;  // reverse
                case 'd': sb.Append("\x1b[0m"); break;  // default colours
                case 'n': sb.Append("\r\n"); break;     // line-break
                case '%': sb.Append('%'); break;        // literal percent
                case 'f': // %fN — foreground colour 0-7
                    if (i + 1 < template.Length && template[i + 1] is >= '0' and <= '7')
                        sb.Append("\x1b[3").Append(template[++i]).Append('m');
                    else { sb.Append('%').Append(code); }
                    break;
                case 'b': // %bN — background colour 0-7
                    if (i + 1 < template.Length && template[i + 1] is >= '0' and <= '7')
                        sb.Append("\x1b[4").Append(template[++i]).Append('m');
                    else { sb.Append('%').Append(code); }
                    break;
                default:
                    sb.Append('%').Append(code); // unknown escape passes through
                    break;
            }
        }
        return sb.ToString();
    }
}
