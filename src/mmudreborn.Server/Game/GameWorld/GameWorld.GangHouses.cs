using System;
using System.Collections.Generic;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

// Gang-house ownership engine: the deed-pool, nightly tax, and eviction stock runs in
// its nightly maintenance. Each of the ten houses (HouseId 1..10) can be owned by one gang at a time.
// Buying a deed from the Realm Deed Shop (ShopType 12) grants ownership; the deed itself drives the
// in-house command scripts (summon guard / make emblem / make key) via the existing textblock engine.
// Each night the owning gang's leader is taxed (deed ability 182, in gold); a leader who can't pay is
// evicted — ownership cleared, deed returned to the pool, gang-house-tagged items swept.
public partial class GameWorld
{
    public const int GangHouseDeedAbilityId = 181;   // value = house id 1..10
    public const int GangHouseTaxAbilityId = 182;    // value = nightly tax in gold
    public const int GangHouseItemAbilityId = 183;   // value = house id (keys/emblems/keyrings/deed)
    public const int DeedShopType = 12;              // ShopType 12

    // Colour names indexed by house id.
    private static readonly string[] GangHouseColorNames =
        { "", "Red", "Orange", "Yellow", "Green", "Violet", "Blue", "Black", "Silver", "Gold", "White" };

    private readonly object _gangHouseLock = new();
    // Owned houses only (houseId -> record). Absence ⇒ unowned/available.
    private readonly Dictionary<int, GangHouseRecord> _gangHouses = new();

    // Gangs evicted for non-payment can't buy another deed until the next daily cleanup clears the
    // lockout (the stock "outstanding paper-work" flag). Persisted as a CSV server setting.
    private const string GangHouseLockoutSettingKey = "GangHouseEvictionLockouts";
    private readonly HashSet<string> _gangHouseLockouts = new(StringComparer.OrdinalIgnoreCase);

    public static string GangHouseColorName(int houseId)
        => houseId >= 1 && houseId <= 10 ? GangHouseColorNames[houseId] : "Gang";

    private void LoadGangHouses()
    {
        lock (_gangHouseLock)
        {
            _gangHouses.Clear();
            foreach (var rec in PlayerRepo.LoadGangHouses())
            {
                if (rec.HouseId is >= 1 and <= 10)
                    _gangHouses[rec.HouseId] = rec;
            }

            _gangHouseLockouts.Clear();
            foreach (var gang in PlayerRepo.GetServerSettingText(GangHouseLockoutSettingKey, "")
                         .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                _gangHouseLockouts.Add(gang);
            }
        }

        // Faithful deed pool: a deed for an owned house is OUT of the shop until the house is lost.
        foreach (var houseId in GetOwnedHouseIds())
            SetGangHouseDeedInStock(houseId, false);
    }

    public bool IsGangHouseLockedOut(string gang)
    {
        if (string.IsNullOrWhiteSpace(gang))
            return false;
        lock (_gangHouseLock)
            return _gangHouseLockouts.Contains(gang);
    }

    private void PersistGangHouseLockouts()
        => PlayerRepo.SetServerSettingText(GangHouseLockoutSettingKey, string.Join(",", _gangHouseLockouts));

    private List<int> GetOwnedHouseIds()
    {
        lock (_gangHouseLock)
            return _gangHouses.Keys.ToList();
    }

    public GangHouseRecord? GetGangHouse(int houseId)
    {
        lock (_gangHouseLock)
            return _gangHouses.TryGetValue(houseId, out var rec) ? rec : null;
    }

