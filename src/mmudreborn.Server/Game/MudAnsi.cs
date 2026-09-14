namespace mmudreborn.Server;

/// <summary>
/// MMUDREBORN-owned ANSI facade layered over shared low-level escape codes.
/// </summary>
public static class MudAnsi
{
    public static string XtermBlue24 => global::CWGaming.Shared.Ansi.XtermBlue24;
    public static string Reset => global::CWGaming.Shared.Ansi.Reset;
    public static string Bold => global::CWGaming.Shared.Ansi.Bold;
    public static string Black => global::CWGaming.Shared.Ansi.Black;
    public static string Red => global::CWGaming.Shared.Ansi.Red;
    public static string Green => global::CWGaming.Shared.Ansi.Green;
    public static string Yellow => global::CWGaming.Shared.Ansi.Yellow;
    public static string Blue => global::CWGaming.Shared.Ansi.Blue;
    public static string Magenta => global::CWGaming.Shared.Ansi.Magenta;
    public static string Cyan => global::CWGaming.Shared.Ansi.Cyan;
    public static string White => global::CWGaming.Shared.Ansi.White;
    public static string BrightBlack => global::CWGaming.Shared.Ansi.BrightBlack;
    public static string BrightRed => global::CWGaming.Shared.Ansi.BrightRed;
    public static string BrightGreen => global::CWGaming.Shared.Ansi.BrightGreen;
    public static string BrightYellow => global::CWGaming.Shared.Ansi.BrightYellow;
    public static string BrightBlue => global::CWGaming.Shared.Ansi.BrightBlue;
    public static string BrightMagenta => global::CWGaming.Shared.Ansi.BrightMagenta;
    public static string BrightCyan => global::CWGaming.Shared.Ansi.BrightCyan;
    public static string BrightWhite => global::CWGaming.Shared.Ansi.BrightWhite;
    public static string DarkGray => global::CWGaming.Shared.Ansi.DarkGray;
    public static string BgBlack => global::CWGaming.Shared.Ansi.BgBlack;
    public static string BgRed => global::CWGaming.Shared.Ansi.BgRed;
    public static string BgGreen => global::CWGaming.Shared.Ansi.BgGreen;
    public static string BgYellow => global::CWGaming.Shared.Ansi.BgYellow;
    public static string BgBlue => global::CWGaming.Shared.Ansi.BgBlue;
    public static string BgMagenta => global::CWGaming.Shared.Ansi.BgMagenta;
    public static string BgCyan => global::CWGaming.Shared.Ansi.BgCyan;
    public static string BgWhite => global::CWGaming.Shared.Ansi.BgWhite;
    public static string BgBrightBlack => global::CWGaming.Shared.Ansi.BgBrightBlack;
    public static string BgBrightRed => global::CWGaming.Shared.Ansi.BgBrightRed;
    public static string BgBrightGreen => global::CWGaming.Shared.Ansi.BgBrightGreen;
    public static string BgBrightYellow => global::CWGaming.Shared.Ansi.BgBrightYellow;
    public static string BgBrightBlue => global::CWGaming.Shared.Ansi.BgBrightBlue;
    public static string BgBrightMagenta => global::CWGaming.Shared.Ansi.BgBrightMagenta;
    public static string BgBrightCyan => global::CWGaming.Shared.Ansi.BgBrightCyan;
    public static string BgBrightWhite => global::CWGaming.Shared.Ansi.BgBrightWhite;
    public static string WhiteOnBlack => global::CWGaming.Shared.Ansi.WhiteOnBlack;
    public static string LinePreamble => global::CWGaming.Shared.Ansi.LinePreamble;
    public static string ClearScreen => global::CWGaming.Shared.Ansi.ClearScreen;
    public static string ClearLine => global::CWGaming.Shared.Ansi.ClearLine;

    // Statline- and PALETTE-aware prompt. Used by host broadcast reprompts (CurrentPrompt), combat, and
    // server-pushed redraws — they must match the same palette colors as the main game-loop prompt. This
    // delegates to GameAnsi.Prompt(player) (palette-aware); previously it built a White-only prompt, so
    // resting/meditating reprompts on palettes 1-3 redrew the statline in White instead of the palette.
    // (The int overload below stays White = the palette-0 / Megamud-parsed format the unit tests pin.)
    public static string Prompt(Game.Player player) => GameAnsi.Prompt(player);

    // The MMUD [HP=.../MA=...] prompt is a DOOR (game) concern, so it is built here in mmudreborn rather
    // than in the shared BBS ANSI library. The prompt shows the second resource only when the class
    // has one (non-empty label) and the pool is non-zero; its label is "MA" (mana) or "KAI" (kai class),
    // and the value is the CURRENT mana/kai. MegaMud detects the prompt by scanning for the literal "[HP=".
    // Palette-0 prompt (the Megamud-parsed format). Delegates to the single faithful builder so the
    // byte layout — the HP colour banding, the default-text brackets, and the stock tag placement (inside the
    // brackets with no resource, after "]:" with a mana/Kai pool) — stays identical everywhere.
    public static string Prompt(int hp, int maxHp, int ma, int maxMa, bool isResting = false, bool isMeditating = false, string? magicLabel = "MA")
        => GameAnsi.Prompt(hp, maxHp, ma, maxMa, isResting, paletteId: 0, isMeditating, magicLabel);

    public static string MainMenuPrompt() => $"{White}[MMUDREBORN]:{Reset}";
    public static string Error(string text) => global::CWGaming.Shared.Ansi.Error(text);
    public static string Info(string text) => global::CWGaming.Shared.Ansi.Info(text);
    public static string SystemMsg(string text) => global::CWGaming.Shared.Ansi.SystemMsg(text);
}