using mmudreborn.Game;

namespace mmudreborn.Server;

// Recent-death log. NON-STOCK: the original records nothing about past deaths — no history, no killer, no
// location — so this ships behind SYSOP CONFIGURE DEATHLOG (default OFF, the same convention as every
// other non-stock toggle here) and surfaces only on the already non-stock `stat all` sheet.
//
// The killer is captured on the DAMAGE rather than the kill: by the time a death resolves
// (ExecuteForcedDeath / ProcessWorldTickDeath) the attacker is several layers out of scope, so the
// damage sites stamp Player.LastDamageSourceName and the death reads it. Deaths with no attacker at all
// (poison, bleeding out, a room spell) pass their cause in explicitly.
public partial class GameWorld
{
    public bool DeathLogEnabled { get; private set; }

    private void LoadDeathLogSetting()
    {
        DeathLogEnabled = PlayerRepo.GetServerSettingInt("DEATHLOG", 0) != 0;
    }

    public void SetDeathLogEnabled(bool enabled)
    {
        DeathLogEnabled = enabled;
        PlayerRepo.SetServerSettingInt("DEATHLOG", enabled ? 1 : 0);
    }

    /// <summary>
    /// Append this death to the character's log. No-op while the feature is off, so a realm running
    /// stock never accumulates the data in the first place. The room NAME is snapshotted here rather
    /// than resolved at display time — a data reseed can rename a room, and "where I died" should keep
    /// reading the way it read at the time.
    /// </summary>
    public void RecordPlayerDeath(Player player, int mapNumber, int roomNumber, string? killer = null)
    {
        if (!DeathLogEnabled)
            return;

        string roomName = GetRoom(mapNumber, roomNumber)?.Name ?? string.Empty;
        player.RecordDeath(DateTime.UtcNow, mapNumber, roomNumber, roomName, killer);
    }
}
