namespace mmudreborn.Server;

public enum GameColorRole
{
    RoomName,
    Exits,
    RoomNotice,
    // The stock "default text" category (2): the base color the prompt establishes
    // and that uncolored player output inherits — White on palettes
    // 0/1, Cyan on palettes 2/3. Used for messages stock leaves in the default color (e.g. the
    // "You gain N experience." line).
    DefaultText,
    DimText,
    CombatHit,
    CombatMiss,
    CombatGood,
    CombatToggle,
    SpellHostile,
    SpellBeneficial,
    SpellFailure,
    // The telepath ("whisper") category (13): the color of the telepath MESSAGE
    // text, indexed by the RECIPIENT's palette — White on palettes 0/1/2, BrightCyan on palette 3.
    TelepathMessage,
    PartyNotice,
    AttentionNotice,
    PlayerName,
    PlayerMovementText,
    DragNotice,
    MonsterHostile,
    MonsterPeaceful,
    MonsterLawful,
    AlsoHerePlayer,
    AlsoHereLabel,
    AlsoHereSeparator,
    InventoryLabel,
    InventoryValue,
    InventoryNeutral,
    InventoryWarning,
    InventoryDanger,
    TravelTextCommand,
    TravelAction,
    TravelHidden,
    PromptBrackets,
    PromptValue,
    PromptDanger,
    // The HP-percentage bands: the prompt's HP number is
    // colored by how full the bar is — healthy >50%, warning 25-50%, critical <=25%. PromptHpHealthy
    // doubles as the mana/Kai value color (stock reuses the healthy table for the second resource).
    PromptHpHealthy,
    PromptHpWarning,
    PromptHpCritical,
    NpcDialogueNarrative,
    NpcDialogueClue,
}

public sealed class GameColorPalette
{
    private readonly Dictionary<GameColorRole, string> _roles;

    public GameColorPalette(IEnumerable<KeyValuePair<GameColorRole, string>> roles)
    {
        _roles = roles.ToDictionary(static entry => entry.Key, static entry => entry.Value);
    }

    public string Get(GameColorRole role)
        => _roles.TryGetValue(role, out string? value) ? value : string.Empty;

    public GameColorPalette With(params (GameColorRole Role, string Value)[] overrides)
    {
        var merged = new Dictionary<GameColorRole, string>(_roles);
        foreach (var (role, value) in overrides)
            merged[role] = value;

        return new GameColorPalette(merged);
    }
}

public static class GameColorPalettes
{
    private static readonly object PaletteLock = new();
    private static readonly Dictionary<int, GameColorPalette> Palettes = BuildDefaultPalettes();

    public static void Register(int paletteId, GameColorPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        lock (PaletteLock)
            Palettes[paletteId] = palette;
    }

    public static bool IsRegistered(int paletteId)
    {
        lock (PaletteLock)
            return Palettes.ContainsKey(paletteId);
    }

    public static IReadOnlyList<int> GetRegisteredIds()
    {
        lock (PaletteLock)
            return Palettes.Keys.OrderBy(static id => id).ToArray();
    }

    public static GameColorPalette Resolve(int paletteId)
    {
        lock (PaletteLock)
            return Palettes.TryGetValue(paletteId, out GameColorPalette? palette) ? palette : Palettes[0];
    }

