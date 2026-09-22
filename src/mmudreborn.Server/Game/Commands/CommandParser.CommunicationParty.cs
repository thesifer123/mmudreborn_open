using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace mmudreborn.Server;

public partial class CommandParser
{
    private async Task HandleActionCommand(string args)
    {
        var actionParts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var subcommand = actionParts.Length > 0 ? actionParts[0].ToLowerInvariant() : "";

        if (string.IsNullOrEmpty(subcommand))
        {
            await _client.SendLineAsync($"Actions are currently {(_player.ActionsEnabled ? "ON" : "OFF")}.");
            await _client.SendLineAsync("Syntax: ACTION LIST");
            await _client.SendLineAsync("Syntax: ACTION ON");
            await _client.SendLineAsync("Syntax: ACTION OFF");
            return;
        }

        if (subcommand.Length >= 2 && "list".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            await HandleActionList();
            return;
        }

        if (subcommand == "on")
        {
            _player.ActionsEnabled = true;
            await _client.SendLineAsync($"{MudAnsi.White}You may now use action commands.{MudAnsi.Reset}");
            return;
        }

        if (subcommand.Length >= 2 && "off".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            _player.ActionsEnabled = false;
            await _client.SendLineAsync($"{MudAnsi.White}You may no longer use action commands.{MudAnsi.Reset}");
            return;
        }

        await _client.SendLineAsync("Syntax: ACTION LIST");
        await _client.SendLineAsync("Syntax: ACTION ON");
        await _client.SendLineAsync("Syntax: ACTION OFF");
    }

    private async Task HandleActionList()
    {
        var actionNames = GetDisplayedActionNames();
        if (actionNames.Count == 0)
        {
            await _client.SendLineAsync("There are no actions available.");
            return;
        }

        await SendWrappedEntryListAsync(
            string.Empty,
            actionNames,
            " ");
    }

    private List<string> GetDisplayedActionNames()
    {
        var orderedNames = _world.Database.OrderedActions
            .Where(static action => action.DisplayOrder.HasValue && !string.IsNullOrWhiteSpace(action.Name))
            .Select(static action => action.Name)
            .ToList();

        if (orderedNames.Count > 0)
            return orderedNames;

        return _world.Database.OrderedActions
            .Select(static action => action.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();
    }

    private string AppendActionHelpList(string body)
    {
        var actionNames = GetDisplayedActionNames();
        if (actionNames.Count == 0)
            return body;

        var wrappedNames = RoomOutputFormatter.WrapEntryList(string.Empty, actionNames, " ");
        return $"{body}\n\n{string.Join('\n', wrappedNames)}";
    }

    private async Task HandleHelpTopicsAsync()
    {
        var topics = _world.Database.HelpTopics.Keys
            .Where(static key => !string.IsNullOrWhiteSpace(key) && key != "?")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!topics.Contains("class", StringComparer.OrdinalIgnoreCase))
            topics.Add("class");

        if (!topics.Contains("race", StringComparer.OrdinalIgnoreCase))
            topics.Add("race");

        if (!topics.Contains("topics", StringComparer.OrdinalIgnoreCase))
            topics.Add("topics");

        topics.Sort(StringComparer.OrdinalIgnoreCase);

        await _client.SendLineAsync();
        await _client.SendLineAsync("The following help topics are currently available:");
        await SendWrappedEntryListAsync(string.Empty, topics, ", ", ".");
        await _client.SendLineAsync();
    }

    private static bool IsActionHelpTopic(string? topic)
    {
        return string.Equals(topic, "action", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(topic, "actions", StringComparison.OrdinalIgnoreCase);
    }

    // Help-topic lookup and CP437-border rendering now live in the shared HelpTopicRenderer
    // so the character-creation `? <topic>` prompt reaches identical output. See HelpTopicRenderer.cs.
    private Task RenderHelpBodyAsync(string body) => HelpTopicRenderer.RenderBodyAsync(_client, body);

    private async Task<bool> TryHandleSocialAction(string command, string args)
    {
        if (!_world.Database.Actions.TryGetValue(command, out var action))
            return false;

        if (!_player.ActionsEnabled)
        {
            await _client.SendLineAsync("You may not use action commands while they are turned off.");
            return true;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return true;

        await BreakSneakAndHideForAction();

        string target = args.Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            if (!action.SupportsSingleTarget)
            {
                await _client.SendLineAsync("Your command had no effect.");
                return true;
            }

            await SendSocialActionToActorAsync(FormatSocialActionMessage(action.SingleToUser));
            SendSocialActionToRoom(
                // Pass the actor's possessive too: a few no-target room templates encode it as a plain
                // %s ("%s is chuckling under %s breath."); harmless extra arg for the 1-%s majority.
                FormatSocialActionMessage(action.SingleToRoom, _player.Name, _player.HisHer_Lower),
                _player.Name);
            return true;
        }

        if (action.SupportsUserTarget)
        {
            var playerTarget = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);
            if (playerTarget != null)
            {
                await SendSocialActionToActorAsync(FormatSocialActionMessage(action.UserToUser, playerTarget.Name));
                SendSocialActionToPlayer(playerTarget.Name, FormatSocialActionMessage(action.UserToOtherUser, _player.Name));
                SendSocialActionToRoom(
                    FormatSocialActionMessage(action.UserToRoom, _player.Name, playerTarget.Name),
                    _player.Name,
                    playerTarget.Name);
                return true;
            }
        }

        if (action.SupportsMonsterTarget)
        {
            var monsterTarget = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
            if (monsterTarget != null)
            {
                await SendSocialActionToActorAsync(FormatSocialActionMessage(action.MonsterToUser, monsterTarget.DisplayName));
                SendSocialActionToRoom(
                    FormatSocialActionMessage(action.MonsterToRoom, _player.Name, monsterTarget.DisplayName),
                    _player.Name);
                return true;
            }
        }

        if (action.SupportsInventoryTarget)
        {
            // A "held item" target is carried OR worn — target resolution scans equipped
            // items too (same as `look`). This was inventory-only, so "slap <worn item>" missed an
            // item the player had equipped. includeEquipped fixes both worn and carried in one pass.
            var carried = FindMatchingCarriedItems(target, includeEquipped: true);
            if (carried.Count > 0)
            {
                var inventoryItem = carried[0].Item;
                await SendSocialActionToActorAsync(FormatSocialActionMessage(action.InventoryToUser, inventoryItem.Name));
                SendSocialActionToRoom(
                    FormatSocialActionMessage(action.InventoryToRoom, _player.Name, _player.HisHer_Lower, inventoryItem.Name),
                    _player.Name);
                return true;
            }
        }

        if (action.SupportsFloorItemTarget)
        {
            var (groundItemId, _) = _world.FindGroundItemByName(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, includeHidden: false);
            if (groundItemId > 0 && _world.Database.Items.TryGetValue(groundItemId, out var groundItem))
            {
                await SendSocialActionToActorAsync(FormatSocialActionMessage(action.FloorItemToUser, groundItem.Name));
                SendSocialActionToRoom(
                    FormatSocialActionMessage(action.FloorItemToRoom, _player.Name, groundItem.Name),
                    _player.Name);
                return true;
            }
        }

        await _client.SendLineAsync("You don't see that anywhere!");
        return true;
    }

