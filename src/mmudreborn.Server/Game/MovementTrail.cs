namespace mmudreborn.Game;

// The movement trail: a small fixed ring of the entity's most-recently-occupied rooms, newest first,
// used by TRACK to infer which way a target
// left a room. Stock keeps parallel room/map rings in the player record (
// ×20) and a room ring in the monster record (×10), shifting them on every non-suppressed move.
// We mirror that as one in-memory ring of (map, room) pairs.
//
// Cost note: this is the *target's* data (the Tracking skill gates the tracker, not the writer), so it
// must exist for any entity that can be tracked — but it is allocated lazily on first move and lives
// only in memory (never persisted). Entities that never change rooms (room-bound monsters, NPCs, a
// stationary player) allocate nothing. A move is an O(capacity) shift — identical to the stock move.
public sealed class MovementTrail
{
    public const int PlayerCapacity = 20;   // player ring length
    public const int MonsterCapacity = 10;  // monster ring length

    private readonly (int Map, int Room)[] _ring;
    private int _count;

    public MovementTrail(int capacity)
    {
        _ring = new (int Map, int Room)[capacity];
    }

    public int Count => _count;

    // Logical index 0 = newest (the room just entered).
    public (int Map, int Room) this[int index] => _ring[index];

    // Push the newly-entered room to slot 0, shifting older entries down one slot (the oldest falls off).
    public void Record(int map, int room)
    {
        for (int i = System.Math.Min(_count, _ring.Length - 1); i > 0; i--)
            _ring[i] = _ring[i - 1];

        _ring[0] = (map, room);
        if (_count < _ring.Length)
            _count++;
    }
}
