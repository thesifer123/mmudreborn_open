using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class AnsiColorTests
{
    [Fact]
    public void Travel_palette_distinguishes_text_and_action_skiff_routes()
    {
        // Stock palette 0: text-command traversal ("You climb into one of the skiffs..." from
        // `borrow skiff`) is bright yellow; action / hidden traversal ("You hop in one of the
        // skiffs...") is green. The Phase 4 sub-agent briefly flipped action+hidden to cyan; the
        // user confirmed in-game that those are also green. The original's real model is per-area
        // / per-item override (NOT one-size-fits-all across every exit type); modelling that is
        // a deferred follow-up — for now these three buckets are the closest faithful split.
        Assert.Equal(Ansi.BrightYellow, GameAnsi.TravelSelfMessageColor(TravelMessageType.TextCommandTraversal));
        Assert.Equal(Ansi.Green, GameAnsi.TravelSelfMessageColor(TravelMessageType.ActionTraversal));
        Assert.Equal(Ansi.Green, GameAnsi.TravelSelfMessageColor(TravelMessageType.HiddenTraversal));
    }

    /// <summary>
    /// Bright colors use combined ESC[1;3Xm format matching stock strings and real BBS captures.
    /// </summary>
    [Theory]
    [InlineData(nameof(Ansi.BrightRed), "\x1b[1;31m")]
    [InlineData(nameof(Ansi.BrightGreen), "\x1b[1;32m")]
    [InlineData(nameof(Ansi.BrightYellow), "\x1b[1;33m")]
    [InlineData(nameof(Ansi.BrightBlue), "\x1b[1;34m")]
    [InlineData(nameof(Ansi.BrightMagenta), "\x1b[1;35m")]
    [InlineData(nameof(Ansi.BrightCyan), "\x1b[1;36m")]
    [InlineData(nameof(Ansi.BrightWhite), "\x1b[1;37m")]
    [InlineData(nameof(Ansi.DarkGray), "\x1b[1;30m")]
    public void Bright_colors_use_combined_bold_foreground_format(string fieldName, string expected)
    {
        var value = (string)typeof(Ansi).GetField(fieldName)!.GetValue(null)!;
        Assert.Equal(expected, value);
    }

    /// <summary>
    /// Standard colors use ESC[0;3Xm format with reset prefix, matching real BBS captures.
    /// </summary>
    [Theory]
    [InlineData(nameof(Ansi.Red), "\x1b[0;31m")]
    [InlineData(nameof(Ansi.Green), "\x1b[0;32m")]
    [InlineData(nameof(Ansi.Yellow), "\x1b[0;33m")]
    [InlineData(nameof(Ansi.Magenta), "\x1b[0;35m")]
    [InlineData(nameof(Ansi.Cyan), "\x1b[0;36m")]
    [InlineData(nameof(Ansi.White), "\x1b[0;37m")]
    public void Standard_colors_use_reset_prefix_format(string fieldName, string expected)
    {
        var value = (string)typeof(Ansi).GetField(fieldName)!.GetValue(null)!;
        Assert.Equal(expected, value);
    }

    [Fact]
    public void LinePreamble_contains_cursor_back_79_and_erase_line()
    {
        Assert.Equal("\x1b[79D\x1b[K", Ansi.LinePreamble);
    }

    [Fact]
    public void RoomName_includes_LinePreamble_and_BrightCyan()
    {
        var result = GameAnsi.RoomName("Test Room");
        Assert.StartsWith(Ansi.LinePreamble, result);
        Assert.Contains(Ansi.BrightCyan, result);
        Assert.Contains("Test Room", result);
    }

    [Theory]
    [InlineData(0, nameof(Ansi.White))]
    [InlineData(1, nameof(Ansi.White))]
    [InlineData(2, nameof(Ansi.Cyan))]
    [InlineData(3, nameof(Ansi.Cyan))]
    public void DefaultText_matches_the_stock_default_text_category(int paletteId, string expectedField)
    {
        // The stock "default text" category: White on palettes 0/1, Cyan on 2/3. This is the
        // color the prompt establishes and uncolored player output inherits (e.g. the experience line).
        var expected = (string)typeof(Ansi).GetField(expectedField)!.GetValue(null)!;
        Assert.Equal(expected, GameColorPalettes.Resolve(paletteId).Get(GameColorRole.DefaultText));
    }

    [Theory]
    [InlineData(0, nameof(Ansi.White))]
    [InlineData(2, nameof(Ansi.Cyan))]
    public void Experience_line_uses_palette_default_text_color(int paletteId, string expectedField)
    {
        // "You gain N experience." renders in the f0a0 default-text color (White pal0/1, Cyan pal2/3),
        // NOT the always-White InventoryNeutral it used to hard-code.
        var expected = (string)typeof(Ansi).GetField(expectedField)!.GetValue(null)!;
        Assert.Contains(expected, GameAnsi.Experience("You gain 9 experience.", paletteId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Travel_self_message_colors_are_palette_independent(int paletteId)
    {
        // Travel self-messages use FIXED stock prefixes (text-command BrightYellow,
        // action/hidden Green), not the palette tables — identical on every palette.
        // The palette-2/3 Cyan overrides were non-faithful and were removed.
        Assert.Equal(Ansi.BrightYellow, GameAnsi.TravelSelfMessageColor(TravelMessageType.TextCommandTraversal, paletteId));
        Assert.Equal(Ansi.Green, GameAnsi.TravelSelfMessageColor(TravelMessageType.ActionTraversal, paletteId));
        Assert.Equal(Ansi.Green, GameAnsi.TravelSelfMessageColor(TravelMessageType.HiddenTraversal, paletteId));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Inventory_wealth_and_encumbrance_colors_are_palette_independent(int paletteId)
    {
        // Stock keeps the inventory wealth/encumbrance lines (and the standalone WEALTH command) a green
        // "Wealth:"/"Encumbrance:" label + a cyan value on EVERY palette — verified vs live Adept on
        // palette 3. They are NOT palette-tinted; the prior palette-2/3 Cyan/White override was wrong.
        var palette = GameColorPalettes.Resolve(paletteId);
        Assert.Equal(Ansi.Green, palette.Get(GameColorRole.InventoryLabel));
        Assert.Equal(Ansi.Cyan, palette.Get(GameColorRole.InventoryValue));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Ground_item_notice_is_cyan_on_every_palette(int paletteId)
    {
        // The room-items display prints the "You notice ... here." line with the FIXED
        // prefix ESC[0;36m (Cyan) before "You notice" — it is not palette-indexed, so
        // ground items are Cyan on every palette. Regression guard for the palette-2 White bug.
        Assert.Equal(Ansi.Cyan, GameColorPalettes.Resolve(paletteId).Get(GameColorRole.RoomNotice));
    }

    [Fact]
    public void Built_in_palette_one_changes_exit_color_while_palette_zero_stays_stock()
    {
        // Palette 0 renders "Obvious exits:" in green (verified in-game). Palette 1 overrides to
        // yellow. The Phase 4 cyan inference was reverted after the user spotted it.
        Assert.Equal($"{Ansi.Green}Obvious exits: north", GameAnsi.Exits("Obvious exits: north"));
        Assert.Equal($"{Ansi.Yellow}Obvious exits: north", GameAnsi.Exits("Obvious exits: north", 1));
    }

    [Fact]
    public void Built_in_palette_one_changes_player_listing_to_green_without_changing_monster_colors()
    {
        var palette = GameColorPalettes.Resolve(1);

        // The room-LISTING roles go green on palette 1 (the theme). PlayerName is NOT one of them — it is
        // the movement-broadcast name (stock category 9), which stock keeps Red on palette 1.
        Assert.Equal(Ansi.Red, palette.Get(GameColorRole.PlayerName));
        Assert.Equal(Ansi.Green, palette.Get(GameColorRole.AlsoHerePlayer));
        Assert.Equal(Ansi.Green, palette.Get(GameColorRole.AlsoHereLabel));
        Assert.Equal(Ansi.Green, palette.Get(GameColorRole.AlsoHereSeparator));
        Assert.Equal(Ansi.BrightMagenta, palette.Get(GameColorRole.MonsterHostile));
        Assert.Equal(Ansi.White, palette.Get(GameColorRole.MonsterLawful));
    }

    // Stock category 13, telepath message: White on palettes 0/1/2, BrightCyan on palette 3 (indexed by
    // the recipient's palette). WHISPER IS telepath; there is no separate same-room whisper in stock.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Telepath_message_is_white_on_palettes_zero_one_two(int paletteId)
    {
        Assert.Equal(Ansi.White, GameColorPalettes.Resolve(paletteId).Get(GameColorRole.TelepathMessage));
    }

    [Fact]
    public void Telepath_message_is_bright_cyan_on_palette_three()
    {
        Assert.Equal(Ansi.BrightCyan, GameColorPalettes.Resolve(3).Get(GameColorRole.TelepathMessage));
    }

    // Stock category 9, movement player NAME: BrightRed on 0/3, Red on 1, Black-on-BgWhite on 2.
    [Fact]
    public void Movement_player_name_follows_stock_per_palette()
    {
        Assert.Equal(Ansi.BrightRed, GameColorPalettes.Resolve(0).Get(GameColorRole.PlayerName));
        Assert.Equal(Ansi.Red, GameColorPalettes.Resolve(1).Get(GameColorRole.PlayerName));
        Assert.Equal(Ansi.Black + Ansi.BgWhite, GameColorPalettes.Resolve(2).Get(GameColorRole.PlayerName));
        Assert.Equal(Ansi.BrightRed, GameColorPalettes.Resolve(3).Get(GameColorRole.PlayerName));
    }

    // Stock category 11, movement VERB: Green on 0/1/3, inherits the name's black-on-white on palette 2.
    [Fact]
    public void Movement_verb_follows_stock_per_palette()
    {
        Assert.Equal(Ansi.Green, GameColorPalettes.Resolve(0).Get(GameColorRole.PlayerMovementText));
        Assert.Equal(Ansi.Green, GameColorPalettes.Resolve(1).Get(GameColorRole.PlayerMovementText));
        Assert.Equal(Ansi.Black + Ansi.BgWhite, GameColorPalettes.Resolve(2).Get(GameColorRole.PlayerMovementText));
        Assert.Equal(Ansi.Green, GameColorPalettes.Resolve(3).Get(GameColorRole.PlayerMovementText));
    }

    // Stock category 1, the "Also here:" label: Magenta on palettes 0/3, Green on 1, BrightMagenta on 2.
    [Fact]
    public void Also_here_label_is_bright_magenta_on_palette_two()
    {
        Assert.Equal(Ansi.Magenta, GameColorPalettes.Resolve(0).Get(GameColorRole.AlsoHereLabel));
        Assert.Equal(Ansi.Green, GameColorPalettes.Resolve(1).Get(GameColorRole.AlsoHereLabel));
        Assert.Equal(Ansi.BrightMagenta, GameColorPalettes.Resolve(2).Get(GameColorRole.AlsoHereLabel));
        Assert.Equal(Ansi.Magenta, GameColorPalettes.Resolve(3).Get(GameColorRole.AlsoHereLabel));
    }

    [Fact]
    public void Custom_palette_registration_supports_future_palette_ids()
    {
        GameColorPalettes.Register(
            99,
            GameColorPalettes.Resolve(0).With(
                (GameColorRole.Exits, Ansi.BrightBlue),
                (GameColorRole.PromptValue, Ansi.BrightGreen)));

        Assert.Contains(99, GameColorPalettes.GetRegisteredIds());
        Assert.Equal(Ansi.BrightBlue, GameColorPalettes.Resolve(99).Get(GameColorRole.Exits));
        Assert.Equal($"{Ansi.BrightBlue}Obvious exits: east", GameAnsi.Exits("Obvious exits: east", 99));
    }

    [Fact]
    public void Spell_message_wrappers_use_requested_cast_colors()
    {
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.BrightRed}harm{Ansi.Reset}", GameAnsi.SpellHostile("harm"));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.BrightBlue}heal{Ansi.Reset}", GameAnsi.SpellBeneficial("heal"));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.Cyan}fail{Ansi.Reset}", GameAnsi.SpellFailure("fail"));
    }

    // The stock beneficial-spell category: BrightBlue on palettes 0/1,
    // BrightCyan on palettes 2/3.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Beneficial_spell_is_bright_blue_on_palettes_0_and_1(int paletteId)
    {
        Assert.Equal(Ansi.BrightBlue, GameColorPalettes.Resolve(paletteId).Get(GameColorRole.SpellBeneficial));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.BrightBlue}heal{Ansi.Reset}", GameAnsi.SpellBeneficial("heal", paletteId));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Beneficial_spell_is_bright_cyan_on_palettes_2_and_3(int paletteId)
    {
        Assert.Equal(Ansi.BrightCyan, GameColorPalettes.Resolve(paletteId).Get(GameColorRole.SpellBeneficial));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.BrightCyan}heal{Ansi.Reset}", GameAnsi.SpellBeneficial("heal", paletteId));
    }

    [Fact]
    public void Attention_notice_and_combat_glance_share_standard_red_by_default()
    {
        Assert.Equal(Ansi.Red, GameColorPalettes.Resolve(0).Get(GameColorRole.AttentionNotice));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.Red}removed{Ansi.Reset}", GameAnsi.AttentionNotice("removed"));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.Red}glance{Ansi.Reset}", GameAnsi.CombatGlance("glance"));
    }

    [Fact]
    public void Custom_palette_registration_can_override_attention_notice_for_both_wrappers()
    {
        GameColorPalettes.Register(
            98,
            GameColorPalettes.Resolve(0).With(
                (GameColorRole.AttentionNotice, Ansi.BrightBlue)));

        Assert.Equal($"{Ansi.LinePreamble}{Ansi.BrightBlue}removed{Ansi.Reset}", GameAnsi.AttentionNotice("removed", 98));
        Assert.Equal($"{Ansi.LinePreamble}{Ansi.BrightBlue}glance{Ansi.Reset}", GameAnsi.CombatGlance("glance", 98));
    }

    [Fact]
    public void Mud_prompt_includes_LinePreamble_and_White_color()
    {
        var result = MudAnsi.Prompt(22, 22, 0, 0);
        Assert.StartsWith(MudAnsi.LinePreamble, result);
        Assert.Contains("[HP=", result);
        Assert.Contains("22", result);
        Assert.Contains(MudAnsi.White, result);
        Assert.DoesNotContain("/MA=", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Mud_prompt_with_mana_includes_both_hp_and_ma()
    {
        var result = MudAnsi.Prompt(8, 22, 36, 36);
        Assert.StartsWith(MudAnsi.LinePreamble, result);
        Assert.Contains("[HP=", result);
        Assert.Contains("/MA=", result);
        Assert.Contains("8", result);
        Assert.Contains("36", result);
    }

    [Fact]
    public void Mud_prompt_with_zero_current_mana_and_nonzero_max_still_shows_mana_pool()
    {
        var result = MudAnsi.Prompt(49, 49, 0, 22);

        Assert.StartsWith(MudAnsi.LinePreamble, result);
        Assert.Contains("[HP=", result);
        Assert.Contains("/MA=", result);
        Assert.Contains("0", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Mud_prompt_places_resting_tag_inside_brackets_when_no_resource()
    {
        // The stock HP-only format "[HP=%s%d%s%s]:" — the tag sits INSIDE the brackets, before "]:".
        var result = MudAnsi.Prompt(22, 22, 0, 0, true);

        Assert.Contains("(Resting) ]:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Mud_prompt_places_resting_tag_after_brackets_when_mana_shown()
    {
        // The stock HP+MA format "[HP=...]:%s" — the tag comes AFTER "]:".
        var result = MudAnsi.Prompt(22, 22, 10, 10, true);

        Assert.EndsWith("]: (Resting) ", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Mud_prompt_appends_meditating_tag_when_requested()
    {
        var result = MudAnsi.Prompt(22, 22, 0, 0, isResting: false, isMeditating: true);

        Assert.Contains("(Meditating) ]:", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Mud_prompt_resting_takes_precedence_over_meditating()
    {
        // Resting and meditating are mutually exclusive; if both were ever set, show only one tag.
        var result = MudAnsi.Prompt(22, 22, 0, 0, isResting: true, isMeditating: true);

        Assert.Contains("(Resting)", result, StringComparison.Ordinal);
        Assert.DoesNotContain("(Meditating)", result, StringComparison.Ordinal);
    }

    [Theory]
    // HP-colour bands (current vs max): >50% healthy, >25% warning, else critical.
    [InlineData(100, 100, 2, nameof(Ansi.BrightCyan))]  // palette 2 healthy -> BrightCyan
    [InlineData(40, 100, 2, nameof(Ansi.White))]        // palette 2 warning  -> White
    [InlineData(10, 100, 2, nameof(Ansi.BrightRed))]    // palette 2 critical -> BrightRed
    [InlineData(40, 100, 0, nameof(Ansi.BrightWhite))]  // palette 0 warning  -> BrightWhite
    [InlineData(10, 100, 0, nameof(Ansi.BrightRed))]    // palette 0 critical -> BrightRed
    public void Hp_color_follows_the_stock_hp_colour_bands(int hp, int maxHp, int paletteId, string expectedField)
    {
        var expected = (string)typeof(Ansi).GetField(expectedField)!.GetValue(null)!;
        Assert.Equal(expected, GameAnsi.HpColor(hp, maxHp, paletteId));
    }

    [Fact]
    public void Prompt_colors_low_hp_red_inside_palette2_cyan_brackets()
    {
        // The user's report: palette 2, low HP, resting -> "{Cyan}[HP={BrightRed}3{Cyan} (Resting) ]:".
        var result = GameAnsi.Prompt(3, 30, 0, 0, isResting: true, paletteId: 2);

        Assert.Contains($"{Ansi.Cyan}[HP={Ansi.BrightRed}3{Ansi.Cyan} (Resting) ]:", result, StringComparison.Ordinal);
    }

    // The combat-toggle markers must match stock byte-for-byte so Megamud's regex parser
    // recognises them and resets its sneak/attack state. Ground truth captured live from
    // a stock server:
    //   1B 5B 37 39 44  1B 5B 4B  1B 5B 30 3B 33 33 6D  *Combat Off*       0D 0A   ← stock
    // Notably there is NO trailing 1B 5B 30 6D (ESC[0m) reset — the next prompt repaints color
    // via its own LinePreamble. Adding a reset here breaks Megamud's pixel-perfect line parser.
    [Fact]
    public void CombatOff_byte_sequence_matches_stock_exactly_with_no_trailing_reset()
    {
        string emitted = GameAnsi.CombatOff("*Combat Off*");
        const string expected = "\x1b[79D\x1b[K\x1b[0;33m*Combat Off*";
        Assert.Equal(expected, emitted);
        Assert.DoesNotContain("\x1b[0m", emitted);
    }

    [Fact]
    public void CombatEngaged_byte_sequence_matches_stock_exactly_with_no_trailing_reset()
    {
        string emitted = GameAnsi.CombatEngaged("*Combat Engaged*");
        const string expected = "\x1b[79D\x1b[K\x1b[0;33m*Combat Engaged*";
        Assert.Equal(expected, emitted);
        Assert.DoesNotContain("\x1b[0m", emitted);
    }
}
