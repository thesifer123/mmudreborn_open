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
    private const int GenericStockLightBurnoutMessageId = 0;
    private const int SilentStockLightBurnoutMessageId = -1;

    // Burnout behavior traced from stock item destruction-message ids in wccitem2.dat.
    private static readonly IReadOnlyDictionary<int, int> StockLightBurnoutMessageIds = new Dictionary<int, int>
    {
        [175] = 8603,
        [176] = 8604,
        [284] = 8608,
        [286] = 8604,
        [692] = 8604,
        [935] = SilentStockLightBurnoutMessageId,
        [1085] = 3379,
        [1153] = 1995,
        [1233] = GenericStockLightBurnoutMessageId,
        [1234] = GenericStockLightBurnoutMessageId,
    };

    private async Task HandleInventory()
    {
        SynchronizeLightState();
        RecalcEquipment();

        GameColorPalette palette = GameColorPalettes.Resolve(_player.PaletteId);

        // Inventory output starts with "You are carrying " and includes equipped/owned items.
        var parts = new List<string>();
        int encumbrance = _player.Encumbrance;

        long litInstanceId = 0;
        int litReadiedTicks = 0;
        if (TryGetSingleActiveLightSource(out _, out var litUntilUtc, out long activeLightInstanceId))
        {
            litInstanceId = activeLightInstanceId;
            litReadiedTicks = GetInventoryLightReadiedValue(litUntilUtc);
        }

        // Currency (descending value order)
        if (_player.Runic > 0) parts.Add($"{_player.Runic} runic {(_player.Runic == 1 ? "coin" : "coins")}");
        if (_player.Platinum > 0) parts.Add($"{_player.Platinum} platinum {(_player.Platinum == 1 ? "piece" : "pieces")}");
        if (_player.Gold > 0) parts.Add($"{_player.Gold} gold {(_player.Gold == 1 ? "crown" : "crowns")}");
        if (_player.Silver > 0) parts.Add($"{_player.Silver} silver {(_player.Silver == 1 ? "noble" : "nobles")}");
        if (_player.Copper > 0) parts.Add($"{_player.Copper} copper {(_player.Copper == 1 ? "farthing" : "farthings")}");

        // Equipped items with slot labels (non-weapon first, weapon last)
        foreach (var (itemId, instanceId, item, baseSlotLabel) in GetOrderedEquippedItems(_player))
        {
            string slotLabel = baseSlotLabel;
            if (instanceId == litInstanceId && litReadiedTicks > 0)
                slotLabel += $"/{litReadiedTicks}";

            parts.Add($"{item.Name} ({slotLabel})");
        }

        // Non-key inventory items; separate out keys (ItemType 7)
        var groupedInventoryItems = new List<string>();
        bool annotatedLitInventoryItem = false;
        var keys = new List<string>();
        EnsureItemInstanceAlignment();
        for (int index = 0; index < _player.Inventory.Count; index++)
        {
            int itemId = _player.Inventory[index];
            long instanceId = _player.InventoryInstanceIds[index];
            var item = _world.Database.Items.GetValueOrDefault(itemId);
            if (item == null) continue;

            if (item.ItemType == 7)
                keys.Add(item.Name);
            else
            {
                if (!annotatedLitInventoryItem && instanceId == litInstanceId && litReadiedTicks > 0)
                {
                    groupedInventoryItems.Add($"{item.Name} (Readied/{litReadiedTicks})");
                    annotatedLitInventoryItem = true;
                }
                else
                {
                    groupedInventoryItems.Add(item.Name);
                }
            }
        }

        parts.AddRange(GroupInventoryEntries(groupedInventoryItems));

        string carryText = parts.Count == 0 ? "Nothing!" : string.Join(", ", parts);
        foreach (var line in WrapText($"You are carrying {carryText}", 76))
            await _client.SendLineAsync(line);

        if (keys.Count > 0)
            await _client.SendLineAsync($"You have the following keys: {FormatGroupedInventoryNames(keys)}.");
        else
            await _client.SendLineAsync("You have no keys.");

        long totalCopper = CurrencyHelper.ToCopper(_player);
        await _client.SendLineAsync($"{palette.Get(GameColorRole.InventoryLabel)}Wealth:{MudAnsi.Reset} {palette.Get(GameColorRole.InventoryValue)}{totalCopper} copper farthings{MudAnsi.Reset}");

        int maxEnc = GetMaxCarryCapacity();
        int encPct = Math.Clamp((int)((long)encumbrance * 100 / maxEnc), 0, 999);
        string encState = encPct >= 67 ? "Heavy"
            : encPct >= 33 ? "Medium"
            : encPct >= 15 ? "Light"
            : "None";
        string encColor = encPct >= 67 ? palette.Get(GameColorRole.InventoryDanger)
            : encPct >= 33 ? palette.Get(GameColorRole.InventoryWarning)
            : encPct >= 15 ? palette.Get(GameColorRole.InventoryLabel)
            : palette.Get(GameColorRole.InventoryNeutral);

        await _client.SendLineAsync($"{palette.Get(GameColorRole.InventoryLabel)}Encumbrance:{MudAnsi.Reset} {palette.Get(GameColorRole.InventoryValue)}{encumbrance}/{maxEnc}{MudAnsi.Reset} - {encColor}{encState} [{encPct}%]{MudAnsi.Reset}");
    }

    private async Task HandleKeys()
    {
        var keys = new List<string>();
        foreach (var itemId in _player.Inventory)
        {
            var item = _world.Database.Items.GetValueOrDefault(itemId);
            if (item != null && item.ItemType == 7)
                keys.Add(item.Name);
        }

        if (keys.Count == 0)
        {
            await _client.SendLineAsync("You have no keys on your keyring.");
            return;
        }

        await _client.SendLineAsync($"You have the following keys: {FormatGroupedInventoryNames(keys)}.");
    }

    private static string FormatGroupedInventoryNames(IEnumerable<string> names)
    {
        return string.Join(", ", GroupInventoryEntries(names));
    }

    private static List<string> BuildGroupedNoticeEntries(IEnumerable<string> names, int paletteId = 0)
    {
        string color = GameColorPalettes.Resolve(paletteId).Get(GameColorRole.RoomNotice);

        return GroupInventoryEntries(names)
            .Select(name => $"{color}{name}{MudAnsi.Reset}")
            .ToList();
    }

    private static List<string> GroupInventoryEntries(IEnumerable<string> names)
    {
        return names
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Count() > 1 ? $"{group.Count()} {group.First()}" : group.First())
            .ToList();
    }

    private static IEnumerable<string> WrapText(string text, int maxWidth)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new[] { string.Empty };

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        string current = string.Empty;

        foreach (var word in words)
        {
            string candidate = current.Length == 0 ? word : current + " " + word;
            if (candidate.Length <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
                lines.Add(current);
            current = word;
        }

        if (current.Length > 0)
            lines.Add(current);

        return lines;
    }

    private void EnsureItemInstanceAlignment()
    {
        _world.EnsurePlayerItemInstanceAlignment(_player);
    }

    private void AddItemToInventory(int itemId, long? instanceId = null)
    {
        AddItemToInventory(_player, itemId, instanceId);
    }

    private void AddItemToInventory(Player player, int itemId, long? instanceId = null)
    {
        _world.EnsurePlayerItemInstanceAlignment(player);
        player.Inventory.Add(itemId);
        player.InventoryInstanceIds.Add(instanceId ?? _world.CreateItemInstance(itemId));
    }

    private bool TryAcquireInventoryItem(int itemId, out long instanceId, long? preferredInstanceId = null)
    {
        instanceId = 0;

        if (!_world.Database.Items.TryGetValue(itemId, out var item))
            return false;

        if (WouldExceedEncumbranceAfterItemGain(item))
            return false;

        AddItemToInventory(itemId, preferredInstanceId);
        instanceId = _player.InventoryInstanceIds[^1];
        return true;
    }

    private int GetMaxCarryCapacity()
    {
        return GetMaxCarryCapacity(_player);
    }

    private static int GetMaxCarryCapacity(Player player)
    {
        return player.MaxEncumbrance > 0 ? player.MaxEncumbrance : Player.CalculateBaseMaxEncumbrance(player.Strength);
    }

    private bool WouldExceedEncumbrance(long additionalEncumbrance)
    {
        return WouldExceedEncumbrance(_player, additionalEncumbrance);
    }

    private bool WouldExceedEncumbrance(Player player, long additionalEncumbrance)
    {
        if (additionalEncumbrance <= 0)
            return false;

        player.RecalculateEquipment(_world.Database);
        return (long)player.Encumbrance + additionalEncumbrance > GetMaxCarryCapacity(player);
    }

    private bool WouldExceedEncumbranceAfterItemGain(Item item)
    {
        return WouldExceedEncumbrance(Math.Max(0, item.Encum));
    }

    private bool WouldExceedEncumbranceAfterPurchase(Item item, int spentAmount, int currencyIndex)
        => WouldExceedEncumbranceAfterPurchaseCopper(item, GetShopCurrencyCopperValue(spentAmount, currencyIndex));

    private bool WouldExceedEncumbranceAfterPurchaseCopper(Item item, long spentCopper)
    {
        RecalcEquipment();

        long[] projectedCurrencyCounts = GetPlayerCurrencyCountsAscending(_player);
        if (!TrySpendCurrencyPreservingDenominations(projectedCurrencyCounts, spentCopper))
            return true;

        long nonCurrencyEncumbrance = Math.Max(0, _player.Encumbrance - _player.GetCarriedCurrencyWeight());
        long projectedCurrencyWeight = Player.CalculateCurrencyWeight(
            projectedCurrencyCounts[4],
            projectedCurrencyCounts[3],
            projectedCurrencyCounts[2],
            projectedCurrencyCounts[1],
            projectedCurrencyCounts[0]);
        long projectedEncumbrance = nonCurrencyEncumbrance + projectedCurrencyWeight + Math.Max(0, item.Encum);

        return projectedEncumbrance > GetMaxCarryCapacity();
    }

    private bool WouldExceedEncumbranceAfterCurrencyGain(long runic, long platinum, long gold, long silver, long copper)
    {
        return WouldExceedEncumbranceAfterCurrencyGain(_player, runic, platinum, gold, silver, copper);
    }

    private bool WouldExceedEncumbranceAfterCurrencyGain(Player player, long runic, long platinum, long gold, long silver, long copper)
    {
        player.RecalculateEquipment(_world.Database);

        long nonCurrencyEncumbrance = Math.Max(0, player.Encumbrance - player.GetCarriedCurrencyWeight());
        long projectedCurrencyWeight = Player.CalculateCurrencyWeight(
            (long)player.Runic + runic,
            (long)player.Platinum + platinum,
            (long)player.Gold + gold,
            (long)player.Silver + silver,
            (long)player.Copper + copper);
        return nonCurrencyEncumbrance + projectedCurrencyWeight > GetMaxCarryCapacity(player);
    }

    private bool WouldExceedEncumbranceAfterCurrencyDenominationGain(long denominationMultiplier, long count)
    {
        return WouldExceedEncumbranceAfterCurrencyDenominationGain(_player, denominationMultiplier, count);
    }

    private bool WouldExceedEncumbranceAfterCurrencyDenominationGain(Player player, long denominationMultiplier, long count)
    {
        return denominationMultiplier switch
        {
            CurrencyHelper.CopperPerRunic => WouldExceedEncumbranceAfterCurrencyGain(player, count, 0, 0, 0, 0),
            CurrencyHelper.CopperPerPlatinum => WouldExceedEncumbranceAfterCurrencyGain(player, 0, count, 0, 0, 0),
            CurrencyHelper.CopperPerGold => WouldExceedEncumbranceAfterCurrencyGain(player, 0, 0, count, 0, 0),
            CurrencyHelper.CopperPerSilver => WouldExceedEncumbranceAfterCurrencyGain(player, 0, 0, 0, count, 0),
            1 => WouldExceedEncumbranceAfterCurrencyGain(player, 0, 0, 0, 0, count),
            _ => false,
        };
    }

    // Move delay (MovementDelayCalculator, unit-tested). The rapid-move counter is
    // incremented per move and reset (no fatigue) for heavy players — who stock also resets every
    // fast tick; the light counter is reset each slow tick (WorldTick).
    private int ComputeMoveDelayTicks(bool isDragging)
    {
        int encPct = GetMovementEncumbrancePercent();
        if (encPct > MovementDelayCalculator.HeavyEncumbrancePercent)
            _player.RecentMoveCount = 0;
        else
            _player.RecentMoveCount++;

        var db = _world.Database;
        return MovementDelayCalculator.ComputeDelayTicks(
            encPct,
            isDragging,
            isHasted: _player.HasActiveAbility(db, MovementHasteAbilityId),   // 67
            isSlowed: _player.HasActiveAbility(db, MovementSlowAbilityId),    // 68
            _player.RecentMoveCount);
    }

    // A move applies an action delay and stores the direction; the relocation (and
    // the departing free attack) only runs AFTER the delay counter drains. So the delay is paid
    // BEFORE you leave — you stay in the room taking combat-round hits and the departing free-attack
    // while "leaving", and you can't instant-bounce out of a room before its monster engages.
    //
    // GameSession waits this delay OFF the world gate (combat pulses keep striking) and then runs the
    // move. Computed here UNDER the gate (it reads shared room/occupant state — dragging, exits). Returns
    // Zero for a wall-bump (no exit) so a failed move stays instant, matching stock, which checks
    // the exit BEFORE the delay. Increments the rapid-move fatigue counter exactly once per real move.
    public TimeSpan ComputeMoveExposureDelay(string trimmedInput)
    {
        var cmd = trimmedInput.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts
            ? parts[0].ToLowerInvariant()
            : string.Empty;
        if (!DirectionAliases.TryGetValue(cmd, out var direction))
            return TimeSpan.Zero;

        var currentRoom = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (currentRoom == null)
            return TimeSpan.Zero;

        // No exit in that direction → instant (HandleMovement prints the rejection); don't burn a delay.
        if (_world.FindMovementExit(_player, currentRoom, direction) == null)
            return TimeSpan.Zero;

        var draggedPlayer = GetDraggedPlayerForMovement(currentRoom);
        return ComputeMoveDelayTicks(isDragging: draggedPlayer != null) * FastTick;
    }

    // Door/skill actions (open/close = 1 tick, bashdoor/picklock = 2) apply a delay
    // directly — a plain fixed delay in fast-ticks, with no encumbrance/haste/fatigue modifiers.
    // The delay gates the player's NEXT command of ANY kind, and it must NOT sleep
    // under the global gate. Stamp NextActionAllowedAtUtc — the wait happens off-gate at the top of the
    // session loop and applies to every following command. (Async signature kept so callers' `await`
    // sites are unchanged.)
    private Task ApplyActionDelayAsync(int fastTicks)
    {
        if (fastTicks > 0)
        {
            DateTime stamp = DateTime.UtcNow + fastTicks * FastTick;
            if (stamp > _player.NextActionAllowedAtUtc)
                _player.NextActionAllowedAtUtc = stamp;
        }
        return Task.CompletedTask;
    }

    // Encumbrance percent: (coinWeight + carriedWeight) * 100 / maxWeight.
    private int GetMovementEncumbrancePercent()
    {
        RecalcEquipment();
        int maxEnc = GetMaxCarryCapacity();
        return (int)((long)_player.Encumbrance * 100 / Math.Max(1, maxEnc));
    }

    // An encumbrance percent > 100 hard-blocks movement.
    private bool IsOverEncumberedForMovement() =>
        MovementDelayCalculator.IsOverEncumbered(GetMovementEncumbrancePercent());

    private bool TryRemoveInventoryItemAt(int inventoryIndex, out int itemId, out long instanceId)
    {
        EnsureItemInstanceAlignment();

        if (inventoryIndex < 0 || inventoryIndex >= _player.Inventory.Count)
        {
            itemId = 0;
            instanceId = 0;
            return false;
        }

        itemId = _player.Inventory[inventoryIndex];
        instanceId = _player.InventoryInstanceIds[inventoryIndex];
        _player.Inventory.RemoveAt(inventoryIndex);
        _player.InventoryInstanceIds.RemoveAt(inventoryIndex);
        return true;
    }

    // Inventory-only resolution via the shared whole-word-prefix matcher (not substring), so a short
    // prefix like "tor" resolves to the intended "torch" instead of the first item whose name happens
    // to contain those letters.
    private bool TryFindInventoryItem(string target, out int inventoryIndex, out int itemId, out long instanceId, out Item item)
    {
        foreach (var match in FindMatchingCarriedItems(target, includeEquipped: false))
        {
            if (match.InventoryIndex < 0)
                continue;

            inventoryIndex = match.InventoryIndex;
            itemId = match.ItemId;
            instanceId = match.InstanceId;
            item = match.Item;
            return true;
        }

        inventoryIndex = -1;
        itemId = 0;
        instanceId = 0;
        item = new Item();
        return false;
    }

    private readonly record struct CarriedItemMatch(int ItemId, Item Item, long InstanceId, int InventoryIndex, string? EquipmentSlot)
    {
        public bool IsEquipped => EquipmentSlot != null;
    }

    private bool TryResolveUniqueCarriedItem(string target, bool includeEquipped, out CarriedItemMatch match, out IReadOnlyList<string>? ambiguousNames)
    {
        match = default;
        ambiguousNames = null;

        var matches = FindMatchingCarriedItems(target, includeEquipped);
        if (matches.Count == 0)
            return false;

        var distinctNames = matches
            .Select(candidate => candidate.Item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctNames.Count > 1)
        {
            ambiguousNames = distinctNames;
            return false;
        }

        match = matches[0];
        return true;
    }

    private List<CarriedItemMatch> FindMatchingCarriedItems(string target, bool includeEquipped)
    {
        EnsureItemInstanceAlignment();

        var matches = new List<CarriedItemMatch>();
        string trimmedTarget = target.Trim();
        if (trimmedTarget.Length == 0)
            return matches;

        for (int index = 0; index < _player.Inventory.Count; index++)
        {
            int candidateId = _player.Inventory[index];
            if (!_world.Database.Items.TryGetValue(candidateId, out var found))
                continue;

            if (!TargetNameMatcher.MatchesWordPrefix(found.Name, trimmedTarget))
                continue;

            matches.Add(new CarriedItemMatch(candidateId, found, _player.InventoryInstanceIds[index], index, null));
        }

        if (includeEquipped)
        {
            foreach (var (slot, equippedItemId) in _player.Equipment)
            {
                if (!_world.Database.Items.TryGetValue(equippedItemId, out var found))
                    continue;

                if (!TargetNameMatcher.MatchesWordPrefix(found.Name, trimmedTarget))
                    continue;

                matches.Add(new CarriedItemMatch(equippedItemId, found, GetEquipmentInstanceId(slot, equippedItemId), -1, slot));
            }
        }

        // Tie-break: when the input matches multiple distinct items at different match qualities,
        // prefer Exact > PrefixFromStart > WordPrefix. Without this, "shovel" with both "shovel"
        // and "black runed shovel" in inventory was unresolvable — the user can't type fewer
        // characters than the full canonical name. See TargetNameMatcher.NarrowToBestMatches.
        return TargetNameMatcher.NarrowToBestMatches(matches, m => m.Item.Name, trimmedTarget);
    }

    private bool TryRemoveResolvedCarriedItem(CarriedItemMatch match, out bool wasEquipped)
    {
        if (match.InventoryIndex >= 0)
        {
            wasEquipped = false;
            return TryRemoveInventoryItemAt(match.InventoryIndex, out _, out _);
        }

        if (match.EquipmentSlot == null)
        {
            wasEquipped = false;
            return false;
        }

        if (!_player.Equipment.Remove(match.EquipmentSlot))
        {
            wasEquipped = false;
            return false;
        }

        _player.EquipmentInstanceIds.Remove(match.EquipmentSlot);
        wasEquipped = true;
        return true;
    }

    private async Task ShowItemDisambiguationAsync(IEnumerable<string> itemNames)
    {
        var distinctNames = itemNames
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctNames.Count == 0)
            return;

        await _client.SendLineAsync($"{MudAnsi.BrightRed}Please be more specific.  You could have meant any of these:{MudAnsi.Reset}");
        foreach (var name in distinctNames)
            await _client.SendLineAsync($"{MudAnsi.White}-- {name}{MudAnsi.Reset}");
    }

    private bool TryFindInventoryItemById(int itemId, out int inventoryIndex, out long instanceId)
    {
        EnsureItemInstanceAlignment();

        for (int index = 0; index < _player.Inventory.Count; index++)
        {
            if (_player.Inventory[index] != itemId)
                continue;

            inventoryIndex = index;
            instanceId = _player.InventoryInstanceIds[index];
            return true;
        }

        inventoryIndex = -1;
        instanceId = 0;
        return false;
    }

    /// <summary>
    /// Bug #211. Spend one charge of the item an Item/Ticket exit required, on traverse.
    ///
    /// Exit type 3 scans the 100 INVENTORY slots for the exit's Para1
    /// and, having matched, deducts a charge — so the charge is
    /// spent on the carried copy, never on an equipped one. We only ran this for exit type 17, and stock
    /// has exactly ONE such exit (2/2552); the 194 type-3 exits — including every inn stairwell that
    /// takes a room ticket, e.g. The Homely Hearth at 1/313 — consumed nothing. The ticket gate passed
    /// forever on a single purchase.
    ///
    /// Honouring UseCount/RetainAfterUses is what makes extending this safe rather than destructive: of
    /// the 195 item exits only 5 name a limited-use item (room ticket #924 at 4 inns, smoky black
    /// talisman #657). Every other one is UseCount -1 — rope and grapple, manhole, the emblem set — and
    /// must survive being used. Removing unconditionally would have deleted them on first use.
    ///
    /// Also the charge a speech/typed ROOM-ACTION trigger spends: exit type
    /// 12 (RemoteAction) deducts the same charge on the Para4 item before running the action.
    /// The amber talisman (#815, UseCount 1) at Dragon's Teeth Hills 2/687 is the live case — "hold up
    /// talisman" opens the way to the Dark Elf lands and the talisman is spent doing it, printing its
    /// DestructMsg 1242 ("Your amber talisman dissolves in a puff of greasy yellow smoke."). We checked
    /// only that the player HELD it, so one talisman opened the door forever.
    ///
    /// Destruction messaging belongs to the charge deduction: DestructMsg row Line1 to the holder +
    /// Line2 to the room, or the generic "It's uses gone…" line when DestructMsg is 0. That keeps the
    /// exit path silent where stock is silent — room ticket #924 points at msg 66, the blank sentinel.
    /// </summary>
    private async Task ConsumeItemChargeAsync(int itemId)
    {
        if (itemId <= 0)
            return;

        if (!_world.Database.Items.TryGetValue(itemId, out var item) || item.UseCount <= 0)
            return; // unlimited-use (UseCount -1/0) — the charge deduction returns without touching it

        if (!TryFindInventoryItemById(itemId, out var inventoryIndex, out var instanceId))
            return;

        var runtimeState = _world.GetOrCreateItemRuntimeState(instanceId);
        runtimeState.RemainingCharges = (runtimeState.RemainingCharges ?? item.UseCount) - 1;
        if (runtimeState.RemainingCharges > 0 || item.RetainAfterUses)
            return;

        if (!TryRemoveInventoryItemAt(inventoryIndex, out _, out var removedInstanceId))
            return;

        _world.RemoveItemRuntimeState(removedInstanceId);
        RecalcEquipment();
        await SendItemDestructionMessagesAsync(item);
    }

    private long GetEquipmentInstanceId(string slot, int itemId)
    {
        return GetEquipmentInstanceId(_player, slot, itemId);
    }

    private long GetEquipmentInstanceId(Player player, string slot, int itemId)
    {
        _world.EnsurePlayerItemInstanceAlignment(player);

        if (player.EquipmentInstanceIds.TryGetValue(slot, out var instanceId))
            return instanceId;

        instanceId = _world.CreateItemInstance(itemId);
        player.EquipmentInstanceIds[slot] = instanceId;
        return instanceId;
    }

    private List<(int ItemId, long InstanceId, Item Item, string SlotLabel)> GetOrderedEquippedItems(Player player)
    {
        _world.EnsurePlayerItemInstanceAlignment(player);

        return player.Equipment
            .Select(entry =>
            {
                var item = _world.Database.Items.GetValueOrDefault(entry.Value);
                if (item == null)
                    return (HasItem: false, ItemId: 0, InstanceId: 0L, Item: (Item?)null, SlotLabel: string.Empty, SortOrder: int.MaxValue);

                string slotLabel = GetEquippedSlotLabel(entry.Key, item);

                return (
                    HasItem: true,
                    ItemId: entry.Value,
                    InstanceId: GetEquipmentInstanceId(player, entry.Key, entry.Value),
                    Item: item,
                    SlotLabel: slotLabel,
                    SortOrder: GetEquipmentDisplayOrder(entry.Key, item.Worn));
            })
            .Where(entry => entry.HasItem && entry.Item != null)
            .OrderBy(entry => entry.SortOrder)
            .ThenBy(entry => entry.ItemId)
            .Select(entry => (entry.ItemId, entry.InstanceId, entry.Item!, entry.SlotLabel))
            .ToList();
    }

    private bool TryGetDisplayedReadiedLightSource(Player player, IReadOnlyCollection<long> displayedInstanceIds, out int itemId, out long instanceId, out Item item)
    {
        _world.EnsurePlayerItemInstanceAlignment(player);

        itemId = 0;
        instanceId = 0;
        item = new Item();

        if (!TryGetSingleActiveLightSource(player, out itemId, out _, out instanceId))
            return false;

        if (displayedInstanceIds.Contains(instanceId))
            return false;

        if (!_world.Database.Items.TryGetValue(itemId, out var displayedItem) || displayedItem is null)
            return false;

        item = displayedItem;
        return true;
    }

    private static readonly (string SlotKey, string SlotLabel)[] ModernLookDisplaySlots =
    [
        ("worn-2", "Head"),
        ("worn-15", "Ears"),
        ("worn-8", "Neck"),
        ("worn-7", "Back"),
        ("worn-11", "Torso"),
        ("worn-6", "Arms"),
        ("worn-14", "Wrist"),
        ("worn-17", "Wrist"),
        ("worn-3", "Hands"),
        ("worn-4", "Finger"),
        ("worn-13", "Finger"),
        ("worn-10", "Waist"),
        ("worn-9", "Legs"),
        ("worn-5", "Feet"),
        ("worn-16", "Worn"),
        ("worn-12", "Off-Hand"),
        ("weapon", "Weapon Hand"),
    ];

    private List<(string ItemName, string SlotLabel)> GetModernLookEquipmentLines(Player player)
    {
        _world.EnsurePlayerItemInstanceAlignment(player);

        var equippedBySlot = player.Equipment
            .Select(entry =>
            {
                var item = _world.Database.Items.GetValueOrDefault(entry.Value);
                if (item == null)
                    return (HasItem: false, SlotKey: entry.Key, Item: (Item?)null, ItemId: 0, SortOrder: int.MaxValue);

                return (
                    HasItem: true,
                    SlotKey: entry.Key,
                    Item: item,
                    ItemId: entry.Value,
                    SortOrder: GetEquipmentDisplayOrder(entry.Key, item.Worn));
            })
            .Where(entry => entry.HasItem && entry.Item != null)
            .ToDictionary(
                entry => entry.SlotKey,
                entry => (Item: entry.Item!, ItemId: entry.ItemId, SortOrder: entry.SortOrder),
                StringComparer.Ordinal);

        var lines = new List<(string ItemName, string SlotLabel)>(ModernLookDisplaySlots.Length + equippedBySlot.Count);
        foreach (var (slotKey, slotLabel) in ModernLookDisplaySlots)
        {
            if (equippedBySlot.Remove(slotKey, out var equipped))
            {
                string occupiedSlotLabel = slotKey == "weapon"
                    ? GetEquippedSlotLabel(slotKey, equipped.Item)
                    : slotLabel;
                lines.Add((equipped.Item.Name, occupiedSlotLabel));
            }
            else
            {
                lines.Add(("<empty>", slotLabel));
            }
        }

        foreach (var equipped in equippedBySlot.Values
                     .OrderBy(entry => entry.SortOrder)
                     .ThenBy(entry => entry.ItemId))
        {
            lines.Add((equipped.Item.Name, GetEquippedSlotLabel(GetEquipmentSlotKey(equipped.Item), equipped.Item)));
        }

        var displayedInstanceIds = player.Equipment
            .Select(entry => GetEquipmentInstanceId(player, entry.Key, entry.Value))
            .ToList();

        if (TryGetDisplayedReadiedLightSource(player, displayedInstanceIds, out _, out _, out var readiedLight))
        {
            // Weapon is always listed last (stock parity): insert the readied light before the
            // weapon-hand line rather than after it.
            int weaponIndex = lines.FindIndex(l => l.SlotLabel is "Weapon Hand" or "Two handed");
            if (weaponIndex >= 0)
                lines.Insert(weaponIndex, (readiedLight.Name, "Readied"));
            else
                lines.Add((readiedLight.Name, "Readied"));
        }

        return lines;
    }

    private static string GetEquippedSlotLabel(string slotKey, Item item)
    {
        if (slotKey != "weapon")
            return GetWornSlotName(item.Worn) ?? "Readied";

        // Inventory display: ItemType 1 with WeaponType 1 or 3 prints "(Two handed)".
        return item.ItemType == 1 && (item.WeaponType == 1 || item.WeaponType == 3)
            ? "Two handed"
            : "Weapon Hand";
    }

    private static bool IsTwoHandedWeapon(Item item)
        => item.IsWieldedWeapon && (item.WeaponType == 1 || item.WeaponType == 3);

    private static bool IsWeaponKeyword(string target)
        => target.Equals("weapon", StringComparison.OrdinalIgnoreCase)
        || target.Equals("weap", StringComparison.OrdinalIgnoreCase);

    private static int GetEquipmentDisplayOrder(string slotKey, int worn)
    {
        if (slotKey == "weapon")
            return 1000;

        return worn switch
        {
            11 => 10,
            2 => 20,
            3 => 30,
            6 => 40,
            9 => 50,
            5 => 60,
            10 => 70,
            8 => 80,
            19 => 90,
            18 => 100,
            15 => 110,
            14 => 120,
            17 => 121,
            4 => 125,
            13 => 126,
            12 => 130,
            7 => 140,
            1 => 150,
            16 => 151,
            _ => 500 + worn
        };
    }

    private static string? GetWornSlotName(int worn)
    {
        return worn switch
        {
            1 => "Worn",
            2 => "Head",
            3 => "Hands",
            4 => "Finger",
            5 => "Feet",
            6 => "Arms",
            7 => "Back",
            8 => "Neck",
            9 => "Legs",
            10 => "Waist",
            11 => "Torso",
            12 => "Off-Hand",
            13 => "Finger",
            14 => "Wrist",
            15 => "Ears",
            16 => "Worn",
            17 => "Wrist",
            18 => "Eyes",
            19 => "Face",
            _ => null
        };
    }

    private List<string> BuildAlsoHereParts(int mapNumber, int roomNumber)
    {
        var alsoHereParts = new List<string>();
        GameColorPalette palette = GameColorPalettes.Resolve(_player.PaletteId);

        // Stock ordering: visible players first, then monsters.
        // Sneaking players are visible in room — sneak only hides movement enter/leave.
        // Hidden players (HIDE command) are invisible unless observer has See Hidden.
        var roomPlayers = _world.GetPlayersInRoom(mapNumber, roomNumber, _player);
        foreach (var p in roomPlayers)
        {
            if (p.IsOutOfRealm)
                continue; // left the Realm to train — not present in the room for anyone

            if (p.IsSysopInvisible && !_player.IsSysop)
                continue;

            string marker = GetAlsoHereEvilMarker(p);

            if (p.IsHidden)
            {
                if (!_player.HasSeeHidden)
                    continue;

                alsoHereParts.Add($"{palette.Get(GameColorRole.AlsoHerePlayer)}{p.Name}{marker} (Hidden){MudAnsi.Reset}");
                continue;
            }

            if (p.IsSysopInvisible)
                alsoHereParts.Add($"{palette.Get(GameColorRole.AlsoHerePlayer)}{p.Name}{marker} (Invisible){MudAnsi.Reset}");
            else if (p.IsSysopNoAggro)
                alsoHereParts.Add($"{palette.Get(GameColorRole.AlsoHerePlayer)}{p.Name}{marker} (Protected){MudAnsi.Reset}");
            else
                alsoHereParts.Add($"{palette.Get(GameColorRole.AlsoHerePlayer)}{p.Name}{marker}{MudAnsi.Reset}");
        }

        var roomMonsters = _world.GetMonstersInRoom(mapNumber, roomNumber);
        foreach (var m in roomMonsters)
        {
            if (!m.IsDead)
            {
                string color = GetMonsterColor(m.Template, _player.PaletteId);
                // A pet shows the " (Charmed)" tag ONLY to its owner.
                string charmedTag = m.IsOwnedBy(_player.Name) ? " (Charmed)" : string.Empty;
                alsoHereParts.Add($"{color}{m.DisplayName}{charmedTag}{MudAnsi.Reset}");
            }
        }

        return alsoHereParts;
    }

    // A viewed player's name gets a '*' suffix
    // when the viewer (_player) sees them as evil —
    //   ( a live PvP edge in either direction
    //     OR viewed.EvilPoints >= 40 )                                   // Outlaw+ (Seedy 30-39 excluded)
    //   AND the evil-star timer suppression is off
    // The marker abuts the name with no separating space (format "%s%s%s%s" = name, marker, "", tag).
    private string GetAlsoHereEvilMarker(Player viewed)
    {
        bool evilRelationship =
            _world.EvilTimers.AlreadyEvil(_player.Name, viewed.Name) != 0 ||
            _world.EvilTimers.AlreadyEvil(viewed.Name, _player.Name) != 0;

        bool outlaw = viewed.EvilPoints >= 40;

        if (!(evilRelationship || outlaw))
            return string.Empty;

        return _world.EvilTimers.DisplayEvilStar(viewed.Name, _player.Name) ? "*" : string.Empty;
    }

    // Monster colour for "Also here:" — based on monster alignment
    // Hostile (Align 1,2,5,6) = BrightMagenta, Guards/Templars (Align 4) = White, Peaceful (Align 0,3) = Cyan
    private static string GetMonsterColor(Monster template, int paletteId = 0)
    {
        GameColorPalette palette = GameColorPalettes.Resolve(paletteId);

        return template.Align switch
        {
            0 => palette.Get(GameColorRole.MonsterPeaceful),
            1 => palette.Get(GameColorRole.MonsterHostile),
            2 => palette.Get(GameColorRole.MonsterHostile),
            3 => palette.Get(GameColorRole.MonsterPeaceful),
            4 => palette.Get(GameColorRole.MonsterLawful),
            5 => palette.Get(GameColorRole.MonsterHostile),
            6 => palette.Get(GameColorRole.MonsterHostile),
            _ => palette.Get(GameColorRole.MonsterPeaceful),
        };
    }

    private async Task HandleGet(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            // A bare GET prints the syntax line, not a question.
            await _client.SendLineAsync("Syntax: GET {Item Name}");
            return;
        }

        // "get all" bulk pickup is a non-stock QOL convenience. When the feature is disabled, "all" is no
        // longer special — it falls through to the stock single-item path (which finds no item named "all").
        bool wantsAll = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            && _world.IsQolEnabled(QolFeature.GetAll);

        // Check if target is a currency request: "get coins", "get sil", "get 8 sil", etc.
        // Parse "get [amount] [currency]" or "get [currency]"
        var targetParts = target.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        int currAmount = 0;
        string currWord = "";
        bool isCurrencyRequest = false;
        bool isAllCurrency = false; // "coins" or "money" — pick up everything
        long currMultiplier = 0;

        if (targetParts.Length == 2 && int.TryParse(targetParts[0], out int parsedQty) && parsedQty > 0)
        {
            // "get 8 silver" or "get 8 si" or "get 1 gold crown"
            if (TryResolveCurrencyName(targetParts[1], out string resolved, out long mult))
            {
                isCurrencyRequest = true;
                currAmount = parsedQty;
                currWord = resolved;
                currMultiplier = mult;
                isAllCurrency = mult == 0; // coins/money
            }
        }
        else if (targetParts.Length == 1 && !wantsAll)
        {
            // "get silver" or "get si" or "get coins"
            if (TryResolveCurrencyName(targetParts[0], out string resolved, out long mult))
            {
                isCurrencyRequest = true;
                currAmount = -1; // all of that type
                currWord = resolved;
                currMultiplier = mult;
                isAllCurrency = mult == 0; // coins/money
            }
        }
        else if (!wantsAll && TryResolveCurrencyName(target, out string resolvedFull, out long multFull))
        {
            // "get gold crown", "get copper farthings", etc. (multi-word currency name, no amount)
            isCurrencyRequest = true;
            currAmount = -1; // all of that type
            currWord = resolvedFull;
            currMultiplier = multFull;
            isAllCurrency = multFull == 0;
        }

        // QOL bulk count: "get 10 torch" repeats the stock single-item pickup. Parsed only after the
        // currency branches have had their say, so stock's own counted form ("get 8 silver") keeps it.
        int bulkQuantity = 1;
        string itemTarget = target;
        if (!wantsAll && !isCurrencyRequest && TryParseBulkQuantity(target, out int parsedBulk, out string bulkName))
        {
            bulkQuantity = parsedBulk;
            itemTarget = bulkName;
        }

        bool pickedAnything = false;
        bool blockedByEncumbrance = false;

        // Handle currency pickup
        if (wantsAll || (isCurrencyRequest && isAllCurrency))
        {
            // "get all" or "get coins" or "get money" — pick up ALL currency
            long visibleRunic = _world.GetVisibleGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, CurrencyHelper.CopperPerRunic);
            long visiblePlatinum = _world.GetVisibleGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, CurrencyHelper.CopperPerPlatinum);
            long visibleGold = _world.GetVisibleGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, CurrencyHelper.CopperPerGold);
            long visibleSilver = _world.GetVisibleGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, CurrencyHelper.CopperPerSilver);
            long visibleCopper = _world.GetVisibleGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, 1);
            long visibleCoinCount = visibleRunic + visiblePlatinum + visibleGold + visibleSilver + visibleCopper;
            if (visibleCoinCount > 0)
            {
                if (WouldExceedEncumbranceAfterCurrencyGain(visibleRunic, visiblePlatinum, visibleGold, visibleSilver, visibleCopper))
                {
                    blockedByEncumbrance = true;
                }
                else
                {
                    var pickedCurrency = _world.PickUpGroundCurrency(_player.CurrentMapNumber, _player.CurrentRoomNumber);
                    long copperPicked = CurrencyHelper.ToCopper(
                        pickedCurrency.Runic,
                        pickedCurrency.Platinum,
                        pickedCurrency.Gold,
                        pickedCurrency.Silver,
                        pickedCurrency.Copper);
                    if (copperPicked > 0)
                    {
                        AddPlayerCurrency(
                            pickedCurrency.Runic,
                            pickedCurrency.Platinum,
                            pickedCurrency.Gold,
                            pickedCurrency.Silver,
                            pickedCurrency.Copper);
                        RecalcEquipment();
                        // One line PER denomination ("You picked up 90 runic coins" / "You picked up 37
                        // platinum pieces" / …) rather than a single combined line. MegaMUD treats each
                        // distinct "You picked up <text>" string as a new item to learn, so a combined
                        // line ("90 runic coins, 37 platinum pieces, 40 gold crowns") spawned a phantom
                        // item every time the amounts changed; per-denomination lines stay stable.
                        foreach (var coinLine in FormatCurrencyParts(
                            pickedCurrency.Runic,
                            pickedCurrency.Platinum,
                            pickedCurrency.Gold,
                            pickedCurrency.Silver,
                            pickedCurrency.Copper))
                        {
                            await _client.SendLineAsync($"You picked up {coinLine}");
                        }
                        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                            $"{_player.Name} picked up some coins.", _client);
                        pickedAnything = true;
                    }
                }
            }
        }
        else if (isCurrencyRequest && currMultiplier > 0)
        {
            // Specific denomination: "get sil" (all silver), "get 8 sil" (8 silver)
            long visibleAvailableCount = _world.GetVisibleGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, currMultiplier);
            // An exact-amount request ("get 99 platinum") falls through to the
            // HIDDEN coin pool when the visible amount can't cover it — with NO search gate. Stashed
            // coins are grabbable by anyone who names the amount; only hidden ITEMS need a search reveal.
            long hiddenAvailableCount = currAmount > 0
                ? _world.GetHiddenGroundCurrencyDenominationCount(_player.CurrentMapNumber, _player.CurrentRoomNumber, currMultiplier)
                : 0;
            if (visibleAvailableCount > 0 || hiddenAvailableCount > 0)
            {
                long availableCount = visibleAvailableCount + hiddenAvailableCount;

                if (availableCount <= 0)
                {
                    await _client.SendLineAsync("There is nothing to pick up here.");
                    return;
                }

                long pickCount = currAmount > 0 ? Math.Min(currAmount, availableCount) : visibleAvailableCount;
                long remainingCountToPick = pickCount;
                long actualPicked = 0;

                if (pickCount > 0 && WouldExceedEncumbranceAfterCurrencyDenominationGain(currMultiplier, pickCount))
                {
                    blockedByEncumbrance = true;
                    remainingCountToPick = 0;
                }

                if (remainingCountToPick > 0 && visibleAvailableCount > 0)
                {
                    long visibleCountToPick = Math.Min(remainingCountToPick, visibleAvailableCount);
                    actualPicked += _world.PickUpGroundCurrencyDenomination(
                        _player.CurrentMapNumber,
                        _player.CurrentRoomNumber,
                        currMultiplier,
                        visibleCountToPick);
                    remainingCountToPick -= visibleCountToPick;
                }

                if (remainingCountToPick > 0 && hiddenAvailableCount > 0)
                {
                    actualPicked += _world.PickUpHiddenGroundCurrencyDenomination(
                        _player.CurrentMapNumber,
                        _player.CurrentRoomNumber,
                        currMultiplier,
                        remainingCountToPick);
                }

                if (actualPicked > 0)
                {
                    long pickedCount = actualPicked / currMultiplier;
                    AddPlayerCurrencyDenomination(currMultiplier, pickedCount);
                    RecalcEquipment();
                    await _client.SendLineAsync($"You picked up {FormatCurrencyDenomination(currMultiplier, pickedCount)}");
                    _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                        $"{_player.Name} picked up some coins.", _client);
                    pickedAnything = true;
                }
            }
        }

        // Pick up ground items (only for "get all")
        if (wantsAll)
        {
            while (true)
            {
                var groundItems = _world.GetVisibleGroundItems(_player.CurrentMapNumber, _player.CurrentRoomNumber);
                if (groundItems.Count == 0) break;

                var (gItemId, gIndex) = groundItems[0];
                if (!_world.Database.Items.TryGetValue(gItemId, out var gItem) || WouldExceedEncumbranceAfterItemGain(gItem))
                {
                    blockedByEncumbrance = true;
                    break;
                }

                var picked = _world.PickUpGroundItemWithInstance(_player.CurrentMapNumber, _player.CurrentRoomNumber, gIndex);
                if (picked.ItemId > 0)
                {
                    AddItemToInventory(picked.ItemId, picked.InstanceId);
                    RecalcEquipment();
                    await _client.SendLineAsync($"You took {gItem.Name}.");
                    _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                        $"{_player.Name} took {gItem.Name}.", _client);
                    pickedAnything = true;
                }
                else break;
            }
        }
        else if (!isCurrencyRequest || !pickedAnything)
        {
            // Ground items by name: visible items always, plus only those hidden instances this player
            // has searched up in this room — you cannot grab a stash you have never seen, even by name.
            // A bare letter like "p" abbreviates a currency ("platinum"), but when no such coins are on
            // the ground we still fall through to items here — so "get p" grabs "padded boots".
            // One pass per requested copy (exactly one pass unless a QOL count was typed). The room is
            // re-scanned every pass because taking an item shifts the ground indices under us. Lines are
            // held back and summarised once the run is over — see CommandParser.BulkQuantity.cs.
            int tookCount = 0;
            string tookName = string.Empty;
            string? stopMessage = null;
            for (int pass = 0; pass < bulkQuantity; pass++)
            {
                var (itemId, index) = _world.FindGettableGroundItemByName(
                    _player.CurrentMapNumber, _player.CurrentRoomNumber, itemTarget,
                    _player.GetRevealedHiddenItems(_player.CurrentMapNumber, _player.CurrentRoomNumber));

                if (itemId <= 0)
                    break;   // nothing (left) by that name: the summary says how many, miss line below when none

                if (!_world.Database.Items.TryGetValue(itemId, out var item) || WouldExceedEncumbranceAfterItemGain(item))
                {
                    stopMessage = "You cannot carry that much!";
                    break;
                }

                var picked = _world.PickUpGroundItemWithInstance(_player.CurrentMapNumber, _player.CurrentRoomNumber, index);
                if (picked.ItemId <= 0)
                    break;

                AddItemToInventory(picked.ItemId, picked.InstanceId);
                RecalcEquipment();
                tookName = item.Name;
                tookCount++;
            }

            if (tookCount > 0)
            {
                string tookText = CountedItemText(tookCount, tookName);
                await _client.SendLineAsync($"You took {tookText}.");
                _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                    $"{_player.Name} took {tookText}.", _client);
            }

            if (stopMessage != null)
                await _client.SendLineAsync(stopMessage);

            if (tookCount > 0 || stopMessage != null)
                return;
        }

        if (blockedByEncumbrance)
        {
            await _client.SendLineAsync("You cannot carry that much!");
            return;
        }

        if (!pickedAnything)
        {
            // GET has THREE distinct misses (items are matched before currency):
            //   - bulk forms ("get all" / "get coins")            → "There is nothing to pick up here."
            //   - a recognised denomination with no such coins     → "You don't see any %s" in WHITE
            //     (ESC[0;37;40m), %s = the canonical plural name (so "get platinum" or
            //     even the abbreviation "get p" → "You don't see any platinum pieces"; no trailing period).
            //   - anything else                                    → "You don't see %s here." in BRIGHT RED
            //     (ESC[1;31;40m), echoing the word the player typed.
            if (wantsAll || isAllCurrency)
                await _client.SendLineAsync("There is nothing to pick up here.");
            else if (isCurrencyRequest && currMultiplier > 0)
                await _client.SendLineAsync($"{MudAnsi.White}You don't see any {CurrencyDenominationPluralName(currMultiplier)}{MudAnsi.Reset}");
            else
                await _client.SendLineAsync($"{MudAnsi.BrightRed}You don't see {itemTarget} here.{MudAnsi.Reset}");
        }
    }

    private async Task HandleDrop(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            // A bare DROP prints the syntax line, not a question.
            await _client.SendLineAsync("Syntax: DROP {Item Name}");
            return;
        }

        // "drop {amount} {currency}" or "drop {currency}"
        if (TryParseCurrency(target, out int currAmount, out string currName, out long copperValue, out long denominationMultiplier))
        {
            long exactCount = currAmount == -1 ? GetPlayerCurrencyDenominationCount(denominationMultiplier) : currAmount;
            long exactCopperValue = exactCount * denominationMultiplier;

            if (exactCount <= 0 || !PlayerHasCurrency(exactCopperValue))
            {
                // The currency branch is only reached with THREE args ("drop 5 gold").
                // A lone "drop gold" is margc == 2, which stock resolves as an ITEM name and answers
                // "You don't have %s to drop!" echoing the word typed. Dropping a whole
                // denomination without naming an amount is ours; when there is nothing to drop, the line
                // is stock's. The counted form keeps the currency branch's own wording, whose
                // "%s %s" is one pre-formatted "<count> <proper currency name>".
                if (currAmount == -1)
                    await _client.SendLineAsync($"You don't have {target} to drop!");
                else
                    await _client.SendLineAsync($"You don't have {currAmount} {currName} to drop!");
                return;
            }
            DeductPlayerCurrency(exactCopperValue);
            DropExactGroundCurrency(denominationMultiplier, exactCount);
            // "You dropped %s." — the canonical denomination name (count + correctly pluralised
            // "gold crowns" / "copper farthings"), NOT the abbreviation the player typed, and ending with
            // a period (unlike the period-less "You picked up %s" on the way in). So "drop 4 gold" →
            // "You dropped 4 gold crowns." (was the wrong, period-less "You dropped 4 gold").
            await _client.SendLineAsync($"You dropped {FormatCurrencyDenomination(denominationMultiplier, exactCount)}.");
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{_player.Name} dropped some {CurrencyDenominationPluralName(denominationMultiplier)}.", _client);
            return;
        }

        // QOL bulk count: "drop 10 torch". Parsed after the currency branch, so stock's own counted coin
        // form ("drop 5 gold") keeps it — only an item name behind the number gets here.
        int bulkQuantity = 1;
        string itemTarget = target;
        if (TryParseBulkQuantity(target, out int parsedBulk, out string bulkName))
        {
            bulkQuantity = parsedBulk;
            itemTarget = bulkName;
        }

        int droppedCount = 0;
        string droppedName = string.Empty;
        string? dropStopMessage = null;
        for (int pass = 0; pass < bulkQuantity; pass++)
        {
            // Find item in inventory or equipment (auto-unequip silently). Re-resolved every pass:
            // each drop removes an entry and renumbers the inventory beneath it.
            if (!TryResolveUniqueCarriedItem(itemTarget, includeEquipped: true, out var carriedItem, out var ambiguousNames))
            {
                if (ambiguousNames != null)
                {
                    await ShowItemDisambiguationAsync(ambiguousNames);
                    return;
                }

                // Dropping the last copy mid-run needs no words — the summary carries the count. A run
                // that dropped nothing prints the stock refusal, so a plain DROP is what it always was.
                if (droppedCount == 0)
                    dropStopMessage = $"You don't have {itemTarget} to drop!";
                break;
            }

            // No-drop gate, BEFORE any removal (abort leaves the item carried).
            //   1) A NotDroppable item can never be dropped — these are Loyal quest rewards
            //      such as the phoenix feather (#1000) and sunstone wristband (#1180) (bug #96).
            //   2) Cursed (82) / Major-Curse (83) items can't be dropped while still worn, unless a
            //      spare copy of the same item sits in the pack (stock counts inventory copies < 2).
            // Both are real refusals rather than a run-out, so they print however far in we are — a bulk
            // run that empties the pack down to the worn cursed copy ends on this line.
            var dropCandidate = carriedItem.Item;
            if (dropCandidate.NotDroppable)
            {
                dropStopMessage = "You may not drop that item!";
                break;
            }

            if ((dropCandidate.Abilities.ContainsKey(ItemCursedAbilityId)
                    || dropCandidate.Abilities.ContainsKey(ItemMajorCurseAbilityId))
                && _player.Equipment.ContainsValue(carriedItem.ItemId)
                && !_player.Inventory.Contains(carriedItem.ItemId))
            {
                dropStopMessage = "You may not drop that item!";
                break;
            }

            if (!TryRemoveResolvedCarriedItem(carriedItem, out _))
            {
                dropStopMessage = $"You don't have {itemTarget} to drop!";
                break;
            }

            int itemId = carriedItem.ItemId;
            long instanceId = carriedItem.InstanceId;
            var item = carriedItem.Item;
            // If dropping a lit light source, extinguish it first and show message
            if (CanItemBeLightSource(item))
            {
                if (ExtinguishLightIfActive(instanceId, item))
                    await _client.SendLineAsync($"You have removed {item.Name} and extinguished it.");
            }

            RemoveLightStateIfNotCarried(instanceId);
            RecalcEquipment();

            // The room add fails when the room's 17 visible slots are full; stock
            // then hands the item straight back and refuses the drop.
            if (!_world.DropItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, itemId, instanceId))
            {
                AddItemToInventory(itemId, instanceId);
                RecalcEquipment();
                dropStopMessage = $"There is no room to drop {item.Name} here.";
                break;
            }

            droppedName = item.Name;
            droppedCount++;
        }

        if (droppedCount > 0)
        {
            // "You dropped %s."
            string droppedText = CountedItemText(droppedCount, droppedName);
            await _client.SendLineAsync($"You dropped {droppedText}.");
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{_player.Name} dropped {droppedText}.", _client);
        }

        if (dropStopMessage != null)
            await _client.SendLineAsync(dropStopMessage);
    }

    // EAT / DRINK: consume a carried food (type 4) or
    // drink (type 5) item. Find it by name in inventory, gate on use-eligibility, then run its use-effect
    // (item ability 43) — exactly like `use`. If the effect fired, deduct a charge
    // (destroying single-charge potions/rations); if it had no effect, stock just bumps the fullness
    // / thirst counter — unmodelled here, so a quiet no-op. Eating/drinking is allowed in
    // combat (quaffing a heal) and does not break hide; the room sees it only when not hidden.
    private async Task HandleEatOrDrinkAsync(string args, bool isDrink)
    {
        int requiredType = isDrink ? 5 : 4;
        string verbSelf = isDrink ? "drink" : "eat";
        string verbRoom = isDrink ? "drinks" : "eats";

        // Both commands fail SILENTLY, in every branch. EAT and DRINK are
        // the same shape: `if (margc == 1) return 0;` for a bare verb, `if (item == NULL) return 0;`
        // when nothing in inventory matches, a use-eligibility bail, and a closing
        // `else { return 0; }` when the item is the wrong ItemType (4 food / 5 drink). Not one of those
        // branches prints anything, and stock carries no "You don't have %s to eat" or "Eat what?"
        // string for either verb to print. The only lines these commands can emit are the success pair
        // ("You eat the %s." / "%s eats %s."), the multi-match list, and — drink only — the empty
        // container notice below.
        if (string.IsNullOrWhiteSpace(args))
            return;

        // Wrong-type and not-carried collapse into one silent exit here, exactly as they do in stock:
        // its type check is a separate branch from its NULL check, but both just return.
        var matches = FindMatchingCarriedItems(args, includeEquipped: false)
            .Where(m => m.Item.ItemType == requiredType)
            .ToList();

        if (matches.Count == 0)
            return;

        var distinctNames = matches
            .Select(m => m.Item.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctNames.Count > 1)
        {
            await ShowItemDisambiguationAsync(distinctNames);
            return;
        }

        var match = matches[0];
        var item = match.Item;

        // Use-eligibility gate (class/race/level/align). Silent on failure, matching EAT/DRINK.
        if (!CanPlayerUseItem(_player, item))
            return;

        // Charge gate: DRINK announces an empty container; EAT returns silently.
        int remaining = GetRemainingItemCharges(match);
        if (item.UseCount > 0 && remaining <= 0)
        {
            if (isDrink)
                await _client.SendLineAsync($"There is nothing more that {item.Name} can do for you.");
            return;
        }

        await _client.SendLineAsync($"You {verbSelf} the {item.Name}.");
        if (!_player.IsHidden)
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{_player.Name} {verbRoom} {item.Name}.", _client);

        if (item.UseSpellId > 0 && _world.Database.Spells.TryGetValue(item.UseSpellId, out var spell))
        {
            await ApplyItemUseSpellEffectAsync(spell);
            await ConsumeCarriedItemChargeAsync(match);
        }

        // Persisted off-gate by GameSession's write-behind (MarkPlayerDirty after the command); a direct
        // synchronous SavePlayer here would block the world gate for the whole DB round-trip.
    }

    // Minimum abbreviations mirror CommandRegistry: arm 2, wield 3, wear 3, ready 5 (full word — the
    // scroll command `read` owns the shorter prefixes). Anything else that reached the equip case is
    // an abbreviation of `equip` itself, which lists rather than prompts.
    private static string? ResolveBareEquipSyntax(string typedVerb)
    {
        static bool Is(string verb, string typed, int minLength) =>
            typed.Length >= minLength && verb.StartsWith(typed, StringComparison.OrdinalIgnoreCase);

        if (Is("arm", typedVerb, 2)) return "Syntax: ARM {weapon name}";
        if (Is("wield", typedVerb, 3)) return "Syntax: WIELD {weapon name}";
        if (Is("wear", typedVerb, 3)) return "Syntax: WEAR {item name}";
        if (Is("ready", typedVerb, 5)) return "Syntax: READY {weapon name}";
        return null;
    }

    // Command dispatch: `equip` jumps STRAIGHT to the equip handler with no
    // argument check, so a bare `equip` — and every abbreviation of it, eq/equ/equi — lists your gear
    // exactly like `i` / `inventory`. Its synonyms are gated instead: `arm`, `wield`, `wear` and
    // `ready` each print their own Syntax line when typed bare (margc == 1) and only fall through to
    // the equip handler once an item is named. Verified live on a stock server:
    // eq/equ/equi/equip all printed the carried-equipment listing, while wear/wield/ready answered
    // "Syntax: WEAR {item name}" etc. We answered "Equip what?" for all of them, so `eq` — the common
    // way to check your gear — did the wrong thing entirely.
    private async Task HandleEquip(string target, string typedVerb = "equip")
    {
        if (string.IsNullOrEmpty(target))
        {
            string? syntax = ResolveBareEquipSyntax(typedVerb);
            if (syntax != null)
            {
                await _client.SendLineAsync(syntax);
                return;
            }

            // Bare `equip` (or eq/equ/equi): show the gear listing, same as `i`.
            await HandleInventory();
            return;
        }

        if (!TryFindInventoryItem(target, out int inventoryIndex, out int itemId, out long instanceId, out var item))
        {
            await _client.SendLineAsync($"You do not have {target} left unequipped.");
            return;
        }

        // The equip lookup runs "only unworn" and rejects a candidate whose
        // id already occupies the slot it would take: re-wearing an item you already have on is answered
        // with "You do not have %s left unequipped." rather than pointlessly self-swapping a duplicate from
        // your pack. A DIFFERENT item sharing the slot still swaps.
        //
        // A ring or bracelet whose twin is already worn is NOT that case. The paired-slot scan
        // matches on the item number and REMOVES the worn copy, so a second silver bracelet replaces the
        // first instead of filling the sibling wrist — it must fall through to the ordinary swap below,
        // which is what performs that removal. Previously it took the free sibling slot and the character
        // ended up wearing both, collecting the item's abilities twice.
        if (!IsPairedSlotDuplicate(item)
            && _player.Equipment.TryGetValue(ResolveEquipSlot(item), out int occupyingSameId)
            && occupyingSameId == itemId)
        {
            await _client.SendLineAsync($"You do not have {target} left unequipped.");
            return;
        }

        // Equipping clears the hidden and sneak flags
        // the moment a matching inventory item is found — before wear/ready validation. Fumbling an item
        // into place gives you away: you become visible to other players and eligible for monster aggro
        // (CheckEncounters runs after this command). Mirrors the break even when the equip is rejected.
        await BreakSneakAndHideForAction();

        if (CanItemBeLightSource(item))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}{item.Name} may not be worn!{MudAnsi.Reset}");
            return;
        }

        // A wielded weapon (ItemType 1) goes to the weapon hand; anything else must have a wear
        // location (Worn > 0) to be worn. A damaging item that is neither (e.g. a scroll/thrown weapon,
        // ItemType 3/9, Worn 0) is not equippable — IsWeapon (damage) must not let it through here.
        if (!CanPlayerUseItem(_player, item) || (!item.IsWieldedWeapon && item.Worn <= 0))
        {
            await _client.SendLineAsync("You may not wear that item!");
            return;
        }

        if (IsTwoHandedWeapon(item)
            && _player.Equipment.TryGetValue("worn-12", out int offHandId)
            && _world.Database.Items.TryGetValue(offHandId, out var offHandItem))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You may not ready a 2-handed weapon with your {offHandItem.Name} worn!{MudAnsi.Reset}");
            return;
        }

        if (item.Worn == 12
            && _player.Equipment.TryGetValue("weapon", out int wpnId)
            && _world.Database.Items.TryGetValue(wpnId, out var wpnItem)
            && IsTwoHandedWeapon(wpnItem))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You may not wear an off-hand item while you have a 2-handed weapon readied.{MudAnsi.Reset}");
            return;
        }

        // The PAIRED-slot branch (fingers 4/13, wrists 14/17): as the rack is
        // scanned, the curse test runs on every worn piece of that family and fires BEFORE the
        // free-slot and item-number logic — and a cursed piece makes the whole call `return 0` with no
        // output at all. The early return skips the trailing flush, so nothing the routine had
        // buffered is flushed either: stock gives the player NO line to explain it.
        //
        // Two consequences worth stating, both faithful: one cursed ring blocks every other ring even
        // with the second finger bare (the check does not care whether a slot is free), and it says
        // nothing while doing so. The non-paired branch further down is the one that speaks.
        if (IsPairedSlotBlockedByCurse(item))
            return;

        string slot = ResolveEquipSlot(item);

        if (_player.Equipment.TryGetValue(slot, out int oldItemId))
        {
            // Equipping over a slot already filled by a
            // cursed item (abilities 82/83) is blocked — you can't displace what you can't remove.
            // Weapons report "You cannot remove %s!"; armour "You are already wearing %s and
            // it may not be removed."
            if (_world.Database.Items.TryGetValue(oldItemId, out var occupyingItem)
                && (occupyingItem.Abilities.ContainsKey(ItemCursedAbilityId)
                    || occupyingItem.Abilities.ContainsKey(ItemMajorCurseAbilityId)))
            {
                await _client.SendLineAsync(item.IsWieldedWeapon
                    ? $"{MudAnsi.BrightRed}You cannot remove {occupyingItem.Name}!{MudAnsi.Reset}"
                    : $"{MudAnsi.BrightRed}You are already wearing {occupyingItem.Name} and it may not be removed.{MudAnsi.Reset}");
                return;
            }

            long oldInstanceId = GetEquipmentInstanceId(slot, oldItemId);
            _player.Equipment.Remove(slot);
            _player.EquipmentInstanceIds.Remove(slot);
            AddItemToInventory(oldItemId, oldInstanceId);

            var oldItem = _world.Database.Items[oldItemId];
            ApplyEquipInstantAbilities(oldItem, equipping: false);
            bool extinguished = ExtinguishLightIfActive(oldInstanceId, oldItem);
            // Armour swaps emit BOTH the room remove line AND a self "You have
            // removed %s."; a weapon swap emits
            // ONLY the room line — the new "You are now holding %s." carries the self update.
            // Mirror both behaviours: self message gated on !IsWieldedWeapon, room broadcast always.
            // Punctuation also differs: armour uses "removes %s!",
            // weapon uses "removes %s."
            if (extinguished)
                await _client.SendLineAsync($"You have removed {oldItem.Name} and extinguished it.");
            else if (!oldItem.IsWieldedWeapon)
                await _client.SendLineAsync($"You have removed {oldItem.Name}.");

            string removeRoomLine = oldItem.IsWieldedWeapon
                ? $"{MudAnsi.Yellow}{_player.Name} removes {oldItem.Name}.{MudAnsi.Reset}"
                : $"{MudAnsi.Yellow}{_player.Name} removes {oldItem.Name}!{MudAnsi.Reset}";
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                removeRoomLine, _client);
        }

        TryRemoveInventoryItemAt(inventoryIndex, out _, out _);
        _player.Equipment[slot] = itemId;
        _player.EquipmentInstanceIds[slot] = instanceId;
        ApplyEquipInstantAbilities(item, equipping: true);
        // Self message is "You are now wearing %s."
        // for armour and "You are now holding %s." for weapons. Room message at @15137 / @67914 is
        // "%s wears %s!" for armour and "%s wields %s!" for weapons — only sent on a successful equip.
        if (item.IsWieldedWeapon)
        {
            await _client.SendLineAsync($"You are now holding {item.Name}.");
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{MudAnsi.Yellow}{_player.Name} wields {item.Name}!{MudAnsi.Reset}", _client);
            // "feels heavy" when Str < StrReq; otherwise, if the
            // weapon is "too fast" for the wielder (Quick & Deadly trips in
            // the marshal: EU < 200, encumbrance < 67, AND Str >= StrReq), the quick-and-deadly note. The
            // two are mutually exclusive (heavy ⟺ Str < StrReq; Q&D already requires Str >= StrReq).
            if (IsWeaponHeavyForPlayer(_player, item))
            {
                await _client.SendLineAsync("This weapon feels heavy in your hands.");
            }
            else if (CombatEngine.GetWeaponQuickAndDeadlyBonus(
                         _player, _world.Database.Classes[_player.ClassId], item, isBashing: false, _world.Database) > 0)
            {
                await _client.SendLineAsync("You are extremely quick and deadly with this weapon.");
            }
        }
        else
        {
            await _client.SendLineAsync($"You are now wearing {item.Name}.");
            _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
                $"{MudAnsi.Yellow}{_player.Name} wears {item.Name}!{MudAnsi.Reset}", _client);
        }

        // Recalculate AC
        RecalcEquipment();
    }

    private static string GetEquipmentSlotKey(Item item)
    {
        // Only ItemType == 1 wields into the main weapon hand. A damaging
        // off-hand item (main-gauche: Worn 12) is worn by its wear-location, not jammed into the weapon
        // slot — bug #111. Use IsWieldedWeapon (type), never IsWeapon (damage), for slot routing.
        if (item.IsWieldedWeapon)
            return "weapon";

        if (item.Worn > 0)
            return $"worn-{item.Worn}";

        return "armor";
    }

    // Worn gear occupies a flat 20-slot rack and body-location uniqueness is
    // enforced by scanning, not by a fixed per-location key. Fingers (worn 4/13) and wrists (worn 14/17)
    // each expose TWO interchangeable slots, so a character wears up to two rings and two bracers before
    // a new one displaces an existing piece (@15048-15065). We model the rack as a dictionary keyed by
    // worn value, so spill a paired item into its sibling slot when its own key is taken; only when BOTH
    // paired slots are full does the equip fall through to the normal single-slot swap (displace one).
    private string ResolveEquipSlot(Item item)
    {
        string primary = GetEquipmentSlotKey(item);

        int[]? pair = GetPairedWornSlots(item.Worn);
        if (pair != null && !item.IsWieldedWeapon)
        {
            // A worn TWIN claims the equip ahead of any free sibling — see IsPairedSlotDuplicate. Two
            // silver bracelets is not a pair of bracelets; the second takes the first off.
            string? duplicate = FindPairedSlotHoldingItem(pair, item.Number);
            if (duplicate != null)
                return duplicate;

            foreach (int worn in pair)
            {
                string candidate = $"worn-{worn}";
                if (!_player.Equipment.ContainsKey(candidate))
                    return candidate;
            }
        }

        return primary;
    }

    /// <summary>
    /// True when one of the item's paired wear slots is already filled by the SAME item.
    ///
    /// The paired-slot branch (fingers 4/13, wrists 14/17) walks the worn rack
    /// and, for every piece in the same family, compares its ITEM NUMBER against the one going on —
    /// A match is taken OFF (its equip abilities reversed, the slot
    /// cleared, "%s removes %s!" to the room and "You have removed %s." to the wearer) before the new copy
    /// is placed. So the two ring / two bracelet allowance is two DIFFERENT pieces: a second copy of the
    /// same one replaces the first rather than stacking beside it, and its abilities never double up.
    /// </summary>
    private bool IsPairedSlotDuplicate(Item item)
    {
        if (item.IsWieldedWeapon)
            return false;

        int[]? pair = GetPairedWornSlots(item.Worn);
        return pair != null && FindPairedSlotHoldingItem(pair, item.Number) != null;
    }

    /// <summary>
    /// True when any piece already worn in this item's paired family (fingers 4/13, wrists 14/17) is
    /// cursed. Stock reaches the curse test for EVERY family member it scans, not just the one
    /// that would be displaced, and answers with a bare `return 0` — so the equip is refused in silence.
    /// Weapons never reach this: they have no paired family and go through the weapon path, which speaks
    /// ("You cannot remove %s!").
    /// </summary>
    private bool IsPairedSlotBlockedByCurse(Item item)
    {
        if (item.IsWieldedWeapon)
            return false;

        int[]? pair = GetPairedWornSlots(item.Worn);
        if (pair == null)
            return false;

        foreach (int worn in pair)
        {
            if (_player.Equipment.TryGetValue($"worn-{worn}", out int wornItemId)
                && _world.Database.Items.TryGetValue(wornItemId, out var wornItem)
                && (wornItem.Abilities.ContainsKey(ItemCursedAbilityId)
                    || wornItem.Abilities.ContainsKey(ItemMajorCurseAbilityId)))
            {
                return true;
            }
        }

        return false;
    }

    private string? FindPairedSlotHoldingItem(int[] pairedWornSlots, int itemNumber)
    {
        foreach (int worn in pairedWornSlots)
        {
            string candidate = $"worn-{worn}";
            if (_player.Equipment.TryGetValue(candidate, out int wornItemId) && wornItemId == itemNumber)
                return candidate;
        }

        return null;
    }

    // Wear locations that share a second interchangeable slot:
    // fingers 4↔13 and wrists 14↔17. Order matters — the item's own location is tried first.
    private static int[]? GetPairedWornSlots(int worn) => worn switch
    {
        4 => [4, 13],
        13 => [13, 4],
        14 => [14, 17],
        17 => [17, 14],
        _ => null,
    };

    // Instant
    // per-equip effects applied once on equip and reversed on unequip. Ability 88 adds flat max+current HP
    // while worn; ability 160 grants (or purges) a spell in the spellbook. MaxHP is a stored, incremental
    // field, so the delta persists through level-ups and saves (matching stock).
    private void ApplyEquipInstantAbilities(Item item, bool equipping)
        => ApplyEquipInstantAbilities(_player, item, equipping);

    private void ApplyEquipInstantAbilities(Player target, Item item, bool equipping)
    {
        int sign = equipping ? 1 : -1;
        foreach (var (abil, val) in item.Abilities)
        {
            if (abil == 88)
            {
                target.MaxHP += sign * val;
                target.CurrentHP += sign * val;
            }
            else if (abil == 160)
            {
                target.SetQuestAbilityValue(GetLearnedScrollSpellAbilityId(val), equipping ? 1 : 0);
            }
        }
    }

    private async Task HandleRemove(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            // A bare REMOVE prints the syntax line
            // before the remove handler is ever called. Lower-case "{item name}", unlike GET/DROP's title case.
            await _client.SendLineAsync("Syntax: REMOVE {item name}");
            return;
        }

        // Find equipped item by name
        string? removeSlot = null;
        int removeItemId = 0;
        foreach (var (slot, itemId) in _player.Equipment)
        {
            if (_world.Database.Items.TryGetValue(itemId, out var item) &&
                item.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                removeSlot = slot;
                removeItemId = itemId;
                break;
            }
        }

        if (removeSlot == null)
        {
            // "You now have no weapon readied." when no weapon is equipped.
            if (IsWeaponKeyword(target) && !_player.Equipment.ContainsKey("weapon"))
            {
                await _client.SendLineAsync($"{MudAnsi.Yellow}You now have no weapon readied.{MudAnsi.Reset}");
                return;
            }

            // Stock behavior: REMOVE can also extinguish a lit light source carried in inventory.
            if (TryFindCarriedItem(target, out int carriedItemId, out var carriedItem, out long carriedInstanceId) && CanItemBeLightSource(carriedItem))
            {
                if (ExtinguishLightIfActive(carriedInstanceId, carriedItem))
                {
                    await _client.SendLineAsync($"You have removed {carriedItem.Name} and extinguished it.");
                }
                else
                {
                    await _client.SendLineAsync($"You do not have {carriedItem.Name} lit.");
                }
                return;
            }

            await _client.SendLineAsync($"{MudAnsi.BrightRed}You are not wearing {target}.{MudAnsi.Reset}");
            return;
        }

        // Cursed items (abilities 82/83) cannot be removed once worn.
        if (_world.Database.Items.TryGetValue(removeItemId, out var removeCandidate)
            && (removeCandidate.Abilities.ContainsKey(ItemCursedAbilityId)
                || removeCandidate.Abilities.ContainsKey(ItemMajorCurseAbilityId)))
        {
            // "You cannot remove %s!" — the same line
            // the weapon path uses when a cursed weapon blocks a swap. "You can't seem to remove %s!" is
            // not a stock string; it was invented here.
            await _client.SendLineAsync($"{MudAnsi.BrightRed}You cannot remove {removeCandidate.Name}!{MudAnsi.Reset}");
            return;
        }

        long removeInstanceId = GetEquipmentInstanceId(removeSlot, removeItemId);
        _player.Equipment.Remove(removeSlot);
        _player.EquipmentInstanceIds.Remove(removeSlot);
        AddItemToInventory(removeItemId, removeInstanceId);
        var removedItem = _world.Database.Items[removeItemId];
        ApplyEquipInstantAbilities(removedItem, equipping: false);

        bool extinguished = ExtinguishLightIfActive(removeInstanceId, removedItem);
        if (extinguished)
            await _client.SendLineAsync($"You have removed {removedItem.Name} and extinguished it.");
        else
            await _client.SendLineAsync($"You have removed {removedItem.Name}.");
        // Armour: "%s removes %s!" / weapon: "%s removes %s."
        string removeMsg = removedItem.IsWieldedWeapon
            ? $"{MudAnsi.Yellow}{_player.Name} removes {removedItem.Name}.{MudAnsi.Reset}"
            : $"{MudAnsi.Yellow}{_player.Name} removes {removedItem.Name}!{MudAnsi.Reset}";
        _world.BroadcastToRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber,
            removeMsg, _client);

        RecalcEquipment();
    }

    private async Task HandleExtinguish(string target)
    {
        SynchronizeLightState();

        if (string.IsNullOrWhiteSpace(target))
        {
            if (!TryGetSingleActiveLightSource(out _, out _, out _))
            {
                await _client.SendLineAsync("Extinguish what?");
                return;
            }

            // Extinguish the currently lit source.
            if (TryGetSingleActiveLightSource(out int activeItemId, out _, out long activeInstanceId) && _world.Database.Items.TryGetValue(activeItemId, out var activeItem))
            {
                if (ExtinguishLightIfActive(activeInstanceId, activeItem))
                {
                    await _client.SendLineAsync($"You have removed {activeItem.Name} and extinguished it.");
                    return;
                }
            }

            await _client.SendLineAsync("You do not have anything lit.");
            return;
        }

        if (!TryFindCarriedItem(target, out int itemId, out var item, out long instanceId) || !CanItemBeLightSource(item))
        {
            await _client.SendLineAsync($"You do not have {target} lit.");
            return;
        }

        if (ExtinguishLightIfActive(instanceId, item))
        {
            await _client.SendLineAsync($"You have removed {item.Name} and extinguished it.");
            return;
        }

        await _client.SendLineAsync($"You do not have {item.Name} lit.");
    }

    // If your class permits it, you will be able to hide amongst the shadows.
    // "Is being attacked": true when any other user is currently combatting this player
    // (has them as their combat target). Combat is room-bound, so a same-room scan matches the stock intent
    // and preserves the "flee to another room, then hide" escape (the pursuer is no longer in your room).
    private bool IsBeingAttackedByPlayer()
        => _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            .Any(p => ReferenceEquals(p.PlayerCombatTarget, _player));

    // A hide blocked by combat — your own autocombat, a player who
    // engaged you, or a monster that could attack — is not refused with a distinct line; it silently fails
    // exactly like an ordinary missed hide ("You don't think you are hidden."), so the attempt confirms
    // nothing about combat state. Stock also clears the hidden flag at the start of every
    // self-hide, so a blocked attempt leaves you un-hidden.
    private async Task SendHideBlockedByCombatAsync()
    {
        _player.IsHidden = false;
        await _client.SendLineAsync($"{MudAnsi.White}Attempting to hide... You don't think you are hidden.{MudAnsi.Reset}");
    }

    private async Task HandleHide(string args)
    {
        // Item/currency hiding (hide/stash with args) is allowed even while attacking;
        // only hiding yourself is blocked by the combat-state restriction.
        if (!string.IsNullOrWhiteSpace(args))
        {
            await HandleHideItem(args);
            return;
        }

        // Combat gate: a hide is blocked while you are in your own autocombat
        // OR a player has engaged you — you can't hide to break off an active fight.
        // Crucially this is NOT a distinct refusal: it silently fails like an ordinary missed hide (see
        // SendHideBlockedByCombatAsync), so it never confirms you tried. Fleeing to another room drops both
        // states (the pursuer is no longer in your room), which is why "flee → hide → sneak back" works.
        if (_player.InCombat || IsBeingAttackedByPlayer())
        {
            await SendHideBlockedByCombatAsync();
            return;
        }

        if (IsMovementBlockedByStatus)
        {
            // Hide gate: smash-knockdown → "...too stunned...", HoldPerson root → "...can't seem...".
            string hideBlock = (_player.IsKnockedDown && _player.KnockdownKind == KnockdownKind.Smash)
                ? "You are too stunned to move anywhere to hide!"
                : "You can't seem to move anywhere to hide!";
            await _client.SendLineAsync($"{MudAnsi.White}{hideBlock}{MudAnsi.Reset}");
            return;
        }

        // Hide self — requires stealth ability
        bool hasStealth = _player.Stealth > 0;
        if (!hasStealth)
        {
            await _client.SendLineAsync($"{MudAnsi.White}You can't seem to move anywhere to hide!{MudAnsi.Reset}");
            return;
        }

        _player.IsResting = false;
        _player.IsMeditating = false;

        // Hide gate: no monster in the room may be able to attack (see CombatEngine). Like the
        // combat/being-attacked gate above, this SILENTLY fails (not "...can't seem to move...") — that
        // line is the HoldPerson/root block handled above.
        var monstersHere = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (CombatEngine.MonsterCouldAttack(_player, monstersHere))
        {
            await SendHideBlockedByCombatAsync();
            return;
        }

        // Hide stealth check — the shared formula (Player.CalculateStealthChance).
        var rng = new Random();
        int chance = Player.CalculateStealthChance(
            _player.Stealth,
            GetMovementEncumbrancePercent(),
            _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player).Count(),
            monstersHere.Count(m => !m.IsDead),
            _player.RecentlySpotted);
        bool success = rng.Next(100) < chance;

        // "Attempting to hide..." then " You don't think you are hidden." on fail
        if (success)
        {
            _player.IsHidden = true;
            await _client.SendLineAsync($"{MudAnsi.White}Attempting to hide...{MudAnsi.Reset}");
        }
        else
        {
            _player.IsHidden = false;
            // The "You don't think you are hidden." reveal only fires when
            // a roll of 0..99 < your Perception; otherwise you're (wrongly) told you succeeded.
            // (Was: always revealed the failure.)
            if (rng.Next(100) < _player.Perception)
                await _client.SendLineAsync($"{MudAnsi.White}Attempting to hide... You don't think you are hidden.{MudAnsi.Reset}");
            else
                await _client.SendLineAsync($"{MudAnsi.White}Attempting to hide...{MudAnsi.Reset}");
        }
    }

    // "hide {item}" / "stash {item}" — hides an item from inventory into the room
    private async Task HandleHideItem(string target)
    {
        // "hide {amount} {currency}" — hide currency
        if (TryParseCurrency(target, out int currAmount, out string currName, out long copperValue, out long denominationMultiplier))
        {
            long exactCount = currAmount == -1 ? GetPlayerCurrencyDenominationCount(denominationMultiplier) : currAmount;
            long exactCopperValue = exactCount * denominationMultiplier;

            if (exactCount <= 0 || !PlayerHasCurrency(exactCopperValue))
            {
                // The currency branch needs THREE args ("hide 5 gold"), and its
                // shortfall line is "You don't have %s %s to hide!". A lone "hide gold" is
                // margc == 2, which falls into the item lookup, finds nothing, and returns 0 WITHOUT a
                // message — so it drops through to room-text matching exactly like an unknown item does
                // a few lines below. The counted form keeps the currency wording.
                if (currAmount == -1)
                {
                    if (await TryHandleRoomAction($"hide {target}"))
                        return;
                    await _client.SendLineAsync("Your command had no effect.");
                    return;
                }

                await _client.SendLineAsync($"You don't have {currAmount} {currName} to hide!");
                return;
            }
            DeductPlayerCurrency(exactCopperValue);
            HideExactGroundCurrency(denominationMultiplier, exactCount);
            // "You hid %s %s." (count + denomination), with a trailing period.
            await _client.SendLineAsync($"You hid {exactCount} {currName}.");
            return;
        }

        if (!TryResolveUniqueCarriedItem(target, includeEquipped: true, out var carriedItem, out var ambiguousNames))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return;
            }

            // A single-arg `hide <x>` whose <x> is not a carried item hits the
            // not-found branch and returns silently — there is NO "You don't have %s to hide!" item
            // string (only the currency form "You don't have %s %s to hide!"). So an unmatched item falls
            // through to room text/exit matching, then "Your command had no effect." — like drop/bash.
            if (await TryHandleRoomAction($"hide {target}"))
                return;
            await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        // The SAME no-drop gate as DROP, checked BEFORE any removal (abort
        // leaves the item carried) — just with the "hide" wording. Without this, a
        // NotDroppable Loyal item (e.g. the Hellblade) that `drop` correctly refuses could still be ditched
        // via `stash`/`hide <item>`. Mirrors HandleDrop's two gates.
        //   1) A NotDroppable item can never be hidden.
        //   2) Cursed (82) / Major-Curse (83) items can't be hidden while still worn, unless a spare copy
        //      of the same item sits in the pack.
        var hideCandidate = carriedItem.Item;
        if (hideCandidate.NotDroppable)
        {
            await _client.SendLineAsync("You may not hide that item!");
            return;
        }

        if ((hideCandidate.Abilities.ContainsKey(ItemCursedAbilityId)
                || hideCandidate.Abilities.ContainsKey(ItemMajorCurseAbilityId))
            && _player.Equipment.ContainsValue(carriedItem.ItemId)
            && !_player.Inventory.Contains(carriedItem.ItemId))
        {
            await _client.SendLineAsync("You may not hide that item!");
            return;
        }

        if (!TryRemoveResolvedCarriedItem(carriedItem, out _))
        {
            await _client.SendLineAsync($"You don't have {target} to hide!");
            return;
        }

        int itemId = carriedItem.ItemId;
        long instanceId = carriedItem.InstanceId;

        RemoveLightStateIfNotCarried(instanceId);

        var item = carriedItem.Item;

        // "There is no room to hide %s here." — room has a max of 15 hidden item slots
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        var hiddenInRoom = _world.GetHiddenGroundItems(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        var staticHidden = room?.GetHiddenItemIds()?.Count ?? 0;
        if (hiddenInRoom.Count + staticHidden >= 15)
        {
            // Can't hide — give item back to inventory
            AddItemToInventory(itemId, instanceId);
            RecalcEquipment();
            await _client.SendLineAsync($"There is no room to hide {item.Name} here.");
            return;
        }

        // Hide in room. Same -1 contract as the drop path, against the 15 hidden slots.
        if (!_world.HideItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, itemId, instanceId))
        {
            AddItemToInventory(itemId, instanceId);
            RecalcEquipment();
            await _client.SendLineAsync($"There is no room to hide {item.Name} here.");
            return;
        }

        RecalcEquipment();

        // "You hid %s."
        await _client.SendLineAsync($"You hid {item.Name}.");
    }

    private async Task HandleLight(string target)
    {
        SynchronizeLightState();

        if (TryGetSingleActiveLightSource(out _, out _, out _))
        {
            await _client.SendLineAsync("You already have something lit!");
            return;
        }

        Item item;
        long instanceId;
        if (string.IsNullOrWhiteSpace(target))
        {
            // No selector: light the first carried/worn light source.
            if (!TryFindLightSource(target, out _, out item, out instanceId))
            {
                await _client.SendLineAsync("You do not have a light source to light.");
                return;
            }
        }
        else
        {
            // Context-aware resolution: match carried/worn items by whole-word prefix (TargetNameMatcher)
            // and PREFER actual light sources, so "light tor" finds the "torch" rather than stopping on
            // an item whose name merely contains those letters (the old substring scan lit on
            // "sTORmhammer" and bailed). Mirrors stock LIGHT resolving through the inventory lookup.
            var matches = FindMatchingCarriedItems(target, includeEquipped: true);
            var lightMatches = matches.Where(match => CanItemBeLightSource(match.Item)).ToList();
            var distinctLightNames = lightMatches
                .Select(match => match.Item.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (distinctLightNames.Count > 1)
            {
                await ShowItemDisambiguationAsync(distinctLightNames);
                return;
            }

            if (lightMatches.Count == 0)
            {
                // A named item that matches but isn't a light source → stock "You cannot light %s.";
                // only when nothing matches at all do we report having no light source.
                await _client.SendLineAsync(matches.Count > 0
                    ? $"You cannot light the {matches[0].Item.Name}."
                    : "You do not have a light source to light.");
                return;
            }

            item = lightMatches[0].Item;
            instanceId = lightMatches[0].InstanceId;
        }

        GameWorld.ItemRuntimeState runtimeState;
        if (_world.TryGetItemRuntimeState(instanceId, out var existingRuntimeState))
        {
            runtimeState = existingRuntimeState;
            NormalizeLightState(instanceId, item, runtimeState, DateTime.UtcNow);
        }
        else
        {
            runtimeState = _world.GetOrCreateItemRuntimeState(instanceId);
        }

        if (runtimeState.LightRechargeReadyAtUtc > DateTime.UtcNow)
        {
            await _client.SendLineAsync("You must recharge that before you may light it again.");
            return;
        }

        runtimeState.LightRechargeReadyAtUtc = DateTime.MinValue;
        var duration = GetRemainingOrDefaultLightDuration(instanceId, item);
        runtimeState.StoredLightRemaining = TimeSpan.Zero;
        runtimeState.ActiveLightUntilUtc = DateTime.UtcNow + duration;

        await _client.SendLineAsync($"You lit the {item.Name}.");
        _world.BroadcastToRoom(
            _player.CurrentMapNumber,
            _player.CurrentRoomNumber,
            $"{_player.Name} lights {GetIndefiniteArticle(item.Name)} {item.Name}.",
            _client);
    }

    // ── Item/Currency Helpers ──────────────────────────────────────────

    private void SynchronizeLightState()
    {
        EnsureItemInstanceAlignment();
        var now = DateTime.UtcNow;
        var expiredLightInstanceIds = new HashSet<long>();
        var expiredLightItems = new List<(long InstanceId, Item Item)>();

        foreach (var (_, instanceId, item, _) in GetOrderedEquippedItems(_player))
        {
            if (!CanItemBeLightSource(item))
                continue;

            if (!_world.TryGetItemRuntimeState(instanceId, out var runtimeState))
                continue;

            if (ShouldConsumeExpiredLightSource(item, runtimeState, now) && expiredLightInstanceIds.Add(instanceId))
            {
                expiredLightItems.Add((instanceId, item));
                continue;
            }

            NormalizeLightState(instanceId, item, runtimeState, now);
        }

        for (int index = 0; index < _player.Inventory.Count; index++)
        {
            int itemId = _player.Inventory[index];
            if (!_world.Database.Items.TryGetValue(itemId, out var item) || !CanItemBeLightSource(item))
                continue;

            long instanceId = _player.InventoryInstanceIds[index];
            if (!_world.TryGetItemRuntimeState(instanceId, out var runtimeState))
                continue;

            if (ShouldConsumeExpiredLightSource(item, runtimeState, now) && expiredLightInstanceIds.Add(instanceId))
            {
                expiredLightItems.Add((instanceId, item));
                continue;
            }

            NormalizeLightState(instanceId, item, runtimeState, now);
        }

        bool recalcEquipment = false;
        foreach (var (instanceId, item) in expiredLightItems)
        {
            recalcEquipment |= RemoveExpiredLightSource(instanceId);
            SendLightBurnoutMessage(item);
        }

        if (recalcEquipment)
            RecalcEquipment();
    }

    private void NormalizeLightState(long instanceId, Item item, GameWorld.ItemRuntimeState runtimeState, DateTime now)
    {
        if (runtimeState.ActiveLightUntilUtc != DateTime.MinValue && runtimeState.ActiveLightUntilUtc <= now)
        {
            runtimeState.ActiveLightUntilUtc = DateTime.MinValue;
            runtimeState.StoredLightRemaining = TimeSpan.Zero;
            if (RequiresRecharge(item))
                runtimeState.LightRechargeReadyAtUtc = now + GetRechargeDuration(item);
        }

        if (runtimeState.LightRechargeReadyAtUtc != DateTime.MinValue && runtimeState.LightRechargeReadyAtUtc <= now)
            runtimeState.LightRechargeReadyAtUtc = DateTime.MinValue;

        // Drop the slot only when it holds NOTHING — this sweep is light-source housekeeping, but the
        // runtime state it deletes is the item instance's whole per-instance record, and RemainingCharges
        // (the uses count in the player's own item slot) lives there too. Deleting it
        // on light grounds silently reset every charged item to full the next time anything scanned for a
        // readied light — which on a relog is immediate, so the black flail #349 always read ten charges
        // no matter how many casts had been spent (bug #218).
        if (runtimeState.ActiveLightUntilUtc == DateTime.MinValue
            && runtimeState.LightRechargeReadyAtUtc == DateTime.MinValue
            && runtimeState.StoredLightRemaining <= TimeSpan.Zero
            && runtimeState.RemainingCharges == null)
        {
            _world.RemoveItemRuntimeState(instanceId);
        }
    }

    private static bool ShouldConsumeExpiredLightSource(Item item, GameWorld.ItemRuntimeState runtimeState, DateTime now)
    {
        return CanItemBeLightSource(item)
            && !item.RetainAfterUses
            && runtimeState.ActiveLightUntilUtc != DateTime.MinValue
            && runtimeState.ActiveLightUntilUtc <= now;
    }

    private bool RemoveExpiredLightSource(long instanceId)
    {
        EnsureItemInstanceAlignment();

        for (int index = 0; index < _player.InventoryInstanceIds.Count; index++)
        {
            if (_player.InventoryInstanceIds[index] != instanceId)
                continue;

            _player.Inventory.RemoveAt(index);
            _player.InventoryInstanceIds.RemoveAt(index);
            _world.RemoveItemRuntimeState(instanceId);
            return false;
        }

        string? equippedSlot = null;
        foreach (var (slot, equippedInstanceId) in _player.EquipmentInstanceIds)
        {
            if (equippedInstanceId != instanceId)
                continue;

            equippedSlot = slot;
            break;
        }

        if (equippedSlot == null)
        {
            _world.RemoveItemRuntimeState(instanceId);
            return false;
        }

        _player.Equipment.Remove(equippedSlot);
        _player.EquipmentInstanceIds.Remove(equippedSlot);
        _world.RemoveItemRuntimeState(instanceId);
        return true;
    }

    private void SendLightBurnoutMessage(Item item)
    {
        string message = GetLightBurnoutMessage(item);
        if (string.IsNullOrWhiteSpace(message))
            return;

        _client.SendLineAsync(message).GetAwaiter().GetResult();
    }

    private string GetLightBurnoutMessage(Item item)
    {
        return ResolveLightBurnoutMessage(item, _world.Database.Messages);
    }

    internal static string ResolveLightBurnoutMessage(Item item, IReadOnlyDictionary<int, RoomMessage> messages)
    {
        if (TryGetLightBurnoutMessageId(item, out int messageId))
        {
            if (messageId == GenericStockLightBurnoutMessageId)
                return FormatGenericLightBurnoutMessage(item);

            if (messageId == SilentStockLightBurnoutMessageId)
                return string.Empty;

            if (messages.TryGetValue(messageId, out var stockMessage)
                && !string.IsNullOrWhiteSpace(stockMessage.Line1))
            {
                return stockMessage.Line1;
            }

            return string.Empty;
        }

        return FormatGenericLightBurnoutMessage(item);
    }

    private static string FormatGenericLightBurnoutMessage(Item item)
    {
        return $"It's uses gone, {item.Name} disappears from your inventory!";
    }

    private static bool TryGetLightBurnoutMessageId(Item item, out int messageId)
    {
        if (StockLightBurnoutMessageIds.TryGetValue(item.Number, out messageId))
            return true;

        messageId = GenericStockLightBurnoutMessageId;
        return false;
    }

    private bool TryFindLightSource(string target, out int itemId, out Item item)
    {
        return TryFindLightSource(target, out itemId, out item, out _);
    }

    private bool TryFindLightSource(string target, out int itemId, out Item item, out long instanceId)
    {
        EnsureItemInstanceAlignment();

        itemId = 0;
        instanceId = 0;
        item = new Item();

        if (string.IsNullOrWhiteSpace(target))
        {
            foreach (var (slot, id) in _player.Equipment)
            {
                if (!_world.Database.Items.TryGetValue(id, out var found))
                    continue;
                if (!CanItemBeLightSource(found))
                    continue;

                itemId = id;
                instanceId = GetEquipmentInstanceId(slot, id);
                item = found;
                return true;
            }

            for (int index = 0; index < _player.Inventory.Count; index++)
            {
                int id = _player.Inventory[index];
                if (!_world.Database.Items.TryGetValue(id, out var found))
                    continue;
                if (!CanItemBeLightSource(found))
                    continue;

                itemId = id;
                instanceId = _player.InventoryInstanceIds[index];
                item = found;
                return true;
            }

            return false;
        }

        foreach (var (slot, id) in _player.Equipment)
        {
            if (!_world.Database.Items.TryGetValue(id, out var found))
                continue;
            if (!found.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                continue;

            itemId = id;
            instanceId = GetEquipmentInstanceId(slot, id);
            item = found;
            return true;
        }

        for (int index = 0; index < _player.Inventory.Count; index++)
        {
            int id = _player.Inventory[index];
            if (!_world.Database.Items.TryGetValue(id, out var found))
                continue;
            if (!found.Name.Contains(target, StringComparison.OrdinalIgnoreCase))
                continue;

            itemId = id;
            instanceId = _player.InventoryInstanceIds[index];
            item = found;
            return true;
        }

        return false;
    }

    private bool TryFindCarriedItem(string target, out int itemId, out Item item)
    {
        return TryFindCarriedItem(target, out itemId, out item, out _);
    }

    // Resolve a carried/worn item by selector using the shared whole-word-prefix matcher
    // (TargetNameMatcher via FindMatchingCarriedItems) — NOT a naive substring scan, so "tor" finds
    // the "torch" and never matches "sTORmhammer". Returns the best single match; callers that need
    // action context (lit, lightable, …) filter the result themselves.
    private bool TryFindCarriedItem(string target, out int itemId, out Item item, out long instanceId)
    {
        itemId = 0;
        instanceId = 0;
        item = new Item();

        var matches = FindMatchingCarriedItems(target, includeEquipped: true);
        if (matches.Count == 0)
            return false;

        itemId = matches[0].ItemId;
        item = matches[0].Item;
        instanceId = matches[0].InstanceId;
        return true;
    }

    private static bool CanItemBeLightSource(Item item)
    {
        return item.ItemType == 6 || item.Abilities.ContainsKey(54);
    }

    // Ability 100 "Loyal Item" keeps the item
    // with the player through death; ability 83 "Major Curse" does
    // the same on the drop side (even though the wearer is still unequipped). Both override the
    // default "drop everything on death" behavior.
    private static bool ItemIsRetainedOnDeath(Item item)
    {
        return item.Abilities.ContainsKey(ItemLoyalAbilityId)
            || item.Abilities.ContainsKey(ItemMajorCurseAbilityId);
    }

    /// <summary>
    /// Whether a spent light source can be relit later instead of being consumed. This is ability
    /// 121 "Recharge at cleanup" — NOT 119, which is "Delete at cleanup" (see AbilityNames). We were
    /// testing 119, so every torch/lantern (all of which carry 119, none of which carry 121) was
    /// treated as rechargeable: burning one out parked a bogus recharge timer and answered every
    /// relight with "You must recharge that before you may light it again." forever, instead of the
    /// item simply being spent and destroyed.
    ///
    /// The stock data is unambiguous: of the 78 items carrying 121, NONE is item-type 6 (light), and
    /// the 8 light items carrying 119 all have RetainAfterUses = 0. So no stock light source is ever
    /// rechargeable, and that message can never arise from a burnt-out torch. (LIGHT
    /// does print it, but only for a type-6 item that a recharge flag has left at zero charges.)
    /// GameWorld.WorldTick already had both ids right (RechargeAtCleanupAbilityId /
    /// DeleteAtCleanupAbilityId); this was the one site that didn't.
    /// </summary>
    private static bool RequiresRecharge(Item item)
    {
        const int rechargeAtCleanupAbilityId = 121;
        return item.Abilities.ContainsKey(rechargeAtCleanupAbilityId);
    }

    // A readied light burns one fuel unit per 30s SLOW tick (not the fast/medium tick), and the
    // readied fuel count is the item's UseCount/10 — a torch's UseCount 800 lights as "Readied/80".
    // So total burn time is (UseCount/10) * 30s = UseCount * 3s: an 800-charge torch lasts 80*30 =
    // 2400s (40 min, matched against stock). The old code treated UseCount as raw seconds, burning
    // ~30x too fast (≈13 min for the same torch).
    private const int LightSecondsPerReadiedTick = 30;   // one 30s slow tick per displayed fuel unit

    private static TimeSpan GetLightDuration(Item item)
    {
        int readiedTicks = item.UseCount > 0 ? item.UseCount / 10 : 20;   // fallback ~20 ticks (10 min)
        int seconds = Math.Max(1, readiedTicks) * LightSecondsPerReadiedTick;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, LightSecondsPerReadiedTick, 432_000));
    }

    private TimeSpan GetRemainingOrDefaultLightDuration(long instanceId, Item item)
    {
        if (_world.TryGetItemRuntimeState(instanceId, out var runtimeState)
            && runtimeState.StoredLightRemaining > TimeSpan.Zero)
            return runtimeState.StoredLightRemaining;

        return GetLightDuration(item);
    }

    private static TimeSpan GetRechargeDuration(Item item)
    {
        int seconds = Math.Max(60, item.UseCount / 2);
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 43_200));
    }

    private bool PlayerCarriesItem(long instanceId)
    {
        EnsureItemInstanceAlignment();
        return _player.InventoryInstanceIds.Contains(instanceId) || _player.EquipmentInstanceIds.Values.Contains(instanceId);
    }

    private bool PlayerCarriesItemId(int itemId)
    {
        return _player.Inventory.Contains(itemId) || _player.Equipment.Values.Contains(itemId);
    }

    private bool TryGetSingleActiveLightSource(out int itemId, out DateTime litUntilUtc)
    {
        return TryGetSingleActiveLightSource(_player, out itemId, out litUntilUtc, out _);
    }

    private bool TryGetSingleActiveLightSource(out int itemId, out DateTime litUntilUtc, out long instanceId)
    {
        return TryGetSingleActiveLightSource(_player, out itemId, out litUntilUtc, out instanceId);
    }

    private bool TryGetSingleActiveLightSource(Player player, out int itemId, out DateTime litUntilUtc, out long instanceId)
    {
        _world.EnsurePlayerItemInstanceAlignment(player);

        itemId = 0;
        litUntilUtc = DateTime.MinValue;
        instanceId = 0;

        var now = DateTime.UtcNow;
        var active = GetOrderedEquippedItems(player)
            .Select(entry =>
            {
                if (!_world.TryGetItemRuntimeState(entry.InstanceId, out var runtimeState))
                    return (HasActive: false, ItemId: 0, InstanceId: 0L, LitUntilUtc: DateTime.MinValue);

                NormalizeLightState(entry.InstanceId, entry.Item, runtimeState, now);
                return runtimeState.ActiveLightUntilUtc > now
                    ? (HasActive: true, ItemId: entry.ItemId, InstanceId: entry.InstanceId, LitUntilUtc: runtimeState.ActiveLightUntilUtc)
                    : (HasActive: false, ItemId: 0, InstanceId: 0L, LitUntilUtc: DateTime.MinValue);
            })
            .Concat(Enumerable.Range(0, player.Inventory.Count)
                .Select(index =>
                {
                    int carriedItemId = player.Inventory[index];
                    if (!_world.Database.Items.TryGetValue(carriedItemId, out var carriedItem))
                        return (HasActive: false, ItemId: 0, InstanceId: 0L, LitUntilUtc: DateTime.MinValue);

                    long carriedInstanceId = player.InventoryInstanceIds[index];
                    if (!_world.TryGetItemRuntimeState(carriedInstanceId, out var runtimeState))
                        return (HasActive: false, ItemId: 0, InstanceId: 0L, LitUntilUtc: DateTime.MinValue);

                    NormalizeLightState(carriedInstanceId, carriedItem, runtimeState, now);
                    return runtimeState.ActiveLightUntilUtc > now
                        ? (HasActive: true, ItemId: carriedItemId, InstanceId: carriedInstanceId, LitUntilUtc: runtimeState.ActiveLightUntilUtc)
                        : (HasActive: false, ItemId: 0, InstanceId: 0L, LitUntilUtc: DateTime.MinValue);
                }))
            .Where(entry => entry.HasActive)
            .OrderBy(entry => entry.LitUntilUtc)
            .FirstOrDefault();

        if (!active.HasActive)
            return false;

        itemId = active.ItemId;
        litUntilUtc = active.LitUntilUtc;
        instanceId = active.InstanceId;
        return true;
    }

    private static int GetInventoryLightReadiedValue(DateTime litUntilUtc)
    {
        int remainingSeconds = (int)Math.Floor((litUntilUtc - DateTime.UtcNow).TotalSeconds);
        if (remainingSeconds <= 0)
            return 0;

        // Displayed fuel = whole 30s slow ticks of burn left, so a fresh 40-min torch reads
        // "Readied/80" (2400/30) and drops one unit every 30s — matching the stock slow-tick burn.
        return remainingSeconds / LightSecondsPerReadiedTick;
    }

    private bool ExtinguishLightIfActive(long instanceId, Item item)
    {
        if (!_world.TryGetItemRuntimeState(instanceId, out var runtimeState))
            return false;

        NormalizeLightState(instanceId, item, runtimeState, DateTime.UtcNow);
        if (runtimeState.ActiveLightUntilUtc == DateTime.MinValue)
            return false;

        var remaining = runtimeState.ActiveLightUntilUtc - DateTime.UtcNow;
        runtimeState.ActiveLightUntilUtc = DateTime.MinValue;
        if (remaining > TimeSpan.Zero)
            runtimeState.StoredLightRemaining = remaining;
        else
            runtimeState.StoredLightRemaining = TimeSpan.Zero;

        runtimeState.LightRechargeReadyAtUtc = DateTime.MinValue;

        return true;
    }

    private static string GetIndefiniteArticle(string itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return "a";

        char c = char.ToLowerInvariant(itemName[0]);
        return c is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a";
    }

    private void RemoveLightStateIfNotCarried(long instanceId)
    {
        if (PlayerCarriesItem(instanceId))
            return;

        // Preserve active/recharge timers across drop/get so light duration does not reset.
        // Timers are synchronized by wall-clock expiry in SynchronizeLightState().
    }

    /// <summary>Find an item by name in inventory first, then equipment.
    /// Removes it from wherever found (auto-unequip from equipment silently).
    /// Returns (itemId, wasEquipped, instanceId). Returns (0, false, 0) if not found.</summary>
    private (int ItemId, bool WasEquipped, long InstanceId) FindAndRemoveItem(string name)
    {
        // Check inventory first
        if (TryFindInventoryItem(name, out int inventoryIndex, out int itemId, out long instanceId, out _))
        {
            TryRemoveInventoryItemAt(inventoryIndex, out _, out _);
            return (itemId, false, instanceId);
        }

        // Check equipment
        foreach (var (slot, eqId) in _player.Equipment)
        {
            if (_world.Database.Items.TryGetValue(eqId, out var item) &&
                item.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                long equippedInstanceId = GetEquipmentInstanceId(slot, eqId);
                _player.Equipment.Remove(slot);
                _player.EquipmentInstanceIds.Remove(slot);
                return (eqId, true, equippedInstanceId);
            }
        }

        return (0, false, 0);
    }

}
