using mmudreborn.Game;

namespace mmudreborn.Server;

// The drop-carrier penalty — the stock hangup routine, which the host
// calls the instant carrier is lost.
//
// Worth stating plainly, because it is the whole shape of the stock design: the original does NOT leave a
// hung-up character standing in the room. huprou saves the player, applies this penalty, broadcasts
// "%s just disconnected!!!", runs the kill check, then the leave-cleanup, the save and the
// clear. The character is gone before anything else can swing at it. What stock does instead of a
// ghost is PUNISH you for dropping — the anti-"hang up to escape a losing fight" measure.
//
// So this file is only half the answer to a ghost. The other half is detection latency: we cannot run any
// of this until we notice the socket died (see the dead-peer timers in TelnetClient, and the login-time
// stale-session kick in TelnetServerHost, which is what actually gets a reconnecting player's ghost out of
// the world quickly).
public partial class GameWorld
{
    private void LoadDisconnectPenaltySettings()
    {
        DisconnectPenaltyLevel = Math.Clamp(
            PlayerRepo.GetServerSettingInt("DISCONNECTPENALTY", DisconnectPenaltyNone),
            DisconnectPenaltyHigh,
            DisconnectPenaltyNone);

        int minPercent = Math.Clamp(PlayerRepo.GetServerSettingInt("DISCONNECTHPMIN", 10), 0, 100);
        int maxPercent = Math.Clamp(PlayerRepo.GetServerSettingInt("DISCONNECTHPMAX", 25), 0, 100);
        // min = MaxHP*minPct/100, max = MaxHP*maxPct/100, then `if (max < min) min = max` before the
        // roll — an inverted pair collapses to the smaller value rather than throwing or rolling backwards.
        DisconnectPenaltyMinHpPercent = Math.Min(minPercent, maxPercent);
        DisconnectPenaltyMaxHpPercent = maxPercent;
        DisconnectPenaltyMaxItemsDropped = Math.Clamp(PlayerRepo.GetServerSettingInt("DISCONNECTITEMS", 3), 0, 100);
    }

    public void SetDisconnectPenaltyLevel(int level)
    {
        DisconnectPenaltyLevel = Math.Clamp(level, DisconnectPenaltyHigh, DisconnectPenaltyNone);
        PlayerRepo.SetServerSettingInt("DISCONNECTPENALTY", DisconnectPenaltyLevel);
    }

    public void SetDisconnectPenaltyHpPercents(int minPercent, int maxPercent)
    {
        int max = Math.Clamp(maxPercent, 0, 100);
        int min = Math.Min(Math.Clamp(minPercent, 0, 100), max);
        DisconnectPenaltyMinHpPercent = min;
        DisconnectPenaltyMaxHpPercent = max;
        PlayerRepo.SetServerSettingInt("DISCONNECTHPMIN", min);
        PlayerRepo.SetServerSettingInt("DISCONNECTHPMAX", max);
    }

    public void SetDisconnectPenaltyMaxItemsDropped(int maxItems)
    {
        DisconnectPenaltyMaxItemsDropped = Math.Clamp(maxItems, 0, 100);
        PlayerRepo.SetServerSettingInt("DISCONNECTITEMS", DisconnectPenaltyMaxItemsDropped);
    }

    public static string DescribeDisconnectPenaltyLevel(int level) => level switch
    {
        DisconnectPenaltyHigh => "HIGH",
        DisconnectPenaltyMedium => "MEDIUM",
        DisconnectPenaltyLow => "LOW",
        _ => "NONE",
    };

    public static bool TryParseDisconnectPenaltyLevel(string token, out int level)
    {
        switch (token.Trim().ToUpperInvariant())
        {
            case "HIGH": level = DisconnectPenaltyHigh; return true;
            case "MEDIUM": level = DisconnectPenaltyMedium; return true;
            case "LOW": level = DisconnectPenaltyLow; return true;
            case "NONE": case "OFF": level = DisconnectPenaltyNone; return true;
            default: level = DisconnectPenaltyNone; return false;
        }
    }

    // The stock three-way gate. HIGH punishes unconditionally; MEDIUM asks
    // inside-autocombat or being-attacked; LOW asks in-PvP-combat only. NONE reaches none of the
    // branches and falls through untouched.
    private bool ShouldApplyDropCarrierPenalty(Player player) => DisconnectPenaltyLevel switch
    {
        DisconnectPenaltyHigh => true,
        DisconnectPenaltyMedium => player.InCombat || IsEngagedByAnotherPlayer(player),
        DisconnectPenaltyLow => player.PlayerCombatTarget != null || IsEngagedByAnotherPlayer(player),
        _ => false,
    };

