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
    // When a suicide password is set, both SUICIDE and
    // REROLL prompt for it interactively (masked, "Enter your suicide password: ") -- stock never accepts
    // the password as an inline argument. An empty or incorrect entry aborts with "Invalid password
    // specified."; a correct entry -- or no password set at all -- proceeds to the suicide/reroll.
    private async Task<bool> VerifySuicideRerollPasswordAsync()
    {
        if (string.IsNullOrEmpty(_player.SuicideRerollPassword))
            return true;

        bool verified = false;
        await RunBufferedInteractiveCommandAsync(async () =>
        {
            await _client.SendAsync("Enter your suicide password: ");
            string entered = FirstToken(await _client.ReadLineMaskedAsync());
            verified = entered.Length > 0
                && string.Equals(entered, _player.SuicideRerollPassword, StringComparison.Ordinal);
            if (!verified)
                await _client.SendLineAsync("Invalid password specified.");
        });
        return verified;
    }

    private async Task HandleSneak()
    {
        // SNEAK clears the rest/meditate/sneaking flags
        // FIRST — before the sneak check runs. This matters: the could-attack "snuck in
        // undetected" exemption (Gate A) keys off the sneaking flag, so clearing it first means a
        // player who just snuck into a room with a monster is re-evaluated as NOT sneaking and is
        // correctly blocked. Resetting after the gate (as we used to) let the stale IsSneaking flag
        // exempt the first re-sneak — "Attempting to sneak..." instead of "You may not sneak right now!".
        _player.IsResting = false;
        _player.IsMeditating = false;
        _player.IsSneaking = false;

        if (IsMovementBlockedByStatus)
        {
            await _client.SendLineAsync($"{MudAnsi.White}You may not sneak right now!{MudAnsi.Reset}");
            return;
        }

        // "You may not sneak right now!" (in combat or monsters present)
        if (_player.InCombat)
        {
            await _client.SendLineAsync($"{MudAnsi.White}You may not sneak right now!{MudAnsi.Reset}");
            return;
        }

        // SNEAK gate: blocked if a monster could attack (see CombatEngine).
        var monsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (CombatEngine.MonsterCouldAttack(_player, monsters))
        {
            await _client.SendLineAsync($"{MudAnsi.White}You may not sneak right now!{MudAnsi.Reset}");
            return;
        }

        // "PERFECT STEALTH" — ability 186 auto-succeeds
        if (_player.HasPerfectStealth)
        {
            _player.IsSneaking = true;
            await _client.SendLineAsync($"{MudAnsi.White}Attempting to sneak...{MudAnsi.Reset}");
            return;
        }

        if (_player.Stealth <= 0)
        {
            await _client.SendLineAsync($"{MudAnsi.White}Attempting to sneak... You don't think you're sneaking.{MudAnsi.Reset}");
            return;
        }

        // Stealth check — the shared formula (see Player.CalculateStealthChance).
        var rng = new Random();
        int chance = Player.CalculateStealthChance(
            _player.Stealth,
            GetMovementEncumbrancePercent(),
            _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player).Count(),
            _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber).Count(m => !m.IsDead),
            _player.RecentlySpotted);
        bool success = rng.Next(100) < chance;

        // "Attempting to sneak..."
        // "You don't think you're sneaking." — not always revealed on failure
        if (success)
        {
            _player.IsSneaking = true;
            await _client.SendLineAsync($"{MudAnsi.White}Attempting to sneak...{MudAnsi.Reset}");
        }
        else
        {
            // On failure you only realize it when a roll of 0..99 < your
            // Perception — a low-perception character is often fooled into thinking they snuck.
            // (Was a flat 50%.)
            if (rng.Next(100) < _player.Perception)
            {
                await _client.SendLineAsync($"{MudAnsi.White}Attempting to sneak... You don't think you're sneaking.{MudAnsi.Reset}");
            }
            else
            {
                // Player doesn't know they failed, but the move still remains unsneaked.
                await _client.SendLineAsync($"{MudAnsi.White}Attempting to sneak...{MudAnsi.Reset}");
            }
        }
    }

    private async Task HandleSearch(string args)
    {
        // SEARCH clears the rest and meditate
        // flags up front — before any search work and regardless of search type — so any search silently
        // stands you up / stops meditating (bug #139: you could search while resting without re-resting).
        // These clears run ahead of BOTH gates below, exactly as stock orders them.
        _player.IsResting = false;
        _player.IsMeditating = false;

        await BreakSneakAndHideForAction();

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync($"{MudAnsi.BrightGreen}Your search revealed nothing.{MudAnsi.Reset}");
            return;
        }

        // Bug #227: you could search a pitch-black room (or one you'd been "enveloped in darkness" in)
        // and still turn up hidden items. SEARCH runs the sight gate immediately after the flag
        // clears and does nothing but burn the delay when it fails — no item roll, no hidden-player roll, no
        // "is searching the area." broadcast, no directional exit search. The sight gate covers BOTH
        // modes because stock applies it before the argument branch.
        if (!await PassesSightGateAsync(room))
            return;

        // "You may not search while attacking!" — checked AFTER the sight gate, so a dark room in a fight
        // reports the darkness, not the combat refusal.
        if (_player.InCombat)
        {
            await _client.SendLineAsync("You may not search while attacking!");
            return;
        }

        // Two modes: "search <direction>" searches for hidden exits,
        //            "search" (no args) searches the room for hidden items/people/currency
        if (!string.IsNullOrWhiteSpace(args) && DirectionAliases.TryGetValue(args.Trim(), out var searchDir))
        {
            await HandleSearchDirection(room, searchDir);
        }
        else
        {
            await HandleSearchRoom(room);
        }
    }

    private async Task HandleSearchDirection(Room room, string direction)
    {
        // "%s is searching for exits." broadcast to room
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            $"{_player.Name} is searching for exits.", _client);

        int perception = _player.Perception;
        var rng = new Random();

        // A directional search has three branches —
        //   1) hidden exit (type 6) whose runtime state has the SEARCHABLE bit set:
        //      roll < max(Perception-15, 3) reveals it. A hidden exit that instead
        //      carries an action-slot bitfield (a "needs N actions" PUZZLE
        //      exit — boulder pushes, lever pulls) has the searchable bit CLEAR, so stock falls straight to
        //      "You notice nothing different" — search must NOT bypass the puzzle. We encode the
        //      searchable bit as Para1 & 2 (IsSearchableHiddenExit); the boulder exits carry Para1=16.
        //   2) trap (exit type 9): roll < Traps prints "You found a trap..." (no
        //      persistent state — discovery isn't required for disarm anyway)
        //   3) anything else: "You notice nothing different..."
        var directionalExit = room.GetExit(direction);
        if (directionalExit != null && directionalExit.IsSearchableHiddenExit)
        {
            int chance = Math.Max(perception - 15, 3);
            if (rng.Next(100) < chance)
            {
                _world.TryRevealHiddenExit(room, direction, out _);

                // "You found an exit upwards!", "You found an exit downwards!", "You found an exit to the %s!"
                string exitMsg = direction switch
                {
                    "up" => "You found an exit upwards!",
                    "down" => "You found an exit downwards!",
                    _ => $"You found an exit to the {direction}!"
                };
                await _client.SendLineAsync($"{MudAnsi.White}{exitMsg}{MudAnsi.Reset}");
                return;
            }
        }
        else if (directionalExit != null && directionalExit.IsTrapExit)
        {
            if (rng.Next(100) < _player.Traps)
            {
                // "You found a trap above you!", "You found a trap below you!",
                //      "You found a trap to the %s!"
                string trapMsg = direction switch
                {
                    "up" => "You found a trap above you!",
                    "down" => "You found a trap below you!",
                    _ => $"You found a trap to the {direction}!"
                };
                await _client.SendLineAsync($"{MudAnsi.White}{trapMsg}{MudAnsi.Reset}");
                return;
            }
        }

        // "You notice nothing different above you.", "You notice nothing different below you.",
        //      "You notice nothing different to the %s."
        string failMsg = direction switch
        {
            "up" => "You notice nothing different above you.",
            "down" => "You notice nothing different below you.",
            _ => $"You notice nothing different to the {direction}."
        };
        await _client.SendLineAsync($"{MudAnsi.White}{failMsg}{MudAnsi.Reset}");
    }

    private async Task HandleSearchRoom(Room room)
    {
        // "%s is searching the area." broadcast to room
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            $"{_player.Name} is searching the area.", _client);

        int perception = _player.Perception;
        var rng = new Random();
        var foundItems = new List<string>();

        // Searching can reveal hidden players in-room.
        // The rule: if HasSeeHidden then always reveal, otherwise rand(0..99) < Perception.
        var hiddenPlayers = _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            .Where(p => p.IsHidden && !p.IsSysopInvisible)
            .ToList();
        var revealedHiddenPlayers = new List<string>();
        foreach (var hiddenPlayer in hiddenPlayers)
        {
            bool reveal = _player.HasSeeHidden || rng.Next(100) < _player.Perception;
            if (!reveal)
                continue;

            // A revealed player loses hidden + sneak
            // and gets the "recently spotted" flag, penalizing the next re-hide.
            hiddenPlayer.IsHidden = false;
            hiddenPlayer.IsSneaking = false;
            hiddenPlayer.RecentlySpotted = true;
            revealedHiddenPlayers.Add(hiddenPlayer.Name);
        }
        // NB: the reveal message is DEFERRED — stock prints found items first, then the reveal, and
        // a revealed player must SUPPRESS the "Your search revealed nothing." line (see the tail below).

        var searchableGroundCurrencyParts = new List<string>();

        // 1b) Check for player-hidden (stashed) currency and reveal it when found.
        var hiddenGroundCurrencyParts = _world.GetHiddenGroundCurrencyParts(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (hiddenGroundCurrencyParts.Count > 0)
        {
            int chance = Math.Min(95, 30 + perception);
            if (rng.Next(100) < chance)
            {
                searchableGroundCurrencyParts.AddRange(hiddenGroundCurrencyParts);
            }
        }

        // 2) Check for hidden items (InvisItems from room data)
        // Skip ItemType=3 (interactable room objects like manhole, hole, wall, etc.)
        // Those are accessible via 'go'/'enter' commands, not found by searching
        var hiddenIds = room.GetHiddenItemIds();
        foreach (var id in hiddenIds)
        {
            if (_world.Database.Items.TryGetValue(id, out var item))
            {
                if (item.ItemType == 3) continue; // Room object — not searchable

                // Higher perception = better chance to find hidden items
                int chance = Math.Min(90, 20 + perception * 2);
                if (rng.Next(100) < chance)
                {
                    foundItems.Add(item.Name);
                }
            }
        }

        // 3) Check for player-hidden (stashed) items in the room
        var stashedItems = _world.GetHiddenGroundItemsWithInstance(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        foreach (var (itemId, _, instanceId) in stashedItems)
        {
            if (_world.Database.Items.TryGetValue(itemId, out var item))
            {
                // Perception roll for player-hidden items
                int chance = Math.Min(90, 25 + perception * 2);
                if (rng.Next(100) < chance)
                {
                    foundItems.Add(item.Name);
                    // Flag this specific instance as seen; only now can `get <name>` grab it.
                    _player.RevealHiddenItem(_player.CurrentMapNumber, _player.CurrentRoomNumber, instanceId);
                }
            }
        }

        if (searchableGroundCurrencyParts.Count > 0)
            foundItems.AddRange(searchableGroundCurrencyParts);

        // The room-items tail: list found items ("... here.") and THEN reveal any
        // hidden players; when nothing was found, reveal hidden players and print "Your search revealed
        // nothing." ONLY if no one was revealed. Spotting a player is the better outcome and suppresses
        // the "revealed nothing" line entirely — matching stock (and observed v1.11p).
        string revealLine = $"{MudAnsi.BrightRed}You see {string.Join(", ", revealedHiddenPlayers)} hiding in the shadows.{MudAnsi.Reset}";

        if (foundItems.Count > 0)
        {
            var itemEntries = BuildGroupedNoticeEntries(foundItems, _player.PaletteId);
            await SendWrappedNoticeHereAsync(itemEntries);
            if (revealedHiddenPlayers.Count > 0)
                await _client.SendLineAsync(revealLine);
        }
        else if (revealedHiddenPlayers.Count > 0)
        {
            await _client.SendLineAsync(revealLine);
        }
        else
        {
            await _client.SendLineAsync($"{MudAnsi.Cyan}Your search revealed nothing.{MudAnsi.Reset}");
        }
    }

    private async Task HandleRest()
    {
        // REST breaks autocombat first,
        // before the sick gate — re-issuing rest mid-fight ends the player's attack loop regardless.
        await BreakAutocombatAsync();

        // Poison (PoisonLevel) blocks rest — "You are too sick to rest!"
        if (_player.PoisonLevel > 0)
        {
            await _client.SendLineAsync("You are too sick to rest!");
            return;
        }

        // The resting flag is set idempotently and always prints "You are now
        // resting." — re-issuing rest re-asserts it, it never toggles you back up (resting ends via other
        // actions, not by typing rest again). The "%s stops to rest." room broadcast fires only on the
        // transition INTO resting (stock gates it on the flag not already being set).
        bool wasResting = _player.IsResting;
        _player.IsResting = true;
        _player.IsSneaking = false;
        _player.IsHidden = false;
        _player.IsMeditating = false;
        _player.RestStartTime = DateTime.UtcNow;

        if (!wasResting)
        {
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{_player.Name} stops to rest.", _client);
        }

        await _client.SendLineAsync("You are now resting.");
    }

    // WCCMMHLP.MSG: "The MEDITATE command will increase your rate of Mana regeneration."
    private async Task HandleMeditate()
    {
        // MEDITATE is gated on the meditate quest ability (187).
        if (_player.MaxMana <= 0 || _player.GetQuestAbilityValue(187) <= 0)
        {
            return;
        }

        // Break autocombat first, before the
        // sick / mana gates — prints "*Combat Off*" to the meditator, same as REST. Stopping the loop
        // stops the swing loop only; it does NOT clear the chosen target or the incoming attackers.
        await BreakAutocombatAsync();

        // Poison blocks meditation, checked BEFORE the mana gate so "too sick" takes
        // priority over "won't help".
        if (_player.PoisonLevel > 0)
        {
            await _client.SendLineAsync("You are too sick to meditate!");
            return;
        }

        // Only meditate when mana isn't already full; otherwise "Meditation will not help...".
        if (_player.CurrentMana >= _player.MaxMana)
        {
            await _client.SendLineAsync("Meditation will not help at this time.");
            return;
        }

        // The meditating flag is set idempotently and always prints "You are
        // now meditating." The "%s kneels to meditate." room broadcast fires only on the transition.
        bool wasMeditating = _player.IsMeditating;
        _player.IsMeditating = true;
        _player.IsSneaking = false;
        _player.IsHidden = false;
        _player.IsResting = false;
        _player.RestStartTime = DateTime.UtcNow;

        if (!wasMeditating)
        {
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{_player.Name} kneels to meditate.", _client);
        }

        await _client.SendLineAsync("You are now meditating.");
    }

    // WCCMMHLP.MSG: "You can aid a fallen player by typing AID <username>"
    private async Task HandleAid(string args)
    {
        if (_player.IsUnconscious)
        {
            await SendMortallyWoundedMovementMessageAsync();
            return;
        }

        string targetName = args.Trim();
        if (string.IsNullOrEmpty(targetName))
        {
            await _client.SendLineAsync("Syntax: AID {user name}");
            return;
        }

        var target = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, targetName, _player);

        if (target == null || !target.IsUnconscious)
        {
            await _client.SendLineAsync($"{targetName} is in no need of assistance.");
            return;
        }

        target.IsAided = true;
        await _client.SendLineAsync($"You have aided {target.Name}, {target.HisHer_Lower} wounds are now healing.");
        _world.SendToPlayer(target.Name, $"{_player.Name} has aided you.");
    }

    // Stock reference strings: "Syntax: DRAG {user name} [{direction}]",
    // "You are now dragging %s.", "%s is too healthy to be dragged.",
    // and "%s is dragging you around."
    private async Task HandleDrag(string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            if (_world.TryStopDraggingForDragger(_player, out var stopMessage))
            {
                await _client.SendLineAsync(stopMessage);
                return;
            }

            await _client.SendLineAsync("Syntax: DRAG {user name} [{direction}]");
            return;
        }

        if (_player.IsUnconscious)
        {
            await SendMortallyWoundedMovementMessageAsync();
            return;
        }

        string targetName = parts[0];
        if (TargetNameMatcher.MatchesWordPrefix(_player.Name, targetName))
        {
            await _client.SendLineAsync("Why would you want to drag yourself around?");
            return;
        }

        string? direction = null;
        if (parts.Length > 1)
        {
            if (!DirectionAliases.TryGetValue(parts[1], out direction))
            {
                await _client.SendLineAsync("Syntax: DRAG {user name} {direction} ");
                return;
            }
        }

        var target = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, targetName, _player);
        if (target == null)
        {
            await _client.SendLineAsync($"{targetName} is too healthy to be dragged.");
            return;
        }

        if (!_world.TryStartDragging(_player, target, out var selfMessage, out var targetMessage))
        {
            await _client.SendLineAsync(selfMessage);
            return;
        }

        await _client.SendLineAsync(selfMessage);
        if (!string.IsNullOrWhiteSpace(targetMessage))
            _world.SendToPlayer(target.Name, targetMessage);

        if (!string.IsNullOrWhiteSpace(direction))
            await HandleMovement(direction);
    }

    // WCCMMHLP.MSG: "WEALTH will display your current monetary situation."
    private async Task HandleWealth()
    {
        var parts = new List<string>();
        if (_player.Runic > 0) parts.Add($"{_player.Runic} runic {(_player.Runic == 1 ? "coin" : "coins")}");
        if (_player.Platinum > 0) parts.Add($"{_player.Platinum} platinum {(_player.Platinum == 1 ? "piece" : "pieces")}");
        if (_player.Gold > 0) parts.Add($"{_player.Gold} gold {(_player.Gold == 1 ? "crown" : "crowns")}");
        if (_player.Silver > 0) parts.Add($"{_player.Silver} silver {(_player.Silver == 1 ? "noble" : "nobles")}");
        if (_player.Copper > 0) parts.Add($"{_player.Copper} copper {(_player.Copper == 1 ? "farthing" : "farthings")}");

        if (parts.Count > 0)
        {
            // Stock wealth (verified against live Adept output): the lead-in is "You have ...." (no SGR),
            // NOT "You are carrying ...". The total line reuses the SAME inventory wealth roles
            // (InventoryLabel = green, InventoryValue = cyan on every palette) so the WEALTH command and the
            // inventory wealth line render byte-identically.
            GameColorPalette palette = GameColorPalettes.Resolve(_player.PaletteId);
            await _client.SendLineAsync($"You have {string.Join(", ", parts)}.");
            long totalCopper = CurrencyHelper.ToCopper(_player);
            await _client.SendLineAsync($"{palette.Get(GameColorRole.InventoryLabel)}Wealth:{MudAnsi.Reset} {palette.Get(GameColorRole.InventoryValue)}{totalCopper} copper farthings{MudAnsi.Reset}");
        }
        else
        {
            await _client.SendLineAsync("You have no money.");
        }
    }

    private async Task HandleExits()
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are in a void!");
            return;
        }
        await _client.SendLineAsync(GameAnsi.Exits($"Obvious exits: {_world.GetVisibleExitString(_player, room)}"));
    }

    private async Task HandleRoom()
    {
        const int profileLabelWidth = 19; // "Block Entrance Msg:" length

        static string ProfileLine(string label, string value, int labelWidth)
            => $"{label.PadRight(labelWidth)} {value}";

        var (Location, RegenTime, RoomIllumination) = GetCurrentRoomDiagnostics();

        await _client.SendLineAsync(ProfileLine("Location:", Location, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Regen Time:", RegenTime, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Room Illu:", RoomIllumination, profileLabelWidth));
    }

    // Non-stock QOL command "home <monster>": report where a unique timer-regen boss (GameLimit 1,
    // RegenTime > 0) spawns, whether it is currently alive and where, and — when dead — roughly when its
    // RegenTime gate lets it respawn. Read-only; never forces a spawn. Gated by SYSOP CONFIGURE QOL home.
    private async Task HandleHome(string args)
    {
        string query = (args ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            await _client.SendLineAsync($"{MudAnsi.White}Usage: home <monster name>  (looks up unique boss spawn locations & respawn timers).{MudAnsi.Reset}");
            return;
        }

        var reports = _world.GetMonsterHomeReports(query);
        if (reports.Count == 0)
        {
            await _client.SendLineAsync($"{MudAnsi.White}No unique (regenerating) monster matches '{query}'.{MudAnsi.Reset}");
            return;
        }

        await _client.SendLineAsync($"{MudAnsi.BrightYellow}Home lookup: '{query}'{MudAnsi.Reset}");
        foreach (var report in reports)
        {
            string regen = report.RegenTimeHours > 0 ? $"  (regen {FormatHomeDuration(TimeSpan.FromHours(report.RegenTimeHours))})" : "";
            await _client.SendLineAsync($"{MudAnsi.BrightWhite}{report.Name}{MudAnsi.Reset} (#{report.Number}){regen}");

            if (report.HomeRooms.Count == 0)
            {
                await _client.SendLineAsync($"  {MudAnsi.White}Home: (no fixed spawn room found){MudAnsi.Reset}");
            }
            else
            {
                foreach (var home in report.HomeRooms)
                {
                    string roomName = string.IsNullOrEmpty(home.RoomName) ? "(unnamed)" : home.RoomName;
                    string how = home.EnterSpawn ? "on entry" : "lair";
                    await _client.SendLineAsync($"  Home: {roomName} ({home.Map}/{home.Room}) [{how}]");
                }
            }

            bool enterSpawn = report.HomeRooms.Count == 0 || report.HomeRooms.Any(h => h.EnterSpawn);
            string appearHint = enterSpawn ? "appears on entry to its home room" : "respawns on the next lair tick";

            if (report.IsActive)
            {
                string where = report.InHomeRoom
                    ? "in its home room"
                    : $"in {(string.IsNullOrEmpty(report.CurrentRoomName) ? "(unnamed)" : report.CurrentRoomName)} ({report.CurrentMap}/{report.CurrentRoom}) — away from home";
                await _client.SendLineAsync($"  {MudAnsi.BrightGreen}Spawned: yes{MudAnsi.Reset} — {where}");
            }
            else if (report.RespawnReadyAtUtc is { } readyAt && readyAt > DateTime.UtcNow)
            {
                var remaining = readyAt - DateTime.UtcNow;
                await _client.SendLineAsync(
                    $"  {MudAnsi.White}Spawned: no{MudAnsi.Reset} — respawns in ~{FormatHomeDuration(remaining)} (around {readyAt:yyyy-MM-dd HH:mm} UTC), then {appearHint}");
            }
            else
            {
                await _client.SendLineAsync($"  {MudAnsi.White}Spawned: no{MudAnsi.Reset} — eligible now; {appearHint}");
            }
        }
    }

    private static string FormatHomeDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        int days = (int)span.TotalDays;
        int hours = span.Hours;
        int minutes = span.Minutes;
        if (days > 0)
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        if (hours > 0)
            return minutes > 0 ? $"{hours}h {minutes}m" : $"{hours}h";
        return $"{Math.Max(1, minutes)}m";
    }

    // Per the stock docs: Profile displays current profile settings
    private async Task HandleProfile()
    {
        string displayMode = _player.BriefMode ? "Brief" : "Verbose";
        string talkingSpeed = _player.TalkMode == 1 ? "Fast" : "Slow";
        const int profileLabelWidth = 19; // "Block Entrance Msg:" length

        static string ProfileLine(string label, string value, int labelWidth)
            => $"{label.PadRight(labelWidth)} {value}";

        var (Location, RegenTime, RoomIllumination) = GetCurrentRoomDiagnostics();

        // Some profile fields are not wired yet; defaults are rendered in classic layout for now.
        await _client.SendLineAsync("Life for this CHAR  357 minutes");
        await _client.SendLineAsync(ProfileLine("Location:", Location, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Regen Time:", RegenTime, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Room Illu:", RoomIllumination, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Display Mode:", displayMode, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Statusline:", "Full", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("EPs:", $"{_player.CurrentEPs:0.##}", profileLabelWidth));
        // Non-stock: only while the realm runs SYSOP CONFIGURE MINEPS, and directly under the EPs it
        // floors. "Off" is the word that clears it (SET MINEPS OFF), so the profile reads back what you
        // would type. Without this the setting is only visible by typing SET MINEPS.
        if (_world.MinEvilPointsEnabled)
            await _client.SendLineAsync(ProfileLine(
                "Evil Points Set:",
                HasMinEvilPointsSet(_player) ? $"{_player.MinEvilPoints:0.##}" : "Off",
                profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Broadcast Channel:", _player.BroadcastChannel.ToString(CultureInfo.InvariantCulture), profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Talking speed:", talkingSpeed, profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Follow Mode:", "Normal", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Attack Interrupts:", "Off", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Receive Items:", _player.ReceiveItemsEnabled ? "Enabled" : "Disabled", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Warn on Evil:", _player.WarnOnEvilEnabled ? "Yes" : "No", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Allow Telepaths:", "Yes", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Block Entrance Msg:", "No", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Allow Gossip:", _player.ReceiveGossipEnabled ? "Yes (3 every 3 seconds)" : "No", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Allow Auction:", _player.ReceiveAuctionEnabled ? "Yes (3 every 3 seconds)" : "No", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Keep some exp:", _player.KeepMode ? "Yes" : "No", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Messages:", "High", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Paging:", "Ok (3 every 3 seconds)", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Talking Responses:", "Brief", profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Style:", GetStyleDisplayName(_player), profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Look Style:", GetLookStyleDisplayName(_player), profileLabelWidth));
        await _client.SendLineAsync(ProfileLine("Colour Palette:", _player.PaletteId.ToString(CultureInfo.InvariantCulture), profileLabelWidth));
        // The profile only announces the ABSENCE of a suicide password. It never displays the
        // secret itself, and prints nothing at all when one is set -- so neither do we.
        if (string.IsNullOrEmpty(_player.SuicideRerollPassword))
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You do not have a suicide password set.{MudAnsi.Reset}");
    }

    private static string GetStyleDisplayName(Player player)
    {
        return player.UseTechnicalStyle ? "Technical" : "Fantasy";
    }

    private static string GetLookStyleDisplayName(Player player)
    {
        return player.UseModernLookStyle ? "Modern" : "Traditional";
    }

    private (string Location, string RegenTime, string RoomIllumination) GetCurrentRoomDiagnostics()
    {
        string location = $"{_player.CurrentMapNumber},{_player.CurrentRoomNumber}";
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
            return (location, "None", "Unknown");

        int modifiedRoomIllumination = GetEffectiveRoomLight(room);
        string regenTime = GetCurrentRoomRegenTime(room);
        string roomIllumination = string.Create(
            CultureInfo.InvariantCulture,
            $"{modifiedRoomIllumination} ({room.Light})");

        return (location, regenTime, roomIllumination);
    }

    private string GetCurrentRoomRegenTime(Room room)
    {
        DateTime? deadline = _world.GetNextRoomSpawnDeadline(room.MapNumber, room.RoomNumber);
        if (deadline.HasValue)
            return FormatRemainingDuration(deadline.Value - DateTime.UtcNow);

        return GameWorld.CanRoomGenerateLairMonsters(room)
            ? "Ready"
            : "None";
    }

    private static string FormatRemainingDuration(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
            return "Ready";

        remaining = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        if (remaining.TotalDays >= 1)
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h {remaining.Minutes}m {remaining.Seconds}s";

        int totalHours = (int)remaining.TotalHours;
        if (totalHours >= 1)
            return $"{totalHours}h {remaining.Minutes}m {remaining.Seconds}s";

        return $"{Math.Max(0, (int)remaining.TotalMinutes)}m {remaining.Seconds}s";
    }

    private async Task HandleTrain(string args)
    {
        // NO combat gate. TRAIN has exactly one early rejection — tournament mode
        // ("You may not do any training while in tournament mode!") —
        // and bare TRAIN then falls straight through to the level-up path, which contains no
        // reference to autocombat at all. "You can't train while in combat!" is not a string in the
        // stock; it was invented here. Stock does not need the check: training rooms are protected, and
        // RM_PROTECTED blocks all combat in the room, so a fighting player cannot be standing in one.
        var mode = args.Trim();

        // Sysops can open the stat allocation screen from anywhere.
        if (_player.IsSysop && mode.Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            var creator = new CharacterCreation(_client, _world);
            await RunBufferedInteractiveCommandAsync(() => creator.TrainAsync(_player, CancellationToken.None));
            return;
        }

        // The training gate: must be in a SHOP-CLASS room that is a
        // training shop — a stray shop number on an ordinary room is not a trainer.
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null || !room.IsShopRoom || room.Shop <= 0)
        {
            await _client.SendLineAsync("You must be in an appropriate training room to train!");
            return;
        }

        if (!_world.Database.Shops.TryGetValue(room.Shop, out var shop) || shop.ShopType != 8)
        {
            await _client.SendLineAsync("This shop is not suitable for your training.");
            return;
        }

        if (mode.Equals("stats", StringComparison.OrdinalIgnoreCase))
        {
            var creator = new CharacterCreation(_client, _world);
            await RunBufferedInteractiveCommandAsync(() => creator.TrainAsync(_player, CancellationToken.None));
            return;
        }

        if (shop.ClassRest > 0 && shop.ClassRest != _player.ClassId)
        {
            await _client.SendLineAsync("This shop is not suitable for your training.");
            return;
        }

        int nextLevel = _player.Level + 1;
        if (shop.MinLvl > 0 && nextLevel < shop.MinLvl)
        {
            await _client.SendLineAsync("You have not progressed far enough to use the training provided here.");
            return;
        }

        if (shop.MaxLvl > 0 && nextLevel > shop.MaxLvl)
        {
            await _client.SendLineAsync("You have progressed too far to use the training provided here.");
            return;
        }

        if (!string.IsNullOrEmpty(mode))
        {
            await _client.SendLineAsync("Syntax: TRAIN [STATS]");
            return;
        }

        await TrainOneLevel(shop);
    }

    private async Task HandleStats(string? args)
    {
        // The rich "stat all" sheet is a non-stock QOL view; when disabled, "stat all" falls back to the
        // stock compact (Megamud-parsed) sheet — stock has no "all" variant, so the arg is simply ignored.
        if (IsAllStatsRequest(args) && _world.IsQolEnabled(QolFeature.StatAll))
        {
            await HandleStatsAll(GetAllStatsTargetArgument(args));
            return;
        }

        await HandleStatsCompact();
    }

    // Everything after the "all"/"a" keyword is the optional monster to measure against — a number, a
    // name, or any distinctive part of one ("stat all red dragon"). Empty means the plain sheet.
    private static string GetAllStatsTargetArgument(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return string.Empty;

        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length <= 1 ? string.Empty : string.Join(' ', tokens.Skip(1));
    }

    // Inspired by Para's `abil` command: list the abilities that are currently affecting the
    // character, grouped by source (race, class, worn items, active spell effects, granted /
    // quest abilities). Stock doesn't expose this — it's a player-facing convenience.
    // Each row is "<name>(<id>)  <value>", with zero values shown because some abilities are
    // gating-active at 0 (e.g. class abilities like Bash 31:0). Sources are NOT merged; the same
    // ability id can appear under multiple sections if multiple sources grant it.
    private async Task HandleAbilities()
    {
        await PrintAbilitySectionAsync("Race", GetRaceAbilities());
        await PrintAbilitySectionAsync("Class", GetClassAbilities());
        await PrintAbilitySectionAsync("Worn Items", GetWornItemAbilities());
        await PrintAbilitySectionAsync("Spell effects", GetActiveSpellAbilities());
        // Strip learned-spellbook markers (ids at LearnedSpellbookAbilityBase + spellId): those are
        // bookkeeping rows recording which spells the character knows, not abilities affecting them.
        // They'd dump 30+ noisy rows for high-magery characters; `spells` already lists them.
        var nonSpellbook = _player.QuestAbilities
            .Where(pair => pair.Key < Player.LearnedSpellbookAbilityBase)
            .ToList();

        // Staged quest-progress flags render as named quests + step (see QuestCatalog); the remaining
        // entries are genuine granted reward abilities (Perfect Stealth, Dodge, …).
        await PrintQuestProgressAsync(nonSpellbook.Where(pair => QuestCatalog.IsQuestFlag(pair.Key)));
        var grantedAbilities = nonSpellbook
            .Where(pair => !QuestCatalog.IsQuestFlag(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        await PrintAbilitySectionAsync("GrantedAbilities", grantedAbilities);
    }

    // Render staged-quest flags as "Quest Name  step N/M" (multi-step) or "Quest Name  complete"
    // (atomic one-action quests, whose flag ticks internally — no meaningful intermediate step).
    private async Task PrintQuestProgressAsync(IEnumerable<KeyValuePair<int, int>> questFlags)
    {
        var rows = questFlags
            .Where(pair => QuestCatalog.TryGet(pair.Key, out _))
            .OrderBy(pair => pair.Key)
            .ToList();
        if (rows.Count == 0)
            return;

        await _client.SendLineAsync($"{MudAnsi.BrightWhite}Quests{MudAnsi.Reset}");
        foreach (var (flag, stage) in rows)
        {
            QuestCatalog.TryGet(flag, out var quest);
            string status = (!quest.MultiStep || stage >= quest.CompleteValue)
                ? "Complete"
                : $"Step {stage}/{quest.CompleteValue}";
            await _client.SendLineAsync(FormatAbilityRow(quest.Name, status));
        }
        await _client.SendLineAsync("");
    }

    private IReadOnlyDictionary<int, int> GetRaceAbilities()
        => _world.Database.Races.TryGetValue(_player.RaceId, out var race) ? race.Abilities : new Dictionary<int, int>();

    private IReadOnlyDictionary<int, int> GetClassAbilities()
        => _world.Database.Classes.TryGetValue(_player.ClassId, out var cls) ? cls.Abilities : new Dictionary<int, int>();

    // Sum a single ability across every equipped item (multiple items can grant AC, Accuracy, etc.).
    // Matches the way RecalculateStats aggregates equipment for derived stats.
    private Dictionary<int, int> GetWornItemAbilities()
    {
        var totals = new Dictionary<int, int>();
        foreach (int itemId in _player.Equipment.Values)
        {
            if (!_world.Database.Items.TryGetValue(itemId, out var item))
                continue;
            foreach (var (abilityId, value) in item.Abilities)
            {
                totals[abilityId] = totals.GetValueOrDefault(abilityId) + value;
            }
        }
        return totals;
    }

    // For each active spell, expose the spell's ability table. The spell's CastLevel feeds 0-valued
    // ability slots dynamically inside the combat math, but for display we just show the raw spell
    // value so the row matches the spell template (showing 0 communicates "this slot is wired").
    private Dictionary<int, int> GetActiveSpellAbilities()
    {
        var totals = new Dictionary<int, int>();
        foreach (var active in _player.ActiveSpells)
        {
            if (!_world.Database.Spells.TryGetValue(active.SpellId, out var spell))
                continue;
            foreach (var (abilityId, value) in spell.Abilities)
            {
                int contribution = value != 0 ? value : active.CastLevel;
                totals[abilityId] = totals.GetValueOrDefault(abilityId) + contribution;
            }
        }
        return totals;
    }

    // A row in the `abilities` panel: a label and a value sharing one fixed right edge so every value
    // lines up regardless of label length. The old "{0,-35}{1,8}" pushed the value past the edge when a
    // label was longer than 35 (e.g. "Alter Hot Attack Damage (Defence)(5)" = 36), breaking alignment.
    // For an over-long label the value keeps a one-space gap and overflows the edge (unavoidable).
    private const int AbilityRowRightEdge = 43;   // old 35 (label) + 8 (value)

    internal static string FormatAbilityRow(string label, string value)
    {
        int pad = Math.Max(1, AbilityRowRightEdge - label.Length - value.Length);
        return label + new string(' ', pad) + value;
    }

    private async Task PrintAbilitySectionAsync(string sectionName, IReadOnlyDictionary<int, int> abilities)
    {
        if (abilities.Count == 0)
            return;

        await _client.SendLineAsync($"{MudAnsi.BrightWhite}{sectionName}{MudAnsi.Reset}");

        foreach (var (abilityId, value) in abilities.OrderBy(pair => pair.Key))
        {
            string label = AbilityNames.Format(abilityId);
            await _client.SendLineAsync(FormatAbilityRow(label, value.ToString(CultureInfo.InvariantCulture)));
        }

        await _client.SendLineAsync("");
    }

    private static bool IsAllStatsRequest(string? args)
    {
        if (string.IsNullOrWhiteSpace(args))
            return false;

        string token = args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        return token.Equals("a", StringComparison.OrdinalIgnoreCase)
            || token.Equals("all", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleStatsCompact()
    {
        var race = _world.Database.Races[_player.RaceId];
        var cls = _world.Database.Classes[_player.ClassId];
        string g = MudAnsi.Green, c = MudAnsi.Cyan, reset = MudAnsi.Reset;
        const int topLeftWidth = 18;
        const int topMiddleWidth = 21;
        const int rightColumnOffset = topLeftWidth + topMiddleWidth;

        // Helper: green label + cyan value, padded to visible width
        string Cell(string label, string value, int width)
        {
            int visLen = label.Length + value.Length;
            int pad = Math.Max(0, width - visLen);
            return $"{g}{label}{reset}{c}{value}{reset}" + new string(' ', pad);
        }

        // Helper: right-column entry with right-justified value.
        string RCol(string label, string value, int width = 18)
        {
            int pad = Math.Max(1, width - label.Length - value.Length);
            return $"{g}{label}{reset}" + new string(' ', pad) + $"{c}{value}{reset}";
        }

        // Helper: middle-column entry with a right-justified value field.
        string MiddleValueCell(string label, string value, int valueWidth)
        {
            int trailingPad = Math.Max(0, topMiddleWidth - label.Length - valueWidth);
            return $"{g}{label}{reset}{c}{value.PadLeft(valueWidth)}{reset}" + new string(' ', trailingPad);
        }

        // Helper: stat cell col1 (total visible 13, padded to 18)
        string Stat1(string label, int val, string color)
        {
            string vs = val.ToString();
            return $"{g}{label}{reset}{color}{vs.PadLeft(13 - label.Length)}{reset}" + new string(' ', 5);
        }

        // Helper: stat cell col2 (total visible 11, padded to 21)
        string Stat2(string label, int val, string color)
        {
            string vs = val.ToString();
            return $"{g}{label}{reset}{color}{vs.PadLeft(11 - label.Length)}{reset}" + new string(' ', 10);
        }

        // The armour rating (shown as "Armour Class: AC/DR" after a /10): the displayed
        // pair is NOT the combat fighter struct — display AC carries ability 7 + blur, display DR carries
        // only blur (no ability 7, no frail%). Reproduce it so the Megamud-parsed line matches stock.
        int displayedAc = _player.GetDisplayArmourRating(out int displayedDr);
        string acStr = $"{displayedAc}/{displayedDr}";

        // Hits/Mana/Kai values use the same 9-char right-justified field in stock.
        string hpStr = $"{_player.CurrentHP}/{_player.MaxHP}";
        string hitsCell = $"{g}Hits:{reset}{c}{hpStr.PadLeft(9)}{reset}" + new string(' ', topLeftWidth - "Hits:".Length - 9);

        // Mana/Kai cell. Stock prints "%sMana: %s%4d/%-5d", where the value's leading marker
        // (%s) is keyed on ability 69, "+max mana" (granted by gear and the
        // 2nd-alignment quest; e.g. Warlock +6). When present the marker is a BRIGHT-RED asterisk
        // immediately followed by the cyan value (stock bytes ESC[1;31m * ESC[0;36m); otherwise it is
        // a blank cyan placeholder that keeps the column aligned. It is NOT keyed on current<max — so
        // we must not flag a merely-spent pool. The old current<max asterisk (plain-red, with a stray
        // reset+space before the digits) desynced Megamud's Max-Mana parse and zeroed its tracked max.
        string resourceLabel = Player.GetMagicResourceLabel(cls);
        bool showResource = cls.MageryLvl > 0;
        bool showSpellcasting = Player.UsesSpellcasting(cls);

        string BuildResourceCell(string label)
        {
            const int valueWidth = 9;
            int labelWidth = label.Length + 1;
            int trailingPad = Math.Max(0, topLeftWidth - labelWidth - valueWidth);
            string value = $"{_player.CurrentMana}/{_player.MaxMana}";
            // One column is reserved for the marker (asterisk or blank), mirroring stock's %s+%4d.
            string padded = value.PadLeft(valueWidth - 1);
            string marker = _player.HasActiveAbility(_world.Database, 69)
                ? $"{MudAnsi.BrightRed}*{c}"   // ability 69 (+max mana) active → stock bright-red asterisk
                : $"{c} ";                  // no max-mana ability → blank placeholder
            return $"{g}{label}:{reset}" + marker + padded + reset + new string(' ', trailingPad);
        }

        string resourceCell = showResource
            ? BuildResourceCell(resourceLabel)
            : new string(' ', topLeftWidth);

        string br = MudAnsi.BrightRed;

        // Stat color: bright red if modified (effective > base), else cyan
        string StatColor(int effective, int baseStat) => effective > baseStat ? br : c;

        string strColor = StatColor(_player.Strength, _player.BaseStrength);
        string aglColor = StatColor(_player.Agility, _player.BaseAgility);
        string intColor = StatColor(_player.Intellect, _player.BaseIntellect);
        string wilColor = StatColor(_player.Willpower, _player.BaseWillpower);
        string heaColor = StatColor(_player.Health, _player.BaseHealth);
        string chmColor = StatColor(_player.Charm, _player.BaseCharm);

        // Lives/CP
        int remainingCP = Math.Max(0, _player.CharacterPoints - _player.SpentCP);
        string livesCP = $"{_player.Lives}/{remainingCP}";

        // Line 1: Name (spans 38 cols) + Lives/CP
        string namePlain = $"Name: {_player.Name}";
        await _client.SendLineAsync(
            $"{g}Name:{reset} {c}{_player.Name}{reset}" +
            new string(' ', Math.Max(1, rightColumnOffset - namePlain.Length)) +
            RCol("Lives/CP:", livesCP, 18));

        // Line 2: Race (18) + Exp (20) + Perception
        await _client.SendLineAsync(
            Cell("Race: ", race.Name, topLeftWidth) +
            Cell("Exp: ", _player.Experience.ToString(), topMiddleWidth) +
            RCol("Perception:", _player.Perception.ToString()));

        // Line 3: Class (18) + Level (20) + Stealth
        await _client.SendLineAsync(
            Cell("Class: ", cls.Name, topLeftWidth) +
            Cell("Level: ", _player.Level.ToString(), topMiddleWidth) +
            RCol("Stealth:", _player.Stealth.ToString()));

        // Line 4: Hits (18) + Armour Class (20) + Thievery
        await _client.SendLineAsync(
            hitsCell +
            MiddleValueCell("Armour Class:", acStr, 6) +
            RCol("Thievery:", _player.Thievery.ToString()));

        // Line 5: Mana/Kai (18) + Spellcasting (20) + Traps
        await _client.SendLineAsync(
            resourceCell +
            (showSpellcasting
                ? Cell("Spellcasting: ", _player.SpellCasting.ToString(), topMiddleWidth)
                : new string(' ', topMiddleWidth)) +
            RCol("Traps:", _player.Traps.ToString()));

        // Line 6: Empty (38) + Picklocks
        await _client.SendLineAsync(
            new string(' ', rightColumnOffset) +
            RCol("Picklocks:", _player.Picklocks.ToString()));

        // Stats section
        // Line 7: Strength (14+4) + Agility (12+6) + Tracking
        await _client.SendLineAsync(
            Stat1("Strength:", _player.Strength, strColor) +
            Stat2("Agility:", _player.Agility, aglColor) +
            RCol("Tracking:", _player.Tracking.ToString()));

        // Line 8: Intellect (14+4) + Health (12+6) + Martial Arts
        await _client.SendLineAsync(
            Stat1("Intellect:", _player.Intellect, intColor) +
            Stat2("Health:", _player.Health, heaColor) +
            RCol("Martial Arts:", _player.MartialArts.ToString()));

        // Line 9: Willpower (14+4) + Charm (12+6) + MagicRes
        await _client.SendLineAsync(
            Stat1("Willpower:", _player.Willpower, wilColor) +
            Stat2("Charm:", _player.Charm, chmColor) +
            RCol("MagicRes:", _player.MagicResist.ToString()));

        // For each active spell slot, look up the
        // spell's Descriptive Message ability (115) and print only that message's Line3.
        // No spell name and no rounds-remaining counter — the original never exposed either, so the
        // helper text "ice storm (11 rounds remaining)" we used to emit confused Megamud's parser.
        // Buffs typically carry a Line3 like "You are protected by ..."; debuffs carry suffering
        // text like "You are freezing!". Spells without an ability 115 (or whose message has an
        // empty Line3) contribute no row.
        foreach (var active in _player.ActiveSpells)
        {
            if (active.SpellId <= 0
                || !_world.Database.Spells.TryGetValue(active.SpellId, out var activeSpell)
                || !activeSpell.Abilities.TryGetValue(SpellDescriptiveMessageAbilityId, out int descMsgId)
                || descMsgId <= 0
                || !_world.Database.Messages.TryGetValue(descMsgId, out var descMsg)
                || string.IsNullOrWhiteSpace(descMsg.Line3))
            {
                continue;
            }

            await _client.SendLineAsync($"{MudAnsi.White}{descMsg.Line3}{reset}");
        }
    }

    // Ability 115 ("Descriptive Message"): on a spell row, the value points at a Messages
    // entry whose Line3 is the persistent self-status text shown for that active spell.
    private const int SpellDescriptiveMessageAbilityId = 115;

    // Phoenix Feather quest craft timer cast by `ask Morukai components` (textblock 1448 `cast 614`).
    private const int MorukaiTempSpellId = 614;
    // Active-spell durations tick on the medium world tick (GameWorld.MediumTickInterval = 3s).
    private const double MediumTickSecondsForTimerDisplay = 3.0;

    private async Task HandleStatsAll(string targetArgument)
    {
        SynchronizeLightState();
        RecalcEquipment();

        // Resolve the optional target BEFORE any of the sheet is written, so an unknown name is a single
        // clean line instead of a full sheet with an error stapled to the bottom.
        MonsterInstance? statTarget = null;
        if (!string.IsNullOrWhiteSpace(targetArgument))
        {
            if (!TryResolveStatTarget(targetArgument, out var resolvedTarget, out string targetFailure))
            {
                await _client.SendLineAsync($"{MudAnsi.BrightRed}{targetFailure}{MudAnsi.Reset}");
                return;
            }

            statTarget = resolvedTarget;
        }

        var cls = _world.Database.Classes[_player.ClassId];
        var weapon = GetEquippedWeaponForAttack();
        string g = MudAnsi.Green;
        string c = MudAnsi.Cyan;
        string reset = MudAnsi.Reset;
        const int middleLabelStart = 23;
        const int rightLabelStart = 49;
        const int leftRegenValueEnd = 15;
        const int leftStatValueEnd = 12;
        const int middleValueEnd = 37;
        const int rightValueEnd = 66;
        const int attackSwingsValueEnd = 17;
        const int attackAccuracyValueEnd = 24;
        const int attackMinValueEnd = 30;
        const int attackMaxValueEnd = 36;
        const int attackQuicknessValueEnd = 46;
        const int attackAverageValueEnd = 64;
        const int spellCastsValueEnd = 17;
        const int spellDiffValueEnd = 24;
        const int spellMinValueEnd = 30;
        const int spellMaxValueEnd = 36;
        const int spellAverageValueEnd = 59;

        string BuildLine(params (int Start, string Text, string Color)[] segments)
        {
            var sb = new StringBuilder();
            int visiblePosition = 0;

            foreach (var (start, text, color) in segments)
            {
                if (start > visiblePosition)
                    sb.Append(' ', start - visiblePosition);

                sb.Append(color).Append(text).Append(reset);
                visiblePosition = start + text.Length;
            }

            return sb.ToString();
        }

        (int Start, string Text, string Color) GreenAt(int start, string text) => (start, text, g);
        (int Start, string Text, string Color) CyanAt(int start, string text) => (start, text, c);
        (int Start, string Text, string Color) CyanRight(int end, string text) => (Math.Max(0, end - text.Length + 1), text, c);

        int protectionEvil = _player.GetActiveAbilityValue(_world.Database, 24);
        int protectionGood = _player.GetActiveAbilityValue(_world.Database, 25);
        int blurDefence = _player.GetActiveAbilityValue(_world.Database, 10);
        int partyDefence = _player.PartyDefenceModifier;
        int baseAc = _player.GetTotalAC();
        int acVsEvil = baseAc + blurDefence + partyDefence + protectionEvil;
        int acVsGood = baseAc + blurDefence + partyDefence + protectionGood;

        // Regen is reported straight from the tick's own formulas (GameWorld.WorldTick regen helpers),
        // never recomputed here. The old panel invented its own numbers — Health/5 for HP and
        // SpellCasting/2 for mana — which matched nothing the engine did, read 0 for every Kai class
        // (Kai classes have no SpellCasting by definition), and printed the clamp ceiling as "/60" and
        // "/30" as though those were cadences. Shown as amount-per-interval, with the rest/meditate
        // pulse alongside it, because those are the numbers a player actually feels.
        int hpRegen = GameWorld.GetPassiveHpRegen(_player);
        int hpRestRegen = GameWorld.GetRestHpRegen(_player);
        int manaRegen = _world.GetPassiveManaRegen(_player);
        int manaMeditateRegen = _world.GetMeditateManaRegen(_player);
        string hpRegenText = $"{hpRegen}/{GameWorld.PassiveRegenIntervalSeconds}s";
        string hpRestText = $"{hpRestRegen}/{GameWorld.RestRegenIntervalSeconds}s";
        string manaRegenText = _player.MaxMana > 0
            ? $"{manaRegen}/{GameWorld.PassiveRegenIntervalSeconds}s"
            : "-";
        string manaMeditateText = _player.MaxMana > 0
            ? $"{manaMeditateRegen}/{GameWorld.MeditateRegenIntervalSeconds}s"
            : "-";

        string resourceLabel = Player.UsesKai(cls) ? "Kai" : "Mana";
        string resourceRegenLabel = Player.UsesKai(cls) ? "Kai Regen:" : "MA Regen:";
        string resourceMaxLabel = $"Max {resourceLabel}:";

        await _client.SendLineAsync(BuildLine(
            GreenAt(0, "Name:"),
            CyanAt(6, _player.Name),
            GreenAt(rightLabelStart, "Illu:"),
            CyanRight(rightValueEnd, _player.Illumination.ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(0, "HP Regen:"),
            CyanRight(leftRegenValueEnd, hpRegenText),
            GreenAt(middleLabelStart, "AC vs Evil:"),
            CyanRight(middleValueEnd, acVsEvil.ToString()),
            GreenAt(rightLabelStart, "Cold Resist:"),
            CyanRight(rightValueEnd, _player.GetActiveAbilityValue(_world.Database, 3).ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(0, resourceRegenLabel),
            CyanRight(leftRegenValueEnd, manaRegenText),
            GreenAt(middleLabelStart, "Shadow:"),
            CyanRight(middleValueEnd, _player.Shadow.ToString()),
            GreenAt(rightLabelStart, "Water Resist:"),
            CyanRight(rightValueEnd, _player.GetActiveAbilityValue(_world.Database, 147).ToString())));

        // The boosted pulses, on their own line: resting is the passive base ×3 on the ~21s fast-tick
        // cycle, meditate is the base ×1 on the ~15s cycle. Both run ALONGSIDE the passive trickle
        // rather than replacing it, so a resting player gets both.
        await _client.SendLineAsync(BuildLine(
            GreenAt(0, "Resting:"),
            CyanRight(leftRegenValueEnd, hpRestText),
            GreenAt(middleLabelStart, "Meditate:"),
            CyanRight(middleValueEnd, manaMeditateText)));

        await _client.SendLineAsync(BuildLine(
            GreenAt(0, "Max HP:"),
            CyanRight(leftStatValueEnd, _player.MaxHP.ToString()),
            GreenAt(middleLabelStart, "Party:"),
            CyanRight(middleValueEnd, partyDefence.ToString()),
            GreenAt(rightLabelStart, "Fire Resist:"),
            CyanRight(rightValueEnd, _player.GetActiveAbilityValue(_world.Database, 5).ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(0, resourceMaxLabel),
            CyanRight(leftStatValueEnd, _player.MaxMana.ToString()),
            GreenAt(middleLabelStart, "Prev:"),
            CyanRight(middleValueEnd, protectionEvil.ToString()),
            GreenAt(rightLabelStart, "Stone Resist:"),
            CyanRight(rightValueEnd, _player.GetActiveAbilityValue(_world.Database, 65).ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(0, "Encum:"),
            CyanRight(leftStatValueEnd, _player.Encumbrance.ToString()),
            GreenAt(middleLabelStart, "Prgd:"),
            CyanRight(middleValueEnd, protectionGood.ToString()),
            GreenAt(rightLabelStart, "Lit Resist:"),
            CyanRight(rightValueEnd, _player.GetActiveAbilityValue(_world.Database, 66).ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(middleLabelStart, "vs Good:"),
            CyanRight(middleValueEnd, acVsGood.ToString()),
            GreenAt(rightLabelStart, "Dodge:"),
            CyanRight(rightValueEnd, _player.GetDodge().ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(rightLabelStart, "Crits:"),
            CyanRight(rightValueEnd, _player.GetCrits().ToString())));

        await _client.SendLineAsync(BuildLine(
            GreenAt(rightLabelStart, "Spell Damage:"),
            CyanRight(rightValueEnd, _player.GetActiveAbilityValue(_world.Database, 165).ToString())));

        // With a target named, the two tables below are replaced by their against-that-monster
        // counterparts (plus the target's own attacks against you) — same engine formulas, resolved
        // instead of abstract. See CommandParser.PlayerState.StatTarget.cs.
        if (statTarget != null)
        {
            // Your melee and the monster's melee sit next to each other so the two sides of a round read
            // as one comparison; spells follow, rather than splitting the pair.
            await ShowStatTargetHeaderAsync(statTarget);
            await ShowStatTargetAttacksAsync(cls, weapon, statTarget);
            await ShowStatTargetIncomingAsync(statTarget);
            await ShowStatTargetSpellsAsync(statTarget);
        }
        else
        {
            await _client.SendLineAsync(string.Empty);
            await _client.SendLineAsync($"{g}Attacks:{reset}");
            await _client.SendLineAsync(BuildLine(
                GreenAt(0, "Type"),
                GreenAt(12, "Swings"),
                GreenAt(21, "Accy"),
                GreenAt(28, "Min"),
                GreenAt(34, "Max"),
                GreenAt(40, "QnD(Total)"),
                GreenAt(53, "Avg/Rnd(+xtra)")));

            foreach (var attackRow in BuildAllStatsAttackRows(cls, weapon))
            {
                string qnd = string.IsNullOrEmpty(attackRow.QuicknessAndDamage)
                    ? string.Empty
                    : attackRow.QuicknessAndDamage;
                var (rowMin, rowMax) = ProjectAttackBoundsAgainst(attackRow, damageResistTenths: 0);
                double rowSwings = EffectiveProjectedSwings(attackRow.AverageSwings);
                int averageRoundDamage = (int)Math.Round(((rowMin + rowMax) / 2.0) * rowSwings, MidpointRounding.AwayFromZero);
                int averageRoundWithExtraDamage = averageRoundDamage
                    + (int)Math.Round(attackRow.ExtraDamagePerHit * rowSwings, MidpointRounding.AwayFromZero);
                string average = averageRoundDamage == averageRoundWithExtraDamage
                    ? averageRoundDamage.ToString(CultureInfo.InvariantCulture)
                    : $"{averageRoundDamage.ToString(CultureInfo.InvariantCulture)}({averageRoundWithExtraDamage.ToString(CultureInfo.InvariantCulture)})";

                var attackSegments = new List<(int Start, string Text, string Color)>
                {
                    GreenAt(0, attackRow.Name),
                    CyanRight(attackSwingsValueEnd, attackRow.SwingDisplay),
                    CyanRight(attackAccuracyValueEnd, attackRow.Accuracy.ToString(CultureInfo.InvariantCulture)),
                    CyanRight(attackMinValueEnd, rowMin.ToString(CultureInfo.InvariantCulture)),
                    CyanRight(attackMaxValueEnd, rowMax.ToString(CultureInfo.InvariantCulture)),
                };

                if (!string.IsNullOrEmpty(qnd))
                    attackSegments.Add(CyanRight(attackQuicknessValueEnd, qnd));

                attackSegments.Add(CyanRight(attackAverageValueEnd, average));
                await _client.SendLineAsync(BuildLine([.. attackSegments]));
            }

            await _client.SendLineAsync(string.Empty);
            await _client.SendLineAsync($"{g}Spells:{reset}");
            await _client.SendLineAsync(BuildLine(
                GreenAt(0, "Short Name"),
                GreenAt(13, "Casts"),
                GreenAt(21, "Diff"),
                GreenAt(28, "Min"),
                GreenAt(34, "Max"),
                GreenAt(53, "Avg/Rnd")));

            var spellRows = BuildAllStatsSpellRows();
            if (spellRows.Count == 0)
            {
                await _client.SendLineAsync($"{g}None known.{reset}");
            }
            else
            {
                foreach (var spellRow in spellRows)
                {
                    await _client.SendLineAsync(BuildLine(
                        GreenAt(0, spellRow.ShortName),
                        CyanRight(spellCastsValueEnd, spellRow.Casts.ToString(CultureInfo.InvariantCulture)),
                        CyanRight(spellDiffValueEnd, spellRow.Diff.ToString(CultureInfo.InvariantCulture)),
                        CyanRight(spellMinValueEnd, spellRow.MinDamage?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                        CyanRight(spellMaxValueEnd, spellRow.MaxDamage?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                        CyanRight(spellAverageValueEnd, spellRow.AverageDamage?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)));
                }
            }
        }

        // Active Spells block — `stat all` is mmudreborn-specific (Megamud only parses the compact
        // `stat` output, which stays stock-faithful), so we can show name + descriptive Line3 + the
        // remaining-rounds counter together: "Ice Storm - You are freezing! (11 rounds remaining)".
        // Spells with no descriptive Line3 fall back to "<name> (N rounds remaining)".
        if (_player.ActiveSpells.Count > 0)
        {
            await _client.SendLineAsync(string.Empty);
            await _client.SendLineAsync($"{g}Active Spells:{reset}");

            foreach (var active in _player.ActiveSpells)
            {
                if (active.SpellId <= 0 || !_world.Database.Spells.TryGetValue(active.SpellId, out var activeSpell))
                    continue;

                // "morukai temp" (#614) is not a buff the player should see as a raw combat spell — it is
                // the Phoenix Feather quest craft timer (ability 152 "Rune") that gates `ask Morukai return`.
                // Render it as a plain-language countdown instead of "morukai temp (N rounds remaining)".
                // RemainingDuration counts medium ticks (3s each — GameWorld.MediumTickInterval), so convert
                // to minutes, rounding up so an in-progress timer never reads "0 minutes".
                if (active.SpellId == MorukaiTempSpellId)
                {
                    int minutesLeft = Math.Max(1, (int)Math.Ceiling(active.RemainingDuration * MediumTickSecondsForTimerDisplay / 60.0));
                    await _client.SendLineAsync(
                        $"{MudAnsi.BrightCyan}Morukai will be done in {minutesLeft} minute{(minutesLeft == 1 ? string.Empty : "s")}.{reset}");
                    continue;
                }

                string descSuffix = string.Empty;
                if (activeSpell.Abilities.TryGetValue(SpellDescriptiveMessageAbilityId, out int descMsgId)
                    && descMsgId > 0
                    && _world.Database.Messages.TryGetValue(descMsgId, out var descMsg)
                    && !string.IsNullOrWhiteSpace(descMsg.Line3))
                {
                    descSuffix = $" - {descMsg.Line3}";
                }

                await _client.SendLineAsync(
                    $"{MudAnsi.BrightCyan}{activeSpell.Name}{reset}{MudAnsi.White}{descSuffix}{reset} {g}({active.RemainingDuration} rounds remaining){reset}");
            }
        }

        await ShowRecentDeathsAsync(g, reset);
    }

    // Non-stock recent-death log, appended to the bottom of `stat all` when SYSOP CONFIGURE DEATHLOG is
    // on. Silent when the feature is off OR the character has not died yet, so a stock-flavoured realm
    // and a fresh character both see the sheet exactly as before.
    //
    // Timestamps are stored UTC and rendered in the SERVER's local time -- the board has one clock its
    // players share, and a UTC time would read as wrong to everyone.
    private async Task ShowRecentDeathsAsync(string g, string reset)
    {
        if (!_world.DeathLogEnabled || _player.DeathLog.Count == 0)
            return;

        await _client.SendLineAsync(string.Empty);
        await _client.SendLineAsync($"{g}Recent Deaths:{reset}");

        foreach (var death in _player.DeathLog)
        {
            string when = death.WhenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            string where = string.IsNullOrWhiteSpace(death.RoomName)
                ? $"{death.MapNumber}/{death.RoomNumber}"
                : $"{death.MapNumber}/{death.RoomNumber} {death.RoomName}";

            await _client.SendLineAsync(
                $"{MudAnsi.White}{when}{reset} {MudAnsi.BrightCyan}{where}{reset} {g}-{reset} {MudAnsi.BrightRed}{death.Killer}{reset}");
        }
    }

    // One row of the `stat all` Attacks table. BaseMin/BaseMax are the RAW weapon/unarmed bounds, before
    // the attack type's pre-roll damage% and its final multiplier — kept raw because the against-a-target
    // view has to subtract the target's damage resist BETWEEN those two steps (stock order: roll the
    // pct-adjusted band, subtract DR/10, then ×3 bash / ×5 smash). AverageSwings is the swing count the
    // damage columns are derived from.
    private readonly record struct AllStatsAttackRow(
        string Name,
        string SwingDisplay,
        double AverageSwings,
        int Accuracy,
        int BaseMin,
        int BaseMax,
        string QuicknessAndDamage,
        CombatEngine.AttackType AttackType,
        int ExtraDamagePerHit);

    private List<AllStatsAttackRow> BuildAllStatsAttackRows(CharacterClass cls, Item? weapon)
    {
        var rows = new List<AllStatsAttackRow>();

        double singleActionCount = 1.0;
        // Mystic action counts come from the shared energy pool (martial weapon-speed per attack
        // type), not the legacy GetSwings(cap 10) — punch/kick/jumpkick differ by speed.
        double punchActionCount = MysticActionCount(cls, "punch");
        double kickActionCount = MysticActionCount(cls, "kick");
        double jumpkickActionCount = MysticActionCount(cls, "jumpkick");
        int extraDamagePerHit = _player.GetActiveAbilityValue(_world.Database, 4);
        // Bare fists are NOT a one-swing special case: stock computes energy use with the
        // weaponless speed constant (see CombatEngine.StockWeaponlessSpeed) and budgets swings from the
        // same pool, so the preview must cover weapon == null too or the row under-reports the round.
        WeaponSwingPreview attackPreview = CombatEngine.GetWeaponSwingPreview(_player, cls, weapon, db: _world.Database);
        WeaponSwingPreview bashPreview = CombatEngine.GetWeaponSwingPreview(_player, cls, weapon, isBashing: true, db: _world.Database);

        // Unarmed types 1/2/3 use the same
        // stat-derived AV formula as the weapon path; only the weapon-skill and weapon.Accy terms
        // are absent. Stat rows must include GetBaseAccuracy so the display matches what combat
        // actually rolls — passing only PartyAccuracyModifier showed AV≈0.
        int unarmedAccuracy = _player.GetBaseAccuracy(cls.CombatLvl) + _player.PartyAccuracyModifier;

        if (_player.HasPunch && weapon == null)
        {
            AddAttackRow(rows, "Punch", FormatSwingDisplay(punchActionCount), punchActionCount, unarmedAccuracy, _player.GetPunchMin(), _player.GetPunchMax(), CombatEngine.AttackType.Punch, extraDamagePerHit);
        }
        else
        {
            var (baseMin, baseMax, attackAccuracy, _) = CombatEngine.GetPlainWeaponAttackProfile(_player, cls, weapon);
            attackAccuracy += _player.PartyAccuracyModifier;
            double attackSwings = WeaponSwingPreviewCalculator.CalculateAverageRoundSwings(attackPreview.EnergyUse);
            // The swing budget above now covers bare fists, but the Q&D/crit column stays a WEAPON column:
            // showing it for weaponless would add a field to the unarmed row that has never been there,
            // which is a stat-table change rather than part of the swing-count fix.
            string qnd = weapon != null
                ? $"{attackPreview.QuickAndDeadlyBonus}({CombatEngine.GetWeaponCritChance(_player, cls, weapon, db: _world.Database)})"
                : string.Empty;

            AddAttackRow(rows, "Attack", BuildWeaponSwingDisplay(attackPreview), attackSwings, attackAccuracy, baseMin, baseMax, CombatEngine.AttackType.Normal, extraDamagePerHit, qnd);
        }

        if (_player.HasBash)
        {
            var (baseMin, baseMax, bashAccuracy, _) = CombatEngine.GetPlainWeaponAttackProfile(_player, cls, weapon);
            bashAccuracy += _player.PartyAccuracyModifier;
            double bashSwings = WeaponSwingPreviewCalculator.CalculateAverageRoundSwings(bashPreview.EnergyUse);
            AddAttackRow(rows, "Bash", BuildWeaponSwingDisplay(bashPreview), bashSwings, bashAccuracy, baseMin, baseMax, CombatEngine.AttackType.Bash, extraDamagePerHit);
        }

        if (_player.HasSmash)
        {
            var (baseMin, baseMax, smashAccuracy, _) = CombatEngine.GetPlainWeaponAttackProfile(_player, cls, weapon);
            smashAccuracy += _player.PartyAccuracyModifier;
            // Smash is NOT a bash-priced row: the fighter marshal replaces the EU with the
            // attacker's stamina cap, so the round always buys exactly one swing (the live path
            // hard-codes the same constant). Reusing the bash EU here showed a multi-swing round --
            // and an equally inflated average-damage column -- for anything the bash budget could
            // swing twice with.
            const double smashSwings = CombatEngine.SmashSwingsPerRound;
            AddAttackRow(rows, "Smash", FormatSwingDisplay(smashSwings), smashSwings, smashAccuracy, baseMin, baseMax, CombatEngine.AttackType.Smash, extraDamagePerHit);
        }

        if ((_player.Stealth > 0 || _player.HasActiveAbility(_world.Database, 27) || _player.HasPerfectStealth)
            && CanUseWeaponForBackstab(weapon))
        {
            // Backstab is always attack type 4 (no accuracy penalty; AV - AC).
            var (baseMin, baseMax, backstabAccuracy, _) = CombatEngine.GetBackstabAttackProfile(_player, weapon);
            backstabAccuracy += _player.PartyAccuracyModifier;
            AddAttackRow(rows, "Backstab", FormatSwingDisplay(singleActionCount), singleActionCount, backstabAccuracy, baseMin, baseMax, CombatEngine.AttackType.Backstab, extraDamagePerHit);
        }

        if (_player.HasPunch && weapon != null)
        {
            AddAttackRow(rows, "Punch", FormatSwingDisplay(punchActionCount), punchActionCount, unarmedAccuracy, _player.GetPunchMin(), _player.GetPunchMax(), CombatEngine.AttackType.Punch, extraDamagePerHit);
        }

        if (_player.HasKick)
        {
            AddAttackRow(rows, "Kick", FormatSwingDisplay(kickActionCount), kickActionCount, unarmedAccuracy, _player.GetKickMin(), _player.GetKickMax(), CombatEngine.AttackType.Kick, extraDamagePerHit);
        }

        if (_player.HasJumpkick)
        {
            // Jumpkick (attack-type 3) does NOT receive the
            // party-rank to-hit modifier, so its displayed accuracy excludes it (unlike punch/kick).
            int jumpkickAccuracy = unarmedAccuracy - _player.PartyAccuracyModifier;
            AddAttackRow(rows, "Jumpkick", FormatSwingDisplay(jumpkickActionCount), jumpkickActionCount, jumpkickAccuracy, _player.GetJumpkickMin(), _player.GetJumpkickMax(), CombatEngine.AttackType.Jumpkick, extraDamagePerHit);
        }

        return rows;
    }

    // Average swings/round for an unarmed attack type, from the shared energy pool (matches live
    // combat's GetUnarmedRoundSwings EU rather than the legacy GetSwings cap).
    private double MysticActionCount(CharacterClass cls, string attackType)
        => WeaponSwingPreviewCalculator.CalculateAverageRoundSwings(
            CombatEngine.GetUnarmedSwingPreview(_player, cls, attackType, _world.Database).EnergyUse);

    private static void AddAttackRow(
        List<AllStatsAttackRow> rows,
        string name,
        string swingDisplay,
        double averageActionCount,
        int accuracy,
        int baseMin,
        int baseMax,
        CombatEngine.AttackType attackType,
        int extraDamagePerHit,
        string quicknessAndDamage = "")
    {
        rows.Add(new AllStatsAttackRow(
            name,
            swingDisplay,
            averageActionCount,
            accuracy,
            baseMin,
            baseMax,
            quicknessAndDamage,
            attackType,
            extraDamagePerHit));
    }

    // The swing count the damage columns multiply by. Capped at 5 so a very fast weapon's projected
    // round damage stays a sane headline number rather than the 6-swing theoretical maximum.
    private const double MaxProjectedRoundSwings = 5.0;

    private static double EffectiveProjectedSwings(double averageSwings)
        => Math.Min(averageSwings, MaxProjectedRoundSwings);

    private static string FormatSwingDisplay(double swings)
    {
        return swings.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string BuildWeaponSwingDisplay(WeaponSwingPreview preview)
    {
        const int maxSwingDisplayLength = 6;

        var cycle = WeaponSwingPreviewCalculator.CalculateSwingCycle(preview.EnergyUse);
        int patternLength = FindRepeatingSwingPatternLength(cycle);
        string compactPattern = BuildCompactSwingPattern(cycle, patternLength);
        if (!string.IsNullOrEmpty(compactPattern) && compactPattern.Length <= maxSwingDisplayLength)
            return compactPattern;

        double averageSwings = WeaponSwingPreviewCalculator.CalculateAverageRoundSwings(preview.EnergyUse);
        return FormatSwingDisplay(Math.Min(averageSwings, WeaponSwingPreviewCalculator.MaxWeaponSwingsPerRound));
    }

    private static int FindRepeatingSwingPatternLength(IReadOnlyList<int> cycle)
    {
        if (cycle.Count == 0)
            return 0;

        for (int candidateLength = 1; candidateLength <= cycle.Count; candidateLength++)
        {
            bool matches = true;
            for (int index = candidateLength; index < cycle.Count; index++)
            {
                if (cycle[index] != cycle[index % candidateLength])
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
                return candidateLength;
        }

        return cycle.Count;
    }

    private static string BuildCompactSwingPattern(IReadOnlyList<int> cycle, int patternLength)
    {
        if (cycle.Count == 0 || patternLength <= 0)
            return string.Empty;

        var builder = new StringBuilder();
        int currentSwings = cycle[0];
        int runLength = 1;

        for (int index = 1; index < patternLength; index++)
        {
            if (cycle[index] == currentSwings)
            {
                runLength++;
                continue;
            }

            AppendSwingPatternSegment(builder, currentSwings, runLength);
            currentSwings = cycle[index];
            runLength = 1;
        }

        AppendSwingPatternSegment(builder, currentSwings, runLength);
        return builder.ToString();
    }

    private static void AppendSwingPatternSegment(StringBuilder builder, int swings, int runLength)
    {
        if (builder.Length > 0)
            builder.Append('/');

        builder.Append(swings.ToString(CultureInfo.InvariantCulture));
        if (runLength > 1)
            builder.Append('x').Append(runLength.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The damage band a row actually lands, in stock order (CalculateAttack): the attack type's pre-roll
    /// damage% scales the raw weapon band, the defender's damage resist comes off the ROLL (DR is carried
    /// in tenths, so DR/10 whole points), and only then does the bash ×3 / smash ×5 multiplier apply —
    /// which is why a target's DR is subtracted before the multiplier, not after. Pass
    /// <paramref name="damageResistTenths"/> = 0 for the untargeted sheet.
    /// </summary>
    private static (int MinDamage, int MaxDamage) ProjectAttackBoundsAgainst(AllStatsAttackRow row, int damageResistTenths)
    {
        int minDamage = row.BaseMin;
        int maxDamage = Math.Max(row.BaseMin, row.BaseMax);
        int damagePct = CombatEngine.GetAttackTypeDamagePct(row.AttackType);

        if (damagePct != 0)
        {
            minDamage = minDamage * (100 + damagePct) / 100;
            maxDamage = maxDamage * (100 + damagePct) / 100;
        }

        int resist = damageResistTenths / 10;
        if (resist != 0)
        {
            minDamage = Math.Max(0, minDamage - resist);
            maxDamage = Math.Max(0, maxDamage - resist);
        }

        int multiplier = CombatEngine.GetAttackTypeDamageMultiplier(row.AttackType);
        minDamage *= multiplier;
        maxDamage *= multiplier;

        return (minDamage, Math.Max(minDamage, maxDamage));
    }

    // Spell is carried alongside the display fields so the against-a-target view can re-resolve the
    // same row through the resist model (magic resistance + the spell's element) without rebuilding it.
    private List<(string ShortName, int Casts, int Diff, int? MinDamage, int? MaxDamage, int? AverageDamage, GameSpell Spell)> BuildAllStatsSpellRows()
    {
        var rows = new List<(string ShortName, int Casts, int Diff, int? MinDamage, int? MaxDamage, int? AverageDamage, GameSpell Spell)>();
        int spellDamageBonus = _player.GetActiveAbilityValue(_world.Database, 165);
        int currentResource = Math.Max(0, _player.CurrentMana);

        foreach (var spell in GetKnownSpells())
        {
            if (spell.MinBase <= 0 || spell.MaxBase < spell.MinBase)
                continue;

            int resourceCost = spell.ManaCost > 0 ? spell.ManaCost : spell.EnergyCost;
            int casts = resourceCost > 0
                ? Math.Max(0, currentResource / resourceCost)
                : 1;

            // The Min/Max/Avg-per-round columns describe an HP roll (damage or healing). Utility/buff
            // spells (blur, illuminate, protective wards) don't roll HP — their MinBase/MaxBase hold an
            // unrelated magnitude (blur's AC bonus, illuminate's item ref), so a "5 / 5 / 5" or
            // "4012 / 4012 / 4012" here is nonsense. List the spell (name/casts/diff) but leave the HP
            // columns blank rather than print a misleading number.
            if (!SpellHasHpRoll(spell))
            {
                rows.Add((spell.Short, casts, spell.Diff, null, null, null, spell));
                continue;
            }

            // Show the SAME capped band the cast actually rolls (ComputeSpellMagnitudeBand): both
            // sides scale by effLvl = min(level, Cap). The old display used an uncapped
            // (level-ReqLevel)*MaxInc bonus added to both MinBase/MaxBase, so it over-stated the range
            // for level-capped spells and mis-scaled the min side.
            var (bandMin, bandMax) = ComputeSpellMagnitudeBand(spell, _player.Level);
            int minDamage = bandMin + spellDamageBonus;
            int maxDamage = bandMax + spellDamageBonus;
            int averageDamage = (int)Math.Floor((minDamage + maxDamage) / 2.0);

            rows.Add((spell.Short, casts, spell.Diff, minDamage, maxDamage, averageDamage, spell));
        }

        return rows;
    }

}