    /// <summary>The house id this gang owns (1..10), or 0 if none.</summary>
    public int GetGangOwnedHouseId(string gang)
    {
        if (string.IsNullOrWhiteSpace(gang))
            return 0;
        lock (_gangHouseLock)
        {
            foreach (var (id, rec) in _gangHouses)
            {
                if (string.Equals(rec.OwnerGang, gang, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }
        return 0;
    }

    public bool IsGangHouseOwned(int houseId) => GetGangHouse(houseId) != null;

    /// <summary>The deed item's house id (its ability 181), or 0 if the item is not a deed.</summary>
    public int GetDeedHouseId(int itemId)
        => Database.Items.TryGetValue(itemId, out var item)
            ? item.Abilities.GetValueOrDefault(GangHouseDeedAbilityId)
            : 0;

    private int GetHouseTaxGold(int houseId)
    {
        // Tax lives on the deed item (ability 182). Find the deed whose ability 181 == houseId.
        foreach (var item in Database.Items.Values)
        {
            if (item.Abilities.GetValueOrDefault(GangHouseDeedAbilityId) == houseId)
                return item.Abilities.GetValueOrDefault(GangHouseTaxAbilityId);
        }
        return 0;
    }

    /// <summary>Record a gang as the owner of a house and persist it (purchase grant).</summary>
    public void AssignGangHouse(int houseId, string gang, string ownerPlayer, DateTime now)
    {
        var rec = new GangHouseRecord
        {
            HouseId = houseId,
            OwnerGang = gang,
            OwnerPlayer = ownerPlayer,
            PurchasedAt = now.ToString("o"),
            LastTaxAt = now.ToString("o"),
        };
        lock (_gangHouseLock)
            _gangHouses[houseId] = rec;
        PlayerRepo.SaveGangHouse(rec);
        SetGangHouseDeedInStock(houseId, false);   // deed leaves the pool while owned
    }

    /// <summary>Find the deed slot for a house in any deed shop and set/clear its stock.</summary>
    private void SetGangHouseDeedInStock(int houseId, bool inStock)
    {
        lock (_shopStockLock)
        {
            foreach (var shop in Database.Shops.Values)
            {
                if (shop.ShopType != DeedShopType)
                    continue;
                foreach (var slot in shop.Items)
                {
                    if (slot.ItemId <= 0)
                        continue;
                    if (GetDeedHouseId(slot.ItemId) != houseId)
                        continue;
                    slot.Current = inStock ? Math.Max(1, slot.Max) : 0;
                }
            }
        }
    }

    // ---- Nightly tax + eviction (called from RunDailyCleanup) ------------------------------------

    public void ProcessGangHouseTax(DateTime now)
    {
        // The "outstanding paper-work" lockout clears each cleanup; tonight's evictions re-arm it.
        lock (_gangHouseLock)
        {
            _gangHouseLockouts.Clear();
            PersistGangHouseLockouts();
        }

        List<GangHouseRecord> owned;
        lock (_gangHouseLock)
            owned = _gangHouses.Values.ToList();

        var evictedHouseIds = new HashSet<int>();
        foreach (var house in owned)
        {
            int taxGold = GetHouseTaxGold(house.HouseId);
            long taxCopper = (long)taxGold * 100;            // tax = the ability value * 100 copper
            string colour = GangHouseColorName(house.HouseId);

            string leaderName = PlayerRepo.GetGangLeaderName(house.OwnerGang) ?? house.OwnerPlayer;
            bool online = _onlinePlayers.TryGetValue(leaderName, out var leader);
            leader ??= PlayerRepo.LoadPlayerByName(leaderName);

            if (leader != null && TryChargeGangHouseTax(leader, taxCopper))
            {
                house.LastTaxAt = now.ToString("o");
                PlayerRepo.SaveGangHouse(house);
                if (!online)
                    PlayerRepo.SavePlayer(leader);            // persist the offline leader's debit
                SendToPlayer(leaderName,
                    $"{MudAnsi.BrightYellow}Your gang paid the {taxGold} gold nightly tax on the {colour} Gang House.{MudAnsi.Reset}");
                Console.WriteLine($"GangHouse {house.HouseId} ({colour}): {house.OwnerGang} paid {taxGold}g tax.");
            }
            else
            {
                Console.WriteLine($"GangHouse {house.HouseId} ({colour}): {house.OwnerGang} could not pay {taxGold}g tax — evicting.");
                EvictGangHouse(house);
                evictedHouseIds.Add(house.HouseId);
            }
        }

        // One global pass over OFFLINE players removes every evicted house's keys/emblems/keyrings —
        // they're shared per-house and emblems can be given to anyone, so a stale holder must not keep
        // working keys or guard safe-passage against the next owner (online players swept in EvictGangHouse).
        if (evictedHouseIds.Count > 0)
            SweepGangHouseItemsFromOfflinePlayers(evictedHouseIds);
    }

    /// <summary>Debit the tax from the gang leader's bank holdings (largest balance first), mirroring
    /// stock drawing house tax from the gang leader's bankbook. Returns false (⇒ eviction) if the
    /// leader's total banked copper can't cover it.</summary>
    internal static bool TryChargeGangHouseTax(Player leader, long taxCopper)
    {
        if (taxCopper <= 0)
            return true;
        long banked = leader.BankBalances.Values.Sum();
        if (banked < taxCopper)
            return false;

        long remaining = taxCopper;
        foreach (var bankNumber in leader.BankBalances.Keys.OrderByDescending(k => leader.BankBalances[k]).ToList())
        {
            if (remaining <= 0)
                break;
            long take = Math.Min(remaining, leader.BankBalances[bankNumber]);
            leader.BankBalances[bankNumber] -= take;
            remaining -= take;
        }
        return true;
    }

    private void EvictGangHouse(GangHouseRecord house)
    {
        lock (_gangHouseLock)
        {
            _gangHouses.Remove(house.HouseId);
            _gangHouseLockouts.Add(house.OwnerGang);        // can't re-buy until next cleanup clears it
            PersistGangHouseLockouts();
        }
        PlayerRepo.DeleteGangHouse(house.HouseId);
        SetGangHouseDeedInStock(house.HouseId, true);        // deed returns to the pool
        ClearGangShopsForHouse(house.HouseId);               // the gang's stocked shops empty out

        // Sweep online holders immediately (so their live state can't access the house this session).
        foreach (var player in GetAllOnlinePlayers())
        {
            if (RemoveGangHouseTaggedItems(player, h => h == house.HouseId))
                RecalculatePlayerStats(player);              // a worn emblem's bonus must drop too
        }

        string colour = GangHouseColorName(house.HouseId);
        foreach (var player in GetAllOnlinePlayers())
        {
            if (string.Equals(player.Gang, house.OwnerGang, StringComparison.OrdinalIgnoreCase))
                SendToPlayer(player.Name,
                    $"{MudAnsi.BrightRed}Your gang failed to pay the tax — the {colour} Gang House has been closed down!{MudAnsi.Reset}");
        }
    }

    /// <summary>Remove every offline player's keys/emblems/keyrings/deed (ability 183) for any of the
    /// evicted houses. Online players are handled live in EvictGangHouse.</summary>
    private void SweepGangHouseItemsFromOfflinePlayers(ISet<int> evictedHouseIds)
    {
        var online = new HashSet<string>(_onlinePlayers.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var name in PlayerRepo.GetAllPlayerNames())
        {
            if (online.Contains(name))
                continue;
            var player = PlayerRepo.LoadPlayerByName(name);
            if (player != null && RemoveGangHouseTaggedItems(player, evictedHouseIds.Contains))
                PlayerRepo.SavePlayer(player);
        }
    }

    /// <summary>Remove items whose ability 183 (Gang House Item) value matches <paramref name="matchesHouse"/>
    /// from a player's inventory and equipment. Returns true if anything was removed.</summary>
    private bool RemoveGangHouseTaggedItems(Player player, Func<int, bool> matchesHouse)
    {
        bool removed = false;

        for (int i = player.Inventory.Count - 1; i >= 0; i--)
        {
            if (!Database.Items.TryGetValue(player.Inventory[i], out var item))
                continue;
            int tag = item.Abilities.GetValueOrDefault(GangHouseItemAbilityId);
            if (tag <= 0 || !matchesHouse(tag))
                continue;
            if (i < player.InventoryInstanceIds.Count)
            {
                RemoveItemRuntimeState(player.InventoryInstanceIds[i]);
                player.InventoryInstanceIds.RemoveAt(i);
            }
            player.Inventory.RemoveAt(i);
            removed = true;
        }

        foreach (var slot in player.Equipment.Where(kv =>
                     Database.Items.TryGetValue(kv.Value, out var it) &&
                     it.Abilities.GetValueOrDefault(GangHouseItemAbilityId) is var t && t > 0 && matchesHouse(t))
                     .Select(kv => kv.Key).ToList())
        {
            player.Equipment.Remove(slot);
            player.EquipmentInstanceIds.Remove(slot);
            removed = true;
        }

        return removed;
    }
}
