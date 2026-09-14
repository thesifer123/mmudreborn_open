using System;
using System.Threading.Tasks;
using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

// Port of the stock `rob` command. A thief attempts to
// pickpocket another player; success is silent to the victim, a near-miss fails quietly, and a clear
// miss "bumps" the victim and alerts them. Evil points are charged on every attempt against a
// non-evil target, and an evil-timer edge is opened (the robbing flags drive the §1 name marker —
// only a *noticed* rob shows the '*'). Robbing a monster is a no-op in V1.11p (the monster path
// is a stub — its single guarded branch is never reached).
public partial class CommandParser
{
    // A roll of 1..99 < 50 picks currency over an item on a successful rob.
    private const int RobCurrencyChanceExclusive = 50;

    // ROB applies a 1-tick delay once a PLAYER or MONSTER target resolves — a rob attempt
    // costs an action tick whether it lands or not, so failed attempts can't be machine-gunned. The
    // delay is NOT charged when the target is an item or is not seen at all.
    private const int RobFastTicks = 1;

    private async Task HandleRob(string args)
    {
        // At the very top — before the syntax check and before any target is
        // resolved: robbing unconditionally drops RESTING and MEDITATING.
        // So even a bare `rob` with no argument breaks rest/meditation.
        _player.IsResting = false;
        _player.IsMeditating = false;

        string target = (args ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            await _client.SendLineAsync("Syntax: ROB {user/monster}");
            return;
        }

        // Target resolution: case 1 player, case 2 monster, cases 4/8/16 item. Self
        // resolves as a normal case-1 player match (the rob path makes the "rob yourself" call itself,
        // AFTER its way-of-life gate), so route it down the same branch rather than short-circuiting.
        bool robbingSelf = TargetNameMatcher.MatchesWordPrefix(_player.Name, target);
        var victim = robbingSelf
            ? _player
            : _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, _player);

        if (victim != null)
        {
            // Case 1 clears SNEAK and HIDE the instant a player target
            // resolves — ahead of the can-you-even-see-them test below, so an attempt that goes nowhere
            // still reveals the robber. (In the self case the victim IS the robber, so this is also what
            // makes `rob <self>` always visible to itself.)
            await BreakSneakAndHideForAction();

            // A hidden victim can't be robbed unless the robber can see hidden (ability 57 → the
            // the See Hidden observer bit). Stock reports the plain not-seen line and charges no delay.
            if (victim.IsHidden && !_player.HasSeeHidden)
            {
                await _client.SendLineAsync("You don't see that anywhere!");
                return;
            }

            await ApplyActionDelayAsync(RobFastTicks);
            await RobPlayerAsync(victim);
            return;
        }

