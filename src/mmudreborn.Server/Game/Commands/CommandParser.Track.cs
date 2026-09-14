using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class CommandParser
{
    // TRACK: follow a player or monster's movement trail and report which way they
    // left the tracker's current room. Resolution is room-first then global.
    // Gating is implicit via the Tracking skill: a character with no
    // Tracking (Warriors, Mages, ...) rolls genrdn(0,100) < 0 every time and so always "fails" — only
    // Thieves and Rangers (ability 38 → Tracking > 0) ever get a hit.
    private async Task HandleTrack(string args)
    {
        // An overt act: clear the hide flag before resolving (matching the stock flag reset).
        _player.IsHidden = false;

        string name = (args ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            await _client.SendLineAsync("Syntax: TRACK {monster}");
            return;
        }

        // A named player anywhere (room first, then any online) — you usually track someone NOT here.
        var targetPlayer = _world.FindPlayerInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, name, _player)
                           ?? _world.FindOnlinePlayer(name);
        if (targetPlayer != null && !ReferenceEquals(targetPlayer, _player))
        {
            await ReportTrackTrailAsync(targetPlayer.Name, targetPlayer.MovementTrail);
            return;
        }

        var targetMonster = _world.FindMonsterInRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber, name)
                            ?? _world.FindMonsterByNameAnywhere(name);
        if (targetMonster != null)
        {
            string monsterName = string.IsNullOrWhiteSpace(targetMonster.DisplayName)
                ? targetMonster.Name
                : targetMonster.DisplayName;
            await ReportTrackTrailAsync(monsterName, targetMonster.MovementTrail);
            return;
        }

        // No trackable target (not found / item / self) → the stock failure line.
        await _client.SendLineAsync("Your tracking skills fail you this time.");
    }

    // Shared player/monster tracking body: walk the target's trail; for each past slot that equals
    // the tracker's current room, the room one slot newer is where they went, so the exit of `here`
    // leading there names the direction. Each match rolls genrdn(0,100) < Tracking; on success it prints
    // "<name> went <dir> from here." If nothing is reported, the stock failure line.
    private async Task ReportTrackTrailAsync(string targetName, MovementTrail? trail)
    {
        var here = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        bool reported = false;

        // Skill 0 ⇒ the roll can never succeed; skip the walk entirely (also the non-tracker fast path).
        if (here != null && trail != null && _player.Tracking > 0)
        {
            var exits = here.GetExitDefinitions();

            // Slot 0 is the target's current room; start at 1 so slot i-1 (where they went) is valid.
            for (int i = 1; i < trail.Count; i++)
            {
                var visited = trail[i];
                if (visited.Map != _player.CurrentMapNumber || visited.Room != _player.CurrentRoomNumber)
                    continue;

                var wentTo = trail[i - 1];
                foreach (var (direction, exit) in exits)
                {
                    if (!exit.HasDestination)
                        continue;
                    if (exit.TargetMap != wentTo.Map || exit.TargetRoom != wentTo.Room)
                        continue;

                    if (Random.Shared.Next(0, 100) < _player.Tracking)
                    {
                        reported = true;
                        await _client.SendLineAsync($"{targetName} went {direction} from here.");
                    }
                }
            }
        }

        if (!reported)
            await _client.SendLineAsync("Your tracking skills fail you this time.");
    }
}
