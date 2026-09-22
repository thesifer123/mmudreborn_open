using mmudreborn.Data.Models;
using mmudreborn.Game;
using System.Text.Json;

namespace mmudreborn.Server;

public partial class GameWorld
{
    public long CreateItemInstance(int itemId)
    {
        long instanceId = Interlocked.Increment(ref _nextItemInstanceId);
        _ = itemId;
        return instanceId;
    }

    public void EnsurePlayerItemInstanceAlignment(Player player)
    {
        while (player.InventoryInstanceIds.Count < player.Inventory.Count)
        {
            int itemId = player.Inventory[player.InventoryInstanceIds.Count];
            player.InventoryInstanceIds.Add(CreateItemInstance(itemId));
        }

        if (player.InventoryInstanceIds.Count > player.Inventory.Count)
            player.InventoryInstanceIds.RemoveRange(player.Inventory.Count, player.InventoryInstanceIds.Count - player.Inventory.Count);

        foreach (var (slot, itemId) in player.Equipment)
        {
            if (!player.EquipmentInstanceIds.ContainsKey(slot))
                player.EquipmentInstanceIds[slot] = CreateItemInstance(itemId);
        }

        var staleEquipmentSlots = player.EquipmentInstanceIds.Keys
            .Where(slot => !player.Equipment.ContainsKey(slot))
            .ToList();

        foreach (var staleSlot in staleEquipmentSlots)
            player.EquipmentInstanceIds.Remove(staleSlot);
    }

    public ItemRuntimeState GetOrCreateItemRuntimeState(long instanceId)
    {
        return _itemRuntimeStates.GetOrAdd(instanceId, static _ => new ItemRuntimeState());
    }

    public bool TryGetItemRuntimeState(long instanceId, out ItemRuntimeState runtimeState)
    {
        return _itemRuntimeStates.TryGetValue(instanceId, out runtimeState!);
    }

    public void RemoveItemRuntimeState(long instanceId)
    {
        _itemRuntimeStates.TryRemove(instanceId, out _);
    }

    private void CaptureOfflinePlayerItemRuntimeState(Player player)
    {
        EnsurePlayerItemInstanceAlignment(player);

        var snapshot = new PlayerRuntimeItemStateSnapshot
        {
            Inventory = player.Inventory
                .Zip(player.InventoryInstanceIds, static (itemId, instanceId) => (itemId, instanceId))
                .ToList(),
            Equipment = player.Equipment
                .ToDictionary(
                    entry => entry.Key,
                    entry => (entry.Value, player.EquipmentInstanceIds.TryGetValue(entry.Key, out var instanceId) ? instanceId : CreateItemInstance(entry.Value)),
                    StringComparer.OrdinalIgnoreCase),
        };

        _offlinePlayerItemStates[player.Name] = snapshot;
    }

