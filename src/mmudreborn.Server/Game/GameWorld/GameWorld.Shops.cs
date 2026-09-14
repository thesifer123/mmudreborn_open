using System;
using System.Collections.Generic;
using System.Text.Json;
using mmudreborn.Data.Models;

namespace mmudreborn.Server;

// Finite shop stock + restock. A buy decrements the slot quantity and the slow tick restocks it.
// A shop slot tracks a current quantity that
// decrements on purchase and is replenished probabilistically on a per-slot timer.
//
// A slot with Max<=0 is a SELL-ONLY slot: the item can be sold TO the shop (HandleSell only checks
// list membership) but never BOUGHT. This is the stock model — "in stock" means current qty > 0
// and a buy only sells a slot whose qty is non-zero; a Max=0 slot boots at Current=0
// (GameDatabase seeds Current=Max) and the restock skips it (needs qty<max, i.e. 0<0), so it stays
// at 0 forever. This is how "junkyard"/Recycler shops work — every slot Max=0 → you can only offload
// items there, never buy them back. (The old code treated Max<=0 as UNLIMITED/"0=infinite" and let
// those slots be bought, which is why buying at the Junkyard worked; that was a divergence from stock.)
public partial class GameWorld
{
    // Gang shops (ShopType 11) are skipped by the restock.
    public const int GangShopType = 11;

    // Healer/Temple (ShopType 5): sells the "healing" and "cure poison" services, not stock.
    public const int HealerShopType = 5;

    // Bank (ShopType 7): deposit/withdraw services, not stock. LIST shows banking rates.
    public const int BankShopType = 7;

    private readonly object _shopStockLock = new();

    // Finite shop stock is in-memory and re-seeded to full at boot (GameDatabase seeds Current=Max). To
    // survive a restart we persist the DEPLETED slots (Current < Max) to a JSON blob in ServerSettings —
    // the same pattern as the monster-regen ledger — and re-apply them after boot. Set on every purchase
    // / restock change; the slow-tick + shutdown persist paths write only when set.
    private const string ShopStockStateSettingKey = "ShopStockState";
    private volatile bool _shopStockDirty;

    private sealed class PersistedShopSlot
    {
        public int ShopId { get; init; }
        public int Slot { get; init; }        // index into shop.Items (order is stable across boots)
        public int ItemId { get; init; }      // guards against the shop being reordered/edited between runs
        public int Current { get; init; }
        public DateTime NextRestockUtc { get; init; }
    }

    /// <summary>
    /// A slot is buyable only while its
    /// current qty &gt; 0. A sell-only slot (Max&lt;=0) boots at Current=0 and never restocks, so it is
    /// never in stock to buy — you can only sell that item to the shop (junkyard/Recycler behavior).
    /// </summary>
    public bool IsShopItemInStock(ShopItem item)
    {
        lock (_shopStockLock)
            return item.Current > 0;
    }

    /// <summary>
    /// Decrement the slot's current qty after a successful purchase
    /// (clamped at 0). No-op for an unlimited slot.
    /// </summary>
    public void ConsumeShopStock(ShopItem item)
    {
        if (item.Max <= 0)
            return;
        lock (_shopStockLock)
        {
            item.Current = Math.Max(0, item.Current - 1);
            _shopStockDirty = true;
        }
    }

    /// <summary>
    /// The restock (slow tick): for every finite slot whose per-slot restock
    /// timer is due, roll <c>genrdn(1,100) &lt; Percent</c> and, on success, add <c>Amount</c> (clamped
    /// to Max) while the slot is below Max. Gang shops never restock; <c>Time</c> is the restock
    /// interval in minutes (Time&lt;=0 ⇒ a restock attempt every tick).
    /// </summary>
    public void ProcessShopRestocks(DateTime now)
    {
        lock (_shopStockLock)
        {
            foreach (var shop in Database.Shops.Values)
            {
                // Gang shops never restock; deed shops (type 12) are a managed pool — a deed only
                // leaves on purchase and returns on eviction (SetGangHouseDeedInStock).
                if (shop.ShopType == GangShopType || shop.ShopType == DeedShopType)
                    continue;

                foreach (var item in shop.Items)
                {
                    if (item.ItemId <= 0 || item.Max <= 0 || item.NextRestockUtc > now)
                        continue;

                    int currentBefore = item.Current;
                    ApplyDueShopRestock(item, _rng.Next(1, 100));
                    item.NextRestockUtc = item.Time > 0 ? now.AddMinutes(item.Time) : now;
                    if (item.Current != currentBefore)
                        _shopStockDirty = true;
                }
            }
        }
    }

