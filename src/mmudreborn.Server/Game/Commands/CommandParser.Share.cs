using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using mmudreborn.Game;

namespace mmudreborn.Server;

// Port of the stock `share` command. Splits a
// single coin denomination evenly across the caller and the chosen group, with the caller keeping
// their own share plus any integer-division remainder.
//
//   Syntax: SHARE {amount} {currency} [WITH ROOM/PARTY]   (default: PARTY)
//
// Fidelity notes (verified against V1.11p):
// - The amount is clamped to the caller's holdings of that ONE denomination (not total wealth); if
//   they hold none of it, "You do not have enough to share!".
// - Divisor = recipients + 1 (the sharer is counted), so each member AND the sharer keep amount/N;
//   the remainder all stays with the sharer. Net loss to the sharer = perMember × recipientCount.
// - Party mode walks the follow-chain regardless of room; "with room" reaches everyone in the room.
// - The "There is nobody to share with." string is DEAD in stock (the divisor starts at 1 so the
//   guard never fires); sharing with no eligible recipients is a silent net-zero no-op there, which
//   we reproduce.
// - Per-recipient: "You gave X N coins" to the sharer, "Sharer gave you N coins" to the recipient,
//   and "Sharer just gave X some coins." to everyone else in the sharer's room.
public partial class CommandParser
{
    private async Task HandleShare(string args)
    {
        var parts = (args ?? string.Empty).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Fewer than three words (verb + amount + currency) → the "default: PARTY" syntax line.
        if (parts.Length < 2)
        {
            await _client.SendLineAsync("Syntax: SHARE {amount} {currency} [WITH ROOM/PARTY] - default: PARTY");
            return;
        }

        // An amount below 1 → syntax error.
        if (!int.TryParse(parts[0], out int amount) || amount < 1)
        {
            await _client.SendLineAsync("Syntax: SHARE {amount} {currency} [WITH ROOM/PARTY]");
            return;
        }

        // A currency that matches no plural name → syntax error.
        if (!TryMatchShareCurrencyIndex(parts[1], out int currencyIndex))
        {
            await _client.SendLineAsync("Syntax: SHARE {amount} {currency} [WITH ROOM/PARTY]");
            return;
        }

        // With 4+ words, the 3rd must abbreviate "with" or it's a syntax error; the 4th word
        // abbreviating "room" selects room mode, otherwise (or absent) it's party mode.
        bool withRoom = false;
        if (parts.Length >= 3)
        {
            if (!"with".StartsWith(parts[2], StringComparison.OrdinalIgnoreCase))
            {
                await _client.SendLineAsync("Syntax: SHARE {amount} {currency} [WITH ROOM/PARTY]");
                return;
            }
            if (parts.Length >= 4 && "room".StartsWith(parts[3], StringComparison.OrdinalIgnoreCase))
                withRoom = true;
        }

        // Clamp to the held amount of this single denomination.
        int held = GetShareCurrency(_player, currencyIndex);
        int shareAmount = Math.Min(amount, held);
        if (shareAmount <= 0)
        {
            await _client.SendLineAsync("You do not have enough to share!");
            return;
        }

        var recipients = withRoom
            ? _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player)
            : _world.GetAllPartyMembers(_player.Name);

        int divisor = recipients.Count + 1;          // +1 = the sharer
        int perMember = shareAmount / divisor;
        int remainder = shareAmount - perMember * divisor;

        // Deduct the full amount, then return the sharer's own share + remainder. Net: -perMember per
        // recipient. With no recipients this is a deliberate silent net-zero no-op (see header).
        AddShareCurrency(_player, currencyIndex, -shareAmount + perMember + remainder);

        foreach (var recipient in recipients)
        {
            AddShareCurrency(recipient, currencyIndex, perMember);
            recipient.RecalculateEquipment(_world.Database);   // coin weight changed

            string coins = RobCurrencyName(currencyIndex, perMember);   // the canonical currency name
            await _client.SendLineAsync($"You gave {recipient.Name} {perMember} {coins}");
            _world.SendToPlayer(recipient.Name, $"{_player.Name} gave you {perMember} {coins}", reprompt: true);

            // Room broadcast excludes BOTH the sharer and this recipient (stock excludes two
            // exclusions); BroadcastToRoom only excludes one client, so route per-observer.
            foreach (var observer in _world.GetPlayersInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, _player))
            {
                if (!observer.Name.Equals(recipient.Name, StringComparison.OrdinalIgnoreCase))
                    _world.SendToPlayer(observer.Name, $"{_player.Name} just gave {recipient.Name} some coins.", reprompt: true);
            }
        }

        _player.RecalculateEquipment(_world.Database);
    }

    // Currency index: 0=copper 1=silver 2=gold 3=platinum 4=runic.
    private static int GetShareCurrency(Player p, int index) => index switch
    {
        0 => p.Copper,
        1 => p.Silver,
        2 => p.Gold,
        3 => p.Platinum,
        _ => p.Runic,
    };

    private static void AddShareCurrency(Player p, int index, int delta)
    {
        switch (index)
        {
            case 0: p.Copper += delta; break;
            case 1: p.Silver += delta; break;
            case 2: p.Gold += delta; break;
            case 3: p.Platinum += delta; break;
            default: p.Runic += delta; break;
        }
    }

    // Match against the plural currency-name table (prefix/abbreviation match). First
    // letters are unique across the five denominations, so a simple prefix test is unambiguous.
    private static readonly string[] ShareCurrencyNames = { "copper", "silver", "gold", "platinum", "runic" };

    private static bool TryMatchShareCurrencyIndex(string token, out int index)
    {
        index = -1;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        for (int i = 0; i < ShareCurrencyNames.Length; i++)
        {
            // "co" → copper (abbreviation); "gold crowns" → gold (full canonical name typed).
            if (ShareCurrencyNames[i].StartsWith(token, StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith(ShareCurrencyNames[i], StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }
        return false;
    }
}
