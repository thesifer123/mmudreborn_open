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
    // A tester sysop (IsTesterSysop, not a full IsSysop) may use only this small testing subset of the
    // SYSOP sub-commands. GOD is further restricted inside HandleSysopGod (IsTesterAllowedGodCommand).
    private static readonly HashSet<string> TesterSysopCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "map", "god", "list", "goto", "go", "giveitem", "destroyitm", "quest",
    };

    private async Task HandleSysop(string args)
    {
        if (!_player.IsSysop && !_player.IsTesterSysop)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Please ask your sysop for access.{MudAnsi.Reset}");
            return;
        }

        // A tester sysop is a limited operator; full sysops are never gated by the tester checks.
        bool testerOnly = !_player.IsSysop && _player.IsTesterSysop;

        if (string.IsNullOrWhiteSpace(args))
        {
            if (testerOnly)
            {
                await SendTesterSysopHelp();
                return;
            }

            await _client.SendLineAsync("Available SYSOP sub-commands (all preceded by SYSOP):");
            await _client.SendLineAsync("  BUFFERS   -- Statistics on the memory buffers used [Clear/Save]");
            await _client.SendLineAsync("  STATUS    -- View the status of another user or room [SYS STAT ROOM n]");
            await _client.SendLineAsync("  DIAG      -- View slow timer diagnostics [ON/OFF/CONSOLE/THRESHOLD/CLEAR]");
            await _client.SendLineAsync("  MAP       -- Display a generated map of this area");
            await _client.SendLineAsync("  GOD       -- Modify certain parameters of other players");
            await _client.SendLineAsync("  LIST      -- List things such as USERS");
            await _client.SendLineAsync("  REPORT    -- Various Game reports");
            await _client.SendLineAsync("  LIGHTNING -- Force lightning to strike a user");
            await _client.SendLineAsync("  JAIL      -- Haul a named user off to jail [SYSOP JAIL <user>]");
            await _client.SendLineAsync("  PARDON    -- Release a jailed user to the slums [SYSOP PARDON <user>]");
            await _client.SendLineAsync("  GOTO      -- Teleport yourself to a named location or <player>");
            await _client.SendLineAsync("  SPAWN     -- Spawn a monster into your current room");
            await _client.SendLineAsync("  FIRSTDROP -- Inspect or re-arm the guaranteed first-kill drop [LIST/STATUS/RESET]");
            await _client.SendLineAsync("  RELOAD    -- Reload settings from the board configuration");
            await _client.SendLineAsync("  INITIALIZE-- Re-initialize mmudreborn buffers");
            await _client.SendLineAsync("  CONFIGURE -- Configure runtime parameters");
            await _client.SendLineAsync("  PASSWORD  -- Use ;password for BBS account password resets");
            await _client.SendLineAsync("  ACCESS    -- Grant or revoke MMUDREBORN game sysop access");
            await _client.SendLineAsync("  ACCOUNT   -- View or update MMUDREBORN player account flags");
            await _client.SendLineAsync("  RESET     -- Reset room or area monster generation");
            await _client.SendLineAsync("  BOARD     -- Board admin; BOARD RESET CONFIRM wipes the ENTIRE realm fresh");
            await _client.SendLineAsync("  DISBAND   -- Disband a gang");
            await _client.SendLineAsync("  DISABLE   -- Remove a gang from the topten gangs");
            await _client.SendLineAsync("  ENABLE    -- Allow a gang back onto the topten gangs");
            await _client.SendLineAsync("  ACTION    -- Add, order, inspect, or delete social action verbs");
            await _client.SendLineAsync("  GANGSIZE  -- Adjust gang size");
            await _client.SendLineAsync("  RENAME    -- Rename a gang");
            await _client.SendLineAsync("  ARENA     -- Change the status of the arenas");
            await _client.SendLineAsync("  INVIS     -- Toggle sysop invisibility [FULL/NOAGGRO/OFF]");
            await _client.SendLineAsync("  SETLEVEL  -- Set your level [n] (recalculates stats/CP/HP)");
            await _client.SendLineAsync("  SETRACE   -- Set your race [name or number]");
            await _client.SendLineAsync("  SETCLASS  -- Set your class [name or number]");
            await _client.SendLineAsync("  HEAL      -- Fully restore your HP and mana");
            await _client.SendLineAsync("  GIVEITEM  -- Give yourself an item by name or number");
            await _client.SendLineAsync("  DESTROYITM-- Destroy a carried item by name or number (bypasses NotDroppable)");
            await _client.SendLineAsync("  GRANTSPELL-- Learn a spell by name or number (checks class/level usability)");
            await _client.SendLineAsync("  GRANTABIL -- Grant a reward abil to player [<player> <id> [value] [force]]");
            await _client.SendLineAsync("  QUEST     -- View quests [<player>|<player> <quest>|<player> <quest> <stage>]");
            await _client.SendLineAsync("  CLEANUP   -- Run daily maint now (recharge items, floor loot, Del@Maint)");
            await _client.SendLineAsync("  RELOADDATA-- Reload game data from the database live (no disconnects)");
            return;
        }

        var rest = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var sub = rest[0].ToLowerInvariant();
        var subArgs = rest.Length > 1 ? rest[1] : string.Empty;

        if (sub == "stat")
            sub = "status";

        // Stock text uses this format; keep for server-side audit tracing.
        Console.WriteLine($"Sysop Command by {_player.Name}: {FormatSysopAuditCommand(args, sub, subArgs)}");

        if (testerOnly && !TesterSysopCommands.Contains(sub))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}SYSOP {sub.ToUpperInvariant()} is not available to tester sysops.{MudAnsi.Reset}");
            return;
        }

        // Read-only phase 1 hooks.
        switch (sub)
        {
            case "buffers":
                await HandleSysopBuffers(subArgs);
                return;

            case "status":
                await HandleSysopStatus(subArgs);
                return;

            case "diag":
            case "diagnostics":
                await HandleSysopDiagnostics(subArgs);
                return;

            case "simulate":
            case "sim":
                await HandleSysopSimulate(subArgs);
                return;

            case "go":
            case "goto":
                await HandleSysopGoto(subArgs);
                return;

            case "spawn":
                await HandleSysopSpawn(subArgs);
                return;

            case "firstdrop":
                await HandleSysopFirstDrop(subArgs);
                return;

            case "god":
                await HandleSysopGod(subArgs);
                return;

            case "report":
                await HandleSysopReport(subArgs);
                return;

            case "list":
                await HandleSysopList(subArgs);
                return;

            case "map":
                await HandleSysopMap(subArgs);
                return;

            case "lightning":
                await HandleSysopLightning(subArgs);
                return;

            case "jail":
                await HandleSysopJail(subArgs);
                return;

            case "pardon":
                await HandleSysopPardon(subArgs);
                return;

            case "reload":
                await HandleSysopReload();
                return;

            case "initialize":
                await HandleSysopInitialize(subArgs);
                return;

            case "configure":
                await HandleSysopConfigure(subArgs);
                return;

            case "password":
                await HandleSysopPassword(subArgs);
                return;

            case "access":
                await HandleSysopAccess(subArgs);
                return;

            case "account":
                await HandleSysopAccount(subArgs);
                return;

            case "reset":
                await HandleSysopReset(subArgs);
                return;

            case "board":
                await HandleSysopBoard(subArgs);
                return;

            case "disband":
                await HandleSysopDisband(subArgs);
                return;

            case "disable":
                await HandleSysopDisableGang(subArgs);
                return;

            case "enable":
                await HandleSysopEnableGang(subArgs);
                return;

            case "action":
                await HandleSysopAction(subArgs);
                return;

            case "gangsize":
                await HandleSysopGangSize(subArgs);
                return;

            case "rename":
                await HandleSysopRenameGang(subArgs);
                return;

            case "arena":
                await HandleSysopArena(subArgs);
                return;

            case "invis":
            case "invisible":
                await HandleSysopInvisible(subArgs);
                return;

            case "setlevel":
                await HandleSysopSetLevel(subArgs);
                return;

            case "setrace":
                await HandleSysopSetRace(subArgs);
                return;

            case "setclass":
                await HandleSysopSetClass(subArgs);
                return;

            case "heal":
                await HandleSysopHeal(subArgs);
                return;

            case "giveitem":
                await SysopGiveItem(_player, subArgs);
                return;

            case "destroyitm":
                await SysopDestroyItem(subArgs);
                return;

            case "grantspell":
                await SysopGrantSpell(_player, subArgs);
                return;

            case "grantability":
            case "grantabil":
                await HandleSysopGrantAbility(subArgs);
                return;

            case "quest":
                await HandleSysopQuest(subArgs);
                return;

            case "cleanup":
                _world.RunDailyCleanup();
                await _client.SendLineAsync("Manual cleanup complete.");
                return;

            case "reloaddata":
                await HandleSysopReloadData();
                return;

            default:
                await _client.SendLineAsync($"SYSOP {sub.ToUpperInvariant()} is not implemented yet.");
                return;
        }
    }

    private async Task SendTesterSysopHelp()
    {
        await _client.SendLineAsync("Available SYSOP sub-commands (all preceded by SYSOP):");
        await _client.SendLineAsync("  MAP       -- Display a generated map of this area");
        await _client.SendLineAsync("  GOD       -- Modify certain parameters of other players");
        await _client.SendLineAsync("  LIST      -- List things such as USERS");
        await _client.SendLineAsync("  GOTO      -- Teleport yourself to a named location or <player>");
        await _client.SendLineAsync("  GIVEITEM  -- Give yourself an item by name or number");
        await _client.SendLineAsync("  QUEST     -- View quests [<player> | <player> <quest> | <player> <quest> <stage>]");
    }

    // Live, in-process game-data reload — picks up edits in PostgreSQL game_data (messages, items,
    // spells, monsters, room exits) without disconnecting anyone. For code changes or structural room
    // changes use SYSOP RESTART.
    private async Task HandleSysopReloadData()
    {
        bool reloaded = _world.ReloadGameData();
        await _client.SendLineAsync(reloaded
            ? "Game data reloaded from the database."
            : "Live data reload is not available in this host; use SYSOP RESTART.");
    }

    // SYSOP BOARD RESET CONFIRM — full realm wipe. FULL sysops only (never tester sysops); wipes every
    // player character (with carried items + bank), pending rerolls, gangs, gang housing, ground loot, and
    // the hall of fame, then respawns monsters fresh. Everyone online — including the invoking sysop — is
    // disconnected. Requires the explicit CONFIRM token so it can't fire by accident.
    private async Task HandleSysopBoard(string args)
    {
        if (!_player.IsSysop)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}SYSOP BOARD is only available to full sysops.{MudAnsi.Reset}");
            return;
        }

        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Syntax: SYSOP BOARD RESET CONFIRM");
            return;
        }

        bool confirmed = parts.Length >= 2 && parts[1].Equals("confirm", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}WARNING: SYSOP BOARD RESET wipes the ENTIRE realm -- every player character with their items and bank, all ground loot, gang housing, gangs, pending rerolls, and the hall of fame -- and respawns all monsters fresh. This CANNOT be undone.{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightYellow}Type  SYSOP BOARD RESET CONFIRM  to proceed. Everyone online, including you, will be disconnected.{MudAnsi.Reset}");
            return;
        }

        Console.WriteLine($"[SYSOP BOARD RESET] Full realm wipe invoked by {_player.Name}.");

        _world.BroadcastToRealm(
            $"{MudAnsi.BrightRed}** A sysop is resetting the Realm. Everyone is being disconnected. **{MudAnsi.Reset}",
            reprompt: true);

        // Kick every OTHER online player: remove them from the world (no save) FIRST, then drop the socket.
        // Removing before the disconnect means the session-teardown save is skipped (the offline guard in
        // GameSession), so nothing re-persists a character we are about to wipe.
        foreach (var other in _world.GetAllOnlinePlayers()
                     .Where(p => !p.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            var otherClient = other.Client;
            if (otherClient != null)
            {
                try
                {
                    await otherClient.EnsureNewLineAsync();
                    await otherClient.SendLineAsync($"{MudAnsi.BrightRed}The Realm has been reset. You have been disconnected.{MudAnsi.Reset}");
                }
                catch { /* best effort; the wipe proceeds regardless */ }
            }

            _world.RemovePlayer(other, save: false);
            // Unbind the character from its connection before dropping the socket, exactly as we do for
            // our own client below. RemovePlayer only clears player.Client (character -> connection); the
            // connection kept its .Player back-reference, so the host's dropped-connection teardown still
            // found a character to save and re-inserted the row this wipe is about to delete.
            if (otherClient != null)
                otherClient.Player = null;
            otherClient?.Disconnect();
        }

        // Our own character is wiped too. Detach it from the connection and skip the session's post-command
        // save (ReturningToMenu) so it isn't re-persisted; ResetRealmToFreshState removes us from the world.
        _client.Player = null;
        ReturningToMenu = true;

        _world.ResetRealmToFreshState();

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}The Realm has been reset to a fresh start -- all players, items, housing, gangs, ground loot, pending rerolls, and the hall of fame cleared; monsters respawned.{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.BrightYellow}Returning you to the menu to create a new character.{MudAnsi.Reset}");
    }

    // NOTE: In-realm SYSOP RESTART / SHUTDOWN were retired in favour of the BBS-front `;restart`, which
    // warns EVERY realm (and everyone on the menu) before relaunching the whole board. The front owns that
    // flow because only it can see the whole topology. See CWGamingServ BbsCommandDispatcher / FrontBoardController.
    private async Task HandleSysopAction(string args)
    {
        var parts = args.Trim().Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        string subcommand = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

        if (string.IsNullOrEmpty(subcommand))
        {
            await SendSysopActionHelp();
            return;
        }

        if (subcommand.Length >= 2 && "list".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            await HandleSysopActionList();
            return;
        }

        if (subcommand.Length >= 2 && "show".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2)
            {
                await _client.SendLineAsync("Syntax: SYSOP ACTION SHOW <verb>");
                return;
            }

            await HandleSysopActionShow(parts[1]);
            return;
        }

        if (subcommand.Length >= 2 && "add".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 2)
            {
                await _client.SendLineAsync("Syntax: SYSOP ACTION ADD <verb> [display-order]");
                return;
            }

            await HandleSysopActionAdd(parts[1], parts.Length > 2 ? parts[2] : string.Empty);
            return;
        }

        if (subcommand.Length >= 2 && "set".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 4)
            {
                await _client.SendLineAsync("Syntax: SYSOP ACTION SET <verb> <field> <message>");
                return;
            }

            await HandleSysopActionSet(parts[1], parts[2], parts[3]);
            return;
        }

        if (subcommand.Length >= 3 && "clear".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                await _client.SendLineAsync("Syntax: SYSOP ACTION CLEAR <verb> <field>");
                return;
            }

            await HandleSysopActionClear(parts[1], parts[2]);
            return;
        }

        if (subcommand.Length >= 2 && "order".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length < 3)
            {
                await _client.SendLineAsync("Syntax: SYSOP ACTION ORDER <verb> <display-order>");
                return;
            }

            await HandleSysopActionOrder(parts[1], parts[2]);
            return;
        }

        if ((subcommand.Length >= 3 && "delete".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase))
            || (subcommand.Length >= 3 && "remove".StartsWith(subcommand, StringComparison.OrdinalIgnoreCase)))
        {
            if (parts.Length < 2)
            {
                await _client.SendLineAsync("Syntax: SYSOP ACTION DELETE <verb>");
                return;
            }

            await HandleSysopActionDelete(parts[1]);
            return;
        }

        await SendSysopActionHelp();
    }

    private async Task SendSysopActionHelp()
    {
        await _client.SendLineAsync("Syntax: SYSOP ACTION LIST");
        await _client.SendLineAsync("Syntax: SYSOP ACTION SHOW <verb>");
        await _client.SendLineAsync("Syntax: SYSOP ACTION ADD <verb> [display-order]");
        await _client.SendLineAsync("Syntax: SYSOP ACTION SET <verb> <field> <message>");
        await _client.SendLineAsync("Syntax: SYSOP ACTION CLEAR <verb> <field>");
        await _client.SendLineAsync("Syntax: SYSOP ACTION ORDER <verb> <display-order>");
        await _client.SendLineAsync("Syntax: SYSOP ACTION DELETE <verb>");
        await _client.SendLineAsync("Fields: SELF, ROOM, PLAYER-SELF, PLAYER-TARGET, PLAYER-ROOM,");
        await _client.SendLineAsync("        MONSTER-SELF, MONSTER-ROOM, INVENTORY-SELF,");
        await _client.SendLineAsync("        INVENTORY-ROOM, FLOOR-SELF, FLOOR-ROOM");
        await _client.SendLineAsync("ANSI tokens: {MudAnsi.Green}, {MudAnsi.BrightWhite}, {MudAnsi.Reset}, {MudAnsi.LinePreamble}, etc.");
        await _client.SendLineAsync("Placeholders: %s and %d are preserved exactly as stored.");
    }

    private async Task HandleSysopActionList()
    {
        if (_world.Database.OrderedActions.Count == 0)
        {
            await _client.SendLineAsync("There are no actions loaded.");
            return;
        }

        await _client.SendLineAsync("[SYSOP ACTION LIST]");
        foreach (var action in _world.Database.OrderedActions)
        {
            string orderLabel = action.DisplayOrder.HasValue
                ? action.DisplayOrder.Value.ToString(CultureInfo.InvariantCulture).PadLeft(3)
                : " --";
            await _client.SendLineAsync($"  {orderLabel} {action.Name}");
        }
    }

    private async Task HandleSysopActionShow(string rawName)
    {
        if (!TryNormalizeSysopActionName(rawName, out string actionName))
        {
            await _client.SendLineAsync("Action names must start with a letter and use only letters, numbers, '-' or '_' characters.");
            return;
        }

        if (!_world.Database.Actions.TryGetValue(actionName, out var action))
        {
            await _client.SendLineAsync($"Action {actionName} was not found.");
            return;
        }

        string orderLabel = action.DisplayOrder.HasValue
            ? $"display order {action.DisplayOrder.Value.ToString(CultureInfo.InvariantCulture)}"
            : "hidden from ACTION LIST/HELP ACTIONS";
        await _client.SendLineAsync($"[SYSOP ACTION] {action.Name} ({orderLabel})");

        foreach (var field in SysopActionFields)
        {
            string value = field.GetValue(action);
            if (!string.IsNullOrWhiteSpace(value))
            {
                string rendered = ResolveAnsiTokens(value);
                await _client.SendLineAsync($"  {field.CanonicalName,-15}: {rendered}");
            }
        }
    }

    private async Task HandleSysopActionAdd(string rawName, string rawDisplayOrder)
    {
        if (!TryNormalizeSysopActionName(rawName, out string actionName))
        {
            await _client.SendLineAsync("Action names must start with a letter and use only letters, numbers, '-' or '_' characters.");
            return;
        }

        if (_world.Database.Actions.ContainsKey(actionName))
        {
            await _client.SendLineAsync($"Action {actionName} already exists.");
            return;
        }

        if (!TryParseActionDisplayOrder(rawDisplayOrder, out int? displayOrder))
        {
            await _client.SendLineAsync("Display order must be a positive number.");
            return;
        }

        _world.Database.SaveAction(new SocialAction
        {
            Name = actionName,
            DisplayOrder = displayOrder,
        });

        var saved = _world.Database.Actions[actionName];
        await _client.SendLineAsync($"Action {saved.Name} added at display order {saved.DisplayOrder?.ToString(CultureInfo.InvariantCulture) ?? "hidden"}.");
    }

    private async Task HandleSysopActionSet(string rawName, string rawFieldName, string message)
    {
        if (!TryNormalizeSysopActionName(rawName, out string actionName))
        {
            await _client.SendLineAsync("Action names must start with a letter and use only letters, numbers, '-' or '_' characters.");
            return;
        }

        if (!_world.Database.Actions.TryGetValue(actionName, out var existingAction))
        {
            await _client.SendLineAsync($"Action {actionName} was not found. Use SYSOP ACTION ADD first.");
            return;
        }

        if (!TryGetSysopActionField(rawFieldName, out var field))
        {
            await _client.SendLineAsync($"Unknown action field {rawFieldName}.");
            await _client.SendLineAsync("Use SYSOP ACTION with no arguments to list valid field names.");
            return;
        }

        var updated = CloneSocialAction(existingAction);
        field.SetValue(updated, message.Trim());
        _world.Database.SaveAction(updated);
        await _client.SendLineAsync($"Action {actionName} field {field.CanonicalName} updated.");
    }

    private async Task HandleSysopActionClear(string rawName, string rawFieldName)
    {
        if (!TryNormalizeSysopActionName(rawName, out string actionName))
        {
            await _client.SendLineAsync("Action names must start with a letter and use only letters, numbers, '-' or '_' characters.");
            return;
        }

        if (!_world.Database.Actions.TryGetValue(actionName, out var existingAction))
        {
            await _client.SendLineAsync($"Action {actionName} was not found.");
            return;
        }

        if (!TryGetSysopActionField(rawFieldName, out var field))
        {
            await _client.SendLineAsync($"Unknown action field {rawFieldName}.");
            await _client.SendLineAsync("Use SYSOP ACTION with no arguments to list valid field names.");
            return;
        }

        var updated = CloneSocialAction(existingAction);
        field.SetValue(updated, string.Empty);
        _world.Database.SaveAction(updated);
        await _client.SendLineAsync($"Action {actionName} field {field.CanonicalName} cleared.");
    }

    private async Task HandleSysopActionOrder(string rawName, string rawDisplayOrder)
    {
        if (!TryNormalizeSysopActionName(rawName, out string actionName))
        {
            await _client.SendLineAsync("Action names must start with a letter and use only letters, numbers, '-' or '_' characters.");
            return;
        }

        if (!_world.Database.Actions.TryGetValue(actionName, out var existingAction))
        {
            await _client.SendLineAsync($"Action {actionName} was not found.");
            return;
        }

        if (!TryParseActionDisplayOrder(rawDisplayOrder, out int? displayOrder) || !displayOrder.HasValue)
        {
            await _client.SendLineAsync("Display order must be a positive number.");
            return;
        }

        var updated = CloneSocialAction(existingAction);
        updated.DisplayOrder = displayOrder.Value;
        _world.Database.SaveAction(updated);

        await _client.SendLineAsync($"Action {actionName} moved to display order {displayOrder.Value.ToString(CultureInfo.InvariantCulture)}.");
    }

    private async Task HandleSysopActionDelete(string rawName)
    {
        if (!TryNormalizeSysopActionName(rawName, out string actionName))
        {
            await _client.SendLineAsync("Action names must start with a letter and use only letters, numbers, '-' or '_' characters.");
            return;
        }

        if (!_world.Database.DeleteAction(actionName))
        {
            await _client.SendLineAsync($"Action {actionName} was not found.");
            return;
        }

        await _client.SendLineAsync($"Action {actionName} deleted.");
    }

    private static bool TryNormalizeSysopActionName(string rawName, out string normalizedName)
    {
        normalizedName = string.IsNullOrWhiteSpace(rawName)
            ? string.Empty
            : rawName.Trim().ToLowerInvariant();
        return ValidSysopActionNameRegex.IsMatch(normalizedName);
    }

    private static bool TryParseActionDisplayOrder(string rawDisplayOrder, out int? displayOrder)
    {
        displayOrder = null;
        if (string.IsNullOrWhiteSpace(rawDisplayOrder))
            return true;

        if (!int.TryParse(rawDisplayOrder, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDisplayOrder)
            || parsedDisplayOrder < 1)
        {
            return false;
        }

        displayOrder = parsedDisplayOrder;
        return true;
    }

    private static bool TryGetSysopActionField(string rawFieldName, out SysopActionFieldDefinition field)
    {
        return SysopActionFieldAliases.TryGetValue(rawFieldName.Trim(), out field!);
    }

    private static SocialAction CloneSocialAction(SocialAction action)
    {
        return new SocialAction
        {
            Name = action.Name,
            DisplayOrder = action.DisplayOrder,
            SingleToUser = action.SingleToUser,
            SingleToRoom = action.SingleToRoom,
            UserToUser = action.UserToUser,
            UserToOtherUser = action.UserToOtherUser,
            UserToRoom = action.UserToRoom,
            MonsterToUser = action.MonsterToUser,
            MonsterToRoom = action.MonsterToRoom,
            InventoryToUser = action.InventoryToUser,
            InventoryToRoom = action.InventoryToRoom,
            FloorItemToUser = action.FloorItemToUser,
            FloorItemToRoom = action.FloorItemToRoom,
        };
    }

    private static string FormatSysopAuditCommand(string originalArgs, string sub, string subArgs)
    {
        if (sub != "password")
            return originalArgs;

        var parts = subArgs.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => originalArgs,
            1 => $"password {parts[0]}",
            _ => $"password {parts[0]} <redacted>"
        };
    }

    private async Task HandleSysopPassword(string args)
    {
        await _client.SendLineAsync("BBS password resets are board-owned. Use ;password <user> <new-password>.");
    }

    private async Task HandleSysopAccess(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts.Length > 3)
        {
            await SendSysopAccessHelp();
            return;
        }

        string targetName;
        string stateToken;
        bool testerTier = false;

        if (parts.Length == 2)
        {
            targetName = Player.NormalizeNamePart(parts[0]);
            stateToken = parts[1];
        }
        else
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "game":
                    break;

                case "tester":
                    // SYSOP ACCESS TESTER <user> <ON|OFF> — grant the limited tester-sysop role.
                    testerTier = true;
                    break;

                case "bbs":
                case "board":
                case "both":
                    await _client.SendLineAsync($"BBS sysop access is board-owned. Use ;access {Player.NormalizeNamePart(parts[1])} {parts[2]}.");
                    return;

                default:
                    await SendSysopAccessHelp();
                    return;
            }

            targetName = Player.NormalizeNamePart(parts[1]);
            stateToken = parts[2];
        }

        if (!TryParseSysopAccessState(stateToken, out bool enabled))
        {
            await SendSysopAccessHelp();
            return;
        }

        Player? targetPlayer = _world.PlayerRepo.LoadPlayerByName(targetName);
        if (targetPlayer == null)
        {
            await _client.SendLineAsync($"Cannot find user {targetName}");
            return;
        }

        if (!enabled && !testerTier && targetPlayer.IsAllowed)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{targetPlayer.Name} is a SuperSysop. Their sysop access cannot be revoked.{MudAnsi.Reset}");
            return;
        }

        if (testerTier)
            targetPlayer.IsTesterSysop = enabled;
        else
            targetPlayer.IsSysop = enabled;
        _world.PlayerRepo.SavePlayer(targetPlayer);

        var onlinePlayer = _world.GetAllOnlinePlayers()
            .FirstOrDefault(player => player.Name.Equals(targetPlayer.Name, StringComparison.OrdinalIgnoreCase));
        if (onlinePlayer != null)
        {
            if (testerTier)
                onlinePlayer.IsTesterSysop = enabled;
            else
                onlinePlayer.IsSysop = enabled;
        }

        await _client.SendLineAsync(testerTier
            ? $"{(enabled ? "Granted" : "Revoked")} tester sysop access for {targetPlayer.Name}."
            : FormatSysopAccessResult(true, false, targetPlayer.Name, targetName, enabled));
    }

    private Task SendSysopAccessHelp()
    {
        return Task.WhenAll(
            _client.SendLineAsync("Syntax: SYSOP ACCESS [GAME] <user> <ON|OFF>"),
            _client.SendLineAsync("        SYSOP ACCESS TESTER <user> <ON|OFF>  (limited tester-sysop role)"),
            _client.SendLineAsync("Use ;access <user> <ON|OFF> for BBS sysop access."));
    }

    private static bool TryParseSysopAccessState(string value, out bool enabled)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "on":
            case "yes":
            case "true":
            case "grant":
            case "enable":
            case "1":
                enabled = true;
                return true;

            case "off":
            case "no":
            case "false":
            case "revoke":
            case "disable":
            case "0":
                enabled = false;
                return true;

            default:
                enabled = false;
                return false;
        }
    }

    private static string FormatSysopAccessResult(bool updateGame, bool updateBbs, string playerName, string bbsUserName, bool enabled)
    {
        string action = enabled ? "Granted" : "Revoked";

        if (updateGame && updateBbs)
            return $"{action} sysop access for player {playerName} and BBS user {bbsUserName}.";

        if (updateGame)
            return $"{action} game sysop access for {playerName}.";

        return $"{action} BBS sysop access for {bbsUserName}.";
    }

    // SYSOP GRANTABILITY <player> <abilityId> [value] [force] — grant a reward ability. Default-deny:
    // only QuestCatalog.GrantableAbilities are allowed unless FORCE is given; quest-progress flags are
    // routed to SYSOP QUEST. Presence-safe (value 0 abilities like Perfect Stealth #186 are stored).
    private async Task HandleSysopGrantAbility(string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out int abilityId))
        {
            await _client.SendLineAsync("Syntax: SYSOP GRANTABILITY <player> <abilityId> [value|remove] [force]");
            await _client.SendLineAsync("  e.g. SYSOP GRANTABILITY Duhh 186 0   (Perfect Stealth)");
            await _client.SendLineAsync("       SYSOP GRANTABILITY Duhh 186 remove   (revoke it)");
            return;
        }

        // REMOVE/REVOKE strips a granted reward ability (presence-safe value-0 abilities included).
        bool remove = parts.Skip(2).Any(p =>
            p.Equals("remove", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("revoke", StringComparison.OrdinalIgnoreCase));
        if (remove)
        {
            if (QuestCatalog.IsQuestFlag(abilityId))
            {
                await _client.SendLineAsync($"{AbilityNames.Format(abilityId)} is a quest-progress flag — use SYSOP QUEST <player> {abilityId} reset.");
                return;
            }

            string removeName = Player.NormalizeNamePart(parts[0]);
            await ApplyQuestAbilityMutationAsync(removeName,
                player => player.RemoveQuestAbility(abilityId),
                $"removed {AbilityNames.Format(abilityId)}");
            return;
        }

        bool force = parts.Skip(2).Any(p => p.Equals("force", StringComparison.OrdinalIgnoreCase));
        int value = parts.Length > 2 && int.TryParse(parts[2], out int parsedValue) ? parsedValue : 1;

        if (QuestCatalog.IsQuestFlag(abilityId))
        {
            await _client.SendLineAsync($"{AbilityNames.Format(abilityId)} is a quest-progress flag — use SYSOP QUEST to stage it.");
            return;
        }

        if (!QuestCatalog.IsGrantable(abilityId) && !force)
        {
            await _client.SendLineAsync($"{AbilityNames.Format(abilityId)} is not a grantable reward ability (intrinsic/derived).");
            await _client.SendLineAsync("Add FORCE to grant it anyway: SYSOP GRANTABILITY <player> <id> [value] FORCE");
            return;
        }

        string targetName = Player.NormalizeNamePart(parts[0]);
        await ApplyQuestAbilityMutationAsync(targetName,
            player => player.GrantQuestAbility(abilityId, value),
            $"granted {AbilityNames.Format(abilityId)} = {value}");
    }

    // SYSOP QUEST <player>                                   — show a player's staged-quest progress (read-only)
    // SYSOP QUEST <player> <quest>                           — show one quest's stage for a player (read-only)
    // SYSOP QUEST <player> <quest> <stage|complete|reset>    — set a staged quest's progress flag.
    // Flag-only (no reward abilities applied); atomic quests are complete/reset only.
    private async Task HandleSysopQuest(string args)
    {
        var parts = args.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await _client.SendLineAsync("Syntax: SYSOP QUEST <player> [<quest> [<stage|complete|reset>]]");
            await _client.SendLineAsync("  <player>                     -- show all staged-quest progress");
            await _client.SendLineAsync("  <player> <quest>             -- show one quest's stage");
            await _client.SendLineAsync("  <player> <quest> <stage>     -- set the stage (number, COMPLETE, or RESET)");
            await _client.SendLineAsync("  quest = flag number (e.g. 126) or name (good/neutral/evil/dragon/druid/dao/phoenix/...)");
            return;
        }

        // Read-only views: no stage argument means "show", not "set".
        if (parts.Length < 3)
        {
            await ShowQuestProgressAsync(Player.NormalizeNamePart(parts[0]), parts.Length == 2 ? parts[1] : null);
            return;
        }

        if (!QuestCatalog.TryResolve(parts[1], out int flag) || !QuestCatalog.TryGet(flag, out var quest))
        {
            await _client.SendLineAsync($"Unknown quest '{parts[1]}'. Use a flag number or a quest name.");
            return;
        }

        string stageToken = parts[2];
        int stage;
        if (stageToken.Equals("complete", StringComparison.OrdinalIgnoreCase) || stageToken.Equals("done", StringComparison.OrdinalIgnoreCase))
            stage = quest.CompleteValue;
        else if (stageToken.Equals("reset", StringComparison.OrdinalIgnoreCase) || stageToken.Equals("clear", StringComparison.OrdinalIgnoreCase))
            stage = 0;
        else if (!int.TryParse(stageToken, out stage))
        {
            await _client.SendLineAsync("Stage must be a number, COMPLETE, or RESET.");
            return;
        }

        int clamped = Math.Clamp(stage, 0, quest.CompleteValue);
        if (clamped != stage)
            await _client.SendLineAsync($"Stage {stage} out of range; clamped to {clamped} (max {quest.CompleteValue}).");
        stage = clamped;

        if (!quest.MultiStep && stage != 0 && stage != quest.CompleteValue)
            await _client.SendLineAsync($"{quest.Name} is a one-action quest with no intermediate steps; COMPLETE/RESET are the meaningful options.");

        string description = stage == 0
            ? $"{quest.Name}: cleared"
            : quest.MultiStep
                ? $"{quest.Name}: step {stage}/{quest.CompleteValue}"
                : $"{quest.Name}: complete";

        string targetName = Player.NormalizeNamePart(parts[0]);
        int targetStage = stage;
        await ApplyQuestAbilityMutationAsync(targetName, player =>
        {
            if (targetStage == 0)
                player.RemoveQuestAbility(flag);
            else
                player.GrantQuestAbility(flag, targetStage);
        }, description);
    }

    // Apply a QuestAbilities mutation to a target online OR offline, then recalc + persist. For an
    // online target the LIVE instance is mutated (a DB-only edit would be clobbered by the
    // save-after-every-command in GameSession); offline targets are loaded, mutated, and saved so the
    // change applies on next login. Mirrors the SYSOP ACCESS online/offline pattern.
    private async Task ApplyQuestAbilityMutationAsync(string targetName, Action<Player> mutate, string description)
    {
        var online = _world.GetAllOnlinePlayers()
            .FirstOrDefault(p => p.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        if (online != null)
        {
            mutate(online);
            if (_world.Database.Races.TryGetValue(online.RaceId, out var race) &&
                _world.Database.Classes.TryGetValue(online.ClassId, out var cls))
                online.RecalculateStats(race, cls, _world.Database);
            _world.PlayerRepo.SavePlayer(online);
            await _client.SendLineAsync($"{online.Name} (online): {description}.");
            return;
        }

        var target = _world.PlayerRepo.LoadPlayerByName(targetName);
        if (target == null)
        {
            await _client.SendLineAsync($"Cannot find user {targetName}");
            return;
        }

        mutate(target);
        _world.PlayerRepo.SavePlayer(target);
        await _client.SendLineAsync($"{target.Name} (offline): {description} (applies on next login).");
    }

    // Read-only view of a player's staged-quest progress. With no quest filter, lists every staged quest
    // the player has started; with a filter, shows that one quest (even if not started). Reads the LIVE
    // instance for an online player, otherwise loads from the repo without mutating/saving anything.
    private async Task ShowQuestProgressAsync(string targetName, string? questFilter)
    {
        var player = _world.GetAllOnlinePlayers()
            .FirstOrDefault(p => p.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase));
        bool online = player != null;
        player ??= _world.PlayerRepo.LoadPlayerByName(targetName);
        if (player == null)
        {
            await _client.SendLineAsync($"Cannot find user {targetName}");
            return;
        }

        string presence = online ? "online" : "offline";

        // Single-quest view.
        if (questFilter != null)
        {
            if (!QuestCatalog.TryResolve(questFilter, out int flag) || !QuestCatalog.TryGet(flag, out var quest))
            {
                await _client.SendLineAsync($"Unknown quest '{questFilter}'. Use a flag number or a quest name.");
                return;
            }
            await _client.SendLineAsync($"{player.Name} ({presence}) — {FormatQuestProgress(flag, quest, player.GetQuestAbilityValue(flag))}");
            return;
        }

        // Full view: every started staged quest.
        await _client.SendLineAsync($"{MudAnsi.BrightWhite}Staged-quest progress for {player.Name} ({presence}):{MudAnsi.Reset}");
        bool any = false;
        foreach (var (flag, quest) in QuestCatalog.All)
        {
            int value = player.GetQuestAbilityValue(flag);
            if (value <= 0)
                continue;
            any = true;
            await _client.SendLineAsync($"  {FormatQuestProgress(flag, quest, value)}");
        }
        if (!any)
            await _client.SendLineAsync("  (no staged quests started)");

        // Reward / quest-granted abilities (Perfect Stealth #186, Dodge, Spellcasting, …). These are
        // earned the same way — from a quest or quest item — but stored as one-shot reward abilities
        // rather than staged flags, so they don't appear above. Surface them here so a sysop sees the
        // player's full set of quest-derived abilities in one place. Learned-spellbook bookkeeping
        // markers are excluded (use the `spells` view for those).
        var rewardAbilities = player.QuestAbilities
            .Where(pair => pair.Key < Player.LearnedSpellbookAbilityBase)
            .Where(pair => !QuestCatalog.IsQuestFlag(pair.Key))
            .OrderBy(pair => pair.Key)
            .ToList();
        if (rewardAbilities.Count > 0)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightWhite}Reward abilities for {player.Name} ({presence}):{MudAnsi.Reset}");
            foreach (var (id, value) in rewardAbilities)
                await _client.SendLineAsync($"  {AbilityNames.Format(id)} {value}");
        }
    }

    private static string FormatQuestProgress(int flag, QuestCatalog.QuestInfo quest, int value)
    {
        // Atomic reward quests have no meaningful intermediate "step" to report: the player earns the
        // quest's reward in a single action (e.g. Adult Red Dragon — `touch`/`move`/`get ruby` grant
        // the exp and abilities at once). The stored flag value isn't partial progress, so report
        // binary complete / not started — never a misleading "in progress, stage N/M" — mirroring how
        // the player-facing `abil` view treats them.
        if (!quest.MultiStep)
        {
            string atomicState = value > 0 ? "complete" : "not started";
            return $"{quest.Name} ({flag}): {atomicState} [atomic]";
        }

        string state = value <= 0 ? "not started"
            : value >= quest.CompleteValue ? "COMPLETE"
            : "in progress";
        return $"{quest.Name} ({flag}): stage {value}/{quest.CompleteValue}  [{state}]";
    }

    private async Task HandleSysopAccount(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await SendSysopAccountHelp();
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
            case "?":
                await SendSysopAccountHelp();
                return;

            case "show":
                if (parts.Length != 2)
                {
                    await SendSysopAccountHelp();
                    return;
                }

                await HandleSysopAccountShow(Player.NormalizeNamePart(parts[1]));
                return;

            case "test":
                if (parts.Length != 3)
                {
                    await SendSysopAccountHelp();
                    return;
                }

                await HandleSysopAccountTest(Player.NormalizeNamePart(parts[1]), parts[2]);
                return;

            default:
                await SendSysopAccountHelp();
                return;
        }
    }

    private async Task SendSysopAccountHelp()
    {
        await _client.SendLineAsync("Syntax: SYSOP ACCOUNT SHOW <user>");
        await _client.SendLineAsync("Syntax: SYSOP ACCOUNT TEST <user> <ON|OFF>");
        await _client.SendLineAsync("Use ;account SHOW/TEST for BBS account details and flags.");
        await _client.SendLineAsync("Example: SYSOP ACCOUNT SHOW Scottt");
        await _client.SendLineAsync("Example: SYSOP ACCOUNT TEST Scottt OFF");
    }

    private async Task HandleSysopAccountShow(string targetName)
    {
        var player = _world.PlayerRepo.LoadPlayerByName(targetName);
        if (player == null)
        {
            await _client.SendLineAsync($"Cannot find player {targetName}. BBS account details are board-owned; use ;account show {targetName}.");
            return;
        }

        await _client.SendLineAsync($"[SYSOP ACCOUNT] {targetName}");
        await _client.SendLineAsync($"  Player      : {player.Name}");
        await _client.SendLineAsync($"  Player BBS  : {player.BbsUserId}");
        await _client.SendLineAsync($"  Game Sysop  : {FormatYesNo(player.IsSysop)}");
        await _client.SendLineAsync($"  Player Test : {FormatYesNo(player.IsTestAccount)}");
        await _client.SendLineAsync($"  BBS Account : Use ;account show {targetName}");
    }

    private async Task HandleSysopAccountTest(string targetName, string stateToken)
    {
        if (!TryParseSysopAccessState(stateToken, out bool isTestAccount))
        {
            await _client.SendLineAsync("Syntax: SYSOP ACCOUNT TEST <user> <ON|OFF>");
            return;
        }

        var player = _world.PlayerRepo.LoadPlayerByName(targetName);
        if (player == null)
        {
            await _client.SendLineAsync($"Cannot find player {targetName}. BBS account flags are board-owned; use ;account test {targetName} {stateToken.ToUpperInvariant()}.");
            return;
        }

        player.IsTestAccount = isTestAccount;
        _world.PlayerRepo.SavePlayer(player);

        var onlinePlayer = _world.GetAllOnlinePlayers()
            .FirstOrDefault(online => online.Name.Equals(player.Name, StringComparison.OrdinalIgnoreCase));
        if (onlinePlayer != null)
            onlinePlayer.IsTestAccount = isTestAccount;

        await _client.SendLineAsync($"{(isTestAccount ? "Enabled" : "Cleared")} test-account status for player {player.Name}.");
    }

    private static string FormatYesNo(bool? value)
    {
        return value.HasValue ? (value.Value ? "Yes" : "No") : "-";
    }

    private async Task HandleSysopDiagnostics(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0)
        {
            string sub = tokens[0].ToLowerInvariant();
            switch (sub)
            {
                case "on":
                    GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: true);
                    await _client.SendLineAsync("World tick diagnostics enabled.");
                    break;

                case "off":
                    GameDiagnostics.ConfigureWorldTickDiagnostics(enabled: false);
                    await _client.SendLineAsync("World tick diagnostics disabled.");
                    break;

                case "clear":
                    GameDiagnostics.ResetWorldTickDiagnostics();
                    await _client.SendLineAsync("World tick diagnostics cleared.");
                    break;

                case "console":
                    if (tokens.Length < 2 || !TryParseSysopAccessState(tokens[1], out bool consoleEnabled))
                    {
                        await SendSysopDiagnosticsHelp();
                        return;
                    }

                    GameDiagnostics.ConfigureWorldTickDiagnostics(consoleLoggingEnabled: consoleEnabled);
                    await _client.SendLineAsync($"World tick console logging {(consoleEnabled ? "enabled" : "disabled")}.");
                    break;

                case "threshold":
                case "thresh":
                    if (tokens.Length < 2 || !int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int thresholdMs))
                    {
                        await SendSysopDiagnosticsHelp();
                        return;
                    }

                    GameDiagnostics.ConfigureWorldTickDiagnostics(slowThresholdMs: thresholdMs);
                    await _client.SendLineAsync($"World tick slow threshold set to {GameDiagnostics.WorldTickSlowThresholdMs} ms.");
                    break;

                case "alert":
                case "alerts":
                    if (tokens.Length < 2 || !TryParseSysopAccessState(tokens[1], out bool alertsEnabled))
                    {
                        await SendSysopDiagnosticsHelp();
                        return;
                    }

                    _world.HealthAlertsEnabled = alertsEnabled;
                    await _client.SendLineAsync($"Proactive health alerts {(alertsEnabled ? "enabled" : "disabled")}.");
                    break;

                case "errors":
                case "error":
                case "err":
                    await SendSysopDiagnosticsErrors();
                    return;

                case "help":
                case "?":
                    await SendSysopDiagnosticsHelp();
                    return;

                default:
                    await SendSysopDiagnosticsHelp();
                    return;
            }
        }

        await SendSysopDiagnosticsSnapshot();
    }

    // Full stack traces for the recent swallowed background exceptions — the deep-dive when the one-line
    // summaries in SYSOP DIAG aren't enough to locate a "Collection was modified"/NRE-style fault.
    private async Task SendSysopDiagnosticsErrors()
    {
        var errors = GameDiagnostics.GetBackgroundExceptionsSnapshot();
        await _client.SendLineAsync("[SYSOP DIAG ERRORS]");
        if (errors.Length == 0)
        {
            await _client.SendLineAsync("  No background errors recorded since the last SYSOP DIAG CLEAR.");
            return;
        }

        foreach (var err in errors)
        {
            string time = err.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            await _client.SendLineAsync($"  {time} source={err.Source} {err.ExceptionType}: {err.Message}");
            foreach (var line in err.StackTrace.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (trimmed.Length > 0)
                    await _client.SendLineAsync($"    {trimmed}");
            }
        }
    }

    private Task SendSysopDiagnosticsHelp()
    {
        return Task.WhenAll(
            _client.SendLineAsync("Syntax: SYSOP DIAG"),
            _client.SendLineAsync("Syntax: SYSOP DIAG ON|OFF"),
            _client.SendLineAsync("Syntax: SYSOP DIAG CONSOLE ON|OFF"),
            _client.SendLineAsync("Syntax: SYSOP DIAG THRESHOLD <milliseconds>"),
            _client.SendLineAsync("Syntax: SYSOP DIAG ALERT ON|OFF"),
            _client.SendLineAsync("Syntax: SYSOP DIAG ERRORS"),
            _client.SendLineAsync("Syntax: SYSOP DIAG CLEAR"));
    }

    private async Task SendSysopDiagnosticsSnapshot()
    {
        var worldSnapshot = GameDiagnostics.GetWorldTickDiagnosticsSnapshot();
        var roomSpellSnapshot = GameDiagnostics.GetRoomSpellTickDiagnosticsSnapshot();
        var gateSnapshot = GameDiagnostics.GetGateHoldDiagnosticsSnapshot();
        await _client.SendLineAsync("[SYSOP DIAG]");
        await _client.SendLineAsync($"  Timer profiler: {(worldSnapshot.Enabled ? "ON" : "OFF")} threshold={worldSnapshot.SlowThresholdMs}ms console={(worldSnapshot.ConsoleLoggingEnabled ? "ON" : "OFF")} health-alerts={(_world.HealthAlertsEnabled ? "ON" : "OFF")}");
        await _client.SendLineAsync($"  World tick: total={worldSnapshot.TotalTicks} slow={worldSnapshot.SlowTicks} skipped={worldSnapshot.SkippedTicks} last={worldSnapshot.LastTickMs}ms max={worldSnapshot.MaxTickMs}ms");
        await _client.SendLineAsync($"  Room spell tick: total={roomSpellSnapshot.TotalTicks} slow={roomSpellSnapshot.SlowTicks} overlap={roomSpellSnapshot.OverlapTicks} last={roomSpellSnapshot.LastTickMs}ms max={roomSpellSnapshot.MaxTickMs}ms");
        // Gate hold = how long the global WorldStateGate was held per acquisition (source command/combat/
        // persist-flush). These should stay tiny; a slow sample is the smoking gun for blocking work that
        // slipped back under the gate. avg/max are across ALL holds since the last clear.
        await _client.SendLineAsync($"  Gate hold: total={gateSnapshot.TotalHolds} slow(>{gateSnapshot.SlowThresholdMs}ms)={gateSnapshot.SlowHolds} avg={gateSnapshot.AverageMs:F2}ms max={gateSnapshot.MaxMs:F2}ms");
        // Live snapshot of who holds the world lock at the instant DIAG ran — usually idle. Catching a
        // holder here (especially a long-held one) is a real-time view of what's blocking the world.
        var (gateHolder, gateHeldMs) = GameDiagnostics.GetCurrentGateHolder();
        await _client.SendLineAsync(gateHolder == null
            ? "  Gate now: idle"
            : $"  Gate now: held by {gateHolder} for {gateHeldMs:F2}ms");
        if (gateSnapshot.RecentSlowHolds.Length == 0)
        {
            await _client.SendLineAsync("  Recent slow gate holds: none");
        }
        else
        {
            await _client.SendLineAsync("  Recent slow gate holds:");
            foreach (var sample in gateSnapshot.RecentSlowHolds.TakeLast(8))
            {
                string time = sample.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                await _client.SendLineAsync($"    {time} source={sample.Source} {sample.Milliseconds:F2}ms");
            }
        }

        if (worldSnapshot.RecentSlowTicks.Length == 0)
        {
            await _client.SendLineAsync("  Recent slow world ticks: none");
        }
        else
        {
            await _client.SendLineAsync("  Recent slow world ticks:");
            foreach (var sample in worldSnapshot.RecentSlowTicks.TakeLast(8))
            {
                string time = sample.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                await _client.SendLineAsync($"    {time} {GameDiagnostics.FormatWorldTickSample(sample)}");
            }
        }

        if (roomSpellSnapshot.RecentSlowTicks.Length == 0)
        {
            await _client.SendLineAsync("  Recent slow room-spell ticks: none");
        }
        else
        {
            await _client.SendLineAsync("  Recent slow room-spell ticks:");
            foreach (var sample in roomSpellSnapshot.RecentSlowTicks.TakeLast(8))
            {
                string time = sample.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                await _client.SendLineAsync($"    {time} {GameDiagnostics.FormatRoomSpellTickSample(sample)}");
            }
        }

        await SendRoomSpellOverlapSummary();
        await SendBackgroundErrorSummary();
    }

    // Recent room-spell-tick overlaps with the gate holder each one queued behind. An overlap is benign
    // (serialized, nothing dropped); this list is here so a sysop can see the PATTERN — if they all sit
    // behind 'combat' or 'tick:slow', that names the contention source without needing console logging on.
    private async Task SendRoomSpellOverlapSummary()
    {
        var overlaps = GameDiagnostics.GetRecentRoomSpellOverlaps();
        if (overlaps.Length == 0)
        {
            await _client.SendLineAsync("  Recent room-spell overlaps: none");
            return;
        }

        await _client.SendLineAsync($"  Recent room-spell overlaps: {overlaps.Length} (benign unless sustained)");
        foreach (var sample in overlaps.TakeLast(8))
        {
            string time = sample.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            await _client.SendLineAsync($"    {time} concurrent={sample.ConcurrentTicks} behind={sample.Cause} ({sample.CauseHeldMs:F2}ms)");
        }
    }

    // One line per recent background fault (time/source/type/message). The full stack is one command
    // away via SYSOP DIAG ERRORS — this summary is what makes the [REALM HEALTH] alert actionable.
    private async Task SendBackgroundErrorSummary()
    {
        var errors = GameDiagnostics.GetBackgroundExceptionsSnapshot();
        if (errors.Length == 0)
        {
            await _client.SendLineAsync("  Background errors: none");
            return;
        }

        await _client.SendLineAsync($"  Background errors: {errors.Length} (full stacks: SYSOP DIAG ERRORS)");
        foreach (var err in errors.TakeLast(8))
        {
            string time = err.AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            string message = err.Message.Length > 80 ? err.Message[..80] : err.Message;
            await _client.SendLineAsync($"    {time} source={err.Source} {err.ExceptionType}: {message}");
        }
    }

    private async Task HandleSysopStatus(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            await HandleSysopStatusRoom(_player.CurrentRoomNumber, _player.CurrentMapNumber);
            return;
        }

        if (tokens[0].Equals("room", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Length < 2 || !int.TryParse(tokens[1], out var roomNumber))
            {
                await _client.SendLineAsync("Syntax: SYSOP STATUS ROOM <room> <optional map#>");
                return;
            }

            int mapNumber = _player.CurrentMapNumber;
            if (tokens.Length > 2 && !int.TryParse(tokens[2], out mapNumber))
            {
                await _client.SendLineAsync("Syntax: SYSOP STATUS ROOM <room> <optional map#>");
                return;
            }

            await HandleSysopStatusRoom(roomNumber, mapNumber);
            return;
        }

        var targetName = string.Join(' ', tokens);
        // Use the shared resolver so an exact name beats a prefix match (e.g. "duhh" → Duhh, not Duhhsucks).
        var target = _world.FindOnlinePlayer(targetName);

        if (target == null)
        {
            await _client.SendLineAsync($"Cannot find user {targetName}");
            return;
        }

        await _client.SendLineAsync($"Info: {target.Name}");
        await _client.SendLineAsync($"values: level={target.Level} exp={target.Experience} hp={target.CurrentHP}/{target.MaxHP} mana={target.CurrentMana}/{target.MaxMana}");
        await _client.SendLineAsync($"values: map={target.CurrentMapNumber} room={target.CurrentRoomNumber} align={target.Alignment} lives={target.Lives}");
        await _client.SendLineAsync($"values: flags sysop={(target.IsSysop ? 1 : 0)} talk={target.TalkMode} brief={(target.BriefMode ? 1 : 0)} combat={(target.InCombat ? 1 : 0)}");
    }

    private async Task HandleSysopStatusRoom(int roomNumber, int mapNumber)
    {
        var room = _world.GetRoom(mapNumber, roomNumber);
        if (room == null)
        {
            await _client.SendLineAsync($"Room {mapNumber}/{roomNumber} not found.");
            return;
        }

        var monsters = _world.GetMonstersInRoom(room.MapNumber, room.RoomNumber);
        var players = _world.GetPlayersInRoom(room.MapNumber, room.RoomNumber);

        await _client.SendLineAsync($"Info: {room.Name}");
        await _client.SendLineAsync($"values: map={room.MapNumber} room={room.RoomNumber} light={room.Light} monstertype={room.MonsterType} delay={room.Delay}");
        await _client.SendLineAsync($"values: players={players.Count} monsters={monsters.Count} npc={room.NPC} cmd={room.CMD} shop={room.Shop} spell={room.Spell}");
        await _client.SendLineAsync($"values: exits={_world.GetVisibleExitString(_player, room)}");
    }

    private async Task HandleSysopReport(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            await _client.SendLineAsync("Syntax: SYSOP REPORT USERS [mask]");
            await _client.SendLineAsync("Syntax: SYSOP REPORT NOTOPTEN");
            return;
        }

        var reportType = tokens[0].ToLowerInvariant();
        if (reportType == "users")
        {
            string mask = tokens.Length > 1 ? string.Join(' ', tokens.Skip(1)) : string.Empty;
            if (mask.Length > 19)
                mask = mask[..19];
            var rows = _world.PlayerRepo.GetPlayerDirectory(mask);

            await _client.SendLineAsync("[SYSOP REPORT USERS]");
            if (!string.IsNullOrWhiteSpace(mask))
                await _client.SendLineAsync($"Mask: {mask}");

            if (rows.Count == 0)
            {
                await _client.SendLineAsync("No users match the report criteria.");
                return;
            }

            foreach (var row in rows)
            {
                await _client.SendLineAsync($"  {row.Name,-12} {row.LastName,-16}");
            }

            await _client.SendLineAsync($"Total users listed: {rows.Count}");
            return;
        }

        if (reportType == "notopten")
        {
            var rows = _world.PlayerRepo.GetPlayersNotTopten();
            await _client.SendLineAsync("[SYSOP REPORT NOTOPTEN]");
            if (rows.Count == 0)
            {
                await _client.SendLineAsync("No users are currently marked as notopten.");
                return;
            }

            foreach (var row in rows)
                await _client.SendLineAsync($"  {row.Name,-12} {row.LastName,-16}");

            await _client.SendLineAsync($"Total users listed: {rows.Count}");
            return;
        }

        await _client.SendLineAsync("Syntax: SYSOP REPORT USERS [mask]");
        await _client.SendLineAsync("Syntax: SYSOP REPORT NOTOPTEN");
    }

    private async Task HandleSysopList(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            await SendSysopListHelp();
            return;
        }

        switch (tokens[0].ToLowerInvariant())
        {
            case "users":
            case "user":
                await _client.SendLineAsync("BBS user lists are board-owned. Use ;users [ALL|TEST|PLAYER].");
                return;

            case "evil":
                {
                    var online = _world.GetAllOnlinePlayers()
                        .Where(player => !player.IsTestAccount)
                        .OrderByDescending(p => p.EvilPoints)
                        .ThenBy(p => p.Name)
                        .ToList();
                    await _client.SendLineAsync("[SYSOP LIST EVIL]");
                    if (online.Count == 0)
                    {
                        await _client.SendLineAsync("There are no users in the game at the moment.");
                        return;
                    }

                    foreach (var p in online)
                        await _client.SendLineAsync($"  {p.Name,-12} evil={p.EvilPoints,7:0.##} align={p.Alignment,5} room={p.CurrentMapNumber}/{p.CurrentRoomNumber}");

                    await _client.SendLineAsync($"There {(online.Count == 1 ? "is" : "are")} {online.Count} user{(online.Count == 1 ? "" : "s")} in the game.");
                    return;
                }

            case var s when s.StartsWith("gang ", StringComparison.OrdinalIgnoreCase):
                {
                    var gangName = args.Trim().Substring(5).Trim();
                    if (string.IsNullOrWhiteSpace(gangName))
                    {
                        await _client.SendLineAsync("Syntax: SYSOP LIST GANG <gangname>");
                        return;
                    }

                    var members = _world.PlayerRepo.GetPlayersByGang(gangName);
                    if (members.Count == 0)
                    {
                        await _client.SendLineAsync($"Gang {gangName} not found.");
                        return;
                    }

                    await _client.SendLineAsync($"[SYSOP LIST GANG] {gangName}");
                    foreach (var m in members)
                        await _client.SendLineAsync($"  {m.Name,-12} {m.LastName,-16}");
                    await _client.SendLineAsync($"Members: {members.Count}");
                    return;
                }

            case "limited":
                {
                    var limited = _world.Database.Items.Values.Where(i => i.Limit > 0).OrderBy(i => i.Number).Take(120).ToList();
                    await _client.SendLineAsync("[SYSOP LIST LIMITED]");
                    if (limited.Count == 0)
                    {
                        await _client.SendLineAsync("No limited items found in loaded data.");
                        return;
                    }

                    foreach (var item in limited)
                        await _client.SendLineAsync($"  #{item.Number,-5} limit={item.Limit,-4} {item.Name}");
                    await _client.SendLineAsync($"Limited item templates: {_world.Database.Items.Values.Count(i => i.Limit > 0)}");
                    return;
                }

            case "rooms":
            case "room":
                await _client.SendLineAsync("Use ;users [ALL|TEST|PLAYER] for BBS user lists.");
                await _client.SendLineAsync("Room listing is not implemented yet.");
                return;

            default:
                await SendSysopListHelp();
                return;
        }
    }

    private Task SendSysopListHelp()
    {
        return Task.WhenAll(
            _client.SendLineAsync("Use ;users [ALL|TEST|PLAYER] for BBS user lists."),
            _client.SendLineAsync("Syntax: SYSOP LIST EVIL"),
            _client.SendLineAsync("Syntax: SYSOP LIST GANG <gangname>"),
            _client.SendLineAsync("Syntax: SYSOP LIST LIMITED"));
    }

    private async Task HandleSysopMap(string args)
    {
        if (!string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: SYSOP MAP");
            return;
        }

        // Generated local map view around current room, intentionally shallow recursion
        // to mirror original stack-overflow safeguards discussed in WCCMMSYS.NOT.
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void.");
            return;
        }

        var discovered = new Dictionary<(int Map, int Room), (int X, int Y)>();
        var q = new Queue<((int Map, int Room) Room, int X, int Y, int Depth)>();
        discovered[(room.MapNumber, room.RoomNumber)] = (0, 0);
        q.Enqueue(((room.MapNumber, room.RoomNumber), 0, 0, 0));

        while (q.Count > 0)
        {
            var cur = q.Dequeue();
            if (cur.Depth >= 4) continue;

            var curRoom = _world.GetRoom(cur.Room.Map, cur.Room.Room);
            if (curRoom == null) continue;

            foreach (var (dir, next) in curRoom.GetExits())
            {
                if (next.Map != room.MapNumber) continue;
                if (!TryGetDirectionOffset(dir, out var dx, out var dy)) continue;

                var nextPos = (cur.X + dx, cur.Y + dy);
                var key = (next.Map, next.Room);
                if (discovered.ContainsKey(key)) continue;
                if (Math.Abs(nextPos.Item1) > 5 || Math.Abs(nextPos.Item2) > 5) continue;

                discovered[key] = nextPos;
                q.Enqueue((key, nextPos.Item1, nextPos.Item2, cur.Depth + 1));
            }
        }

        await _client.SendLineAsync($"[SYSOP MAP] {room.MapNumber}/{room.RoomNumber} {room.Name}");
        int minX = -5, maxX = 5, minY = -5, maxY = 5;
        for (int y = maxY; y >= minY; y--)
        {
            var line = new char[(maxX - minX + 1) * 2 + 1];
            Array.Fill(line, ' ');
            for (int x = minX; x <= maxX; x++)
            {
                char ch = ' ';
                if (x == 0 && y == 0)
                    ch = '@';
                else if (discovered.Values.Any(pos => pos.X == x && pos.Y == y))
                    ch = '#';
                line[(x - minX) * 2] = ch;
            }
            await _client.SendLineAsync(new string(line).TrimEnd());
        }

        await _client.SendLineAsync("Legend: @ = current room, # = mapped room");
    }

    private async Task HandleSysopBuffers(string args)
    {
        var sub = args.Trim().ToLowerInvariant();
        if (sub == "clear")
        {
            await _client.SendLineAsync("Done");
            return;
        }

        if (sub == "save")
        {
            foreach (var p in _world.GetAllOnlinePlayers())
                _world.PlayerRepo.SavePlayer(p);
            await _client.SendLineAsync("Done");
            return;
        }

        var stats = _world.GetBufferStats();
        await _client.SendLineAsync("[SYSOP BUFFERS]");
        await _client.SendLineAsync($"  Active monster rooms: {stats.ActiveRooms}");
        await _client.SendLineAsync($"  Active monsters:      {stats.ActiveMonsters}");
        await _client.SendLineAsync($"  Ground item rooms:    {stats.GroundItemRooms}");
        await _client.SendLineAsync($"  Ground currency rooms:{stats.GroundCurrencyRooms}");
        await _client.SendLineAsync("  Commands: SYSOP BUFFERS CLEAR | SYSOP BUFFERS SAVE");
    }

    private async Task HandleSysopGoto(string args)
    {
        if (_world.EvilTimers.IsInRetaliation(_player.Name))
        {
            await _client.SendLineAsync("You may not teleport during a PVP retaliation period.");
            return;
        }

        if (_player.InCombat)
        {
            _player.ClearCombatState();
            await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Current GOTO locations: NEWHAVEN, SILVERMERE, SUPPORT, RHUDAUR,");
            await _client.SendLineAsync("                        KHAZARAD, LOSTCITY, TESTING, TESTING ITEMS,");
            await _client.SendLineAsync("                        TESTING GROUP");
            return;
        }

        var input = args.Trim();

        // Resolution priority:
        //  1. Explicit destinations — map,room coordinates, the SYSOP TESTING rooms, and the curated GOTO
        //     location keywords (NEWHAVEN/SILVERMERE/...). Unambiguous, so they win outright.
        //  2. An ONLINE player by name (SYSOP GOTO <player>). A player beats the loose "a room mentions
        //     this word" search below — so GOTO TINY lands on the player Tiny, not the first room whose
        //     description merely contains "tiny" (the Lucky Strike Casino, 1/2 — the reported bug).
        //  3. The loose fallback: the first room whose Name/Description contains the word.
        // When the word ALSO matches the loose room search, we still go to the player but say so, so an
        // ambiguous name isn't silently disambiguated.
        if (TryResolveNamedGotoDestination(input, out int destMap, out int destRoom))
        {
            await TeleportSelfToRoomAsync(destMap, destRoom);
            return;
        }

        var gotoTarget = _world.FindOnlinePlayer(input);
        if (gotoTarget != null)
        {
            if (TryResolveGotoRoomByText(input, out int otherMap, out int otherRoom, out string otherRoomName))
            {
                await _client.SendLineAsync(
                    $"Note: \"{input}\" also matches the room \"{otherRoomName}\" ({otherMap}/{otherRoom}). " +
                    $"Going to player {gotoTarget.Name} — use SYSOP GOTO {otherMap},{otherRoom} for the room.");
            }

            await TeleportSelfToRoomAsync(gotoTarget.CurrentMapNumber, gotoTarget.CurrentRoomNumber);
            return;
        }

        if (TryResolveGotoRoomByText(input, out destMap, out destRoom, out _))
        {
            await TeleportSelfToRoomAsync(destMap, destRoom);
            return;
        }

        await _client.SendLineAsync("Current GOTO locations: NEWHAVEN, SILVERMERE, SUPPORT, RHUDAUR,");
        await _client.SendLineAsync("                        KHAZARAD, LOSTCITY, TESTING, TESTING ITEMS,");
        await _client.SendLineAsync("                        TESTING GROUP, or a <player> name.");
    }

    // Teleport the calling sysop to a room with the standard puff-of-smoke / burst-of-energy broadcasts.
    private async Task TeleportSelfToRoomAsync(int destMap, int destRoom)
    {
        if (_world.GetRoom(destMap, destRoom) == null)
        {
            await _client.SendLineAsync($"Room {destMap}/{destRoom} not found.");
            return;
        }

        int fromMap = _player.CurrentMapNumber;
        int fromRoom = _player.CurrentRoomNumber;
        _player.CurrentMapNumber = destMap;
        _player.CurrentRoomNumber = destRoom;
        _world.NotifyPlayerEnteredRoom(_player);

        _world.BroadcastToRoom(fromMap, fromRoom, $"{_player.Name} disappears in a puff of smoke.", _client);
        _world.BroadcastToRoom(destMap, destRoom, $"{_player.Name} appears in a burst of energy.", _client);
        await HandleIndependentPartyTravelCleanupAsync(_player, disbandLeader: true);

        await ShowRoom(brief: _player.BriefMode);
    }

    // Combined resolver used by SYSOP GOD … MOVEPLAYER (coordinates or any location name). Named
    // destinations first, then the loose room-text search.
    private bool TryResolveGotoDestination(string input, out int map, out int room)
        => TryResolveNamedGotoDestination(input, out map, out room)
           || TryResolveGotoRoomByText(input, out map, out room, out _);

    private async Task HandleSysopSpawn(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: SYSOP SPAWN <monster-id|name> [count]");
            await _client.SendLineAsync("Use SYSOP GOTO TESTING to stage arbitrary monsters for combat and regression testing.");
            return;
        }

        string monsterSelector = args.Trim();
        int spawnCount = 1;
        int trailingSpace = monsterSelector.LastIndexOf(' ');
        if (trailingSpace > 0 && int.TryParse(monsterSelector[(trailingSpace + 1)..], out var parsedCount))
        {
            if (parsedCount < 1 || parsedCount > 15)
            {
                await _client.SendLineAsync("Spawn count must be between 1 and 15.");
                return;
            }

            spawnCount = parsedCount;
            monsterSelector = monsterSelector[..trailingSpace].TrimEnd();
        }

        if (!TryResolveMonsterTemplate(monsterSelector, out var resolvedMonsterTemplate, out var failureMessage) || resolvedMonsterTemplate == null)
        {
            await _client.SendLineAsync(failureMessage);
            return;
        }

        var monsterTemplate = resolvedMonsterTemplate;

        bool ignoreRoomRestrictions = _world.IsSysopTestingRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        int spawned = 0;
        string? lastError = null;

        for (int index = 0; index < spawnCount; index++)
        {
            // Sysop spawn is a deliberate admin action — bypass the unique RegenTime cooldown so a boss can
            // be re-spawned for testing without waiting out its hour.
            if (_world.TrySpawnMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, monsterTemplate.Number, ignoreRoomRestrictions, out _, out var errorMessage, respectRegenTimer: false))
            {
                spawned++;
                continue;
            }

            lastError = errorMessage;
            break;
        }

        if (spawned == 0)
        {
            if (!ignoreRoomRestrictions && lastError == $"The {monsterTemplate.Name} cannot be spawned in this room.")
                lastError += " Use SYSOP GOTO TESTING to stage arbitrary monsters.";

            await _client.SendLineAsync(lastError ?? "The spawn failed.");
            return;
        }

        if (spawned == 1)
            await _client.SendLineAsync($"Spawned {monsterTemplate.Name} (#{monsterTemplate.Number}) in room {_player.CurrentMapNumber}/{_player.CurrentRoomNumber}.");
        else
            await _client.SendLineAsync($"Spawned {spawned} instances of {monsterTemplate.Name} (#{monsterTemplate.Number}) in room {_player.CurrentMapNumber}/{_player.CurrentRoomNumber}.");

        if (!string.IsNullOrWhiteSpace(lastError))
            await _client.SendLineAsync(lastError);
    }

    // SYSOP FIRSTDROP -- inspect and re-arm the guaranteed first-kill drop
    // (a LIMITED monster's first-ever spawn carries its whole drop table at 100%, until the
    // last-death stamp is set). Stock has no way to re-arm it short of a fresh board; this is the
    // operator tool for running another "first clear" season.
    //
    // REALM-SCOPED: the ledger lives in this realm's own database (ServerSettings MonsterRegenDeaths)
    // and every realm runs its own game process, so Main and PvP arm, spend and reset independently.
    private async Task HandleSysopFirstDrop(string args)
    {
        if (!_player.IsSysop)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}SYSOP FIRSTDROP is only available to full sysops.{MudAnsi.Reset}");
            return;
        }

        var parts = args.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;
        string subArgs = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (sub)
        {
            case "":
                await SendSysopFirstDropSummary();
                return;

            case "list":
                await HandleSysopFirstDropList(subArgs);
                return;

            case "status":
            case "show":
                await HandleSysopFirstDropStatus(subArgs);
                return;

            case "reset":
            case "rearm":
                await HandleSysopFirstDropReset(subArgs);
                return;

            default:
                await SendSysopFirstDropSyntax();
                return;
        }
    }

    private async Task SendSysopFirstDropSyntax()
    {
        await _client.SendLineAsync("Syntax: SYSOP FIRSTDROP                     -- this realm's first-kill drop status");
        await _client.SendLineAsync("        SYSOP FIRSTDROP LIST [n]            -- monsters whose first kill is SPENT");
        await _client.SendLineAsync("        SYSOP FIRSTDROP STATUS <id|name>    -- one monster, with its drop table");
        await _client.SendLineAsync("        SYSOP FIRSTDROP RESET <id|name>     -- re-arm one monster");
        await _client.SendLineAsync("        SYSOP FIRSTDROP RESET ALL CONFIRM   -- re-arm every monster in this realm");
    }

    private async Task SendSysopFirstDropSummary()
    {
        var spent = _world.GetSpentFirstDrops();
        int armed = _world.CountArmedFirstDrops();

        await _client.SendLineAsync($"{MudAnsi.BrightYellow}=== FIRST-KILL DROP (this realm only) ==={MudAnsi.Reset}");
        await _client.SendLineAsync($"  A limited monster's FIRST kill drops its entire table at 100%, ignoring drop %.");
        await _client.SendLineAsync($"  Armed : {armed} monster(s) still owe their first-kill drop.");
        await _client.SendLineAsync($"  Spent : {spent.Count} monster(s) have already been killed here.");
        await _client.SendLineAsync(string.Empty);
        await SendSysopFirstDropSyntax();
    }

    private async Task HandleSysopFirstDropList(string args)
    {
        int limit = 25;
        if (!string.IsNullOrWhiteSpace(args) && int.TryParse(args.Trim(), out var parsed) && parsed > 0)
            limit = Math.Clamp(parsed, 1, 500);

        var spent = _world.GetSpentFirstDrops();
        if (spent.Count == 0)
        {
            await _client.SendLineAsync("No monster has spent its first-kill drop in this realm -- every limited monster is armed.");
            return;
        }

        await _client.SendLineAsync($"{MudAnsi.BrightYellow}Spent first-kill drops (newest first), {Math.Min(limit, spent.Count)} of {spent.Count}:{MudAnsi.Reset}");
        foreach (var entry in spent.Take(limit))
        {
            string killed = entry.DiedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string name = entry.Name.Length > 30 ? entry.Name[..30] : entry.Name;
            await _client.SendLineAsync($"  #{entry.MonsterNumber,-5} {name,-30} first killed {killed}");
        }

        if (spent.Count > limit)
            await _client.SendLineAsync($"  ... {spent.Count - limit} more. Use SYSOP FIRSTDROP LIST {spent.Count} to see them all.");
    }

    private async Task HandleSysopFirstDropStatus(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: SYSOP FIRSTDROP STATUS <monster-id|name>");
            return;
        }

        if (!TryResolveMonsterTemplate(args, out var template, out var failureMessage) || template == null)
        {
            await _client.SendLineAsync(failureMessage);
            return;
        }

        await _client.SendLineAsync($"{MudAnsi.BrightYellow}{template.Name} (#{template.Number}){MudAnsi.Reset}");

        if (template.GameLimit <= 0)
        {
            await _client.SendLineAsync("  Not a limited monster (GameLimit 0) -- it never qualifies for a guaranteed first drop.");
            return;
        }

        await _client.SendLineAsync($"  GameLimit {template.GameLimit}, RegenTime {template.RegenTime}h");
        if (_world.IsFirstDropSpent(template.Number))
        {
            var entry = _world.GetSpentFirstDrops().First(candidate => candidate.MonsterNumber == template.Number);
            string killed = entry.DiedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            await _client.SendLineAsync($"  {MudAnsi.BrightRed}SPENT{MudAnsi.Reset} -- first killed {killed}. Drops now roll their percentages.");
        }
        else
        {
            await _client.SendLineAsync($"  {MudAnsi.BrightGreen}ARMED{MudAnsi.Reset} -- its next kill drops every item below at 100%.");
        }

        if (template.Drops.Count == 0)
        {
            await _client.SendLineAsync("  Drop table: (empty -- the guarantee has nothing to give)");
            return;
        }

        await _client.SendLineAsync("  Drop table:");
        foreach (var drop in template.Drops)
        {
            string itemName = _world.Database.Items.TryGetValue(drop.ItemId, out var item) ? item.Name : "(unknown item)";
            await _client.SendLineAsync($"    {drop.Percent,3}%  #{drop.ItemId,-5} {itemName}");
        }

        if (template.Drops.All(drop => drop.Percent >= 100))
            await _client.SendLineAsync("  NOTE: every item is already 100% -- the first-kill guarantee makes no visible difference here.");
    }

    private async Task HandleSysopFirstDropReset(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: SYSOP FIRSTDROP RESET <monster-id|name>  |  SYSOP FIRSTDROP RESET ALL CONFIRM");
            return;
        }

        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            bool confirmed = parts.Length >= 2 && parts[1].Equals("confirm", StringComparison.OrdinalIgnoreCase);
            if (!confirmed)
            {
                int spentCount = _world.GetSpentFirstDrops().Count;
                await _client.SendLineAsync($"{MudAnsi.BrightRed}WARNING: this re-arms the guaranteed first-kill drop for ALL {spentCount} spent monster(s) in THIS realm -- every boss will hand out its full drop table again on its next kill.{MudAnsi.Reset}");
                await _client.SendLineAsync($"{MudAnsi.BrightYellow}It also clears the RegenTime respawn cooldown those same stamps drive, so timer uniques become spawnable immediately.{MudAnsi.Reset}");
                await _client.SendLineAsync("Type  SYSOP FIRSTDROP RESET ALL CONFIRM  to proceed.");
                return;
            }

            int cleared = _world.RearmAllFirstDrops(out int rerolledAll);
            Console.WriteLine($"[SYSOP FIRSTDROP] {_player.Name} re-armed ALL first-kill drops ({cleared} cleared).");
            await _client.SendLineAsync($"{MudAnsi.BrightGreen}Re-armed the first-kill drop for {cleared} monster(s) in this realm.{MudAnsi.Reset}");
            if (rerolledAll > 0)
                await _client.SendLineAsync($"  {rerolledAll} instance(s) already standing in the world were re-rolled to carry their full table.");
            return;
        }

        if (!TryResolveMonsterTemplate(args.Trim(), out var template, out var failureMessage) || template == null)
        {
            await _client.SendLineAsync(failureMessage);
            return;
        }

        if (template.GameLimit <= 0)
        {
            await _client.SendLineAsync($"{template.Name} (#{template.Number}) is not a limited monster (GameLimit 0) -- it has no first-kill drop to re-arm.");
            return;
        }

        if (!_world.RearmFirstDrop(template.Number, out int rerolled))
        {
            await _client.SendLineAsync($"{template.Name} (#{template.Number}) is already armed -- it has not been killed in this realm yet.");
            return;
        }

        Console.WriteLine($"[SYSOP FIRSTDROP] {_player.Name} re-armed #{template.Number} {template.Name}.");
        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Re-armed the first-kill drop for {template.Name} (#{template.Number}) in this realm.{MudAnsi.Reset}");
        await _client.SendLineAsync("  Its RegenTime respawn cooldown was cleared with it (one stamp drives both, as in stock).");
        if (rerolled > 0)
            await _client.SendLineAsync($"  {rerolled} instance(s) already standing in the world were re-rolled to carry their full table.");
    }

    private static bool TryParseGotoCoordinates(string input, out int map, out int room)
    {
        map = 0;
        room = 0;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        var directMatch = System.Text.RegularExpressions.Regex.Match(
            input.Trim(),
            @"^(?<map>\d+)\s*[,/]\s*(?<room>\d+)$");

        if (directMatch.Success
            && int.TryParse(directMatch.Groups["map"].Value, out map)
            && int.TryParse(directMatch.Groups["room"].Value, out room))
        {
            return true;
        }

        var reverseMatch = System.Text.RegularExpressions.Regex.Match(
            input.Trim(),
            @"^(?<room>\d+)\s+(?<map>\d+)$");

        if (reverseMatch.Success
            && int.TryParse(reverseMatch.Groups["map"].Value, out map)
            && int.TryParse(reverseMatch.Groups["room"].Value, out room))
        {
            return true;
        }

        return false;
    }

    // Explicit, unambiguous GOTO targets: map,room coordinates, the SYSOP TESTING rooms, and the curated
    // location keywords. Does NOT do the loose "any room mentioning this word" search — that lives in
    // TryResolveGotoRoomByText so it can sit BELOW the online-player lookup in SYSOP GOTO.
    private bool TryResolveNamedGotoDestination(string input, out int map, out int room)
    {
        map = 0;
        room = 0;

        if (TryParseGotoCoordinates(input, out map, out room))
            return true;

        string key = input.Trim().ToLowerInvariant();
        if (GameWorld.TryResolveSysopTestingRoomAlias(key, out room))
        {
            map = GameWorld.SysopTestingRoomMapNumber;
            return true;
        }

        // Lost City is an outlier: no room is actually NAMED "lost city" (it's the amazon pyramid city on
        // map 16, named only in room descriptions), so the text search below can't pin it. Hardcode the
        // curated alias — and bare "lost" — to the city's bank (16/320), a central, memorable landing spot.
        if (key is "lostcity" or "lost city" or "lost")
        {
            map = 16;
            room = 320;
            return true;
        }

        string[]? aliases = key switch
        {
            "newhaven" => new[] { "newhaven" },
            "silvermere" => new[] { "silvermere" },
            "support" => new[] { "support" },
            "rhudaur" => new[] { "rhudaur" },
            "khazarad" => new[] { "khazarad", "khazad" },
            _ => null
        };

        if (aliases == null)
            return false;

        foreach (var alias in aliases)
        {
            if (TryFindFirstRoomByText(alias, out map, out room, out _))
                return true;
        }

        return false;
    }

    // Loose fallback: the first room (by map then room) whose Name or Description contains the word.
    private bool TryResolveGotoRoomByText(string input, out int map, out int room, out string roomName)
        => TryFindFirstRoomByText(input.Trim().ToLowerInvariant(), out map, out room, out roomName);

    private bool TryFindFirstRoomByText(string needle, out int map, out int room, out string roomName)
    {
        map = 0;
        room = 0;
        roomName = string.Empty;

        if (string.IsNullOrWhiteSpace(needle))
            return false;

        // Rank every candidate so SYSOP GOTO <name> lands on the actual PLACE, not a room that merely
        // mentions it. A room whose NAME matches beats one that only matches in its DESCRIPTION, and a
        // description match must be a whole WORD (not an incidental substring). This is what kept "bank of
        // godfrey" landing one room out on the street outside, "khaz" landing in Silvermere (a room whose
        // description names the road to Khazarad) instead of Khazarad itself, and "arly" teleporting into a
        // room whose description said "nearly". Ties break by map then room (lowest first) for determinism.
        Room? best = null;
        int bestScore = 0;
        foreach (var r in _world.Database.Rooms.Values)
        {
            int score = ScoreRoomTextMatch(r.Name, r.Description, needle);
            if (score == 0)
                continue;
            if (best == null
                || score > bestScore
                || (score == bestScore && RoomComesBefore(r, best)))
            {
                best = r;
                bestScore = score;
            }
        }

        if (best == null)
            return false;

        map = best.MapNumber;
        room = best.RoomNumber;
        roomName = best.Name;
        return true;
    }

    // Score a room against a GOTO query. Higher wins: exact name (100) > name prefix (90) > name whole-word
    // (80) > name substring (70) > description whole-word (40). A description SUBSTRING is deliberately NOT
    // a match (returns 0) — that loose case is exactly what sent "arly" into "nearly". Name substring IS a
    // match so an abbreviation still works (e.g. "khaz" → "Khazarad, Entry Arch"). internal for unit tests.
    internal static int ScoreRoomTextMatch(string? name, string? description, string needle)
    {
        needle = needle?.Trim() ?? string.Empty;
        if (needle.Length == 0)
            return 0;

        name ??= string.Empty;
        if (name.Equals(needle, StringComparison.OrdinalIgnoreCase))
            return 100;
        if (name.StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            return 90;
        if (ContainsWholeWord(name, needle))
            return 80;
        if (name.Contains(needle, StringComparison.OrdinalIgnoreCase))
            return 70;
        if (ContainsWholeWord(description ?? string.Empty, needle))
            return 40;
        return 0;
    }

    private static bool ContainsWholeWord(string haystack, string needle)
        => !string.IsNullOrEmpty(haystack)
           && System.Text.RegularExpressions.Regex.IsMatch(
                haystack,
                $@"(?<!\w){System.Text.RegularExpressions.Regex.Escape(needle)}(?!\w)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool RoomComesBefore(Room candidate, Room incumbent)
        => candidate.MapNumber != incumbent.MapNumber
            ? candidate.MapNumber < incumbent.MapNumber
            : candidate.RoomNumber < incumbent.RoomNumber;

    private async Task RelocateOnlinePlayerAsync(Player target, int destMap, int destRoom, string? targetNotice = null)
    {
        var targetClient = _world.GetClientForPlayer(target.Name);
        bool targetIsCurrentClient = ReferenceEquals(targetClient, _client);
        bool targetWasInCombat = target.InCombat;
        int fromMap = target.CurrentMapNumber;
        int fromRoom = target.CurrentRoomNumber;

        if (targetWasInCombat)
            target.ClearCombatState();

        target.CurrentMapNumber = destMap;
        target.CurrentRoomNumber = destRoom;
        _world.PlayerRepo.SavePlayer(target);
        _world.NotifyPlayerEnteredRoom(target);
        await HandleIndependentPartyTravelCleanupAsync(target, disbandLeader: true);

        _world.BroadcastToRoom(fromMap, fromRoom, $"{target.Name} disappears in a puff of smoke.", targetClient);
        _world.BroadcastToRoom(destMap, destRoom, $"{target.Name} appears in a burst of energy.", targetClient);

        if (targetClient == null)
            return;

        if (targetIsCurrentClient)
        {
            if (targetWasInCombat)
                await _client.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));

            if (!string.IsNullOrWhiteSpace(targetNotice))
                await _client.SendLineAsync(targetNotice);

            await ShowRoom(brief: target.BriefMode);
            return;
        }

        target.SuppressBroadcastReprompt = true;
        targetClient.BeginBuffering();
        try
        {
            await targetClient.ClearCurrentLineAsync();

            if (targetWasInCombat)
                await targetClient.SendLineAsync(GameAnsi.CombatOff("*Combat Off*"));

            if (!string.IsNullOrWhiteSpace(targetNotice))
                await targetClient.SendLineAsync(targetNotice);

            var targetParser = new CommandParser(targetClient, _world, target);
            await targetParser.ShowRoom(brief: target.BriefMode || target.FollowModeBlind);
            await targetClient.SendAsync(MudAnsi.Prompt(target));
        }
        finally
        {
            targetClient.FlushOutput();
            target.SuppressBroadcastReprompt = false;
        }
    }

    private const int SysopJailTimeSpellId = 935;       // "sysop jail time"; 151→MinBase 929→Teleport 1/1076
    private const int MediumTicksPerMinute = 20;        // active-spell upkeep runs on the 3s medium tick
    private const int SlumReleaseMap = 1;
    private const int SlumReleaseRoom = 1076;           // "Slum Street, Crossroads" — the jail-release room
    // Every jail-time debuff (alignment bands + the sysop one); cleared on a pardon so it can't re-fire.
    private static readonly int[] JailTimeSpellIds = { 586, 641, 642, 643, 644, SysopJailTimeSpellId };

    // Immediately release a jailed user to the slums, clearing any jail-time sentence so its auto-release
    // can't double-fire afterward. SYSOP PARDON <user>.
    private async Task HandleSysopPardon(string args)
    {
        var name = args.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await _client.SendLineAsync("Syntax: SYSOP PARDON <user>");
            return;
        }

        var target = _world.FindOnlinePlayer(name);
        if (target == null)
        {
            await _client.SendLineAsync($"Cannot find user {name}");
            return;
        }

        bool clearedAnySentence = false;
        foreach (int jailSpellId in JailTimeSpellIds)
            clearedAnySentence |= target.RemoveActiveSpell(jailSpellId);
        if (clearedAnySentence)
            _world.RecalculatePlayerStats(target);

        await RelocateOnlinePlayerAsync(target, SlumReleaseMap, SlumReleaseRoom,
            $"{MudAnsi.BrightWhite}You have been pardoned by the Gods. Be careful!{MudAnsi.Reset}");

        await _client.SendLineAsync($"You have pardoned {target.Name}.");
    }

    // SYSOP JAIL: spells 935 "sysop jail time" / 936 "sysop jail tele" haul a named player off
    // to jail. We reuse the same relocation the guard auto-jail and SYSOP GOD <user> JAIL use (teleport to
    // the jail room with the gods-cast notice). This bare form models no confinement timer; the timed form
    // (SYSOP GOD <user> JAIL <minutes>) layers spell 935 on top for an auto-release.
    private async Task<bool> JailPlayerAsync(Player target)
    {
        var jail = _world.Database.Rooms.Values
            .FirstOrDefault(r => r.Name.Contains("jail", StringComparison.OrdinalIgnoreCase));
        if (jail == null)
            return false;

        await RelocateOnlinePlayerAsync(target, jail.MapNumber, jail.RoomNumber,
            $"{MudAnsi.BrightWhite}The gods cast you into jail!{MudAnsi.Reset}");
        return true;
    }

    private async Task HandleSysopJail(string args)
    {
        var name = args.Trim();
        if (string.IsNullOrEmpty(name))
        {
            await _client.SendLineAsync("Syntax: SYSOP JAIL <user>");
            return;
        }

        var target = _world.FindOnlinePlayer(name);
        if (target == null)
        {
            await _client.SendLineAsync($"Cannot find user {name}");
            return;
        }

        if (await JailPlayerAsync(target))
            await _client.SendLineAsync($"You have jailed {target.Name}.");
        else
            await _client.SendLineAsync("No jail room is defined.");
    }

    // SYSOP GOD <user> CHANGE NAME <new> — rename a character whether they are ONLINE or OFFLINE, and
    // persist it (Players row + all references + the BBS account->character link, NOT the BBS login name).
    private async Task HandleSysopChangeNameAsync(string playerName, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
        {
            await _client.SendLineAsync("Syntax: SYSOP GOD <user> CHANGE NAME <new-character-id>");
            return;
        }

        var oldName = Player.NormalizeNamePart(playerName);
        switch (_world.RenamePlayer(oldName, newName))
        {
            case GameWorld.RenamePlayerOutcome.Success:
                await _client.SendLineAsync($"Renamed {oldName} to {Player.NormalizeNamePart(newName)}.");
                break;
            case GameWorld.RenamePlayerOutcome.NotFound:
                await _client.SendLineAsync($"Cannot find user {playerName}");
                break;
            case GameWorld.RenamePlayerOutcome.NameTaken:
                await _client.SendLineAsync("That name is already taken!");
                break;
            default:
                await _client.SendLineAsync("The name you have chosen is invalid!");
                break;
        }
    }

    private async Task HandleSysopGod(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await _client.SendLineAsync("Syntax: SYSOP GOD <user> <command>");
            return;
        }

        // A tester sysop may only use the limited GOD option set (checked before any handler, incl. the
        // offline CHANGE NAME path below). Full sysops are never gated here.
        if (!_player.IsSysop && _player.IsTesterSysop
            && !IsTesterAllowedGodCommand(parts[1].Trim().ToLowerInvariant()))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}That SYSOP GOD option is not available to tester sysops.{MudAnsi.Reset}");
            await SendTesterGodOptions();
            return;
        }

        // CHANGE NAME must work on OFFLINE players too, so handle it BEFORE the online-only target lookup.
        if (parts[1].Trim().StartsWith("change name ", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSysopChangeNameAsync(parts[0], parts[1].Trim()["change name ".Length..].Trim());
            return;
        }

        var target = _world.FindOnlinePlayer(parts[0]);
        if (target == null)
        {
            await _client.SendLineAsync($"Cannot find user {parts[0]}");
            return;
        }

        var cmd = parts[1].Trim();
        var cmdLower = cmd.ToLowerInvariant();

        if (cmdLower == "inventory")
        {
            await _client.SendLineAsync($"Info: {target.Name} inventory items={target.Inventory.Count} equipped={target.Equipment.Count}");
            return;
        }
        if (cmdLower == "profile")
        {
            await _client.SendLineAsync($"Info: {target.Name} profile brief={(target.BriefMode ? 1 : 0)} talk={target.TalkMode}");
            return;
        }
        if (cmdLower == "spells")
        {
            var targetClass = _world.Database.Classes[target.ClassId];
            if (Player.UsesKai(targetClass))
                await _client.SendLineAsync($"Info: {target.Name} kai={target.CurrentMana}/{target.MaxMana}");
            else
                await _client.SendLineAsync($"Info: {target.Name} spellcasting={target.SpellCasting} mana={target.CurrentMana}/{target.MaxMana}");
            return;
        }
        if (cmdLower == "powers" || cmdLower == "abilities" || cmdLower == "verify abilities")
        {
            await _client.SendLineAsync($"Info: {target.Name} class/race abilities loaded via data tables.");
            return;
        }
        if (cmdLower == "status")
        {
            await HandleSysopStatus(target.Name);
            return;
        }
        if (cmdLower == "info")
        {
            await HandleSysopStatus(target.Name);
            await _client.SendLineAsync($"Info: inventory={target.Inventory.Count} equipped={target.Equipment.Count} gang='{target.Gang}'");
            return;
        }
        if (cmdLower == "add life")
        {
            target.Lives = Math.Min(9, target.Lives + 1);
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        if (cmdLower.StartsWith("addcurrency ") || cmdLower.StartsWith("add currency "))
        {
            int prefixLength = cmdLower.StartsWith("addcurrency ") ? "addcurrency ".Length : "add currency ".Length;
            string currencyArgs = cmd[prefixLength..].Trim();

            if (!TryParseCurrency(currencyArgs, out int amount, out _, out _, out long denominationMultiplier)
                || amount <= 0
                || denominationMultiplier <= 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> ADDCURRENCY <amount> <copper|silver|gold|platinum|runic>");
                return;
            }

            AddPlayerCurrencyDenomination(target, denominationMultiplier, amount);
            _world.PlayerRepo.SavePlayer(target);

            string currencyText = FormatCurrencyDenomination(denominationMultiplier, amount);
            await _client.SendLineAsync($"Granted {currencyText} to {target.Name}.");
            if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
                _world.SendToPlayer(target.Name, $"{MudAnsi.White}The gods grant you {currencyText}.{MudAnsi.Reset}");
            return;
        }
        if (cmdLower.StartsWith("add exp ") || cmdLower.StartsWith("add experience "))
        {
            var value = cmdLower.StartsWith("add exp ") ? cmd[8..].Trim() : cmd[15..].Trim();
            if (!long.TryParse(value, out var delta))
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> ADD EXP <n>");
                return;
            }

            target.Experience = Math.Max(0, target.Experience + delta);
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        if (cmdLower.StartsWith("add evil "))
        {
            if (!int.TryParse(cmd[9..].Trim(), out var delta))
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> ADD EVIL <n>");
                return;
            }
            // EvilPoints is the only alignment slot — sysop deltas write it
            // directly; no separate Alignment field to keep in sync.
            int alignmentBucketBefore = CombatEngine.GetAlignmentBucket(target.EvilPoints);
            target.EvilPoints += delta;
            // A band change strips disallowed gear.
            RevalidateWornItemsAfterAlignmentChange(target, alignmentBucketBefore);
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        // Stock alignment-banding thresholds (CombatEngine.GetPlayerAlignment): Saint < −200,
        // Good < −50, Neutral [−50..10), Seedy [10..40), Outlaw [40..100), Criminal [100..200),
        // Villain [200..400), FIEND ≥ 400. Sysop shortcuts drop the player into the centre of
        // each band. Lawful flips IsLawful and clamps EP into the Good range so future evil
        // actions are blocked by the lawful gate; unlawful drops the flag and bumps EP into Seedy.
        // Each shortcut writes EvilPoints directly, then re-validates worn gear against the new band
        // before saving — so a sysop-forced alignment swap strips items
        // the player can no longer use, exactly like an earned change.
        if (cmdLower == "good") { int b = CombatEngine.GetAlignmentBucket(target.EvilPoints); target.EvilPoints = -100; RevalidateWornItemsAfterAlignmentChange(target, b); _world.PlayerRepo.SavePlayer(target); await _client.SendLineAsync("Done"); return; }
        if (cmdLower == "saint") { int b = CombatEngine.GetAlignmentBucket(target.EvilPoints); target.EvilPoints = -300; RevalidateWornItemsAfterAlignmentChange(target, b); _world.PlayerRepo.SavePlayer(target); await _client.SendLineAsync("Done"); return; }
        if (cmdLower == "neutral") { int b = CombatEngine.GetAlignmentBucket(target.EvilPoints); target.EvilPoints = 0; RevalidateWornItemsAfterAlignmentChange(target, b); _world.PlayerRepo.SavePlayer(target); await _client.SendLineAsync("Done"); return; }
        if (cmdLower == "lawful") { int b = CombatEngine.GetAlignmentBucket(target.EvilPoints); target.IsLawful = true; target.EvilPoints = Math.Min(target.EvilPoints, -100); RevalidateWornItemsAfterAlignmentChange(target, b); _world.PlayerRepo.SavePlayer(target); await _client.SendLineAsync("Done"); return; }
        if (cmdLower == "unlawful") { int b = CombatEngine.GetAlignmentBucket(target.EvilPoints); target.IsLawful = false; target.EvilPoints = Math.Max(target.EvilPoints, 40); RevalidateWornItemsAfterAlignmentChange(target, b); _world.PlayerRepo.SavePlayer(target); await _client.SendLineAsync("Done"); return; }
        if (cmdLower == "clear suicide")
        {
            await _client.SendLineAsync("Done");
            return;
        }
        // SYSOP GOD <player> ME — pull the player straight to the sysop's current room (the inverse of
        // MOVEPLAYER, which sends them to a named place). "here"/"summon" are accepted aliases.
        if (cmdLower is "me" or "here" or "summon")
        {
            if (target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("You cannot summon yourself.");
                return;
            }

            await RelocateOnlinePlayerAsync(target, _player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{MudAnsi.BrightYellow}A sysop summons you through the void.{MudAnsi.Reset}");
            await _client.SendLineAsync($"Summoned {target.Name} to your room.");
            return;
        }
        if (cmdLower.StartsWith("moveplayer ") || cmdLower.StartsWith("move player "))
        {
            int prefixLength = cmdLower.StartsWith("moveplayer ") ? "moveplayer ".Length : "move player ".Length;
            string destinationInput = cmd[prefixLength..].Trim();
            if (string.IsNullOrWhiteSpace(destinationInput))
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> MOVEPLAYER <map,room|location>");
                return;
            }

            if (!TryResolveGotoDestination(destinationInput, out int destMap, out int destRoom))
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> MOVEPLAYER <map,room|location>");
                return;
            }

            var room = _world.GetRoom(destMap, destRoom);
            if (room == null)
            {
                await _client.SendLineAsync($"Room {destMap}/{destRoom} not found.");
                return;
            }

            await RelocateOnlinePlayerAsync(target, destMap, destRoom, $"{MudAnsi.BrightYellow}A sysop moves you through the void.{MudAnsi.Reset}");
            await _client.SendLineAsync($"Moved {target.Name} to {destMap}/{destRoom}.");
            return;
        }
        if (cmdLower.StartsWith("speak "))
        {
            var message = cmd[6..].Trim();
            if (string.IsNullOrWhiteSpace(message))
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> SPEAK <message>");
                return;
            }

            _world.BroadcastToRoom(target.CurrentMapNumber, target.CurrentRoomNumber,
                $"The gods tell you: {message}");
            await _client.SendLineAsync("You have spoken.");
            return;
        }
        if (cmdLower == "change sex")
        {
            target.Gender = target.Gender == 1 ? 0 : 1;
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        if (cmdLower == "disable topten")
        {
            target.IsToptenDisabled = true;
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        if (cmdLower == "enable topten")
        {
            target.IsToptenDisabled = false;
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        if (cmdLower == "retrain")
        {
            target.CharacterPoints += target.SpentCP;
            target.SpentCP = 0;
            target.CurrentMapNumber = 1;
            target.CurrentRoomNumber = 1;
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync("Done");
            return;
        }
        if (cmdLower == "jail" || cmdLower.StartsWith("jail "))
        {
            string jailArg = cmd.Length > 4 ? cmd[4..].Trim() : string.Empty;
            int minutes = 0;
            if (jailArg.Length > 0 && (!int.TryParse(jailArg, out minutes) || minutes <= 0))
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> JAIL [minutes]");
                return;
            }

            if (!await JailPlayerAsync(target))
            {
                await _client.SendLineAsync("No jail room is defined.");
                return;
            }

            if (minutes > 0)
            {
                // Timed sentence: the canonical "sysop jail time" debuff (935) auto-releases the prisoner to
                // the slums when it expires (ability 151 → MinBase 929 "exit jail" → Teleport 1/1076).
                // Duration is in medium ticks (3s); the timer advances while online and survives a relog.
                target.AddOrRefreshActiveSpell(SysopJailTimeSpellId, 0, minutes * MediumTicksPerMinute);
                _world.RecalculatePlayerStats(target);
                _world.PlayerRepo.SavePlayer(target);
                await _client.SendLineAsync($"Jailed {target.Name} for {minutes} minute(s).");
            }
            else
            {
                await _client.SendLineAsync("Done");
            }
            return;
        }
        if (cmdLower.StartsWith("setlevel "))
        {
            if (!int.TryParse(cmd[9..].Trim(), out int newLevel) || newLevel < 1)
            {
                await _client.SendLineAsync("Syntax: SYSOP GOD <user> SETLEVEL <level>");
                return;
            }
            await SetPlayerLevel(target, newLevel);
            return;
        }
        if (cmdLower.StartsWith("setrace "))
        {
            await SetPlayerRace(target, cmd[8..].Trim());
            return;
        }
        if (cmdLower.StartsWith("setclass "))
        {
            await SetPlayerClass(target, cmd[9..].Trim());
            return;
        }
        if (cmdLower == "heal")
        {
            target.CurrentHP = target.MaxHP;
            target.CurrentMana = target.MaxMana;
            _world.PlayerRepo.SavePlayer(target);
            await _client.SendLineAsync($"Healed {target.Name}. HP: {target.CurrentHP}/{target.MaxHP}  Mana: {target.CurrentMana}/{target.MaxMana}");
            if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
                _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}The gods restore your health!{MudAnsi.Reset}");
            return;
        }
        if (cmdLower.StartsWith("giveitem "))
        {
            await SysopGiveItem(target, cmd[9..].Trim());
            return;
        }
        if (cmdLower.StartsWith("grantspell "))
        {
            await SysopGrantSpell(target, cmd[11..].Trim());
            return;
        }

        await _client.SendLineAsync("Syntax: SYSOP GOD <user> <command>");
        await _client.SendLineAsync("Valid options: ADD LIFE, ADD EXP n, ADD EVIL n, ADDCURRENCY n <type>, CLEAR SUICIDE, SPEAK xxx,");
        await _client.SendLineAsync("CHANGE NAME, DISABLE TOPTEN, ENABLE TOPTEN, INVENTORY, PROFILE,");
        await _client.SendLineAsync("SPELLS, POWERS, GOOD, SAINT, NEUTRAL, LAWFUL, UNLAWFUL, RETRAIN, ABILITIES,");
        await _client.SendLineAsync("VERIFY ABILITIES, STATUS, INFO, CHANGE SEX, JAIL [minutes], MOVEPLAYER <map,room|location>,");
        await _client.SendLineAsync("SETLEVEL <n>, SETRACE <name|#>, SETCLASS <name|#>, HEAL, GIVEITEM <name|#>");
    }

    // The GOD options a tester sysop may use (a non-destructive testing subset). Everything else —
    // ADD EXP/CURRENCY, SETLEVEL/RACE/CLASS, CHANGE NAME/SEX, JAIL, HEAL, RETRAIN, topten, SPEAK, ME —
    // is full-sysop only.
    private static bool IsTesterAllowedGodCommand(string cmdLower)
    {
        switch (cmdLower)
        {
            case "add life":
            case "inventory":
            case "profile":
            case "spells":
            case "powers":
            case "abilities":
            case "verify abilities":
            case "status":
            case "info":
            case "good":
            case "saint":
            case "neutral":
            case "lawful":
            case "unlawful":
                return true;
        }

        return cmdLower.StartsWith("add evil ")
            || cmdLower.StartsWith("moveplayer ")
            || cmdLower.StartsWith("move player ")
            || cmdLower.StartsWith("giveitem ");
    }

    private async Task SendTesterGodOptions()
    {
        await _client.SendLineAsync("Syntax: SYSOP GOD <user> <command>");
        await _client.SendLineAsync("Valid options: ADD LIFE, ADD EVIL n, INVENTORY, PROFILE,");
        await _client.SendLineAsync("SPELLS, POWERS, GOOD, SAINT, NEUTRAL, LAWFUL, UNLAWFUL,");
        await _client.SendLineAsync("VERIFY ABILITIES, STATUS, INFO, MOVEPLAYER <map,room|location>, GIVEITEM <name|#>");
    }

    private async Task HandleSysopInvisible(string args)
    {
        var mode = args.Trim().ToLowerInvariant();

        if (string.IsNullOrEmpty(mode))
        {
            string current = _player.IsSysopInvisible ? "FULL"
                : _player.IsSysopNoAggro ? "NOAGGRO"
                : "OFF";
            await _client.SendLineAsync($"Current invisibility: {current}");
            await _client.SendLineAsync("Syntax: SYSOP INVIS [FULL/NOAGGRO/OFF]");
            await _client.SendLineAsync("  FULL    -- Completely invisible: not shown in room, no movement messages, no monster aggro");
            await _client.SendLineAsync("  NOAGGRO -- Visible in room but monsters will not attack you");
            await _client.SendLineAsync("  OFF     -- Normal visibility");
            return;
        }

        switch (mode)
        {
            case "full":
            case "on":
                _player.IsSysopInvisible = true;
                _player.IsSysopNoAggro = false;
                await _client.SendLineAsync($"{MudAnsi.BrightCyan}You fade from sight. You are now fully invisible.{MudAnsi.Reset}");
                break;
            case "noaggro":
            case "partial":
            case "protected":
                _player.IsSysopInvisible = false;
                _player.IsSysopNoAggro = true;
                await _client.SendLineAsync($"{MudAnsi.BrightCyan}A divine shield surrounds you. Monsters will ignore you.{MudAnsi.Reset}");
                break;
            case "off":
            case "none":
                _player.IsSysopInvisible = false;
                _player.IsSysopNoAggro = false;
                await _client.SendLineAsync($"{MudAnsi.BrightCyan}You shimmer back into full visibility.{MudAnsi.Reset}");
                break;
            default:
                await _client.SendLineAsync("Syntax: SYSOP INVIS [FULL/NOAGGRO/OFF]");
                break;
        }

        // Visibility just changed — refresh the web online-players roster.
        _world.PublishOnlinePresence();
    }

    private async Task HandleSysopSetLevel(string args)
    {
        if (!int.TryParse(args.Trim(), out int newLevel) || newLevel < 1)
        {
            await _client.SendLineAsync("Syntax: SYSOP SETLEVEL <level>");
            return;
        }

        await SetPlayerLevel(_player, newLevel);
    }

    private async Task SetPlayerLevel(Player target, int newLevel)
    {
        if (!_world.Database.Races.TryGetValue(target.RaceId, out var race) ||
            !_world.Database.Classes.TryGetValue(target.ClassId, out var cls))
        {
            await _client.SendLineAsync("Error: could not resolve race/class data.");
            return;
        }

        int oldLevel = target.Level;
        target.Level = newLevel;

        // Recalculate HP from scratch: start with level-1 base, then roll each subsequent level.
        int baseHp = CharacterCreation.CalculateStartingHp(target.Health, race.HPPerLvl, cls.MinHits, cls.MaxHits - cls.MinHits);
        int totalHp = baseHp;
        for (int lvl = 1; lvl < newLevel; lvl++)
        {
            totalHp += CharacterCreation.RollLevelUpHpGain(Random.Shared, target.Health, lvl, race.HPPerLvl, cls.MinHits, cls.MaxHits - cls.MinHits);
        }
        target.MaxHP = Math.Max(1, totalHp);
        target.CurrentHP = target.MaxHP;

        // Recalculate CP: BaseCP from race + per-level gains.
        int totalCP = race.BaseCP;
        for (int lvl = 2; lvl <= newLevel; lvl++)
        {
            totalCP += lvl <= 10 ? 10 : lvl <= 20 ? 15 : (((lvl - 1) / 10) * 5) + 10;
        }
        int oldTotalCP = target.CharacterPoints + target.SpentCP;
        int cpDelta = totalCP - oldTotalCP;
        target.CharacterPoints = Math.Max(0, target.CharacterPoints + cpDelta);

        target.RecalculateStats(race, cls, _world.Database);
        target.RecalculateEquipment(_world.Database);
        target.CurrentMana = target.MaxMana;
        target.Lives = 9;
        _world.PlayerRepo.SavePlayer(target);

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Level changed: {oldLevel} -> {newLevel}{MudAnsi.Reset}");
        await _client.SendLineAsync($"  HP: {target.MaxHP}  Mana: {target.MaxMana}  CP: {target.CharacterPoints} unspent ({target.CharacterPoints + target.SpentCP} total)");

        if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}A sysop has set your level to {newLevel}.{MudAnsi.Reset}");
    }

    private async Task HandleSysopSetRace(string args)
    {
        var input = args.Trim();
        if (string.IsNullOrEmpty(input))
        {
            await _client.SendLineAsync("Syntax: SYSOP SETRACE <name or number>");
            await _client.SendLineAsync("Available races:");
            foreach (var r in _world.Database.Races.Values.OrderBy(r => r.Number))
                await _client.SendLineAsync($"  {r.Number,3}: {r.Name}");
            return;
        }

        await SetPlayerRace(_player, input);
    }

    private async Task SetPlayerRace(Player target, string input)
    {
        Race? newRace = null;
        if (int.TryParse(input, out int raceId))
            _world.Database.Races.TryGetValue(raceId, out newRace);
        else
            newRace = _world.Database.Races.Values.FirstOrDefault(r =>
                r.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase));

        if (newRace == null)
        {
            await _client.SendLineAsync($"Race '{input}' not found.");
            return;
        }

        if (!_world.Database.Classes.TryGetValue(target.ClassId, out var cls))
        {
            await _client.SendLineAsync("Error: could not resolve class data.");
            return;
        }

        int oldRaceId = target.RaceId;
        target.RaceId = newRace.Number;

        // Clamp base stats to new race minimums (don't lower stats above the new min).
        target.BaseStrength = Math.Max(target.BaseStrength, newRace.MinStr);
        target.BaseAgility = Math.Max(target.BaseAgility, newRace.MinAgl);
        target.BaseIntellect = Math.Max(target.BaseIntellect, newRace.MinInt);
        target.BaseWillpower = Math.Max(target.BaseWillpower, newRace.MinWil);
        target.BaseHealth = Math.Max(target.BaseHealth, newRace.MinHea);
        target.BaseCharm = Math.Max(target.BaseCharm, newRace.MinChm);
        target.Strength = target.BaseStrength;
        target.Agility = target.BaseAgility;
        target.Intellect = target.BaseIntellect;
        target.Willpower = target.BaseWillpower;
        target.Health = target.BaseHealth;
        target.Charm = target.BaseCharm;

        // Recalculate MaxHP for the new race: HPPerLvl varies by race, so the stored MaxHP
        // (rolled under the old race) is stale. Mirror SetPlayerClass's HP roll.
        int baseHp = CharacterCreation.CalculateStartingHp(target.Health, newRace.HPPerLvl, cls.MinHits, cls.MaxHits - cls.MinHits);
        int totalHp = baseHp;
        for (int lvl = 1; lvl < target.Level; lvl++)
        {
            totalHp += CharacterCreation.RollLevelUpHpGain(Random.Shared, target.Health, lvl, newRace.HPPerLvl, cls.MinHits, cls.MaxHits - cls.MinHits);
        }
        target.MaxHP = Math.Max(1, totalHp);

        target.RecalculateStats(newRace, cls, _world.Database);
        target.RecalculateEquipment(_world.Database);
        target.CurrentHP = target.MaxHP;
        target.CurrentMana = target.MaxMana;
        _world.PlayerRepo.SavePlayer(target);

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Race changed to {newRace.Name} (#{newRace.Number}).{MudAnsi.Reset}");

        if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}A sysop has changed your race to {newRace.Name}.{MudAnsi.Reset}");
    }

    private async Task HandleSysopSetClass(string args)
    {
        var input = args.Trim();
        if (string.IsNullOrEmpty(input))
        {
            await _client.SendLineAsync("Syntax: SYSOP SETCLASS <name or number>");
            await _client.SendLineAsync("Available classes:");
            foreach (var c in _world.Database.Classes.Values.OrderBy(c => c.Number))
                await _client.SendLineAsync($"  {c.Number,3}: {c.Name}");
            return;
        }

        await SetPlayerClass(_player, input);
    }

    private async Task SetPlayerClass(Player target, string input)
    {
        CharacterClass? newClass = null;
        if (int.TryParse(input, out int classId))
            _world.Database.Classes.TryGetValue(classId, out newClass);
        else
            newClass = _world.Database.Classes.Values.FirstOrDefault(c =>
                c.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase));

        if (newClass == null)
        {
            await _client.SendLineAsync($"Class '{input}' not found.");
            return;
        }

        if (!_world.Database.Races.TryGetValue(target.RaceId, out var race))
        {
            await _client.SendLineAsync("Error: could not resolve race data.");
            return;
        }

        target.ClassId = newClass.Number;

        // Recalculate HP for the new class.
        int baseHp = CharacterCreation.CalculateStartingHp(target.Health, race.HPPerLvl, newClass.MinHits, newClass.MaxHits - newClass.MinHits);
        int totalHp = baseHp;
        for (int lvl = 1; lvl < target.Level; lvl++)
        {
            totalHp += CharacterCreation.RollLevelUpHpGain(Random.Shared, target.Health, lvl, race.HPPerLvl, newClass.MinHits, newClass.MaxHits - newClass.MinHits);
        }
        target.MaxHP = Math.Max(1, totalHp);
        target.CurrentHP = target.MaxHP;

        target.RecalculateStats(race, newClass, _world.Database);
        target.RecalculateEquipment(_world.Database);
        target.CurrentMana = target.MaxMana;
        _world.PlayerRepo.SavePlayer(target);

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Class changed to {newClass.Name} (#{newClass.Number}).{MudAnsi.Reset}");

        if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}A sysop has changed your class to {newClass.Name}.{MudAnsi.Reset}");
    }

    private async Task HandleSysopHeal(string args)
    {
        Player target = _player;
        if (!string.IsNullOrWhiteSpace(args))
        {
            var found = _world.FindOnlinePlayer(args.Trim());
            if (found == null)
            {
                await _client.SendLineAsync($"Cannot find user {args.Trim()}");
                return;
            }
            target = found;
        }

        target.CurrentHP = target.MaxHP;
        target.CurrentMana = target.MaxMana;
        _world.PlayerRepo.SavePlayer(target);

        if (target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            await _client.SendLineAsync($"{MudAnsi.BrightGreen}You are fully healed. HP: {target.CurrentHP}/{target.MaxHP}  Mana: {target.CurrentMana}/{target.MaxMana}{MudAnsi.Reset}");
        else
        {
            await _client.SendLineAsync($"Healed {target.Name}. HP: {target.CurrentHP}/{target.MaxHP}  Mana: {target.CurrentMana}/{target.MaxMana}");
            _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}The gods restore your health!{MudAnsi.Reset}");
        }
    }

    private async Task SysopGiveItem(Player target, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            await _client.SendLineAsync("Syntax: SYSOP GIVEITEM <name or number>");
            return;
        }

        Item? item = null;
        if (int.TryParse(input, out int itemId))
        {
            _world.Database.Items.TryGetValue(itemId, out item);
        }
        else
        {
            // Narrow the world-wide candidate set by match quality before checking ambiguity —
            // typing "shovel" picks the "shovel" item over the "black runed shovel" because the
            // user literally cannot be more specific than the full canonical name.
            var matches = TargetNameMatcher.NarrowToBestMatches(
                _world.Database.Items.Values,
                i => i.Name,
                input);

            if (matches.Count == 0)
            {
                await _client.SendLineAsync($"No item found matching '{input}'.");
                return;
            }

            var distinctNames = matches.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctNames.Count > 1)
            {
                await ShowItemDisambiguationAsync(distinctNames);
                return;
            }

            item = matches[0];
        }

        if (item == null)
        {
            await _client.SendLineAsync($"Item '{input}' not found.");
            return;
        }

        AddItemToInventory(target, item.Number);
        _world.PlayerRepo.SavePlayer(target);

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Gave {item.Name} (#{item.Number}) to {target.Name}.{MudAnsi.Reset}");
        if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}The gods grant you {item.Name}.{MudAnsi.Reset}");
    }

    // SYSOP DESTROYITM <name|id>: annihilate a carried item from your own inventory/equipment. Unlike
    // DROP/STASH, this deliberately BYPASSES the NotDroppable / cursed gates so a tester can get rid of a
    // Loyal quest item (e.g. the Hellblade) that no player command can remove. Nothing hits the ground —
    // the instance is destroyed. Available to full and tester sysops.
    private async Task SysopDestroyItem(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            await _client.SendLineAsync("Syntax: SYSOP DESTROYITM <name or number>");
            return;
        }

        if (!TryResolveCarriedItemByNameOrId(input.Trim(), out var carriedItem, out var ambiguousNames))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return;
            }

            await _client.SendLineAsync($"You aren't carrying '{input}'.");
            return;
        }

        // Remove from inventory or equipment (auto-unequip), then clean up light state exactly like DROP so
        // destroying a lit torch doesn't leak a dangling light source.
        if (!TryRemoveResolvedCarriedItem(carriedItem, out _))
        {
            await _client.SendLineAsync($"You aren't carrying '{input}'.");
            return;
        }

        var item = carriedItem.Item;
        long instanceId = carriedItem.InstanceId;
        if (CanItemBeLightSource(item))
            ExtinguishLightIfActive(instanceId, item);

        RemoveLightStateIfNotCarried(instanceId);
        RecalcEquipment();
        _world.PlayerRepo.SavePlayer(_player);

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Destroyed {item.Name} (#{item.Number}).{MudAnsi.Reset}");
    }

    // Resolve one carried item (inventory OR equipped) by numeric id or by name. The id path lets a tester
    // target an item precisely (e.g. two differently-instanced copies of the same name); the name path
    // reuses the standard carried-item matcher, including its ambiguity disambiguation.
    private bool TryResolveCarriedItemByNameOrId(string input, out CarriedItemMatch match, out IReadOnlyList<string>? ambiguousNames)
    {
        match = default;
        ambiguousNames = null;

        if (int.TryParse(input, out int itemId))
        {
            EnsureItemInstanceAlignment();

            for (int index = 0; index < _player.Inventory.Count; index++)
            {
                if (_player.Inventory[index] == itemId && _world.Database.Items.TryGetValue(itemId, out var invItem))
                {
                    match = new CarriedItemMatch(itemId, invItem, _player.InventoryInstanceIds[index], index, null);
                    return true;
                }
            }

            foreach (var (slot, equippedItemId) in _player.Equipment)
            {
                if (equippedItemId == itemId && _world.Database.Items.TryGetValue(itemId, out var eqItem))
                {
                    match = new CarriedItemMatch(itemId, eqItem, GetEquipmentInstanceId(slot, equippedItemId), -1, slot);
                    return true;
                }
            }

            return false;
        }

        return TryResolveUniqueCarriedItem(input, includeEquipped: true, out match, out ambiguousNames);
    }

    // SYSOP GRANTSPELL <name|id>: learn a spell into the target's spellbook (same learned-flag path as
    // reading a scroll), but only if the target's class can actually USE it. The gate mirrors the stock
    // spell-eligibility check (PlayerCanUseLearnedSpell): a non-zero magery school must match the class's
    // school and level, and the target must meet the spell's required level. So `grantspell hellstorm`
    // succeeds for a level-50+ Mage and is refused (with the reason) for a Warrior or an under-level Mage.
    private async Task SysopGrantSpell(Player target, string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            await _client.SendLineAsync("Syntax: SYSOP GRANTSPELL <name or number>");
            return;
        }

        if (!TryResolveSpellByNameOrId(input.Trim(), out var spell, out var ambiguousNames))
        {
            if (ambiguousNames.Count > 1)
            {
                await _client.SendLineAsync($"'{input}' is ambiguous. Did you mean: {string.Join(", ", ambiguousNames)}?");
                return;
            }

            await _client.SendLineAsync($"No spell found matching '{input}'.");
            return;
        }

        if (!_world.Database.Classes.TryGetValue(target.ClassId, out var cls) || cls.MageryType == 0)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{target.Name} is not a spellcasting class and cannot use {spell!.Name}.{MudAnsi.Reset}");
            return;
        }

        // Mirror PlayerCanUseLearnedSpell so a granted spell is genuinely castable, with a specific reason.
        if (spell!.Magery != 0 && (cls.MageryType != spell.Magery || cls.MageryLvl < spell.MageryLvl))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{cls.Name} cannot use {spell.Name} (#{spell.Number}) — wrong magery school/level.{MudAnsi.Reset}");
            return;
        }

        if (target.Level < spell.ReqLevel)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{spell.Name} (#{spell.Number}) requires level {spell.ReqLevel}; {target.Name} is level {target.Level}.{MudAnsi.Reset}");
            return;
        }

        int learnedAbilityId = Player.GetLearnedSpellbookAbilityId(spell.Number);
        if (target.GetQuestAbilityValue(learnedAbilityId) > 0)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightYellow}{target.Name} already knows {spell.Name} (#{spell.Number}).{MudAnsi.Reset}");
            return;
        }

        target.SetQuestAbilityValue(learnedAbilityId, 1);
        _world.PlayerRepo.SavePlayer(target);

        await _client.SendLineAsync($"{MudAnsi.BrightGreen}Granted {spell.Name} (#{spell.Number}) to {target.Name}.{MudAnsi.Reset}");
        if (!target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
            _world.SendToPlayer(target.Name, $"{MudAnsi.BrightYellow}The gods grant you knowledge of {spell.Name}.{MudAnsi.Reset}");
    }

    // Resolve a spell by numeric id or by name — matching the strongest rank across BOTH the short and
    // full name (so "harm" hits short "harm" exactly and "hells" prefix-hits "hellstorm"). Returns false
    // with the tied names in <paramref name="ambiguousNames"/> when two differently-named spells match
    // equally well, mirroring SYSOP GIVEITEM's disambiguation.
    private bool TryResolveSpellByNameOrId(string input, out GameSpell? spell, out List<string> ambiguousNames)
    {
        spell = null;
        ambiguousNames = [];

        if (int.TryParse(input, out int spellId))
        {
            _world.Database.Spells.TryGetValue(spellId, out spell);
            return spell != null;
        }

        GameSpell? best = null;
        var bestRank = TargetNameMatcher.MatchRank.None;
        var tiedNames = new List<string>();
        foreach (var candidate in _world.Database.Spells.Values)
        {
            var rank = (TargetNameMatcher.MatchRank)Math.Max(
                (int)TargetNameMatcher.GetMatchRank(candidate.Short, input),
                (int)TargetNameMatcher.GetMatchRank(candidate.Name, input));
            if (rank == TargetNameMatcher.MatchRank.None)
                continue;

            if (rank > bestRank)
            {
                bestRank = rank;
                best = candidate;
                tiedNames = [candidate.Name];
            }
            else if (rank == bestRank && !tiedNames.Contains(candidate.Name, StringComparer.OrdinalIgnoreCase))
            {
                tiedNames.Add(candidate.Name);
            }
        }

        if (best == null)
            return false;

        var distinct = tiedNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count > 1)
        {
            ambiguousNames = distinct;
            return false;
        }

        spell = best;
        return true;
    }

    private async Task HandleSysopLightning(string args)
    {
        var targetName = args.Trim();
        if (string.IsNullOrWhiteSpace(targetName))
        {
            await _client.SendLineAsync("SYSOP LIGHTNING <userid>");
            return;
        }

        var target = _world.FindOnlinePlayer(targetName);
        if (target == null)
        {
            await _client.SendLineAsync($"Cannot find user {targetName}");
            return;
        }

        if (target.Name.Equals(_player.Name, StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("You may not lightning that user.");
            return;
        }

        int damage = Random.Shared.Next(25, 121);
        target.CurrentHP -= damage;
        _world.PlayerRepo.SavePlayer(target);

        _world.BroadcastToRoom(target.CurrentMapNumber, target.CurrentRoomNumber,
            $"A bolt of lightning from the heavens strikes you for {damage} points damage!");
        _world.BroadcastToRoom(target.CurrentMapNumber, target.CurrentRoomNumber,
            $"A bolt of lightning just struck {target.Name}!");

        await _client.SendLineAsync("mmudreborn - Notice");
        await _client.SendLineAsync($"Lightning struck: {target.Name} by {_player.Name}");
    }

    private async Task HandleSysopReload()
    {
        await _client.SendLineAsync("INI Settings reloaded.");
    }

    private async Task HandleSysopInitialize(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out _))
        {
            await _client.SendLineAsync("Syntax: SYSOP INITIALIZE {rate} [RECOVER]");
            return;
        }

        bool recover = parts.Length > 1 && parts[1].Equals("recover", StringComparison.OrdinalIgnoreCase);
        _world.ReinitializeBuffers();
        _ = recover;
        await _client.SendLineAsync("Done");
    }

    // One-line explanation of every SYSOP CONFIGURE knob, shown by `SYSOP CONFIGURE HELP [setting]`.
    private static readonly (string Key, string Help)[] ConfigureHelpEntries =
    {
        ("PVPLEVEL", "PvP level range. A player may only attack (or be attacked by) others within this many character levels. Larger = more open PvP."),
        ("DEATH", "Death HP threshold -- the HP a character bottoms out at when killed (how far below 0 before they die). Stock -10."),
        ("MONSTERXP", "Monster experience multiplier. Every monster XP award is multiplied by this. 1 = normal/stock, 10 = 10x leveling."),
        ("REROLLXP", "Reroll XP retention (0-100). Percent of experience a character keeps when they reroll. 40 = keep 40%."),
        ("LEVELAHEAD", "Level-ahead cap. How many levels of XP a player may bank before further XP is refused until they train. 0 = OFF; max " + GameWorld.MaxLevelAheadCap + "."),
        ("GANGCOST", "Runic cost to create a gang."),
        ("GANGEXP", "Minimum total experience required to create a gang."),
        ("GANGHOUSEEXP", "Minimum total experience required to own a gang house."),
        ("GROUNDLIMIT", "Per-room ground capacity. ON (default/stock) enforces the stock 17 visible / 15 hidden item slots per room; a full room refuses the drop with \"There is no room to drop <item> here.\" and the item stays in inventory. OFF lifts the cap entirely (unlimited entries per room). Stacking is applied either way -- identical copies with no charge value share one slot, which is how stock counts them, not a limit. Corpse loot is unaffected either way: it spills to adjacent rooms and along the victim's trail rather than vanishing."),
        ("LIMITEDITEMS", "Limited-item mode. 0 = OFF (items spawn freely); 1 = ON (limited/unique items are capped realm-wide to their intended counts)."),
        ("MAXEPDAY", "Evil forgiveness: the MOST evil points one player can have forgiven per real-world day (a daily cap). 0 = forgiveness OFF."),
        ("EPCYCLE", "Evil forgiveness: how often (in minutes) the forgiveness tick runs for each player. 0 = OFF."),
        ("EPAMOUNT", "Evil forgiveness: how many evil points are removed each cycle. 0 = OFF."),
        ("GENRATE", "Monster generation rate (in seconds) -- how often the spawn driver runs. Stock 15; lower = faster respawns."),
        ("MINWAIT", "Default room/lair regen delay (in minutes), applied to rooms whose own Delay is 0. Stock 5."),
        ("BUBRADIUS", "Spawn-bubble radius (in rooms) -- how far around each player lairs are kept on the scheduled background-regen list. Default 10. Smaller = cheaper + tighter regen; larger = wider ambient population. Not a stock value."),
        ("EVILCAPBLOCK", "Evil-point cap action block. ON (stock): a player over the EP cap (300) is blocked from attacking innocents (townsfolk/guards) with 'progressed too far'. OFF (modern): the attack proceeds and only the EP gain is refused. Either way EP never grows past the cap."),
        ("SURPRISEROUND", "Surprise round for the NON-BACKSTAB-WEAPON case only (a sneaker using `backstab` with a weapon that can't backstab). ON: it gets a full surprise round — backstab DAMAGE + silent (no 'moves to attack' warning; victim learns when the hit lands). OFF: no surprise round — it is a plain NORMAL attack (normal damage, victim warned). REAL backstabs (backstab weapon / unarmed) always do the full silent surprise and are NOT affected by this. Default OFF."),
        ("QOLFUNCTIONS", "Master switch for the non-stock quality-of-life COMMANDS (get all, stat all, abil, room, hall, set look, home, web-who). OFF (default/stock): a player typing these gets the stock result (unknown command, or the compact/stock variant). ON: all are available. Per-command overrides (SYSOP CONFIGURE QOL <feature> on|off|auto) win over the master. Does NOT affect always-on conveniences (take/extinguish/ignore) or the bug tracker (its own switch)."),
        ("QOL", "Per-command override of the QOLFUNCTIONS master. SYSOP CONFIGURE QOL <feature> <on|off|auto>, where feature is getall|statall|abil|room|hall|setlook|home|webwho|qty|spells and auto = follow the master. e.g. run a mostly-stock realm but allow the web roster: QOLFUNCTIONS off + QOL webwho on. SYSOP CONFIGURE QOL (no args) shows the table."),
        ("BUGTRACKER", "Player bug-report tooling (bug/bugs/showbug/completebug/verifybug/stillbug/deletebug) — non-stock. ON (default): available. OFF: every bug verb is an unrecognized command. Independent of QOLFUNCTIONS (it's tooling, not a gameplay command)."),
        ("DEATHLOG", "Recent-death log -- NON-STOCK; the original records nothing about past deaths. OFF (default): nothing is tracked and nothing is shown. ON: each death records the time, map/room, room name and what killed you (a monster, a player, or a cause like poison or a trap), keeping the last 4, and they are listed at the bottom of `stat all`. Arena deaths that cost no life are not logged. Requires QOL statall to be enabled for players to see it."),
        ("DISCONNECTPENALTY", "Drop-carrier penalty. Stock defaults to HIGH; we default to NONE so it ships inert. HIGH = penalize EVERY lost connection. MEDIUM = only while in combat or engaged by another player. LOW = only while in PvP combat. NONE = never. The penalty is a random slice of MaxHP (see DISCONNECTHP) plus up to DISCONNECTITEMS items dropped on the floor, and it CAN kill — a player who drops while bleeding out dies. Loyal (ability 100) items are exempt. The victim is told on their next login: 'Last time you were on, you disconnected while playing.'"),
        ("DISCONNECTHP", "Drop-carrier penalty HP cost, as a percentage band of MaxHP: SYSOP CONFIGURE DISCONNECTHP <min> <max>. Stock rolls uniformly between the two. The stock values live in a board config file we do not have, so the 10/25 default is ours. Only applies when DISCONNECTPENALTY is not NONE."),
        ("DISCONNECTITEMS", "Drop-carrier penalty item cost: how many carried/worn items hit the floor. Default 3. Loyal (ability 100) items are never dropped, but a worn Loyal item still consumes a slot of the budget — a stock quirk of the hangup equipment loop. Only applies when DISCONNECTPENALTY is not NONE."),
        ("MINEPS", "Player-set minimum evil points -- non-stock. OFF (default/stock): evil-point forgiveness drifts every character down the scale toward Saint, so a player left scripting overnight loses their band (Outlaw -> Seedy) and update_allowed_worn_items strips the gear that band allowed. ON: a player may type SET MINEPS <amount> to pin a floor the forgiveness tick will not carry them below (SET MINEPS OFF clears it). It never GRANTS evil points -- the standing is still earned by doing the deeds -- and it clamps ONLY the passive drift: the stock FORGIVE refund, quest absolution and sysop changes still move a character freely. Turning this off ignores every stored floor without erasing it."),
        ("QUESTALLPARTY", "Main-quest party-drop convenience. OFF (stock): a main-quest item a monster DROPS on death (gleaming shard, elf-head, the severed heads, iron crown, obsidian talisman, red parchment, spectral webbing, locked wooden box) drops a SINGLE copy to the floor, so a party of N must kill the monster N times — one turn-in each. ON: one copy is placed in the inventory of EACH engaged player who is on that quest and hasn't yet passed the step that uses it (over-encumbrance falls to the floor); if no one in the fight needs it, the stock single floor drop happens instead. Only the curated main-quest ground-drop items are affected; normal loot is unchanged. (The other 'received when you kill' quest items — Eternal Fire, Storm Spirit, Heartstone, etc. — already deliver room-wide via the boss's death-spell and are unaffected.)"),
        ("SELLWORN", "Selling worn gear. OFF (default): SELL and APPRAISE only look at items in your pack, so gear you are wearing can't be sold (remove it first). ON (stock): SELL/APPRAISE also match worn gear, which counts toward 'Please be more specific' and can be sold straight off your body — except a cursed item you are wearing, which can't be sold unless you carry another copy (\"You may not sell that item!\")."),
    };

    private const string ConfigureEpForgivenessNote =
        "Note: MAXEPDAY, EPCYCLE and EPAMOUNT must ALL be > 0 for evil points to decay.\n A player who plays clean has EvilPoints reduced by EPAMOUNT every EPCYCLE\n minutes online (up to MAXEPDAY/day), drifting Neutral -> Good -> Saint all the way down to the -200 floor.";

    // The QOL commands gated by SYSOP CONFIGURE QOLFUNCTIONS / QOL <feature>. Token = the word a sysop types;
    // Label = how it reads in output (the command as a player types it).
    private static readonly (QolFeature Feature, string Label, string Token)[] QolFeatureRows =
    {
        (QolFeature.GetAll, "get all", "getall"),
        (QolFeature.StatAll, "stat all [<monster>]", "statall"),
        (QolFeature.Abilities, "abil", "abil"),
        (QolFeature.Room, "room", "room"),
        (QolFeature.Hall, "hall", "hall"),
        (QolFeature.SetLook, "set look", "setlook"),
        (QolFeature.Home, "home", "home"),
        (QolFeature.WhoWeb, "web-who", "webwho"),
        (QolFeature.BulkQuantity, "<n> on get/drop/give/buy/sell/hide", "qty"),
        (QolFeature.SpellRemoves, "spells: remove + apply", "spells"),
    };

    private static bool TryParseQolFeature(string token, out QolFeature feature)
    {
        token = token.ToLowerInvariant();
        foreach (var row in QolFeatureRows)
        {
            if (token == row.Token || token == row.Label.Replace(" ", "", StringComparison.Ordinal))
            {
                feature = row.Feature;
                return true;
            }
        }
        // A couple of friendly aliases.
        switch (token)
        {
            case "abilities": feature = QolFeature.Abilities; return true;
            case "halloffame": feature = QolFeature.Hall; return true;
            case "look": feature = QolFeature.SetLook; return true;
            // Back-compat: the command was renamed who-web → web-who (MegaMud lag on "who…" verbs), but
            // keep the old QOL tokens resolving so existing sysop config/muscle-memory still works.
            case "who-web":
            case "whoweb": feature = QolFeature.WhoWeb; return true;
            case "bulk":
            case "quantity":
            case "bulkquantity": feature = QolFeature.BulkQuantity; return true;
        }
        feature = default;
        return false;
    }

    private static string QolFeatureLabel(QolFeature feature)
    {
        foreach (var row in QolFeatureRows)
            if (row.Feature == feature)
                return row.Label;
        return feature.ToString();
    }

    private static string QolOverrideToken(QolOverride ov) => ov switch
    {
        QolOverride.ForceOn => "on",
        QolOverride.ForceOff => "off",
        _ => "auto",
    };

    private async Task ShowQolStateAsync()
    {
        await _client.SendLineAsync($"[SYSOP CONFIGURE QOL]  master QOLFUNCTIONS: {(_world.QolFunctionsEnabled ? "ON" : "OFF")}");
        foreach (var row in QolFeatureRows)
        {
            await _client.SendLineAsync(
                $"  {row.Token,-8} ({row.Label,-8})  override={QolOverrideToken(_world.GetQolOverride(row.Feature)),-4}  effective={(_world.IsQolEnabled(row.Feature) ? "ON" : "OFF")}");
        }
        await _client.SendLineAsync("Set: SYSOP CONFIGURE QOLFUNCTIONS <on|off>   |   SYSOP CONFIGURE QOL <feature> <on|off|auto>  (auto = follow the master)");
    }

    private async Task HandleSysopConfigure(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            await _client.SendLineAsync("[SYSOP CONFIGURE]");
            await _client.SendLineAsync($"  PVP LEVEL RANGE: {_world.PvpLevelRange}");
            await _client.SendLineAsync($"  DEATH SETTING: {_world.DeathSetting}");
            await _client.SendLineAsync($"  MONSTER XP RATE: {_world.MonsterExperienceRate}x");
            await _client.SendLineAsync($"  REROLLXP KEEP: {_world.RerollKeepExperiencePercent}%");
            await _client.SendLineAsync($"  LEVELAHEAD: {FormatLevelAheadCap(_world.LevelAheadCap)}");
            await _client.SendLineAsync($"  GANG COST RUNIC: {_world.GangCreateRunicCost}");
            await _client.SendLineAsync($"  GANG MIN EXP: {_world.GangCreateMinimumExperience}");
            await _client.SendLineAsync($"  GANG HOUSE MIN EXP: {_world.GangHouseMinimumExperience}");
            await _client.SendLineAsync($"  LIMITED ITEMS: {_world.LimitedItemsMode}");
            await _client.SendLineAsync($"  GROUND LIMIT: {(_world.GroundItemLimitEnabled ? "ON" : "OFF")}");
            await _client.SendLineAsync($"  MAXEPDAY: {_world.MaxEvilPointsForgivenPerDay}");
            await _client.SendLineAsync($"  EPCYCLE: {_world.EvilPointForgivenessCycleMinutes}");
            await _client.SendLineAsync($"  EPAMOUNT: {_world.EvilPointForgivenessAmount}");
            await _client.SendLineAsync($"  GENRATE: {_world.MonsterGenerationRateSeconds} seconds (monster generation rate)");
            await _client.SendLineAsync($"  MINWAIT: {_world.LairRegenDefaultMinutes} minutes (default room/lair regen delay)");
            await _client.SendLineAsync($"  BUBRADIUS: {_world.ActiveLairSpawnRoomRadius} rooms (spawn-bubble BFS radius)");
            await _client.SendLineAsync($"  EVILCAPBLOCK: {(_world.EvilCapBlocksActions ? "ON" : "OFF")} (block maxed-evil players from attacking innocents)");
            await _client.SendLineAsync($"  SURPRISEROUND: {(_world.SurpriseRoundEnabled ? "ON (non-bs weapon: backstab-damage silent surprise)" : "OFF (non-bs weapon: plain normal attack)")} (real backstabs always silent)");
            await _client.SendLineAsync($"  QUESTALLPARTY: {(_world.QuestDropToAllParty ? "ON" : "OFF")} (main-quest ground-drop items go to each engaged party member on that quest step, not one to the floor)");
            await _client.SendLineAsync($"  SELLWORN: {(_world.SellWornEnabled ? "ON" : "OFF")} (SELL/APPRAISE may match gear you are wearing; ON is stock)");
            await _client.SendLineAsync($"  MINEPS: {(_world.MinEvilPointsEnabled ? "ON" : "OFF")} (players may SET MINEPS <amount> to floor their evil-point forgiveness)");
            await _client.SendLineAsync($"  DEATHLOG: {(_world.DeathLogEnabled ? "ON" : "OFF")} (track the last {Player.MaxDeathLogEntries} deaths + killer, shown on `stat all`; non-stock)");
            await _client.SendLineAsync($"  DISCONNECTPENALTY: {GameWorld.DescribeDisconnectPenaltyLevel(_world.DisconnectPenaltyLevel)} (stock default HIGH; ours NONE) — cost {_world.DisconnectPenaltyMinHpPercent}-{_world.DisconnectPenaltyMaxHpPercent}% MaxHP + up to {_world.DisconnectPenaltyMaxItemsDropped} items");
            await _client.SendLineAsync($"  QOLFUNCTIONS: {(_world.QolFunctionsEnabled ? "ON" : "OFF")} (master switch for non-stock convenience commands — see SYSOP CONFIGURE QOL for per-command overrides)");
            await _client.SendLineAsync($"  BUGTRACKER: {(_world.BugTrackerEnabled ? "ON" : "OFF")} (player bug-report tooling; non-stock)");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE PVPLEVEL <levels>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DEATH <minimum-hp>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MONSTERXP <multiplier>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE REROLLXP <0-100>");
            await _client.SendLineAsync($"Syntax: SYSOP CONFIGURE LEVELAHEAD <0-{GameWorld.MaxLevelAheadCap}>  (0 = OFF)");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGCOST <runic>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGEXP <experience>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGHOUSEEXP <experience>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE LIMITEDITEMS <0|1>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MAXEPDAY <amount>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EPCYCLE <minutes>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EPAMOUNT <amount>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GENRATE <seconds>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MINWAIT <minutes>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE BUBRADIUS <rooms>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EVILCAPBLOCK <on|off>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE SURPRISEROUND <on|off>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QUESTALLPARTY <on|off>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE SELLWORN <on|off>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MINEPS <on|off>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QOLFUNCTIONS <on|off> (master switch for QOL commands)");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QOL [getall|statall|abil|room|hall|setlook|home|webwho|qty|spells] <on|off|auto>");
            await _client.SendLineAsync("Syntax: SYSOP CONFIGURE BUGTRACKER <on|off>");
            await _client.SendLineAsync("Type SYSOP CONFIGURE HELP [setting] for an explanation of each.");
            return;
        }

        var setting = tokens[0].ToLowerInvariant();

        if (setting is "help" or "?" or "explain")
        {
            string? specific = tokens.Length > 1 ? tokens[1] : null;
            await _client.SendLineAsync("[SYSOP CONFIGURE HELP]");

            bool matched = false;
            foreach (var (key, help) in ConfigureHelpEntries)
            {
                if (specific != null && !key.Equals(specific, StringComparison.OrdinalIgnoreCase))
                    continue;
                await _client.SendLineAsync($"  {key} -- {help}");
                matched = true;
            }

            if (!matched)
            {
                await _client.SendLineAsync($"  Unknown setting '{specific}'. Type SYSOP CONFIGURE HELP for the full list.");
                return;
            }

            bool showEpNote = specific == null
                || specific.Equals("MAXEPDAY", StringComparison.OrdinalIgnoreCase)
                || specific.Equals("EPCYCLE", StringComparison.OrdinalIgnoreCase)
                || specific.Equals("EPAMOUNT", StringComparison.OrdinalIgnoreCase);
            if (showEpNote)
                await _client.SendLineAsync($"  {ConfigureEpForgivenessNote}");

            return;
        }

        if (setting is "genrate" or "monstergen" or "spawnrate")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"GENRATE is currently {_world.MonsterGenerationRateSeconds} seconds.");
                return;
            }

            if (!int.TryParse(tokens[1], out var seconds) || seconds < 1)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GENRATE <seconds>  (how often monsters generate; stock 15)");
                return;
            }

            _world.SetMonsterGenerationRateSeconds(seconds);
            await _client.SendLineAsync($"GENRATE set to {_world.MonsterGenerationRateSeconds} seconds.");
            return;
        }

        if (setting is "minwait" or "regenminutes" or "lairwait")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"MINWAIT is currently {_world.LairRegenDefaultMinutes} minutes.");
                return;
            }

            if (!int.TryParse(tokens[1], out var minutes) || minutes < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MINWAIT <minutes>  (default room/lair regen delay; stock 5)");
                return;
            }

            _world.SetLairRegenDefaultMinutes(minutes);
            await _client.SendLineAsync($"MINWAIT set to {_world.LairRegenDefaultMinutes} minutes (applies to rooms whose own Delay is 0).");
            return;
        }

        if (setting is "bubradius" or "spawnradius" or "bubble")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"BUBRADIUS is currently {_world.ActiveLairSpawnRoomRadius} rooms.");
                return;
            }

            if (!int.TryParse(tokens[1], out var radius) || radius < 1)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE BUBRADIUS <rooms>  (spawn-bubble BFS radius; default 10, max 60)");
                return;
            }

            _world.SetActiveLairSpawnRoomRadius(radius);
            await _client.SendLineAsync($"BUBRADIUS set to {_world.ActiveLairSpawnRoomRadius} rooms (rebuilt every online player's bubble).");
            return;
        }

        if (setting is "deathlog" or "deaths" or "deathrooms")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"DEATHLOG is currently {(_world.DeathLogEnabled ? "ON" : "OFF")}.");
                return;
            }

            var deathLogValue = tokens[1].ToLowerInvariant();
            bool? deathLogEnabled = deathLogValue is "1" or "on" or "true" or "yes" ? true
                                  : deathLogValue is "0" or "off" or "false" or "no" ? false
                                  : null;
            if (deathLogEnabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DEATHLOG <on|off>  (track the last deaths + killer on `stat all`; non-stock, default OFF)");
                return;
            }

            _world.SetDeathLogEnabled(deathLogEnabled.Value);
            await _client.SendLineAsync($"DEATHLOG set to {(_world.DeathLogEnabled ? "ON" : "OFF")}.");
            return;
        }

        if (setting is "disconnectpenalty" or "droppenalty" or "hangup")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"DISCONNECTPENALTY is currently {GameWorld.DescribeDisconnectPenaltyLevel(_world.DisconnectPenaltyLevel)}.");
                await _client.SendLineAsync($"  cost: {_world.DisconnectPenaltyMinHpPercent}-{_world.DisconnectPenaltyMaxHpPercent}% of MaxHP, up to {_world.DisconnectPenaltyMaxItemsDropped} items dropped");
                return;
            }

            if (!GameWorld.TryParseDisconnectPenaltyLevel(tokens[1], out int level))
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DISCONNECTPENALTY <high|medium|low|none>");
                await _client.SendLineAsync("  high = every lost connection | medium = in combat or engaged | low = PvP only | none = never (default)");
                return;
            }

            _world.SetDisconnectPenaltyLevel(level);
            await _client.SendLineAsync($"DISCONNECTPENALTY set to {GameWorld.DescribeDisconnectPenaltyLevel(_world.DisconnectPenaltyLevel)}.");
            return;
        }

        if (setting is "disconnecthp" or "disconnecthploss")
        {
            if (tokens.Length < 3
                || !int.TryParse(tokens[1], out int minPercent)
                || !int.TryParse(tokens[2], out int maxPercent))
            {
                await _client.SendLineAsync($"DISCONNECTHP is currently {_world.DisconnectPenaltyMinHpPercent}-{_world.DisconnectPenaltyMaxHpPercent}% of MaxHP.");
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DISCONNECTHP <min-percent> <max-percent>");
                return;
            }

            _world.SetDisconnectPenaltyHpPercents(minPercent, maxPercent);
            await _client.SendLineAsync($"DISCONNECTHP set to {_world.DisconnectPenaltyMinHpPercent}-{_world.DisconnectPenaltyMaxHpPercent}% of MaxHP.");
            return;
        }

        if (setting is "disconnectitems" or "disconnectdrop")
        {
            if (tokens.Length < 2 || !int.TryParse(tokens[1], out int maxItems))
            {
                await _client.SendLineAsync($"DISCONNECTITEMS is currently {_world.DisconnectPenaltyMaxItemsDropped}.");
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DISCONNECTITEMS <count>");
                return;
            }

            _world.SetDisconnectPenaltyMaxItemsDropped(maxItems);
            await _client.SendLineAsync($"DISCONNECTITEMS set to {_world.DisconnectPenaltyMaxItemsDropped}.");
            return;
        }

        if (setting is "evilcapblock" or "epcapblock" or "evilblock")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"EVILCAPBLOCK is currently {(_world.EvilCapBlocksActions ? "ON" : "OFF")}.");
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? enabled = val is "1" or "on" or "true" or "yes" ? true
                          : val is "0" or "off" or "false" or "no" ? false
                          : null;
            if (enabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EVILCAPBLOCK <on|off>  (block maxed-evil players from attacking innocents; stock ON)");
                return;
            }

            _world.SetEvilCapBlocksActions(enabled.Value);
            await _client.SendLineAsync($"EVILCAPBLOCK set to {(_world.EvilCapBlocksActions ? "ON (stock — block the action at the EP cap)" : "OFF (modern — action proceeds, EP gain still capped)")}.");
            return;
        }

        if (setting is "surpriseround" or "showsurprise" or "showround")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"SURPRISEROUND is currently {(_world.SurpriseRoundEnabled ? "ON" : "OFF")}.");
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? enabled = val is "1" or "on" or "true" or "yes" ? true
                          : val is "0" or "off" or "false" or "no" ? false
                          : null;
            if (enabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE SURPRISEROUND <on|off>  (ON: non-backstab weapon gets a backstab-damage silent surprise round; OFF: it's a plain normal attack. Real backstabs always silent.)");
                return;
            }

            _world.SetSurpriseRoundEnabled(enabled.Value);
            await _client.SendLineAsync($"SURPRISEROUND set to {(_world.SurpriseRoundEnabled ? "ON (non-backstab weapon → backstab-damage silent surprise round)" : "OFF (non-backstab weapon → plain normal attack: normal damage, victim warned)")}. Real backstabs are always silent.");
            return;
        }
        if (setting is "mineps" or "minevilpoints" or "minep")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"MINEPS is currently {(_world.MinEvilPointsEnabled ? "ON" : "OFF")}.");
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? enabled = val is "1" or "on" or "true" or "yes" ? true
                          : val is "0" or "off" or "false" or "no" ? false
                          : null;
            if (enabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MINEPS <on|off>  (let players floor their own evil-point forgiveness with SET MINEPS; default OFF/stock)");
                return;
            }

            _world.SetMinEvilPointsEnabled(enabled.Value);
            await _client.SendLineAsync($"MINEPS set to {(_world.MinEvilPointsEnabled ? "ON (players may SET MINEPS <amount>; forgiveness stops at their floor)" : "OFF (stock — forgiveness drifts everyone toward Saint; stored floors are ignored, not erased)")}.");
            return;
        }
        if (setting is "questallparty" or "questparty" or "questdropall")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"QUESTALLPARTY is currently {(_world.QuestDropToAllParty ? "ON" : "OFF")}.");
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? enabled = val is "1" or "on" or "true" or "yes" ? true
                          : val is "0" or "off" or "false" or "no" ? false
                          : null;
            if (enabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QUESTALLPARTY <on|off>  (give a main-quest ground-drop item to each engaged party member; default OFF/stock)");
                return;
            }

            _world.SetQuestDropToAllParty(enabled.Value);
            await _client.SendLineAsync($"QUESTALLPARTY set to {(_world.QuestDropToAllParty ? "ON (each engaged party member receives the quest drop in inventory)" : "OFF (stock — a single copy drops to the ground)")}.");
            return;
        }
        if (setting is "sellworn")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"SELLWORN is currently {(_world.SellWornEnabled ? "ON" : "OFF")}.");
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? enabled = val is "1" or "on" or "true" or "yes" ? true
                          : val is "0" or "off" or "false" or "no" ? false
                          : null;
            if (enabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE SELLWORN <on|off>  (let SELL/APPRAISE match worn gear; ON is stock, default OFF)");
                return;
            }

            _world.SetSellWornEnabled(enabled.Value);
            await _client.SendLineAsync($"SELLWORN set to {(_world.SellWornEnabled ? "ON (stock — worn gear can be sold)" : "OFF (worn gear must be removed before it can be sold)")}.");
            return;
        }
        if (setting is "qolfunctions" or "qolmaster")
        {
            if (tokens.Length == 1)
            {
                await ShowQolStateAsync();
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? master = val is "1" or "on" or "true" or "yes" ? true
                         : val is "0" or "off" or "false" or "no" ? false
                         : null;
            if (master is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QOLFUNCTIONS <on|off>  (master switch for the QOL commands; default OFF/stock)");
                return;
            }

            _world.SetQolFunctionsEnabled(master.Value);
            await _client.SendLineAsync($"QOLFUNCTIONS master set to {(_world.QolFunctionsEnabled ? "ON" : "OFF")}. Per-command overrides still win where set.");
            await ShowQolStateAsync();
            return;
        }
        if (setting is "qol")
        {
            if (tokens.Length == 1)
            {
                await ShowQolStateAsync();
                return;
            }
            if (!TryParseQolFeature(tokens[1], out var feature))
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QOL [getall|statall|abil|room|hall|setlook|home|webwho|qty|spells] <on|off|auto>");
                return;
            }
            if (tokens.Length < 3)
            {
                await _client.SendLineAsync($"QOL {QolFeatureLabel(feature)}: override={QolOverrideToken(_world.GetQolOverride(feature))}, effective={(_world.IsQolEnabled(feature) ? "ON" : "OFF")}.");
                return;
            }

            var ovToken = tokens[2].ToLowerInvariant();
            QolOverride? ov = ovToken is "on" or "1" or "true" or "yes" or "forceon" ? QolOverride.ForceOn
                            : ovToken is "off" or "0" or "false" or "no" or "forceoff" ? QolOverride.ForceOff
                            : ovToken is "auto" or "inherit" or "default" or "clear" ? QolOverride.Inherit
                            : null;
            if (ov is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE QOL <feature> <on|off|auto>  (auto = follow the QOLFUNCTIONS master)");
                return;
            }

            _world.SetQolOverride(feature, ov.Value);
            await _client.SendLineAsync($"QOL {QolFeatureLabel(feature)} override set to {QolOverrideToken(ov.Value)} (effective: {(_world.IsQolEnabled(feature) ? "ON" : "OFF")}).");
            return;
        }
        if (setting is "bugtracker" or "bugtrack")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"BUGTRACKER is currently {(_world.BugTrackerEnabled ? "ON" : "OFF")}.");
                return;
            }

            var val = tokens[1].ToLowerInvariant();
            bool? enabled = val is "1" or "on" or "true" or "yes" ? true
                          : val is "0" or "off" or "false" or "no" ? false
                          : null;
            if (enabled is null)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE BUGTRACKER <on|off>  (player bug-report tooling; default ON)");
                return;
            }

            _world.SetBugTrackerEnabled(enabled.Value);
            await _client.SendLineAsync($"BUGTRACKER set to {(_world.BugTrackerEnabled ? "ON (bug/bugs/showbug/… available)" : "OFF (all bug verbs unrecognized)")}.");
            return;
        }
        if (setting is "pvp" or "pvplevel" or "pvplevels")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"PVP LEVEL RANGE is currently {_world.PvpLevelRange}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var levels) || levels < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE PVPLEVEL <levels>");
                return;
            }

            _world.SetPvpLevelRange(levels);
            await _client.SendLineAsync($"PVP LEVEL RANGE set to {_world.PvpLevelRange}.");
            return;
        }

        if (setting is "maxepday" or "epday")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"MAXEPDAY is currently {_world.MaxEvilPointsForgivenPerDay}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var amount) || amount < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MAXEPDAY <amount>");
                return;
            }

            _world.SetMaxEvilPointsForgivenPerDay(amount);
            await _client.SendLineAsync($"MAXEPDAY set to {_world.MaxEvilPointsForgivenPerDay}.");
            return;
        }

        if (setting is "epcycle")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"EPCYCLE is currently {_world.EvilPointForgivenessCycleMinutes}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var minutes) || minutes < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EPCYCLE <minutes>");
                return;
            }

            _world.SetEvilPointForgivenessCycleMinutes(minutes);
            await _client.SendLineAsync($"EPCYCLE set to {_world.EvilPointForgivenessCycleMinutes}.");
            return;
        }

        if (setting is "epamount")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"EPAMOUNT is currently {_world.EvilPointForgivenessAmount}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var amount) || amount < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EPAMOUNT <amount>");
                return;
            }

            _world.SetEvilPointForgivenessAmount(amount);
            await _client.SendLineAsync($"EPAMOUNT set to {_world.EvilPointForgivenessAmount}.");
            return;
        }

        if (setting is "cleanup" or "cleanuptime")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"CLEANUP TIME is currently {_world.DailyCleanupTime:HH:mm}.");
                return;
            }

            if (!TimeOnly.TryParse(tokens[1], out var cleanupTime))
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE CLEANUP <HH:MM>  (e.g. 04:00)");
                return;
            }

            _world.SetDailyCleanupTime(cleanupTime);
            await _client.SendLineAsync($"CLEANUP TIME set to {_world.DailyCleanupTime:HH:mm}.");
            return;
        }

        if (setting is "death" or "deathhp" or "deathsetting")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"DEATH SETTING is currently {_world.DeathSetting}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var minimumHp) || minimumHp == 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DEATH <minimum-hp>");
                return;
            }

            _world.SetDeathSetting(minimumHp);
            await _client.SendLineAsync($"DEATH SETTING set to {_world.DeathSetting}.");
            return;
        }

        if (setting is "monsterxp" or "monsterexprate" or "exprate" or "xprate")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"MONSTER XP RATE is currently {_world.MonsterExperienceRate}x.");
                return;
            }

            if (!int.TryParse(tokens[1], out var multiplier) || multiplier < 1)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MONSTERXP <multiplier>");
                return;
            }

            _world.SetMonsterExperienceRate(multiplier);
            await _client.SendLineAsync($"MONSTER XP RATE set to {_world.MonsterExperienceRate}x.");
            return;
        }

        if (setting is "rerollxp" or "keepxp" or "reroll")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"REROLLXP KEEP is currently {_world.RerollKeepExperiencePercent}%.");
                return;
            }

            if (!int.TryParse(tokens[1], out var percent) || percent < 0 || percent > 100)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE REROLLXP <0-100>");
                return;
            }

            _world.SetRerollKeepExperiencePercent(percent);
            await _client.SendLineAsync($"REROLLXP KEEP set to {_world.RerollKeepExperiencePercent}%.");
            return;
        }

        if (setting is "levelahead" or "levelcap" or "expcap")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"LEVELAHEAD is currently {FormatLevelAheadCap(_world.LevelAheadCap)}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var levelsAhead) || levelsAhead < 0 || levelsAhead > GameWorld.MaxLevelAheadCap)
            {
                await _client.SendLineAsync($"Syntax: SYSOP CONFIGURE LEVELAHEAD <0-{GameWorld.MaxLevelAheadCap}>  (0 = OFF)");
                return;
            }

            _world.SetLevelAheadCap(levelsAhead);
            await _client.SendLineAsync($"LEVELAHEAD set to {FormatLevelAheadCap(_world.LevelAheadCap)}.");
            return;
        }

        if (setting is "gangcost" or "gangrunic" or "creategangcost")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"Gang creation cost is currently {_world.GangCreateRunicCost} runic.");
                return;
            }

            if (!int.TryParse(tokens[1], out var runic) || runic < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGCOST <runic>");
                return;
            }

            _world.SetGangCreateRunicCost(runic);
            await _client.SendLineAsync($"Gang creation runic cost set to {_world.GangCreateRunicCost}.");
            return;
        }

        if (setting is "gangexp" or "gangexperience" or "creategangexp")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"Gang creation minimum experience is currently {_world.GangCreateMinimumExperience}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var requiredExp) || requiredExp < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGEXP <experience>");
                return;
            }

            _world.SetGangCreateMinimumExperience(requiredExp);
            await _client.SendLineAsync($"Gang creation minimum experience set to {_world.GangCreateMinimumExperience}.");
            return;
        }

        if (setting is "ganghouseexp" or "ganghouseexperience" or "deedexp")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"Gang house deed minimum experience is currently {_world.GangHouseMinimumExperience}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var requiredExp) || requiredExp < 0)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGHOUSEEXP <experience>");
                return;
            }

            _world.SetGangHouseMinimumExperience(requiredExp);
            await _client.SendLineAsync($"Gang house deed minimum experience set to {_world.GangHouseMinimumExperience}.");
            return;
        }

        if (setting is "limiteditems" or "limiteditem")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"LIMITED ITEMS is currently {_world.LimitedItemsMode}.");
                return;
            }

            if (!int.TryParse(tokens[1], out var mode) || mode < 0 || mode > 1)
            {
                await _client.SendLineAsync("Syntax: SYSOP CONFIGURE LIMITEDITEMS <0|1>");
                return;
            }

            _world.SetLimitedItemsMode(mode);
            await _client.SendLineAsync($"LIMITED ITEMS set to {_world.LimitedItemsMode}.");
            return;
        }

        if (setting is "groundlimit" or "groundlimits")
        {
            if (tokens.Length == 1)
            {
                await _client.SendLineAsync($"GROUND LIMIT is currently {(_world.GroundItemLimitEnabled ? "ON" : "OFF")}"
                    + $" ({GameWorld.StockVisibleGroundSlots} visible / {GameWorld.StockHiddenGroundSlots} hidden slots per room).");
                return;
            }

            bool groundLimitEnabled;
            switch (tokens[1].ToLowerInvariant())
            {
                case "on":
                case "1":
                    groundLimitEnabled = true;
                    break;
                case "off":
                case "0":
                    groundLimitEnabled = false;
                    break;
                default:
                    await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GROUNDLIMIT <on|off>");
                    return;
            }

            _world.SetGroundItemLimitEnabled(groundLimitEnabled);
            await _client.SendLineAsync($"GROUND LIMIT set to {(_world.GroundItemLimitEnabled ? "ON" : "OFF")}.");
            return;
        }

        await _client.SendLineAsync("[SYSOP CONFIGURE]");
        await _client.SendLineAsync($"  PVP LEVEL RANGE: {_world.PvpLevelRange}");
        await _client.SendLineAsync($"  DEATH SETTING: {_world.DeathSetting}");
        await _client.SendLineAsync($"  MONSTER XP RATE: {_world.MonsterExperienceRate}x");
        await _client.SendLineAsync($"  REROLLXP KEEP: {_world.RerollKeepExperiencePercent}%");
        await _client.SendLineAsync($"  LEVELAHEAD: {FormatLevelAheadCap(_world.LevelAheadCap)}");
        await _client.SendLineAsync($"  GANG COST RUNIC: {_world.GangCreateRunicCost}");
        await _client.SendLineAsync($"  GANG MIN EXP: {_world.GangCreateMinimumExperience}");
        await _client.SendLineAsync($"  LIMITED ITEMS: {_world.LimitedItemsMode}");
        await _client.SendLineAsync($"  GROUND LIMIT: {(_world.GroundItemLimitEnabled ? "ON" : "OFF")}");
        await _client.SendLineAsync($"  MAXEPDAY: {_world.MaxEvilPointsForgivenPerDay}");
        await _client.SendLineAsync($"  EPCYCLE: {_world.EvilPointForgivenessCycleMinutes}");
        await _client.SendLineAsync($"  EPAMOUNT: {_world.EvilPointForgivenessAmount}");
        await _client.SendLineAsync($"  CLEANUP TIME: {_world.DailyCleanupTime:HH:mm}");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE PVPLEVEL <levels>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE DEATH <minimum-hp>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MONSTERXP <multiplier>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE REROLLXP <0-100>");
        await _client.SendLineAsync($"Syntax: SYSOP CONFIGURE LEVELAHEAD <0-{GameWorld.MaxLevelAheadCap}>  (0 = OFF)");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGCOST <runic>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GANGEXP <experience>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE LIMITEDITEMS <0|1>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE GROUNDLIMIT <on|off>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE MAXEPDAY <amount>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EPCYCLE <minutes>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE EPAMOUNT <amount>");
        await _client.SendLineAsync("Syntax: SYSOP CONFIGURE CLEANUP <HH:MM>");
    }

    private static string FormatLevelAheadCap(int cap)
        => cap <= 0 ? "OFF" : $"{cap} level(s)";

    private async Task HandleSysopReset(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await _client.SendLineAsync("Syntax: SYSOP RESET <room/area>");
            return;
        }

        if (parts[0].Equals("room", StringComparison.OrdinalIgnoreCase))
        {
            int room = _player.CurrentRoomNumber;
            int map = _player.CurrentMapNumber;
            if (parts.Length > 1 && !int.TryParse(parts[1], out room))
            {
                await _client.SendLineAsync("Syntax: SYSOP RESET <room/area>");
                return;
            }
            if (parts.Length > 2 && !int.TryParse(parts[2], out map))
            {
                await _client.SendLineAsync("Syntax: SYSOP RESET <room/area>");
                return;
            }

            int spawned = _world.ResetMonsterGenerationRoom(map, room);
            _ = spawned;
            await _client.SendLineAsync("Done");
            return;
        }

        if (parts[0].Equals("area", StringComparison.OrdinalIgnoreCase))
        {
            int map = _player.CurrentMapNumber;
            if (parts.Length > 1 && !int.TryParse(parts[1], out map))
            {
                await _client.SendLineAsync("Syntax: SYSOP RESET <room/area>");
                return;
            }

            int affected = _world.ResetMonsterGenerationArea(map);
            _ = affected;
            await _client.SendLineAsync("Done");
            return;
        }

        await _client.SendLineAsync("Syntax: SYSOP RESET <room/area>");
    }

    private async Task HandleSysopDisband(string args)
    {
        var gangName = args.Trim();
        if (string.IsNullOrWhiteSpace(gangName))
        {
            await _client.SendLineAsync("Syntax: SYSOP DISBAND <gangname>");
            return;
        }

        int affected = _world.PlayerRepo.DisbandGang(gangName);
        if (affected == 0)
        {
            await _client.SendLineAsync($"Gang {gangName} not found.");
            return;
        }

        foreach (var p in _world.GetAllOnlinePlayers().Where(p => p.Gang.Equals(gangName, StringComparison.OrdinalIgnoreCase)))
        {
            p.Gang = string.Empty;
            p.GangExperience = 0;
        }

        await _client.SendLineAsync($"Gang {gangName} has been disbanded and will be deleted at cleanup.");
    }

    private async Task HandleSysopRenameGang(string args)
    {
        var parts = args.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("gang", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync("Syntax: SYSOP RENAME GANG <oldname> <newname>");
            return;
        }

        string oldName = parts[1].Trim();
        string newName = parts[2].Trim();

        if (!IsValidGangName(newName, out var reason))
        {
            await _client.SendLineAsync(reason);
            return;
        }

        if (!_world.PlayerRepo.RenameGang(oldName, newName))
        {
            await _client.SendLineAsync("Gang rename failed. Verify old name exists and new name is unused.");
            return;
        }

        foreach (var online in _world.GetAllOnlinePlayers().Where(p => p.Gang.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
            online.Gang = newName;

        await _client.SendLineAsync($"Gang {oldName} renamed to {newName}.");
    }

    private async Task HandleSysopDisableGang(string args)
    {
        var gangName = args.Trim();
        if (string.IsNullOrWhiteSpace(gangName))
        {
            await _client.SendLineAsync("Syntax: SYSOP DISABLE <gangname>");
            return;
        }

        _world.PlayerRepo.SetGangToptenDisabled(gangName, true);
        await _client.SendLineAsync($"Gang {gangName} has been disabled and will not appear on the top gangs.");
    }

    private async Task HandleSysopEnableGang(string args)
    {
        var gangName = args.Trim();
        if (string.IsNullOrWhiteSpace(gangName))
        {
            await _client.SendLineAsync("Syntax: SYSOP ENABLE <gangname>");
            return;
        }

        _world.PlayerRepo.SetGangToptenDisabled(gangName, false);
        await _client.SendLineAsync($"Gang {gangName} has been enabled and will appear on the top gangs listing again.");
    }

    private async Task HandleSysopGangSize(string args)
    {
        var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[0], out int size))
        {
            await _client.SendLineAsync("Syntax: SYSOP GANGSIZE <n> <gangname>");
            return;
        }

        if (size == 0)
        {
            await _client.SendLineAsync("You cannot set the gangsize to 0");
            return;
        }

        if (size < 0)
        {
            await _client.SendLineAsync("Syntax: SYSOP GANGSIZE <n> <gangname>");
            return;
        }

        string gangName = parts[1].Trim();
        _world.PlayerRepo.SetGangMaxSize(gangName, size);
        await _client.SendLineAsync($"Gang {gangName} has been resized to {size}.");
    }

    private async Task HandleSysopArena(string args)
    {
        var mode = args.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(mode) || mode == "status")
        {
            if (_world.ArenaCombatMode)
                await _client.SendLineAsync("Arena rooms are in combat mode (death DOES count)");
            else
                await _client.SendLineAsync("Arena rooms are in normal mode (death does not count)");
            return;
        }

        if (mode == "normal")
        {
            if (!_world.ArenaCombatMode)
            {
                await _client.SendLineAsync("Arena rooms are already in normal mode.");
                return;
            }
            if (_world.AnyPlayersInArenaRooms())
            {
                await _client.SendLineAsync("You cannot switch arena mode while users are currently in arena rooms!");
                return;
            }
            _world.SetArenaCombatMode(false);
            await _client.SendLineAsync("Arena rooms are now in normal mode (death does not count)");
            return;
        }

        if (mode == "combat")
        {
            if (_world.ArenaCombatMode)
            {
                await _client.SendLineAsync("Arena rooms are already in combat mode.");
                return;
            }
            if (_world.AnyPlayersInArenaRooms())
            {
                await _client.SendLineAsync("You cannot switch arena mode while users are currently in arena rooms!");
                return;
            }
            _world.SetArenaCombatMode(true);
            await _client.SendLineAsync("Arena rooms are now in combat mode (death DOES count)");
            return;
        }

        await _client.SendLineAsync("Syntax: SYSOP ARENA [status/normal/combat]");
    }

    private static readonly Regex SimAnsiRegex = new("\x1b\\[[0-9;]*m", RegexOptions.Compiled);
    private static readonly Regex SimDamageRegex = new(@"for (\d+) damage", RegexOptions.Compiled);

    // SYSOP SIMULATE <monster#|name> [rounds] [--hp N --ac N --dr N --mr N --level N] [-v]
    // Runs a monster's real attack resolution (melee AtkType slots + AtkType=2 spell casts, with the
    // live ResolveMonsterAttackSpell damage path) against YOUR character, so you can see how hard a mob
    // hits a given build and whether multi-cast bursts (e.g. an Adult Red Dragon's twin dragonfires)
    // actually land. HP is refreshed to full at the start of each round so the fight runs the full count
    // and the monster's energy pool carries across rounds (the accumulate-above-cap is the whole point —
    // that's what lets a 1000-cap dragon fire two 600-EU casts from round 2 on). Overrides temporarily
    // swap your base HP/AC/DR/MR/level for the run and are restored afterward; nothing is persisted.
    // NOT simulated (v1): MidSpells, DeathSpell, area fan-out across a room, and retaliation back at the
    // monster — so the numbers are the single-target incoming damage from the attack slots only.
    private async Task HandleSysopSimulate(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            await _client.SendLineAsync("[SYSOP SIMULATE] Run a monster's attacks vs a copy of your character.");
            await _client.SendLineAsync("Syntax: SYSOP SIMULATE <monster#|name> [rounds] [--hp N --ac N --dr N --mr N --level N] [-v]");
            await _client.SendLineAsync("  rounds default 10 (max 200). -v prints every hit/cast line.");
            await _client.SendLineAsync("  Overrides swap your base stats for the run (effective AC/DR still add buffs).");
            await _client.SendLineAsync("  Simulates melee + AtkType=2 cast slots (real spell damage). Not: midspell/deathspell/area.");
            return;
        }

        Monster? template = null;
        if (int.TryParse(tokens[0], out var monNum))
            _world.Database.Monsters.TryGetValue(monNum, out template);
        if (template == null)
        {
            template = _world.Database.Monsters.Values.FirstOrDefault(m => m.Name.Equals(tokens[0], StringComparison.OrdinalIgnoreCase))
                    ?? _world.Database.Monsters.Values.FirstOrDefault(m => m.Name.Contains(tokens[0], StringComparison.OrdinalIgnoreCase));
        }
        if (template == null)
        {
            await _client.SendLineAsync($"No monster matches '{tokens[0]}'.");
            return;
        }

        int rounds = 10;
        bool verbose = false;
        int? ovHp = null, ovAc = null, ovDr = null, ovMr = null, ovLevel = null;
        for (int i = 1; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t is "-v" or "--verbose") { verbose = true; continue; }
            if (t.StartsWith("--") && i + 1 < tokens.Length && int.TryParse(tokens[i + 1], out var v))
            {
                switch (t.ToLowerInvariant())
                {
                    case "--hp": ovHp = v; break;
                    case "--ac": ovAc = v; break;
                    case "--dr": ovDr = v; break;
                    case "--mr": ovMr = v; break;
                    case "--level": case "--lvl": ovLevel = v; break;
                }
                i++;
                continue;
            }
            if (int.TryParse(t, out var r) && r > 0)
                rounds = Math.Clamp(r, 1, 200);
        }

        // Snapshot the base fields we may swap, so the run never leaks into the live character.
        int snapCurHp = _player.CurrentHP, snapMaxHp = _player.MaxHP, snapAc = _player.ArmourClass;
        int snapDr = _player.DamageResist, snapMr = _player.MagicResist, snapLevel = _player.Level;
        try
        {
            if (ovHp.HasValue) _player.MaxHP = Math.Max(1, ovHp.Value);
            if (ovAc.HasValue) _player.ArmourClass = ovAc.Value;
            if (ovDr.HasValue) _player.DamageResist = ovDr.Value;
            if (ovMr.HasValue) _player.MagicResist = ovMr.Value;
            if (ovLevel.HasValue) _player.Level = Math.Max(1, ovLevel.Value);

            var monster = MonsterInstance.Create(template, _player.CurrentMapNumber, _player.CurrentRoomNumber);
            // Engage at a full pool, mirroring a monster that was idle (RefillEnergyOutOfCombat) before
            // the fight — round 1 then stays at one cap, round 2+ accumulates the remainder.
            int cap = monster.GetCombatEnergyCap();
            monster.CurrentEnergy = cap;

            await _client.SendLineAsync($"{MudAnsi.BrightYellow}=== SIMULATE: {monster.DisplayName} (#{template.Number}) vs {_player.Name} ==={MudAnsi.Reset}");
            await _client.SendLineAsync($"  Target : L{_player.Level}  HP {_player.MaxHP}  AC {_player.GetTotalAC()}  DR {_player.GetTotalDR()}  MR {_player.MagicResist}"
                + (ovHp.HasValue || ovAc.HasValue || ovDr.HasValue || ovMr.HasValue || ovLevel.HasValue ? "  (overridden)" : "  (your live stats)"));
            await _client.SendLineAsync($"  Monster: energy cap {cap}, {template.Attacks.Count} attack slot(s):");
            foreach (var atk in template.Attacks)
            {
                string desc = atk.Type switch
                {
                    2 => $"CAST spell #{atk.Accuracy} ({(_world.Database.Spells.TryGetValue(atk.Accuracy, out var sp) ? sp.Name : "?")}) @lvl {atk.Max}",
                    3 => "ROB",
                    _ => $"melee dmg {atk.Min}-{atk.Max} acc {atk.Accuracy}",
                };
                await _client.SendLineAsync($"    [{atk.SlotIndex}] {atk.Percent,3}% sel  {atk.Energy,4} EU  {desc}");
            }
            int lethalThreshold = _player.MaxHP - Player.DeathHP; // round dmg at/above this kills a full-HP target
            await _client.SendLineAsync($"  Running {rounds} round(s); HP refreshed each round. A full-HP target dies at {lethalThreshold}+ dmg/round.");
            await _client.SendLineAsync("");

            int totalDmg = 0, maxRound = 0, maxHit = 0, doubleCastRounds = 0, totalCasts = 0, lethalRounds = 0;
            for (int rd = 1; rd <= rounds; rd++)
            {
                _player.CurrentHP = _player.MaxHP;
                monster.PrepareCombatRound();
                int pool = monster.CurrentEnergy;
                int casts = 0;
                var result = CombatEngine.MonsterAttack(monster, _player, _world.Database.Items, _world.Database.Messages,
                    (spellId, castLevel) => { casts++; return ResolveMonsterAttackSpell(monster, spellId, castLevel, target: _player); },
                    null);
                int left = monster.CurrentEnergy;
                int roundDmg = _player.MaxHP - _player.CurrentHP;
                totalDmg += roundDmg;
                totalCasts += casts;
                if (roundDmg > maxRound) maxRound = roundDmg;
                if (casts >= 2) doubleCastRounds++;
                bool lethal = roundDmg >= lethalThreshold;
                if (lethal) lethalRounds++;

                foreach (Match m in SimDamageRegex.Matches(SimAnsiRegex.Replace(string.Join("\n", result.Messages), "")))
                    if (int.TryParse(m.Groups[1].Value, out var d) && d > maxHit) maxHit = d;

                string flag = lethal ? $"  {MudAnsi.BrightRed}<<< KILLS full-HP target{MudAnsi.Reset}" : "";
                await _client.SendLineAsync($"  Rd {rd,3}  pool {pool,4} left {left,4}  casts {casts}  hits {result.Hits}  dmg {roundDmg,4}{flag}");
                if (verbose)
                    foreach (var line in result.Messages)
                        await _client.SendLineAsync($"        {line}");
            }

            await _client.SendLineAsync("");
            await _client.SendLineAsync($"{MudAnsi.BrightYellow}--- SUMMARY ({rounds} rounds) ---{MudAnsi.Reset}");
            await _client.SendLineAsync($"  avg dmg/round : {(double)totalDmg / rounds:F1}");
            await _client.SendLineAsync($"  max round dmg : {maxRound}   max single hit : {maxHit}");
            await _client.SendLineAsync($"  casts total   : {totalCasts}   rounds with 2+ casts : {doubleCastRounds}/{rounds}");
            await _client.SendLineAsync($"  rounds that would kill a full-HP target : {lethalRounds}/{rounds}");
        }
        finally
        {
            _player.CurrentHP = snapCurHp; _player.MaxHP = snapMaxHp; _player.ArmourClass = snapAc;
            _player.DamageResist = snapDr; _player.MagicResist = snapMr; _player.Level = snapLevel;
        }
    }

}
