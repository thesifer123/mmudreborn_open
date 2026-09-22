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
    private static readonly string[] BankExchangeRateLines =
    {
        "The currency conversion rates are:",
        "100 platinum pieces == 1 Runic coins",
        "100 gold crowns == 1 platinum piece",
        "10 silver nobles == 1 gold crown",
        "10 copper farthings == 1 silver noble",
    };

    private List<int> GetVisibleRoomItemIds(Room room)
    {
        var visibleItemIds = new List<int>();
        var runtimeItems = _world.GetVisibleGroundItems(room.MapNumber, room.RoomNumber);
        var matchedRuntimeEntries = new bool[runtimeItems.Count];

        foreach (var itemId in room.GetPlacedItemIds())
        {
            if (!_world.Database.Items.TryGetValue(itemId, out var item))
                continue;

            if (!item.Gettable)
            {
                // Non-gettable visible-placed items render straight from the static Placed field —
                // unless a clearitem has logically removed them (e.g. the destroyed apparatus 819).
                if (!_world.IsPlacedItemRemoved(room.MapNumber, room.RoomNumber, itemId))
                    visibleItemIds.Add(itemId);
                continue;
            }

            for (int index = 0; index < runtimeItems.Count; index++)
            {
                if (matchedRuntimeEntries[index] || runtimeItems[index].ItemId != itemId)
                    continue;

                matchedRuntimeEntries[index] = true;
                visibleItemIds.Add(itemId);
                break;
            }
        }

        for (int index = 0; index < runtimeItems.Count; index++)
        {
            if (!matchedRuntimeEntries[index])
                visibleItemIds.Add(runtimeItems[index].ItemId);
        }

        return visibleItemIds;
    }

    /// <summary>
    /// Show room display. When brief=false, full description is shown.
    /// When brief=true, just room name + contents + exits.
    /// </summary>
    private async Task ShowRoom(bool brief)
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
        {
            await _client.SendLineAsync("You are lost in the void...");
            return;
        }

        // GMCP position feed for opted-in clients (e.g. GigaMud). Emitted before the
        // light/brief gates so the client always learns the authoritative room, even
        // when it is too dark to render a description. No-op for legacy clients.
        await GmcpEmitter.SendRoomInfoAsync(_client, _world, _player, room);

        // The sight gate: a blinded player sees NOTHING — no room desc, items, "Also here",
        // or exits — just "You are blind." The blind flag is tested BEFORE the light level, so blind
        // takes priority over the darkness message. (The bright-light blindness path is handled by the
        // light-level gate below, matching the separate "You are blind!" branch.)
        if (_player.IsBlinded)
        {
            await _client.SendLineAsync("You are blind.");
            return;
        }

        // Light level message (room base + racial/item illumination + equipped light sources)
        int effectiveLight = GetEffectiveRoomLight(room);
        var lightDesc = Room.GetLightDescription(effectiveLight);

        // If too dark, can't see anything
        if (Room.IsTooBlindForLight(effectiveLight))
        {
            if (lightDesc != null)
                await SendLightDescriptionAsync(lightDesc);
            return;
        }

        int paletteId = _player.PaletteId;
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);

        // Room name (always shown - bright cyan)
        await _client.SendLineAsync(GameAnsi.RoomName(room.Name, paletteId));

        // Room description (only in full mode, and only if exists)
        if (!brief && !string.IsNullOrEmpty(room.Description))
        {
            if (room.GangHouseId > 0)
            {
                // Gang-house rooms carry .HSE-style text rendered verbatim by stock,
                // line-by-line with no reflow. Mirror that so the
                // owner-customizable layout/symbols survive.
                await SendVerbatimRoomDescriptionAsync(room.Description);
            }
            else
            {
                // Join lines into a flowing paragraph (dat file stores 7x71-char lines)
                var desc = room.Description.Replace("\r\n", " ").Replace("\n", " ");
                // Collapse any double spaces from joining
                while (desc.Contains("  ")) desc = desc.Replace("  ", " ");
                await SendWrappedRoomDescriptionAsync(desc.Trim());
            }
            await ShowBankExchangeRatesAsync(room, emitRates: true);
        }

        await ShowGroundNoticeHereAsync(room, paletteId);

        // "Also here: %s%s%s%s" with per-entity color codes.
        // Players are listed before monsters.
        var alsoHereParts = BuildAlsoHereParts(_player.CurrentMapNumber, _player.CurrentRoomNumber);

        if (alsoHereParts.Count > 0)
        {
            await SendWrappedEntryListAsync(
                $"{palette.Get(GameColorRole.AlsoHereLabel)}Also here: {MudAnsi.Reset}",
                alsoHereParts,
                $"{MudAnsi.Reset}{palette.Get(GameColorRole.AlsoHereSeparator)}, {MudAnsi.Reset}",
                $"{MudAnsi.Reset}{palette.Get(GameColorRole.AlsoHereSeparator)}.{MudAnsi.Reset}");
        }

        // Exits (green)
        await _client.SendLineAsync(GameAnsi.Exits($"Obvious exits: {_world.GetVisibleExitString(_player, room)}", paletteId));

        // Light level
        if (lightDesc != null)
            await SendLightDescriptionAsync(lightDesc);
    }

    // Cosmetic display arrays (stock-faithful, matching CharacterCreation)
    private static readonly string[] HairLengths = { "", "short ", "shoulder-length ", "long ", "waist-length ", "ankle-length " };
    private static readonly string[] HairColours = { "black", "white", "silver", "red", "brown", "dark-brown", "blonde", "green", "blue", "grey" };
    private static readonly string[] EyeColours = { "black", "crimson", "yellow", "pale-blue", "sea-blue", "dark-blue", "grey-blue", "slate-grey", "bright-green", "forest-green", "pale-green", "chesnut-brown", "dark-brown", "hazel", "violet", "lavender", "golden" };

    /// <summary>
    /// Initial room render shown when a player enters the realm at login. Honors the player's
    /// brief/verbose preference exactly like movement does — unlike the explicit `look` command, which
    /// always prints the full description. A brand-new character defaults to Verbose (BriefMode=false),
    /// so an absolute-first login still gets the full description.
    /// </summary>
    public Task ShowRoomOnEntryAsync() => ShowRoom(brief: _player.BriefMode);

    /// <summary>look/l command — show room, look at player/monster/item with stock-style disambiguation.</summary>
    public async Task HandleLook(string target = "")
    {
        // LOOK gates the entire command on the sight test: a blinded player can't look at the
        // room, a direction, a monster, a player, or an item — it all collapses to "You are blind."
        if (_player.IsBlinded)
        {
            await _client.SendLineAsync("You are blind.");
            return;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            await ShowRoom(brief: false);
            // A visible looker is seen scanning the room by everyone else present.
            if (!_player.IsSysopInvisible)
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                    $"{_player.Name} is looking around the room.", _client);
            return;
        }

        var lookTarget = target.Trim();
        if (DirectionAliases.TryGetValue(lookTarget, out var lookDirection))
        {
            var sourceRoom = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
            if (sourceRoom == null)
            {
                await _client.SendLineAsync("You are lost in the void...");
                return;
            }

            var exits = _world.GetVisibleExits(_player, sourceRoom);
            if (!exits.TryGetValue(lookDirection, out var destination))
            {
                string noExitMessage = lookDirection switch
                {
                    "up" => "There are no exits upwards!",
                    "down" => "There are no exits downwards!",
                    _ => $"There are no exits to the {lookDirection}!"
                };
                await _client.SendLineAsync($"{MudAnsi.BrightRed}{noExitMessage}{MudAnsi.Reset}");
                return;
            }

            if (!_world.CanLookThroughExit(_player, sourceRoom, destination, out var lookFailure))
            {
                await _client.SendLineAsync(lookFailure ?? "You don't see anything special that way.");
                return;
            }

            var destinationRoom = _world.GetRoom(destination.TargetMap, destination.TargetRoom);
            if (destinationRoom == null)
            {
                await _client.SendLineAsync("You don't see anything special that way.");
                return;
            }

            _world.BroadcastToRoom(
                _player.CurrentMapNumber,
                _player.CurrentRoomNumber,
                GetDirectionalLookSourceMessage(lookDirection, _player.Name),
                _client);

            _world.BroadcastToRoom(
                destinationRoom.MapNumber,
                destinationRoom.RoomNumber,
                GetDirectionalLookPeekMessage(GetOppositeDirection(lookDirection), _player.Name));

            await ShowRoomPreview(destinationRoom);
            return;
        }

        await LookAtNamedTargetAsync(target);
    }

    // Stock LOOK for a named target. The target lookup scores every match in one pass:
    //   1. monsters in the room — the phrase, then the phrase with trailing words dropped, one word at a
    //      time; an exact full name on the untrimmed phrase wins outright;
    //   2. players in the room (yourself included) — matched on the FIRST WORD only; a hidden player
    //      counts only for someone who can see hidden; an exact name wins;
    //   3. carried items, worn included — an exact name wins; else the first;
    //   4. room items — an exact name wins; a loose visible match is taken only when
    //      it is the very first match of the whole scan; a hidden item that is non-gettable or searched
    //      up is taken outright;
    //   5. known spells — an exact short or full name wins; else the name counts as a match.
    // Two or more matches are ambiguous — unless the last kind that matched was a monster (monsters alone
    // never are). An ambiguous name picks the combat target while in combat, else prints the list.
    // With no match at all: a shop item, then a coin, then the room's own "look" script, then your own
    // name, then "You do not see %s here!".
    private async Task LookAtNamedTargetAsync(string target)
    {
        var result = ResolveStockLookTarget(target.Trim());

        if (result.Ambiguous)
        {
            var combatTarget = _player.CombatTarget;
            if (_player.InCombat && combatTarget != null && !combatTarget.IsDead
                && TargetNameMatcher.MatchesWordPrefix(LookName(combatTarget), target))
            {
                await LookAtCandidate((combatTarget.DisplayName, "monster", combatTarget));
                return;
            }

            await ShowItemDisambiguationAsync(result.Names);
            return;
        }

        if (result.Kind != null)
        {
            if (result.Kind == "player" && result.Obj is Player seen && !ReferenceEquals(seen, _player)
                && seen.IsHidden && !_player.HasSeeHidden)
            {
                await _client.SendLineAsync($"{MudAnsi.BrightRed}You do not see {target.Trim()} here!{MudAnsi.Reset}");
                return;
            }

            await LookAtCandidate((result.Name, result.Kind, result.Obj!));
            return;
        }

        if (await TryLookAtShopItemAsync(target))   // the shop-item display
            return;
        if (await TryLookAtCurrencyAsync(target))    // the coin description
            return;

        // The room's own special-command script ("look book" on the open red book in Tower Bedroom
        // 10/235, textblock 2931). `look` is a recognized command so it never reaches the default-case
        // room-action fallback. (Bug #105.)
        if (await TryHandleRoomAction($"look {target.Trim()}"))
            return;

        if (TargetNameMatcher.MatchesWordPrefix(_player.Name, target))
        {
            await LookAtCandidate((_player.Name, "player", _player));
            return;
        }

        // "You do not see %s here!" (BrightRed), echoing the typed name — NOT the
        // generic "You don't see that anywhere!" (a different stock string used by other commands).
        await _client.SendLineAsync($"{MudAnsi.BrightRed}You do not see {target.Trim()} here!{MudAnsi.Reset}");
    }

    private readonly record struct LookResolution(string? Kind, string Name, object? Obj, bool Ambiguous, List<string> Names);

    private static string LookName(MonsterInstance monster)
        => string.IsNullOrEmpty(monster.DisplayName) ? monster.Name : monster.DisplayName;

    private LookResolution ResolveStockLookTarget(string target)
    {
        var names = new List<string>();
        int count = 0;
        string? kind = null;
        string pickedName = string.Empty;
        object? picked = null;

        LookResolution Found(string foundKind, string name, object obj) => new(foundKind, name, obj, false, names);

        // 1. Monsters, retried with trailing words dropped (the scan does not stop at a match).
        var monsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber)
            .Where(monster => !monster.IsDead)
            .ToList();
        string phrase = target;
        bool trimmed = false;
        while (true)
        {
            foreach (var monster in monsters)
            {
                string name = LookName(monster);
                var rank = TargetNameMatcher.GetMatchRank(name, phrase);
                if (rank == TargetNameMatcher.MatchRank.None)
                    continue;

                kind = "monster";
                if (count == 0)
                {
                    picked = monster;
                    pickedName = monster.DisplayName;
                }
                count++;
                if (!trimmed && rank == TargetNameMatcher.MatchRank.Exact)
                    return Found("monster", monster.DisplayName, monster);
                names.Add(name);
            }

            int lastSpace = phrase.LastIndexOf(' ');
            if (lastSpace < 0)
                break;
            phrase = phrase[..lastSpace].TrimEnd();
            trimmed = true;
        }

        // 2. Players (yourself included), on the first word of the phrase only.
        int firstSpace = target.IndexOf(' ');
        string firstWord = firstSpace < 0 ? target : target[..firstSpace];
        var players = new List<Player> { _player };
        players.AddRange(_world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player));
        foreach (var candidate in players)
        {
            if (!ReferenceEquals(candidate, _player) && candidate.IsHidden && !_player.HasSeeHidden)
                continue;

            var rank = TargetNameMatcher.GetMatchRank(candidate.Name, firstWord);
            if (rank == TargetNameMatcher.MatchRank.None)
                continue;

            kind = "player";
            count++;
            picked = candidate;
            pickedName = candidate.Name;
            if (rank == TargetNameMatcher.MatchRank.Exact)
                return Found("player", candidate.Name, candidate);
            names.Add(candidate.Name);
        }

        // 3. Carried items, worn included.
        var carried = FindMatchingCarriedItems(target, includeEquipped: true);
        if (carried.Count > 0)
        {
            var first = carried[0];
            if (TargetNameMatcher.GetMatchRank(first.Item.Name, target) == TargetNameMatcher.MatchRank.Exact)
                return Found("item", first.Item.Name, first.Item);

            var distinctCarried = carried.Select(m => m.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            kind = "item";
            picked = first.Item;
            pickedName = first.Item.Name;
            count += distinctCarried.Count;
            names.AddRange(distinctCarried);
        }

        // 4. Room items: the visible list, then the hidden one.
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room != null)
        {
            foreach (var itemId in GetVisibleRoomItemIds(room))
            {
                if (!_world.Database.Items.TryGetValue(itemId, out var item))
                    continue;

                var rank = TargetNameMatcher.GetMatchRank(item.Name, target);
                if (rank == TargetNameMatcher.MatchRank.None)
                    continue;
                if (rank == TargetNameMatcher.MatchRank.Exact)
                    return Found("item", item.Name, item);

                count++;
                if (count == 1)
                {
                    kind = "item";
                    picked = item;
                    pickedName = item.Name;
                }
                names.Add(item.Name);
            }

            // A hidden room item is taken outright when it is a permanent fixture (non-gettable — a sign,
            // pedestal, hole described in the room prose; e.g. the Ancient Library "old parchment" #990 in
            // room 9/1008) or has been searched up.
            foreach (var itemId in room.GetHiddenItemIds())
            {
                if (_world.Database.Items.TryGetValue(itemId, out var fixture)
                    && !fixture.Gettable
                    && TargetNameMatcher.MatchesWordPrefix(fixture.Name, target))
                    return Found("item", fixture.Name, fixture);
            }

            var revealed = _player.GetRevealedHiddenItems(room.MapNumber, room.RoomNumber);
            foreach (var entry in _world.GetHiddenGroundItemsWithInstance(room.MapNumber, room.RoomNumber))
            {
                if (revealed.Contains(entry.InstanceId)
                    && _world.Database.Items.TryGetValue(entry.ItemId, out var searchedUp)
                    && TargetNameMatcher.MatchesWordPrefix(searchedUp.Name, target))
                    return Found("item", searchedUp.Name, searchedUp);
            }
        }

        // 5. Known spells.
        foreach (var spell in GetKnownSpells())
        {
            if (!string.IsNullOrWhiteSpace(spell.Short) && spell.Short.Equals(target, StringComparison.OrdinalIgnoreCase))
                return Found("spell", spell.Name, spell);

            var rank = TargetNameMatcher.GetMatchRank(spell.Name, target);
            if (rank == TargetNameMatcher.MatchRank.None)
                continue;
            if (rank == TargetNameMatcher.MatchRank.Exact)
                return Found("spell", spell.Name, spell);

            kind = "spell";
            count++;
            picked = spell;
            pickedName = spell.Name;
            names.Add(spell.Name);
        }

        bool ambiguous = count > 1 && kind != "monster";
        return new LookResolution(ambiguous ? null : kind, pickedName, picked, ambiguous, names);
    }

    private async Task ShowRoomPreview(Room room)
    {
        int effectiveLight = GetEffectiveRoomLight(room);
        var lightDesc = Room.GetLightDescription(effectiveLight);
        if (Room.IsTooBlindForLight(effectiveLight))
        {
            if (lightDesc != null)
                await SendLightDescriptionAsync(lightDesc);
            return;
        }

        int paletteId = _player.PaletteId;
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);

        await _client.SendLineAsync(GameAnsi.RoomName(room.Name, paletteId));

        if (!string.IsNullOrEmpty(room.Description))
        {
            if (room.GangHouseId > 0)
            {
                // Match the in-room rendering: gang-house .HSE text is shown verbatim, not reflowed.
                await SendVerbatimRoomDescriptionAsync(room.Description);
            }
            else
            {
                var desc = room.Description.Replace("\r\n", " ").Replace("\n", " ");
                while (desc.Contains("  ")) desc = desc.Replace("  ", " ");
                await SendWrappedRoomDescriptionAsync(desc.Trim());
            }
            await ShowBankExchangeRatesAsync(room, emitRates: true);
        }

        await ShowGroundNoticeHereAsync(room, paletteId);

        var alsoHereParts = BuildAlsoHereParts(room.MapNumber, room.RoomNumber);

        if (alsoHereParts.Count > 0)
        {
            await SendWrappedEntryListAsync(
                $"{palette.Get(GameColorRole.AlsoHereLabel)}Also here: {MudAnsi.Reset}",
                alsoHereParts,
                $"{MudAnsi.Reset}{palette.Get(GameColorRole.AlsoHereSeparator)}, {MudAnsi.Reset}",
                $"{MudAnsi.Reset}{palette.Get(GameColorRole.AlsoHereSeparator)}.{MudAnsi.Reset}");
        }

        await _client.SendLineAsync(GameAnsi.Exits($"Obvious exits: {_world.GetVisibleExitString(_player, room)}", paletteId));

        if (lightDesc != null)
            await SendLightDescriptionAsync(lightDesc);
    }

    private async Task ShowGroundNoticeHereAsync(Room room, int paletteId)
    {
        var groundItemNames = new List<string>();
        foreach (var itemId in GetVisibleRoomItemIds(room))
        {
            if (_world.Database.Items.TryGetValue(itemId, out var item))
                groundItemNames.Add(item.Name);
        }

        groundItemNames.AddRange(GetVisibleRoomCurrencyParts(room));
        if (groundItemNames.Count == 0)
            return;

        var itemEntries = BuildGroupedNoticeEntries(groundItemNames, paletteId);
        await SendWrappedNoticeHereAsync(itemEntries);
    }

    private Task SendLightDescriptionAsync(string lightDesc)
    {
        return _client.SendLineAsync($"{MudAnsi.White}{lightDesc}{MudAnsi.Reset}");
    }

    // The shared sight gate a sight-dependent command runs BEFORE doing any work.
    //   1. the blind flag → "You are blind." — tested first, so it beats the darkness line;
    //   2. a light level below -150 → the descriptor line ("The room is very dark - you can't see
    //      anything"), which the light level itself formats and the gate prints.
    //
    // Either way the gate returns FALSE and the caller does nothing but burn its command delay — the
    // room's contents are never examined. (Its third branch, a blinding-bright room at >= 901,
    // is modelled separately by Player.IsBlindedByBrightLight; GetEffectiveRoomLight clamps to 900.)
    // Returns true when the player can see well enough for the command to proceed.
    private async Task<bool> PassesSightGateAsync(Room room)
    {
        if (_player.IsBlinded)
        {
            await _client.SendLineAsync("You are blind.");
            return false;
        }

        int effectiveLight = GetEffectiveRoomLight(room);
        if (!Room.IsTooBlindForLight(effectiveLight))
            return true;

        var lightDesc = Room.GetLightDescription(effectiveLight);
        if (lightDesc != null)
            await SendLightDescriptionAsync(lightDesc);
        return false;
    }

    private async Task ShowBankExchangeRatesAsync(Room room, bool emitRates)
    {
        if (!emitRates)
            return;

        if (!TryGetBankInRoom(room, out _))
            return;

        await _client.SendLineAsync();
        foreach (string line in BankExchangeRateLines)
            await _client.SendLineAsync($"{MudAnsi.Cyan}{line}{MudAnsi.Reset}");
    }

    private const int MaxRoomLight = 900;   // the light-level clamp (the Daylight ceiling; >=900 ⇒ blinding)

    // The light level keeps two channels distinct:
    //   • VIEWER-only (ability 0xd "Alter User Light") — the caller's racial darkvision / worn glow items /
    //     user-light spells. Only the caller benefits (the mask example).
    //   • ROOM-WIDE (summed over every player in the room) — ability 14 "Alter Room Light" (
    //     e.g. a Sunsword or starlight) plus each player's currently-readied light source (ability 54
    //     "Alter General Light" / type-6 torch, lamp, illuminate light-ball). A party member's
    //     lit torch lights the room for everyone who walks in behind them.
    private int GetEffectiveRoomLight(Room room)
    {
        SynchronizeLightState();

        // Viewer-only: room base + the caller's personal illumination (ability 13 from race/class/spell)
        // + any worn item carrying ability 13 (RecalculateStats deliberately skips item-13 so it stays a
        // pure light term here, never a stat).
        int effectiveLight = room.Light + _player.Illumination + GetWornUserLightBonus(_player);

        // Room-wide: every occupant's "Alter Room Light" total + their one readied light source. The
        // viewer is one of those occupants, so their own room-light / torch is counted here exactly once.
        // Guard against the viewer not yet being in the room index (mid-login/move) so they never lose
        // the benefit of their own light.
        bool viewerCounted = false;
        foreach (var occupant in _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber))
        {
            effectiveLight += occupant.RoomIllumination + GetReadiedLightSourceBonus(occupant);
            if (ReferenceEquals(occupant, _player))
                viewerCounted = true;
        }
        if (!viewerCounted)
            effectiveLight += _player.RoomIllumination + GetReadiedLightSourceBonus(_player);

        return Math.Min(effectiveLight, MaxRoomLight);
    }

    // Ability 13 item contribution: an item carrying it lights only its bearer (the mask
    // example). RecalculateStats skips item-13 specifically so it is summed here as a viewer-only light
    // term. Deduped by item id to preserve the prior single-count behavior across equip + inventory.
    private int GetWornUserLightBonus(Player player)
    {
        int bonus = 0;
        var counted = new HashSet<int>();

        foreach (var (_, itemId) in player.Equipment)
            if (counted.Add(itemId)
                && _world.Database.Items.TryGetValue(itemId, out var item)
                && item.Abilities.TryGetValue(13, out int illumBonus))
                bonus += illumBonus;

        // The carried-inventory scan applies an
        // item's abilities only when it is NEITHER a weapon (type 1) NOR wearable (equip-loc
        // != 0) — a wearable's light counts solely through the worn array. So a carried mask / the
        // Onyx Earrings (#1521, ability 13 = -999) contribute their personal-light ONLY when equipped; a
        // plain non-wearable light source (torch/lantern) still counts while merely carried. Mirror the same
        // gate RecalculateStats uses for the inventory ability pass (Player.cs).
        foreach (int itemId in player.Inventory)
            if (counted.Add(itemId)
                && _world.Database.Items.TryGetValue(itemId, out var item)
                && item.ItemType != 1 && item.Worn == 0
                && item.Abilities.TryGetValue(13, out int illumBonus))
                bonus += illumBonus;

        return bonus;
    }

    // A player's single currently-readied (lit) light source contributes its ability
    // 54 ("Alter General Light") value — or 100 for a bare type-6 source — to the room for EVERYONE
    // present. Stock tracks one readied slot, so take the single active source, not a sum.
    private int GetReadiedLightSourceBonus(Player player)
    {
        if (!TryGetSingleActiveLightSource(player, out int itemId, out _, out _)
            || !_world.Database.Items.TryGetValue(itemId, out var item))
            return 0;

        if (item.Abilities.TryGetValue(54, out int lightBonus))
            return lightBonus;
        if (item.ItemType == 6)
            return 100;
        return 0;
    }

    private async Task LookAtCandidate((string Name, string Kind, object Obj) candidate)
    {
        switch (candidate.Kind)
        {
            case "player":
                var lookedAt = (Player)candidate.Obj;
                await DisplayPlayerDescription(lookedAt);
                // Looking at ANOTHER player tells the room "<You> looks <them> up and down." (only when
                // neither of you is hidden) and the target "<You> is looking at you." (unless you are hidden).
                if (!ReferenceEquals(lookedAt, _player) && !_player.IsSysopInvisible && !_player.IsHidden)
                {
                    if (!lookedAt.IsHidden)
                    {
                        foreach (var observer in _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player))
                        {
                            if (!ReferenceEquals(observer, lookedAt))
                                _world.SendToPlayer(observer.Name, $"{_player.Name} looks {lookedAt.Name} up and down.");
                        }
                    }
                    _world.SendToPlayer(lookedAt.Name, $"{_player.Name} is looking at you.");
                }
                break;
            case "spell":
                await ShowKnownSpellDescriptionAsync((GameSpell)candidate.Obj);
                break;
            case "monster":
                var m = (MonsterInstance)candidate.Obj;
                // Look-at-monster: colored display name, inline description (DescLine1-4), wound status
                await _client.SendLineAsync($"{MudAnsi.BrightCyan}{m.DisplayName}{MudAnsi.Reset}");

                // Show inline description from DAT DescLine1-4 fields
                if (!string.IsNullOrWhiteSpace(m.Template.Description))
                {
                    var descLines = m.Template.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in descLines)
                        await _client.SendLineAsync(line.TrimEnd());
                }

                // Wound status (stock format)
                string woundStatus = CombatEngine.GetWoundLevel(m.CurrentHP, m.MaxHP);
                await _client.SendLineAsync($"{m.Template.GetLookSubjectPronoun()} appears to be {woundStatus}.");
                break;
            case "item":
                var item = (Item)candidate.Obj;
                if (await TryHandleTownSquareSignLook(item))
                    break;
                if (await TryHandleHealerSmallSignLook(item))
                    break;
                if (await TryHandleFileDescriptionItemLook(item))
                    break;
                if (await TryHandleNewbieManualLook(item))
                    break;

                if (TryGetScrollSpell(item, out _))
                {
                    // The item description: description only, no item-name header (white text).
                    if (!string.IsNullOrWhiteSpace(item.Description))
                    {
                        var descText = item.Description.Replace("\r\n", " ").Replace("\n", " ");
                        while (descText.Contains("  ")) descText = descText.Replace("  ", " ");

                        foreach (var line in WrapText(descText.Trim(), 76))
                            await _client.SendLineAsync($"{MudAnsi.White}{line}{MudAnsi.Reset}");
                    }
                    else
                    {
                        await _client.SendLineAsync($"{MudAnsi.White}You see nothing special about {item.Name}.{MudAnsi.Reset}");
                    }

                    await ShowItemUsesRemainingAsync(item);
                    break;
                }

                // Looking at an item prints the description fragments
                // (word-wrapped, NEVER a name header — stock `l maul` shows just the prose), then, at the
                // tail, the item's ReadTextBlock when it has one.
                // So a readable (note/deed/sign/book) shows its description AND its readable body from one
                // look. That field is referenced ONLY here in stock, so LOOK is the canonical
                // path that renders it; a note's description ends in "...reading:" precisely because the
                // letter follows. (Bug #202: `look yellowed note` showed only the "...reading:" intro and
                // dropped the letter — textblock 1415 — because we never rendered ReadTextBlock.)
                string? itemDescription = GetUsableItemDescription(item);
                bool hasDescription = !string.IsNullOrWhiteSpace(itemDescription);
                if (hasDescription)
                {
                    var descText = itemDescription!.Replace("\r\n", " ").Replace("\n", " ");
                    while (descText.Contains("  ")) descText = descText.Replace("  ", " ");
                    await SendWrappedAsyncNoIndent(descText.Trim(), 76);
                }

                bool shownReadText = item.ReadTextBlock > 0
                    && await TryExecuteTextBlockAsync(item.ReadTextBlock, triggerInput: null, clueKeywords: null);

                // The item description has no "nothing special" line — that is our friendlier fallback for a
                // truly featureless item. Only reach it when there was neither a description NOR a rendered
                // ReadTextBlock (e.g. an empty-description deed still shows its house text, not this line).
                if (!hasDescription && !shownReadText)
                    await _client.SendLineAsync($"You see nothing special about {item.Name}.");

                await ShowItemUsesRemainingAsync(item);
                break;
        }
    }

    // The item description to show on `look <item>`, or null if there is none. We ship no .DSC/.TXT
    // description files, so a stock "FILE DESCRIPTION <file>" pointer has no backing text — treat it as
    // no description (the item then reads "You see nothing special about ...") rather than printing the
    // raw "FILE DESCRIPTION ..." token. Items given a real DB description render it normally.
    private static string? GetUsableItemDescription(Item item)
    {
        if (string.IsNullOrWhiteSpace(item.Description))
            return null;
        if (item.Description.TrimStart().StartsWith("FILE DESCRIPTION", StringComparison.OrdinalIgnoreCase))
            return null;
        return item.Description;
    }

    private async Task ShowItemUsesRemainingAsync(Item item)
    {
        if (item.UseCount <= 0)
            return;
        if (item.UseSpellId <= 0 && item.Abilities.GetValueOrDefault(43) <= 0)
            return;

        int remaining = item.UseCount;

        EnsureItemInstanceAlignment();
        for (int i = 0; i < _player.Inventory.Count; i++)
        {
            if (_player.Inventory[i] != item.Number)
                continue;
            if (_world.TryGetItemRuntimeState(_player.InventoryInstanceIds[i], out var state) && state.RemainingCharges.HasValue)
                remaining = state.RemainingCharges.Value;
            break;
        }

        if (remaining == item.UseCount)
        {
            foreach (var (slot, equipId) in _player.Equipment)
            {
                if (equipId != item.Number)
                    continue;
                long instanceId = GetEquipmentInstanceId(slot, equipId);
                if (_world.TryGetItemRuntimeState(instanceId, out var state) && state.RemainingCharges.HasValue)
                    remaining = state.RemainingCharges.Value;
                break;
            }
        }

        string suffix = item.RetainAfterUses ? $" {MudAnsi.Green}(Resets at Cleanup){MudAnsi.Reset}" : "";
        await _client.SendLineAsync($"{MudAnsi.Green}Uses remaining: {MudAnsi.Reset}{MudAnsi.BrightYellow}{remaining}{MudAnsi.Reset}{suffix}");
    }

    private bool TryGetScrollSpell(Item item, out GameSpell spell)
    {
        spell = new GameSpell();

        int spellId = item.Abilities.GetValueOrDefault(42);
        if (spellId <= 0 || !_world.Database.Spells.TryGetValue(spellId, out var found))
            return false;

        spell = found;
        return true;
    }

    private static int GetLearnedScrollSpellAbilityId(int spellId)
        => Player.GetLearnedSpellbookAbilityId(spellId);

    private bool PlayerKnowsSpell(int spellId)
        => _player.GetQuestAbilityValue(GetLearnedScrollSpellAbilityId(spellId)) > 0;

    private void LearnSpell(int spellId)
        => _player.SetQuestAbilityValue(GetLearnedScrollSpellAbilityId(spellId), 1);

    private List<GameSpell> GetKnownSpellbookEntries(Func<GameSpell, bool> canList)
    {
        return _player.QuestAbilities
            .Where(entry => entry.Key >= LearnedScrollSpellAbilityBase && entry.Value > 0)
            .Select(entry => entry.Key - LearnedScrollSpellAbilityBase)
            .Distinct()
            .Select(spellId => _world.Database.Spells.GetValueOrDefault(spellId))
            .Where(spell => spell != null)
            .Cast<GameSpell>()
            .Where(canList)
            .OrderBy(spell => spell.ReqLevel)
            .ThenBy(spell => spell.Number)
            .ToList();
    }

    private List<GameSpell> GetKnownSpells()
        => GetKnownSpellbookEntries(spell => CanPlayerMemorizeSpell(spell));

    // "look <spell>": resolve a KNOWN spell by its short OR full name, picking the strongest match
    // (Exact > PrefixFromStart > WordPrefix) across both fields — so `l harm` hits short "harm" exactly
    // and `l enta` hits "entangle" by prefix. Returns null when nothing in the spellbook matches.
    // The spell description: "<Name> (<Short>):" header (just "<Name>:" when the spell has no
    // short form), then the spell's description lines.
    private async Task ShowKnownSpellDescriptionAsync(GameSpell spell)
    {
        string header = string.IsNullOrWhiteSpace(spell.Short)
            ? $"{spell.Name}:"
            : $"{spell.Name} ({spell.Short}):";
        await _client.SendLineAsync($"{MudAnsi.White}{header}{MudAnsi.Reset}");

        if (!string.IsNullOrWhiteSpace(spell.Description))
        {
            foreach (var line in spell.Description.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                await _client.SendLineAsync(line.TrimEnd());
        }
    }

    // In a shop room, `look <item>` shows the description of an item
    // the shop SELLS — even one you don't own — so you can inspect wares before buying. Picks the
    // strongest name match and reuses the normal item-look rendering.
    private async Task<bool> TryLookAtShopItemAsync(string target)
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        // The shop-item display opens with the same room-class test.
        if (room == null || !room.IsShopRoom || room.Shop <= 0 || !_world.Database.Shops.TryGetValue(room.Shop, out var shop))
            return false;

        // Stock shop-item display: an exact name wins, otherwise every
        // word-prefix match counts the same — two or more print the "be more specific" list (handled).
        // A single match is shown only while it is in stock; out of stock falls through to the next check.
        var candidates = shop.Items
            .Select(shopItem => (ShopItem: shopItem, Item: _world.Database.Items.GetValueOrDefault(shopItem.ItemId)))
            .Where(candidate => candidate.Item != null)
            .Select(candidate => (candidate.ShopItem, Item: candidate.Item!));
        var matches = TargetNameMatcher.NarrowToExactOrAllMatches(candidates, m => m.Item.Name, target);
        if (matches.Count == 0)
            return false;

        var distinctNames = matches.Select(m => m.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinctNames.Count > 1)
        {
            await ShowItemDisambiguationAsync(distinctNames);
            return true;
        }

        if (!_world.IsShopItemInStock(matches[0].ShopItem))
            return false;

        await LookAtCandidate((matches[0].Item.Name, "item", matches[0].Item));
        return true;
    }

    // `look <currency>` prints a hardcoded flavor line for the
    // matched coin type (word-prefix, so `look gold` hits "gold
    // crowns"). Sits after the shop check, so a "gold ring" for sale still wins inside a shop.
    private static readonly (string Name, string Description)[] CurrencyLookDescriptions =
    {
        ("copper farthings", "The copper farthings look like they've been around forever."),
        ("silver nobles", "The silver nobles glitter with use."),
        ("gold crowns", "The gold crowns are rustic and used."),
        ("platinum pieces", "The platinum pieces glitter like nothing you have ever seen before."),
        ("runic coins", "The runic coins glitter like nothing you have ever seen before."),
    };

    private async Task<bool> TryLookAtCurrencyAsync(string target)
    {
        foreach (var (name, description) in CurrencyLookDescriptions)
        {
            if (TargetNameMatcher.MatchesWordPrefix(name, target))
            {
                await _client.SendLineAsync(description);
                return true;
            }
        }

        return false;
    }

    private List<GameSpell> GetKnownPowers()
        => GetKnownSpellbookEntries(CanPlayerListKaiPower);

    // PURGE: remove a known spell from the player's spellbook.
    private async Task HandlePurge(string args)
    {
        string name = (args ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await _client.SendLineAsync("Purge what spell?");
            return;
        }

        var spell = GetKnownSpells().FirstOrDefault(s =>
            s.Short.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (spell == null)
        {
            await _client.SendLineAsync("You don't know a spell like that.");
            return;
        }

        _player.SetQuestAbilityValue(GetLearnedScrollSpellAbilityId(spell.Number), 0);
        await _client.SendLineAsync($"{MudAnsi.BrightCyan}Purging spell {spell.Name} from your spellbook.{MudAnsi.Reset}");
    }

    private bool CanPlayerMemorizeSpell(GameSpell spell, bool ignoreRequiredLevel = false)
        => CanPlayerMemorizeSpell(_player, spell, ignoreRequiredLevel);

    private bool CanPlayerMemorizeSpell(Player player, GameSpell spell, bool ignoreRequiredLevel = false)
    {
        if (!_world.Database.Classes.TryGetValue(player.ClassId, out var cls))
            return false;

        if (Player.UsesKai(cls) || cls.MageryType == 0)
            return false;

        return cls.MageryType == spell.Magery
            && cls.MageryLvl >= spell.MageryLvl
            && (ignoreRequiredLevel || player.Level >= spell.ReqLevel);
    }

    private bool CanPlayerListKaiPower(GameSpell spell)
    {
        if (!_world.Database.Classes.TryGetValue(_player.ClassId, out var cls) || !Player.UsesKai(cls))
            return false;

        // A Kai class's "powers" are every LEARNED spell it can actually invoke — the auto-learned
        // Magery-5 "ways" AND the quest-taught Magery-0 totem forms (#838-842). Mirror the cast-time use
        // gate (PlayerCanUseLearnedSpell) so the powers list matches what INVOKE accepts; stock skips
        // the magery-type check for Magery-0 spells, so forms belong here too (bug #184).
        return PlayerCanUseLearnedSpell(spell);
    }

    private static bool IsSpellScrollItem(Item item)
    {
        return item.Abilities.GetValueOrDefault(42) > 0;
    }

    private bool TryFindReadableScroll(string target, out int inventoryIndex, out int itemId, out long instanceId, out Item item, out GameSpell spell)
    {
        // The shared stock inventory lookup (word-prefix, exact name wins, trailing-word retry); a name
        // that matches two different items is not a scroll pick — the caller reports the ambiguity.
        var matches = FindMatchingCarriedItems(target, includeEquipped: false);
        if (matches.Count > 0
            && matches.Select(m => m.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            inventoryIndex = matches[0].InventoryIndex;
            itemId = matches[0].ItemId;
            instanceId = matches[0].InstanceId;
            item = matches[0].Item;

            if (TryGetScrollSpell(item, out var foundSpell))
            {
                spell = foundSpell;
                return true;
            }
        }

        inventoryIndex = -1;
        itemId = 0;
        instanceId = 0;
        item = new Item();
        spell = new GameSpell();
        return false;
    }

    private async Task HandleRead(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync(MudAnsi.Error("Syntax: Read {Item}"));
            return;
        }

        string target = args.Trim();

        // READ is the USE handler (as stock): the carried-item lookup runs first, and two or more
        // different matches print the "be more specific" list.
        if (!TryResolveUniqueCarriedItem(target, includeEquipped: true, out _, out var ambiguousReadNames)
            && ambiguousReadNames != null)
        {
            await ShowItemDisambiguationAsync(ambiguousReadNames);
            return;
        }

        // READ routes through the no-target use handler: an inventory Link-to-Spell scroll
        // is learned here. Shared with the USE verb (see HandleUse) because one handler backs both.
        if (await TryLearnSpellScrollAsync(target))
            return;

        // Not a spell scroll: a readable item (deed, book, note, sign) displays its ReadTextBlock.
        // Reading prose is passive, so it does not break sneak/hide. Stock deeds (1008-1017) carry
        // their house descriptions here (e.g. white parchment deed → textblock 9045).
        //
        // Mirror the look renderer: show the item's own description FIRST, then the
        // ReadTextBlock body. A note's description ends in "...reading:", so this is what makes the letter
        // read as a continuation instead of appearing headerless. Deeds have an empty description, so this
        // adds nothing for them. (Bug #202: `read yellowed note` printed the letter with no "...reading:"
        // lead-in.) In stock this content is really the LOOK path — a carried non-usable
        // readable through the no-target handler, which ignores the ReadTextBlock, and an uncarried one falls back to LOOK;
        // rendering it on `read` too is the sensible union of both.
        if (TryFindInventoryItem(target, out _, out _, out _, out var readable)
            && readable.ReadTextBlock > 0)
        {
            string? readableDescription = GetUsableItemDescription(readable);
            if (!string.IsNullOrWhiteSpace(readableDescription))
            {
                var descText = readableDescription.Replace("\r\n", " ").Replace("\n", " ");
                while (descText.Contains("  ")) descText = descText.Replace("  ", " ");
                await SendWrappedAsyncNoIndent(descText.Trim(), 76);
            }

            if (await TryExecuteTextBlockAsync(readable.ReadTextBlock, triggerInput: null, clueKeywords: null))
                return;
        }

        // Not an inventory scroll/readable: try a "read X" ROOM-action verb (the room.CMD special-
        // command path). `read` is a recognized command, so it never reaches the default-case
        // room-action fallback in HandleCommand the way an unknown verb does — e.g. "read book" on
        // the open red book in Tower Bedroom (10/235, textblock 2931) shows its message and teleports
        // toward the Massive White Dragon. Mirror the bash/smash room-action fallback. (Bug #105.)
        if (await TryHandleRoomAction($"read {target}"))
            return;

        // READ with an item not in inventory falls back to LOOK —
        // so "read scroll of swarm" in a shop shows the shelf item's description (display_item_in_
        // shop) instead of an error. Bug #189: we printed "You don't know what to do with this!",
        // which read as "can't read it" when the reporter hadn't bought the scroll yet.
        await HandleLook(target);
    }

    // Ability 42 ("Link to Spell"): reading OR using a
    // Link-to-Spell scroll LEARNS the spell ("You read %s and learn the
    // spell %s"). One handler backs BOTH the READ and USE verbs,
    // so `use scroll of X` learns the spell identically to `read` — including the "You read ..." wording,
    // which it emits regardless of the invoking verb. Returns true when `target` resolved to
    // an inventory spell scroll (learned it, or emitted the stock can't-learn / already-known line);
    // false when it is not a spell scroll, so each caller runs its own fallback (read →
    // textblock/room-action/look; use → key-on-door).
    private async Task<bool> TryLearnSpellScrollAsync(string target)
    {
        if (!TryFindReadableScroll(target, out int inventoryIndex, out _, out long instanceId, out var item, out _))
            return false;

        // Every Link-to-Spell (ability 42) slot is handled on its own, in slot order, with its own
        // line: an item may teach several spells (the black tome #764 teaches chaos shield AND mana
        // flux). Iterate AbilitySlots, not the Abilities dictionary, which keeps only the LAST value of a
        // repeated ability and so taught just the final spell. A slot the player can't use or already
        // knows prints its refusal and the loop moves on; the item is consumed only if at least one
        // spell was learned.
        //
        // The per-slot gate: a spell-eligibility failure prints "You don't know what to do with this!" —
        // with the ReqLevel case FIRST queueing "This spell is too powerful for you - ", so the two
        // concatenate into one line. The magery-type/level mismatch fails silently before that, leaving
        // only the generic line.
        var lines = new List<string>();
        var toLearn = new List<GameSpell>();
        foreach (var (abilityId, spellId) in item.AbilitySlots)
        {
            if (abilityId != 42 || spellId <= 0 || !_world.Database.Spells.TryGetValue(spellId, out var spell))
                continue;

            if (!CanPlayerMemorizeSpell(spell, ignoreRequiredLevel: true))
                lines.Add("You don't know what to do with this!");
            else if (_player.Level < spell.ReqLevel)
                lines.Add("This spell is too powerful for you - You don't know what to do with this!");
            else if (PlayerKnowsSpell(spell.Number) || toLearn.Any(s => s.Number == spell.Number))
                lines.Add("You realize that you already know this scroll!");
            else
            {
                toLearn.Add(spell);
                lines.Add($"You read {item.Name} and learn the spell {spell.Name}.");
            }
        }

        if (lines.Count == 0)
        {
            await _client.SendLineAsync("You don't know what to do with this!");
            return true;
        }

        // Scroll reading does NOT break sneak/hide — verified against stock (it never touches the flags).
        if (toLearn.Count > 0 && !TryRemoveInventoryItemAt(inventoryIndex, out _, out _))
        {
            await _client.SendLineAsync("You don't know what to do with this!");
            return true;
        }

        foreach (var spell in toLearn)
            LearnSpell(spell.Number);
        // Off-gate write-behind (GameSession MarkPlayerDirty) persists the learned spells; no synchronous
        // SavePlayer on the world gate.

        foreach (string line in lines)
            await _client.SendLineAsync(line);

        if (toLearn.Count > 0)
        {
            _world.RemoveItemRuntimeState(instanceId);
            await SendItemDestructionMessagesAsync(item);
        }
        return true;
    }

    // When an item is consumed, the destruction message
    // comes from item.DestructMsg → Messages table (Line1 to self, Line2 to room when set).
    // DestructMsg == 0 falls back to the stock default "It's uses gone, %s disappears from your
    // inventory!" string. DestructMsg IS imported from the DAT (tools/dat-import) and loaded at runtime (GameDatabase.LoadItems SELECT *): 354/1950
    // stock items carry it, and the 91 whose referenced row has a non-blank Line2 broadcast to the room
    // today (e.g. msg 1357 "The scroll explodes with unfathomable force!"). Note the stock LEARN-SPELL
    // scrolls (119-136) point at the blank/absent msg 8420 → silent vanish, and msg 97 "Its magic used,
    // the scroll disintegrates." is holder-only (its Line2 is blank) — both faithful, so reading a
    // typical learn-spell scroll shows no room line by design, not by a missing export.
    private async Task SendItemDestructionMessagesAsync(Item item)
    {
        var (holderLine, roomLine) =
            ResolveItemDestructionMessages(item, _world.Database.Messages, _player.Name);

        if (holderLine != null)
            await _client.SendLineAsync(holderLine);

        if (roomLine != null)
        {
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                roomLine, _client);
        }
    }

    // The generic "It's uses gone..." line is printed ONLY when the
    // item carries NO destruction message (DestructMsg == 0). When DestructMsg is non-zero stock
    // looks it up and prints that message's Line1 (to the holder) + Line2 (to the room) — and if
    // the referenced message is missing or BLANK it prints NOTHING; it never falls back to the generic
    // line in that branch. Many stock consumables (the 120 potions/scrolls pointing at the
    // deliberately-blank message 8420, plus those on 66/121/1) rely on this: they vanish silently
    // because their own use/cast already printed the effect. Returns the holder line and the room
    // broadcast line, each null when nothing should be sent. Pure + static so it's unit-testable.
    internal static (string? HolderLine, string? RoomLine) ResolveItemDestructionMessages(
        Item item, IReadOnlyDictionary<int, RoomMessage> messages, string playerName)
    {
        if (item.DestructMsg > 0)
        {
            if (messages.TryGetValue(item.DestructMsg, out var msg))
            {
                string? holder = string.IsNullOrWhiteSpace(msg.Line1)
                    ? null : FormatItemDestructLine(msg.Line1, item.Name);
                string? room = string.IsNullOrWhiteSpace(msg.Line2)
                    ? null : FormatItemDestructLine(msg.Line2, playerName, item.Name);
                return (holder, room);
            }
            return (null, null);   // non-zero DestructMsg, row absent/blank → silent (no generic line)
        }

        return ($"It's uses gone, {item.Name} disappears from your inventory!", null);
    }

    // Message templates use %s placeholders consumed left-to-right (e.g. Line1 typically
    // takes one substitution = item name, Line2 takes two = player name then item name). Mirror
    // the helper used by CommandParser.Spells.FormatLegacyMessage to keep behaviour consistent.
    private static string FormatItemDestructLine(string template, params object[] args)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        int index = 0;
        return System.Text.RegularExpressions.Regex.Replace(template, "%[sd]", _ =>
        {
            if (index >= args.Length)
                return string.Empty;
            object value = args[index++];
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        });
    }

    private async Task HandleSpells()
    {
        if (_world.Database.Classes.TryGetValue(_player.ClassId, out var cls) && Player.UsesKai(cls))
        {
            await _client.SendLineAsync("You may not list your spells. You are KAI! You must list your powers.");
            return;
        }

        await ShowKnownSpellbookEntriesAsync("spells", "Mana", GetKnownSpells(), emptyMessage: "You have no spells.");
    }

    private async Task HandlePowers()
    {
        if (!_world.Database.Classes.TryGetValue(_player.ClassId, out var cls) || !Player.UsesKai(cls))
        {
            await _client.SendLineAsync("You may not list your powers. You are not KAI! You must list your spells.");
            return;
        }

        await ShowKnownSpellbookEntriesAsync("powers", "Kai", GetKnownPowers(), emptyMessage: "You have no powers.");
    }

    private async Task ShowKnownSpellbookEntriesAsync(string entryTypePlural, string resourceLabel, IReadOnlyList<GameSpell> knownEntries, string emptyMessage)
    {
        if (knownEntries.Count == 0)
        {
            await _client.SendLineAsync(emptyMessage);
            return;
        }

        await _client.SendLineAsync($"{MudAnsi.BrightWhite}You have the following {entryTypePlural}:{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.Magenta}{FormatSpellbookHeader(resourceLabel)}{MudAnsi.Reset}");

        foreach (var spell in knownEntries)
        {
            string row = string.Format(
                CultureInfo.InvariantCulture,
                "{0,3}   {1,-4} {2,4}  {3,-30}",
                spell.ReqLevel,
                spell.ManaCost,
                spell.Short,
                spell.Name);
            await _client.SendLineAsync($"{MudAnsi.Cyan}{row.TrimEnd()}{MudAnsi.Reset}");
        }

        await _client.SendLineAsync("");
    }

    private static string FormatSpellbookHeader(string resourceLabel)
    {
        return string.Format(CultureInfo.InvariantCulture, "Level {0,4} Short Spell Name", resourceLabel);
    }

    private bool IsInTownSquare()
    {
        return _player.CurrentMapNumber == TownSquareMap && _player.CurrentRoomNumber == TownSquareRoom;
    }

    private static bool IsTownSquareLargeSign(Item item)
    {
        return item.Number == TownSquareLargeSignItemId ||
               item.Name.Equals("large sign", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTownSquareSmallSign(Item item)
    {
        return item.Number == TownSquareSmallSignItemId ||
               item.Name.Equals("small sign", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveCustomSignTextPath(string fileName)
    {
        static string? TryResolveFromRoot(string? root, string fileName)
        {
            while (!string.IsNullOrEmpty(root))
            {
                var direct = Path.Combine(root, fileName);
                if (File.Exists(direct))
                    return direct;

                var dataAssets = Path.Combine(root, "Data", "assets", fileName);
                if (File.Exists(dataAssets))
                    return dataAssets;

                var data = Path.Combine(root, "Data", fileName);
                if (File.Exists(data))
                    return data;

                var projectDataAssets = Path.Combine(root, "src", "mmudreborn.Server", "Data", "assets", fileName);
                if (File.Exists(projectDataAssets))
                    return projectDataAssets;

                var projectData = Path.Combine(root, "src", "mmudreborn.Server", "Data", fileName);
                if (File.Exists(projectData))
                    return projectData;

                var projectRoot = Path.Combine(root, "src", "mmudreborn.Server", fileName);
                if (File.Exists(projectRoot))
                    return projectRoot;

                root = Directory.GetParent(root)?.FullName;
            }

            return null;
        }

        var path = TryResolveFromRoot(AppContext.BaseDirectory, fileName);
        if (!string.IsNullOrEmpty(path))
            return path;

        path = TryResolveFromRoot(Directory.GetCurrentDirectory(), fileName);
        if (!string.IsNullOrEmpty(path))
            return path;

        return null;
    }

    private static string? LoadCustomSignText(string fileName)
    {
        var path = ResolveCustomSignTextPath(fileName);
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
                return null;

            var text = Encoding.GetEncoding(437).GetString(bytes)
                .Replace("\r\n", "\n")
                .TrimEnd();

            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> TryHandleFileDescriptionItemLook(Item item)
    {
        if (string.IsNullOrWhiteSpace(item.Description))
            return false;

        var lines = item.Description
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (lines.Length < 2 || !lines[0].Equals("FILE DESCRIPTION", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = lines[1];
        var customText = LoadCustomSignText(fileName);
        if (string.IsNullOrWhiteSpace(customText))
            return false;

        await _client.SendLineAsync($"{MudAnsi.BrightCyan}{item.Name}{MudAnsi.Reset}");
        foreach (var line in customText.Split('\n'))
            await _client.SendLineAsync(line);

        return true;
    }

    private async Task<bool> TryHandleNewbieManualLook(Item item)
    {
        if (item.Number != NewbieManualItemId)
            return false;

        var customText = LoadCustomSignText("NEWBIE.TXT");
        await _client.SendLineAsync($"{MudAnsi.BrightCyan}{item.Name}{MudAnsi.Reset}");

        if (!string.IsNullOrWhiteSpace(customText))
        {
            foreach (var line in customText.Split('\n'))
                await _client.SendLineAsync(line);
            return true;
        }

        await _client.SendLineAsync("Welcome to mmudreborn.");
        await _client.SendLineAsync("Type HELP COMMANDS to view your basic actions.");
        await _client.SendLineAsync("Type LOOK to study each room, and SEARCH to discover hidden things.");
        await _client.SendLineAsync("When you have enough experience, visit your guild and TRAIN.");
        return true;
    }

    private async Task<bool> TryHandleHealerSmallSignLook(Item item)
    {
        if (_player.CurrentMapNumber != TownSquareMap || _player.CurrentRoomNumber != NewhavenHealerRoom)
            return false;

        if (item.Number != HealerSmallSignItemId &&
            !item.Name.Equals("small sign", StringComparison.OrdinalIgnoreCase))
            return false;

        await ShowHealerSmallSignAsciiAsync();
        return true;
    }

    private async Task ShowHealerSmallSignAsciiAsync()
    {
        await _client.SendAsync(MudAnsi.ClearScreen);

        const string bgOlive = "\x1b[43m";
        const string fgBlack = "\x1b[30m";
        const string fgRed = "\x1b[31m";
        const string fgSilver = "\x1b[38;5;250m";
        const string fgGold = "\x1b[38;5;220m";
        const int panelWidth = 42;
        const int leftWidth = 19;
        const int rightStart = 24;

        string PanelBlank() => $"{bgOlive}{fgBlack}{new string(' ', panelWidth)}{MudAnsi.Reset}";

        string PanelEdge()
        {
            return $"{bgOlive}{MudAnsi.DarkGray}.{fgBlack}{new string(' ', panelWidth - 2)}{MudAnsi.DarkGray}.{MudAnsi.Reset}";
        }

        string PanelRow(string left, string right, bool isGold)
        {
            var chars = Enumerable.Repeat(' ', panelWidth).ToArray();
            var leftText = left.Length > leftWidth ? left[..leftWidth] : left;
            for (int i = 0; i < leftText.Length; i++)
                chars[2 + i] = leftText[i];

            var rightText = right;
            for (int i = 0; i < rightText.Length && (rightStart + i) < panelWidth; i++)
                chars[rightStart + i] = rightText[i];

            string baseRow = new string(chars);
            string leftSegment = leftText.PadRight(leftWidth);
            string rightColor = isGold ? fgGold : fgSilver;

            string amount = rightText;
            string currency = "";
            int split = rightText.IndexOf(' ');
            if (split > 0)
            {
                amount = rightText[..split];
                currency = rightText[(split + 1)..];
            }

            string rightSegment = split > 0
                ? $"{fgBlack}{amount} {rightColor}{currency}{fgBlack}"
                : $"{fgBlack}{rightText}";

            return $"{bgOlive}{fgBlack}{baseRow[..2]}{fgRed}{leftSegment}{fgBlack}{baseRow[(2 + leftWidth)..rightStart]}{rightSegment}{fgBlack}{baseRow[(rightStart + rightText.Length)..]}{MudAnsi.Reset}";
        }

        await _client.SendLineAsync($" {PanelEdge()}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelRow("Minor Healing", "5 silver", false)}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelRow("Major Healing", "12 silver", false)}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelRow("Greater Healing", "20 silver", false)}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelRow("Cure Poison", "10 gold", true)}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelRow("Cure Disease", "25 gold", true)}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelRow("Remove Curse", "50 gold", true)}");
        await _client.SendLineAsync($" {PanelBlank()}");
        await _client.SendLineAsync($" {PanelEdge()}");
        await _client.SendLineAsync();
    }

    private async Task<bool> TryHandleTownSquareSignLook(Item item)
    {
        if (!IsInTownSquare())
            return false;

        if (IsTownSquareLargeSign(item))
        {
            await RunBufferedInteractiveCommandAsync(ShowTownSquareLargeSignMapAsync);
            return true;
        }

        if (IsTownSquareSmallSign(item))
        {
            var customText = LoadCustomSignText("SILVRMRE.TXT");
            if (!string.IsNullOrWhiteSpace(customText))
            {
                await _client.SendLineAsync($"{MudAnsi.BrightCyan}small sign{MudAnsi.Reset}");
                foreach (var line in customText.Split('\n'))
                    await _client.SendLineAsync(line);
            }
            else
            {
                await _client.SendLineAsync($"{MudAnsi.White}Welcome to JMud, To be updated...{MudAnsi.Reset}");
            }
            return true;
        }

        return false;
    }

    // The `map` command: print the area ANSI map for the room the player is standing in. Each room carries
    // an "Ansi Map" field (the room editor's WCCMAPxx.ANS, e.g. Map 6 -> WCCMAP06.ANS); our import keeps the
    // area number that field tracks, so derive the filename from the current map and fall back to WCCMAP01.ANS
    // when the specific area map asset isn't bundled.
    private async Task HandleMapAsync()
    {
        string areaMapFile = $"WCCMAP{_player.CurrentMapNumber:00}.ANS";
        string? mapAnsi = LoadMapAnsi(areaMapFile) ?? LoadMapAnsi("WCCMAP01.ANS");

        await RunBufferedInteractiveCommandAsync(async () =>
        {
            await _client.SendAsync(MudAnsi.ClearScreen);
            if (!string.IsNullOrEmpty(mapAnsi))
                await _client.SendAsync(mapAnsi);
            else
                await _client.SendLineAsync($"{MudAnsi.BrightRed}No map is available for this area.{MudAnsi.Reset}");

            await _client.SendAsync($"\r\n{MudAnsi.DarkGray}Hit any key to continue...{MudAnsi.Reset}");
            _client.SetEcho(false);
            _ = await _client.ReadKeyAsync();
            _client.SetEcho(true);
            await _client.SendLineAsync();
        });

        // Return the player to their room view after the full-screen map.
        await ShowRoom(brief: _player.BriefMode);
    }

    // Load a WCCMAPxx.ANS asset as CP437 ANSI text (the original map files are codepage-437 art), or null if
    // the file isn't bundled so the caller can fall back to the default area map.
    private static string? LoadMapAnsi(string fileName)
    {
        // Primary: the WCCMAPxx.ANS copied next to the binary (Data/assets — see the csproj None copy).
        var path = ResolveCustomSignTextPath(fileName);
        if (!string.IsNullOrEmpty(path))
        {
            try
            {
                return Encoding.GetEncoding(437).GetString(File.ReadAllBytes(path));
            }
            catch
            {
                // fall through to the embedded copy
            }
        }

        // Fallback: the SAME asset embedded in the assembly (csproj EmbeddedResource), so a deploy that
        // fails to copy Data/assets next to the binary can never make the map "unavailable".
        return LoadEmbeddedMapAnsi(fileName);
    }

    // Reads an embedded WCCMAPxx.ANS resource by filename — matched on the resource-name suffix so the
    // assembly's root namespace / folder layout doesn't have to be hard-coded — decoded as CP437.
    private static string? LoadEmbeddedMapAnsi(string fileName)
    {
        try
        {
            var assembly = typeof(CommandParser).Assembly;
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
                return null;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
                return null;

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return Encoding.GetEncoding(437).GetString(buffer.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static string? LoadTownSquareMapAnsi()
    {
        if (_cachedTownSquareMapAnsi != null)
            return _cachedTownSquareMapAnsi;

        var path = ResolveCustomSignTextPath("WCCMAP01.ANS");
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var bytes = File.ReadAllBytes(path);
            // The original map asset is ANSI + CP437 text.
            _cachedTownSquareMapAnsi = Encoding.GetEncoding(437).GetString(bytes);
            return _cachedTownSquareMapAnsi;
        }
        catch
        {
            return null;
        }
    }

    private async Task RunBufferedInteractiveCommandAsync(Func<Task> action)
    {
        _client.FlushOutput();

        try
        {
            await action();
        }
        finally
        {
            _client.BeginBuffering();
        }
    }

    private async Task<T> RunBufferedInteractiveCommandAsync<T>(Func<Task<T>> action)
    {
        _client.FlushOutput();

        try
        {
            return await action();
        }
        finally
        {
            _client.BeginBuffering();
        }
    }

    private async Task ShowTownSquareLargeSignMapAsync()
    {
        await _client.SendAsync(MudAnsi.ClearScreen);

        var mapAnsi = LoadTownSquareMapAnsi();
        if (!string.IsNullOrEmpty(mapAnsi))
        {
            await _client.SendAsync(mapAnsi);
        }
        else
        {
            // Fallback only if the original ANSI map file is unavailable.
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Weapon Shoppes{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Adventurer's Guild{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.DarkGray}Trader Tull's{MudAnsi.Reset}");
            await _client.SendLineAsync();
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Holy Temple           Arena{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}  Junkyard           Curio Shoppe{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}   Casino            General Store{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}                     Town Gates{MudAnsi.Reset}");
            await _client.SendLineAsync();
            await _client.SendLineAsync($"{MudAnsi.BrightRed}Bank of Silvermere{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}  Jewelry Shoppe{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightRed}     Bow Shoppe{MudAnsi.Reset}");
            await _client.SendLineAsync();
        }

        await _client.SendAsync($"\r\n{MudAnsi.DarkGray}Hit any key to continue...{MudAnsi.Reset}");
        _client.SetEcho(false);
        _ = await _client.ReadKeyAsync();
        _client.SetEcho(true);
        await _client.SendLineAsync();
    }

    /// <summary>The user-description format: stat-based character description.</summary>
    private async Task DisplayPlayerDescription(Player target)
    {
        // Shadowform (ability 178): when the target carries a nonzero
        // shadowform value, the ENTIRE normal look (name header + appearance paragraph + equipment list)
        // is replaced by textblock #value. The name header is built only in the value==0 branch, so a
        // shadowformed player's identity AND gear are hidden when examined (PVP). Emit just the textblock.
        if (target.ShadowformTextblock != 0)
        {
            if (_world.Database.TextBlocks.TryGetValue(target.ShadowformTextblock, out var shadowText))
                await SendDialogueLinesAsync(shadowText.Replace("\r", "").Split('\n'), null);
            return;
        }

        var race = _world.Database.Races.GetValueOrDefault(target.RaceId);
        var cls = _world.Database.Classes.GetValueOrDefault(target.ClassId);
        string raceName = race?.Name ?? "Unknown";
        string className = cls?.Name ?? "Unknown";

        string hairLen = target.HairLength >= 0 && target.HairLength < HairLengths.Length
            ? HairLengths[target.HairLength] : "";
        string hairCol = target.HairColour >= 0 && target.HairColour < HairColours.Length
            ? HairColours[target.HairColour] : "unknown";
        string eyeCol = target.EyeColour >= 0 && target.EyeColour < EyeColours.Length
            ? EyeColours[target.EyeColour] : "unknown";

        // Header: [ FirstName LASTNAME ] (Gang)
        string lastName = string.IsNullOrEmpty(target.LastName) ? "" : target.LastName;
        string nameHeader = string.IsNullOrEmpty(lastName)
            ? $"[ {target.Name} ]"
            : $"[ {target.Name} {lastName} ]";
        string gang = target.Gang;
        if (!string.IsNullOrEmpty(gang))
            nameHeader += $" ({gang})";
        await _client.SendLineAsync($"{MudAnsi.BrightCyan}{nameHeader}{MudAnsi.Reset}");

        // Stock format strings (description paragraph, wound separate):
        string desc =
            $"{target.Name} is a {target.GetHealthDesc()}, {target.GetStrengthDesc()} " +
            $"{raceName} {className} with " +
            $"{hairLen}{hairCol} hair and {eyeCol} eyes.  " +
            $"{target.Name} moves {target.GetAgilityDesc()}, and is {target.GetCharmDesc()}.  " +
            $"{target.Name} appears to be {target.GetIntellectDesc()} and {target.GetWillpowerDesc()}.  " +
            $"{target.Name} is {target.GetWoundDesc()}.";

        // Remove indent for the first line of description
        await SendWrappedAsyncNoIndent(desc, 76);

        // Blank line after description+wound
        await _client.SendLineAsync("");

        await _client.SendLineAsync($"{MudAnsi.Yellow}{target.HeShe} is equipped with:{MudAnsi.Reset}");
        await _client.SendLineAsync("");

        // Equipment list (stock format: "%s is equipped with:")
        if (_player.UseModernLookStyle)
        {
            foreach (var (itemName, slotLabel) in GetModernLookEquipmentLines(target))
            {
                await _client.SendLineAsync($"{MudAnsi.Green}{itemName,-30}{MudAnsi.Reset} {MudAnsi.Cyan}({slotLabel}){MudAnsi.Reset}");
            }
        }
        else
        {
            var equippedItems = GetOrderedEquippedItems(target);
            var displayedInstanceIds = equippedItems
                .Select(entry => entry.InstanceId)
                .ToList();

            if (TryGetDisplayedReadiedLightSource(target, displayedInstanceIds, out int activeLightItemId, out long activeLightInstanceId, out var activeLightItem))
            {
                // The wielded weapon is printed LAST,
                // after the readied light source and all worn items. So the readied light must come
                // before the weapon, not after it. Insert ahead of the weapon-hand entry (else append).
                var readiedEntry = (activeLightItemId, activeLightInstanceId, activeLightItem, "Readied");
                int weaponIndex = equippedItems.FindIndex(e => e.SlotLabel is "Weapon Hand" or "Two handed");
                if (weaponIndex >= 0)
                    equippedItems.Insert(weaponIndex, readiedEntry);
                else
                    equippedItems.Add(readiedEntry);
            }

            if (equippedItems.Count > 0)
            {
                foreach (var (_, _, item, slotLabel) in equippedItems)
                {
                    await _client.SendLineAsync($"{MudAnsi.Green}{item.Name,-30}{MudAnsi.Reset} {MudAnsi.Cyan}({slotLabel}){MudAnsi.Reset}");
                }
            }
            else
            {
                await _client.SendLineAsync($"{MudAnsi.Green}Nothing{MudAnsi.Reset}");
            }
        }
        await _client.SendLineAsync("");
    }

    /// <summary>Word-wrap text to a given width, no indent. Used for player/item descriptions.</summary>
    private async Task SendWrappedAsyncNoIndent(string text, int width)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        foreach (var word in words)
        {
            if (line.Length == 0)
            {
                line = word;
            }
            else if (line.Length + 1 + word.Length > width)
            {
                await _client.SendLineAsync(line);
                line = word;
            }
            else
            {
                line += " " + word;
            }
        }
        if (line.Length > 0)
            await _client.SendLineAsync(line);
    }

    /// <summary>Word-wrap a room description with stock-style white text and first-line indent.</summary>
    private async Task SendWrappedRoomDescriptionAsync(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = "";
        bool firstLine = true;
        const int width = 79;
        const int firstIndent = 4;

        foreach (var word in words)
        {
            int curWidth = firstLine ? width - firstIndent : width;
            int lineVisible = RoomOutputFormatter.GetVisibleLength(line);
            int wordVisible = RoomOutputFormatter.GetVisibleLength(word);
            if (lineVisible == 0)
            {
                line = word;
            }
            else if (lineVisible + 1 + wordVisible > curWidth)
            {
                await SendRoomDescriptionLineAsync(line, firstLine ? firstIndent : 0);
                firstLine = false;
                line = word;
            }
            else
            {
                line += " " + word;
            }
        }

        if (line.Length > 0)
        {
            await SendRoomDescriptionLineAsync(line, firstLine ? firstIndent : 0);
        }
    }

    private Task SendRoomDescriptionLineAsync(string line, int indent)
    {
        string prefix = indent > 0 ? MudAnsi.LinePreamble : string.Empty;
        string indentText = indent > 0 ? new string(' ', indent) : string.Empty;
        return _client.SendLineAsync($"{prefix}{MudAnsi.White}{indentText}{line}{MudAnsi.Reset}");
    }

    // Render a description verbatim — one output line per source line, no word-wrap or reflow.
    // Used for gang-house (.HSE) rooms, which stock streams line-by-line so owner layouts survive.
    private async Task SendVerbatimRoomDescriptionAsync(string text)
    {
        foreach (var rawLine in text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            await SendRoomDescriptionLineAsync(rawLine.TrimEnd(), 0);
        }
    }

}