    /// <summary>
    /// Re-seed every shop slot back to a full-boot state (Current = Max, restock timers cleared). The
    /// live server only does this at startup (LoadShops), but the integration harness reuses one
    /// GameWorld across many tests, so the test reset path calls this to keep finite stock isolated.
    /// </summary>
    public void ResetShopStock()
    {
        lock (_shopStockLock)
        {
            foreach (var shop in Database.Shops.Values)
            {
                foreach (var item in shop.Items)
                {
                    item.Current = item.Max;
                    item.NextRestockUtc = default;
                }
            }
        }
    }

    /// <summary>
    /// Pure per-slot restock decision for a slot that is already due:
    /// when below Max, a <paramref name="roll"/> (1..100) strictly below <see cref="ShopItem.Percent"/>
    /// adds <see cref="ShopItem.Amount"/>, clamped to <see cref="ShopItem.Max"/>. Static + side-effect
    /// only on the slot so it is deterministically unit-testable.
    /// </summary>
    internal static void ApplyDueShopRestock(ShopItem item, int roll)
    {
        if (item.Current < item.Max && roll < item.Percent)
            item.Current = Math.Min(item.Max, item.Current + item.Amount);
    }

    /// <summary>
    /// Persist the depleted finite shop slots (Current &lt; Max) so purchases survive a restart. Only
    /// deviations are recorded, so the blob stays tiny. Gang/deed shops are managed elsewhere and skipped.
    /// Builds the snapshot under the stock lock, then writes outside it (no DB I/O under the lock).
    /// </summary>
    public void PersistShopStock()
    {
        List<PersistedShopSlot> entries;
        lock (_shopStockLock)
        {
            entries = [];
            foreach (var (shopId, shop) in Database.Shops)
            {
                if (shop.ShopType == GangShopType || shop.ShopType == DeedShopType)
                    continue;

                for (int i = 0; i < shop.Items.Count; i++)
                {
                    var item = shop.Items[i];
                    // Only depleted finite slots deviate from the full-boot state worth restoring.
                    if (item.ItemId <= 0 || item.Max <= 0 || item.Current >= item.Max)
                        continue;

                    entries.Add(new PersistedShopSlot
                    {
                        ShopId = shopId,
                        Slot = i,
                        ItemId = item.ItemId,
                        Current = item.Current,
                        NextRestockUtc = item.NextRestockUtc,
                    });
                }
            }
            _shopStockDirty = false;
        }

        PlayerRepo.SetServerSettingText(ShopStockStateSettingKey, JsonSerializer.Serialize(entries));
    }

    /// <summary>
    /// Re-apply persisted shop-stock depletion after boot (shops seed Current=Max on load). Matches by
    /// (shop, slot) and verifies the slot still holds the same item, so a shop edited between runs simply
    /// keeps its full-boot stock for the changed slots rather than mis-applying a saved quantity.
    /// </summary>
    public void LoadPersistedShopStock()
    {
        string json = PlayerRepo.GetServerSettingText(ShopStockStateSettingKey, string.Empty);
        if (string.IsNullOrWhiteSpace(json))
            return;

        List<PersistedShopSlot> entries;
        try
        {
            entries = JsonSerializer.Deserialize<List<PersistedShopSlot>>(json) ?? [];
        }
        catch (JsonException)
        {
            return; // corrupt blob: leave shops at their full-boot state
        }

        lock (_shopStockLock)
        {
            foreach (var entry in entries)
            {
                if (!Database.Shops.TryGetValue(entry.ShopId, out var shop))
                    continue;
                if (shop.ShopType == GangShopType || shop.ShopType == DeedShopType)
                    continue;
                if (entry.Slot < 0 || entry.Slot >= shop.Items.Count)
                    continue;

                var item = shop.Items[entry.Slot];
                if (item.Max <= 0 || item.ItemId != entry.ItemId)
                    continue;

                item.Current = Math.Clamp(entry.Current, 0, item.Max);
                item.NextRestockUtc = entry.NextRestockUtc;
            }
        }
    }
}
