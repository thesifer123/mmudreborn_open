using System;
using System.Collections.Generic;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

// Gang-owned shops (shop type 11). Unlike normal shops — whose stock is static game data reset
// each boot — a gang shop is stocked by its owners with items from their own inventory (STOCK),
// emptied with UNSTOCK, and priced with a per-shop markup (MARKUP). Each shop has up to ten
// slots (parallel item / qty / price / currency arrays, ten entries) plus
// a shop markup. Buying from a gang shop deposits the takings into the owning gang leader's
// bank account (bankbook 8). All of this mutable state is persisted
// per shop (stock saves the whole shop record on a dirty flag); we mirror that with a row
// per shop in the GangShops table.
public partial class GameWorld
{
    // Stock caps: ten distinct slots, twenty of each item, markup 0..1000, price 0..9999.
    public const int GangShopSlotCount = 10;
    public const int GangShopSlotMaxQuantity = 20;
    public const int GangShopMaxMarkupPercent = 1000;
    public const int GangShopMaxSlotPrice = 9999;
    public const int GangShopControllerAbilityId = 184;   // value == room GangHouse#
    // Gang-shop takings go to the leader's bankbook index 8 ("Bank of Godfrey").
    public const int GangLeaderBankNumber = 8;

    public sealed class GangShopSlot
    {
        public int ItemId { get; set; }
        public int Quantity { get; set; }
        /// <summary>Owner-set base cost. 0 ⇒ free.</summary>
        public int Price { get; set; }
        /// <summary>Currency index 0..4 (copper/silver/gold/platinum/runic).</summary>
        public int Currency { get; set; }
    }

    public sealed class GangShopState
    {
        public int ShopId { get; set; }
        public int MarkupPercent { get; set; }
        public GangShopSlot?[] Slots { get; } = new GangShopSlot?[GangShopSlotCount];
    }

    private readonly object _gangShopLock = new();
    private readonly Dictionary<int, GangShopState> _gangShops = new();
    // shopId -> houseId (1..10), built from the room that hosts each gang shop.
    private readonly Dictionary<int, int> _gangShopHouse = new();

    private void LoadGangShops()
    {
        lock (_gangShopLock)
        {
            _gangShops.Clear();
            _gangShopHouse.Clear();

            // Map every gang shop (type 11) to the house its room belongs to (room GangHouse#).
            foreach (var room in Database.Rooms.Values)
            {
                if (room.Shop <= 0 || room.GangHouseId < 1 || room.GangHouseId > 10)
                    continue;
                if (Database.Shops.TryGetValue(room.Shop, out var shop) && shop.ShopType == GangShopType)
                    _gangShopHouse[room.Shop] = room.GangHouseId;
            }

            foreach (var rec in PlayerRepo.LoadGangShops())
                _gangShops[rec.ShopId] = DeserializeGangShop(rec);
        }
    }

    public bool IsGangShop(int shopId)
        => Database.Shops.TryGetValue(shopId, out var shop) && shop.ShopType == GangShopType;

    /// <summary>House id (1..10) that owns/contains the given gang shop, or 0 if none.</summary>
    public int GetGangShopHouseId(int shopId)
    {
        lock (_gangShopLock)
            return _gangShopHouse.TryGetValue(shopId, out var houseId) ? houseId : 0;
    }