        var monster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, target);
        if (monster != null)
        {
            // Case 2: identical state clears and delay, then the monster path — a stub in V1.11p
            // (its one branch is never reached), so the attempt
            // costs the tick and produces no output at all.
            await BreakSneakAndHideForAction();
            await ApplyActionDelayAsync(RobFastTicks);
            return;
        }

        // Cases 4/8/16 — the name matched an ITEM (room floor or pack) rather than a
        // creature. No state clear, no delay, just the refusal.
        if (_world.FindGroundItemByName(_player.CurrentMapNumber, _player.CurrentRoomNumber, target, includeHidden: false).ItemId > 0
            || TryFindInventoryItem(target, out _, out _, out _, out _))
        {
            await _client.SendLineAsync("Why would you want to rob from that?");
            return;
        }

        await _client.SendLineAsync("You don't see that anywhere!");
    }

    private async Task RobPlayerAsync(Player victim)
    {
        // Gate 1: warn-on-evil or a lawful "way of life" vow blocks robbing outright,
        // BEFORE any skill roll or evil-points math.
        if (_player.WarnOnEvilEnabled || _player.IsLawful)
        {
            await _client.SendLineAsync("You have chosen a way of life which prevents this action.");
            return;
        }

        // Gate 2: the self-target test runs only AFTER the way-of-life gate, so a lawful
        // character typing `rob <self>` is told about their vow, not about robbing themselves.
        if (ReferenceEquals(victim, _player))
        {
            await _client.SendLineAsync("Why would you want to rob yourself?");
            return;
        }

        // Gate 3: the PvP-legality window — newbies (<L4) are off-limits, otherwise the
        // target must be within the configured level range, an Outlaw+, or already attacking us.
        if (!_player.IsSysop && !IsRobTargetLegal(victim))
        {
            await _client.SendLineAsync("Such an action would result in a very unbalanced game.");
            return;
        }

        // Gate 4: the room must be neither PROTECTED (attribute bit 0) nor a combat ARENA while arena
        // combat mode is on. Stock writes this as one condition —
        //   `not protected && (room type != 5 || arena mode off)`
        // guarding the rob, with the guilt line on the else — so an arena is rob-proof exactly when the
        // arena option is enabled (the same one that makes an arena evil-point-free for casts).
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        bool arenaBlocksRob = room?.RoomType == ArenaRoomType && _world.ArenaCombatMode;
        if (room?.IsProtected == true || arenaBlocksRob)
        {
            await _client.SendLineAsync("You are overcome with a feeling of guilt and return your hands to your own pockets");
            return;
        }

        // Gate 4 (the "too far to the evil side" guard): an already-deeply-evil robber is stopped.
        if (_player.EvilPoints > 300)
        {
            await _client.SendLineAsync("You have progressed too far to the evil side to do this action.");
            return;
        }

        int roll = Random.Shared.Next(1, 100);   // genrdn(1,100)
        int skill = _player.Thievery;
        float robEP = CombatEngine.GetEPCostForRob(_player, victim);

        if (roll > skill + 10)
        {
            // Caught: a clear miss bumps the victim and alerts them. Evil-timer flag bit-0 only, so the
            // §1 '*' marker shows on this now-known feud.
            await ChargeRobEvilAsync(victim, robEP, EvilTimerRegistry.EvilTimerKind.RobNoticed);
            await _client.SendLineAsync($"You bump {victim.Name} as you try to rob {victim.HimHer_Lower}.");
            await SendCombatMessagesToPlayerAsync(victim,
                [$"{_player.Name} bumps you as {_player.HeShe_Lower} tries to rob you!"]);
            return;
        }

        if (roll > skill)
        {
            // Near-miss: fails quietly (robber-only). Evil-timer flag bit-0|bit-1 → marker suppressed.
            await ChargeRobEvilAsync(victim, robEP, EvilTimerRegistry.EvilTimerKind.RobHidden);
            await _client.SendLineAsync($"Your skills fail as you try to rob {victim.Name}.");
            return;
        }

        // Success: silent to the victim. Same hidden evil-timer flags as the near-miss.
        await ChargeRobEvilAsync(victim, robEP, EvilTimerRegistry.EvilTimerKind.RobHidden);

        if (Random.Shared.Next(1, 100) < RobCurrencyChanceExclusive)
            await RobCurrencyAsync(victim);
        else
            await RobItemAsync(victim);
    }

    // The robbing evil-point path: charge EP (with the "dark cloud" feedback) and open/refresh the
    // directed robber→victim evil-timer edge with the robbing flags. Always opens the edge even when
    // the charge is 0 (e.g. robbing a Seedy+ target), exactly like stock.
    private async Task ChargeRobEvilAsync(Player victim, float robEP, EvilTimerRegistry.EvilTimerKind kind)
    {
        if (robEP > 0)
            await AddEvilPointsWithCloudAsync(robEP);
        _world.EvilTimers.AddEvilTimer(_player.Name, victim.Name, robEP, kind);
    }

    private bool IsRobTargetLegal(Player victim)
    {
    // Players under level 4 can neither rob nor be robbed.
        if (_player.Level < 4 || victim.Level < 4)
            return false;

        // A target already in an evil-timer feud with us (they attacked/robbed us) is always fair game.
        if (HasActivePvpRetaliationAgainst(victim))
            return true;

        // An Outlaw+ victim (EvilPoints >= 40) is always fair game.
        if (victim.EvilPoints >= 40)
            return true;

        return Math.Abs(_player.Level - victim.Level) <= _world.PvpLevelRange;
    }

    // Success branch, currency case (a roll of 1..99 < 50): pick a random
    // denomination and steal genrdn(0, amount) of it. 0 stolen → the quiet "skills fail" line.
    private async Task RobCurrencyAsync(Player victim)
    {
        // DELIBERATE DEVIATION (NMR-style stock-bug fix): stock rolls the denomination with genrdn(0,4),
        // whose top is EXCLUSIVE → [0,3], so the runic case is dead code and runic is NEVER
        // robbed in stock. The author clearly intended 5 denominations (they wrote case 4), so we
        // keep Next(0,5) to let runic be robbed. For strict stock fidelity this would be Next(0,4).
        int type = Random.Shared.Next(0, 5);   // 0=copper 1=silver 2=gold 3=platinum 4=runic
        int available = type switch
        {
            0 => victim.Copper,
            1 => victim.Silver,
            2 => victim.Gold,
            3 => victim.Platinum,
            _ => victim.Runic,
        };

        // genrdn(0, available) = [0, available-1] (top exclusive) → you never steal the whole stack.
        int amount = Random.Shared.Next(0, available);
        if (amount <= 0)
        {
            await _client.SendLineAsync($"Your skills fail as you try to rob {victim.Name}.");
            return;
        }

        switch (type)
        {
            case 0: victim.Copper -= amount; _player.Copper += amount; break;
            case 1: victim.Silver -= amount; _player.Silver += amount; break;
            case 2: victim.Gold -= amount; _player.Gold += amount; break;
            case 3: victim.Platinum -= amount; _player.Platinum += amount; break;
            default: victim.Runic -= amount; _player.Runic += amount; break;
        }

        _player.RecalculateEquipment(_world.Database);
        victim.RecalculateEquipment(_world.Database);

        await _client.SendLineAsync($"You stole {amount} {RobCurrencyName(type, amount)} from {victim.Name}.");
    }

    private static string RobCurrencyName(int type, int amount)
    {
        bool one = amount == 1;
        return type switch
        {
            0 => one ? "copper farthing" : "copper farthings",
            1 => one ? "silver noble" : "silver nobles",
            2 => one ? "gold crown" : "gold crowns",
            3 => one ? "platinum piece" : "platinum pieces",
            _ => one ? "runic coin" : "runic coins",
        };
    }

    // Success branch, item case (a roll of 1..99 >= 50): each carried (unequipped) item gets an
    // independent 50% roll; the last eligible one that passes is the steal candidate. An item is
    // eligible only if it is a real takeable item (Robable — i.e. not scenery) AND is NOT a
    // Loyal item (ability 100). Equipped items are excluded by construction (we scan the carried
    // inventory, never the Equipment slots).
    //
    // FIDELITY NOTE — deliberate divergence from V1.11p: the stock pack-scan REQUIRES ability 100,
    // i.e. it would only ever lift a *Loyal* item from the pack (and otherwise fall back to a key).
    // That is a stock quirk/bug and is dangerous here — Loyal items include one-time, irreplaceable
    // quest items, so robbing them could permanently strip a player of un-regainable gear. We invert
    // it: Loyal items are never stealable. This matches the official Thievery help ("steal cash or
    // items (non-equipped)") and the board owner's design intent. See rob-parity.md.
    private const int LoyalItemAbilityId = 100;

    private async Task RobItemAsync(Player victim)
    {
        int candidateIndex = -1;
        for (int i = 0; i < victim.Inventory.Count; i++)
        {
            if (Random.Shared.Next(1, 100) >= RobCurrencyChanceExclusive)
                continue;   // this slot failed its 50% roll

            var candidate = _world.Database.Items.GetValueOrDefault(victim.Inventory[i]);
            if (candidate == null || !candidate.CanBeRobbed || candidate.Abilities.ContainsKey(LoyalItemAbilityId))
                continue;   // scenery or a Loyal/quest item — never stealable

            candidateIndex = i;
        }

        if (candidateIndex < 0)
        {
            await _client.SendLineAsync($"Your skills fail as you try to rob {victim.Name}.");
            return;
        }

        int itemId = victim.Inventory[candidateIndex];
        var item = _world.Database.Items[itemId];

        // The steal removes from the victim's inventory and adds to the robber's, and
        // when that ADD fails the item is handed straight back to the victim and the robber gets the
        // ordinary "Your skills fail" line. Testing the robber's capacity up front is the same outcome
        // without the remove/re-add churn — an overloaded thief walks away empty-handed and the victim
        // never loses the item. (The currency branch has no such check in stock: coins transfer with no
        // capacity test at all, so RobCurrencyAsync deliberately stays unguarded.)
        if (WouldExceedEncumbranceAfterItemGain(item))
        {
            await _client.SendLineAsync($"Your skills fail as you try to rob {victim.Name}.");
            return;
        }

        long instanceId = candidateIndex < victim.InventoryInstanceIds.Count
            ? victim.InventoryInstanceIds[candidateIndex]
            : _world.CreateItemInstance(itemId);

        victim.Inventory.RemoveAt(candidateIndex);
        if (candidateIndex < victim.InventoryInstanceIds.Count)
            victim.InventoryInstanceIds.RemoveAt(candidateIndex);

        _player.Inventory.Add(itemId);
        _player.InventoryInstanceIds.Add(instanceId);

        _player.RecalculateEquipment(_world.Database);
        victim.RecalculateEquipment(_world.Database);

        await _client.SendLineAsync($"You successfully stole {item.Name} from {victim.Name}.");
    }
}
