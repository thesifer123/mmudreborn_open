using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Game.Combat;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class CombatEnginePvpMysticTests
{
    [Theory]
    [InlineData(PlayerCombatRoundAction.Punch, "punch")]
    [InlineData(PlayerCombatRoundAction.Kick, "kick")]
    [InlineData(PlayerCombatRoundAction.Jumpkick, "jumpkick")]
    public void Player_mystic_attack_against_player_uses_expected_pvp_verb(PlayerCombatRoundAction action, string expectedVerb)
    {
        var cls = new CharacterClass
        {
            CombatLvl = 5,
        };

        var attacker = new Player
        {
            Name = "Mystic",
            Level = 50,
            PartyAccuracyModifier = 500,
            PunchDamage = 10,
            KickDamage = 10,
            JumpkickDamage = 10,
            MinDamage = 10,
            MaxDamage = 20,
        };

        var target = new Player
        {
            Name = "Defender",
            CurrentHP = 1000,
            MaxHP = 1000,
            Health = 50,
            Agility = 0,
        };

        bool foundExpectedVerb = false;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            target.CurrentHP = target.MaxHP;
            attacker.PrepareCombatRound();
            var result = CombatEngine.PlayerAttackPlayer(attacker, target, cls, weapon: null, action: action);
            string combinedMessages = string.Join('\n', result.Messages.Concat(result.TargetMessages).Concat(result.RoomMessages));
            if (combinedMessages.Contains(expectedVerb, StringComparison.OrdinalIgnoreCase))
            {
                foundExpectedVerb = true;
                break;
            }
        }

        Assert.True(foundExpectedVerb, $"Expected PvP mystic attack to emit '{expectedVerb}' in combat text.");
    }
}