    /// <summary>True when the player carries the controller item for this house (an inventory
    /// item with ability 184 whose value equals the shop room's GangHouse#). The gang-house deed
    /// carries ability 184 == its house id, so keeping the deed grants shop control.</summary>
    public bool PlayerControlsGangShop(Player player, int houseId)
    {
        if (houseId <= 0)
            return false;
        foreach (var itemId in player.Inventory)
        {
            if (Database.Items.TryGetValue(itemId, out var item) &&
                item.Abilities.GetValueOrDefault(GangShopControllerAbilityId) == houseId)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A read-only snapshot of a gang shop's stocked slots (for LIST/BUY display), in slot
    /// order, excluding empty slots.</summary>
    public IReadOnlyList<GangShopSlot> SnapshotGangShopSlots(int shopId)
    {
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
                return Array.Empty<GangShopSlot>();
            var copy = new List<GangShopSlot>();
            foreach (var slot in state.Slots)
            {
                if (slot != null && slot.ItemId > 0 && slot.Quantity > 0)
                    copy.Add(new GangShopSlot { ItemId = slot.ItemId, Quantity = slot.Quantity, Price = slot.Price, Currency = slot.Currency });
            }
            return copy;
        }
    }

    public int GetGangShopMarkup(int shopId)
    {
        lock (_gangShopLock)
            return _gangShops.TryGetValue(shopId, out var state) ? state.MarkupPercent : 0;
    }

    public void SetGangShopMarkup(int shopId, int markupPercent)
    {
        markupPercent = Math.Clamp(markupPercent, 0, GangShopMaxMarkupPercent);
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
            {
                state = new GangShopState { ShopId = shopId };
                _gangShops[shopId] = state;
            }
            state.MarkupPercent = markupPercent;
        }
    }

