using mmudreborn.Data.Models;
using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class IncomingMonsterAttackerConcurrencyTests
{
    // Reproduces the move-during-combat race that lost moves (e.g. a party leader couldn't move while
    // a follower fought): one thread SNAPSHOTS a player's incoming attackers (the mover's
    // HandleMovement / SnapshotDepartingMonsterAttackers) while ANOTHER thread MUTATES the same list (a
    // shared monster dying removes itself from every room player's list, on its killer's thread — see
    // CommandParser.Combat). With a plain List<> this threw "Collection was modified during enumeration"
    // and aborted the move; the per-player lock behind Player's accessors makes it safe. Remove the lock
    // and this test fails (the snapshot's ToList races the writer).
    [Fact]
    public async Task Snapshotting_incoming_attackers_while_another_thread_modifies_them_is_safe()
    {
        var player = new Player { Name = "Mover" };
        var monsters = Enumerable.Range(0, 64)
            .Select(i => new MonsterInstance { Template = new Monster { Name = $"m{i}" }, DisplayName = $"m{i}" })
            .ToList();
        foreach (var monster in monsters)
            player.AddIncomingMonsterAttacker(monster);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        Exception? readerEx = null;
        Exception? writerEx = null;

        // Reader: the mover repeatedly snapshotting + reading its incoming attackers.
        var reader = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    foreach (var attacker in player.SnapshotIncomingMonsterAttackers())
                        _ = attacker.IsDead;
                    _ = player.IncomingMonsterAttackerCount;
                    _ = player.HasIncomingMonsterAttacker(monsters[0]);
                }
            }
            catch (Exception ex) { readerEx = ex; }
        });

        // Writer: another player's combat thread pruning/refreshing this player's attackers.
        var writer = Task.Run(() =>
        {
            try
            {
                var rng = new Random(1);
                while (!cts.IsCancellationRequested)
                {
                    var monster = monsters[rng.Next(monsters.Count)];
                    player.RemoveIncomingMonsterAttacker(monster);
                    player.AddIncomingMonsterAttacker(monster);
                    player.RemoveIncomingMonsterAttackersWhere(static m => m.IsDead);
                    player.ReplaceIncomingMonsterAttackers(monsters);
                }
            }
            catch (Exception ex) { writerEx = ex; }
        });

        await Task.WhenAll(reader, writer);

        Assert.Null(readerEx);
        Assert.Null(writerEx);
    }
}
