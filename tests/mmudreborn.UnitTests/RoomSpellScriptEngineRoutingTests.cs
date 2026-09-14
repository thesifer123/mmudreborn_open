using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Room-spell pulses for ability-148 (Trigger Text Block) spells flow through ApplyRoomSpellPulse. A
// textblock that uses ONLY message/cast/random/failitem is served by the simple fast loop (it preserves
// per-pulse reprompt + Silver River coloring). A textblock that uses ANY other verb — an alignment/
// class/level gate, a teleport, an item op — must instead run through the full special-command engine,
// because the fast loop flat-splits on ':' and silently skips gate verbs, which used to make the
// trailing cast/teleport fire on every player regardless of alignment (White Forest noise 1079 burning
// good paladins mid-quest; church check 1147 ignoring its nomonsters/evil gate). This pins that routing
// decision so a future fast-loop edit can't quietly re-broaden it.
public sealed class RoomSpellScriptEngineRoutingTests
{
    [Theory]
    // Simple ambient/biome blocks the fast loop handles directly.
    [InlineData("random 9056")]                                  // Silvermere / Darkwood ambient flavor
    [InlineData("message 2654:cast 753")]                        // Silver River bash + battered damage
    [InlineData("failitem 690:message 2098:cast 526")]          // negate-then-cast (failitem is fast-path)
    public void Simple_message_cast_random_blocks_stay_on_the_fast_loop(string textBlock)
    {
        Assert.False(GameWorld.RoomSpellTextBlockNeedsScriptEngine(textBlock));
    }

    [Theory]
    // White Forest noise 1079: alignment gates + teleport + cast — must run the gate, not just the cast.
    [InlineData("evilaligned -50:goodaligned 39:teleport 1641 17\nevilaligned 40:teleport 1641 17:cast 1135")]
    // Church check 1147: nomonsters + evil gate before the cast.
    [InlineData("nomonsters:evilaligned 40:cast 1135")]
    // Class-filter rooms (thief/battle/magic filters): teleport wrong classes out.
    [InlineData("class 1:teleport 2982 17\nclass 2:teleport 2982 17")]
    // Pit escapes / branch checks: item + level gates + teleport.
    [InlineData("nomonsters:failability 152:roomitem 1954:teleport 2381 12:message 1625")]
    [InlineData("maxlevel 49:random 9383\nminlevel 50:nomonsters:summon 945")]
    public void Gated_or_teleporting_blocks_require_the_script_engine(string textBlock)
    {
        Assert.True(GameWorld.RoomSpellTextBlockNeedsScriptEngine(textBlock));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_block_does_not_need_the_engine(string? textBlock)
    {
        Assert.False(GameWorld.RoomSpellTextBlockNeedsScriptEngine(textBlock!));
    }
}
