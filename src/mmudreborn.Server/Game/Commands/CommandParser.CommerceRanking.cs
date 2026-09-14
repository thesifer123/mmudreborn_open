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
    // Exact-match table for currency names that the single-token abbreviation matcher (CurrencyNames,
    // below) CANNOT reproduce. Bare full words like "runic"/"gold"/"silver"/"copper"/"platinum" are
    // deliberately absent — TryResolveCurrencyName's prefix pass already resolves them to the same
    // multiplier and canonical name. What must live here:
    //   - multi-word denomination phrases: the abbreviation pass prefix-matches ONE token, so it can
    //     never match "runic coins", "gold crown", etc.
    //   - "plat": CurrencyNames has "platinum" (so "plat" still resolves via prefix) but the returned
    //     name would change from "plat" to "platinum"; the exact entry preserves the displayed word.
    private static readonly Dictionary<string, long> CurrencyMultipliers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["runic coin"] = CurrencyHelper.CopperPerRunic,
        ["runic coins"] = CurrencyHelper.CopperPerRunic,
        ["plat"] = CurrencyHelper.CopperPerPlatinum,
        ["platinum piece"] = CurrencyHelper.CopperPerPlatinum,
        ["platinum pieces"] = CurrencyHelper.CopperPerPlatinum,
        ["gold crown"] = CurrencyHelper.CopperPerGold,
        ["gold crowns"] = CurrencyHelper.CopperPerGold,
        ["silver noble"] = CurrencyHelper.CopperPerSilver,
        ["silver nobles"] = CurrencyHelper.CopperPerSilver,
        ["copper farthing"] = 1,
        ["copper farthings"] = 1,
    };

    // Canonical currency names for abbreviation matching (longest first for unambiguous matching)
    private static readonly (string Name, long Multiplier)[] CurrencyNames =
    [
        ("platinum", CurrencyHelper.CopperPerPlatinum),
        ("copper", 1),
        ("silver", CurrencyHelper.CopperPerSilver),
        ("runic", CurrencyHelper.CopperPerRunic),
        ("runics", CurrencyHelper.CopperPerRunic),
        ("gold", CurrencyHelper.CopperPerGold),
        ("coins", 0),   // special: means all currency
        ("money", 0),   // special: means all currency
    ];

    /// <summary>Try to resolve a currency name from an abbreviation (e.g., "si"→"silver", "co"→"copper").</summary>
    private static bool TryResolveCurrencyName(string input, out string resolvedName, out long multiplier)
    {
        resolvedName = "";
        multiplier = 0;

        // Exact match first
        if (CurrencyMultipliers.TryGetValue(input, out long mult))
        {
            resolvedName = input.ToLowerInvariant();
            multiplier = mult;
            return true;
        }

        // Abbreviation match: find first currency name that starts with the input
        foreach (var (name, m) in CurrencyNames)
        {
            if (name.StartsWith(input, StringComparison.OrdinalIgnoreCase))
            {
                resolvedName = name;
                multiplier = m;
                return true;
            }
        }

        return false;
    }

    /// <summary>Try to parse "{amount} {currency}" or just "{currency}" from input.
    /// Returns true if it's a currency reference.</summary>
    private static bool TryParseCurrency(string input, out int amount, out string currencyName, out long copperValue)
    {
        return TryParseCurrency(input, out amount, out currencyName, out copperValue, out _);
    }

    private static bool TryParseCurrency(string input, out int amount, out string currencyName, out long copperValue, out long denominationMultiplier)
    {
        amount = 0;
        currencyName = "";
        copperValue = 0;
        denominationMultiplier = 0;

        var parts = input.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[0], out int parsedAmount) && parsedAmount > 0)
        {
            // "10 silver" or "10 si" format
            if (TryResolveCurrencyName(parts[1], out string resolved, out long mult) && mult > 0)
            {
                amount = parsedAmount;
                currencyName = resolved;
                denominationMultiplier = mult;
                copperValue = parsedAmount * mult;
                return true;
            }
        }
        else if (parts.Length == 1 && TryResolveCurrencyName(parts[0], out string resolved2, out long mult2) && mult2 > 0)
        {
            // Just "silver" or "si" — means all of that currency type
            amount = -1; // sentinel for "all"
            currencyName = resolved2;
            denominationMultiplier = mult2;
            copperValue = -1; // will need special handling
            return true;
        }

        return false;
    }

    /// <summary>Check if player has at least copperValue worth of currency.</summary>
    private bool PlayerHasCurrency(long copperValue)
    {
        if (copperValue == -1) return true; // "all" — handled by DeductPlayerCurrency
        long total = CurrencyHelper.ToCopper(_player);
        return total >= copperValue;
    }

    /// <summary>Deduct currency from player by total copper value.</summary>
    private void DeductPlayerCurrency(long copperValue)
    {
        if (copperValue <= 0) return;

        long total = CurrencyHelper.ToCopper(_player);
        if (total < copperValue)
            return;

        CurrencyHelper.SetFromCopper(_player, total - copperValue);
        RecalcEquipment();
    }

    private static readonly long[] CurrencyDenominationValuesAscending =
    [
        1L,
        CurrencyHelper.CopperPerSilver,
        CurrencyHelper.CopperPerGold,
        CurrencyHelper.CopperPerPlatinum,
        CurrencyHelper.CopperPerRunic,
    ];

    private void AddPlayerCurrency(long runic, long platinum, long gold, long silver, long copper)
    {
        AddPlayerCurrency(_player, runic, platinum, gold, silver, copper);
    }

    private static void AddPlayerCurrency(Player player, long runic, long platinum, long gold, long silver, long copper)
    {
        if (runic > 0)
            player.Runic = checked((int)(player.Runic + runic));

        if (platinum > 0)
            player.Platinum = checked((int)(player.Platinum + platinum));

        if (gold > 0)
            player.Gold = checked((int)(player.Gold + gold));

        if (silver > 0)
            player.Silver = checked((int)(player.Silver + silver));

        if (copper > 0)
            player.Copper = checked((int)(player.Copper + copper));
    }

    private void AddPlayerCurrencyDenomination(long denominationMultiplier, long count)
    {
        AddPlayerCurrencyDenomination(_player, denominationMultiplier, count);
    }

    private void AddPlayerCurrencyDenomination(Player player, long denominationMultiplier, long count)
    {
        if (count <= 0)
            return;

        switch (denominationMultiplier)
        {
            case CurrencyHelper.CopperPerRunic:
                AddPlayerCurrency(player, count, 0, 0, 0, 0);
                break;
            case CurrencyHelper.CopperPerPlatinum:
                AddPlayerCurrency(player, 0, count, 0, 0, 0);
                break;
            case CurrencyHelper.CopperPerGold:
                AddPlayerCurrency(player, 0, 0, count, 0, 0);
                break;
            case CurrencyHelper.CopperPerSilver:
                AddPlayerCurrency(player, 0, 0, 0, count, 0);
                break;
            case 1:
                AddPlayerCurrency(player, 0, 0, 0, 0, count);
                break;
        }

        player.RecalculateEquipment(_world.Database);
    }

    private long GetPlayerCurrencyDenominationCount(long denominationMultiplier)
    {
        return GetPlayerCurrencyDenominationCount(_player, denominationMultiplier);
    }

    private static long GetPlayerCurrencyDenominationCount(Player player, long denominationMultiplier)
    {
        return denominationMultiplier switch
        {
            CurrencyHelper.CopperPerRunic => player.Runic,
            CurrencyHelper.CopperPerPlatinum => player.Platinum,
            CurrencyHelper.CopperPerGold => player.Gold,
            CurrencyHelper.CopperPerSilver => player.Silver,
            1 => player.Copper,
            _ => 0,
        };
    }

    private bool TryDeductPlayerCurrencyDenomination(Player player, long denominationMultiplier, long count)
    {
        if (count <= 0)
            return false;

        if (GetPlayerCurrencyDenominationCount(player, denominationMultiplier) < count)
            return false;

        switch (denominationMultiplier)
        {
            case CurrencyHelper.CopperPerRunic:
                player.Runic = checked((int)(player.Runic - count));
                break;
            case CurrencyHelper.CopperPerPlatinum:
                player.Platinum = checked((int)(player.Platinum - count));
                break;
            case CurrencyHelper.CopperPerGold:
                player.Gold = checked((int)(player.Gold - count));
                break;
            case CurrencyHelper.CopperPerSilver:
                player.Silver = checked((int)(player.Silver - count));
                break;
            case 1:
                player.Copper = checked((int)(player.Copper - count));
                break;
            default:
                return false;
        }

        player.RecalculateEquipment(_world.Database);
        return true;
    }

    private static long[] GetPlayerCurrencyCountsAscending(Player player)
    {
        return
        [
            Math.Max(0, (long)player.Copper),
            Math.Max(0, (long)player.Silver),
            Math.Max(0, (long)player.Gold),
            Math.Max(0, (long)player.Platinum),
            Math.Max(0, (long)player.Runic)
        ];
    }

    private static void ApplyPlayerCurrencyCountsAscending(Player player, long[] counts)
    {
        player.Copper = checked((int)Math.Max(0, counts[0]));
        player.Silver = checked((int)Math.Max(0, counts[1]));
        player.Gold = checked((int)Math.Max(0, counts[2]));
        player.Platinum = checked((int)Math.Max(0, counts[3]));
        player.Runic = checked((int)Math.Max(0, counts[4]));
    }

    private bool TrySpendPlayerCurrencyPreservingDenominations(Player player, long copperValue)
    {
        long[] counts = GetPlayerCurrencyCountsAscending(player);
        if (!TrySpendCurrencyPreservingDenominations(counts, copperValue))
            return false;

        ApplyPlayerCurrencyCountsAscending(player, counts);
        player.RecalculateEquipment(_world.Database);
        return true;
    }

    private bool PlayerHasShopCurrency(int amount, int currencyIndex)
    {
        if (amount <= 0)
            return true;

        long copperValue = GetShopCurrencyCopperValue(amount, currencyIndex);
        return PlayerHasCurrency(copperValue);
    }

    private bool TrySpendPlayerShopCurrency(Player player, int amount, int currencyIndex)
    {
        if (amount <= 0)
            return true;

        long copperValue = GetShopCurrencyCopperValue(amount, currencyIndex);
        return TrySpendPlayerCurrencyPreservingDenominations(player, copperValue);
    }

    private static bool TrySpendCurrencyPreservingDenominations(long[] counts, long copperValue)
    {
        if (copperValue <= 0)
            return true;

        long remaining = copperValue;

        for (int i = 0; i < CurrencyDenominationValuesAscending.Length && remaining > 0; i++)
        {
            long denominationValue = CurrencyDenominationValuesAscending[i];
            long available = counts[i];
            if (available <= 0 || denominationValue > remaining)
                continue;

            long spendCount = Math.Min(available, remaining / denominationValue);
            counts[i] -= spendCount;
            remaining -= spendCount * denominationValue;
        }

        if (remaining <= 0)
            return true;

        int denominationToBreakIndex = -1;
        for (int i = 0; i < CurrencyDenominationValuesAscending.Length; i++)
        {
            if (counts[i] <= 0 || CurrencyDenominationValuesAscending[i] <= remaining)
                continue;

            denominationToBreakIndex = i;
            break;
        }

        if (denominationToBreakIndex < 0)
            return false;

        counts[denominationToBreakIndex]--;

        long changeCopper = CurrencyDenominationValuesAscending[denominationToBreakIndex] - remaining;
        AddCurrencyChangeInLargestAvailableDenominations(counts, denominationToBreakIndex, changeCopper);
        return true;
    }

    // Rebuild change from the top down so a broken large coin comes back as the fewest large coins possible.
    private static void AddCurrencyChangeInLargestAvailableDenominations(long[] counts, int denominationIndex, long changeCopper)
    {
        long remaining = Math.Max(0, changeCopper);

        for (int i = denominationIndex - 1; i >= 0 && remaining > 0; i--)
        {
            long denominationValue = CurrencyDenominationValuesAscending[i];
            long addCount = remaining / denominationValue;
            if (addCount <= 0)
                continue;

            counts[i] += addCount;
            remaining -= addCount * denominationValue;
        }
    }

    private void DropExactGroundCurrency(long denominationMultiplier, long count)
    {
        if (count <= 0)
            return;

        switch (denominationMultiplier)
        {
            case CurrencyHelper.CopperPerRunic:
                _world.DropCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, count, 0, 0, 0, 0);
                break;
            case CurrencyHelper.CopperPerPlatinum:
                _world.DropCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, count, 0, 0, 0);
                break;
            case CurrencyHelper.CopperPerGold:
                _world.DropCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, 0, count, 0, 0);
                break;
            case CurrencyHelper.CopperPerSilver:
                _world.DropCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, 0, 0, count, 0);
                break;
            case 1:
                _world.DropCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, 0, 0, 0, count);
                break;
        }
    }

    private void HideExactGroundCurrency(long denominationMultiplier, long count)
    {
        if (count <= 0)
            return;

        switch (denominationMultiplier)
        {
            case CurrencyHelper.CopperPerRunic:
                _world.HideCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, count, 0, 0, 0, 0);
                break;
            case CurrencyHelper.CopperPerPlatinum:
                _world.HideCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, count, 0, 0, 0);
                break;
            case CurrencyHelper.CopperPerGold:
                _world.HideCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, 0, count, 0, 0);
                break;
            case CurrencyHelper.CopperPerSilver:
                _world.HideCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, 0, 0, count, 0);
                break;
            case 1:
                _world.HideCurrencyInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, 0, 0, 0, 0, count);
                break;
        }
    }

    private void RecalcEquipment()
    {
        _player.RecalculateEquipment(_world.Database);
    }

    private Task HandleWho()
        => RenderWhoAsync(_client, _world, _player.IsSysop, _player.UseTechnicalStyle);

    // Non-stock `web-who`: the live web-presence roster (characters whose telepath dock is open, i.e.
    // reachable by telepath from the web) rendered in the same fantasy WHO layout. Sourced from the
    // in-memory web-presence set the telepath bridge already maintains; a web-present name is resolved
    // to its live Player when in the realm, else loaded from the character store. Gated by
    // SYSOP CONFIGURE QOL WHOWEB (see the dispatch site).
    private async Task HandleWhoWeb()
    {
        bool viewerIsSysop = _player.IsSysop;
        var players = new List<Player>();
        foreach (string name in _world.GetWebPresentNames())
        {
            // Prefer the live in-realm instance (current stats/flags); fall back to the stored character.
            Player? p = _world.FindOnlinePlayer(name) ?? _world.PlayerRepo.LoadPlayerByName(name);
            if (p == null || p.IsTestAccount)
                continue;
            if (p.IsSysopInvisible && !viewerIsSysop)
                continue;
            players.Add(p);
        }

        players = players
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase) // de-dupe a name that resolved twice
            .Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.LastName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (players.Count == 0)
        {
            await _client.SendLineAsync($"{MudAnsi.Green}No adventurers are currently on the web.{MudAnsi.Reset}");
            return;
        }

        await RenderWhoWebAsync(_client, _world, players);
    }

    // Shared WHO renderer used by the in-game WHO command AND the pre-realm main-menu "Who's in the
    // Realm" option. The two viewer-specific inputs (sysop sees sys-invisible players; technical vs
    // fantasy layout) are passed explicitly so the menu — which has no logged-in Player — can call it.
    internal static async Task RenderWhoAsync(IGameClient client, GameWorld world, bool viewerIsSysop, bool technicalStyle)
    {
        var players = world.GetAllOnlinePlayers()
            .Where(player => !player.IsTestAccount)
            .Where(player => !player.IsOutOfRealm) // left the Realm to train — gone from WHO for everyone
            .Where(player => !player.IsSysopInvisible || viewerIsSysop)
            .OrderBy(player => player.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(player => player.LastName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (players.Count == 0)
        {
            await client.SendLineAsync($"{MudAnsi.Green}There are no users in the game at the moment.{MudAnsi.Reset}");
            return;
        }

        if (technicalStyle)
        {
            await RenderTechnicalWhoAsync(client, world, players);
            return;
        }

        await RenderFantasyWhoAsync(client, world, players);
    }

    // Hall of Fame: the dead-player record store written on permadeath (see PerformPermadeathAsync /
    // the permanent-info save). Stock keeps the records but exposes no command; this is an
    // mmudreborn-only viewer of fallen heroes, newest first.
    private async Task HandleHallOfFame(string args)
    {
        int limit = 20;
        if (!string.IsNullOrWhiteSpace(args) && int.TryParse(args.Trim(), out int requested) && requested > 0)
            limit = Math.Min(requested, 50);

        var entries = _world.PlayerRepo.GetHallOfFameEntries(limit);

        await _client.SendLineAsync($"{MudAnsi.WhiteOnBlack}{MudAnsi.LinePreamble}{MudAnsi.BrightYellow}         Hall of Fame -- Fallen Heroes{MudAnsi.Reset}");
        await _client.SendLineAsync($"{MudAnsi.DarkGray}         ============================={MudAnsi.Reset}");
        await _client.SendLineAsync();

        if (entries.Count == 0)
        {
            await _client.SendLineAsync("No heroes have fallen... yet.");
            await _client.SendLineAsync();
            return;
        }

        foreach (var entry in entries)
        {
            string className = _world.Database.Classes.TryGetValue(entry.ClassId, out var cls) ? cls.Name : "Adventurer";
            string raceName = _world.Database.Races.TryGetValue(entry.RaceId, out var race) ? race.Name : "being";
            string fullName = string.IsNullOrWhiteSpace(entry.LastName) ? entry.PlayerName : $"{entry.PlayerName} {entry.LastName}";

            await _client.SendLineAsync(
                $"{MudAnsi.BrightWhite}{fullName}{MudAnsi.Reset} - level {entry.Level} {raceName} {className} ({entry.Experience:N0} exp)");
        }

        await _client.SendLineAsync();
    }

    private const string FantasyWhoIndent = "         ";

    // One fantasy-style WHO row (alignment tag, green name field, gossip mark, magenta title, gang). Shared
    // by the stock WHO and the non-stock web-who so both stay pixel-identical.
    private static string BuildFantasyWhoRow(GameWorld world, Player p)
    {
        const int whoNameFieldWidth = 21;
        const string whoNameGreen = "\x1b[0;32m";
        const string whoGreen = "\x1b[32m";
        const string whoYellow = "\x1b[33m";
        const string whoMagenta = "\x1b[35m";
        const string whoWhite = "\x1b[37m";

        var cls = world.Database.Classes.TryGetValue(p.ClassId, out var foundClass)
            ? foundClass
            : new CharacterClass { Name = "Unknown" };

        string title = p.GetTitle(cls);
        string align = GetWhoAlignmentTag(p);
        string alignColor = GetWhoAlignmentColor(p);
        string fullName = string.IsNullOrWhiteSpace(p.LastName)
            ? p.Name
            : $"{p.Name} {p.LastName}";

        string gangSuffix = string.IsNullOrWhiteSpace(p.Gang)
            ? $"{whoGreen}  {whoWhite} "
            : $"{whoGreen}  of {whoYellow}{p.Gang} {whoWhite} ";

        string alignPrefix = string.IsNullOrEmpty(align)
            ? FantasyWhoIndent
            : $"{alignColor}{align.PadLeft(8)} ";

        string nameField = fullName.PadRight(whoNameFieldWidth);

        // The name/title separator doubles as a gossip indicator: "x" means this player has
        // gossip turned off (won't hear you), "-" means they're listening.
        string gossipMark = p.ReceiveGossipEnabled ? "-" : "x";

        return $"{MudAnsi.Reset}{alignPrefix}{whoNameGreen}{nameField}{gossipMark}  {whoMagenta}{title}{gangSuffix}";
    }

    private static async Task RenderFantasyWhoAsync(IGameClient client, GameWorld world, List<Player> players)
    {
        await client.SendLineAsync($"{MudAnsi.WhiteOnBlack}{MudAnsi.LinePreamble}{MudAnsi.BrightYellow}{FantasyWhoIndent}Current Adventurers");
        await client.SendLineAsync($"{MudAnsi.DarkGray}{FantasyWhoIndent}===================");
        await client.SendLineAsync();

        foreach (var p in players)
            await client.SendLineAsync(BuildFantasyWhoRow(world, p));

        await client.SendLineAsync();
    }

    // Non-stock web-who: same fantasy layout as WHO but a distinct header, so a player can tell at a glance
    // this is the web-presence roster (telepath-reachable) rather than who's physically in the realm.
    private static async Task RenderWhoWebAsync(IGameClient client, GameWorld world, List<Player> players)
    {
        await client.SendLineAsync($"{MudAnsi.WhiteOnBlack}{MudAnsi.LinePreamble}{MudAnsi.BrightYellow}{FantasyWhoIndent}Adventurers on the Web");
        await client.SendLineAsync($"{MudAnsi.DarkGray}{FantasyWhoIndent}======================");
        await client.SendLineAsync();

        foreach (var p in players)
            await client.SendLineAsync(BuildFantasyWhoRow(world, p));

        await client.SendLineAsync();
    }

    // The stock `who` in standard/technical style. The column
    // widths AND raw colour bytes below come from the stock header and
    // row format strings, verified byte-for-byte against a live technical-WHO capture.
    // Megamud parses this layout by fixed
    // column position, so every space and escape must match exactly.
    //
    // IMPORTANT: the stock technical WHO emits BARE SGR codes here (\x1b[35m), NOT the 0;-prefixed forms
    // the MudAnsi.* constants use (\x1b[0;35m) — and the fantasy WHO. The only exceptions are the data-row
    // gang colour (\x1b[0;33m), the reputation colour (alignment-driven, e.g. Good = \x1b[1;37m), the
    // divider (\x1b[1;30m) and the leading reset (\x1b[0m). So the structural colours are spelled out
    // literally rather than via MudAnsi.* to keep the bytes faithful.
    private const string TechnicalWhoDivider =
        "=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=";
    private const int TechnicalWhoTitleWidth = 15;
    private const int TechnicalWhoNameWidth = 20;
    private const int TechnicalWhoReputationWidth = 10;
    private const int TechnicalWhoGangWidth = 19;
    private const string TechnicalWhoTitleColor = "\x1b[35m";
    private const string TechnicalWhoNameColor = "\x1b[32m";
    private const string TechnicalWhoRepHeaderColor = "\x1b[36m";
    private const string TechnicalWhoGangHeaderColor = "\x1b[33m";
    private const string TechnicalWhoTrailingColor = "\x1b[37m";
    private const string TechnicalWhoGangRowColor = "\x1b[0;33m";
    private const string TechnicalWhoTrailingPad = "      "; // %-6.6s of the empty trailing field

    // Hard left-justify/truncate to width (the %-N.Ns form) — no "~" ellipsis.
    private static string FitTechnicalWho(string value, int width)
        => value.Length <= width ? value.PadRight(width) : value[..width];

    private static async Task RenderTechnicalWhoAsync(IGameClient client, GameWorld world, List<Player> players)
    {
        // Header carries the one-shot line-clear preamble; the 3 spaces after the name field line up
        // with the row's "{gossip}{auction} " flag slot so the reputation column stays aligned.
        await client.SendLineAsync(
            $"{MudAnsi.WhiteOnBlack}{MudAnsi.LinePreamble}{TechnicalWhoTitleColor}{FitTechnicalWho("Title", TechnicalWhoTitleWidth)} " +
            $"{TechnicalWhoNameColor}{FitTechnicalWho("Name", TechnicalWhoNameWidth)}   " +
            $"{TechnicalWhoRepHeaderColor}{FitTechnicalWho("Reputation", TechnicalWhoReputationWidth)} " +
            $"{TechnicalWhoGangHeaderColor}{FitTechnicalWho("Gang/Guild", TechnicalWhoGangWidth)} " +
            $"{TechnicalWhoTrailingColor}{TechnicalWhoTrailingPad}");
        await client.SendLineAsync($"{MudAnsi.DarkGray}{TechnicalWhoDivider}");

        bool firstRow = true;
        foreach (var p in players)
        {
            var cls = world.Database.Classes.TryGetValue(p.ClassId, out var foundClass)
                ? foundClass
                : new CharacterClass { Name = "Unknown" };

            string title = p.GetTitle(cls);
            string fullName = string.IsNullOrWhiteSpace(p.LastName)
                ? p.Name
                : $"{p.Name} {p.LastName}";
            string reputation = GetTechnicalWhoReputationText(p);
            // Technical WHO prints "None" for a gangless player (verified vs live MUD); only the
            // fantasy WHO leaves the gang field blank (see gangSuffix above).
            string gang = string.IsNullOrWhiteSpace(p.Gang) ? "None" : p.Gang;

            // Channel-off flags sit immediately after the 20-wide name: "g" = gossip off, "a" =
            // auction off (so "ga" = both off, "  " = both on), then one space before reputation.
            char gossipMark = p.ReceiveGossipEnabled ? ' ' : 'g';
            char auctionMark = p.ReceiveAuctionEnabled ? ' ' : 'a';

            // Stock emits a single reset once, right before the first data row.
            string rowReset = firstRow ? MudAnsi.Reset : string.Empty;
            firstRow = false;

            await client.SendLineAsync(
                $"{rowReset}{TechnicalWhoTitleColor}{FitTechnicalWho(title, TechnicalWhoTitleWidth)} " +
                $"{TechnicalWhoNameColor}{FitTechnicalWho(fullName, TechnicalWhoNameWidth)}{gossipMark}{auctionMark} " +
                $"{GetTechnicalWhoReputationColor(p)}{FitTechnicalWho(reputation, TechnicalWhoReputationWidth)} " +
                $"{TechnicalWhoGangRowColor}{FitTechnicalWho(gang, TechnicalWhoGangWidth)} " +
                $"{TechnicalWhoTrailingColor}{TechnicalWhoTrailingPad}");
        }
    }

    private async Task HandleTop(string args, bool forcedTopTen = false)
    {
        int requestedCount = 10;
        bool showGangList = false;
        int classId = 0;

        if (!TryParseTopArgs(args, out requestedCount, out showGangList, out classId))
        {
            // The stock TOPTEN never prints a syntax help. A single
            // unrecognized leading arg (atol < 1, not a class or "gangs") returns 0, which
            // the dispatcher reports as "Your command had no effect." (e.g. TOP FOOBAR). Any
            // multi-token junk — margc >= 3 with neither arg "gangs", e.g. TOP 10 10 — falls
            // through the dispatch with no display and returns 1, i.e. silently ignored.
            int argTokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (argTokens <= 1)
                await _client.SendLineAsync("Your command had no effect.");
            return;
        }

        // TOPTEN is fixed at ten players, except a class filter shows the full class list.
        if (forcedTopTen && classId == 0)
            requestedCount = 10;

        if (showGangList)
        {
            await RenderTopGangsAsync(_client, _world, requestedCount);
            return;
        }

        await RenderTopAdventurersAsync(_client, _world, requestedCount, classId);
    }

    // Shared TOP-adventurers renderer used by the in-game TOP/TOPTEN command AND the pre-realm main-menu
    // "Topten Adventurers" option (which always asks for the top 10, no class filter).
    internal static async Task RenderTopAdventurersAsync(IGameClient client, GameWorld world, int requestedCount, int classId)
    {
        const int rankColWidth = 6;
        const int nameColWidth = 21; // 9 first + 1 space + 9 last + 2 spaces
        const int classColWidth = 14;
        const int gangColWidth = 20;

        var rows = world.PlayerRepo.GetTopPlayers(requestedCount, classId);

        string? classFilterName = null;
        if (classId > 0 && world.Database.Classes.TryGetValue(classId, out var filterClass))
            classFilterName = filterClass.Name;

        static string Fit(string value, int width)
        {
            if (value.Length <= width)
                return value.PadRight(width);
            if (width <= 0)
                return string.Empty;
            if (width == 1)
                return value[..1];
            return value[..(width - 1)] + "~";
        }

        static string FitName(string firstName, string lastName)
        {
            string first = firstName.Length > 9 ? firstName[..9] : firstName;
            string last = lastName.Length > 9 ? lastName[..9] : lastName;

            if (string.IsNullOrWhiteSpace(last))
                return first;

            return $"{first} {last}";
        }

        await client.SendLineAsync($"{MudAnsi.Yellow}Top Heroes of the Realm{MudAnsi.Reset}");
        await client.SendLineAsync($"{MudAnsi.White}-=-=-=-=-=-=-=-=-=-=-=-{MudAnsi.Reset}");
        if (classFilterName != null)
            await client.SendLineAsync($"{MudAnsi.Magenta}Class: {classFilterName}{MudAnsi.Reset}");
        await client.SendLineAsync();
        await client.SendLineAsync(
            $"{MudAnsi.Red}{Fit("Rank", rankColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Green}{Fit("Name", nameColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Magenta}{Fit("Class", classColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Yellow}{Fit("Gang/Guild", gangColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Green}Experience{MudAnsi.Reset}");
        await client.SendLineAsync($"{MudAnsi.White}=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-={MudAnsi.Reset}");

        int rank = 1;
        foreach (var row in rows)
        {
            string fullName = FitName(row.Name, row.LastName);

            string className = world.Database.Classes.TryGetValue(row.ClassId, out var cls)
                ? cls.Name
                : "Unknown";

            string gangName = string.IsNullOrWhiteSpace(row.Gang) ? "None" : row.Gang;

            await client.SendLineAsync(
                $"{MudAnsi.Red}{Fit($"{rank,2}.", rankColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Green}{Fit(fullName, nameColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Magenta}{Fit(className, classColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Yellow}{Fit(gangName, gangColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Green}{row.Experience}{MudAnsi.Reset}");

            rank++;
        }
    }

    // Shared TOP-gangs renderer used by the in-game `top gang` command (via HandleTop) AND the pre-realm
    // main-menu "Topten Gangs" option.
    internal static async Task RenderTopGangsAsync(IGameClient client, GameWorld world, int requestedCount)
    {
        const int rankColWidth = 6;
        const int gangColWidth = 20;
        const int leaderColWidth = 16;
        const int membersColWidth = 9;
        const int createdColWidth = 12;

        var rows = world.PlayerRepo.GetTopGangs(requestedCount);

        static string Fit(string value, int width)
        {
            if (value.Length <= width)
                return value.PadRight(width);
            if (width <= 0)
                return string.Empty;
            if (width == 1)
                return value[..1];
            return value[..(width - 1)] + "~";
        }

        static string FormatCreated(string createdRaw)
        {
            if (DateTime.TryParse(createdRaw, out var dt))
                return dt.ToString("yyyy-MM-dd");
            return createdRaw;
        }

        await client.SendLineAsync($"{MudAnsi.Yellow}Top Gangs of the Realm{MudAnsi.Reset}");
        await client.SendLineAsync($"{MudAnsi.White}-=-=-=-=-=-=-=-=-=-=-={MudAnsi.Reset}");
        await client.SendLineAsync();
        await client.SendLineAsync(
            $"{MudAnsi.Red}{Fit("Rank", rankColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Green}{Fit("Gangname", gangColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Magenta}{Fit("Leader", leaderColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Yellow}{Fit("Members", membersColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Green}{Fit("Created", createdColWidth)}{MudAnsi.Reset}" +
            $"{MudAnsi.Green}Exp{MudAnsi.Reset}");
        await client.SendLineAsync($"{MudAnsi.White}=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-=-={MudAnsi.Reset}");

        int rank = 1;
        foreach (var row in rows)
        {
            string leaderName = string.IsNullOrWhiteSpace(row.LeaderName) ? "Unknown" : row.LeaderName;
            string created = FormatCreated(row.Created);

            await client.SendLineAsync(
                $"{MudAnsi.Red}{Fit($"{rank,2}.", rankColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Green}{Fit(row.GangName, gangColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Magenta}{Fit(leaderName, leaderColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Yellow}{Fit(row.Members.ToString(), membersColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Green}{Fit(created, createdColWidth)}{MudAnsi.Reset}" +
                $"{MudAnsi.Green}{row.Experience}{MudAnsi.Reset}");

            rank++;
        }
    }

    private bool TryParseTopArgs(string args, out int requestedCount, out bool showGangList, out int classId)
    {
        requestedCount = 10;
        showGangList = false;
        classId = 0;

        string trimmed = args.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return true;

        bool countSpecified = false;
        bool modeSpecified = false;

        foreach (var token in trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string lower = token.ToLowerInvariant();

            if (lower is "gang" or "gangs")
            {
                showGangList = true;
                modeSpecified = true;
                continue;
            }

            if (lower is "player" or "players")
            {
                showGangList = false;
                modeSpecified = true;
                continue;
            }

            if (lower == "all")
            {
                if (countSpecified)
                    return false;

                requestedCount = 0;
                countSpecified = true;
                continue;
            }

            if (int.TryParse(lower, out int parsedCount) && parsedCount >= 0)
            {
                if (countSpecified)
                    return false;

                requestedCount = parsedCount;
                countSpecified = true;
                continue;
            }

            // A non-numeric token may name a class (e.g. TOP THIEF, TOP MYSTIC).
            // TOPTEN matches the class via a prefix compare.
            if (classId == 0 && TryResolveClassFilter(lower, out int resolvedClassId))
            {
                classId = resolvedClassId;
                continue;
            }

            return false;
        }

        // TOP TEN historically maps to players unless explicitly set to gang.
        if (!modeSpecified)
            showGangList = false;

        // A class filter shows every matching player by default (stock passes the
        // configured max), so only cap the list when the user gave an explicit count.
        if (classId > 0 && !countSpecified)
            requestedCount = 0;

        return true;
    }

    // Resolve a class-name token to its class number using a case-insensitive
    // prefix match, matching the stock prefix compare in TOPTEN.
    private bool TryResolveClassFilter(string token, out int classId)
    {
        classId = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        CharacterClass? match = null;
        foreach (var cls in _world.Database.Classes.Values)
        {
            if (string.IsNullOrWhiteSpace(cls.Name))
                continue;

            // Exact name wins outright; otherwise take the first prefix match in id order.
            if (cls.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
            {
                classId = cls.Number;
                return true;
            }

            if (match == null && cls.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                match = cls;
        }

        if (match != null)
        {
            classId = match.Number;
            return true;
        }

        return false;
    }

    private static string GetWhoAlignmentTag(Player player)
    {
        if (player.IsLawful)
            return "Lawful";

        return CombatEngine.GetPlayerAlignment(player.EvilPoints) switch
        {
            CombatEngine.PlayerAlignment.Saint => "Saint",
            CombatEngine.PlayerAlignment.Good => "Good",
            CombatEngine.PlayerAlignment.Neutral => string.Empty,
            CombatEngine.PlayerAlignment.Seedy => "Seedy",
            CombatEngine.PlayerAlignment.Outlaw => "Outlaw",
            CombatEngine.PlayerAlignment.Criminal => "Criminal",
            CombatEngine.PlayerAlignment.Villain => "Villain",
            CombatEngine.PlayerAlignment.FIEND => "FIEND",
            _ => string.Empty
        };
    }

    private static string GetTechnicalWhoReputationText(Player player)
    {
        if (player.IsLawful)
            return "Lawful";

        return CombatEngine.GetPlayerAlignment(player.EvilPoints) switch
        {
            CombatEngine.PlayerAlignment.Saint => "Saint",
            CombatEngine.PlayerAlignment.Good => "Good",
            CombatEngine.PlayerAlignment.Neutral => "Neutral",
            CombatEngine.PlayerAlignment.Seedy => "Seedy",
            CombatEngine.PlayerAlignment.Outlaw => "Outlaw",
            CombatEngine.PlayerAlignment.Criminal => "Criminal",
            CombatEngine.PlayerAlignment.Villain => "Villain",
            CombatEngine.PlayerAlignment.FIEND => "FIEND",
            _ => "Neutral"
        };
    }

    private static string GetTechnicalWhoReputationColor(Player player)
    {
        string color = GetWhoAlignmentColor(player);
        // Neutral has no fantasy tag (empty colour); in the technical column it's cyan (bare \x1b[36m).
        return string.IsNullOrEmpty(color) ? "\x1b[36m" : color;
    }

    // Per-alignment colour used by BOTH WHO styles (the stock tag/reputation colour table).
    // The exact bytes were read out of the stock alignment colour
    // table and confirmed against a live capture (Good = \x1b[1;37m). Note these are the stock
    // own codes, NOT the 0;-prefixed MudAnsi.* constants: Seedy is WHITE (\x1b[37m), not dark-gray, and
    // Outlaw/Criminal are the bare \x1b[31m/\x1b[33m forms. The bright entries (Good/Saint/Villain/
    // FIEND) happen to equal the MudAnsi.Bright* constants already.
    private static string GetWhoAlignmentColor(Player player)
    {
        if (player.IsLawful)
            return MudAnsi.BrightWhite; // \x1b[1;37m

        return CombatEngine.GetPlayerAlignment(player.EvilPoints) switch
        {
            CombatEngine.PlayerAlignment.Saint => MudAnsi.BrightWhite,    // \x1b[1;37m
            CombatEngine.PlayerAlignment.Good => MudAnsi.BrightWhite,     // \x1b[1;37m
            CombatEngine.PlayerAlignment.Neutral => string.Empty,      // no fantasy tag
            CombatEngine.PlayerAlignment.Seedy => "\x1b[37m",          // white
            CombatEngine.PlayerAlignment.Outlaw => "\x1b[31m",         // red (bare)
            CombatEngine.PlayerAlignment.Criminal => "\x1b[33m",       // yellow (bare)
            CombatEngine.PlayerAlignment.Villain => MudAnsi.BrightYellow, // \x1b[1;33m
            CombatEngine.PlayerAlignment.FIEND => MudAnsi.BrightRed,      // \x1b[1;31m
            _ => string.Empty
        };
    }

}
