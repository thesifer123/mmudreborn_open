namespace mmudreborn.Server;

public enum TravelMessageType
{
    HiddenTraversal,
    ActionTraversal,
    TextCommandTraversal,
}

/// <summary>
/// Game-specific ANSI wrappers tied to in-realm semantics and messaging.
/// </summary>
public static class GameAnsi
{
    public static string Prompt(int hp, int maxHp, int ma, int maxMa, bool isResting = false, bool isMeditating = false, string? magicLabel = "MA")
        => Prompt(hp, maxHp, ma, maxMa, isResting, 0, isMeditating, magicLabel);

    // Statline-aware prompt: renders OFF/ON/FULL/CUSTOM per the player's StatlineMode, using the
    // palette-coloured standard prompt as the ON/FULL fallback. The second resource (MA/Kai) shows only
    // when the player's CLASS has one (player.MagicResourceLabel) — a stale MaxMana never resurrects it.
    public static string Prompt(Game.Player player)
        => Statline.Render(player, Prompt(player.CurrentHP, player.MaxHP, player.CurrentMana, player.MaxMana,
            player.IsResting, player.PaletteId, player.IsMeditating, player.MagicResourceLabel));

    // The prompt's HP number is colored by how full the bar is — healthy
    // when current > 50% of max, warning when > 25%, critical otherwise (also when dead). Honors palette.
    public static string HpColor(int hp, int maxHp, int paletteId)
    {
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);
        if (maxHp / 2 < hp)
            return palette.Get(GameColorRole.PromptHpHealthy);
        if (maxHp / 4 < hp)
            return palette.Get(GameColorRole.PromptHpWarning);
        return palette.Get(GameColorRole.PromptHpCritical);
    }

    public static string Prompt(int hp, int maxHp, int ma, int maxMa, bool isResting, int paletteId, bool isMeditating = false, string? magicLabel = "MA")
    {
        // The stock prompt format strings, byte-for-byte:
        //   no resource: "[HP=%s%d%s%s]:"          (hpColor, hp, default, TAG)            <- tag INSIDE
        //   with mana:   "[HP=%s%d%s/%s=%s%d%s]:%s" (hpColor, hp, default, label, manaColor,
        //                                            mana, default, TAG)                  <- tag AFTER ]:
        // Brackets/labels are the default-text color; the HP number is percentage-banded; the
        // mana value uses f0b0 (the healthy color). The prompt is led by the default color and carries no
        // trailing reset, so following uncolored output inherits the palette's default text color.
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);
        string def = palette.Get(GameColorRole.DefaultText);
        string hpColor = HpColor(hp, maxHp, paletteId);

        // Resting and meditating are mutually exclusive, so the prompt carries at most one tag.
        string tag = isResting ? " (Resting) " : isMeditating ? " (Meditating) " : string.Empty;

        // Show the second resource only when the class HAS one (non-empty label) and the
        // pool is non-zero (e.g. kai appears at level 2). Value shown is CURRENT mana/kai.
        bool showResource = maxMa > 0 && !string.IsNullOrEmpty(magicLabel);
        if (!showResource)
            return $"{MudAnsi.LinePreamble}{def}[HP={hpColor}{hp}{def}{tag}]:";

        string manaColor = palette.Get(GameColorRole.PromptHpHealthy);
        return $"{MudAnsi.LinePreamble}{def}[HP={hpColor}{hp}{def}/{magicLabel}={manaColor}{ma}{def}]:{tag}";
    }

    public static string RoomName(string name) => RoomName(name, 0);
    public static string RoomName(string name, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.RoomName)}{name}";
    public static string Exits(string exits) => Exits(exits, 0);
    public static string Exits(string exits, int paletteId) => $"{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.Exits)}{exits}";
    public static string DimText(string text) => DimText(text, 0);
    public static string DimText(string text, int paletteId) => $"{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.DimText)}{text}";
    public static string CombatHit(string text) => CombatHit(text, 0);
    public static string CombatHit(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.CombatHit)}{text}{MudAnsi.Reset}";
    public static string CombatMiss(string text) => CombatMiss(text, 0);
    public static string CombatMiss(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.CombatMiss)}{text}{MudAnsi.Reset}";
    // A combat-round line that carries no color of its own in stock — a "no effect / cannot harm"
    // reject (e.g. "Your weapon has no effect against this monster!"). Stock prints these with a plain
    // prf (no SGR), so they render in the prompt's f0a0 default-text color: White on palettes 0/1, Cyan
    // on 2/3. Both the in-round path and the engage-path twins route through here so every reject carries
    // the LinePreamble — a raw (preamble-less) reject flushed alongside a combat round desyncs Megamud's
    // parser and crashes it ("Crash detected in game parsing routines"); CombatReject frames it correctly.
    public static string CombatReject(string text) => CombatReject(text, 0);
    public static string CombatReject(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.DefaultText)}{text}{MudAnsi.Reset}";
    public static string Dodge(string text) => Dodge(text, 0);
    public static string Dodge(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.CombatMiss)}{text}{MudAnsi.Reset}";
    public static string CombatGood(string text) => CombatGood(text, 0);
    public static string CombatGood(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.CombatGood)}{text}{MudAnsi.Reset}";
    public static string Experience(string text) => Experience(text, 0);
    // The "You gain N experience." line carries no color of its own (the experience award
    // prints it with the neutral line-clear prefix) — it renders in the f0a0 default-text color the
    // prompt establishes: White on palettes 0/1, Cyan on 2/3. Use DefaultText, not the always-White
    // InventoryNeutral, and honor the recipient's palette.
    public static string Experience(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.DefaultText)}{text}{MudAnsi.Reset}";
    // *Combat Off* / *Combat Engaged* lines are emitted WITHOUT a trailing ESC[0m reset to match
    // stock exactly — a live capture confirms the
    // stock byte sequence is `ESC[79D ESC[K ESC[0;33m *Combat Off* CR LF` with no reset before
    // the CR LF. Megamud parses these lines pixel-for-pixel; a stray reset breaks its sneak/
    // attack state tracking. The next prompt repaints the color via its own LinePreamble, so the
    // omitted reset is visually invisible but parser-critical.
    public static string CombatOff(string text) => CombatOff(text, 0);
    public static string CombatOff(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.CombatToggle)}{text}";
    public static string CombatEngaged(string text) => CombatEngaged(text, 0);
    public static string CombatEngaged(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.CombatToggle)}{text}";
    public static string SpellHostile(string text) => SpellHostile(text, 0);
    public static string SpellHostile(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.SpellHostile)}{text}{MudAnsi.Reset}";
    public static string SpellBeneficial(string text) => SpellBeneficial(text, 0);
    public static string SpellBeneficial(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.SpellBeneficial)}{text}{MudAnsi.Reset}";
    public static string SpellFailure(string text) => SpellFailure(text, 0);
    public static string SpellFailure(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.SpellFailure)}{text}{MudAnsi.Reset}";
    // A plain line in the palette's DEFAULT text color. Stock prints some lines with no color of their
    // own (just the neutral line-clear prefix), so they inherit the prompt's f0a0 default — White on
    // palettes 0/1, Cyan on 2/3. Used for the confusion fumble ("You fumble in confusion!" / "You look
    // around stupidly and do nothing!"), which carry NO ANSI in stock (verified byte-for-byte:
    // the confusion check writes them plainly, not through a colored format).
    public static string Neutral(string text) => Neutral(text, 0);
    public static string Neutral(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.DefaultText)}{text}{MudAnsi.Reset}";
    public static string PartyNotice(string text) => PartyNotice(text, 0);
    public static string PartyNotice(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.PartyNotice)}{text}{MudAnsi.Reset}";
    public static string AttentionNotice(string text) => AttentionNotice(text, 0);
    public static string AttentionNotice(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.AttentionNotice)}{text}{MudAnsi.Reset}";
    public static string CombatGlance(string text) => CombatGlance(text, 0);
    public static string CombatGlance(string text, int paletteId) => AttentionNotice(text, paletteId);

    /// <summary>
    /// The knocked-unconscious line, byte-for-byte from stock:
    ///     ESC[79D ESC[K  +  ESC[0;37m  +  ESC[41m
    ///     prf("%s drops to the ground!", name)
    ///     BEL             — stock really does beep on this line
    ///     ESC[40m         — background back to black, NOT a full reset
    /// The white is NORMAL (0;37), not bright: we used ESC[1;37m, which is why ours read brighter
    /// than a stock capture side by side. Closing with ESC[40m rather than ESC[0m leaves the
    /// foreground as-is, exactly like the reset-less combat toggles.
    /// </summary>
    public static string DropsToTheGround(string text)
        => $"{MudAnsi.LinePreamble}{MudAnsi.White}{MudAnsi.BgRed}{text}\x07{MudAnsi.BgBlack}";

    /// <summary>
    /// "You have been killed!" — stock prints it behind a prefix which is
    /// ESC[79D ESC[K + ESC[1;31;40m: BRIGHT RED on BLACK, with no trailing reset. We were emitting
    /// bright white on a red background, a different colour entirely rather than a shade off.
    /// (The room twin "%s is dead." uses the same ESC[1;31;40m but carries no
    /// line preamble of its own.)
    /// </summary>
    public static string KilledOutright(string text)
        => $"{MudAnsi.LinePreamble}{MudAnsi.BrightRed}{text}";

    /// <summary>
    /// The room twin of KilledOutright: "%s is dead." is printed behind a prefix which is
    /// ESC[1;31;40m with NO ESC[79D ESC[K of its own (the victim's line carries the frame; this one
    /// rides the same flush to the room). Same bright red as "You have been killed!" — they are meant
    /// to read as one announcement in two directions.
    /// </summary>
    public static string IsDead(string text)
        => $"{MudAnsi.BrightRed}{text}";
    public static string RoomMovementDeparture(string name, string direction)
        => RoomMovementDeparture(name, direction, 0);
    public static string RoomMovementDeparture(string name, string direction, int paletteId)
        => $"{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.PlayerName)}{name}{MudAnsi.Reset}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.PlayerMovementText)}{FormatPlayerDeparture(direction)}{MudAnsi.Reset}";
    public static string RoomMovementArrival(string name, string direction)
        => RoomMovementArrival(name, direction, 0);
    public static string RoomMovementArrival(string name, string direction, int paletteId)
        => $"{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.PlayerName)}{name}{MudAnsi.Reset}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.PlayerMovementText)}{FormatPlayerArrival(direction)}{MudAnsi.Reset}";
    public static string DragNotice(string text) => DragNotice(text, 0);
    public static string DragNotice(string text, int paletteId) => $"{MudAnsi.LinePreamble}{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.DragNotice)}{text}{MudAnsi.Reset}";
    public static string MonsterEntry(string text) => MonsterEntry(text, 0);
    public static string MonsterEntry(string text, int paletteId) => $"{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.MonsterHostile)}{text}{MudAnsi.Reset}";
    public static string AlsoHere(string text) => AlsoHere(text, 0);
    public static string AlsoHere(string text, int paletteId) => $"{GameColorPalettes.Resolve(paletteId).Get(GameColorRole.AlsoHereLabel)}{text}{MudAnsi.Reset}";

    private static string FormatPlayerDeparture(string direction) => direction switch
    {
        "up" => " just left upwards.",
        "down" => " just left downwards.",
        _ => $" just left to the {direction}.",
    };

    private static string FormatPlayerArrival(string direction) => direction switch
    {
        "above" => " walks into the room from above.",
        "below" => " walks into the room from below.",
        _ => $" walks into the room from the {direction}.",
    };

    public static string TravelSelfMessageColor(TravelMessageType type, int paletteId = 0)
    {
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);
        return type switch
        {
            TravelMessageType.TextCommandTraversal => palette.Get(GameColorRole.TravelTextCommand),
            TravelMessageType.HiddenTraversal => palette.Get(GameColorRole.TravelHidden),
            TravelMessageType.ActionTraversal => palette.Get(GameColorRole.TravelAction),
            _ => palette.Get(GameColorRole.TravelAction),
        };
    }

    // Monster display colors by alignment
    // Hostile (Align 1,2,5,6) = BrightMagenta, Guards/Templars (Align 4) = White, Peaceful (Align 0,3) = Cyan
    public static string MonsterName(string name, int align) => align switch
    {
        1 => $"{MudAnsi.BrightMagenta}{name}{MudAnsi.Reset}",
        2 => $"{MudAnsi.BrightMagenta}{name}{MudAnsi.Reset}",
        4 => $"{MudAnsi.White}{name}{MudAnsi.Reset}",
        5 => $"{MudAnsi.BrightMagenta}{name}{MudAnsi.Reset}",
        6 => $"{MudAnsi.BrightMagenta}{name}{MudAnsi.Reset}",
        _ => $"{MudAnsi.Cyan}{name}{MudAnsi.Reset}",
    };
}