    private void RestorePlayerItemRuntimeState(Player player)
    {
        if (!_offlinePlayerItemStates.TryRemove(player.Name, out var snapshot))
            return;

        bool inventoryMatches = snapshot.Inventory.Count == player.Inventory.Count
            && snapshot.Inventory.Select(entry => entry.ItemId).SequenceEqual(player.Inventory);

        bool equipmentMatches = snapshot.Equipment.Count == player.Equipment.Count
            && snapshot.Equipment.All(entry => player.Equipment.TryGetValue(entry.Key, out var itemId) && itemId == entry.Value.ItemId);

        if (!inventoryMatches || !equipmentMatches)
        {
            foreach (var (_, instanceId) in snapshot.Inventory)
                RemoveItemRuntimeState(instanceId);

            foreach (var (_, equipmentEntry) in snapshot.Equipment)
                RemoveItemRuntimeState(equipmentEntry.InstanceId);

            return;
        }

        player.InventoryInstanceIds = snapshot.Inventory.Select(entry => entry.InstanceId).ToList();
        player.EquipmentInstanceIds = snapshot.Equipment.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.InstanceId,
            StringComparer.OrdinalIgnoreCase);
    }

    private void LoadPersistedRoomGroundState()
    {
        lock (_groundItemLock)
        {
            _roomGroundItems.Clear();
            foreach (var row in PlayerRepo.LoadRoomGroundItems())
            {
                var key = (row.MapNumber, row.RoomNumber);
                if (!_roomGroundItems.TryGetValue(key, out var items))
                {
                    items = [];
                    _roomGroundItems[key] = items;
                }

                items.Add(new GroundItemEntry
                {
                    ItemId = row.ItemId,
                    IsHidden = row.IsHidden,
                    InstanceId = CreateItemInstance(row.ItemId),
                    IsStaticSeeded = false,
                });
            }
        }

        _initializedStaticGroundItemRooms.Clear();

        _roomGroundCurrency.Clear();
        _initializedStaticGroundCurrencyRooms.Clear();

        foreach (var row in PlayerRepo.LoadRoomGroundCurrency())
        {
            var key = (row.MapNumber, row.RoomNumber);
            var visible = DeserializeGroundCurrencyStacks(row.VisibleStacksJson, row.VisibleCopper);
            var hidden = DeserializeGroundCurrencyStacks(row.HiddenStacksJson, row.HiddenCopper);
            if (!visible.IsEmpty || !hidden.IsEmpty)
                _roomGroundCurrency[key] = (visible, hidden);

            if (row.StaticInitialized)
                _initializedStaticGroundCurrencyRooms[key] = 0;
        }
    }

    private void PersistRoomGroundState()
    {
        var itemRows = new List<(int MapNumber, int RoomNumber, int Sequence, int ItemId, bool IsHidden)>();
        lock (_groundItemLock)
        {
            foreach (var kvp in _roomGroundItems)
            {
                for (int index = 0; index < kvp.Value.Count; index++)
                {
                    var entry = kvp.Value[index];
                    if (entry.IsStaticSeeded || !ShouldPersistGroundItem(entry.ItemId))
                        continue;

                    itemRows.Add((kvp.Key.Map, kvp.Key.Room, index, entry.ItemId, entry.IsHidden));
                }
            }
        }

        var currencyKeys = new HashSet<(int Map, int Room)>(_roomGroundCurrency.Keys);
        foreach (var key in _initializedStaticGroundCurrencyRooms.Keys)
            currencyKeys.Add(key);

        var currencyRows = new List<(int MapNumber, int RoomNumber, long VisibleCopper, long HiddenCopper, string VisibleStacksJson, string HiddenStacksJson, bool StaticInitialized)>();
        foreach (var key in currencyKeys)
        {
            _roomGroundCurrency.TryGetValue(key, out var currency);
            bool staticInitialized = _initializedStaticGroundCurrencyRooms.ContainsKey(key);
            if (currency.Visible.IsEmpty && currency.Hidden.IsEmpty && !staticInitialized)
                continue;

            currencyRows.Add((
                key.Map,
                key.Room,
                currency.Visible.TotalCopper,
                currency.Hidden.TotalCopper,
                SerializeGroundCurrencyStacks(currency.Visible),
                SerializeGroundCurrencyStacks(currency.Hidden),
                staticInitialized));
        }

        PlayerRepo.SaveRoomGroundState(itemRows, currencyRows);
    }

    private static string SerializeGroundCurrencyStacks(GroundCurrencyStacks stacks)
    {
        return stacks.IsEmpty ? string.Empty : JsonSerializer.Serialize(stacks.ToSnapshot());
    }

    private static GroundCurrencyStacks DeserializeGroundCurrencyStacks(string? json, long legacyCopperTotal)
    {
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return GroundCurrencyStacks.FromSnapshot(JsonSerializer.Deserialize<GroundCurrencySnapshot>(json));
            }
            catch (JsonException)
            {
                // Fall back to the legacy copper-total columns when older or malformed data is encountered.
            }
        }

        return GroundCurrencyStacks.FromCopperNormalized(legacyCopperTotal);
    }

    private bool ShouldPersistGroundItem(int itemId)
    {
        return Database.Items.TryGetValue(itemId, out var item)
            && item.Name.IndexOf("del@Maint", StringComparison.OrdinalIgnoreCase) < 0;
    }

    // ── Per-room ground capacity ────────────────────────────
    // The room struct holds two FIXED-SIZE parallel arrays, picked by the function's hidden flag:
    //   visible: 17 slots, each with its own charge and stack-count entry
    //   hidden:  15 slots, likewise
    // The record layout self-checks: each array ends exactly where the next begins, which is how the
    // 17/15 slot counts were confirmed.
    // When no slot is free the add fails and the caller undoes the move (DROP restores the
    // item to inventory and prints "There is no room to drop %s here!"; HIDE the hide equivalent).
    public const int StockVisibleGroundSlots = 17;
    public const int StockHiddenGroundSlots = 15;

    /// <summary>
    /// SYSOP CONFIGURE GROUNDLIMIT. ON (default, stock) enforces the 17 visible / 15 hidden
    /// per-room slot caps; OFF makes a room's ground hold unlimited entries (our pre-2026-08-16
    /// behaviour). Stacking is applied either way — it is how stock counts slots, not a limit.
    /// </summary>
    public bool GroundItemLimitEnabled { get; set; } = true;

    // Stacking rule: a room add folds the new item onto an EXISTING slot holding the same
    // item id when its charge parameter is -1 (the "no specific charge value" case), bumping the 16-bit
    // stack counter instead of consuming a slot. An item carrying a real charge value never
    // stacks and always takes its own slot. Our per-instance entries model that same accounting: an
    // entry with no RemainingCharges is a stackable copy, one with an explicit value is not.
    private bool GroundEntryHasExplicitCharges(long instanceId)
        => TryGetItemRuntimeState(instanceId, out var runtimeState) && runtimeState.RemainingCharges.HasValue;

    /// <summary>Slots a pile occupies under the stock accounting (stacked copies share one slot).</summary>
    private int CountGroundSlotsUsed(List<GroundItemEntry> entries, bool hidden)
    {
        var stackedItemIds = new HashSet<int>();
        int slots = 0;
        foreach (var entry in entries)
        {
            if (entry.IsHidden != hidden)
                continue;
            if (GroundEntryHasExplicitCharges(entry.InstanceId))
                slots++;                              // charge-bearing: never stacks
            else if (stackedItemIds.Add(entry.ItemId))
                slots++;                              // first copy of this id claims the slot
        }
        return slots;
    }

    /// <summary>
    /// True when the pile can accept this item — either it stacks onto a slot it already occupies, or a
    /// free slot remains. `reservedSlots` counts slots held outside this pile (a room's static hidden
    /// items, which are never materialized into it). Caller must hold _groundItemLock.
    /// </summary>
    private bool HasGroundSlotFor(List<GroundItemEntry> entries, int itemId, long instanceId, bool hidden, int reservedSlots = 0)
    {
        if (!GroundItemLimitEnabled)
            return true;

        // A stackable copy of an id already lying here needs no new slot.
        if (!GroundEntryHasExplicitCharges(instanceId)
            && entries.Any(e => e.IsHidden == hidden
                                && e.ItemId == itemId
                                && !GroundEntryHasExplicitCharges(e.InstanceId)))
        {
            return true;
        }

        int cap = hidden ? StockHiddenGroundSlots : StockVisibleGroundSlots;
        return CountGroundSlotsUsed(entries, hidden) + reservedSlots < cap;
    }

    private bool CanMaterializeGroundItem(int itemId)
    {
        if (LimitedItemsMode == 0)
            return true;

        if (!Database.Items.TryGetValue(itemId, out var item) || item.Limit <= 0)
            return true;

        return CountMaterializedGroundItemCopies(itemId) < item.Limit;
    }

    private int CountMaterializedGroundItemCopies(int itemId)
    {
        int count = 0;

        lock (_groundItemLock)
        {
            foreach (var entries in _roomGroundItems.Values)
                count += entries.Count(entry => entry.ItemId == itemId);
        }

        if (!Database.Items.TryGetValue(itemId, out var item) || !item.Gettable)
            return count;

        foreach (var room in Database.Rooms.Values)
        {
            var key = (room.MapNumber, room.RoomNumber);
            if (_initializedStaticGroundItemRooms.ContainsKey(key))
                continue;

            count += room.GetPlacedItemIds().Count(placedItemId => placedItemId == itemId);
        }

        return count;
    }

    private void EnsureStaticGroundItemsInitialized(int mapNumber, int roomNumber)
    {
        var room = GetRoom(mapNumber, roomNumber);
        if (room == null)
            return;

        var key = (mapNumber, roomNumber);
        if (!_initializedStaticGroundItemRooms.TryAdd(key, 0))
            return;

        var seededEntries = room.GetPlacedItemIds()
            .Where(itemId =>
                Database.Items.TryGetValue(itemId, out var item) &&
                item.Gettable &&
                CanMaterializeGroundItem(itemId))
            .Select(itemId => new GroundItemEntry
            {
                ItemId = itemId,
                IsHidden = false,
                InstanceId = CreateItemInstance(itemId),
                IsStaticSeeded = true,
            })
            .ToList();

        if (seededEntries.Count == 0)
            return;

        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items))
            {
                items = [];
                _roomGroundItems[key] = items;
            }

            items.InsertRange(0, seededEntries);
        }
    }

    /// <summary>
    /// Bug #228: re-place a room's static Placed items that are no longer in the room.
    ///
    /// The world-init pass:
    /// for each of the room record's 10 static placed slots it scans the 17 live visible
    /// slots and the 15 hidden slots, and when the item is in NEITHER it adds it to the room
    /// with the item's default charge count. Verified against the shipped
    /// data: the Royal Treasure Vault record (map 8 / room 707) carries
    /// 1727,910,909,1834 in its static array and the same four with charges 1,1,1,1 in the live one.
    /// The pass is skipped only by one boot flag, which is set solely for a
    /// sysop-requested `reinitialize`, so an ordinary boot always restores.
    ///
    /// We already do the equivalent at process start (static entries are never persisted and
    /// _initializedStaticGroundItemRooms is cleared on load, so the first visit re-seeds). The gap this
    /// closes is CADENCE: a stock board re-initialized the game at every nightly event, while ours stays up
    /// for days — so the dark elf vault's chests, opened once, never came back. That the two chests carrying
    /// ability 119 "Delete at cleanup" (iron-banded chest #909, silver casket #1834) are deleted by the
    /// maintenance sweep is the tell: stock would not mark a room's own placed item Del@Maint unless the
    /// re-init reliably put it back, and on a real board cleanup and boot happened back to back.
    ///
    /// Non-gettable placed items (the gang-house banners, scenery) are not ground state here — they render
    /// straight from room.Placed — so this only re-seeds the gettable ones, exactly like the initial seed.
    /// Their logical-removal ledger is cleared instead, which is what a restart does to it.
    /// </summary>
    public int RestoreStaticPlacedGroundItems()
    {
        int restored = 0;

        foreach (var room in Database.Rooms.Values)
        {
            var placedIds = room.GetPlacedItemIds();
            if (placedIds.Count == 0)
                continue;

            // A room nobody has visited since boot has no ground state yet: seed it the normal way (which
            // also marks it initialized) so the per-item pass below can't double up on it later.
            EnsureStaticGroundItemsInitialized(room.MapNumber, room.RoomNumber);

            var key = (room.MapNumber, room.RoomNumber);

            foreach (int itemId in placedIds)
            {
                if (!Database.Items.TryGetValue(itemId, out var item) || !item.Gettable)
                    continue;

                lock (_groundItemLock)
                {
                    // Present in EITHER array (visible or hidden) means leave it alone.
                    if (_roomGroundItems.TryGetValue(key, out var existing)
                        && existing.Exists(entry => entry.ItemId == itemId))
                    {
                        continue;
                    }
                }

                if (!CanMaterializeGroundItem(itemId))
                    continue;

                lock (_groundItemLock)
                {
                    if (!_roomGroundItems.TryGetValue(key, out var items))
                    {
                        items = [];
                        _roomGroundItems[key] = items;
                    }

                    items.Insert(0, new GroundItemEntry
                    {
                        ItemId = itemId,
                        IsHidden = false,
                        InstanceId = CreateItemInstance(itemId),
                        IsStaticSeeded = true,
                    });
                }

                restored++;
            }
        }

        _removedPlacedItems.Clear();

        return restored;
    }

    private void EnsureStaticGroundCurrencyInitialized(int mapNumber, int roomNumber)
    {
        var room = GetRoom(mapNumber, roomNumber);
        if (room == null || room.GroundCurrency <= 0)
            return;

        var key = (mapNumber, roomNumber);
        if (!_initializedStaticGroundCurrencyRooms.TryAdd(key, 0))
            return;

        _roomGroundCurrency.AddOrUpdate(
            key,
            (GroundCurrencyStacks.FromCopperNormalized(room.GroundCurrency), GroundCurrencyStacks.Empty),
            (_, current) => (current.Visible.Add(GroundCurrencyStacks.FromCopperNormalized(room.GroundCurrency)), current.Hidden));
    }

    // ── Overflow-tolerant placement ──────────────────
    // Loot never "poofs" just because the room underneath it is full. Stock spreads it outward:
    //   1. try the room itself (twice, as stock does);
    //   2. failing that, recurse into each of the room's 10 exits, SKIPPING exit types 12 and 8 —
    //      RemoteAction (speech-triggered, not walkable) and the addon/purchase gate — so loot only
    //      travels through exits a player could actually follow;
    //   3. bail out past a global recursion depth of 5.
    // Returns true once the item has come to rest somewhere.
    private const int GroundSpillMaxDepth = 5;

    public bool DisposeOfItemInRoom(int mapNumber, int roomNumber, int itemId, long? instanceId = null, int uses = 0)
        => DisposeOfItemInRoom(mapNumber, roomNumber, itemId, instanceId, uses, depth: 0);

    private bool DisposeOfItemInRoom(int mapNumber, int roomNumber, int itemId, long? instanceId, int uses, int depth)
    {
        if (depth > GroundSpillMaxDepth)
            return false;

        var room = GetRoom(mapNumber, roomNumber);
        if (room == null)
            return false;

        if (DropItemInRoom(mapNumber, roomNumber, itemId, instanceId, uses))
            return true;

        foreach (var exit in room.GetExitDefinitions().Values)
        {
            if (!exit.HasDestination)
                continue;
            // The two exit types stock refuses to push loot through.
            if (exit.ExitType is RoomExitType.RemoteAction or RoomExitType.ChangeMap)
                continue;

            if (DisposeOfItemInRoom(exit.TargetMap, exit.TargetRoom, itemId, instanceId, uses, depth + 1))
                return true;
        }

        return false;
    }

    // Walk the dying player's movement trail (20 rooms) and try each in turn —
    // VISIBLE first, then HIDDEN, which is the
    // one place stock will stuff loot into the hidden pile to avoid losing it.
    public bool DisposeOfItemInTrail(Player player, int itemId, long? instanceId = null, int uses = 0)
    {
        var trail = player.MovementTrail;
        if (trail == null)
            return false;

        for (int i = 0; i < trail.Count; i++)
        {
            var (map, room) = trail[i];
            if (GetRoom(map, room) == null)
                continue;
            if (DropItemInRoom(map, room, itemId, instanceId, uses))
                return true;
            if (HideItemInRoom(map, room, itemId, instanceId))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The full stock death drop chain for one item off a corpse:
    ///   1. the death room (recursive spill into adjacent rooms)
    ///   2. the victim's 20-room movement trail
    ///   3. the overflow room (stock: map 164 room 1)
    /// Only when all three fail does stock give up and print "Your %s has returned to its rightful
    /// place!". Returns false in exactly that case so the caller can emit the line.
    ///
    /// Stock's overflow room lives on map 164, which our imported room set does not contain, so tier 3
    /// resolves to nothing here and is skipped rather than faked. Tiers 1 and 2 cover a 5-deep spill
    /// plus 20 trail rooms, so reaching tier 3 at all is vanishingly rare.
    /// </summary>
    public const int StockOverflowMap = 164;
    public const int StockOverflowRoom = 1;

    public bool DisposeOfCorpseItem(Player victim, int mapNumber, int roomNumber, int itemId, long? instanceId = null, int uses = 0)
    {
        if (DisposeOfItemInRoom(mapNumber, roomNumber, itemId, instanceId, uses))
            return true;

        if (DisposeOfItemInTrail(victim, itemId, instanceId, uses))
            return true;

        if (GetRoom(StockOverflowMap, StockOverflowRoom) != null
            && DisposeOfItemInRoom(StockOverflowMap, StockOverflowRoom, itemId, instanceId, uses))
        {
            return true;
        }

        return false;
    }

    /// <summary>Place an item on a room's visible ground. False when the room's slots are full.</summary>
    public bool DropItemInRoom(int mapNumber, int roomNumber, int itemId, long? instanceId = null, int uses = 0)
    {
        if (!CanMaterializeGroundItem(itemId))
            return false;

        long resolvedInstanceId = instanceId ?? CreateItemInstance(itemId);

        // A monster-specified DropUses (>0) seeds the dropped item's
        // uses/charges, overriding the item's default UseCount. uses==0 leaves RemainingCharges null,
        // which the use/display paths resolve to the item default (the stackable/default case).
        if (uses > 0)
            GetOrCreateItemRuntimeState(resolvedInstanceId).RemainingCharges = uses;

        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.ContainsKey(key))
                _roomGroundItems[key] = [];

            // The room add fails when every slot is taken; the caller undoes the move.
            if (!HasGroundSlotFor(_roomGroundItems[key], itemId, resolvedInstanceId, hidden: false))
                return false;

            _roomGroundItems[key].Add(new GroundItemEntry
            {
                ItemId = itemId,
                IsHidden = false,
                InstanceId = resolvedInstanceId,
                IsStaticSeeded = false,
            });
        }

        return true;
    }

    public bool HideItemInRoom(int mapNumber, int roomNumber, int itemId, long? instanceId = null, int reservedSlots = 0)
    {
        if (!CanMaterializeGroundItem(itemId))
            return false;

        long resolvedInstanceId = instanceId ?? CreateItemInstance(itemId);

        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.ContainsKey(key))
                _roomGroundItems[key] = [];

            if (!HasGroundSlotFor(_roomGroundItems[key], itemId, resolvedInstanceId, hidden: true, reservedSlots))
                return false;

            _roomGroundItems[key].Add(new GroundItemEntry
            {
                ItemId = itemId,
                IsHidden = true,
                InstanceId = resolvedInstanceId,
                IsStaticSeeded = false,
            });
        }

        return true;
    }

    public List<(int ItemId, int Index)> GetVisibleGroundItems(int mapNumber, int roomNumber)
    {
        EnsureStaticGroundItemsInitialized(mapNumber, roomNumber);

        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return [];
            var result = new List<(int, int)>();
            for (int index = 0; index < items.Count; index++)
            {
                bool forcedVisible = Database.Items.TryGetValue(items[index].ItemId, out var item)
                    && item.Abilities.ContainsKey(138);
                if (!items[index].IsHidden || forcedVisible)
                    result.Add((items[index].ItemId, index));
            }
            return result;
        }
    }

    public List<(int ItemId, int Index)> GetHiddenGroundItems(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return [];
            var result = new List<(int, int)>();
            for (int index = 0; index < items.Count; index++)
            {
                if (items[index].IsHidden)
                    result.Add((items[index].ItemId, index));
            }
            return result;
        }
    }

    // Hidden (player-stashed) ground items paired with their instance ids, so the searcher can flag
    // exactly which instances it uncovered (a later `get` is gated on that per-player reveal set).
    public List<(int ItemId, int Index, long InstanceId)> GetHiddenGroundItemsWithInstance(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return [];
            var result = new List<(int, int, long)>();
            for (int index = 0; index < items.Count; index++)
            {
                if (items[index].IsHidden)
                    result.Add((items[index].ItemId, index, items[index].InstanceId));
            }
            return result;
        }
    }

    // Find a gettable ground item by name — the stock `get` lookup. It takes the FIRST
    // item whose name word-prefix matches — no exact-name preference and no ambiguity prompt —
    // scanning the VISIBLE items first (forced-visible ability-138 items count as visible), then the hidden
    // ones. A hidden item matches only when its instance is in the searcher's reveal set: you cannot grab a
    // stash you have not searched up, even by naming it. A non-gettable item is skipped and the scan moves on.
    // (Stock also lets an ability-181 item match ANY typed name through an operator-precedence slip; that is
    // not reproduced — "get sword" must never pick up a gang-house deed.)
    public (int ItemId, int Index) FindGettableGroundItemByName(int mapNumber, int roomNumber, string name, IReadOnlySet<long> revealedHiddenInstanceIds)
    {
        EnsureStaticGroundItemsInitialized(mapNumber, roomNumber);

        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return (0, -1);
            foreach (bool hiddenPass in new[] { false, true })
            {
                for (int index = 0; index < items.Count; index++)
                {
                    GroundItemEntry entry = items[index];
                    if (!Database.Items.TryGetValue(entry.ItemId, out var item))
                        continue;

                    bool listedVisible = !entry.IsHidden || item.Abilities.ContainsKey(138);
                    if (listedVisible == hiddenPass)
                        continue;
                    if (hiddenPass && !revealedHiddenInstanceIds.Contains(entry.InstanceId))
                        continue;

                    if (item.Gettable && TargetNameMatcher.MatchesWordPrefix(item.Name, name))
                        return (entry.ItemId, index);
                }
            }
            return (0, -1);
        }
    }

    public bool RemoveGroundItemById(int mapNumber, int roomNumber, int itemId)
    {
        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return false;
            int index = items.FindIndex(entry => entry.ItemId == itemId);
            if (index < 0) return false;
            items.RemoveAt(index);
            if (items.Count == 0) _roomGroundItems.TryRemove(key, out _);
            return true;
        }
    }

    // ---- Logical room-item layer ----------------------------------
    // A non-gettable visible-placed item (ability 138, e.g. the giant apparatus 819) is drawn from the
    // room's static Placed field and has no dynamic ground entry. These let clearitem remove such an
    // item, roomitem gate on its presence, and the renderer skip it once removed.

    public bool IsPlacedItemRemoved(int mapNumber, int roomNumber, int itemId)
        => _removedPlacedItems.TryGetValue((mapNumber, roomNumber), out var set) && set.Contains(itemId);

    private void MarkPlacedItemRemoved(int mapNumber, int roomNumber, int itemId)
    {
        var set = _removedPlacedItems.GetOrAdd((mapNumber, roomNumber), _ => new HashSet<int>());
        lock (set)
            set.Add(itemId);
    }

    // A NON-gettable static placed item present in the room's Placed field. Gettable placed items are
    // deliberately excluded: they are seeded into dynamic ground state, so their presence is decided
    // there (a picked-up gettable placed item is gone, even though the static field still lists it).
    private bool RoomHasNonGettableStaticPlacedItem(int mapNumber, int roomNumber, int itemId)
    {
        var room = GetRoom(mapNumber, roomNumber);
        return room != null
            && room.GetPlacedItemIds().Contains(itemId)
            && Database.Items.TryGetValue(itemId, out var item)
            && !item.Gettable;
    }

    /// <summary>The roomitem gate's view of the room's logical items: a dynamic ground entry
    /// (visible or hidden — gettable placed items are seeded here) OR a static placed item that hasn't
    /// been logically removed.</summary>
    public bool RoomHasItem(int mapNumber, int roomNumber, int itemId)
    {
        EnsureStaticGroundItemsInitialized(mapNumber, roomNumber);
        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (_roomGroundItems.TryGetValue(key, out var items) && items.Exists(e => e.ItemId == itemId))
                return true;
        }
        return RoomHasNonGettableStaticPlacedItem(mapNumber, roomNumber, itemId)
            && !IsPlacedItemRemoved(mapNumber, roomNumber, itemId);
    }

    /// <summary>clearitem: remove one of <paramref name="itemId"/> from the room. Removes a dynamic
    /// ground entry if present (this also covers gettable placed items, which are seeded into ground
    /// state); otherwise logically removes a non-gettable static placed item so the renderer drops it.
    /// Returns true if anything was cleared.</summary>
    public bool ClearRoomItem(int mapNumber, int roomNumber, int itemId)
    {
        EnsureStaticGroundItemsInitialized(mapNumber, roomNumber);
        if (RemoveGroundItemById(mapNumber, roomNumber, itemId))
            return true;
        if (RoomHasNonGettableStaticPlacedItem(mapNumber, roomNumber, itemId)
            && !IsPlacedItemRemoved(mapNumber, roomNumber, itemId))
        {
            MarkPlacedItemRemoved(mapNumber, roomNumber, itemId);
            return true;
        }
        return false;
    }

    /// <summary>clearitem 0: the rubbish-disposal "flush everything" sentinel (stock uses item id 0 only
    /// in the disposal-lever scripts). Removes every dynamic ground entry — dropped items and seeded
    /// gettable placed loot, hidden or not — from the room. Static non-gettable placed decorations (the
    /// slag sign, etc.) are room scenery, not floor rubbish, so they are left in place. Returns the count
    /// removed. The periodic PersistRoomGroundState snapshot syncs the DB, same as single-item clears.</summary>
    public int ClearAllRoomItems(int mapNumber, int roomNumber)
    {
        EnsureStaticGroundItemsInitialized(mapNumber, roomNumber);
        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items) || items.Count == 0)
                return 0;
            int removed = items.Count;
            _roomGroundItems.TryRemove(key, out _);
            return removed;
        }
    }

    public int PickUpGroundItem(int mapNumber, int roomNumber, int index)
    {
        return PickUpGroundItemWithInstance(mapNumber, roomNumber, index).ItemId;
    }

    public (int ItemId, long InstanceId) PickUpGroundItemWithInstance(int mapNumber, int roomNumber, int index)
    {
        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return (0, 0);
            if (index < 0 || index >= items.Count) return (0, 0);
            GroundItemEntry entry = items[index];
            items.RemoveAt(index);
            if (items.Count == 0) _roomGroundItems.TryRemove(key, out _);
            return (entry.ItemId, entry.InstanceId);
        }
    }

    public (int ItemId, int Index) FindGroundItemByName(int mapNumber, int roomNumber, string name, bool includeHidden)
    {
        EnsureStaticGroundItemsInitialized(mapNumber, roomNumber);

        var key = (mapNumber, roomNumber);
        lock (_groundItemLock)
        {
            if (!_roomGroundItems.TryGetValue(key, out var items)) return (0, -1);
            // Stock room-item lookup: word-prefix match; an exact full name wins outright,
            // otherwise the first match in room order.
            (int ItemId, int Index) firstLoose = (0, -1);
            for (int index = 0; index < items.Count; index++)
            {
                if (items[index].IsHidden && !includeHidden) continue;
                if (!Database.Items.TryGetValue(items[index].ItemId, out var item))
                    continue;

                var rank = TargetNameMatcher.GetMatchRank(item.Name, name);
                if (rank == TargetNameMatcher.MatchRank.Exact)
                    return (items[index].ItemId, index);
                if (rank != TargetNameMatcher.MatchRank.None && firstLoose.Index < 0)
                    firstLoose = (items[index].ItemId, index);
            }
            return firstLoose;
        }
    }

    private void AddGroundCurrency(int mapNumber, int roomNumber, GroundCurrencyStacks visibleToAdd, GroundCurrencyStacks hiddenToAdd)
    {
        if (visibleToAdd.IsEmpty && hiddenToAdd.IsEmpty)
            return;

        var key = (mapNumber, roomNumber);
        _roomGroundCurrency.AddOrUpdate(
            key,
            (visibleToAdd, hiddenToAdd),
            (_, cur) => (cur.Visible.Add(visibleToAdd), cur.Hidden.Add(hiddenToAdd)));
    }

    public void DropCurrencyInRoom(int mapNumber, int roomNumber, long copperAmount)
    {
        AddGroundCurrency(mapNumber, roomNumber, GroundCurrencyStacks.FromCopperNormalized(copperAmount), GroundCurrencyStacks.Empty);
    }

    public void DropCurrencyInRoom(int mapNumber, int roomNumber, long runic, long platinum, long gold, long silver, long copper)
    {
        AddGroundCurrency(mapNumber, roomNumber, new GroundCurrencyStacks(runic, platinum, gold, silver, copper), GroundCurrencyStacks.Empty);
    }

    public void HideCurrencyInRoom(int mapNumber, int roomNumber, long copperAmount)
    {
        AddGroundCurrency(mapNumber, roomNumber, GroundCurrencyStacks.Empty, GroundCurrencyStacks.FromCopperNormalized(copperAmount));
    }

    public void HideCurrencyInRoom(int mapNumber, int roomNumber, long runic, long platinum, long gold, long silver, long copper)
    {
        AddGroundCurrency(mapNumber, roomNumber, GroundCurrencyStacks.Empty, new GroundCurrencyStacks(runic, platinum, gold, silver, copper));
    }

    public long GetVisibleGroundCurrency(int mapNumber, int roomNumber)
    {
        EnsureStaticGroundCurrencyInitialized(mapNumber, roomNumber);
        return _roomGroundCurrency.TryGetValue((mapNumber, roomNumber), out var cur) ? cur.Visible.TotalCopper : 0;
    }

    public long GetHiddenGroundCurrency(int mapNumber, int roomNumber)
    {
        return _roomGroundCurrency.TryGetValue((mapNumber, roomNumber), out var cur) ? cur.Hidden.TotalCopper : 0;
    }

    public IReadOnlyList<string> GetVisibleGroundCurrencyParts(int mapNumber, int roomNumber)
    {
        EnsureStaticGroundCurrencyInitialized(mapNumber, roomNumber);
        return _roomGroundCurrency.TryGetValue((mapNumber, roomNumber), out var cur) ? cur.Visible.ToDisplayParts() : [];
    }

    public IReadOnlyList<string> GetHiddenGroundCurrencyParts(int mapNumber, int roomNumber)
    {
        return _roomGroundCurrency.TryGetValue((mapNumber, roomNumber), out var cur) ? cur.Hidden.ToDisplayParts() : [];
    }

    public long GetVisibleGroundCurrencyDenominationCount(int mapNumber, int roomNumber, long denominationMultiplier)
    {
        EnsureStaticGroundCurrencyInitialized(mapNumber, roomNumber);
        return _roomGroundCurrency.TryGetValue((mapNumber, roomNumber), out var cur)
            ? cur.Visible.GetDenominationCount(denominationMultiplier)
            : 0;
    }

    public long GetHiddenGroundCurrencyDenominationCount(int mapNumber, int roomNumber, long denominationMultiplier)
    {
        return _roomGroundCurrency.TryGetValue((mapNumber, roomNumber), out var cur)
            ? cur.Hidden.GetDenominationCount(denominationMultiplier)
            : 0;
    }

    public long RevealHiddenGroundCurrency(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);

        while (true)
        {
            if (!_roomGroundCurrency.TryGetValue(key, out var cur) || cur.Hidden.IsEmpty)
                return 0;

            var updated = (cur.Visible.Add(cur.Hidden), GroundCurrencyStacks.Empty);
            if (_roomGroundCurrency.TryUpdate(key, updated, cur))
                return cur.Hidden.TotalCopper;
        }
    }

    public (long Runic, long Platinum, long Gold, long Silver, long Copper) PickUpGroundCurrency(int mapNumber, int roomNumber)
    {
        var key = (mapNumber, roomNumber);
        EnsureStaticGroundCurrencyInitialized(mapNumber, roomNumber);
        if (_roomGroundCurrency.TryGetValue(key, out var cur) && !cur.Visible.IsEmpty)
        {
            var picked = cur.Visible;
            _roomGroundCurrency.AddOrUpdate(key,
                (GroundCurrencyStacks.Empty, GroundCurrencyStacks.Empty),
                (_, current) => (GroundCurrencyStacks.Empty, current.Hidden));
            if (_roomGroundCurrency.TryGetValue(key, out var after) && after.Visible.IsEmpty && after.Hidden.IsEmpty)
                _roomGroundCurrency.TryRemove(key, out _);
            return (picked.Runic, picked.Platinum, picked.Gold, picked.Silver, picked.Copper);
        }
        return default;
    }

    public long PickUpGroundCurrencyDenomination(int mapNumber, int roomNumber, long denominationMultiplier, long count)
    {
        var key = (mapNumber, roomNumber);
        EnsureStaticGroundCurrencyInitialized(mapNumber, roomNumber);
        if (_roomGroundCurrency.TryGetValue(key, out var cur) && !cur.Visible.IsEmpty)
        {
            GroundCurrencyStacks updatedVisible = cur.Visible.RemoveDenomination(denominationMultiplier, count, out var removed);
            long picked = removed.TotalCopper;
            if (picked <= 0)
                return 0;

            _roomGroundCurrency.AddOrUpdate(key,
                (GroundCurrencyStacks.Empty, GroundCurrencyStacks.Empty),
                (_, current) => (updatedVisible, current.Hidden));
            if (_roomGroundCurrency.TryGetValue(key, out var after) && after.Visible.IsEmpty && after.Hidden.IsEmpty)
                _roomGroundCurrency.TryRemove(key, out _);
            return picked;
        }
        return 0;
    }

    public long PickUpHiddenGroundCurrencyDenomination(int mapNumber, int roomNumber, long denominationMultiplier, long count)
    {
        var key = (mapNumber, roomNumber);
        if (_roomGroundCurrency.TryGetValue(key, out var cur) && !cur.Hidden.IsEmpty)
        {
            GroundCurrencyStacks updatedHidden = cur.Hidden.RemoveDenomination(denominationMultiplier, count, out var removed);
            long picked = removed.TotalCopper;
            if (picked <= 0)
                return 0;

            _roomGroundCurrency.AddOrUpdate(key,
                (GroundCurrencyStacks.Empty, GroundCurrencyStacks.Empty),
                (_, current) => (current.Visible, updatedHidden));
            if (_roomGroundCurrency.TryGetValue(key, out var after) && after.Visible.IsEmpty && after.Hidden.IsEmpty)
                _roomGroundCurrency.TryRemove(key, out _);
            return picked;
        }
        return 0;
    }
}