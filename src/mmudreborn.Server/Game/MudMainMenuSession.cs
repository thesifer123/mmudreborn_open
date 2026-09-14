using System.Globalization;
using CWGaming.Shared;
using mmudreborn.Data;

namespace mmudreborn.Server;

public enum MudMainMenuResult
{
    EnterRealm,
    ReturnToBbs,
    Disconnect,
}

/// <summary>
/// MMUDREBORN-owned pre-realm main menu reached after the BBS launches the game.
/// </summary>
public sealed class MudMainMenuSession
{
    /// <summary>The single source of truth for the MMUDREBORN release version shown on the MUD main
    /// menu banner ("MMUDREBORN v0.1.12 (timestamp)"). Door-specific, so it lives here in mmudreborn —
    /// not in the shared BBS config. Bump this ONE constant to change the version everywhere (the banner
    /// and the integration tests both read it; playtest snapshots normalize the version away).</summary>
    public const string MudMenuVersion = "v0.1.12";
    private static readonly TimeSpan MudMenuAutoEnterDelay = TimeSpan.FromSeconds(5);

    private readonly IGameClient _client;
    private readonly GameWorld _world;

    public MudMainMenuSession(IGameClient client, GameWorld world)
    {
        _client = client;
        _world = world;
    }

    public async Task<MudMainMenuResult> RunAsync(bool autoEnterRealm, string? viewerBbsUserName, CancellationToken ct)
    {
        bool renderFullMenu = true;
        while (!ct.IsCancellationRequested && _client.Connected)
        {
            _client.CurrentHostedAppId = HostedAppIds.Mmudreborn;
            _client.CurrentHostedWorldId = HostedAppIds.DefaultWorldId;
            _client.CurrentHostedLocation = "Main Menu";

            // Stock parity: the full menu (banner + option list) is drawn on entry and again only when the
            // user asks for it (a bare Enter). After an info command we just re-show the prompt with the
            // command's output still on screen — NOT a screen-clearing redraw that would wipe it (which is
            // why the old "Press [ENTER]" pause was needed). The original drops straight back to the prompt.
            if (renderFullMenu)
                await RenderMudMainMenuAsync();
            await _client.SendAsync(MudAnsi.MainMenuPrompt());
            renderFullMenu = false;

            var choice = await ReadMenuChoiceAsync(
                autoEnterRealm ? MudMenuAutoEnterDelay : null,
                autoEnterRealm ? "E" : null,
                viewerBbsUserName,
                ct);
            if (choice == null)
                return MudMainMenuResult.Disconnect;

            if (choice == GameTransportConstants.ReadCommandHandledSentinel)
                continue;

            if (choice == GameTransportConstants.ReadTimeoutSentinel)
            {
                choice = "E";
                await _client.SendLineAsync(choice);
            }

            switch (NormalizeMenuChoice(choice))
            {
                case "":
                    renderFullMenu = true; // a bare Enter refreshes the menu listing
                    break;

                case "E":
                case "ENTER":
                    return MudMainMenuResult.EnterRealm;

                case "H":
                case "HELP":
                case "?":
                    await ShowMudMenuHelpAsync();
                    break;

                case "C":
                case "COUNTS":
                    await ShowGameCountsAsync();
                    break;

                case "T":
                case "TOPTEN":
                    await _client.SendLineAsync();
                    await CommandParser.RenderTopAdventurersAsync(_client, _world, requestedCount: 10, classId: 0);
                    break;

                case "G":
                case "GANGS":
                    await _client.SendLineAsync();
                    await CommandParser.RenderTopGangsAsync(_client, _world, requestedCount: 10);
                    break;

                case "W":
                case "WHO":
                    await _client.SendLineAsync();
                    // No logged-in player at the menu: viewer is treated as a non-sysop (sys-invisible
                    // players stay hidden) and the standard fantasy WHO layout is used.
                    await CommandParser.RenderWhoAsync(_client, _world, viewerIsSysop: IsViewerSysop(viewerBbsUserName), technicalStyle: false);
                    break;

                case "X":
                case "EXIT":
                    return MudMainMenuResult.ReturnToBbs;

                default:
                    await _client.SendLineAsync();
                    await _client.SendLineAsync(MudAnsi.Error("Please choose one of the listed options."));
                    break;
            }

            autoEnterRealm = false;
        }

        return MudMainMenuResult.Disconnect;
    }

