using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class CommandParser
{
    // QUESTALLPARTY registry — main-quest items a monster DROPS to the ground on death (its DropItem table),
    // which the player picks up and turns in elsewhere. In stock only ONE copy drops per kill, so a party of
    // N must kill the monster N times (each member needs their own to turn in). With SYSOP CONFIGURE
    // QUESTALLPARTY ON, HandleMonsterDeath hands one copy to EACH engaged player's inventory instead of
    // dropping a single copy to the room.
    //
    // Sourced from the quest walkthrough's "received when you kill" notes, filtered to items that are
    // actually a monster DropItem. The OTHER guide kill-items — Eternal Fire 935, Storm Spirit 941,
    // Heartstone 949, ancient blackwood heart 1533, massive black gem 1554, long golden chain 1555, black
    // retchweed 927 — are NOT ground drops; they deliver via the boss's area DeathSpell chain (e.g. efreeti's
    // "efreeti temp" #568, Targets 12), which is already room-wide to every engaged player, so they have no
    // party-tedium problem and are intentionally excluded.
    internal static readonly IReadOnlySet<int> QuestPartyDropItemIds = new HashSet<int>
    {
        621,   // severed head of Markus     (Commander Markus)
        622,   // locked wooden box          (Commander Markus)
        684,   // severed head of Goru-Nezar (Goru-Nezar)
        776,   // elf-head                   (woodelf ranger/druid/citizen/guard/wardancer)
        777,   // head of the woodelf lord   (woodelf lord)
        917,   // gleaming shard             (metallic monstrosity)
        931,   // iron crown                 (kobold king)
        948,   // obsidian talisman          (huge obsidian golem)
        1341,  // red parchment              (Dreadlord of Blood)
        1683,  // pile of spectral webbing   (arachnigoth / Mayor of Arlysia)
    };

    // QUESTALLPARTY: hand ONE copy of a quest ground-drop item to each engaged player's inventory instead of
    // dropping a single copy to the room. Each non-killer recipient is driven through a CommandParser bound
    // to THEIR session (so the inventory add + over-encumbrance fallback act on that player), mirroring
    // ApplyQuestKillProgressAsync. Engaged players are always in the dying monster's room (see
    // GetMonsterExperienceRecipients), so an over-encumbered recipient's copy falls at their own feet.
    private async Task DistributeQuestPartyDropAsync(int itemId, IReadOnlyList<Player> engagedPlayers)
    {
        foreach (var player in engagedPlayers)
        {
            if (ReferenceEquals(player, _player))
            {
                await GiveQuestPartyDropAsync(itemId);
                continue;
            }

            var playerClient = _world.GetClientForPlayer(player.Name);
            if (playerClient == null)
                continue;

            var playerParser = new CommandParser(playerClient, _world, player);
            await playerParser.GiveQuestPartyDropAsync(itemId);
        }
    }

    // Add the quest drop to THIS parser's player. Like a scripted giveitem, an item that would over-encumber
    // the player is dropped at their feet rather than refused; a carried item gets a short notice so the
    // player knows they received it (there is no textblock narrative on a ground-drop kill).
    private async Task GiveQuestPartyDropAsync(int itemId)
    {
        if (!_world.Database.Items.TryGetValue(itemId, out var item))
            return;

        if (TryAcquireInventoryItem(itemId, out _))
            await _client.SendLineAsync(GameAnsi.AttentionNotice($"{item.Name} has been added to your inventory."));
        else
            // A quest turn-in item that cannot fit in inventory must never be lost to a full floor.
            _world.DisposeOfItemInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, itemId);
    }

    // Quest-on-kill registry.
    //
    // Stock hardcodes the killed-monster -> quest-script mapping: the monster's own
    // data carries NONE of it (Abil-*/AbilVal-* are the monster's own stat buffs, DropItem-* are its loot,
    // DeathSpell is a combat debuff, and the quest bosses don't even drop the quest reward item). So on the
    // death of one of these bosses stock runs a "special command" text block whose lines are
    // alignment/stage-gated alternatives (`checkability F N` lower-bound + `testability F N` upper-bound)
    // that advance the killer's quest-progress flag (`giveability F N+1`) and apply the take/give item,
    // bonus exp, and flavour text/messages.
    //
    // We mirror that exactly: map the monster number -> its quest text block and run it through the same
    // gate engine that drives ask-dialogue steps (PerformTextBlockAsSpecialCommandAsync, the
    // special-command path). The block's own gates decide whether anything happens,
    // so a kill by a player who is at the wrong stage or alignment is a silent no-op — no extra checks here.
    //
    // Block ids were identified from game_data."TextBlocks" by their `giveability F N` values, cross-checked
    // against the quest-flag reference. Multi-alignment endgame bosses (dark phoenix, Dark
    // Mage, Zanthus) carry one gated line per quest flag (126 good / 127 neutral / 128 evil) inside a single
    // block, so the one entry serves all three alignment chains.
    private static readonly IReadOnlyDictionary<int, int> QuestKillTextBlocks = new Dictionary<int, int>
    {
        [409] = 1241,  // spectral knight     -> Good 126 13->14 (takeitem 954, giveitem 955)
        [463] = 1245,  // Gulgulthra          -> Good 126 14->15 (takeitem 955)
        [406] = 442,   // kobold king         -> Evil 128 4->5
        [452] = 1307,  // huge obsidian golem -> Evil 128 8->9
        [700] = 2896,  // dark phoenix        -> 126 17->18 / 127 11->12 / 128 12->13 (minlevel 40, +30M exp)
        [850] = 9537,  // Dark Mage           -> 126 25->26 / 127 19->20 / 128 21->22 (+175M exp)
        [215] = 9547,  // Zanthus the Lich    -> 126/127/128 28->29 (+50M exp, messages 3146/3150)
        [490] = 1417,  // dread mystic        -> Phoenix Feather 133 1->2 (giveitem 989)
        [478] = 1418,  // smuggler boss       -> Phoenix Feather 133 2->3 (giveitem 991)
        [594] = 2679,  // Pharaoh Rastep      -> Dao Lord 134 9->10 (summon 595/596)
        [597] = 2688,  // Dao Lord            -> Dao Lord 134 10->11 (+2M exp)
    };

    // Invoked from HandleMonsterDeath. Runs the dying boss's quest script through the shared gate engine
    // for EVERY engaged player in the room — not just the killer. The script's checkability/testability/
    // alignment gates self-select the right line (or none) per player, so a player at the wrong stage or
    // alignment is a silent no-op, and a stage-already-advanced player (e.g. the killer, who the DeathSpell
    // chain may have already advanced) re-runs to a no-op (idempotent).
    //
    // Why room-wide: in stock these bosses don't carry the quest mapping in their own data — the kill
    // delivers the quest via the boss's DeathSpell, and that DeathSpell is an AREA spell (Targets 12, e.g.
    // "obsidian golem temp" #578 for the huge obsidian golem #452). The kill's area cast
    // lands a duration-1 "temp" marker on every engaged player in the room (the combat flag plus a
    // same-room check). When that marker expires, the spell-termination upkeep fires its ability-151
    // chain → a no-target cast of the <text> spell at that player → ability 148 → the special-command engine on
    // THAT player (golem: 578 → chain 579 → text block 1307 = Evil 128 8→9). So the quest script runs once
    // per engaged holder, advancing everyone who fought it — not only whoever landed the killing blow. We
    // collapse the temp-marker/upkeep mechanism into a direct per-engaged-player run here.
    private async Task ApplyQuestKillProgressAsync(MonsterInstance monster, IReadOnlyList<Player> engagedPlayers)
    {
        if (monster.Template == null ||
            !QuestKillTextBlocks.TryGetValue(monster.Template.Number, out var questBlockId))
        {
            return;
        }

        foreach (var player in engagedPlayers)
        {
            // The killer's own session runs the block directly; every other engaged player runs it in a
            // CommandParser bound to THEIR session (the quest verbs — giveability/checkability/takeitem/
            // giveitem/exp — act on that parser's _player), matching the stock per-holder delivery.
            if (ReferenceEquals(player, _player))
            {
                await PerformTextBlockAsSpecialCommandAsync(questBlockId);
                continue;
            }

            var playerClient = _world.GetClientForPlayer(player.Name);
            if (playerClient == null)
                continue;

            var playerParser = new CommandParser(playerClient, _world, player);
            await playerParser.PerformTextBlockAsSpecialCommandAsync(questBlockId);
        }
    }
}