    private async Task SendSocialActionToActorAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        await _client.SendLineAsync(FormatSocialActionOutput(message));
    }

    private void SendSocialActionToPlayer(string playerName, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _world.SendToPlayer(playerName, FormatSocialActionOutput(message));
    }

    private void SendSocialActionToRoom(string message, params string[] excludedPlayerNames)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var excluded = new HashSet<string>(excludedPlayerNames, StringComparer.OrdinalIgnoreCase);
        foreach (var observer in _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber))
        {
            if (excluded.Contains(observer.Name))
                continue;

            _world.SendToPlayer(observer.Name, FormatSocialActionOutput(message));
        }
    }

    private static string FormatSocialActionOutput(string message)
    {
        if (message.IndexOf('\u001b') >= 0)
            return message.EndsWith(MudAnsi.Reset, StringComparison.Ordinal) ? message : message + MudAnsi.Reset;

        return $"{MudAnsi.Green}{message}{MudAnsi.Reset}";
    }

    private string FormatSocialActionMessage(string template, params object?[] args)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var builder = new StringBuilder(template.Length + 32);
        int argIndex = 0;

        for (int index = 0; index < template.Length; index++)
        {
            if (template[index] == '%' && index + 1 < template.Length)
            {
                char marker = template[index + 1];
                // %p = the actor's possessive pronoun (his/her). A social action's actor is always the
                // player issuing it, so %p resolves from _player's gender and does NOT consume a
                // positional argument — only %s/%d are positional (the actor and target names). NOTE:
                // some stock templates instead encode the possessive as a plain %s (e.g. caress's
                // "%s caresses %s %s gently." = name, possessive, item; "%s is chuckling under %s
                // breath."); for those the caller passes _player.HisHer_Lower in as an ordinary %s
                // argument, so both stock conventions render correctly.
                if (marker == 'p')
                {
                    builder.Append(_player.HisHer_Lower);
                    index++;
                    continue;
                }
                if (marker is 's' or 'd')
                {
                    string replacement = argIndex < args.Length
                        ? Convert.ToString(args[argIndex], CultureInfo.InvariantCulture) ?? string.Empty
                        : string.Empty;
                    builder.Append(replacement);
                    argIndex++;
                    index++;
                    continue;
                }
            }

            builder.Append(template[index]);
        }

        return ResolveAnsiTokens(MudText.CollapseSpaces(builder.ToString()).Trim());
    }

    private static string ResolveAnsiTokens(string value)
    {
        // Cheap pre-filter that matches both {Ansi.NAME} and {MudAnsi.NAME} (both contain "Ansi.").
        if (string.IsNullOrEmpty(value) || value.IndexOf("Ansi.", StringComparison.OrdinalIgnoreCase) < 0)
            return value;

        return AnsiTokenRegex.Replace(value, match =>
        {
            string tokenName = match.Groups["name"].Value;
            return AnsiTokenValues.TryGetValue(tokenName, out string? ansiValue)
                ? ansiValue
                : match.Value;
        });
    }

    private async Task HandleSet(string args)
    {
        var setParts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var setting = setParts.Length > 0 ? setParts[0].ToLowerInvariant() : "";
        var valueRaw = setParts.Length > 1 ? setParts[1].Trim() : "";
        var value = valueRaw.ToLowerInvariant();

        if (setting == "talk")
        {
            if (value == "fast")
            {
                _player.TalkMode = 1;
                await _client.SendLineAsync($"{MudAnsi.White}Anything you type which is not recognized will be sent to the room.{MudAnsi.Reset}");
            }
            else // "slow", "", or anything else = slow
            {
                _player.TalkMode = 0;
                await _client.SendLineAsync($"{MudAnsi.White}To talk to the room, you must begin your line with a .{MudAnsi.Reset}");
            }
        }
        else if (setting.Length >= 3 && "receive".StartsWith(setting, StringComparison.OrdinalIgnoreCase))
        {
            bool newValue = value switch
            {
                "on" => true,
                "off" => false,
                "" => !_player.ReceiveItemsEnabled,
                _ => !_player.ReceiveItemsEnabled,
            };

            _player.ReceiveItemsEnabled = newValue;
            await _client.SendLineAsync(newValue
                ? $"{MudAnsi.White}You will now receive items from other players.{MudAnsi.Reset}"
                : $"{MudAnsi.White}You will no longer receive items from other players.{MudAnsi.Reset}");
        }
        else if (setting.Length >= 4 && "warning".StartsWith(setting, StringComparison.OrdinalIgnoreCase))
        {
            bool? requestedWarning = value switch
            {
                "on" => true,
                "off" => false,
                "" => !_player.WarnOnEvilEnabled,
                _ => null,
            };

            if (!requestedWarning.HasValue)
            {
                await _client.SendLineAsync("Valid warning options: ON, OFF");
            }
            else
            {
                _player.WarnOnEvilEnabled = requestedWarning.Value;
                await _client.SendLineAsync(_player.WarnOnEvilEnabled
                    ? "You will now be warned and stopped from doing most evil actions."
                    : "You will no longer be stopped from performing evil actions.");
            }
        }
        else if (setting == "gang")
        {
            if (value is "online")
            {
                _player.GangViewOnlineOnly = true;
                await _client.SendLineAsync("You will now only see online gang members.");
            }
            else if (value is "all")
            {
                _player.GangViewOnlineOnly = false;
                await _client.SendLineAsync("You will now see all gang members.");
            }
            else
            {
                await _client.SendLineAsync("Valid gang options: Online, All");
            }
        }
        else if (setting.Length >= 3 && "suicide".StartsWith(setting, StringComparison.OrdinalIgnoreCase))
        {
                // SET SUICIDE (prefix-matched) opens an interactive masked prompt -- there is no
            // inline "SET SUICIDE PASSWORD <x>" / CLEAR syntax, and no separate SET REROLL.
            await HandleSetSuicidePasswordAsync();
        }
        else if (setting == "keep")
        {
            bool newKeepMode = value switch
            {
                "on" => true,
                "off" => false,
                "" => !_player.KeepMode,
                _ => !_player.KeepMode,
            };

            _player.KeepMode = newKeepMode;
            await _client.SendLineAsync(newKeepMode
                ? $"{MudAnsi.White}If you die/reroll you will keep some of your experience.{MudAnsi.Reset}"
                : $"{MudAnsi.White}If you die/reroll you will start with 0 experience.{MudAnsi.Reset}");
        }
        // "set mineps <amount>" — NON-STOCK, and only a recognized SET option while the realm runs
        // SYSOP CONFIGURE MINEPS on (otherwise it falls through to the settings list, which hides it too).
        // The amount is a floor for the PASSIVE evil-point forgiveness drift, nothing else: it never
        // grants evil points, so the standing still has to be earned by doing the deeds. It exists
        // because a character left scripting overnight gets forgiven out of their band (Outlaw → Seedy)
        // and the gear that band allowed is stripped off them.
        else if (setting.Length >= 3 && "mineps".StartsWith(setting, StringComparison.OrdinalIgnoreCase)
                 && _world.MinEvilPointsEnabled)
        {
            await HandleSetMinEvilPointsAsync(valueRaw, value);
        }
        // "set look modern|traditional" is a non-stock QOL toggle; when disabled, "look" is not a recognized
        // SET option and falls through to the available-settings help (which also hides it — see below).
        else if (setting == "look" && _world.IsQolEnabled(QolFeature.SetLook))
        {
            if (value == "modern")
            {
                _player.UseModernLookStyle = true;
                await _client.SendLineAsync($"{MudAnsi.White}Look style is now Modern.{MudAnsi.Reset}");
            }
            else if (value == "traditional")
            {
                _player.UseModernLookStyle = false;
                await _client.SendLineAsync($"{MudAnsi.White}Look style is now Traditional.{MudAnsi.Reset}");
            }
            else
            {
                await _client.SendLineAsync($"Look style is currently {GetLookStyleDisplayName(_player)}.");
                await _client.SendLineAsync("Valid look options: Modern, Traditional");
            }
        }
        else if (setting.Length >= 3 && "style".StartsWith(setting, StringComparison.OrdinalIgnoreCase))
        {
            bool? requestedStyle = value switch
            {
                "technical" => true,
                "fantasy" => false,
                "" => !_player.UseTechnicalStyle,
                _ => null,
            };

            if (!requestedStyle.HasValue)
            {
                await _client.SendLineAsync("Valid style options: TECHNICAL, FANTASY");
            }
            else
            {
                _player.UseTechnicalStyle = requestedStyle.Value;
                await _client.SendLineAsync(_player.UseTechnicalStyle
                    ? "You will now receive techincal style messages."
                    : "You will now receive fantasy style messages.");
            }
        }
        else if (setting.Length >= 3 && "palette".StartsWith(setting, StringComparison.OrdinalIgnoreCase))
        {
            string paletteOptions = string.Join(", ", GameColorPalettes.GetRegisteredIds());

            if (string.IsNullOrWhiteSpace(valueRaw))
            {
                // Bare SET PALETTE is a non-stock query convenience (stock only handles SET PALETTE <n>).
                await _client.SendLineAsync($"Colour palette is currently {_player.PaletteId}.");
                await _client.SendLineAsync($"Valid palettes are: {paletteOptions}");
            }
            else if (!int.TryParse(valueRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int paletteId) ||
                     !GameColorPalettes.IsRegistered(paletteId))
            {
                // An unrecognized palette prints only the valid list.
                await _client.SendLineAsync($"Valid palettes are: {paletteOptions}");
            }
            else
            {
                _player.PaletteId = paletteId;
                // Stock confirmation line.
                await _client.SendLineAsync($"{MudAnsi.White}You will now use colour palette number {paletteId}.{MudAnsi.Reset}");
            }
        }
        else if (setting == "statline")
        {
            await ApplyStatlineSettingAsync(valueRaw);
        }
        else
        {
            await _client.SendLineAsync("Available settings: set talk [slow/fast]");
            await _client.SendLineAsync("Available settings: set statline [on/off/full/custom <template>]");
            await _client.SendLineAsync("Available settings: set receive [on/off]");
            await _client.SendLineAsync("Available settings: set warn [on/off]");
            await _client.SendLineAsync("Available settings: set gang [online/all]");
            await _client.SendLineAsync("Available settings: set keep [on/off]");
            if (_world.IsQolEnabled(QolFeature.SetLook))
                await _client.SendLineAsync("Available settings: set look [modern/traditional]");
            if (_world.MinEvilPointsEnabled)
                await _client.SendLineAsync($"Available settings: set mineps [{(int)Player.NoEvilPointFloor}-{Player.EvilPointGainCap}/off]");
            await _client.SendLineAsync($"Available settings: set palette [{string.Join('/', GameColorPalettes.GetRegisteredIds())}]");
            await _client.SendLineAsync("Available settings: set suicide");
        }
    }

    // SET MINEPS <amount|off> — the player's floor for evil-point forgiveness. Reachable only while
    // SYSOP CONFIGURE MINEPS is on. Stored as Player.MinEvilPoints, where the scale's own bottom
    // (-220) doubles as "unset", so OFF just writes that back.
    private async Task HandleSetMinEvilPointsAsync(string valueRaw, string value)
    {
        const int floor = (int)Player.NoEvilPointFloor;
        int ceiling = Player.EvilPointGainCap;
        string syntax = $"Valid minimum evil points: {floor} to {ceiling}, or OFF.";

        if (string.IsNullOrWhiteSpace(valueRaw))
        {
            await _client.SendLineAsync(HasMinEvilPointsSet(_player)
                ? $"{MudAnsi.White}Your evil points will not be forgiven below {FormatEvilPoints(_player.MinEvilPoints)}.{MudAnsi.Reset}"
                : $"{MudAnsi.White}You have no minimum evil points set.{MudAnsi.Reset}");
            await _client.SendLineAsync(syntax);
            return;
        }

        // OFF/NONE/CLEAR clear the floor. NOT "0" — zero is a legitimate minimum (hold at Neutral rather
        // than drifting Good/Saint), so it has to stay a number.
        if (value is "off" or "none" or "clear")
        {
            _player.MinEvilPoints = Player.NoEvilPointFloor;
            await _client.SendLineAsync($"{MudAnsi.White}Your evil points will now be forgiven normally.{MudAnsi.Reset}");
            return;
        }

        if (!int.TryParse(valueRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minimum)
            || minimum < floor || minimum > ceiling)
        {
            await _client.SendLineAsync(syntax);
            return;
        }

        _player.MinEvilPoints = minimum;
        await _client.SendLineAsync($"{MudAnsi.White}Your evil points will not be forgiven below {minimum}.{MudAnsi.Reset}");

        // Setting a minimum does not hand out evil points, and saying so up front saves the "I set it to
        // 100 and I am still Seedy" round trip: it only holds a standing they have already earned.
        if (_player.EvilPoints < minimum)
            await _client.SendLineAsync("You are not that evil yet — this will hold your evil points at a special level not raise it.");
    }

    private static bool HasMinEvilPointsSet(Player player) => player.MinEvilPoints > Player.NoEvilPointFloor;

    private static string FormatEvilPoints(float value)
        => ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);

    // SET SUICIDE: an interactive, echo-masked
    // password prompt. There is no inline "SET SUICIDE PASSWORD <x>" or CLEAR syntax in stock, and the
    // single password guards both suicide and reroll (there is no SET REROLL). With a password already set
    // the current one must be verified first; the new password is capped at 8 characters.
    // The secret is never echoed or displayed -- input is masked and the profile only ever
    // reports whether one is set. Runs on the world gate like the other interactive commands (map, train).
    private async Task HandleSetSuicidePasswordAsync()
    {
        await RunBufferedInteractiveCommandAsync(async () =>
        {
            // First stage: verify the existing password before allowing a change.
            if (!string.IsNullOrEmpty(_player.SuicideRerollPassword))
            {
                await _client.SendAsync("Enter the current password: ");
                // ReadLineMaskedAsync masks each keystroke and echoes CR/LF on Enter, so the prompt line
                // is already terminated -- no extra newline needed before the result message.
                string current = FirstToken(await _client.ReadLineMaskedAsync());

                if (current.Length == 0)
                {
                    await _client.SendLineAsync("Password NOT changed");
                    return;
                }

                if (!string.Equals(current, _player.SuicideRerollPassword, StringComparison.Ordinal))
                {
                    await _client.SendLineAsync("Invalid password!");
                    return;
                }
            }

            // Second stage: read the new password, re-prompting while it exceeds the 8-char cap.
            while (true)
            {
                await _client.SendAsync("Enter New Password: ");
                string next = FirstToken(await _client.ReadLineMaskedAsync());

                if (next.Length == 0)
                {
                    await _client.SendLineAsync("Password NOT changed");
                    return;
                }

                if (next.Length > 8)
                {
                    await _client.SendLineAsync("The password may not be longer than 8 characters.");
                    continue;
                }

                _player.SuicideRerollPassword = next;
                _world.PlayerRepo.SavePlayer(_player);
                await _client.SendLineAsync("Password changed");
                return;
            }
        });
    }

    // Stock tokenizes prompt input, so a suicide password is the first whitespace-delimited
    // token (it cannot contain spaces); mirror that instead of taking the raw line.
    private static string FirstToken(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length > 0 ? tokens[0] : string.Empty;
    }

    // The statline command (SET STATLINE ... and the standalone STATLINE verb). Sets the
    // prompt mode: ON/OFF/FULL plus CUSTOM / FULL CUSTOM with a ≤60-char %-template.
    private async Task ApplyStatlineSettingAsync(string valueRaw)
    {
        var parts = valueRaw.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string first = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
        string rest = parts.Length > 1 ? parts[1].Trim() : "";

        switch (first)
        {
            case "on":
                _player.StatlineMode = Statline.ModeOn;
                await _client.SendLineAsync("Your statline is now ON.");
                break;
            case "off":
                _player.StatlineMode = Statline.ModeOff;
                await _client.SendLineAsync("Your statline is now OFF.");
                break;
            case "custom":
                if (string.IsNullOrWhiteSpace(rest))
                {
                    await _client.SendLineAsync("Syntax: SET STATLINE CUSTOM <template>");
                    break;
                }
                _player.CustomStatline = TrimStatlineTemplate(rest);
                _player.StatlineMode = Statline.ModeCustom;
                await _client.SendLineAsync("Your custom statline has been set.");
                break;
            case "full":
                if (rest.StartsWith("custom", StringComparison.OrdinalIgnoreCase))
                {
                    string template = rest.Length > 6 ? rest.Substring(6).Trim() : "";
                    if (string.IsNullOrWhiteSpace(template))
                    {
                        await _client.SendLineAsync("Syntax: SET STATLINE FULL CUSTOM <template>");
                        break;
                    }
                    _player.CustomStatline = TrimStatlineTemplate(template);
                    _player.StatlineMode = Statline.ModeFullCustom;
                    await _client.SendLineAsync("Your custom statline has been set.");
                }
                else
                {
                    _player.StatlineMode = Statline.ModeFull;
                    await _client.SendLineAsync("Your statline is now FULL.");
                }
                break;
            default:
                await _client.SendLineAsync("Valid statline options: ON, OFF, FULL, CUSTOM xxx, FULL CUSTOM xxx");
                break;
        }
    }

    // Custom statline templates are capped at 60 characters (help statline custom).
    private static string TrimStatlineTemplate(string template)
        => template.Length > 60 ? template.Substring(0, 60) : template;

    // Ability 76 (mute). The mute bit gates ONLY two paths, each emitting its own
    // message from an if/else where the muted branch prints and aborts:
    //   * say/yell: "You cannot speak!"
    //   * tell:     "You may not direct any messages right now."
    // whisper/telepath/gossip/auction/broadcast are NOT mute-gated in stock — the bit is checked at
    // only those two sites (verified against the strings dump). Emits the exact stock text;
    // returns true when the speech was suppressed.
    private async Task<bool> IsSilencedByMuteAsync(string muteMessage)
    {
        if (!_player.IsMuted)
            return false;
        await _client.SendLineAsync(muteMessage);
        return true;
    }

    private async Task HandleSay(string message, bool speakEvenWithoutAudience = false)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await _client.SendLineAsync("Say what?");
            return;
        }

        // Speech never fires a room action: stock matches the raw typed line against the room's exits and
        // CMD script BEFORE anything is spoken (the `say` case and the unknown-command path both do that
        // first), and only speaks what nothing claimed. So ".trees" is just chatter, while typing the
        // literal phrase "say trees" is what solves the White Forest riddle.
        if (await IsSilencedByMuteAsync("You cannot speak!"))
            return;

        // With no VISIBLE audience a say is normally a no-op — and, deliberately, it reads the SAME whether
        // the room is empty or holds only a hidden/sneaking player, so speech can't be used to detect a
        // hider. The one exception is fast-talk mode (SET TALK FAST): there, unrecognized input the player
        // typed is meant to be spoken, so it still echoes "You say ..." to the speaker even when alone. This
        // stays leak-safe because fast-talk always echoes regardless of who's present, so it reveals nothing
        // about a hidden occupant either.
        if (!speakEvenWithoutAudience && !HasVisibleRoomSayAudience())
        {
            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return;

        await BreakSneakAndHideForAction();

        // Normal say chatter is Green (ESC[0;32m).
        await _client.SendLineAsync($"{MudAnsi.Green}You say \"{message}\"{MudAnsi.Reset}");
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            $"{MudAnsi.Green}{_player.Name} says \"{message}\"{MudAnsi.Reset}", _client);
    }

    private bool HasVisibleRoomSayAudience()
    {
        return _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            .Any(player => !player.IsHidden);
    }

    private async Task HandleApostropheSpeech(string message)
    {
        if (_player.BroadcastChannel > 0)
        {
            await HandleBroadcastChannelMessage(message);
            return;
        }

        await HandleSay(message);
    }

    // WHISPER: telepath one player by name -> "<name> telepaths: <msg>".
    // Honors the target's ignore list. Rate counters and the
    // telepath-block toggle are stock nuances not yet modeled.

    // TELL: a PUBLIC in-room message aimed at one player. The recipient sees
    // "<name> says (to you) "<msg>"", everyone else in the room sees "<name> says (to <recipient>) "<msg>"",
    // and the sender gets a "--- Message Directed to <recipient> ---" confirmation (the sender does NOT see
    // an echo of the text). Stock resolves the target FIRST, so an unknown/offline name reads "Cannot find
    // user!" while an online-but-unreachable one (different room or hidden) reads "--- Message Not Sent ---".
    private async Task HandleDirectMessage(string args)
    {
        var parts = (args ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            await _client.SendLineAsync("You have to specify a person to direct to.");
            return;
        }
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            await _client.SendLineAsync("You have to direct something!");
            return;
        }

        var target = _world.FindOnlinePlayer(parts[0]);

        // Stock order: self, then unknown-user, THEN the mute/room checks. So an unknown name always reads
        // "Cannot find user!" — distinct from an online player who simply isn't reachable here.
        if (target != null && target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Why are you directing messages to yourself?");
            return;
        }
        if (target == null)
        {
            await _client.SendLineAsync("Cannot find user!");
            return;
        }
        if (await IsSilencedByMuteAsync("You may not direct any messages right now."))
            return;
        if (target.CurrentMapNumber != _player.CurrentMapNumber
            || target.CurrentRoomNumber != _player.CurrentRoomNumber
            || target.IsHidden)
        {
            await _client.SendLineAsync("--- Message Not Sent ---");
            return;
        }

        // TELL does NOT break sneak/hide — verified against stock (it never touches the flags).
        string message = parts[1].Trim();
        _world.SendToPlayer(target.Name, $"{MudAnsi.Green}{_player.Name} says (to you) \"{message}\"{MudAnsi.Reset}");

        // Everyone else in the room (sender and recipient excluded) sees the directed form.
        foreach (var observer in _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player))
        {
            if (observer.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            _world.SendToPlayer(observer.Name, $"{MudAnsi.Green}{_player.Name} says (to {target.Name}) \"{message}\"{MudAnsi.Reset}");
        }

        await _client.SendLineAsync($"--- Message Directed to {target.Name} ---");
    }

    // GREET: resolve a room target; NPC -> ask a (generic) question; player -> no-op.
    private async Task HandleGreet(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Greet whom?");
            return;
        }

        await HandleAsk(args);
    }

    private async Task<bool> TryHandleRoomAction(string command)
    {
        // The input dispatcher scans the room's EXIT table — text exits (type 10) AND
        // remote-action exits (type 12) — BEFORE running the room CMD textblock
        // — so a verb that matches a remote-action exit wins over a same-verb CMD-textblock line.
        // The Ancestral Tomb sarcophagus depends on this: room 6/859 carries BOTH a type-0xc "push coffin"
        // exit (reveals the west "dark passage" to 860, msgs 1530/1526) AND CMD textblock 984
        // ("push coffin" -> message 1525 "...moves not"). Textblock-first prints the failure forever and the
        // passage never opens. Order here: text exits, then remote actions, then the CMD textblock last.
        if (await TryHandleTextCommandExitAsync(command, showMissingMessage: false))
            return true;

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
            return false;

        var remoteAction = _world.TryTriggerRemoteAction(_player, room, command);
        if (remoteAction == null)
            return await TryHandleRoomCommandTextBlockAsync(command);

        // Typed room-action verbs run via the room CMD path, which does NOT
        // clear the sneak bit. Only movement (handled earlier via TryHandleTextCommandExitAsync) breaks sneak.
        // Same coloring as the say-triggered action (HandleSay): the action MESSAGE line ("You pull the
        // lever.") uses the f0a0 default-text color, the hidden-exit REVEAL is emphatic BrightWhite.
        string defaultText = GameColorPalettes.Resolve(_player.PaletteId).Get(GameColorRole.DefaultText);
        if (!string.IsNullOrWhiteSpace(remoteAction.PlayerSpeechMessage))
            await _client.SendLineAsync($"{defaultText}{remoteAction.PlayerSpeechMessage}{MudAnsi.Reset}");

        if (!string.IsNullOrWhiteSpace(remoteAction.RoomSpeechMessage))
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{defaultText}{remoteAction.RoomSpeechMessage}{MudAnsi.Reset}", _client);

        if (!string.IsNullOrWhiteSpace(remoteAction.RevealPlayerMessage))
            await _client.SendLineAsync($"{MudAnsi.BrightWhite}{remoteAction.RevealPlayerMessage}{MudAnsi.Reset}");

        if (!string.IsNullOrWhiteSpace(remoteAction.RevealRoomMessage))
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{MudAnsi.BrightWhite}{remoteAction.RevealRoomMessage}{MudAnsi.Reset}", _client);

        await ConsumeItemChargeAsync(remoteAction.ConsumedItemId);
        return true;
    }

    // Returns false when stock returns 0 ("not handled") — see TryOpenInventoryItemAsync.
    private async Task<bool> HandleOpen(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            // A bare OPEN prints the syntax line "Syntax: OPEN {Direction|Item}",
            // not an invented "Open what?".
            await _client.SendLineAsync("Syntax: OPEN {Direction|Item}");
            return true;
        }

        // OPEN with an argument breaks the opener's autocombat before
        // resolving the target — so `open <anything>` ends your attack loop whether it turns out to be a
        // door, an inventory container, or nothing.
        await BreakAutocombatAsync();

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return true;
        }

        // Try opening a door/barrier exit first.
        if (_world.TryOpenExit(_player, room, args, out var message, out var openedExit, out bool openDoorPresent))
        {
            await _client.SendLineAsync(message);

            // Near room: "You see %s open the door to the %s." / "You see %s open the %s
            // to the %s." — the same shape CLOSE uses in HandleClose below.
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"You see {_player.Name} open the {openedExit!.DoorNoun} to the {openedExit.Direction}.", _client);

            // Far room: "The door to the %s just opened." / "The %s to the %s just
            // opened.", named from the OTHER side of the door. OPEN sends it only
            // when the far room's reverse exit points back here AND is itself a door/gate.
            var openReverse = _world.GetReverseExit(openedExit);
            if (openReverse != null && openReverse.IsBarrierExit)
            {
                _world.BroadcastToRoom(openReverse.MapNumber, openReverse.RoomNumber,
                    $"The {openedExit.DoorNoun} to the {openReverse.Direction} just opened.", _client);
            }

            await BreakSneakAndHideForAction();
            await ApplyActionDelayAsync(OpenCloseDoorFastTicks);   // OPEN: a 1-tick delay
            return true;
        }

        // OPEN clears the sneak and hide bits the moment it
        // acts on a REAL door — even when the open then fails because the door is locked or already open.
        // Only "no door that way" leaves stealth intact. So a failed open of a locked door still gives you
        // away.
        if (openDoorPresent)
        {
            await _client.SendLineAsync(message);
            await BreakSneakAndHideForAction();
            return true;
        }

        // Once the argument parses as a DIRECTION (n/s/e/w/…/up/down), it's the
        // door path — an exit that way that is NOT a door/gate, OR no exit at all, both print "That is not
        // a door or a gate!". Only a NON-direction argument falls through to opening an
        // inventory item and, failing that, the syntax line.
        string firstToken = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (DirectionAliases.ContainsKey(firstToken))
        {
            await _client.SendLineAsync("That is not a door or a gate!");
            return true;
        }

        // Try opening an inventory item (chests, boxes — ItemType 8 with ability 43 linking to a spell).
        bool? opened = await TryOpenInventoryItemAsync(args.Trim());
        if (opened == null)
            return false;
        if (opened == true)
            return true;

        // An unresolvable non-direction argument re-prints the syntax line, same as the no-arg case.
        await _client.SendLineAsync("Syntax: OPEN {Direction|Item}");
        return true;
    }

    // Returns null when the one matching item is not an openable container (stock "not handled").
    private async Task<bool?> TryOpenInventoryItemAsync(string target)
    {
        if (!TryFindOpenableInventoryItem(target, out int inventoryIndex, out var item, out var spell, out var ambiguousNames, out bool matchedOther))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return true;
            }
            return matchedOther ? null : false;
        }

        if (_player.InCombat)
        {
            await _client.SendLineAsync("You can't do that while in combat!");
            return true;
        }

        await BreakSneakAndHideForAction();

        long instanceId = _player.InventoryInstanceIds[inventoryIndex];

        if (!TryRemoveInventoryItemAt(inventoryIndex, out _, out _))
        {
            await _client.SendLineAsync("You can't open that!");
            return true;
        }

        _world.RemoveItemRuntimeState(instanceId);

        await ExecuteTriggeredSpellByIdAsync(spell.Number, showRoomAfterTeleport: true,
            TriggeredCastAnnounce.RoomCast);

        // AFTER the loot spell, the container rolls its coin-on-open
        // maxes straight into the opener's purse — silently, with NO message. `lngrnd(0, max)` per
        // denomination has an exclusive top (the monster-drop path uses max+1 to include max; OPEN
        // does not), so Random.Next(0, max) is the faithful roll. The alder chest (#974: gold≤750,
        // silver≤2500, copper 0) is the canonical example — the gems come from the spell, the coins
        // from here. RecalcEquipment because coin weight feeds encumbrance.
        if (item.HasOpenCoins)
        {
            var (runic, platinum, gold, silver, copper) = item.RollOpenCoins(max => Random.Shared.Next(0, max));
            AddPlayerCurrency(runic, platinum, gold, silver, copper);
            RecalcEquipment();
        }

        await SendItemDestructionMessagesAsync(item);
        // Off-gate write-behind (GameSession MarkPlayerDirty) persists the consumed item; no synchronous
        // SavePlayer on the world gate.
        return true;
    }

    // Ability 42 = "Link to Spell" (learn on read), Ability 43 = "One Time Cast" (cast on use/open).
    // Openable items (chests, boxes) use ability 43 to trigger a spell when opened.
    private const int OneTimeCastAbilityId = 43;

    // Stock OPEN's item branch: the carried-item lookup over EVERY carried item (worn too) — two or more
    // different matches print the "be more specific" list, before the item is looked at. matchedOther is
    // set when exactly one item matched but it is not an openable container (ItemType 8 with a spell) or
    // the player may not use it: stock returns "not handled" there, so the command falls through.
    private bool TryFindOpenableInventoryItem(string target, out int inventoryIndex, out Item item, out GameSpell spell, out IReadOnlyList<string>? ambiguousNames, out bool matchedOther)
    {
        EnsureItemInstanceAlignment();
        ambiguousNames = null;
        matchedOther = false;
        inventoryIndex = -1;
        item = new Item();
        spell = new GameSpell();

        var matches = FindMatchingCarriedItems(target, includeEquipped: true);
        if (matches.Count == 0)
            return false;

        var distinctNames = matches.Select(m => m.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctNames.Count > 1)
        {
            ambiguousNames = distinctNames;
            return false;
        }

        var match = matches[0];
        int spellId = match.Item.UseSpellId > 0
            ? match.Item.UseSpellId
            : match.Item.Abilities.GetValueOrDefault(OneTimeCastAbilityId);
        if (match.IsEquipped
            || match.Item.ItemType != 8
            || spellId <= 0
            || !_world.Database.Spells.TryGetValue(spellId, out var foundSpell)
            || !CanPlayerUseItem(_player, match.Item))
        {
            matchedOther = true;
            return false;
        }

        inventoryIndex = match.InventoryIndex;
        item = match.Item;
        spell = foundSpell;
        return true;
    }

    // CLOSE opens with a four-part combat test, and everything else in the command
    // — including argument parsing — sits inside its true branch:
    //     not inside autocombat && not being attacked
    //         && no monster hit you this tick && no monster in the room could attack
    // Fail any of them and the command stops at the refusal line, so even a bare "close" typed
    // mid-fight answers with that line rather than the syntax line.
    // That third term is the count of monster attacks launched at you this tick (incremented beside
    // each monster swing, zeroed on upkeep) and has no field in this engine. It is subsumed in
    // practice: a monster that just swung at you is a live hostile in your room, which already makes
    // MonsterCouldAttack true. The only divergence is the tick in which you sneak in and are attacked
    // anyway, where MonsterCouldAttack's Gate A short-circuits — too narrow to warrant a new field.
    private bool IsCloseBlockedByCombat()
    {
        if (_player.InCombat || IsBeingAttackedByPlayer())
            return true;

        var monstersHere = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        return CombatEngine.MonsterCouldAttack(_player, monstersHere);
    }

    private async Task HandleClose(string args)
    {
        if (IsCloseBlockedByCombat())
        {
            // Note this is the ONE rejection in this command with no colour escape of
            // its own: stock prints it bare, where the syntax / not-a-door / not-open lines are all
            // prefixed with ESC[1;31;40m. So it lands in default text.
            await _client.SendLineAsync(GameAnsi.Neutral(
                "You may not close doors or gates while attacking or being attacked!", _player.PaletteId));
            return;
        }

        // No argument, or a token that cannot resolve to one of the ten direction
        // indices, both fall to the same syntax line. CLOSE takes a DIRECTION only — it
        // never matches a door by name or label.
        string direction = args.Trim();
        if (direction.Length == 0 || !MudDirections.Aliases.TryGetValue(direction.ToLowerInvariant(), out var directionName))
        {
            await _client.SendLineAsync(MudAnsi.Error("Syntax: CLOSE {Direction}"));
            return;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return;
        }

        var outcome = _world.TryCloseExit(_player, room, directionName, out var message, out var exit, out bool exitPresent);

        // Stock clears sneak and hidden as soon as the direction HAS an exit,
        // before it tests the exit type — so a close aimed at a plain passage still gives you away.
        if (exitPresent)
            await BreakSneakAndHideForAction();

        if (outcome != GameWorld.CloseExitOutcome.Closed)
        {
            await _client.SendLineAsync(MudAnsi.Error(message));
            return;
        }

        await _client.SendLineAsync(GameAnsi.Neutral(message, _player.PaletteId));

        // Near room: "You see %s close the %s to the %s."
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            $"You see {_player.Name} close the {exit!.DoorNoun} to the {directionName}.", _client);

        // Far room: "The %s to the %s just closed.", addressed to the room on the other
        // side and named from ITS side of the door. Stock sends this only when the far room's
        // reverse exit points back here AND is itself a door/gate.
        var reverse = _world.GetReverseExit(exit);
        if (reverse != null && reverse.IsBarrierExit)
        {
            _world.BroadcastToRoom(reverse.MapNumber, reverse.RoomNumber,
                $"The {exit.DoorNoun} to the {reverse.Direction} just closed.", _client);
        }

        await ApplyActionDelayAsync(OpenCloseDoorFastTicks);   // CLOSE: a 1-tick delay
    }

    private (string Message, TravelMessageType Type) GetTextExitSelfMessageWithType(RoomExitDefinition exit)
    {
        if (exit.Para3 > 0 && _world.Database.Messages.TryGetValue(exit.Para3, out var message) && !string.IsNullOrWhiteSpace(message.Line1))
            return (message.Line1, TravelMessageType.TextCommandTraversal);

        return (string.Empty, TravelMessageType.TextCommandTraversal);
    }

    private string GetTextExitDepartureMessage(RoomExitDefinition exit, string? playerName = null)
    {
        if (exit.Para3 > 0 && _world.Database.Messages.TryGetValue(exit.Para3, out var message) && !string.IsNullOrWhiteSpace(message.Line2))
            return message.Line2.Replace("%s", playerName ?? _player.Name, StringComparison.OrdinalIgnoreCase);

        return string.Empty;
    }

    private string GetTextExitArrivalMessage(RoomExitDefinition exit, string? playerName = null)
    {
        if (exit.Para3 > 0 && _world.Database.Messages.TryGetValue(exit.Para3, out var message) && !string.IsNullOrWhiteSpace(message.Line3))
            return message.Line3.Replace("%s", playerName ?? _player.Name, StringComparison.OrdinalIgnoreCase);

        return string.Empty;
    }

    private async Task BreakSneakAndHideForAction()
    {
        if (_player.IsSneaking)
            _player.IsSneaking = false;

        if (_player.IsHidden)
            _player.IsHidden = false;

        await Task.CompletedTask;
    }

    private async Task HandleYell(string message)
    {
        if (await IsSilencedByMuteAsync("You cannot speak!"))
            return;

        if (string.IsNullOrEmpty(message))
        {
            await _client.SendLineAsync("Yell what?");
            return;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return;

        // Yell breaks sneak/hide — the say/yell fallthrough clears the sneak bit
        // in both the say AND yell branches (verified against stock).
        await BreakSneakAndHideForAction();

        await _client.SendLineAsync($"{MudAnsi.Green}You yell \"{message}\"{MudAnsi.Reset}");
        string roomYellLine = $"{MudAnsi.Green}{_player.Name} yells \"{message}\"{MudAnsi.Reset}";
        string adjacentYellLine = $"{MudAnsi.Green}Someone yells \"{message}\"{MudAnsi.Reset}";
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, roomYellLine, _client);
        _world.BroadcastToAdjacentRooms(_player.CurrentMapNumber, _player.CurrentRoomNumber, adjacentYellLine, _client);
    }

    private async Task<bool> TryConsumeCommunicationAllowanceAsync()
    {
        var now = DateTime.UtcNow;
        if (_player.CommunicationThrottleUntilUtc > now)
        {
            await _client.SendLineAsync("You are talking too fast. Please wait a moment.");
            return false;
        }

        while (_player.CommunicationBurstTimestampsUtc.Count > 0 &&
               now - _player.CommunicationBurstTimestampsUtc.Peek() > CommunicationBurstWindow)
        {
            _player.CommunicationBurstTimestampsUtc.Dequeue();
        }

        if (_player.CommunicationBurstTimestampsUtc.Count >= CommunicationBurstLimit)
        {
            _player.CommunicationBurstTimestampsUtc.Clear();
            _player.CommunicationThrottleUntilUtc = now + CommunicationThrottleInterval;
            await _client.SendLineAsync("You are talking too fast. Please wait a moment.");
            return false;
        }

        _player.CommunicationBurstTimestampsUtc.Enqueue(now);
        return true;
    }

    // The realm-wide private message. In stock there is NO separate
    // same-room "whisper" command (the only "whisper" token in the binary is the function symbol); the
    // user-facing command is "telepath" (and the `/` prefix), so this single handler serves both. Verified
    // not to break sneak/hide. Coloring is byte-faithful: the MESSAGE text is the cat 13 (f150) telepath
    // color indexed by the RECIPIENT's palette (White on pal 0/1/2, BrightCyan on 3); the "X telepaths:"
    // prefix inherits the default color and the sender confirmation is White.
    private async Task HandleTelepath(string args)
    {
        var parts = (args ?? string.Empty).Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            await _client.SendLineAsync("You have to specify a person to telepath to.");
            return;
        }
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
        {
            await _client.SendLineAsync("You have to telepath something!");
            return;
        }

        string message = parts[1].Trim();
        var target = _world.FindOnlinePlayer(parts[0]);

        // Not in the realm — but they may be reachable on the web (telepath dock open). Deliver there.
        if (target == null)
        {
            string? webName = _world.ResolveWebPresent(parts[0]);
            if (webName == null)
            {
                await _client.SendLineAsync("Cannot find user!");
                return;
            }
            if (webName.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("Why are you telepathing to yourself?");
                return;
            }
            if (!await TryConsumeCommunicationAllowanceAsync())
                return;

            _world.PublishTelepathToWeb(_player.Name, webName, message);
            await _client.SendLineAsync($"{MudAnsi.White}--- Telepath Sent to {webName} ---{MudAnsi.Reset}");
            return;
        }

        if (target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Why are you telepathing to yourself?");
            return;
        }
        if (target.IgnoredPlayerNames.Contains(_player.Name))
        {
            await _client.SendLineAsync("--- Telepath Not Sent ---");
            return;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return;

        string messageColor = GameColorPalettes.Resolve(target.PaletteId).Get(GameColorRole.TelepathMessage);
        string targetLine = $"{MudAnsi.Green}{_player.Name} telepaths:{MudAnsi.Reset} {messageColor}{message}{MudAnsi.Reset}";

        _world.SendChannelMessage(_player.Name, target.Name, targetLine, reprompt: true, prependLineBreak: true);

        // If the recipient is ALSO viewing the web, mirror the telepath to their browser too.
        if (_world.IsWebPresent(target.Name))
            _world.PublishTelepathToWeb(_player.Name, target.Name, message);

        await _client.SendLineAsync($"{MudAnsi.White}--- Telepath Sent to {target.Name} ---{MudAnsi.Reset}");
    }

    private static bool MatchesPlayerNameOrPrefix(string candidate, string target)
    {
        return candidate.Equals(target, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(target, StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveIgnoredPlayerName(string target)
    {
        string trimmed = target.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return null;

        var ignored = _player.IgnoredPlayerNames.FirstOrDefault(name => MatchesPlayerNameOrPrefix(name, trimmed));
        if (!string.IsNullOrWhiteSpace(ignored))
            return ignored;

        var online = _world.FindOnlinePlayer(trimmed);
        if (online != null)
            return online.Name;

        return _world.PlayerRepo.PlayerExists(trimmed) ? trimmed : null;
    }

    private async Task HandleIgnore(string args)
    {
        string targetName = args.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            if (_player.IgnoredPlayerNames.Count == 0)
            {
                await _client.SendLineAsync("You have not forgotten any users.");
                return;
            }

            string ignored = string.Join(", ", _player.IgnoredPlayerNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
            await _client.SendLineAsync($"Forgotten Users: {ignored}");
            return;
        }

        var resolvedName = ResolveIgnoredPlayerName(targetName);
        if (string.IsNullOrWhiteSpace(resolvedName))
        {
            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        if (resolvedName.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("You may not forget yourself, sorry Scott.");
            return;
        }

        if (_player.IgnoredPlayerNames.Remove(resolvedName))
        {
            await _client.SendLineAsync($"You have no longer forgotten {resolvedName}");
            return;
        }

        _player.IgnoredPlayerNames.Add(resolvedName);
        await _client.SendLineAsync($"You have now forgotten {resolvedName}");
    }

    private async Task HandleCreate(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("gang", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Syntax: CREATE GANG <gangname>");
            return;
        }

        string rawGangName = parts[1].Trim();

        if (_player.Experience < _world.GangCreateMinimumExperience)
        {
            await _client.SendLineAsync("You are not experienced enough to start your own gang!");
            await _client.SendLineAsync($"Required experience: {_world.GangCreateMinimumExperience}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are already in one gang.  You cannot create another one.");
            return;
        }

        if (!IsValidGangName(rawGangName, out var invalidReason))
        {
            await _client.SendLineAsync(invalidReason);
            return;
        }

        if (_world.PlayerRepo.GangExists(rawGangName))
        {
            await _client.SendLineAsync("The name you have chosen is already being used!");
            return;
        }

        if (_world.GangCreateRunicCost > 0 && _player.Runic < _world.GangCreateRunicCost)
        {
            await _client.SendLineAsync($"You need {_world.GangCreateRunicCost} runic to create a gang.");
            await _client.SendLineAsync($"You currently have {_player.Runic} runic.");
            return;
        }

        if (_world.GangCreateRunicCost > 0)
            _player.Runic -= _world.GangCreateRunicCost;

        _world.PlayerRepo.CreateGangRecord(rawGangName, 9, _player.Name);
        _player.Gang = rawGangName;
        _player.GangExperience = 0;
        _player.IsGangLieutenant = false;

        await _client.SendLineAsync($"{_player.Name} is the leader of {_player.Gang}.");
        await _client.SendLineAsync("Gang created.");
    }

    private async Task HandleJoin(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1 && parts[0].Equals("gang", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGangJoin(args);
            return;
        }

        string targetName = args.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("Syntax: JOIN {group number} or {user name} or GANG {gangname}");
            return;
        }

        if (int.TryParse(targetName, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requestedChannel))
        {
            await HandleBroadcastChannelJoin(requestedChannel);
            return;
        }

        var target = _world.FindOnlinePlayer(targetName);
        if (target == null)
        {
            await _client.SendLineAsync($"{targetName} is not online.");
            return;
        }

        if (!_world.TryJoinParty(_player, target, out var selfMessage, out var leaderName, out var leaderMessage, out var disbandedOldPartyMembers))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(selfMessage);
        if (!string.IsNullOrWhiteSpace(leaderMessage))
            _world.SendToPlayer(leaderName, leaderMessage);
        // If the joiner led their own party, it is now disbanded by the switch — tell its former members.
        foreach (var oldMember in disbandedOldPartyMembers)
            _world.SendToPlayer(oldMember, GameAnsi.PartyNotice($"You are no longer following {_player.Name}."));
    }

    private async Task HandleBroadcastChannelJoin(int requestedChannel)
    {
        if (!_world.IsValidBroadcastChannel(requestedChannel))
        {
            await _client.SendLineAsync($"Broadcast channels must be between 0 and {GameWorld.MaxBroadcastChannelNumber}.");
            return;
        }

        int currentChannel = _player.BroadcastChannel;
        if (requestedChannel == currentChannel)
        {
            if (requestedChannel <= 0)
            {
                await _client.SendLineAsync("You are not on a broadcast channel.");
                return;
            }

            await ShowBroadcastChannelRoster(requestedChannel);
            return;
        }

        if (currentChannel > 0)
        {
            _world.AnnounceBroadcastChannelLeave(_player, currentChannel);
            await _client.SendLineAsync($"{MudAnsi.BrightYellow}You just left group {currentChannel}.{MudAnsi.Reset}");
        }

        _player.BroadcastChannel = requestedChannel;
        if (requestedChannel <= 0)
            return;

        _world.AnnounceBroadcastChannelJoin(_player, requestedChannel);
        await _client.SendLineAsync($"{MudAnsi.BrightYellow}You just joined channel {requestedChannel}.{MudAnsi.Reset}");
        await ShowBroadcastChannelRoster(requestedChannel);
    }

    private async Task ShowBroadcastChannelRoster(int channel)
    {
        var memberNames = _world.GetBroadcastChannelMemberNames(channel);
        await _client.SendLineAsync($"{MudAnsi.BrightYellow}The following users are on channel {channel}:{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.BrightYellow}{string.Join(", ", memberNames)}{MudAnsi.Reset}");
    }

    private async Task HandleGangJoin(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("gang", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Syntax: JOIN {group number} or {user name} or GANG {gangname}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You may not join another gang!  You are already a member of one.");
            return;
        }

        string requestedGang = parts[1].Trim();
        if (string.IsNullOrWhiteSpace(requestedGang))
        {
            await _client.SendLineAsync("Syntax: JOIN GANG <gangname>");
            return;
        }

        string? resolvedGang = _world.PlayerRepo.ResolveGangName(requestedGang);
        if (resolvedGang == null)
        {
            await _client.SendLineAsync("That gang doesn't exist!");
            return;
        }

        // No size gate: stock has no gang member limit. Joining increments the gang record's
        // member counter unconditionally, inviting never measures the gang, and no
        // "gang is full" string exists anywhere in stock. SYSOP GANGSIZE, which we had modelled as a
        // maximum, writes that SAME counter — the one leaving a gang decrements and
        // the member list prints as "%s members (%d)" — so it is a repair tool for a drifted count,
        // not a cap. (Stock says as much itself when a sysop renames a gang: "You must manually update
        // each gang's user counts.") We enforced GangSettings.MaxSize here and in HandleGangInvite, which
        // put a hard 9-member ceiling on every gang. Our own count is a live COUNT(*) and cannot drift.
        if (!_world.PlayerRepo.HasGangInvite(_player.Name, resolvedGang))
        {
            await _client.SendLineAsync("You have not been invited to join that gang!");
            return;
        }

        _player.Gang = resolvedGang;
        _player.GangExperience = 0;
        _player.IsGangLieutenant = false;
        _world.PlayerRepo.RemoveGangInvite(_player.Name, resolvedGang);

        await _client.SendLineAsync($"You have joined the gang {_player.Gang}.");
        foreach (var member in _world.GetAllOnlinePlayers().Where(p =>
                     !p.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase) &&
                     p.Gang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase)))
        {
            _world.SendToPlayer(member.Name, $"{_player.Name} just joined your gang.", reprompt: true, prependLineBreak: true);
        }
    }

    private async Task HandleInvite(string args)
    {
        if (args.TrimStart().StartsWith("member ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGangInvite(args);
            return;
        }

        string targetName = args.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("Syntax: INVITE {user name}");
            return;
        }

        // INVITE resolves a party invite from the ROOM, NOT globally — you can
        // only invite someone standing with you. A name not in your room yields "You don't see %s here."
        // (gang invites, handled above, are NOT room-scoped).
        var target = GameWorld.ResolvePlayerByNameOrPrefix(
            _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber), targetName);
        if (target == null)
        {
            await _client.SendLineAsync($"You don't see {targetName} here.");
            return;
        }

        if (!_world.TryInviteToParty(_player, target, out var selfMessage, out var inviteeMessage))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(selfMessage);
        // An empty invitee message means the group add was refused (the target already holds a
        // group slot with us): stock notifies nobody in that case but still prints the inviter's own
        // confirmation, which is emitted above.
        if (!string.IsNullOrEmpty(inviteeMessage))
            _world.SendToPlayer(target.Name, inviteeMessage);
    }

    private async Task HandleGangInvite(string args)
    {
        string targetName = args.Trim();
        if (targetName.StartsWith("member ", StringComparison.OrdinalIgnoreCase))
            targetName = targetName[7..].Trim();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("Syntax: INVITE {user name}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are not in a gang!");
            return;
        }

        bool canInvite = _world.PlayerRepo.IsGangLeader(_player.Name, _player.Gang)
                         || _world.PlayerRepo.IsGangLieutenant(_player.Name, _player.Gang);
        if (!canInvite)
        {
            await _client.SendLineAsync("You must be the leader or a lieutenant of your gang to invite new members!");
            return;
        }

        var target = _world.FindOnlinePlayer(targetName);
        if (target == null)
        {
            await _client.SendLineAsync($"{targetName} is not online.");
            return;
        }

        if (target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Why would you invite yourself?");
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.Gang))
        {
            await _client.SendLineAsync($"{target.Name} is already in a gang.");
            return;
        }

        // No size gate — inviting never measures the gang. See HandleGangJoin for why
        // SYSOP GANGSIZE is a member-count repair tool rather than the maximum we had read it as.
        _world.PlayerRepo.AddGangInvite(target.Name, _player.Gang, _player.Name);

        await _client.SendLineAsync($"You have invited {target.Name} to join your gang.");
        _world.SendToPlayer(target.Name,
            $"{MudAnsi.Bold}{MudAnsi.Blue}{_player.Name} has invited you to join {_player.Gang}.{MudAnsi.Reset}",
            reprompt: true,
            prependLineBreak: true);
    }

    private async Task HandleUninvite(string args)
    {
        if (args.TrimStart().StartsWith("member ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleGangUninvite(args);
            return;
        }

        string targetName = args.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("Syntax: UNINVITE {user name}");
            return;
        }

        if (!_world.TryRemoveFromParty(_player, targetName, out var actorMessage, out var targetMessage, out var removedPlayerName))
        {
            await _client.SendLineAsync(actorMessage);
            return;
        }

        await _client.SendLineAsync(GameAnsi.AttentionNotice(actorMessage, _player.PaletteId));
        if (!string.IsNullOrWhiteSpace(targetMessage) && !string.IsNullOrWhiteSpace(removedPlayerName))
        {
            int targetPaletteId = _world.FindOnlinePlayer(removedPlayerName)?.PaletteId ?? 0;
            _world.SendToPlayer(removedPlayerName, GameAnsi.AttentionNotice(targetMessage, targetPaletteId));
        }
    }

    private async Task HandleGangUninvite(string args)
    {
        string targetName = args.Trim();
        if (targetName.StartsWith("member ", StringComparison.OrdinalIgnoreCase))
            targetName = targetName[7..].Trim();

        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("Syntax: UNINVITE {user name}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are not in a gang!");
            return;
        }

        bool isLeader = _world.PlayerRepo.IsGangLeader(_player.Name, _player.Gang);
        bool isLieutenant = _world.PlayerRepo.IsGangLieutenant(_player.Name, _player.Gang);
        if (!isLeader && !isLieutenant)
        {
            await _client.SendLineAsync("You must be the leader or lieutenant of your gang to uninvite members!");
            return;
        }

        if (targetName.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Why would you uninvite yourself?");
            return;
        }

        var target = _world.FindOnlinePlayer(targetName);
        if (target != null && target.Gang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase))
        {
            bool targetLeader = _world.PlayerRepo.IsGangLeader(target.Name, _player.Gang);
            bool targetLieutenant = _world.PlayerRepo.IsGangLieutenant(target.Name, _player.Gang);
            if (targetLeader)
            {
                await _client.SendLineAsync("You are not able to uninvite the gang leader.");
                return;
            }

            if (targetLieutenant && !isLeader)
            {
                await _client.SendLineAsync("You must be the gang leader to uninvite a lieutenant!");
                return;
            }

            target.Gang = string.Empty;
            target.GangExperience = 0;
            target.IsGangLieutenant = false;
            _world.PlayerRepo.SavePlayer(target);

            await _client.SendLineAsync($"You have removed {target.Name} from your gang.");
            _world.SendToPlayer(target.Name,
                $"{MudAnsi.Red}{_player.Name} has exiled you from {_player.Gang}.{MudAnsi.Reset}",
                reprompt: true,
                prependLineBreak: true);
            return;
        }

        if (_world.PlayerRepo.IsGangLeader(targetName, _player.Gang))
        {
            await _client.SendLineAsync("You are not able to uninvite the gang leader.");
            return;
        }

        if (_world.PlayerRepo.IsGangLieutenant(targetName, _player.Gang) && !isLeader)
        {
            await _client.SendLineAsync("You must be the gang leader to uninvite a lieutenant!");
            return;
        }

        if (_world.PlayerRepo.RemovePlayerFromGang(targetName, _player.Gang))
        {
            await _client.SendLineAsync($"You have removed {targetName} from your gang.");
            return;
        }

        if (_world.PlayerRepo.HasGangInvite(targetName, _player.Gang))
        {
            _world.PlayerRepo.RemoveGangInvite(targetName, _player.Gang);
            await _client.SendLineAsync($"You removed {targetName} from your gang.");
            return;
        }

        await _client.SendLineAsync($"{targetName} is not in your gang.");
    }

    private async Task HandleFollow(string args)
    {
        string targetName = args.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("Syntax: FOLLOW {user/monster}");
            return;
        }

        var target = _world.FindOnlinePlayer(targetName);
        if (target == null)
        {
            await HandleGoEnter($"follow {args}");
            return;
        }

        if (!_world.TryJoinParty(_player, target, out var selfMessage, out var leaderName, out var leaderMessage, out var disbandedOldPartyMembers))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(selfMessage);
        if (!string.IsNullOrWhiteSpace(leaderMessage))
            _world.SendToPlayer(leaderName, leaderMessage);
        // If the joiner led their own party, it is now disbanded by the switch — tell its former members.
        foreach (var oldMember in disbandedOldPartyMembers)
            _world.SendToPlayer(oldMember, GameAnsi.PartyNotice($"You are no longer following {_player.Name}."));
    }

    // The party display's per-member row, reproduced byte-for-byte from the stock format strings
    // "  %-30.30s %-12.12s", " [%c:%3.3s%%]", a 9-space no-mana filler, and
    // " [H:%3.3s%%]%c%c%s":
    //   name+class : 2 leading spaces, name padded/truncated to 30, one space, class to 12.
    //   mana       : " [%c:%3.3s%%]" when shown (%c = 'K' Kai-magery / 'M' otherwise), else 9 blanks.
    //   hits+status: " [H:%3.3s%%]" then the TWO status chars with NO gap, then the rank string.
    //                  1st %c = 'P' poisoned else ' '   2nd %c = 'R' resting / 'M' meditating else ' '
    //                  rank %s already carries its leading " - " (" - Frontrank"/"- Backrank"/"- Midrank").
    // The old code inserted extra spaces before the status chars and an extra space before the rank, which
    // pushed the R/M/P flags one+ columns right of stock (the reported "Resting flag in the wrong spot").
    internal static string FormatPartyMemberRow(
        string fullName, string classLabel, bool showMana, bool usesKai, int manaPercent,
        int hitsPercent, bool poisoned, bool resting, bool meditating, GameWorld.PartyRank rank)
    {
        string name = fullName.Length > 30 ? fullName[..30] : fullName;
        string cls = classLabel.Length > 12 ? classLabel[..12] : classLabel;
        string manaSegment = showMana
            ? string.Format(CultureInfo.InvariantCulture, " [{0}:{1,3}%]", usesKai ? 'K' : 'M', manaPercent)
            : "         ";
        char poisonChar = poisoned ? 'P' : ' ';
        char restChar = resting ? 'R' : meditating ? 'M' : ' ';
        string rankLabel = rank switch
        {
            GameWorld.PartyRank.Front => " - Frontrank",
            GameWorld.PartyRank.Back => " - Backrank",
            _ => " - Midrank",
        };
        return string.Format(
            CultureInfo.InvariantCulture,
            "  {0,-30} {1,-12}{2} [H:{3,3}%]{4}{5}{6}",
            name, cls, manaSegment, hitsPercent, poisonChar, restChar, rankLabel);
    }

    private async Task HandleParty()
    {
        var snapshot = _world.GetPartySnapshot(_player);
        if (snapshot == null)
        {
            string fullName = string.IsNullOrWhiteSpace(_player.LastName) ? _player.Name : $"{_player.Name} {_player.LastName}";
            bool hasClass = _world.Database.Classes.TryGetValue(_player.ClassId, out var playerClass);
            string classLabel = hasClass ? $"({playerClass!.Name})" : "";
            int hitsPercent = _player.MaxHP > 0 ? Math.Clamp(_player.CurrentHP * 100 / _player.MaxHP, 0, 100) : 0;
            int manaPercent = _player.MaxMana > 0 ? Math.Clamp(_player.CurrentMana * 100 / _player.MaxMana, 0, 100) : 0;
            string statusLine = FormatPartyMemberRow(
                fullName, classLabel,
                showMana: _player.MaxMana > 0, usesKai: hasClass && Player.UsesKai(playerClass!), manaPercent: manaPercent,
                hitsPercent: hitsPercent,
                poisoned: _player.PoisonLevel > 0, resting: _player.IsResting, meditating: _player.IsMeditating,
                rank: GameWorld.PartyRank.Middle);

            await _client.SendLineAsync($"{MudAnsi.BrightRed}You are not in a party at the present time.{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{statusLine}{MudAnsi.Reset}");
            return;
        }

        if (!snapshot.ViewerIsLeader)
            await _client.SendLineAsync($"{MudAnsi.BrightCyan}You are following {snapshot.LeaderName}.{MudAnsi.Reset}");

        await _client.SendLineAsync("The following people are in your travel party:");

        foreach (var member in snapshot.Members)
        {
            string fullName = string.IsNullOrWhiteSpace(member.LastName) ? member.Name : $"{member.Name} {member.LastName}";
            string classLabel = string.IsNullOrWhiteSpace(member.ClassName) ? "" : $"({member.ClassName})";

            if (member.IsInvited)
            {
                // Stock format: "  %-30.30s %-12.12s [Invited]".
                string name = fullName.Length > 30 ? fullName[..30] : fullName;
                string cls = classLabel.Length > 12 ? classLabel[..12] : classLabel;
                await _client.SendLineAsync(string.Format(CultureInfo.InvariantCulture, "  {0,-30} {1,-12} [Invited]", name, cls));
                continue;
            }

            await _client.SendLineAsync(FormatPartyMemberRow(
                fullName, classLabel,
                showMana: member.HasMana, usesKai: member.UsesKai, manaPercent: member.ManaPercent,
                hitsPercent: member.HitsPercent,
                poisoned: member.IsPoisoned, resting: member.IsResting, meditating: member.IsMeditating,
                rank: member.Rank));
        }
    }

    private async Task HandleLeaveParty()
    {
        if (!_world.TryLeaveParty(_player, out var selfMessage, out var leaderName))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(selfMessage);

    }

    private async Task HandlePartyRank(GameWorld.PartyRank rank)
    {
        if (!_world.TrySetPartyRank(_player, rank, out var selfMessage, out var groupMessage))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(selfMessage);

        var snapshot = _world.GetPartySnapshot(_player);
        if (snapshot == null || string.IsNullOrWhiteSpace(groupMessage))
            return;

        foreach (var member in snapshot.Members.Where(member => !member.IsInvited && !member.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase)))
            _world.SendToPlayer(member.Name, groupMessage);
    }

    private async Task HandleDisband(string args)
    {
        string what = args.Trim();
        if (string.IsNullOrWhiteSpace(what))
        {
            await _client.SendLineAsync("Syntax: DISBAND {Party/Gang}");
            return;
        }

        if (what.Equals("gang", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDisbandGang();
            return;
        }

        if (what.Equals("party", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDisbandParty();
            return;
        }

        await _client.SendLineAsync("Syntax: DISBAND {Party/Gang}");
    }

    private async Task HandleDisbandParty()
    {
        if (!_world.IsPartyLeader(_player.Name))
        {
            await _client.SendLineAsync("You are not the leader of the party.");
            return;
        }

        if (!_world.TryDisbandParty(_player, out var selfMessage, out var affectedMembers))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(GameAnsi.PartyNotice(selfMessage));
        foreach (var memberName in affectedMembers)
            _world.SendToPlayer(memberName, GameAnsi.PartyNotice($"You are no longer following {_player.Name}."));
    }

    private async Task HandleIndependentPartyTravelCleanupAsync(Player player, bool disbandLeader)
    {
        if (!_world.TryCleanupPartyForIndependentTravel(player, disbandLeader, out var result) || result == null)
            return;

        bool isCurrentPlayer = ReferenceEquals(player, _player);

        if (result.DisbandedParty)
        {
            string selfMessage = GameAnsi.PartyNotice(result.SelfMessage);
            if (isCurrentPlayer)
                await _client.SendLineAsync(selfMessage);
            else
                _world.SendToPlayer(player.Name, selfMessage);

            foreach (var memberName in result.AffectedMembers)
                _world.SendToPlayer(memberName, GameAnsi.PartyNotice($"You are no longer following {player.Name}."));

            return;
        }

        if (isCurrentPlayer)
            await _client.SendLineAsync(result.SelfMessage);
        else
            _world.SendToPlayer(player.Name, result.SelfMessage);

    }

    private async Task HandleDisbandGang()
    {
        if (string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are not in a gang!");
            return;
        }

        if (!_world.PlayerRepo.IsGangLeader(_player.Name, _player.Gang))
        {
            await _client.SendLineAsync("You are not the leader of the gang; You may not disband it!");
            return;
        }

        string gangName = _player.Gang;
        int affected = _world.PlayerRepo.DisbandGang(gangName);
        foreach (var member in _world.GetAllOnlinePlayers().Where(player => player.Gang.Equals(gangName, StringComparison.OrdinalIgnoreCase)))
        {
            member.Gang = string.Empty;
            member.GangExperience = 0;
            member.IsGangLieutenant = false;
            if (!member.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
                _world.SendToPlayer(member.Name, $"The gang {gangName} has now been disbanded.");
        }

        _player.Gang = string.Empty;
        _player.GangExperience = 0;
        _player.IsGangLieutenant = false;
        await _client.SendLineAsync($"The gang {gangName} has now been disbanded.");
    }

    private async Task HandleBroadgang(string message)
    {
        if (string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are not in a gang at the present!");
            return;
        }

        if (!_world.PlayerRepo.GangExists(_player.Gang))
        {
            await _client.SendLineAsync("This gang has been disbanded.");
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            var members = _world.PlayerRepo.GetGangRoster(_player.Gang);
            var onlineNames = _world.GetAllOnlinePlayers()
                .Where(p => p.Gang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase))
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (_player.GangViewOnlineOnly)
                members = members.Where(m => onlineNames.Contains(m.Name)).ToList();

            if (_player.GangViewOnlineOnly)
                await _client.SendLineAsync($"{_player.Gang} members (online)");
            else
                await _client.SendLineAsync($"{_player.Gang} members ({members.Count})");

            if (members.Count == 0)
            {
                await _client.SendLineAsync("No gang members found.");
                return;
            }

            string leaderName = _world.PlayerRepo.GetGangLeaderName(_player.Gang) ?? string.Empty;

            // Stock sort: leader first, then lieutenants, then members — alphabetical within each rank.
            members = members
                .OrderByDescending(m => m.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(m => m.IsLieutenant)
                .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var m in members)
            {
                string fullName = string.IsNullOrWhiteSpace(m.LastName) ? m.Name : $"{m.Name} {m.LastName}";
                if (fullName.Length > 21)
                    fullName = fullName[..21];

                string race = _world.Database.Races.TryGetValue(m.RaceId, out var raceObj) ? raceObj.Name : "Unknown";
                string cls = _world.Database.Classes.TryGetValue(m.ClassId, out var classObj) ? classObj.Name : "Unknown";

                string rank = m.Name.Equals(leaderName, StringComparison.OrdinalIgnoreCase)
                    ? "Leader"
                    : m.IsLieutenant ? "Lieutenant" : "Member";

                string status = onlineNames.Contains(m.Name) ? "Online" : "Offline";
                await _client.SendLineAsync($"{fullName,-21} {m.Level,2} {race} {cls} - {status} [{rank}]");
            }
            return;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return;

        var gangLine = $"{MudAnsi.Green}{_player.Name} gangpaths:{MudAnsi.Reset}{MudAnsi.Yellow} {message}{MudAnsi.Reset}";
        await _client.SendLineAsync(gangLine);

        foreach (var member in _world.GetAllOnlinePlayers().Where(p =>
                     !p.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase) &&
                     p.Gang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase)))
        {
            _world.SendToPlayer(member.Name, gangLine, reprompt: true, prependLineBreak: true);
        }
    }

    private async Task HandleBroadcastChannelMessage(string message)
    {
        if (_player.BroadcastChannel <= 0)
        {
            await _client.SendLineAsync("You are not on a broadcast channel.");
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            await ShowBroadcastChannelRoster(_player.BroadcastChannel);
            return;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return;

        string channelLine = $"{MudAnsi.BrightYellow}Broadcast from {_player.Name} \"{message}\"{MudAnsi.Reset}";
        await _client.SendLineAsync(channelLine);
        _world.BroadcastToPlayerChannel(_player.BroadcastChannel, _player.Name, channelLine, reprompt: true, prependLineBreak: true);
    }

    // The single argument PROMOTE/DEMOTE accept, or "" when the line carried none or carried more than one.
    //
    // PROMOTE and DEMOTE both hang their entire body off a two-token form and
    // then use that single argument: PROMOTE has no MEMBER keyword, unlike INVITE/UNINVITE which
    // really do test the argument against "member". A gang leader who carried that habit
    // over and typed "promote member Jerk" sent three tokens, and we glued the lot into one name, hunted
    // for a player literally called "member Jerk", missed, and blamed them for not being in the gang.
    //
    // Stock answers a 3-token PROMOTE by falling out of both margc arms and printing nothing whatsoever.
    // We print the Syntax line instead — the same line stock itself gives every other unusable PROMOTE
    // argument (no argument at all, or a name that resolves to nobody). Silence there is an argc
    // fall-through, not a message stock chose to show.
    private static bool TryGetSingleGangRankArgument(string args, out string targetName)
    {
        var tokens = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        targetName = tokens.Length == 1 ? tokens[0] : string.Empty;
        return tokens.Length == 1;
    }

    private async Task HandlePromote(string args)
    {
        if (!TryGetSingleGangRankArgument(args, out string targetName))
        {
            await _client.SendLineAsync("Syntax: PROMOTE {user name}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are not in a gang!");
            return;
        }

        if (!_world.PlayerRepo.IsGangLeader(_player.Name, _player.Gang))
        {
            await _client.SendLineAsync("You are not the leader of the gang; You may not promote members!");
            return;
        }

        // Online first (realm-wide resolution rather than room-scoped),
        // then the user file. The two paths word their rejections differently and must not be merged: an
        // unknown name is a SYNTAX error to stock, an offline stranger gets an invitation hint, and only
        // someone actually standing in another gang gets told so. We used to reach all three by watching
        // SetGangLieutenant fail, which cannot tell "no such player" from "wrong gang" — that is what put
        // "You may not promote somebody who is not in your gang!." in front of a leader whose only mistake
        // was an extra word. (The stray "!." is stock: byte-exact.)
        var targetOnline = _world.FindOnlinePlayer(targetName);
        if (targetOnline != null)
        {
            if (targetOnline.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("You may not demote yourself to lieutenant. Your gang needs a leader!");
                return;
            }

            if (!targetOnline.Gang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("You may not promote somebody who is not in your gang!.");
                return;
            }

            if (_world.PlayerRepo.IsGangLeader(targetOnline.Name, _player.Gang))
            {
                await _client.SendLineAsync("You may not promote your gang leader!");
                return;
            }

            if (_world.PlayerRepo.IsGangLieutenant(targetOnline.Name, _player.Gang))
            {
                await _client.SendLineAsync($"Gang member {targetOnline.Name} is already a lieutenant in your gang.");
                return;
            }

            _world.PlayerRepo.SetGangLieutenant(targetOnline.Name, _player.Gang, true);
            targetOnline.IsGangLieutenant = true;
            await _client.SendLineAsync($"Gang member {targetOnline.Name} has been notified of their promotion.");
            _world.SendToPlayer(targetOnline.Name, "Your gang leader has promoted you to the rank of lieutenant.", reprompt: true, prependLineBreak: true);
            return;
        }

        var stored = _world.PlayerRepo.GetPlayerNameAndGang(targetName);
        if (stored == null)
        {
            await _client.SendLineAsync("Syntax: PROMOTE {user name}");
            return;
        }

        (string canonicalTarget, string targetGang) = stored.Value;

        if (!targetGang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync($"Perhaps you should invite {canonicalTarget} into your gang first.");
            return;
        }

        if (_world.PlayerRepo.IsGangLeader(canonicalTarget, _player.Gang))
        {
            await _client.SendLineAsync("You may not promote your gang leader!");
            return;
        }

        if (_world.PlayerRepo.IsGangLieutenant(canonicalTarget, _player.Gang))
        {
            await _client.SendLineAsync($"Gang member {canonicalTarget} is already a lieutenant in your gang.");
            return;
        }

        _world.PlayerRepo.SetGangLieutenant(canonicalTarget, _player.Gang, true);
        await _client.SendLineAsync($"Gang member {canonicalTarget} will be notified of their promotion next time they log on.");
    }

    // Same shape as HandlePromote, and it carried the same two defects. Note stock words the wrong-gang
    // rejection differently on each path — "someone ... gang!" for a target who is online,
    // "somebody ... gang." for one who is not — where we reached both through
    // SetGangLieutenant's return value and so could print either for either reason.
    private async Task HandleDemote(string args)
    {
        if (!TryGetSingleGangRankArgument(args, out string targetName))
        {
            await _client.SendLineAsync("Syntax: DEMOTE {user name}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_player.Gang))
        {
            await _client.SendLineAsync("You are not in a gang!");
            return;
        }

        if (!_world.PlayerRepo.IsGangLeader(_player.Name, _player.Gang))
        {
            await _client.SendLineAsync("You are not the leader of the gang; You may not demote members!");
            return;
        }

        var targetOnline = _world.FindOnlinePlayer(targetName);
        if (targetOnline != null)
        {
            if (targetOnline.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("You wish to demote yourself from leader of your gang?");
                return;
            }

            if (!targetOnline.Gang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("You may not demote someone who is not in your gang!");
                return;
            }

            if (!_world.PlayerRepo.IsGangLieutenant(targetOnline.Name, _player.Gang))
            {
                await _client.SendLineAsync($"Gang member {targetOnline.Name} is not a lieutenant in your gang.");
                return;
            }

            _world.PlayerRepo.SetGangLieutenant(targetOnline.Name, _player.Gang, false);
            targetOnline.IsGangLieutenant = false;
            await _client.SendLineAsync($"Gang member {targetOnline.Name} has been notified of their demotion.");
            _world.SendToPlayer(targetOnline.Name, "Your gang leader has demoted you.", reprompt: true, prependLineBreak: true);
            return;
        }

        var stored = _world.PlayerRepo.GetPlayerNameAndGang(targetName);
        if (stored == null)
        {
            await _client.SendLineAsync("Syntax: DEMOTE {user name}");
            return;
        }

        (string canonicalTarget, string targetGang) = stored.Value;

        if (!targetGang.Equals(_player.Gang, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("You may not demote somebody who is not in your gang.");
            return;
        }

        if (!_world.PlayerRepo.IsGangLieutenant(canonicalTarget, _player.Gang))
        {
            await _client.SendLineAsync($"Gang member {canonicalTarget} is not a lieutenant in your gang.");
            return;
        }

        _world.PlayerRepo.SetGangLieutenant(canonicalTarget, _player.Gang, false);
        await _client.SendLineAsync($"Gang member {canonicalTarget} will be notified of their demotion next time they log on.");
    }

    private static bool IsValidGangName(string gangName, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(gangName))
        {
            reason = "Syntax: CREATE GANG <gangname>";
            return false;
        }

        string trimmed = gangName.Trim();
        if (trimmed.Length > 19)
        {
            reason = $"The name you have chosen is too LONG: {trimmed}";
            return false;
        }

        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            reason = "You may not use 'None' as a gang name.";
            return false;
        }

        foreach (char c in trimmed)
        {
            if (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '\'')
                continue;

            reason = "You have specified an invalid character in your gang name.";
            return false;
        }

        return true;
    }

    private async Task HandleGossip(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _player.ReceiveGossipEnabled = !_player.ReceiveGossipEnabled;
            await _client.SendLineAsync(_player.ReceiveGossipEnabled
                ? "You will now receive gossip messages."
                : "You will no longer receive gossip messages.");
            return;
        }

        await HandleRealmBroadcastChannel(message, "Gossip what?", "gossips", "gossip", recipient => recipient.ReceiveGossipEnabled);
    }

    private async Task HandleAuction(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _player.ReceiveAuctionEnabled = !_player.ReceiveAuctionEnabled;
            await _client.SendLineAsync(_player.ReceiveAuctionEnabled
                ? "You will now receive auction messages."
                : "You will no longer receive auction messages.");
            return;
        }

        await HandleRealmBroadcastChannel(message, "Auction what?", "auctions", "auction", recipient => recipient.ReceiveAuctionEnabled);
    }

    private async Task HandleRealmBroadcastChannel(string message, string emptyPrompt, string verb, string channel, Func<Player, bool>? canReceive = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            await _client.SendLineAsync(emptyPrompt);
            return;
        }

        if (!await TryConsumeCommunicationAllowanceAsync())
            return;

        var channelLine = $"{MudAnsi.White}{_player.Name} {verb}: {MudAnsi.Magenta}{message}{MudAnsi.Reset}";

        await _client.SendLineAsync(channelLine);
        _world.BroadcastChannelToRealm(_player.Name, channelLine, except: _client, reprompt: true, prependLineBreak: true, canReceive: canReceive);

        PersistChannelMessage(channel, message);
    }

    // Persist the raw broadcast to the size-capped GossipLog for the web explorer. Best-effort and
    // off the game path: fire-and-forget so a DB hiccup never blocks the round or affects gameplay.
    private void PersistChannelMessage(string channel, string message)
    {
        var sender = _player.Name;
        _ = Task.Run(() =>
        {
            try { _world.PlayerRepo.AppendChannelMessage(sender, channel, message); }
            catch { /* logging is non-critical; never surface to the player */ }
        });
    }

}
