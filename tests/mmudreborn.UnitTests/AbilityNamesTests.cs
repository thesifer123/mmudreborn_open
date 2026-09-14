using mmudreborn.Game;
using Xunit;

namespace mmudreborn.UnitTests;

// The `abil` command renders rows as "<name>(<id>)  <value>" using AbilityNames.Format. These
// tests pin the contract: well-known stock ability ids return their audited name; unknown ids
// fall back to a placeholder so callers can still print a row.
public sealed class AbilityNamesTests
{
    [Theory]
    [InlineData(2, "Alter Defence Value(2)")]
    [InlineData(7, "Alter Damage Resistance Value(7)")]
    [InlineData(13, "Alter User Light(13)")]
    [InlineData(27, "Stealth(27)")]
    [InlineData(31, "Bash(31)")]
    [InlineData(58, "Alter Critical Hit Chance(58)")]
    [InlineData(70, "Spellcasting(70)")]
    [InlineData(186, "Perfect Stealth(186)")]
    [InlineData(187, "Meditate(187)")]
    public void Format_returns_audit_name_with_id_suffix(int abilityId, string expected)
    {
        Assert.Equal(expected, AbilityNames.Format(abilityId));
    }

    [Theory]
    [InlineData(0)]    // "No ability" — excluded from the lookup, falls through
    [InlineData(200)]  // Beyond audit range (0-187)
    [InlineData(1002)] // Para's custom ids that aren't in our name map
    public void Format_unknown_ids_fall_back_to_generic_placeholder(int abilityId)
    {
        Assert.Equal($"Ability({abilityId})", AbilityNames.Format(abilityId));
    }
}