    private async Task RenderMudMainMenuAsync()
    {
        await _client.SendLineAsync(MudAnsi.ClearScreen);
        await _client.SendLineAsync($"{MudAnsi.BrightWhite}MMUDREBORN{MudAnsi.Reset} {MudAnsi.BrightYellow}{MudMenuVersion} ({FormatMudMenuTimestamp(DateTime.Now)}){MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.White}{{ A Legend Reborn }}{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.BrightMagenta}*ANSI RECOMMENDED*{MudAnsi.Reset}");
        await _client.SendLineAsync();
        await _client.SendLineAsync(MudMenuOption("E", "Enter the Realm"));
        await _client.SendLineAsync(MudMenuOption("H", "Help"));
        await _client.SendLineAsync(MudMenuOption("C", "Game Counts"));
        await _client.SendLineAsync(MudMenuOption("T", "Topten Adventurers"));
        await _client.SendLineAsync(MudMenuOption("G", "Topten Gangs"));
        await _client.SendLineAsync(MudMenuOption("W", "Who's in the Realm"));
        await _client.SendLineAsync(MudMenuOption("X", "Exit Game"));
        await _client.SendLineAsync();
        // The prompt itself is emitted by the run loop (so it can re-prompt without redrawing the menu).
    }

    private static string MudMenuOption(string hotkey, string label, bool disabled = false)
    {
        string disabledLabel = disabled ? $" {MudAnsi.BrightRed}[Disabled]{MudAnsi.Reset}" : string.Empty;
        return $"{MudAnsi.White}[{MudAnsi.Reset}{MudAnsi.BrightWhite}{hotkey}{MudAnsi.Reset}{MudAnsi.White}] . {label}{MudAnsi.Reset}{disabledLabel}";
    }

    private async Task ShowMudMenuHelpAsync()
    {
        await _client.SendLineAsync();
        await _client.SendLineAsync($"{MudAnsi.BrightYellow}Choose E to enter the realm, C for counts, or X to return to the BBS menu.{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.BrightYellow}Full HELP topics are available after entering the realm.{MudAnsi.Reset}");
        await _client.SendLineAsync();
    }

    // Pre-realm "Game Counts" screen, in the spirit of the classic stats page:
    // a centered banner and the realm's content tallies. We keep the nostalgic
    // "over N" phrasing but compute it from the live database, and the value column is aligned (the
    // original's Classes/Races rows weren't). Counts are rounded DOWN so "over N" is always truthful.
    private async Task ShowGameCountsAsync()
    {
        await _client.SendLineAsync(MudAnsi.ClearScreen);
        await _client.SendLineAsync();
        await _client.SendLineAsync($"{MudAnsi.BrightBlue}{CenterMenuLine(SpaceLetters("MMUDREBORN"))}{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.White}{CenterMenuLine("{ A Legend Reborn }")}{MudAnsi.Reset}");
        await _client.SendLineAsync();
        await _client.SendLineAsync($"{MudAnsi.White}  Here are some statistical pieces of information regarding the{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.White}  current MMUDREBORN realm:{MudAnsi.Reset}");
        await _client.SendLineAsync();
        await SendCountLineAsync("Rooms", _world.Database.Rooms.Count);
        await SendCountLineAsync("Monsters", _world.Database.Monsters.Count);
        await SendCountLineAsync("Items", _world.Database.Items.Count);
        await SendCountLineAsync("Spells", _world.Database.Spells.Count);
        await SendCountLineAsync("Shops", _world.Database.Shops.Count);
        await SendCountLineAsync("Classes", _world.Database.Classes.Count);
        await SendCountLineAsync("Races", _world.Database.Races.Count);
        await _client.SendLineAsync();
    }

