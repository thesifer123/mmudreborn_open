using mmudreborn.Data.Models;
using System.Globalization;

namespace mmudreborn.Server;

// Owner-side management of gang-owned shops (STOCK / UNSTOCK / MARKUP).
// Stocking takes an item out of the owner's inventory and onto a shop slot (ten
// slots, twenty of each, optional price and currency); unstocking returns it (or, with "all", dumps
// everything to the floor); markup sets the shop-wide price multiplier. The slot data itself lives in
// GameWorld.GangShops.cs (single-locked); these handlers parse input and do the player-facing I/O.
public partial class CommandParser
{
    // ---- STOCK ----------------------------------------------------------------------------------

    private async Task HandleStock(string args)
    {
        if (!TryGetGangShopForManagement("STOCK", "stock", out var shop, out _, out string error))
        {
            await _client.SendLineAsync(error);
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: STOCK {item} {price} {currency}");
            return;
        }

        // Parse "<item name> [price|free] [currency]" right-to-left: an optional trailing currency
        // word, then an optional trailing price/"free", with everything left over as the item name.
        var tokens = new List<string>(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int currency = -1;
        bool priceGiven = false;
        int price = 0;

        if (tokens.Count >= 2 && TryMatchCurrencyWord(tokens[^1], out int ci))
        {
            currency = ci;
            tokens.RemoveAt(tokens.Count - 1);
        }
        if (tokens.Count >= 2)
        {
            string last = tokens[^1];
            if (last.Equals("free", StringComparison.OrdinalIgnoreCase) || last.Equals("f", StringComparison.OrdinalIgnoreCase))
            {
                price = 0;
                priceGiven = true;
                tokens.RemoveAt(tokens.Count - 1);
            }
            else if (int.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out int p))
            {
                price = Math.Min(p, GameWorld.GangShopMaxSlotPrice);
                priceGiven = true;
                tokens.RemoveAt(tokens.Count - 1);
            }
        }

        string itemName = string.Join(' ', tokens).Trim();
        if (!TryResolveUniqueCarriedItem(itemName, includeEquipped: false, out var carried, out var ambiguousNames))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return;
            }
            await _client.SendLineAsync("You do not have the correct item to stock this shop.");
            return;
        }

        var item = carried.Item;

        // Stock eligibility: limited (Limit set) items and Loyal (ability 100) items are refused.
        if (item.Limit != 0)
        {
            await _client.SendLineAsync("You may not stock limited items!");
            return;
        }
        if (item.Abilities.GetValueOrDefault(ItemLoyalAbilityId) > 0)
        {
            await _client.SendLineAsync("You may not stock that item!");
            return;
        }

        int slotIndex = _world.StockGangShopItem(
            shop.Number, item.Number, item.Price, item.Currency, priceGiven, price, currency);
        if (slotIndex < 0)
        {
            await _client.SendLineAsync("No more new items may be stocked in this shop.");
            return;
        }

        // Take the item out of the owner's inventory and persist the shop.
        TryRemoveInventoryItemAt(carried.InventoryIndex, out _, out _);
        _world.RemoveItemRuntimeState(carried.InstanceId);
        RecalcEquipment();
        _world.PersistGangShop(shop.Number);

        await _client.SendLineAsync($"You add the {item.Name} to your shops stock.");
        _world.BroadcastToRoom(
            _player.CurrentMapNumber,
            _player.CurrentRoomNumber,
            $"You see {_player.Name} add a {item.Name} to the shops stock.",
            _client);
    }

    // ---- UNSTOCK --------------------------------------------------------------------------------

    private async Task HandleUnstock(string args)
    {
        if (!TryGetGangShopForManagement("UNSTOCK", "unstock", out var shop, out _, out string error))
        {
            await _client.SendLineAsync(error);
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: UNSTOCK {item}");
            return;
        }

        // UNSTOCK ALL: empty every slot onto the room floor.
        if (args.Trim().Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var dropped = _world.UnstockAllGangShopItems(shop.Number);
            if (dropped.Count == 0)
            {
                await _client.SendLineAsync("There are no stocked items to remove.");
                return;
            }

            // Unstocking a shop can easily exceed 17 slots; spill so gang property is never destroyed.
            foreach (int itemId in dropped)
                _world.DisposeOfItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, itemId);
            _world.PersistGangShop(shop.Number);

            await _client.SendLineAsync("You remove all the items from your shop and place them on the floor.");
            _world.BroadcastToRoom(
                _player.CurrentMapNumber,
                _player.CurrentRoomNumber,
                $"You see {_player.Name} remove all items from the gang shop and place them on the floor.",
                _client);
            return;
        }

        // UNSTOCK <item>: take one back into inventory.
        int removedItemId = _world.ResolveGangShopItemByName(shop.Number, args.Trim(), out var ambiguousStock);
        if (ambiguousStock != null)
        {
            await ShowItemDisambiguationAsync(ambiguousStock);
            return;
        }
        if (removedItemId <= 0
            || !_world.Database.Items.TryGetValue(removedItemId, out var item)
            || !_world.UnstockGangShopItem(shop.Number, removedItemId))
        {
            await _client.SendLineAsync("This item is not currently in stock.");
            return;
        }

        AddItemToInventory(removedItemId);
        RecalcEquipment();
        _world.PersistGangShop(shop.Number);

        await _client.SendLineAsync($"You remove {item.Name} from the shops stock.");
        _world.BroadcastToRoom(
            _player.CurrentMapNumber,
            _player.CurrentRoomNumber,
            $"You see {_player.Name} remove a {item.Name} from the shops stock.",
            _client);
    }

    // ---- MARKUP ---------------------------------------------------------------------------------

    private async Task HandleMarkup(string args)
    {
        if (!TryGetGangShopForManagement("MARKUP", "set the markup value for", out var shop, out _, out string error))
        {
            await _client.SendLineAsync(error);
            return;
        }

        if (string.IsNullOrWhiteSpace(args))
        {
            await _client.SendLineAsync("Syntax: MARKUP {percentage value}");
            return;
        }

        // Parsed as an integer; negatives clamp to 0, values >= 1000 clamp to 1000.
        long parsed = long.TryParse(args.Trim(), NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v) ? v : 0;
        int markup = (int)Math.Clamp(parsed, 0, GameWorld.GangShopMaxMarkupPercent);

        _world.SetGangShopMarkup(shop.Number, markup);
        _world.PersistGangShop(shop.Number);

        await _client.SendLineAsync($"New gang shop markup value set to {markup} percent.");
    }

    // ---- BUY (gang-shop branch of HandleBuy) ----------------------------------------------------

    private async Task BuyFromGangShopAsync(Shop shop, string target)
    {
        int wantedItemId = _world.ResolveGangShopItemByName(shop.Number, target, out var ambiguousStock);
        if (ambiguousStock != null)
        {
            await ShowItemDisambiguationAsync(ambiguousStock);
            return;
        }
        if (!_world.TryFindGangShopPurchase(shop.Number, wantedItemId, out int slotIndex, out var slot) ||
            !_world.Database.Items.TryGetValue(slot.ItemId, out var item))
        {
            await _client.SendLineAsync($"You cannot buy {target} here!");
            return;
        }

        int markup = _world.GetGangShopMarkup(shop.Number);
        long copper = ComputeGangShopBuyCopper(_player.Charm, markup, slot.Price, slot.Currency);

        if (copper > 0 && !PlayerHasCurrency(copper))
        {
            await _client.SendLineAsync($"You cannot afford {item.Name}.");
            return;
        }

        if (WouldExceedEncumbranceAfterPurchaseCopper(item, copper))
        {
            await _client.SendLineAsync("You cannot carry that much!");
            return;
        }

        // Decrement the slot first (atomic) so two simultaneous buyers can't both take the last unit.
        if (!_world.ConsumeGangShopSlot(shop.Number, slotIndex, slot.ItemId))
        {
            await _client.SendLineAsync($"You cannot buy {item.Name} here!");
            return;
        }

        if (copper > 0)
            DeductPlayerCurrency(copper);
        AddItemToInventory(item.Number);
        RecalcEquipment();

        // Takings flow to the owning gang leader's bank.
        _world.DepositGangShopRevenue(shop.Number, copper);
        _world.PersistGangShop(shop.Number);

        string priceText = copper <= 0
            ? "nothing"
            : $"{copper} copper {(copper == 1 ? "farthing" : "farthings")}";
        await _client.SendLineAsync($"You just bought {item.Name} for {priceText}.");
        _world.BroadcastToRoom(
            _player.CurrentMapNumber,
            _player.CurrentRoomNumber,
            $"You see {_player.Name} buy a {item.Name}.",
            _client);
    }

    // Display price (LIST): the per-slot price scaled by the shop markup, shown in the slot's
    // currency unit. No Charm term — stock omits it from the listing (only the buy applies Charm).
    internal static int ComputeGangShopDisplayPrice(int markupPercent, int slotPrice)
    {
        if (slotPrice <= 0)
            return 0;
        int markup = Math.Clamp(markupPercent, 0, GameWorld.GangShopMaxMarkupPercent);
        long price = (markup + 100L) * slotPrice / 100;
        return (int)Math.Clamp(price, 0, int.MaxValue);
    }

    // The gang-shop buy: convert the per-slot price into copper via its currency, then
    // apply the same Charm/markup curve normal shops use — price = ((110 - Chm/5) * ((markup+100) *
    // copperBase / 100)) / 100. Unlike normal shops there is NO minimum-markup floor (the owner sets
    // the markup outright). 64-bit math throughout, so no >100000 overflow scaling is needed.
    internal static long ComputeGangShopBuyCopper(int charm, int markupPercent, int slotPrice, int currencyIndex)
    {
        if (slotPrice <= 0)
            return 0;
        long copperBase = (long)slotPrice * GetShopCurrencyDenominationMultiplier(currencyIndex);
        int markup = Math.Clamp(markupPercent, 0, GameWorld.GangShopMaxMarkupPercent);
        long inner = (markup + 100L) * copperBase / 100;
        long price = (110L - charm / 5) * inner / 100;
        return Math.Max(0, price);
    }

    // ---- shared validation ----------------------------------------------------------------------

    /// <summary>Resolve the current room's gang shop for an owner-management command, enforcing the
    /// Stock gates: in a shop room, the shop is gang-owned (type 11), and the player carries the
    /// house's controller item (ability 184). On failure returns the matching stock refusal string.</summary>
    private bool TryGetGangShopForManagement(string commandName, string actionWord, out Shop shop, out int houseId, out string error)
    {
        shop = null!;
        houseId = 0;
        error = string.Empty;

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        // STOCK/UNSTOCK/MARKUP each test the room class before reading the shop number.
        if (room == null || !room.IsShopRoom || room.Shop <= 0 || !_world.Database.Shops.TryGetValue(room.Shop, out var currentShop))
        {
            error = $"You cannot {commandName} if you are not in your gangs shop!";
            return false;
        }

        if (currentShop.ShopType != GameWorld.GangShopType)
        {
            error = "This is not a gang owned shop.";
            return false;
        }

        houseId = room.GangHouseId;
        if (!_world.PlayerControlsGangShop(_player, houseId))
        {
            error = $"You do not have the correct item to {actionWord} this shop.";
            return false;
        }

        shop = currentShop;
        return true;
    }

    private static bool TryMatchCurrencyWord(string token, out int currencyIndex)
    {
        currencyIndex = token.ToLowerInvariant() switch
        {
            "copper" => 0,
            "silver" => 1,
            "gold" => 2,
            "platinum" => 3,
            "runic" => 4,
            _ => -1,
        };
        return currencyIndex >= 0;
    }
}