    // "Is being attacked": another USER currently has this player as their combat target.
    // Combat is room-bound, so a same-room scan matches the stock intent (CommandParser's hide gate resolves
    // it the same way).
    private bool IsEngagedByAnotherPlayer(Player player)
        => GetPlayersInRoom(player.CurrentMapNumber, player.CurrentRoomNumber, player)
            .Any(other => ReferenceEquals(other.PlayerCombatTarget, player));

    /// <summary>
    /// Apply the stock drop-carrier penalty to a player whose connection was lost. Returns true when the
    /// penalty left them at or below the death floor, i.e. when the stock kill check that follows would
    /// kill them. No-op (returns false) unless a sysop has switched the penalty on.
    /// </summary>
    internal bool ApplyDropCarrierPenalty(Player player)
    {
        if (!ShouldApplyDropCarrierPenalty(player))
            return false;

        // An already-unconscious player has the death floor ADDED to their HP rather than losing a
        // percentage — Player.DeathHP is negative, so this drives them under the line and the
        // kill check that follows finishes them. Hanging up while bleeding out does not save you.
        if (player.CurrentHP < 1)
        {
            player.CurrentHP += Player.DeathHP;
        }
        else
        {
            int minLoss = player.MaxHP * DisconnectPenaltyMinHpPercent / 100;
            int maxLoss = player.MaxHP * DisconnectPenaltyMaxHpPercent / 100;
            if (maxLoss < minLoss)
                minLoss = maxLoss;

            // genrdn(0, span+1) + min — genrdn's top is EXCLUSIVE, so Next(0, span + 1) is the same band.
            int loss = Math.Max(0, _rng.Next(0, maxLoss - minLoss + 1) + minLoss);
            player.CurrentHP -= loss;
        }

        DropItemsForDropCarrierPenalty(player);
        // Stock records it on the character rather than telling the player now —
        // there is nobody on the other end of the socket to tell. The message waits for their next login.
        player.DisconnectedWhilePlaying = true;

        return player.CurrentHP <= Player.DeathHP;
    }

    // The stock hangup has two drop loops. Inventory first, then equipment, both bounded by the same running
    // count against the configured item cap. Items carrying ability 100 (Loyal) are exempt — note that unlike the
    // DEATH drop this checks ONLY Loyal, not Major Curse (83) as well; the death path tests both, the hangup
    // tests one. Kept faithful.
    private void DropItemsForDropCarrierPenalty(Player player)
    {
        int budget = DisconnectPenaltyMaxItemsDropped;
        if (budget <= 0)
            return;

        int dropped = 0;
        int map = player.CurrentMapNumber;
        int room = player.CurrentRoomNumber;

        var keptIds = new List<int>();
        var keptInstanceIds = new List<long>();
        for (int index = 0; index < player.Inventory.Count; index++)
        {
            int itemId = player.Inventory[index];
            long instanceId = index < player.InventoryInstanceIds.Count
                ? player.InventoryInstanceIds[index]
                : CreateItemInstance(itemId);

            Database.Items.TryGetValue(itemId, out var item);
            bool loyal = item != null && item.Abilities.ContainsKey(100);
            if (dropped >= budget || item == null || loyal)
            {
                keptIds.Add(itemId);
                keptInstanceIds.Add(instanceId);
                continue;
            }

            // DestroyOnDeath items are removed but never reach the floor (stock skips the room add
            // for them) — and they still consume a slot of the budget.
            if (!item.DestroyOnDeath)
                DropItemInRoom(map, room, itemId, instanceId);

            RemoveItemRuntimeState(instanceId);
            dropped++;
        }

        player.Inventory.Clear();
        player.InventoryInstanceIds.Clear();
        player.Inventory.AddRange(keptIds);
        player.InventoryInstanceIds.AddRange(keptInstanceIds);

        foreach (var (slot, itemId) in player.Equipment.ToList())
        {
            if (dropped >= budget)
                break;

            long instanceId = player.EquipmentInstanceIds.TryGetValue(slot, out var existing)
                ? existing
                : CreateItemInstance(itemId);

            if (!Database.Items.TryGetValue(itemId, out var item))
                continue;

            bool loyal = item.Abilities.ContainsKey(100);
            if (!loyal)
            {
                if (!item.DestroyOnDeath)
                    DropItemInRoom(map, room, itemId, instanceId);

                player.Equipment.Remove(slot);
                player.EquipmentInstanceIds.Remove(slot);
                RemoveItemRuntimeState(instanceId);
            }

            // Stock quirk, preserved: the equipment loop increments its counter for every item it LOOKS at
            // once the item data resolves, including a Loyal one it then declines to drop. So a player
            // wearing loyal gear can burn the whole budget without losing anything. (The inventory loop
            // above counts only actual drops — the two loops genuinely differ in stock.)
            dropped++;
        }

        // Stripping worn gear changes the derived stats the penalty's HP arithmetic and the save both read.
        RecalculatePlayerStats(player);
    }
}