    /// <summary>Add one of <paramref name="itemId"/> to the shop (the STOCK slot logic): grow an
    /// existing under-cap slot, else claim a free slot, and (re)write the slot's price+currency. The
    /// price/currency default to the item's own when not supplied. Returns the slot index, or -1 when
    /// all ten slots are full of other items.</summary>
    public int StockGangShopItem(int shopId, int itemId, int itemDefaultPrice, int itemDefaultCurrency,
        bool priceGiven, int price, int currencyGiven)
    {
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
            {
                state = new GangShopState { ShopId = shopId };
                _gangShops[shopId] = state;
            }

            int slotIndex = -1;
            for (int i = 0; i < state.Slots.Length; i++)
            {
                var s = state.Slots[i];
                if (s != null && s.ItemId == itemId && s.Quantity < GangShopSlotMaxQuantity)
                {
                    s.Quantity++;
                    slotIndex = i;
                    break;
                }
            }
            if (slotIndex < 0)
            {
                for (int i = 0; i < state.Slots.Length; i++)
                {
                    if (state.Slots[i] == null)
                    {
                        state.Slots[i] = new GangShopSlot { ItemId = itemId, Quantity = 1 };
                        slotIndex = i;
                        break;
                    }
                }
            }

            if (slotIndex >= 0)
            {
                var slot = state.Slots[slotIndex]!;
                slot.Price = Math.Clamp(priceGiven ? price : itemDefaultPrice, 0, GangShopMaxSlotPrice);
                slot.Currency = currencyGiven >= 0 ? Math.Clamp(currencyGiven, 0, 4) : Math.Clamp(itemDefaultCurrency, 0, 4);
            }
            return slotIndex;
        }
    }

    /// <summary>Stock shop-item lookup: resolve a typed name to ONE stocked item. Word-prefix match
    /// over the stocked slots; an exact full name wins outright; otherwise exactly one matching
    /// slot resolves, and two or more are ambiguous (ambiguousNames = what matched, for the "Please be more
    /// specific" list). Returns the item id, or 0 when nothing matched or the name was ambiguous.</summary>
    public int ResolveGangShopItemByName(int shopId, string itemName, out IReadOnlyList<string>? ambiguousNames)
    {
        ambiguousNames = null;
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
                return 0;

            var matchedNames = new List<string>();
            int firstMatchId = 0;
            foreach (var s in state.Slots)
            {
                if (s == null || s.Quantity <= 0 || !Database.Items.TryGetValue(s.ItemId, out var slotItem))
                    continue;

                var rank = TargetNameMatcher.GetMatchRank(slotItem.Name, itemName);
                if (rank == TargetNameMatcher.MatchRank.None)
                    continue;
                if (rank == TargetNameMatcher.MatchRank.Exact)
                    return s.ItemId;

                if (firstMatchId == 0)
                    firstMatchId = s.ItemId;
                matchedNames.Add(slotItem.Name);
            }

            if (matchedNames.Count > 1)
            {
                ambiguousNames = matchedNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return 0;
            }

            return firstMatchId;
        }
    }

    /// <summary>Remove one of an item from stock — the lowest-quantity slot holding it (the last such slot
    /// on a tie, as stock scans with &lt;=). Returns false when no slot holds it.</summary>
    public bool UnstockGangShopItem(int shopId, int itemId)
    {
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
                return false;

            int chosen = -1;
            int bestQty = int.MaxValue;
            for (int i = 0; i < state.Slots.Length; i++)
            {
                var s = state.Slots[i];
                if (s == null || s.Quantity <= 0 || s.ItemId != itemId)
                    continue;
                if (s.Quantity <= bestQty)
                {
                    bestQty = s.Quantity;
                    chosen = i;
                }
            }

            if (chosen < 0)
                return false;

            var slot = state.Slots[chosen]!;
            slot.Quantity--;
            if (slot.Quantity <= 0)
                state.Slots[chosen] = null;
            return true;
        }
    }

    /// <summary>Empty every slot, returning the flattened list of item ids removed (UNSTOCK ALL —
    /// caller drops them on the floor).</summary>
    public List<int> UnstockAllGangShopItems(int shopId)
    {
        var dropped = new List<int>();
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
                return dropped;
            for (int i = 0; i < state.Slots.Length; i++)
            {
                var s = state.Slots[i];
                if (s == null)
                    continue;
                for (int q = 0; q < s.Quantity; q++)
                    dropped.Add(s.ItemId);
                state.Slots[i] = null;
            }
        }
        return dropped;
    }

    /// <summary>Find the first stocked slot holding an item (for BUY, after ResolveGangShopItemByName).
    /// Returns false when no stocked slot holds it.</summary>
    public bool TryFindGangShopPurchase(int shopId, int itemId, out int slotIndex, out GangShopSlot slot)
    {
        slotIndex = -1;
        slot = new GangShopSlot();
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state))
                return false;
            for (int i = 0; i < state.Slots.Length; i++)
            {
                var s = state.Slots[i];
                if (s == null || s.Quantity <= 0 || s.ItemId != itemId)
                    continue;
                slotIndex = i;
                slot = new GangShopSlot { ItemId = s.ItemId, Quantity = s.Quantity, Price = s.Price, Currency = s.Currency };
                return true;
            }
        }
        return false;
    }

    /// <summary>Consume one unit from a specific slot after a successful purchase (clears the slot at
    /// zero). Returns false if the slot is already empty (lost a race), so the caller can refund.</summary>
    public bool ConsumeGangShopSlot(int shopId, int slotIndex, int expectedItemId)
    {
        lock (_gangShopLock)
        {
            if (!_gangShops.TryGetValue(shopId, out var state) ||
                slotIndex < 0 || slotIndex >= state.Slots.Length)
                return false;
            var s = state.Slots[slotIndex];
            if (s == null || s.Quantity <= 0 || s.ItemId != expectedItemId)
                return false;
            s.Quantity--;
            if (s.Quantity <= 0)
                state.Slots[slotIndex] = null;
            return true;
        }
    }

    /// <summary>Persist a gang shop's current state (upsert), or delete the row when fully empty and
    /// at default markup so cleared shops leave no trace.</summary>
    public void PersistGangShop(int shopId)
    {
        string slots;
        int markup;
        bool exists;
        lock (_gangShopLock)
        {
            exists = _gangShops.TryGetValue(shopId, out var state);
            if (!exists)
            {
                slots = "";
                markup = 0;
            }
            else
            {
                slots = SerializeGangShopSlots(state!);
                markup = state!.MarkupPercent;
                if (slots.Length == 0 && markup == 0)
                {
                    // Nothing worth keeping — drop the in-memory state and the row together.
                    _gangShops.Remove(shopId);
                    exists = false;
                }
            }
        }

        if (!exists)
        {
            PlayerRepo.DeleteGangShop(shopId);
            return;
        }

        PlayerRepo.SaveGangShop(new GangShopRecord
        {
            ShopId = shopId,
            MarkupPercent = markup,
            Slots = slots,
        });
    }

    /// <summary>Deposit gang-shop takings into the owning gang leader's bank (bankbook 8). No-op
    /// when the host house is unowned or its leader can't be resolved.</summary>
    public void DepositGangShopRevenue(int shopId, long copper)
    {
        if (copper <= 0)
            return;

        int houseId = GetGangShopHouseId(shopId);
        if (houseId <= 0)
            return;

        var house = GetGangHouse(houseId);
        if (house == null)
            return;

        string leaderName = PlayerRepo.GetGangLeaderName(house.OwnerGang) ?? house.OwnerPlayer;
        if (string.IsNullOrWhiteSpace(leaderName))
            return;

        if (_onlinePlayers.TryGetValue(leaderName, out var onlineLeader))
        {
            onlineLeader.BankBalances[GangLeaderBankNumber] =
                checked(onlineLeader.BankBalances.GetValueOrDefault(GangLeaderBankNumber) + copper);
            return;
        }

        var leader = PlayerRepo.LoadPlayerByName(leaderName);
        if (leader == null)
            return;
        leader.BankBalances[GangLeaderBankNumber] =
            checked(leader.BankBalances.GetValueOrDefault(GangLeaderBankNumber) + copper);
        PlayerRepo.SavePlayer(leader);
    }

    /// <summary>Empty a house's gang shops (used on eviction). Clears every slot and resets markup,
    /// then deletes the persisted rows.</summary>
    public void ClearGangShopsForHouse(int houseId)
    {
        if (houseId <= 0)
            return;

        List<int> shopIds;
        lock (_gangShopLock)
            shopIds = _gangShopHouse.Where(kv => kv.Value == houseId).Select(kv => kv.Key).ToList();

        foreach (var shopId in shopIds)
        {
            lock (_gangShopLock)
                _gangShops.Remove(shopId);
            PlayerRepo.DeleteGangShop(shopId);
        }
    }

    // ---- serialization ("itemId:qty:price:currency" entries joined by ';') -----------------------

    private static string SerializeGangShopSlots(GangShopState state)
    {
        var parts = new List<string>();
        foreach (var slot in state.Slots)
        {
            if (slot == null || slot.ItemId <= 0 || slot.Quantity <= 0)
                continue;
            parts.Add($"{slot.ItemId}:{slot.Quantity}:{slot.Price}:{slot.Currency}");
        }
        return string.Join(";", parts);
    }

    private static GangShopState DeserializeGangShop(GangShopRecord rec)
    {
        var state = new GangShopState { ShopId = rec.ShopId, MarkupPercent = rec.MarkupPercent };
        int slotIndex = 0;
        foreach (var part in (rec.Slots ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (slotIndex >= GangShopSlotCount)
                break;
            var f = part.Split(':');
            if (f.Length != 4)
                continue;
            if (int.TryParse(f[0], out int itemId) && int.TryParse(f[1], out int qty) &&
                int.TryParse(f[2], out int price) && int.TryParse(f[3], out int currency) &&
                itemId > 0 && qty > 0)
            {
                state.Slots[slotIndex++] = new GangShopSlot
                {
                    ItemId = itemId,
                    Quantity = Math.Min(qty, GangShopSlotMaxQuantity),
                    Price = Math.Clamp(price, 0, GangShopMaxSlotPrice),
                    Currency = Math.Clamp(currency, 0, 4),
                };
            }
        }
        return state;
    }
}
