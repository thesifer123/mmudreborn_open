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
    private async Task HandleShopList()
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        // LIST tests the room CLASS before it ever reads the shop number —
        // a stray shop id on an ordinary room is not a shop. See Room.IsShopRoom.
        if (room == null || !room.IsShopRoom || room.Shop <= 0)
        {
            await _client.SendLineAsync("You cannot LIST if you are not in a shop!");
            return;
        }

        if (!_world.Database.Shops.TryGetValue(room.Shop, out var shop))
        {
            await _client.SendLineAsync("You cannot LIST if you are not in a shop!");
            return;
        }

        // A bank (ShopType 7) deals in deposits/loans, not stock — LIST shows the banking rates, not an
        // (always-empty) item table. Stock prints the rates as "??" (unimplemented interest), so we match.
        if (shop.ShopType == GameWorld.BankShopType)
        {
            await _client.SendLineAsync("Banking services:");
            await _client.SendLineAsync("Deposit Rate: ??");
            await _client.SendLineAsync("Loan Rate: ??");
            return;
        }

        const int itemColWidth = 29;
        const int qtyColWidth = 12;
        const int priceColWidth = 18;

        // The "for sale" header is printed LAZILY — only once the first
        // IN-STOCK slot is found — and ONLY slots whose current quantity is non-zero are listed.
        // So a shop with nothing in stock — a junkyard/Recycler where every slot is Max=0 (sell-only, qty 0),
        // or a normal shop momentarily sold out — prints NOTHING AT ALL: no header, no error, LIST just
        // returns. Build the visible rows first, then emit the header only if there is at least one.
        bool headerEmitted = false;
        async Task EmitHeaderOnceAsync()
        {
            if (headerEmitted)
                return;
            headerEmitted = true;
            await _client.SendLineAsync("The following items are for sale here:");
            await _client.SendLineAsync($"{MudAnsi.Green}{"Item",-itemColWidth}{MudAnsi.Cyan}{"Quantity",-qtyColWidth}Price{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.Cyan}{new string('-', itemColWidth + qtyColWidth + priceColWidth)}{MudAnsi.Reset}");
        }

        // Gang shops list their owner-stocked slots (per-slot price scaled by the shop markup) rather
        // than the static shop data, which is empty for them.
        if (shop.ShopType == GameWorld.GangShopType)
        {
            int markup = _world.GetGangShopMarkup(shop.Number);
            foreach (var slot in _world.SnapshotGangShopSlots(shop.Number))
            {
                if (slot.Quantity <= 0 || !_world.Database.Items.TryGetValue(slot.ItemId, out var slotItem))
                    continue;
                await EmitHeaderOnceAsync();
                int displayPrice = ComputeGangShopDisplayPrice(markup, slot.Price);
                string slotPriceText = displayPrice <= 0 ? "Free" : FormatShopCurrency(displayPrice, slot.Currency);
                string slotUseSuffix = GetShopUseSuffix(_player, slotItem);
                string slotPriceDisplay = string.IsNullOrEmpty(slotUseSuffix)
                    ? $"{slotPriceText,-priceColWidth}"
                    : $"{slotPriceText}{slotUseSuffix}";
                await _client.SendLineAsync(
                    $"{MudAnsi.Green}{slotItem.Name,-itemColWidth}{MudAnsi.Cyan}{slot.Quantity,-qtyColWidth}{slotPriceDisplay}{MudAnsi.Reset}");
            }
            return;
        }

        foreach (var shopItem in shop.Items)
        {
            // Only in-stock slots are listed (current qty != 0). A Max=0 sell-only slot boots at
            // Current=0 and never restocks, so it never appears in LIST — you can only sell it here.
            if (!_world.IsShopItemInStock(shopItem)
                || !_world.Database.Items.TryGetValue(shopItem.ItemId, out var item))
                continue;

            await EmitHeaderOnceAsync();
            int quantity = shopItem.Current; // stock prints the live current quantity, not the Max cap
            int displayPrice = ComputeShopListDisplayPrice(shop.MarkupPercent, item.Price);
            string priceText = FormatShopCurrency(displayPrice, item.Currency);

            string useSuffix = GetShopUseSuffix(_player, item);
            string priceDisplay = string.IsNullOrEmpty(useSuffix)
                ? $"{priceText,-priceColWidth}"
                : $"{priceText}{useSuffix}";
            await _client.SendLineAsync(
                $"{MudAnsi.Green}{item.Name,-itemColWidth}{MudAnsi.Cyan}{quantity,-qtyColWidth}{priceDisplay}{MudAnsi.Reset}");
        }
    }

    private async Task HandleBankBook()
    {
        // The bankbook display walks the user's stored BANKBOOK RECORDS and prints
        // a balance for each. A player with no records simply falls out of the loop having
        // printed NOTHING — there is no empty-handed line anywhere in stock, and "You have no funds
        // on deposit." was invented here.
        //
        // The record is what matters, not the balance: opening an account leaves a bankbook behind
        // permanently, so a bank you emptied still lists at zero ("On deposit: 0 copper farthings
        // [0.00 gold crowns]"). Keying off "balance > 0" hid exactly that case, which is the other
        // half of the report.
        foreach (var bank in _world.Database.Shops.Values
                     .Where(shop => shop.ShopType == GameWorld.BankShopType)
                     .Where(shop => _player.BankBalances.ContainsKey(shop.Number))
                     .OrderBy(shop => shop.Number))
        {
            await DisplayBankBalanceAsync(bank, includeZeroBalance: true);
        }
    }

    private async Task HandleDeposit(string args)
    {
        if (!TryGetCurrentBank(out var currentBank))
        {
            await _client.SendLineAsync("You cannot DEPOSIT if you are not in a bank!");
            return;
        }

        if (!TryParseBankTransferAmount(args, out long amount))
        {
            await _client.SendLineAsync("Syntax: DEPOSIT {Amount to deposit in copper}");
            return;
        }

        if (!PlayerHasCurrency(amount))
        {
            await _client.SendLineAsync("You do not have enough copper farthings.");
            return;
        }

        DeductPlayerCurrency(amount);
        _player.BankBalances[currentBank.Number] = checked(_player.BankBalances.GetValueOrDefault(currentBank.Number) + amount);

        await _client.SendLineAsync($"You deposit {amount.ToString(CultureInfo.InvariantCulture)} copper farthings.");
        _world.BroadcastToRoom(
            _player.CurrentMapNumber,
            _player.CurrentRoomNumber,
            $"You see {_player.Name} making a deposit.",
            _client);
    }

    private async Task HandleWithdraw(string args)
    {
        if (!TryGetCurrentBank(out var currentBank))
        {
            await _client.SendLineAsync("You cannot WITHDRAW if you are not in a bank!");
            return;
        }

        if (!TryParseBankTransferAmount(args, out long amount))
        {
            await _client.SendLineAsync("Syntax: WITHDRAW {Amount to withdraw in copper}");
            return;
        }

        long currentBalance = _player.BankBalances.GetValueOrDefault(currentBank.Number);
        if (currentBalance < amount)
        {
            await _client.SendLineAsync("You do not have enough copper farthings.");
            return;
        }

        // Emptying an account does NOT close it. The bankbook record persists once opened, which is
        // why a drained bank still reports "On deposit: 0 copper farthings" instead of vanishing from
        // the listing. Removing the key here erased the distinction between "never banked here" (no
        // output at all) and "banked here and withdrew it all" (a zero line).
        _player.BankBalances[currentBank.Number] = currentBalance - amount;

        CurrencyHelper.SetFromCopper(_player, checked(CurrencyHelper.ToCopper(_player) + amount));
        RecalcEquipment();

        await _client.SendLineAsync($"You withdraw {amount.ToString(CultureInfo.InvariantCulture)} copper farthings.");
        _world.BroadcastToRoom(
            _player.CurrentMapNumber,
            _player.CurrentRoomNumber,
            $"You see {_player.Name} making a withdrawal.",
            _client);
    }

    private static bool TryParseBankTransferAmount(string args, out long amount)
    {
        amount = 0;
        return long.TryParse(args.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out amount)
            && amount > 0;
    }

    private bool TryGetCurrentBank(out Shop bank)
    {
        bank = null!;

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null)
            return false;

        return TryGetBankInRoom(room, out bank);
    }

    private bool TryGetBankInRoom(Room room, out Shop bank)
    {
        bank = null!;

        // DEPOSIT / WITHDRAW gate on the room class first, exactly like LIST/BUY/SELL.
        if (!room.IsShopRoom || room.Shop <= 0)
            return false;

        if (!_world.Database.Shops.TryGetValue(room.Shop, out var currentBank) || currentBank.ShopType != GameWorld.BankShopType)
            return false;

        bank = currentBank;

        return true;
    }

    private async Task DisplayBankBalanceAsync(Shop bank, bool includeZeroBalance)
    {
        long balance = _player.BankBalances.GetValueOrDefault(bank.Number);
        if (!includeZeroBalance && balance <= 0)
            return;

        await _client.SendLineAsync($"Your balance at {bank.Name} (#{bank.Number.ToString(CultureInfo.InvariantCulture)}) is:");
        await _client.SendLineAsync(
            // The balance line formats "On deposit: %s %s [%s%s.%s %s%s]" and
            // threads colour through the varargs: slot 3 is ESC[1;37m and slot 6 is
            // ESC[0m. So the bracketed gold figure — integer, '.', fraction and the
            // space after it — is BRIGHT white, while the rest of the line inherits the prompt's
            // default text colour (the line is prefixed with the bare line preamble, with
            // no SGR of its own). We rendered the whole line uncoloured.
            $"On deposit: {balance.ToString(CultureInfo.InvariantCulture)} copper farthings " +
            $"[{MudAnsi.BrightWhite}{Player.FormatGoldCrownsFromCopper(balance)} {MudAnsi.Reset}gold crowns]");
    }

    // The healer branch (shop type 5): a Temple sells two services via
    // BUY. "healing" fully restores HP for (maxHP-curHP)*2 copper farthings. "curing"/"cure poison" cures
    // poison for 25 silver when poisoned (clears the poison level + its source spells), or charges a
    // 15-silver "you were not poisoned" diagnostic fee when the player isn't poisoned. Insufficient funds
    // refuses with no effect. (The typed word need only be a prefix of the service.)
    private const int HealerCurePoisonCostCopper = 25 * CurrencyHelper.CopperPerSilver;   // poisoned → cure
    private const int HealerNotPoisonedFeeCopper = 15 * CurrencyHelper.CopperPerSilver;   // not poisoned → diagnostic

    private async Task HandleHealerServiceAsync(string target)
    {
        if (IsHealerService(target, "healing"))
        {
            int cost = Math.Max(0, _player.MaxHP - _player.CurrentHP) * 2;
            if (!PlayerHasCurrency(cost))
            {
                await _client.SendLineAsync("You do not have sufficient funds for that service.");
                return;
            }

            CurrencyHelper.SetFromCopper(_player, CurrencyHelper.ToCopper(_player) - cost);
            _player.CurrentHP = _player.MaxHP;
            await _client.SendLineAsync($"You hand over {FormatCurrency(cost)} and all your wounds are healed!");
            return;
        }

        if (IsHealerService(target, "curing") || IsHealerService(target, "cure poison"))
        {
            bool poisoned = _player.PoisonLevel > 0;
            int cost = poisoned ? HealerCurePoisonCostCopper : HealerNotPoisonedFeeCopper;
            if (!PlayerHasCurrency(cost))
            {
                await _client.SendLineAsync("You do not have sufficient funds for that service.");
                return;
            }

            CurrencyHelper.SetFromCopper(_player, CurrencyHelper.ToCopper(_player) - cost);
            if (poisoned)
            {
                _player.PoisonLevel = 0;
                RemoveActiveSpellsBySource(_player, source => source.Abilities.ContainsKey(PoisonSpellAbilityId));
                await _client.SendLineAsync($"You hand over {FormatCurrency(cost)} and your poisoning is cured!");
            }
            else
            {
                await _client.SendLineAsync($"You hand over {FormatCurrency(cost)} and find that you were not poisoned!");
            }
            return;
        }

        await _client.SendLineAsync("The healer offers BUY HEALING and BUY CURE POISON.");
    }

    // Prefix match: the typed word is a (case-insensitive) prefix of the service keyword (so "heal" hits
    // "healing", "cure" hits "curing"). Empty input matches nothing.
    private static bool IsHealerService(string target, string keyword)
        => target.Length > 0 && keyword.StartsWith(target, StringComparison.OrdinalIgnoreCase);

    private async Task HandleBuy(string target)
    {
        if (string.IsNullOrEmpty(target))
        {
            await _client.SendLineAsync("Syntax: BUY {item}");
            return;
        }

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null || !room.IsShopRoom || room.Shop <= 0)
        {
            await _client.SendLineAsync("You cannot BUY if you are not in a shop!");
            return;
        }

        if (!_world.Database.Shops.TryGetValue(room.Shop, out var shop))
        {
            await _client.SendLineAsync("You cannot BUY if you are not in a shop!");
            return;
        }

        if (shop.ShopType == GameWorld.GangShopType)
        {
            await BuyFromGangShopAsync(shop, target);
            return;
        }

        if (shop.ShopType == GameWorld.HealerShopType)
        {
            await HandleHealerServiceAsync(target);
            return;
        }

        // QOL bulk count: "buy 5 torch". Parsed here, AFTER the gang-shop and healer dispatches, so those
        // paths still see the raw line and a "buy 10 healing" cannot silently collapse into one healing.
        int buyQuantity = 1;
        if (TryParseBulkQuantity(target, out int parsedBuyQuantity, out string buyTargetName))
        {
            buyQuantity = parsedBuyQuantity;
            target = buyTargetName;
        }

        if (!TryResolveUniqueShopItem(target, shop, out var shopItem, out var buyItem, out var ambiguousNames))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return;
            }

            await _client.SendLineAsync($"{target} is not a known item.");
            return;
        }

        // Deed shop (type 12): buying a gang-house deed is gated like the stock buy path —
        // gang leaders only, one house per gang, and the target house must be unowned.
        int deedHouseId = shop.ShopType == GameWorld.DeedShopType
            ? buyItem.Abilities.GetValueOrDefault(GameWorld.GangHouseDeedAbilityId)
            : 0;
        if (deedHouseId > 0)
        {
            string? refusal = ValidateDeedPurchase(deedHouseId);
            if (refusal != null)
            {
                await _client.SendLineAsync(refusal);
                return;
            }
        }

        // A gang house is one per gang, so a count on a deed is meaningless — buy exactly one.
        if (deedHouseId > 0)
            buyQuantity = 1;

        int boughtCount = 0;
        long boughtTotalCopper = 0;
        // The buy prints the price by handing it to the currency deduction with its print flag set:
        //     print "You just bought %s for "; deduct(price, printing); print "."
        // The deduction counts the coins it actually REMOVES from the purse (one counter
        // per denomination) and prints those counters through the
        // canonical currency names, comma-joined — it never re-splits the total. So the line names the
        // coins that left the purse, which is NOT the same number as the total re-denominated: paying
        // 6000 gold reads "6000 gold crowns", never the equivalent "60 platinum pieces".
        // We snapshot the purse and report its NET change for the whole run, which is also what makes
        // a bulk "buy 10 rope" name one correct total rather than ten separate lines. Net rather than
        // per-take because our spend is deliberately denomination-PRESERVING (ascending, with one
        // coin broken for change) instead of the stock largest-first greedy, so the intermediate
        // churn — spend 3 copper, take 3 copper back as change — is an artifact, not a payment.
        long[] purseBeforeBuy = GetPlayerCurrencyCountsAscending(_player);
        string? buyStopMessage = null;
        for (int pass = 0; pass < buyQuantity; pass++)
        {
            // A finite slot at 0 qty isn't a buy candidate, so the player falls through to
            // the "You cannot buy %s here." branch (the same string stock prints for an unstocked item).
            if (!_world.IsShopItemInStock(shopItem))
            {
                // Running the shelf dry mid-run needs no words — the summary carries the count. The
                // refusal is only for a run that bought nothing at all (stock's own behavior).
                if (boughtCount == 0)
                    buyStopMessage = $"You cannot buy {buyItem.Name} here!";
                break;
            }

            long price = GetShopPriceCopper(shop, buyItem);

            if (price > 0 && !PlayerHasCurrency(price))
            {
                buyStopMessage = $"You cannot afford {buyItem.Name}.";
                break;
            }

            if (WouldExceedEncumbranceAfterPurchaseCopper(buyItem, price))
            {
                buyStopMessage = "You cannot carry that much!";
                break;
            }

            if (price > 0 && !TrySpendPlayerCurrencyPreservingDenominations(_player, price))
            {
                buyStopMessage = $"You cannot afford {buyItem.Name}.";
                break;
            }

            AddItemToInventory(buyItem.Number);
            _world.ConsumeShopStock(shopItem);   // the slot quantity decrements
            RecalcEquipment();
            boughtTotalCopper += price;
            boughtCount++;
        }

        if (boughtCount > 0)
        {
            // One line for the run, naming the coins the whole run actually cost — the single-purchase
            // line unchanged when a run bought one.
            string buyPriceText = boughtTotalCopper <= 0
                ? "nothing"
                : FormatSpentCurrencyText(purseBeforeBuy, GetPlayerCurrencyCountsAscending(_player), boughtTotalCopper);
            await _client.SendLineAsync(
                $"You just bought {CountedItemText(boughtCount, buyItem.Name)} for {buyPriceText}.");
        }

        if (buyStopMessage != null)
            await _client.SendLineAsync(buyStopMessage);

        if (deedHouseId > 0 && boughtCount > 0)
        {
            // Record gang ownership of the house (the deed in inventory drives the in-house scripts).
            _world.AssignGangHouse(deedHouseId, _player.Gang, _player.Name, DateTime.UtcNow);
            await _client.SendLineAsync(
                $"{MudAnsi.BrightGreen}Your gang now owns the {GameWorld.GangHouseColorName(deedHouseId)} Gang House! " +
                $"USE the deed to receive your keys and emblem.{MudAnsi.Reset}");
        }
    }

    /// <summary>Gate a gang-house deed purchase (shop type 12). Returns a refusal
    /// message, or null when the purchase is allowed.</summary>
    private string? ValidateDeedPurchase(int houseId)
    {
        if (string.IsNullOrWhiteSpace(_player.Gang) ||
            !_world.PlayerRepo.IsGangLeader(_player.Name, _player.Gang))
        {
            return "You must be a gang leader to purchase a gang house deed.";
        }

        if (_world.GangHouseMinimumExperience > 0 && _player.Experience < _world.GangHouseMinimumExperience)
        {
            return $"You are not experienced enough to own a gang house! Required experience: {_world.GangHouseMinimumExperience}";
        }

        if (_world.IsGangHouseLockedOut(_player.Gang))
        {
            return "Due to outstanding paper-work we are unable to provide you with another gang house deed at this time.";
        }

        if (_world.GetGangOwnedHouseId(_player.Gang) > 0)
        {
            return "You are already the owner of a gang house.";
        }

        if (_world.IsGangHouseOwned(houseId))
        {
            return $"The {GameWorld.GangHouseColorName(houseId)} Gang House is already owned by another gang.";
        }

        return null;
    }

    private async Task HandleSell(string target)
    {
        // QOL bulk count: "sell 100 oaken staff" sells copies one at a time until the pack runs out.
        int sellQuantity = 1;
        if (TryParseBulkQuantity(target, out int parsedSellQuantity, out string sellTargetName))
        {
            sellQuantity = parsedSellQuantity;
            target = sellTargetName;
        }

        int soldCount = 0;
        long soldTotalCopper = 0;
        string soldName = string.Empty;
        string? sellStopMessage = null;
        for (int pass = 0; pass < sellQuantity; pass++)
        {
            // Re-resolved every pass: each sale removes an entry and renumbers the inventory beneath it.
            if (!TryGetShopSaleCandidate(target, "SELL", "sell", requireShopRoom: true, out var saleMatch, out var item, out string errorMessage, out var ambiguousNames))
            {
                if (ambiguousNames != null)
                {
                    await ShowItemDisambiguationAsync(ambiguousNames);
                    return;
                }

                // Selling the last copy mid-run needs no words — the summary carries the count. A run
                // that sold nothing prints the stock refusal, so a plain SELL is what it always was.
                if (soldCount == 0)
                    sellStopMessage = errorMessage;
                break;
            }

            long sellPriceCopper = GetSellPriceCopper(item);
            TryRemoveResolvedCarriedItem(saleMatch, out _);
            _world.RemoveItemRuntimeState(saleMatch.InstanceId);
            CurrencyHelper.SetFromCopper(_player, CurrencyHelper.ToCopper(_player) + sellPriceCopper);
            RecalcEquipment();
            soldTotalCopper += sellPriceCopper;
            soldName = item.Name;
            soldCount++;
        }

        if (soldCount > 0)
        {
            // One line for the run, with the running total taken — identical to the stock line when a
            // run sold one, since both the name and the payout collapse to the single-sale values.
            await _client.SendLineAsync(
                $"You sold {CountedItemText(soldCount, soldName)} for {FormatCopperFarthings(soldTotalCopper)}.");
        }

        if (sellStopMessage != null)
            await _client.SendLineAsync(sellStopMessage);
    }

    /// <summary>
    /// APPRAISE. Returns false when the command is NOT handled, which the dispatcher turns into the
    /// ordinary unhandled-line path (room action / direct spell / social / "Your command had no effect.").
    ///
    /// APPRAISE is unusually terse — it has no message of its own, and the string table
    /// carries no "Syntax: APPRAISE" line to go with the BUY/SELL ones:
    ///     if (no argument) return;                                      // bare APPRAISE: silent
    ///     look up the room;
    ///     if (the room carries a shop number) { appraise against that shop; return; }
    ///     return 0;                                                        // no shop number: silent
    /// Both zero-returns fall through rather than printing, so neither an invented syntax line nor a
    /// "not in a shop" refusal belongs here. Note the room-CLASS test the other shop verbs run
    /// == 1) is absent: appraising works wherever a shop number is attached, shop room or not.
    /// </summary>
    private async Task<bool> HandleAppraise(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return false;

        var appraisalRoom = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (appraisalRoom == null || appraisalRoom.Shop <= 0)
            return false;

        if (!TryGetShopSaleCandidate(target, "APPRAISE", "appraise", requireShopRoom: false, out _, out var item, out string errorMessage, out var ambiguousNames))
        {
            if (ambiguousNames != null)
            {
                await ShowItemDisambiguationAsync(ambiguousNames);
                return true;
            }

            await _client.SendLineAsync(errorMessage);
            return true;
        }

        await _client.SendLineAsync($"You would get {FormatSellPriceText(item)} for your {item.Name}.");
        return true;
    }

    private bool TryGetShopSaleCandidate(
        string target,
        string commandName,
        string actionName,
        bool requireShopRoom,
        out CarriedItemMatch saleMatch,
        out Item item,
        out string errorMessage,
        out IReadOnlyList<string>? ambiguousNames)
    {
        saleMatch = default;
        item = null!;
        errorMessage = string.Empty;
        ambiguousNames = null;

        if (string.IsNullOrEmpty(target))
        {
            errorMessage = $"Syntax: {commandName} {{item}}";
            return false;
        }

        // SELL gates on the room class like the rest of the shop verbs; APPRAISE
        // is the one that does NOT — it reads the shop number straight off the room, so
        // appraising still works wherever a shop id happens to be attached.
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null || room.Shop <= 0 || (requireShopRoom && !room.IsShopRoom))
        {
            errorMessage = $"You cannot {commandName} if you are not in a shop!";
            return false;
        }

        // Gang shops (ShopType 11) refuse sales with this message; the
        // type-12 deed/fence shop is NOT gang and falls through to the normal stock check.
        if (!_world.Database.Shops.TryGetValue(room.Shop, out var shop) || shop.ShopType == 11)
        {
            errorMessage = "You may not sell items to a gang shop.";
            return false;
        }

        // SYSOP CONFIGURE SELLWORN ON is stock (stock SELL looks the name up across EVERY carried item, worn
        // included, so worn gear can be sold and counts toward "be more specific"). OFF — the default — keeps
        // worn gear out of the lookup entirely.
        bool sellWorn = _world.SellWornEnabled;
        if (!TryResolveUniqueCarriedItem(target, includeEquipped: sellWorn, out var carriedItem, out ambiguousNames))
        {
            if (ambiguousNames != null)
                return false;

            errorMessage = $"You don't have {target} to {actionName}.";
            return false;
        }

        saleMatch = carriedItem;
        item = carriedItem.Item;
        int itemId = carriedItem.ItemId;

        // A cursed item (ability 82/83) the player is wearing can't be sold unless they carry another copy.
        if (sellWorn
            && (item.Abilities.ContainsKey(ItemCursedAbilityId) || item.Abilities.ContainsKey(ItemMajorCurseAbilityId))
            && _player.Equipment.Values.Contains(itemId)
            && _player.Inventory.Count(id => id == itemId) + _player.Equipment.Values.Count(id => id == itemId) < 2)
        {
            errorMessage = "You may not sell that item!";
            return false;
        }

        if (!shop.Items.Any(shopItem => shopItem.ItemId == itemId))
        {
            errorMessage = $"You cannot sell {item.Name} here.";
            return false;
        }

        return true;
    }

    private bool TryResolveUniqueShopItem(string target, Shop shop, out ShopItem shopItem, out Item item, out IReadOnlyList<string>? ambiguousNames)
    {
        shopItem = null!;
        item = null!;
        ambiguousNames = null;

        string trimmedTarget = target.Trim();
        var allMatches = shop.Items
            .Select(candidate => (ShopItem: candidate, Item: _world.Database.Items.GetValueOrDefault(candidate.ItemId)))
            .Where(candidate => candidate.Item != null)
            .Select(candidate => (candidate.ShopItem, Item: candidate.Item!))
            .ToList();

        // Stock shop-item lookup: an exact name wins ("potion" over "healing potion"); otherwise every
        // word-prefix match counts the same, so two different items are ambiguous.
        var matches = TargetNameMatcher.NarrowToExactOrAllMatches(allMatches, m => m.Item.Name, trimmedTarget);

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

        shopItem = matches[0].ShopItem;
        item = matches[0].Item;
        return true;
    }

    // Payout in COPPER. The item's price must be converted to copper BEFORE the Charm percentage is
    // applied — see ComputeShopSellPriceCopper.
    private long GetSellPriceCopper(Item item)
    {
        return ComputeShopSellPriceCopper(_player.Charm, GetShopCurrencyCopperValue(item.Price, item.Currency));
    }

    // Payout = ((Charm/2 + 25) * baseValue) / 100, where baseValue is the item
    // price ALREADY reduced to a single denomination:
    //     base   = the price converted to copper
    //     payout = (((Charm >> 1) + 25) * base) / 100
    // The conversion happens first, so the percentage is taken against the full copper value and the
    // single integer division loses at most a fraction of a copper.
    //
    // We used to scale the RAW price in its own denomination and convert afterwards, which threw the
    // whole payout away on cheap items: a hard leather helm is Price 2 / Currency 2 (gold), so at
    // Charm 30 we computed (15+25)*2/100 = 0 and paid "0 copper farthings" for an item that had just
    // cost 4 gold. Correct order gives (15+25)*200/100 = 80 copper. Selling is purely Charm-based —
    // no shop markup term — and is neutral at Charm 50 (half value).
    internal static long ComputeShopSellPriceCopper(int charm, long baseValueCopper)
    {
        if (baseValueCopper <= 0)
            return 0;

        long payout = (charm / 2 + 25) * baseValueCopper / 100;
        return Math.Max(0, payout);
    }

    // SELL and APPRAISE quote the payout as a flat copper count — NOT broken into denominations.
    // The payout is named with denomination 0 (copper
    // farthings) and printed beside it, so a 90-copper payout reads "90 copper
    // farthings" even though that is 9 silver. Confirmed on a stock server: "You would get 90 copper
    // farthings for your hard leather helm." We were denominating it as "9 silver nobles" — the same
    // value, but not the same line. BUY is different and does denominate ("4 gold crowns, 8 copper
    // farthings"), so the two paths format deliberately differently.
    private string FormatSellPriceText(Item item)
    {
        return FormatCopperFarthings(GetSellPriceCopper(item));
    }

    private static string FormatCopperFarthings(long copper)
    {
        long amount = Math.Max(0, copper);
        return $"{amount} copper farthing{(amount == 1 ? string.Empty : "s")}";
    }

    /// <summary>
    /// The LIST price — what the shop's board says an item costs, NOT what it will cost *you*.
    /// Each row formats as "%s%-29.29s %s%-8d %5d %-8s":
    ///     price    = item.Price * (shop.Markup + 100) / 100
    ///     currency = currencyNames[item.Currency]
    /// Integer arithmetic in the item's OWN denomination, printed as ONE number plus ONE currency
    /// name. Price 0 uses a sibling format and prints "Free".
    ///
    /// Two things the listing deliberately does NOT do, both of which we used to do here:
    ///   * No Charm term. The Charm curve ((110 - Chm/5) ...) lives in the buy path, not in
    ///     the display. A shop board shows one price to everyone who reads it.
    ///   * No denomination breakdown. Stock never converts to copper and re-splits, so it shows
    ///     "16 gold crowns", never "16 gold crowns, 3 silver nobles, 2 copper farthings".
    /// The gang-shop branch of the same routine already followed this rule via
    /// ComputeGangShopDisplayPrice; normal shops were routed through the BUY formula by mistake.
    ///
    /// NB: this uses the shop's RAW markup, exactly as stock does — MinimumShopMarkupPercent (our
    /// NMR anti-arbitrage floor) applies to the buy/sell/appraise paths only. On the 52 stock shops
    /// with Markup 0 the board therefore quotes the base price while a purchase charges the floored
    /// rate; that gap is the floor's, not the listing's.
    /// </summary>
    internal static int ComputeShopListDisplayPrice(int markupPercent, int itemPrice)
    {
        if (itemPrice <= 0)
            return 0;

        long price = (Math.Max(0, markupPercent) + 100L) * itemPrice / 100;
        return (int)Math.Clamp(price, 0, int.MaxValue);
    }

    // Buy price in COPPER — the item's price is converted before the Charm/markup scaling, per stock.
    private long GetShopPriceCopper(Shop shop, Item item)
    {
        return ComputeShopBuyPriceCopper(
            _player.Charm, shop.MarkupPercent, GetShopCurrencyCopperValue(item.Price, item.Currency));
    }

    // Realm-wide minimum shop markup. NMR fix for "Orfero's Shop bug" (unfixed in stock 1.00x/1.11p):
    // a 0%-markup shop lets a max-Charm elf bard (blessed with Beauty) buy low and sell high on the
    // same item for a steady profit. Flooring every shop's markup at 25% removes the arbitrage. We
    // deliberately do NOT reproduce the stock bug.
    internal const int MinimumShopMarkupPercent = 25;

    // The price is computed against the item's COPPER value — the conversion
    // runs first and the result is charged from the copper slot:
    //     base  = the price converted to copper
    //     price = ((110 - Charm/5) * (((markup + 100) * base) / 100)) / 100
    //     then charged from the copper slot
    // i.e. price = ((110 - Charm/5) * ((markup+100) * baseValueCopper / 100)) / 100. Higher Charm
    // lowers the price, the shop's markup raises it, and it is neutral at Charm 50 (110 - 50/5 = 100).
    //
    // Scaling the RAW price in its own denomination instead threw away the sub-denomination
    // remainder: a hard leather helm (Price 2 / Currency 2 = 200 copper) at Charm 40 in a 100%-markup
    // shop is (110-8) * ((200*200)/100) / 100 = 408 copper — stock charges "4 gold crowns, 8 copper
    // farthings", while we truncated to 4 gold flat. Same class of bug as the sell side.
    // 64-bit math means we never need the stock >100000 overflow-scaling and impose no value cap.
    internal static long ComputeShopBuyPriceCopper(int charm, int markupPercent, long baseValueCopper)
    {
        if (baseValueCopper <= 0)
            return 0;

        int markup = Math.Max(MinimumShopMarkupPercent, markupPercent);
        long inner = (markup + 100) * baseValueCopper / 100;   // ((markup+100) * val) / 100
        long price = (110 - charm / 5) * inner / 100;          // ((110 - Chm/5) * inner) / 100
        return Math.Max(0, price);
    }

    private string FormatShopCurrency(int amount, int currencyIndex)
    {
        if (amount <= 0)
            return "Free";

        return currencyIndex switch
        {
            4 => $"{amount} runic {(amount == 1 ? "coin" : "coins")}",
            3 => $"{amount} platinum {(amount == 1 ? "piece" : "pieces")}",
            2 => $"{amount} gold {(amount == 1 ? "crown" : "crowns")}",
            1 => $"{amount} silver {(amount == 1 ? "noble" : "nobles")}",
            _ => $"{amount} copper {(amount == 1 ? "farthing" : "farthings")}",
        };
    }

    private static long GetShopCurrencyCopperValue(int amount, int currencyIndex)
    {
        if (amount <= 0)
            return 0;

        return (long)amount * GetShopCurrencyDenominationMultiplier(currencyIndex);
    }

    private static long GetShopCurrencyDenominationMultiplier(int currencyIndex)
    {
        return currencyIndex switch
        {
            4 => CurrencyHelper.CopperPerRunic,
            3 => CurrencyHelper.CopperPerPlatinum,
            2 => CurrencyHelper.CopperPerGold,
            1 => CurrencyHelper.CopperPerSilver,
            _ => 1,
        };
    }

    private string GetShopUseSuffix(Player player, Item item)
    {
        if (TryGetScrollSpell(item, out var spell) && IsSpellScrollItem(item))
        {
            if (CanPlayerMemorizeSpell(player, spell))
                return string.Empty;

            return CanPlayerMemorizeSpell(player, spell, ignoreRequiredLevel: true)
                ? " (Too powerful)"
                : " (You can't use)";
        }

        if (CanPlayerUseItem(player, item))
            return string.Empty;

        return IsPlayerBlockedOnlyByItemMinimumLevel(player, item)
            ? " (Too powerful)"
            : " (You can't use)";
    }

    private bool IsPlayerBlockedOnlyByItemMinimumLevel(Player player, Item item)
    {
        return IsPlayerBelowItemMinimumLevel(player, item)
            && CanPlayerUseItem(player, item, ignoreMinimumLevel: true);
    }

    private bool CanPlayerUseItem(Player player, Item item)
    {
        return CanPlayerUseItem(player, item, ignoreMinimumLevel: false);
    }

    // Ability slot indices sourced from the stock ability-name table and
    // confirmed by the Nightmare Redux ability.mdb export:
    //   28 "Magical"    - "Defines the magical potency of an item. Witchunters cannot use items with this flag."
    //   51 "Anti Magic" - The class/race flag that triggers that restriction (Witchunter has it in Classes."Abil-1").
    //   97 "Good Aligned"    - makes the item useable only by Good aligned characters.
    //   98 "Evil Aligned"    - makes the item useable only by Evil aligned characters.
    //   112 "Neutral Aligned" - makes the item useable only by Neutral aligned characters.
    private const int ItemMagicalAbilityId = 28;
    private const int ClassAntiMagicAbilityId = 51;
    private const int ItemGoodAlignedAbilityId = 97;
    private const int ItemEvilAlignedAbilityId = 98;
    private const int ItemNeutralAlignedAbilityId = 112;
    // Use-eligibility also gates the inverse abilities: 110 "Not Good Aligned"
    // (Good players can't use it) and 111 "Not Evil Aligned" (Evil players can't use it).
    // 113 "Not Neutral Aligned" exists in data (the star helm) but is never gated, so we
    // deliberately don't either — faithful to stock.
    private const int ItemNotGoodAlignedAbilityId = 110;
    private const int ItemNotEvilAlignedAbilityId = 111;
    // Cursed item flags:
    //   82 "Cursed"      - once worn, can't be removed.
    //   83 "Major Curse" - same as 82 plus does not drop on death (still unequipped on death).
    private const int ItemCursedAbilityId = 82;
    private const int ItemMajorCurseAbilityId = 83;
    // 100 "Loyal Item" - stays with the player even into death.
    // 138 "Visible Placed Item" - makes the item visible to the entire room.
    // 139 "Spell Immunity"     -
    //                            target immune to spells at or below the parameter level.
    // 158 "Required to hit"    -
    //                            monster requires an identical weapon/spell flag parameter to affect it.
    private const int ItemLoyalAbilityId = 100;
    // 155 "Death text block" - a carried
    // item caches this text-block id on death and replays it as a special command on revival.
    private const int ItemReviveTextBlockAbilityId = 155;
    private const int ItemVisiblePlacedAbilityId = 138;
    private const int DamageReducedByMagicResistanceAbilityId = 17;
    private const int HealingSpellAbilityId = 18;
    private const int PoisonSpellAbilityId = 19;
    private const int PoisonImmunityAbilityId = 21;
    // Ability 26 — identify/detect-magic: a spell cast ON A CARRIED ITEM that reads the item's aura
    // magnitude (ItemMagical 28) and narrates how much magic it holds.
    private const int IdentifySpellAbilityId = 26;
    // Abilities 74 / 75 — paralysis / disease status markers carried by hold/entangle/web/paralyze
    // spells. A target with the ward ability (81) is immune to spells carrying either marker.
    private const int ParalysisStatusAbilityId = 74;
    private const int DiseaseStatusAbilityId = 75;
    // Ability 23 — "Kill Dead": a spell carrying this (turn undead, control undead, disrupt, area
    // undead, exorcism, sunburst) affects ONLY monsters whose undead flag is set; on a non-undead
    // target stock prints "Your spell has no effect on <name>." and applies
    // nothing — even if the spell also carries a damage ability (turn undead carries ability 1).
    private const int KillDeadAbilityId = 23;
    private const int AnimalAbilityId = 78;
    private const int AffectsAnimalAbilityId = 80;
    private const int HasteSlowAbilityId = 87;   // haste/slow EU scaler
    private const int AffectsLivingAbilityId = 108;
    private const int NonLivingAbilityId = 109;
    private const int SpellStartMessageAbilityId = 120;
    private const int SpellImmunityAbilityId = 139;
    private const int NonMagicalSpellAbilityId = 144;
    private const int RequiredToHitAbilityId = 158;

    private bool CanPlayerUseItem(Player player, Item item, bool ignoreMinimumLevel)
    {
        if (!_world.Database.Classes.TryGetValue(player.ClassId, out var cls))
            return true;

        if (!ignoreMinimumLevel && IsPlayerBelowItemMinimumLevel(player, item))
            return false;

        // "Class Item Inclusion" (ability 59): each item slot
        // `Abil=59, AbilVal=<classId>` names a class that's permitted to equip the item even when
        // the standard weapon-type/armour-type checks would deny them. Example: dagger has 59:12 +
        // 59:15 so Mystic (whose WeaponType doesn't normally allow daggers) can equip it. There is
        // no dedicated call site because the use-eligibility check inlines the
        // scan: after the type gate lands on deny, it walks the 20 ability slots for ability 59 whose
        // parameter equals the player's class and flips the verdict back to allow. In stock that
        // rescue sits INSIDE the type-gate branch, so it cannot rescue a class-list mismatch; we
        // additionally let it rescue the class list, which differs only for item 687 "twisted bone
        // staff" (lists classes 5/12/13, ClassOk 12/5/15 - stock would bar Mystic).
        bool classOkPermits = item.ClassOkClassIds.Contains(player.ClassId);

        // Use-eligibility walks the class list (10 entries) and then the race
        // list (10 entries) BEFORE any type gate, and records in one flag whether
        // the player was NAMED by either list. Being named is an unconditional permit: the check
        // reads `if (named) { allow; } else { ...weapon/armour type gate... }`, so the
        // type gate is never evaluated for a class or race the item lists explicitly.
        //
        // Bug #233: item 634 "main-gauche" is ItemType 0 / ArmourType 6 / Worn 12 and lists class 7
        // (Ninja), whose class ArmourType is 2. Running the armour-weight comparison first denied the
        // Ninja an item the data explicitly grants them - and the same list also names Thief, Bard,
        // Gypsy and Missionary, whose class WeaponType 4 stock otherwise bars from Worn-12 off-hand
        // items entirely. The list IS the exemption; check it first and let a match short-circuit.
        var allowedClasses = item.ClassRestrictions.Where(v => v > 0).ToHashSet();
        bool namedByClassList = allowedClasses.Contains(player.ClassId);
        if (allowedClasses.Count > 0 && !namedByClassList && !classOkPermits)
            return false;

        var allowedRaces = item.RaceRestrictions.Where(v => v > 0).ToHashSet();
        bool namedByRaceList = allowedRaces.Contains(player.RaceId);
        if (allowedRaces.Count > 0 && !namedByRaceList)
            return false;

        if (!namedByClassList && !namedByRaceList && !classOkPermits)
        {
            if (!CanClassUseWeapon(cls, item))
                return false;

            if (item.ArmourType > 0 && cls.ArmourType > 0 && item.ArmourType > cls.ArmourType)
                return false;

            if (!CanClassUseOffHandSlot(cls, item))
                return false;
        }

        // Anti-magic classes (Witchunter) refuse any item flagged Magical (Abil 28 > 0).
        // Check the class definition directly so equipped Anti-Magic gear cannot retroactively
        // lock the wearer out of other magical items.
        if (cls.Abilities.ContainsKey(ClassAntiMagicAbilityId)
            && item.Abilities.GetValueOrDefault(ItemMagicalAbilityId) > 0)
        {
            return false;
        }

        // Alignment-restricted items (abilities 97/98/110/111/112).
        if (!PlayerAlignmentAllowsItem(item, CombatEngine.GetPlayerAlignment(player.EvilPoints)))
            return false;

        return true;
    }

    // The alignment slice of the use-eligibility check. Returns false when the player's Good/Neutral/Evil
    // bucket forbids the item: a Good-only (97) item rejects non-Good wearers, Evil-only (98) rejects
    // non-Evil, Neutral-only (112) rejects non-Neutral, Not-Good (110) rejects Good wearers, Not-Evil
    // (111) rejects Evil wearers. Classification: Good/Saint = Good, Seedy..FIEND = Evil, else Neutral.
    private static bool PlayerAlignmentAllowsItem(Item item, CombatEngine.PlayerAlignment alignment)
    {
        bool isGood = CombatEngine.IsGood(alignment);
        bool isEvil = CombatEngine.IsEvil(alignment);
        bool isNeutral = !isGood && !isEvil;

        if (item.Abilities.ContainsKey(ItemGoodAlignedAbilityId) && !isGood)
            return false;
        if (item.Abilities.ContainsKey(ItemEvilAlignedAbilityId) && !isEvil)
            return false;
        if (item.Abilities.ContainsKey(ItemNeutralAlignedAbilityId) && !isNeutral)
            return false;
        if (item.Abilities.ContainsKey(ItemNotGoodAlignedAbilityId) && isGood)
            return false;
        if (item.Abilities.ContainsKey(ItemNotEvilAlignedAbilityId) && isEvil)
            return false;

        return true;
    }

    // Re-check every worn item against the wearer's CURRENT
    // alignment and unequip each one their alignment no longer permits — move it to inventory, reverse
    // its instant equip abilities (e.g. the +HP on the serpent ring's ability 88), and recalc once.
    // Returns the display names of the items removed (in slot order) so the caller can announce them.
    // Mirrors ApplyRemoveCurseEffect's unequip mechanics; alignment-only because alignment is the only
    // equip requirement an evil-point change can flip (level/class/race/type are unaffected).
    private List<string> StripAlignmentDisallowedWornItems(Player target)
    {
        var alignment = CombatEngine.GetPlayerAlignment(target.EvilPoints);
        var removedNames = new List<string>();

        foreach (var slot in target.Equipment.Keys.ToList())
        {
            if (!target.Equipment.TryGetValue(slot, out var itemId)
                || !_world.Database.Items.TryGetValue(itemId, out var item))
                continue;
            if (PlayerAlignmentAllowsItem(item, alignment))
                continue;

            long instanceId = GetEquipmentInstanceId(target, slot, itemId);
            target.Equipment.Remove(slot);
            target.EquipmentInstanceIds.Remove(slot);
            AddItemToInventory(target, itemId, instanceId);
            ApplyEquipInstantAbilities(target, item, equipping: false);
            removedNames.Add(item.Name);
        }

        if (removedNames.Count > 0)
            target.RecalculateEquipment(_world.Database);

        return removedNames;
    }

    // After an evil-point change the alignment band is re-derived and,
    // ONLY when the band changed, the worn items are re-validated. We mirror that — compare the
    // Good/Neutral/Evil bucket before vs after; if it crossed, strip the now-disallowed worn items and
    // tell the wearer "Your <item> has been removed." No-op when unchanged.
    internal void RevalidateWornItemsAfterAlignmentChange(Player target, int alignmentBucketBefore)
    {
        if (CombatEngine.GetAlignmentBucket(target.EvilPoints) == alignmentBucketBefore)
            return;

        var removed = StripAlignmentDisallowedWornItems(target);
        if (removed.Count == 0)
            return;

        foreach (var name in removed)
            _world.SendToPlayer(target.Name, $"Your {name} has been removed.", reprompt: true);

        _world.PlayerRepo.SavePlayer(target);
    }

    private static bool IsPlayerBelowItemMinimumLevel(Player player, Item item)
    {
        return item.MinimumLevel > 0 && player.Level < item.MinimumLevel;
    }

    private static bool CanClassUseWeapon(CharacterClass cls, Item item)
    {
        // Weapons ONLY. Use-eligibility branches its weapon/armour gate on the item's
        // ItemType: type 0 takes the armour branch, type 1 the weapon branch,
        // and every other type falls straight through ungated. Armour is handled
        // separately by the ArmourType comparison in CanPlayerUseItem.
        //
        // Type 5 — DRINKABLE — used to be gated here too, which silently barred Mage and Mystic (the two
        // classes with WeaponType 9) from drinking ANY potion: WeaponType 9 routes to IsStaffOrDagger,
        // which tests whether the item's NAME contains "staff" or "dagger", and no potion does. The
        // failure is invisible because EAT/DRINK return silently on an eligibility denial, so a
        // mage just saw nothing happen while other classes in the same room drank theirs fine. Not one of
        // the 34 type-5 items carries a WeaponType at all, so the gate could only ever deny.
        if (item.ItemType is not 1)
            return true;

        return cls.WeaponType switch
        {
            4 => item.WeaponType is 0 or 2, // Stock DAT/help evidence: any one-handed weapon.
            7 => item.WeaponType is 0 or 1, // Stock DAT/help evidence: blunt weapons.
            8 => true, // Stock DAT/help evidence: any weapon.
            9 => IsStaffOrDagger(item), // Stock DAT/help evidence: staff and dagger.
            _ => cls.WeaponType <= 0 || item.WeaponType == cls.WeaponType,
        };
    }

    // Wear location 12 - the off-hand slot.
    private const int OffHandWearLocation = 12;

    // The tail of the use-eligibility armour branch (ItemType 0). After the armour-weight
    // comparison stock switches on the CLASS WeaponType and, for cases 0, 2 and 4, refuses outright
    // when the item's wear location is 12:
    //
    //     case 0: case 2: case 4:
    //         if (wearLocation == 12) { deny; } else { allow; }
    //         break;
    //     default: allow;
    //
    // Only WeaponType 4 occurs among the 15 stock classes, so the rule lands on exactly five of them -
    // Missionary, Thief, Bard, Gypsy and Warlock - and bars them from the off-hand slot entirely,
    // regardless of the item's armour weight. It is reached only when the item does NOT name the class
    // or race (the permit above) and carries no ClassOk for them.
    //
    // Confirmed against stock: a Thief carrying a buckler shield (item 57, ArmourType 6 - inside the
    // Thief's own weight cap of 6, so the weight comparison passes) is refused with "You may not wear
    // that item!". That blanket refusal is precisely why every off-hand item built for these classes
    // names them explicitly instead of relying on weight: the bard instruments (silver flute, golden
    // harp, bardic lute, elven harp, blackwood harp, battlehorn) all list class 9, and the gypsy deck
    // of cards and brass zills list class 10. The class list is the carve-out from this rule.
    private static bool CanClassUseOffHandSlot(CharacterClass cls, Item item)
    {
        if (item.ItemType is not 0 || item.Worn != OffHandWearLocation)
            return true;

        return cls.WeaponType is not (0 or 2 or 4);
    }

    private static bool IsStaffOrDagger(Item item)
    {
        return item.Name.Contains("staff", StringComparison.OrdinalIgnoreCase)
            || item.Name.Contains("dagger", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWeaponHeavyForPlayer(Player player, Item item)
    {
        return item.IsWeapon && item.StrReq > player.Strength;
    }

    private async Task HandleHelp(string args)
    {
        var topic = args.Trim();
        string? resolvedTopic = null;

        // No topic = show the stock help topic MENU. In stock, bare HELP shows this menu;
        // HELP HELP (resolved as a normal topic below) shows the "how to use help" description held in
        // the "help"/"?" help-data entries -- so we must NOT short-circuit bare HELP to those here.
        if (string.IsNullOrEmpty(topic))
        {
            // Stock colouring: the HELP keyword and every topic name are bright yellow, NOTE: is cyan,
            // and the descriptions stay default white.
            string hy = MudAnsi.BrightYellow, rs = MudAnsi.Reset;
            await _client.SendLineAsync();
            await _client.SendLineAsync($"Type {hy}HELP{rs} followed by a topic for help on that topic");
            await _client.SendLineAsync($"({MudAnsi.BrightCyan}NOTE:{rs} If you are in the Help Sub-menu, there is no need to type {hy}HELP{rs}, just the topic will be fine)");
            await _client.SendLineAsync();

            (string Name, string Desc)[] menu =
            [
                ("Tips", "A few tips to help you get started."),
                ("Commands", "A list of commands available within the game"),
                ("Stats1", "An explanation of the statistics of your character"),
                ("Stats2", "A continuation of stats1, including help on allocating your stats"),
                ("Combat", "Everything you need to know about killing others"),
                ("Races", "A list of the various races"),
                ("Classes", "For help on a certain class, type Help <Classname>"),
                ("Commun", "Communicating with others in the realm"),
                ("Info", "A list of information commands, and how to use them"),
                ("Spells", "Everything you need to know about spellcasting"),
                ("Shops", "Buying and selling of items in the Realm"),
                ("Laws", "Before thinking of doing anything nasty, read this"),
                ("Movement", "How to travel throughout the Realm"),
                ("Party", "You have friends? Well here's how to use them"),
                ("Items", "Commands related to items within the game"),
                ("Help", "A quick description of how to use the help system"),
                ("Topics", "A list of all available topics"),
                ("Profile", "Setting up your personal options within the game"),
                ("Misc", "Miscellaneous commands"),
                ("Set", "Various toggleable profile options"),
            ];
            foreach (var (name, desc) in menu)
                await _client.SendLineAsync($"{hy}{name,-8}{rs} - {desc}");
            await _client.SendLineAsync();
            return;
        }

        // Special handling for "race" (singular) and "class" (singular)
        if (topic.Equals("race", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync();
            await _client.SendLineAsync("Type \x1b[1;33mHELP \x1b[36m<Race Name> \x1b[0mfor specific help on a race.");
            await _client.SendLineAsync();
            foreach (var race in _world.Database.Races.Values.OrderBy(r => r.Name))
                await _client.SendLineAsync(race.Name);
            await _client.SendLineAsync();
            return;
        }

        if (topic.Equals("class", StringComparison.OrdinalIgnoreCase))
        {
            await _client.SendLineAsync();
            await _client.SendLineAsync("Type \x1b[1;33mHELP \x1b[36m<class name> \x1b[0mfor specific help on a class.");
            await _client.SendLineAsync();
            await _client.SendLineAsync("Type \x1b[1;33mHELP CLASS POWERS \x1b[0mfor descriptions of the powers assigned to classes.");
            await _client.SendLineAsync();
            await _client.SendLineAsync("Type \x1b[1;33mHELP CLASS SKILLS1 \x1b[0mor \x1b[1;33mHELP CLASS SKILLS2 \x1b[0mfor descriptions of the skills");
            await _client.SendLineAsync("assigned to classes.");
            await _client.SendLineAsync();
            foreach (var cls in _world.Database.Classes.Values.OrderBy(c => c.Name))
                await _client.SendLineAsync(cls.Name);
            await _client.SendLineAsync();
            return;
        }

        if (topic.Equals("topics", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelpTopicsAsync();
            return;
        }

        // Look up the topic: exact match first, then prefix match (faithful to stock)
        string? body = HelpTopicRenderer.TryResolveTopic(_world.Database, topic, out var found, out resolvedTopic)
            ? found
            : null;

        if (body != null)
        {
            if (IsActionHelpTopic(resolvedTopic))
                body = AppendActionHelpList(body);

            await RenderHelpBodyAsync(body);
        }
        else
        {
            await _client.SendLineAsync($"No help available on '{topic}'.");
        }
    }

    private async Task HandleHelpExp()
    {
        var race = _world.Database.Races[_player.RaceId];
        var cls = _world.Database.Classes[_player.ClassId];
        int startLevel = Math.Max(1, _player.Level - 2);
        int endLevel = Math.Max(startLevel + 9, _player.Level + 7);
        await _client.SendLineAsync(MudAnsi.White + "The following is a table of experience for your character:" + MudAnsi.Reset);
        await _client.SendLineAsync(MudAnsi.White + "" + MudAnsi.Reset);
        await _client.SendLineAsync(MudAnsi.White + "Level   Experience" + MudAnsi.Reset);
        await _client.SendLineAsync(MudAnsi.White + "-----   ----------" + MudAnsi.Reset);
        for (int lvl = startLevel; lvl <= endLevel; lvl++)
        {
            long exp = Player.GetTotalExpForLevel(lvl, race.ExpTable, cls.ExpTable);
            await _client.SendLineAsync($"{MudAnsi.White}{lvl,5}   {exp,10}{MudAnsi.Reset}");
        }
        await _client.SendLineAsync(MudAnsi.White + "" + MudAnsi.Reset);
    }

    private async Task<bool> TryHandleQuestionMarkUtility(string args)
    {
        string topic = args.Trim();
        if (topic.Equals("exp", StringComparison.OrdinalIgnoreCase) ||
            topic.Equals("experience", StringComparison.OrdinalIgnoreCase))
        {
            await HandleHelpExp();
            return true;
        }

        var parts = topic.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0 && parts[0].Equals("expr", StringComparison.OrdinalIgnoreCase))
        {
            await HandleExperienceRateProjection(parts.Length > 1 ? parts[1] : string.Empty);
            return true;
        }

        return false;
    }

    private async Task HandleExperienceRateProjection(string args)
    {
        if (!long.TryParse(args.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out long rateKPerHour) ||
            rateKPerHour <= 0)
        {
            await _client.SendLineAsync($"{MudAnsi.White}Syntax: ? expr {{rate}}{MudAnsi.Reset}");
            return;
        }

        var race = _world.Database.Races[_player.RaceId];
        var cls = _world.Database.Classes[_player.ClassId];
        int startLevel = Math.Max(1, _player.Level - 5);
        int endLevel = Math.Max(startLevel + 14, _player.Level + 9);
        decimal experiencePerHour = rateKPerHour * 1000m;

        await _client.SendLineAsync(MudAnsi.White + "The following is a table of experience for your character:" + MudAnsi.Reset);
        await _client.SendLineAsync(MudAnsi.White + string.Empty + MudAnsi.Reset);
        await _client.SendLineAsync($"{MudAnsi.White}With a rate of: {rateKPerHour.ToString(CultureInfo.InvariantCulture)}k/hr{MudAnsi.Reset}");
        await _client.SendLineAsync(MudAnsi.White + string.Empty + MudAnsi.Reset);
        await _client.SendLineAsync(MudAnsi.White + "Level    Experience    Remaining    In Hours      In Days" + MudAnsi.Reset);
        await _client.SendLineAsync(MudAnsi.White + "-----    ----------    ---------    --------      -------" + MudAnsi.Reset);

        for (int lvl = startLevel; lvl <= endLevel; lvl++)
        {
            long exp = Player.GetTotalExpForLevel(lvl, race.ExpTable, cls.ExpTable);
            long remaining = Math.Max(0, exp - _player.Experience);
            decimal hours = remaining == 0 ? 0m : remaining / experiencePerHour;
            decimal days = hours / 24m;

            string formattedHours = FormatProjectionDuration(hours);
            string formattedDays = FormatProjectionDuration(days);

            await _client.SendLineAsync($"{MudAnsi.White}{lvl,5}    {exp,10}    {remaining,9}    {formattedHours,8}      {formattedDays,7}{MudAnsi.Reset}");
        }

        await _client.SendLineAsync(MudAnsi.White + string.Empty + MudAnsi.Reset);
    }

    private static string FormatProjectionDuration(decimal value)
    {
        decimal rounded = Math.Round(value, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0m)
            return "0";

        return rounded.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private async Task HandleQuit()
    {
        _world.PlayerRepo.SavePlayer(_player);
        await _client.SendLineAsync(MudAnsi.SystemMsg("Your character has been saved. Farewell!"));
    }

    private async Task HandleExperience()
    {
        var race = _world.Database.Races[_player.RaceId];
        var cls = _world.Database.Classes[_player.ClassId];
        long totalNeeded = _player.GetExpForNextLevel(race.ExpTable, cls.ExpTable);
        long remaining = Math.Max(0, totalNeeded - _player.Experience);
        int pct = totalNeeded > 0 ? (int)(_player.Experience * 100 / totalNeeded) : 100;
        await _client.SendLineAsync($"{MudAnsi.Green}Exp: {MudAnsi.Cyan}{_player.Experience}{MudAnsi.Green} Level: {MudAnsi.Cyan}{_player.Level}{MudAnsi.Green} Exp needed for next level: {MudAnsi.Cyan}{remaining} ({MudAnsi.Cyan}{totalNeeded}) [{MudAnsi.Cyan}{pct}%]{MudAnsi.Reset}");
    }

    // WCCMMHLP.MSG: "Displays your current Health and Mana Points."
    private async Task HandleHealth()
    {
        var cls = _world.Database.Classes[_player.ClassId];

        static int Percent(int current, int max)
        {
            return max <= 0 ? 0 : Math.Clamp(current * 100 / max, 0, 100);
        }

        string healthSegment = $"Health:    {_player.CurrentHP}/{_player.MaxHP}    [{Percent(_player.CurrentHP, _player.MaxHP)}%]";

        bool showResource = _player.MaxMana > 0 &&
            (Player.UsesSpellcasting(cls) || (Player.UsesKai(cls) && _player.Level >= 2));

        if (!showResource)
        {
            await _client.SendLineAsync(healthSegment);
            return;
        }

        string resourceLabel = Player.UsesKai(cls) ? "Kai" : "Mana";
        string resourceSegment = $"{resourceLabel}:  {_player.CurrentMana}/{_player.MaxMana}  [{Percent(_player.CurrentMana, _player.MaxMana)}%]";
        await _client.SendLineAsync($"{healthSegment}  {resourceSegment}");
    }

    private async Task<bool> TrainOneLevel(Shop trainerShop)
    {
        var race = _world.Database.Races[_player.RaceId];
        var cls = _world.Database.Classes[_player.ClassId];
        bool usesKai = Player.UsesKai(cls);
        HashSet<int> knownKaiPowersBeforeTraining = usesKai
            ? GetKnownKaiPowerSpellIds()
            : [];

        if (_player.Level >= 75)
        {
            await _client.SendLineAsync("You may not train any further.  Please contact your sysop");
            await _client.SendLineAsync("for appropriate access so that you may continue your training.");
            return false;
        }

        long needed = _player.GetExpForNextLevel(race.ExpTable, cls.ExpTable);
        if (_player.Experience < needed)
        {
            await _client.SendLineAsync("You do not have the required experience to train yet!");
            return false;
        }

        // Training pricing:
        //   baseCost = floor((level * 5) * (shopMarkup + 100) / 100)
        // The raw amount is then passed through currency helpers in a denomination slot,
        // so convert to our copper-farthing economy before affordability checks/deduction.
        int trainingCost = GetTrainingCostCopper(_player.Level, trainerShop.MarkupPercent);
        if (trainingCost > 0 && !PlayerHasCurrency(trainingCost))
        {
            await _client.SendLineAsync("You do not have the money required for your training.");
            return false;
        }

        if (trainingCost > 0)
            DeductPlayerCurrency(trainingCost);

        int hpGain = CharacterCreation.RollLevelUpHpGain(Random.Shared, _player.Health, _player.Level, race.HPPerLvl, cls.MinHits, cls.MaxHits);
        _player.Level++;
        _player.MaxHP = Math.Max(1, _player.MaxHP + hpGain);

        // CP gain bands from training.
        int cpGain = _player.Level <= 10
            ? 10
            : _player.Level <= 20
                ? 15
                : (((_player.Level - 1) / 10) * 5) + 10;
        _player.CharacterPoints += cpGain;

        // Training adds a fixed per-train life increment (5, hardcoded in
        // stock) and caps total lives at 9. Since characters start at 9, this only restores
        // lives after death has dropped them below the cap.
        const int livesPerTrain = 5;
        int oldLives = _player.Lives;
        _player.Lives = Math.Min(9, _player.Lives + livesPerTrain);
        int gainedLives = _player.Lives - oldLives;

        _player.RecalculateStats(race, cls, _world.Database);
        List<GameSpell> newlyLearnedKaiPowers = usesKai
            ? GetNewlyLearnedKaiPowers(knownKaiPowersBeforeTraining)
            : [];

        if (trainingCost > 0)
            await _client.SendLineAsync($"You hand over {FormatCurrency(trainingCost)} and you receive training to attain level {_player.Level}.");
        else
            await _client.SendLineAsync($"and you receive training to attain level {_player.Level}.");
        await _client.SendLineAsync("You receive the following:");
        await _client.SendLineAsync($"{hpGain} hit points");
        await _client.SendLineAsync($"{cpGain} additional character points");
        if (gainedLives > 0)
            await _client.SendLineAsync($"{gainedLives} additional lives");
        if (newlyLearnedKaiPowers.Count > 0)
        {
            await _client.SendLineAsync($"{MudAnsi.Magenta}You learn the following Kai abilities:{MudAnsi.Reset}");
            foreach (var spell in newlyLearnedKaiPowers)
                await _client.SendLineAsync($"{MudAnsi.Cyan}{spell.Name}{MudAnsi.Reset}");
        }

        return true;
    }

    private HashSet<int> GetKnownKaiPowerSpellIds()
    {
        return _world.Database.Spells.Values
            .Where(static spell => spell.Magery == Player.KaiMageryType)
            .Where(spell => _player.GetQuestAbilityValue(Player.GetLearnedSpellbookAbilityId(spell.Number)) > 0)
            .Select(spell => spell.Number)
            .ToHashSet();
    }

    private List<GameSpell> GetNewlyLearnedKaiPowers(IReadOnlySet<int> knownKaiPowerSpellIds)
    {
        return _world.Database.Spells.Values
            .Where(static spell => spell.Magery == Player.KaiMageryType)
            .Where(spell => _player.GetQuestAbilityValue(Player.GetLearnedSpellbookAbilityId(spell.Number)) > 0)
            .Where(spell => !knownKaiPowerSpellIds.Contains(spell.Number))
            .OrderBy(spell => spell.ReqLevel)
            .ThenBy(spell => spell.Number)
            .ToList();
    }

    private static int GetTrainingCostCopper(int currentLevel, int shopMarkupPercent)
    {
        int safeLevel = Math.Max(1, currentLevel);
        int safeMarkup = Math.Max(0, shopMarkupPercent);

        long baseCost = (safeLevel * 5L * (safeMarkup + 100L)) / 100L;

        // _train_level routes cost through a non-copper denomination slot; convert to copper.
        // Keep the same overall economy scale already used by command currency helpers.
        const long trainingUnitToCopper = 10L;
        long copperCost = baseCost * trainingUnitToCopper;
        return (int)Math.Clamp(copperCost, 0L, int.MaxValue);
    }

    /// <summary>
    /// Returns the opposite compass direction for arrival messages.
    /// Stock: "%s%s moves into the room from the %s."
    /// </summary>
    private static string GetOppositeDirection(string direction) => MudDirections.Opposite(direction);

    /// <summary>
    /// Format a copper amount into a stock-faithful currency string.
    /// Currency: copper farthings, silver nobles (=10 copper), gold crowns (=100 copper), platinum pieces (=1000 copper), runic coins (=10000 copper)
    /// </summary>
    // The coins a buy run actually cost, named the way the currency deduction names them.
    //
    // Prefer the coins that LEFT the purse — each denomination that went down — because that is what
    // the stock counters hold and it is the only rendering that can say "6000 gold crowns" instead of
    // the equivalent-but-different "60 platinum pieces". A denomination that went UP is change from a
    // broken coin, never a payment, so it contributes nothing.
    //
    // That view is only honest while it accounts for the whole price. Buy a torch out of a purse
    // holding nothing but platinum and the only decrease is one platinum piece, with the rest handed
    // back as change — reporting "1 platinum piece" for a few copper of torches. Stock does not do
    // that either: its take-loops break the platinum down first and land their counters on the small
    // coins, so the line quotes the real price. So when the coins that left do not add up to what was
    // paid, change was involved and we denominate the total instead — which is what stock prints in
    // exactly those cases.
    private static string FormatSpentCurrencyText(long[] purseBefore, long[] purseAfter, long totalCopper)
    {
        long spentCopper = Math.Max(0, purseBefore[0] - purseAfter[0]);
        long spentSilver = Math.Max(0, purseBefore[1] - purseAfter[1]);
        long spentGold = Math.Max(0, purseBefore[2] - purseAfter[2]);
        long spentPlatinum = Math.Max(0, purseBefore[3] - purseAfter[3]);
        long spentRunic = Math.Max(0, purseBefore[4] - purseAfter[4]);

        long spentValue = CurrencyHelper.ToCopper(spentRunic, spentPlatinum, spentGold, spentSilver, spentCopper);
        if (spentValue != totalCopper)
            return FormatCurrency((int)Math.Clamp(totalCopper, 0, int.MaxValue));

        return FormatCurrency(spentRunic, spentPlatinum, spentGold, spentSilver, spentCopper);
    }

    private static string FormatCurrency(int copperAmount)
    {
        var parts = FormatCurrencyParts(copperAmount);
        return parts.Count > 0 ? string.Join(", ", parts) : "0 copper farthings";
    }

    private static string FormatCurrency(long runic, long platinum, long gold, long silver, long copper)
    {
        var parts = FormatCurrencyParts(runic, platinum, gold, silver, copper);
        return parts.Count > 0 ? string.Join(", ", parts) : "0 copper farthings";
    }

    // Canonical plural denomination noun ("runic coins", "gold crowns", "copper farthings"), independent
    // of the abbreviation the player typed — used for the drop room-broadcast ("dropped some gold crowns").
    private static string CurrencyDenominationPluralName(long denominationMultiplier) => denominationMultiplier switch
    {
        CurrencyHelper.CopperPerRunic => "runic coins",
        CurrencyHelper.CopperPerPlatinum => "platinum pieces",
        CurrencyHelper.CopperPerGold => "gold crowns",
        CurrencyHelper.CopperPerSilver => "silver nobles",
        _ => "copper farthings",
    };

    private static string FormatCurrencyDenomination(long denominationMultiplier, long count)
    {
        return denominationMultiplier switch
        {
            CurrencyHelper.CopperPerRunic => FormatCurrency(count, 0, 0, 0, 0),
            CurrencyHelper.CopperPerPlatinum => FormatCurrency(0, count, 0, 0, 0),
            CurrencyHelper.CopperPerGold => FormatCurrency(0, 0, count, 0, 0),
            CurrencyHelper.CopperPerSilver => FormatCurrency(0, 0, 0, count, 0),
            1 => FormatCurrency(0, 0, 0, 0, count),
            _ => "0 copper farthings",
        };
    }

    /// <summary>Return individual denomination strings for a copper amount (e.g., ["3 gold crowns", "6 silver nobles"]).</summary>
    private static List<string> FormatCurrencyParts(int copperAmount)
    {
        if (copperAmount <= 0)
            return [];

        int remaining = copperAmount;

        int runic = remaining / CurrencyHelper.CopperPerRunic;
        remaining %= CurrencyHelper.CopperPerRunic;
        int platinum = remaining / CurrencyHelper.CopperPerPlatinum;
        remaining %= CurrencyHelper.CopperPerPlatinum;
        int gold = remaining / CurrencyHelper.CopperPerGold;
        remaining %= CurrencyHelper.CopperPerGold;
        int silver = remaining / CurrencyHelper.CopperPerSilver;
        int copper = remaining % CurrencyHelper.CopperPerSilver;

        return FormatCurrencyParts(runic, platinum, gold, silver, copper);
    }

    private static List<string> FormatCurrencyParts(long runic, long platinum, long gold, long silver, long copper)
    {
        var parts = new List<string>();

        if (runic > 0) parts.Add($"{runic} runic {(runic == 1 ? "coin" : "coins")}");
        if (platinum > 0) parts.Add($"{platinum} platinum {(platinum == 1 ? "piece" : "pieces")}");
        if (gold > 0) parts.Add($"{gold} gold {(gold == 1 ? "crown" : "crowns")}");
        if (silver > 0) parts.Add($"{silver} silver {(silver == 1 ? "noble" : "nobles")}");
        if (copper > 0) parts.Add($"{copper} copper {(copper == 1 ? "farthing" : "farthings")}");

        return parts;
    }

    public Task CheckEncounters()
    {
        // NOT gated on IsUnconscious. Going down ends YOUR fight, not the monsters' — the
        // monster attack guards only on the monster's own energy/timer, the room's
        // Protected bit and the known-monster lookup; there is NO player-HP check, so a mortally
        // wounded player keeps getting swung at. That is the same invariant 579807f restored for the
        // world-tick drop paths. Skipping registration here left a downed player who re-entered the
        // realm permanently untouchable: both the incoming-attacker list and NextMonsterAttackAtUtc
        // live only in memory, so a fresh login starts with an empty list and a MinValue deadline —
        // which drops them out of GatherMonstersWithCandidates entirely (it needs IsCombatBeatDue).
        // A conscious player self-heals from that on their next command, since this runs after every
        // one; an unconscious player can issue no commands, so nothing ever re-armed it.
        if (_player.PlayerCombatTarget != null)
            return Task.CompletedTask;

        var activeAttackers = GetActiveIncomingMonsterAttackers();
        bool hadActiveAttackers = activeAttackers.Count > 0;

        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        if (room == null) return Task.CompletedTask;

        // Monster→player attacks are absolutely forbidden in a
        // Protected/safe room (a hard gate with no override). Monsters — including
        // alignment guards (Align 4) and any aggressive mob that wandered in — never aggro a
        // player here, so they must not even register as incoming attackers. The GameSession
        // round-resolver also defers swings in a safe room as the authoritative backstop for any
        // monster that pursued the player in. See memory [[protected-room-combat-gate]].
        if (room.IsProtected)
            return Task.CompletedTask;

        if (_player.IsSysopInvisible || _player.IsSysopNoAggro || _player.IsOutOfRealm)
            return Task.CompletedTask;

        var monsters = _world.GetMonstersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        foreach (var monster in monsters)
        {
            if (monster.IsDead) continue;

            // Sneaking/hidden players are invisible to monsters
            // UNLESS the monster has ability 57 (See Hidden / Perception).
            if (_player.IsSneaking || _player.IsHidden)
            {
                if (!CombatEngine.MonsterCanSeeHidden(monster.Template))
                    continue; // monster can't see the sneaker
            }

            // Check if this monster should aggro based on alignment
            if (!CombatEngine.ShouldMonsterAggro(monster.Template, _player))
                continue;

            QueueMonsterAggro(monster, activeAttackers, ref hadActiveAttackers);
        }

        return Task.CompletedTask;
    }

    // A counter-attack against someone who already struck you is
    // self-defence — no evil points, and the opening-strike window is NOT (re)opened on you. This is
    // i.e. the target holds an aggression edge toward this player.
    private bool HasActivePvpRetaliationAgainst(Player target)
    {
        return _world.EvilTimers.AlreadyEvil(target.Name, _player.Name) != 0;
    }

    // The whole positive-delta half of the evil-point change for one PvP act (melee open, offensive
    // cast, area sweep) against `target`: run the should-give-evil rule, charge what it says, and open/refresh
    // the directed edge. Returns the amount charged so the caller can see what the edge was stamped
    // with (the amount drives the retaliation state, i.e. the travel gate).
    //
    // The verdict matters in three ways:
    //   0 + they hold an edge on us  → self-defence. Stock returns BEFORE opening a timer, so the
    //                                  counter-attacker never opens a window of their own.
    //   0 + we hold a plain edge     → continued aggression inside our own window: free.
    //   1 (we hold a ROBBING edge)   → robbing does not pre-pay an attack. Charge in full and
    //                                  remove the old timer first, so the re-added edge is plain and the
    //                                  '*' a hidden rob was suppressing comes back.
    //   2                            → fresh aggression: charge and open.
    private async Task<float> ApplyPvpAggressionEvilAsync(Player target)
    {
        if (HasActivePvpRetaliationAgainst(target))
            return 0f;

        int verdict = _world.EvilTimers.ShouldGiveEvil(_player.Name, target.Name);
        float epCost = verdict == 0
            ? 0f
            : CombatEngine.GetEPCostForPlayerAttack(_player, target, recentlyAttackedBy: false);

        // AddEvilPointsWithCloudAsync honours the IsLawful gate (stock refuses positive
        // deltas while lawful) and surfaces "A dark cloud passes over you".
        if (epCost > 0)
            await AddEvilPointsWithCloudAsync(epCost);

        if (verdict == 1)
            _world.EvilTimers.RemoveSingleTimer(_player.Name, target.Name);

        StartPvpRetaliationWindow(_player, target, epCost);
        return epCost;
    }

    // Open (or refresh) the directed aggressor→defender edge. The charged
    // amount drives the retaliation state (the travel/teleport gate); a 0 charge (e.g. striking an
    // already-evil target) records the relationship for the name marker without a travel restriction.
    private void StartPvpRetaliationWindow(Player attacker, Player defender, float chargedEvilPoints)
    {
        _world.EvilTimers.AddEvilTimer(attacker.Name, defender.Name, chargedEvilPoints);
    }

    private IReadOnlyList<string> GetVisibleRoomCurrencyParts(Room room)
    {
        return _world.GetVisibleGroundCurrencyParts(room.MapNumber, room.RoomNumber);
    }
}