    private Task SendCountLineAsync(string label, int count)
        => _client.SendLineAsync($"    {MudAnsi.White}{(label + ":").PadRight(11)}{MudAnsi.BrightWhite}{FormatRealmCount(count)}{MudAnsi.Reset}");

    // Nostalgic "over N" phrasing rounded DOWN from the live count (so the realm always has MORE than
    // stated). Small fixed sets (Classes/Races) show the exact number, matching the original screen.
    private static string FormatRealmCount(int count)
    {
        int step = count >= 10000 ? 1000 : count >= 1000 ? 100 : count >= 100 ? 50 : 0;
        if (step == 0)
            return count.ToString("N0", CultureInfo.InvariantCulture);

        int floored = count / step * step;
        if (floored == count)
            floored -= step; // keep "over N" strictly true when the count lands on a round number

        return $"over {floored.ToString("N0", CultureInfo.InvariantCulture)}";
    }

    private static string SpaceLetters(string text) => string.Join(" ", text.ToCharArray());

    private static string CenterMenuLine(string text, int width = 79)
    {
        if (text.Length >= width)
            return text;

        return new string(' ', (width - text.Length) / 2) + text;
    }

    private async Task<string?> ReadMenuChoiceAsync(TimeSpan? timeout, string? autoChoice, string? viewerBbsUserName, CancellationToken ct)
    {
        if (timeout.HasValue && autoChoice != null && BbsMenuSettings.GetEffectiveAutoAdvanceEnabled(_world.BbsUserRepo))
        {
            var choice = await ReadBbsCommandAwareLineAsync((int)timeout.Value.TotalMilliseconds, echo: true, viewerBbsUserName, ct);
            if (choice == null)
                return null;

            return choice == GameTransportConstants.ReadTimeoutSentinel
                ? GameTransportConstants.ReadTimeoutSentinel
                : choice;
        }

        return await ReadBbsCommandAwareLineAsync(echo: true, viewerBbsUserName, ct);
    }

    private async Task<string?> ReadBbsCommandAwareLineAsync(bool echo, string? viewerBbsUserName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var input = await _client.ReadLineEchoAsync(echo, ct);
            if (input == null)
                return null;

            if (await _world.BbsCommandDispatcher.TryDispatchAsync(input.Trim(), _client, _world, viewerBbsUserName))
            {
                if (_client.Connected)
                    await _client.SendLineAsync();
                return GameTransportConstants.ReadCommandHandledSentinel;
            }

            return input;
        }

        return null;
    }

    private async Task<string?> ReadBbsCommandAwareLineAsync(int timeoutMs, bool echo, string? viewerBbsUserName, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var input = await _client.ReadLineEchoAsync(timeoutMs, echo, ct);
            if (input == null || input.Equals(GameTransportConstants.ReadTimeoutSentinel, StringComparison.Ordinal))
                return input;

            if (await _world.BbsCommandDispatcher.TryDispatchAsync(input.Trim(), _client, _world, viewerBbsUserName))
            {
                if (_client.Connected)
                    await _client.SendLineAsync();
                return GameTransportConstants.ReadCommandHandledSentinel;
            }

            return input;
        }

        return null;
    }

    // Resolve sysop status from the BBS account so the menu WHO matches the in-game WHO for a sysop
    // (sys-invisible players are only listed to sysops). No account name → treated as a normal user.
    private bool IsViewerSysop(string? viewerBbsUserName)
    {
        if (string.IsNullOrWhiteSpace(viewerBbsUserName) || _world.BbsUserRepo == null)
            return false;

        return _world.BbsUserRepo.LoadUser(viewerBbsUserName.Trim())?.IsSysop == true;
    }

    private static string NormalizeMenuChoice(string? choice)
    {
        return (choice ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string FormatMudMenuTimestamp(DateTime timestamp)
    {
        return timestamp.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture).ToUpperInvariant();
    }
}