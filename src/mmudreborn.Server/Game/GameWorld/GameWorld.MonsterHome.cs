using System;
using System.Collections.Generic;
using System.Linq;
using mmudreborn.Data;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

// "home {monster}" QOL lookup (gated by SYSOP CONFIGURE QOL home / the QOLFUNCTIONS master): for a unique
// timer-regen boss (GameLimit==1 && RegenTime>0 — the only mobs with a meaningful, trackable home) report
// where it spawns, whether it is currently alive and where, and — when dead — roughly when its RegenTime
// gate next lets it respawn. Strictly read-only; it never triggers a lazy spawn.
public partial class GameWorld
{
    // One spawn-origin ("home") room for a unique timer-regen monster. EnterSpawn = it is the room's
    // Room.NPC primary (placed when a player enters); otherwise it is a lair/by-number background spawn.
    public readonly record struct MonsterHomeRoom(int Map, int Room, string RoomName, bool EnterSpawn);

    public sealed record MonsterHomeReport(
        int Number,
        string Name,
        int RegenTimeHours,
        IReadOnlyList<MonsterHomeRoom> HomeRooms,
        bool IsActive,
        int CurrentMap,
        int CurrentRoom,
        string CurrentRoomName,
        bool InHomeRoom,
        DateTime? DiedAtUtc,
        DateTime? RespawnReadyAtUtc);

    // Reverse index (monster number -> home rooms), limited to timer-regen uniques. Lazily built and
    // rebuilt whenever ReloadGameData swaps the database reference (same pattern as the other derived caches).
    private Dictionary<int, List<MonsterHomeRoom>>? _timerRegenHomeRooms;
    private IGameDatabase? _timerRegenHomeRoomsDb;
    private readonly object _timerRegenHomeRoomsLock = new();

    private bool IsTimerRegenNumber(int monsterNumber)
        => Database.Monsters.TryGetValue(monsterNumber, out var template) && IsTimerRegenMonster(template);

    private Dictionary<int, List<MonsterHomeRoom>> GetTimerRegenHomeRoomIndex()
    {
        lock (_timerRegenHomeRoomsLock)
        {
            if (_timerRegenHomeRooms != null && ReferenceEquals(_timerRegenHomeRoomsDb, _database))
                return _timerRegenHomeRooms;

            var index = new Dictionary<int, List<MonsterHomeRoom>>();

            void Add(int number, Room room, bool enterSpawn)
            {
                if (!index.TryGetValue(number, out var list))
                    index[number] = list = [];
                if (!list.Any(h => h.Map == room.MapNumber && h.Room == room.RoomNumber))
                    list.Add(new MonsterHomeRoom(room.MapNumber, room.RoomNumber, room.Name, enterSpawn));
            }

            foreach (var room in Database.Rooms.Values)
            {
                // Enter-spawn primary (Room.NPC).
                if (room.NPC > 0 && IsTimerRegenNumber(room.NPC))
                    Add(room.NPC, room, enterSpawn: true);

                // Lair / by-number background spawn — reuse the authoritative candidate resolver so the
                // home room matches exactly what the spawner would place here.
                if (CanRoomGenerateLairMonsters(room))
                {
                    foreach (var candidate in GetLairMonsterCandidates(room))
                        if (IsTimerRegenMonster(candidate) && candidate.Number != room.NPC)
                            Add(candidate.Number, room, enterSpawn: false);
                }
            }

            _timerRegenHomeRooms = index;
            _timerRegenHomeRoomsDb = _database;
            return index;
        }
    }

    // Currently-live instance of a specific monster number, if any, WITHOUT forcing a lazy spawn (scans
    // only rooms already instantiated). Unique GameLimit-1 mobs have at most one live instance.
    private MonsterInstance? FindLiveMonsterInstance(int monsterNumber)
    {
        lock (_monsterLock)
        {
            foreach (var list in _roomMonsters.Values)
                foreach (var monster in list)
                    if (!monster.IsDead && monster.Template.Number == monsterNumber)
                        return monster;
        }
        return null;
    }

    // Build a home report for every timer-regen unique whose name matches the query (case-insensitive
    // substring). Returns at most maxResults, alphabetised. Empty when nothing matches.
    public IReadOnlyList<MonsterHomeReport> GetMonsterHomeReports(string nameQuery, int maxResults = 15)
    {
        if (string.IsNullOrWhiteSpace(nameQuery))
            return [];

        string query = nameQuery.Trim();
        var index = GetTimerRegenHomeRoomIndex();

        var matches = Database.Monsters.Values
            .Where(IsTimerRegenMonster)
            .Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            // Exact / starts-with first so "home dragon" surfaces the closest names before partials.
            .OrderByDescending(m => m.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(m => m.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .ToList();

        var reports = new List<MonsterHomeReport>(matches.Count);
        foreach (var template in matches)
        {
            var homeRooms = index.TryGetValue(template.Number, out var list)
                ? (IReadOnlyList<MonsterHomeRoom>)list
                : [];

            var live = FindLiveMonsterInstance(template.Number);
            bool inHome = live != null
                && homeRooms.Any(h => h.Map == live.MapNumber && h.Room == live.RoomNumber);

            DateTime? diedAt = _monsterRegenLastDeathUtc.TryGetValue(template.Number, out var d) ? d : null;
            // Nominal respawn eligibility (RegenTime hours after death). Stock re-rolls a ±12.5% jitter
            // on each spawn attempt, so the true window is roughly [0.875x, 1.125x] of this; null once the
            // mob is alive or has no recorded death (eligible now).
            DateTime? respawnReadyAt = (live == null && diedAt.HasValue)
                ? diedAt.Value.AddHours(template.RegenTime)
                : null;

            reports.Add(new MonsterHomeReport(
                template.Number,
                template.Name,
                template.RegenTime,
                homeRooms,
                IsActive: live != null,
                CurrentMap: live?.MapNumber ?? 0,
                CurrentRoom: live?.RoomNumber ?? 0,
                CurrentRoomName: live != null ? (GetRoom(live.MapNumber, live.RoomNumber)?.Name ?? "") : "",
                InHomeRoom: inHome,
                DiedAtUtc: diedAt,
                RespawnReadyAtUtc: respawnReadyAt));
        }

        return reports;
    }
}
