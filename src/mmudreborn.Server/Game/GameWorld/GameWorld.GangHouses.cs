using System;
using System.Collections.Generic;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

// Gang-house ownership engine: the deed pool, nightly ownership, tax and eviction the stock cleanup
// runs. Stock stores no owner. Each night a house is owned exactly when a player in a
// gang carries its deed (ability 181); that carrier pays the tax (deed ability 182, in gold) from their
// bankbook, and the owning gang is the carrier's gang. A house nobody carries the deed to, or whose
// carrier can't pay, is closed: its ability-183 items leave every player, anyone saved inside is moved
// out, its floors and gang shops are emptied and the deed goes back on the Realm Deed Shop shelf. An owned
// house's items leave anyone outside the owning gang. The deed itself drives the in-house command scripts
// (summon guard / make emblem / make key) via the existing textblock engine.
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

    // Legacy: the re-buy lockout used to be a per-gang CSV armed on eviction. Stock keeps it per player,
    // armed by selling a deed (Player.GangHouseDeedSoldFlag), so the old setting is only ever cleared.
    private const string GangHouseLockoutSettingKey = "GangHouseEvictionLockouts";

    // The nightly bank read lands on the carrier's first bankbook at or above this one (Bank of Godfrey).
    private const int GangHouseTaxBankNumber = 8;

    private readonly record struct GangHouseOwner(Player Carrier, string Gang);

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
        }

        if (PlayerRepo.GetServerSettingText(GangHouseLockoutSettingKey, "").Length > 0)
            PlayerRepo.SetServerSettingText(GangHouseLockoutSettingKey, string.Empty);

        // Faithful deed pool: a deed for an owned house is OUT of the shop until the house is lost.
        foreach (var houseId in GetOwnedHouseIds())
            SetGangHouseDeedInStock(houseId, false);
    }

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

    /// <summary>A gang's experience: what its members have earned while in it (the deed-purchase gate).
    /// Online members count their live total.</summary>
    public long GetGangExperience(string gang)
    {
        long total = 0;
        foreach (var (name, _) in PlayerRepo.GetPlayersByGang(gang))
        {
            var member = _onlinePlayers.TryGetValue(name, out var online) ? online : PlayerRepo.LoadPlayerByName(name);
            if (member != null)
                total += member.GangExperience;
        }
        return total;
    }

    // ---- Nightly cleanup: ownership, tax, sweep (called from RunDailyCleanup) ---------------------

    public void ProcessGangHouseTax(DateTime now)
    {
        // Every character: the live object for anyone online, the stored record for everyone else.
        var online = GetAllOnlinePlayers();
        var onlineNames = new HashSet<string>(online.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var offline = new List<Player>();
        foreach (var name in PlayerRepo.GetAllPlayerNames())
        {
            if (onlineNames.Contains(name))
                continue;
            var stored = PlayerRepo.LoadPlayerByName(name);
            if (stored != null)
                offline.Add(stored);
        }

        // 1. Ownership. A house is owned tonight exactly when a player in a gang carries its deed in their
        //    pack; the carrier is the owner and their gang the owning gang. Stock overwrites on every find,
        //    so the last carrier walked wins.
        var owners = new Dictionary<int, GangHouseOwner>();
        foreach (var player in online.Concat(offline))
        {
            if (string.IsNullOrWhiteSpace(player.Gang))
                continue;
            foreach (var itemId in player.Inventory)
            {
                int houseId = GetDeedHouseId(itemId);
                if (houseId is >= 1 and <= 10)
                    owners[houseId] = new GangHouseOwner(player, player.Gang);
            }
        }

        // 2. Tax, from the carrier's bankbook. A carrier who can't pay loses the house tonight.
        var changedOffline = new HashSet<Player>(ReferenceEqualityComparer.Instance);
        var unpaid = new HashSet<int>();
        foreach (var (houseId, owner) in owners.OrderBy(kv => kv.Key))
        {
            int taxGold = GetHouseTaxGold(houseId);
            string colour = GangHouseColorName(houseId);
            if (TryChargeGangHouseTax(owner.Carrier, (long)taxGold * 100))
            {
                if (!onlineNames.Contains(owner.Carrier.Name))
                    changedOffline.Add(owner.Carrier);
                Console.WriteLine($"GangHouse {houseId} ({colour}): {owner.Carrier.Name} of {owner.Gang} paid {taxGold}g tax.");
            }
            else
            {
                unpaid.Add(houseId);
                Console.WriteLine($"GangHouse {houseId} ({colour}): {owner.Carrier.Name} of {owner.Gang} could not pay {taxGold}g tax — evicting.");
            }
        }

        bool IsClosed(int houseId) => !owners.ContainsKey(houseId) || unpaid.Contains(houseId);

        // 3. Every character: the paper-work lockout ends, house items go, anyone saved inside a closed
        //    house is moved out. Stock runs this only for characters with play time since the last cleanup,
        //    so an idle player kept stale keys for days; we deliberately run it for everyone.
        foreach (var player in online.Concat(offline))
        {
            bool isOnline = onlineNames.Contains(player.Name);
            if (ApplyNightlyGangHouseRules(player, owners, IsClosed, isOnline) && !isOnline)
                changedOffline.Add(player);
        }
        foreach (var player in changedOffline)
            PlayerRepo.SavePlayer(player);

        // 4. Houses: a closed house loses its record, its gang shops and its deed goes back on sale; an
        //    owned one's record follows tonight's carrier. Then the house floors.
        for (int houseId = 1; houseId <= 10; houseId++)
        {
            if (IsClosed(houseId))
            {
                bool hadRecord;
                lock (_gangHouseLock)
                    hadRecord = _gangHouses.Remove(houseId);
                if (hadRecord)
                    PlayerRepo.DeleteGangHouse(houseId);
                SetGangHouseDeedInStock(houseId, true);
                ClearGangShopsForHouse(houseId);
                continue;
            }

            var owner = owners[houseId];
            GangHouseRecord rec;
            lock (_gangHouseLock)
            {
                bool sameGang = _gangHouses.TryGetValue(houseId, out var existing)
                    && string.Equals(existing.OwnerGang, owner.Gang, StringComparison.OrdinalIgnoreCase);
                rec = new GangHouseRecord
                {
                    HouseId = houseId,
                    OwnerGang = owner.Gang,
                    OwnerPlayer = owner.Carrier.Name,
                    PurchasedAt = sameGang ? existing!.PurchasedAt : now.ToString("o"),
                    LastTaxAt = now.ToString("o"),
                };
                _gangHouses[houseId] = rec;
            }
            PlayerRepo.SaveGangHouse(rec);
        }

        if (ClearGangHouseFloors(IsClosed))
            PersistRoomGroundState();
    }

    /// <summary>One character's share of the nightly cleanup. Returns true when anything changed.
    /// Items tagged (ability 183) to a closed house are removed from everyone; items of an owned house
    /// from anyone outside its owning gang. Losing an unpaid house's items leaves its carrier "Your
    /// ganghouse has been closed down!!" and its gang "Gang house items have dissappeared..." — shown
    /// now to an online player, at next login otherwise. A house nobody carried has no owner to notify.</summary>
    private bool ApplyNightlyGangHouseRules(Player player, Dictionary<int, GangHouseOwner> owners,
        Func<int, bool> isClosed, bool isOnline)
    {
        bool changed = false;

        // The paper-work lockout from selling a deed lasts until this cleanup.
        if ((player.GangHouseFlags & Player.GangHouseDeedSoldFlag) != 0)
        {
            player.GangHouseFlags &= ~Player.GangHouseDeedSoldFlag;
            changed = true;
        }

        int notices = 0;
        foreach (int houseId in GetCarriedGangHouseTags(player))
        {
            if (!isClosed(houseId) || !owners.TryGetValue(houseId, out var owner))
                continue;
            if (string.Equals(owner.Carrier.Name, player.Name, StringComparison.OrdinalIgnoreCase))
                notices |= Player.GangHouseClosedNoticeFlag;
            if (string.Equals(owner.Gang, player.Gang, StringComparison.OrdinalIgnoreCase))
                notices |= Player.GangHouseItemsGoneNoticeFlag;
        }

        bool removed = RemoveGangHouseTaggedItems(player, houseId =>
            isClosed(houseId)
            || !string.Equals(player.Gang, owners[houseId].Gang, StringComparison.OrdinalIgnoreCase));
        if (removed)
        {
            changed = true;
            if (isOnline)
                RecalculatePlayerStats(player);              // a worn emblem's bonus must drop too
        }

        if (notices != 0)
        {
            changed = true;
            if (isOnline)
            {
                if ((notices & Player.GangHouseClosedNoticeFlag) != 0)
                    SendToPlayer(player.Name, GangHouseClosedNotice);
                if ((notices & Player.GangHouseItemsGoneNoticeFlag) != 0)
                    SendToPlayer(player.Name, GangHouseItemsGoneNotice);
            }
            else
            {
                player.GangHouseFlags |= notices;
            }
        }

        // Anyone saved inside a closed house is put out: the Temple, or the Earthen Tomb from 40 evil
        // points up — the same rooms (and cut) a death respawns to.
        var room = GetRoom(player.CurrentMapNumber, player.CurrentRoomNumber);
        if (room != null && room.IsGangHouse && room.GangHouseId is >= 1 and <= 10 && isClosed(room.GangHouseId))
        {
            player.CurrentMapNumber = DefaultDeathRespawnMapNumber;
            player.CurrentRoomNumber = ShouldRespawnAtEvilDeathRoom(player) ? EvilDeathRespawnRoomNumber : DefaultDeathRespawnRoomNumber;
            changed = true;
            if (isOnline)
            {
                player.IsResting = false;
                player.IsMeditating = false;
                player.IsSneaking = false;
                player.IsHidden = false;
                NotifyPlayerEnteredRoom(player);
                RepromptPlayer(player.Name);
            }
        }

        return changed;
    }

    // The two gang-house login notices, as stock prints them (bold white).
    public static readonly string GangHouseClosedNotice = $"{MudAnsi.BrightWhite}Your ganghouse has been closed down!!{MudAnsi.Reset}";
    public static readonly string GangHouseItemsGoneNotice = $"{MudAnsi.BrightWhite}Gang house items have dissappeared from your inventory!{MudAnsi.Reset}";

    // House ids (1..10) of every ability-183 item a character carries or wears.
    private HashSet<int> GetCarriedGangHouseTags(Player player)
    {
        var tags = new HashSet<int>();
        foreach (var itemId in player.Inventory.Concat(player.Equipment.Values))
        {
            if (Database.Items.TryGetValue(itemId, out var item)
                && item.Abilities.GetValueOrDefault(GangHouseItemAbilityId) is var tag && tag is >= 1 and <= 10)
                tags.Add(tag);
        }
        return tags;
    }

    /// <summary>The house floors at cleanup: every item and coin in a closed house's rooms is swept away,
    /// and an owned house's floors lose any item tagged to a closed house. Returns true when anything went.</summary>
    private bool ClearGangHouseFloors(Func<int, bool> isClosed)
    {
        bool changed = false;
        foreach (var room in Database.Rooms.Values)
        {
            if (!room.IsGangHouse || room.GangHouseId is < 1 or > 10)
                continue;

            var key = (room.MapNumber, room.RoomNumber);
            bool closed = isClosed(room.GangHouseId);
            lock (_groundItemLock)
            {
                if (_roomGroundItems.TryGetValue(key, out var items))
                {
                    int removedCount = items.RemoveAll(entry => closed
                        || (Database.Items.TryGetValue(entry.ItemId, out var item)
                            && item.Abilities.GetValueOrDefault(GangHouseItemAbilityId) is var tag
                            && tag is >= 1 and <= 10 && isClosed(tag)));
                    if (removedCount > 0)
                        changed = true;
                    if (items.Count == 0)
                        _roomGroundItems.TryRemove(key, out _);
                }
            }

            if (closed && _roomGroundCurrency.TryRemove(key, out _))
                changed = true;
        }
        return changed;
    }

    /// <summary>Charge the nightly tax to the deed carrier's bankbook: the first one at or above bank 8
    /// (Bank of Godfrey), which is where the cleanup's keyed bank read lands. That one book must cover the
    /// whole tax — the carrier's other banks are never touched. Returns false (⇒ eviction) otherwise.</summary>
    internal static bool TryChargeGangHouseTax(Player carrier, long taxCopper)
    {
        if (taxCopper <= 0)
            return true;

        var books = carrier.BankBalances.Keys.Where(bank => bank >= GangHouseTaxBankNumber).ToList();
        if (books.Count == 0)
            return false;

        int book = books.Min();
        if (carrier.BankBalances[book] < taxCopper)
            return false;

        carrier.BankBalances[book] -= taxCopper;
        return true;
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