    private static Dictionary<int, GameColorPalette> BuildDefaultPalettes()
    {
        var palette0 = new GameColorPalette(new Dictionary<GameColorRole, string>
        {
            [GameColorRole.RoomName] = MudAnsi.BrightCyan,
            // Stock palette 0 "Obvious exits:" — byte-verified from the stock colour table
            // as "\x1b[0;32m" (green).
            [GameColorRole.Exits] = MudAnsi.Green,
            [GameColorRole.RoomNotice] = MudAnsi.Cyan,
            [GameColorRole.DefaultText] = MudAnsi.White,
            [GameColorRole.DimText] = MudAnsi.WhiteOnBlack,
            [GameColorRole.CombatHit] = MudAnsi.BrightRed,
            [GameColorRole.CombatMiss] = MudAnsi.Cyan,
            [GameColorRole.CombatGood] = MudAnsi.BrightGreen,
            [GameColorRole.CombatToggle] = MudAnsi.Yellow,
            [GameColorRole.SpellHostile] = MudAnsi.BrightRed,
            [GameColorRole.SpellBeneficial] = MudAnsi.BrightBlue,
            [GameColorRole.SpellFailure] = MudAnsi.Cyan,
            [GameColorRole.TelepathMessage] = MudAnsi.White,   // cat 13: White on pal 0/1/2, BrightCyan on 3
            [GameColorRole.PartyNotice] = MudAnsi.BrightBlue,
            [GameColorRole.AttentionNotice] = MudAnsi.Red,
            [GameColorRole.PlayerName] = MudAnsi.BrightRed,
            [GameColorRole.PlayerMovementText] = MudAnsi.Green,
            [GameColorRole.DragNotice] = MudAnsi.BrightWhite,
            [GameColorRole.MonsterHostile] = MudAnsi.BrightMagenta,
            [GameColorRole.MonsterPeaceful] = MudAnsi.Cyan,
            [GameColorRole.MonsterLawful] = MudAnsi.White,
            [GameColorRole.AlsoHerePlayer] = MudAnsi.BrightMagenta,
            [GameColorRole.AlsoHereLabel] = MudAnsi.Magenta,
            [GameColorRole.AlsoHereSeparator] = MudAnsi.Magenta,
            [GameColorRole.InventoryLabel] = MudAnsi.Green,
            [GameColorRole.InventoryValue] = MudAnsi.Cyan,
            [GameColorRole.InventoryNeutral] = MudAnsi.White,
            [GameColorRole.InventoryWarning] = MudAnsi.Yellow,
            [GameColorRole.InventoryDanger] = MudAnsi.Red,
            // Byte-verified from the stock move switch:
            //   type 10 (Text command): self-message prefix "\x1b[1;33m"
            //     (BrightYellow). Used by e.g. "borrow skiff" -> "You climb into one of the
            //     skiffs, and row to Silvermere."
            //   type 5  (Action):       self-message prefix "\x1b[0;32m"
            //     (Green). Used by e.g. the Action exit at 1/2335 -> "You hop in one of the
            //     skiffs, and row to Newhaven."
            // Hidden exits are inferred to follow the Action path (no separate case in the move
            // switch we've inspected); set to the same green default. The user's earlier
            // intuition that "per-area / per-item color overrides" drive the distinction was
            // almost right but the actual driver is the EXIT TYPE in the DAT row, not the item —
            // same skiff item, two different RoomExits rows with different ExitType.
            [GameColorRole.TravelTextCommand] = MudAnsi.BrightYellow,
            [GameColorRole.TravelAction] = MudAnsi.Green,
            [GameColorRole.TravelHidden] = MudAnsi.Green,
            [GameColorRole.PromptBrackets] = MudAnsi.White,
            [GameColorRole.PromptValue] = MudAnsi.White,
            [GameColorRole.PromptDanger] = MudAnsi.Red,
            // HP bands — byte-verified from the stock tables. Healthy on palettes 0/1 is the
            // empty (inherit) entry, which resolves to the f0a0 default text (White) — set explicitly.
            [GameColorRole.PromptHpHealthy] = MudAnsi.White,      // f0b0 pal0 (inherit -> default White)
            [GameColorRole.PromptHpWarning] = MudAnsi.BrightWhite, // f0c0 pal0
            [GameColorRole.PromptHpCritical] = MudAnsi.BrightRed,  // f0d0 (all palettes)
            // NPC dialogue: stock palette 0 prose is green with bright-green clue-keyword
            // highlights — the pre-Phase-4 hard-coded values were correct. The Phase 4 routing
            // change (palette role instead of hard-coded MudAnsi.Green) is kept; only the default
            // values revert to match stock.
            [GameColorRole.NpcDialogueNarrative] = MudAnsi.Green,
            [GameColorRole.NpcDialogueClue] = MudAnsi.BrightGreen,
        });

        // Stock capture: palette 1 keeps hostile/lawful monster colors but shifts player-facing
        // room listings and exits away from palette 0. Dialogue + travel-action defaults match
        // palette 0 (Green narrative / BrightGreen clue / Green travel) so no explicit override.
        var palette1 = palette0.With(
            (GameColorRole.RoomName, MudAnsi.BrightCyan),
            (GameColorRole.Exits, MudAnsi.Yellow),
            // PlayerName is the MOVEMENT-broadcast name (GameAnsi.PlayerDeparture/Arrival), stock category 9
            // (f110) = Red on palette 1 — NOT the room-list name (that's AlsoHerePlayer/cat 8 below,
            // which the "green listings" theme correctly tints Green). The prior PlayerName=Green here
            // wrongly themed moving-player names; stock keeps them Red. (byte-verified 2026-06-12)
            (GameColorRole.PlayerName, MudAnsi.Red),
            (GameColorRole.AlsoHerePlayer, MudAnsi.Green),
            (GameColorRole.AlsoHereLabel, MudAnsi.Green),
            (GameColorRole.AlsoHereSeparator, MudAnsi.Green),
            (GameColorRole.PromptBrackets, MudAnsi.White),
            (GameColorRole.PromptValue, MudAnsi.White));

        // Ground-item "You notice ... here." is the fixed stock prefix ESC[0;36m
        // (Cyan) on EVERY palette — it is not palette-indexed (the room-items display prints
        // the prefix once at line start, no per-palette table lookup). So RoomNotice must stay Cyan
        // on palettes 2/3; the prior White override rendered ground items white in-game (the bug the
        // user reported).
        var palette2 = palette0.With(
            (GameColorRole.RoomName, MudAnsi.BrightCyan),
            (GameColorRole.Exits, MudAnsi.Green),
            // The beneficial-spell category: BrightBlue on palettes 0/1,
            // BrightCyan on palettes 2/3.
            (GameColorRole.SpellBeneficial, MudAnsi.BrightCyan),
            // f090 room-list label "Also here:": Magenta on palettes 0/3, Green on 1, BrightMagenta on 2
            // (cat 1). The override block previously omitted it, so palette 2 inherited palette0's Magenta.
            (GameColorRole.AlsoHereLabel, MudAnsi.BrightMagenta),
            // Movement broadcast on palette 2 is the whole line BLACK on a WHITE background: the name is
            // cat 9 (f110) = ESC[0;30m + ESC[47m, and the verb is cat 11 (f130) = empty (inherits the
            // name's black-on-white). Stock clears it with a trailing ESC[40m (cat 10); our movement
            // wrappers already end with a full Reset, which clears the background just the same.
            (GameColorRole.PlayerName, MudAnsi.Black + MudAnsi.BgWhite),
            (GameColorRole.PlayerMovementText, MudAnsi.Black + MudAnsi.BgWhite),
            // f0a0 default-text category: palettes 2/3 tint general output Cyan (vs White on 0/1).
            (GameColorRole.DefaultText, MudAnsi.Cyan),
            // NOTE: InventoryLabel/InventoryValue are NOT palette-tinted — stock keeps the inventory
            // wealth/encumbrance lines green "Wealth:"/"Encumbrance:" + cyan value on EVERY palette
            // (verified vs live Adept on palette 3). They inherit palette0's green/cyan; the prior
            // Cyan/White override here was wrong.
            (GameColorRole.PromptBrackets, MudAnsi.Cyan),
            (GameColorRole.PromptValue, MudAnsi.BrightCyan),
            // f0b0/f0c0 on palette 2: healthy HP & mana = BrightCyan, warning = White (critical stays red).
            (GameColorRole.PromptHpHealthy, MudAnsi.BrightCyan),
            (GameColorRole.PromptHpWarning, MudAnsi.White));
        // Travel self-messages are fixed stock prefixes (text-command BrightYellow, action/
        // hidden Green) — NOT palette-indexed — so Travel* inherits palette0 on every
        // palette. The prior palette-2/3 Cyan overrides were non-faithful; removed 2026-06-12.

        // RoomNotice (ground-item line) is the fixed Cyan prefix on every palette — see palette2.
        var palette3 = palette0.With(
            (GameColorRole.RoomName, MudAnsi.BrightCyan),
            (GameColorRole.Exits, MudAnsi.Green),
            // f0e0 beneficial-spell category: BrightCyan on palettes 2/3 (see palette2).
            (GameColorRole.SpellBeneficial, MudAnsi.BrightCyan),
            // f150 telepath category: White on palettes 0/1/2, BrightCyan on palette 3 (cat 13).
            (GameColorRole.TelepathMessage, MudAnsi.BrightCyan),
            (GameColorRole.DefaultText, MudAnsi.Cyan),
            // InventoryLabel/InventoryValue inherit palette0's green/cyan — stock keeps the inventory
            // wealth/encumbrance colors fixed across all palettes (see palette2 note).
            (GameColorRole.PromptBrackets, MudAnsi.White),
            (GameColorRole.PromptValue, MudAnsi.BrightMagenta),
            // f0b0/f0c0 on palette 3 match palette 2: healthy/mana BrightCyan, warning White.
            (GameColorRole.PromptHpHealthy, MudAnsi.BrightCyan),
            (GameColorRole.PromptHpWarning, MudAnsi.White));
        // Travel* left at palette0 (fixed stock prefixes) — see palette2.

        return new Dictionary<int, GameColorPalette>
        {
            [0] = palette0,
            [1] = palette1,
            [2] = palette2,
            [3] = palette3,
        };
    }
}