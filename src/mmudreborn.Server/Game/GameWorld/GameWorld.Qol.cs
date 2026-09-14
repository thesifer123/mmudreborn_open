using System;
using System.Collections.Generic;

namespace mmudreborn.Server;

// The non-stock, player-facing quality-of-life COMMANDS a sysop can switch on/off so a realm can run
// pure-stock or opt into our conveniences. Each is gated at its dispatch/handler site by
// GameWorld.IsQolEnabled(feature). (Bug tracker is gated separately — it is tooling, not a gameplay command.)
public enum QolFeature
{
    GetAll,     // bulk "get all" / "get coins" / "get money" pickup (stock get is one item at a time)
    StatAll,    // the rich "stat all" sheet (stock stat is the compact Megamud-parsed sheet only)
    Abilities,  // the "abil" / "abilities" listing (stock has no ability-inspection command)
    Room,       // the "room" location/regen/light diagnostics command
    Hall,       // the "hall" / "halloffame" fallen-hero leaderboard
    SetLook,    // "set look modern|traditional" room-description rendering toggle
    Home,       // the "home <monster>" unique-boss spawn-origin / respawn-timer lookup
    WhoWeb,     // the "web-who" list of characters present on the web (telepath-eligible), mirroring WHO
    BulkQuantity, // a leading count on GET/DROP/GIVE/BUY/SELL of an ITEM ("get 10 torch") — stock counts only coins
    SpellRemoves, // a cast that strips a spell on its remove list still applies its own buff (stock: that cast ends at the removal)
}

// Per-feature override of the QOLFUNCTIONS master switch. Inherit = follow the master; ForceOn/ForceOff =
// pin this one feature regardless of the master. Values are the persisted ints (0/1/2) — do not reorder.
public enum QolOverride
{
    Inherit = 0,
    ForceOn = 1,
    ForceOff = 2,
}

public partial class GameWorld
{
    // Master QOL switch. Default OFF = stock-first (matches the project's stock-fidelity-default principle): a
    // realm boots pure-stock and the sysop opts INTO the convenience commands. Persisted as "QOLFUNCTIONS".
    public bool QolFunctionsEnabled { get; private set; }

    private readonly Dictionary<QolFeature, QolOverride> _qolOverrides = new();

    private const string QolMasterSettingKey = "QOLFUNCTIONS";

    private static string QolOverrideSettingKey(QolFeature feature) => "QOL_" + feature switch
    {
        QolFeature.GetAll => "GETALL",
        QolFeature.StatAll => "STATALL",
        QolFeature.Abilities => "ABIL",
        QolFeature.Room => "ROOM",
        QolFeature.Hall => "HALL",
        QolFeature.SetLook => "SETLOOK",
        QolFeature.Home => "HOME",
        QolFeature.WhoWeb => "WHOWEB",
        QolFeature.BulkQuantity => "QTY",
        QolFeature.SpellRemoves => "SPELLS",
        _ => feature.ToString().ToUpperInvariant(),
    };

    private void LoadQolSettings()
    {
        QolFunctionsEnabled = PlayerRepo.GetServerSettingInt(QolMasterSettingKey, 0) != 0;
        foreach (QolFeature feature in Enum.GetValues<QolFeature>())
        {
            int raw = Math.Clamp(PlayerRepo.GetServerSettingInt(QolOverrideSettingKey(feature), 0), 0, 2);
            _qolOverrides[feature] = (QolOverride)raw;
        }
    }

    // Effective state of one QOL feature: its override wins when set, otherwise it follows the master switch.
    public bool IsQolEnabled(QolFeature feature)
        => _qolOverrides.TryGetValue(feature, out var ov) && ov != QolOverride.Inherit
            ? ov == QolOverride.ForceOn
            : QolFunctionsEnabled;

    public QolOverride GetQolOverride(QolFeature feature)
        => _qolOverrides.TryGetValue(feature, out var ov) ? ov : QolOverride.Inherit;

    public void SetQolFunctionsEnabled(bool enabled)
    {
        QolFunctionsEnabled = enabled;
        PlayerRepo.SetServerSettingInt(QolMasterSettingKey, enabled ? 1 : 0);
    }

    public void SetQolOverride(QolFeature feature, QolOverride ov)
    {
        _qolOverrides[feature] = ov;
        PlayerRepo.SetServerSettingInt(QolOverrideSettingKey(feature), (int)ov);
    }

    // Bug tracker is non-stock TOOLING (not a gameplay command), so it gets its OWN switch independent of the
    // QOL master. Default ON — it's a useful reporting tool and disabling it by default would silently drop
    // reports; a sysop wanting a clean stock realm can turn it off. Persisted as "BUGTRACKER".
    public bool BugTrackerEnabled { get; private set; } = true;

    private void LoadBugTrackerSetting()
        => BugTrackerEnabled = PlayerRepo.GetServerSettingInt("BUGTRACKER", 1) != 0;

    public void SetBugTrackerEnabled(bool enabled)
    {
        BugTrackerEnabled = enabled;
        PlayerRepo.SetServerSettingInt("BUGTRACKER", enabled ? 1 : 0);
    }
}
