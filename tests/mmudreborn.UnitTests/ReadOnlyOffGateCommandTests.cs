using mmudreborn.Server;
using Xunit;

namespace mmudreborn.UnitTests;

// Pins which commands bypass the global WorldStateGate. The off-gate set must stay limited to
// commands that never touch live mutable world state: pure read-only info (who/top/`top gang`/hall),
// the bug-report subsystem (its own DB table), and the read-only SYSOP sub-commands. Anything that
// mutates world state has to keep serializing against the combat round, so a regression that lets a
// mutating verb slip into IsOffGateCommand would silently break the single-thread combat fidelity.
public sealed class ReadOnlyOffGateCommandTests
{
    [Theory]
    [InlineData("who")]
    [InlineData("scan")]        // resolver alias → who
    [InlineData("top")]
    [InlineData("top 25")]      // args don't change the verb
    [InlineData("top gang")]    // gang leaderboard is the STOCK `top gang` (resolves to the off-gate "top")
    [InlineData("topten")]
    [InlineData("hall")]
    [InlineData("halloffame")]
    [InlineData("  WHO  ")]     // trimmed + case-insensitive
    public void Recognizes_read_only_info_commands(string input)
    {
        Assert.True(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("bug")]                  // starts the report wizard
    [InlineData("bug something broke")]  // wizard with an inline title
    [InlineData("bugs")]                 // list
    [InlineData("listbugs")]             // resolver alias → bugs
    [InlineData("showbug 12")]
    [InlineData("completebug 12")]       // moderation write — bug table only
    [InlineData("verifybug 12")]
    [InlineData("stillbug 12")]
    [InlineData("stillpresentbug 12")]   // resolver alias → stillbug
    [InlineData("deletebug 12")]
    [InlineData("removebug 12")]         // resolver alias → deletebug
    public void Recognizes_bug_subsystem_commands(string input)
    {
        Assert.True(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("/bob hi there")]        // '/' prefix is always telepath
    [InlineData("telepath bob hi")]      // verb → whisper
    [InlineData("whisper bob hi")]
    [InlineData("auction WTS a sword")]
    [InlineData("gossip hey everyone")]  // message form delivers only
    [InlineData("broadcast hi")]         // join-channel range — does NOT break sneak
    [InlineData("br hi")]                // br → broadcast
    [InlineData("'hi there")]            // apostrophe = broadcast prefix (join-channel)
    [InlineData("-hi there")]            // dash = broadcast prefix
    public void Recognizes_pure_delivery_chat(string input)
    {
        Assert.True(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("say hi")]               // room speech — breaks sneak, stays gated
    [InlineData(".hi")]                  // dot say prefix — breaks sneak
    [InlineData("yell hi")]              // breaks sneak
    [InlineData("\"hi there")]           // quote = yell prefix — breaks sneak
    [InlineData("gossip")]               // bare gossip toggles a persisted flag — stays gated
    [InlineData("auction")]              // bare auction toggles a persisted flag — stays gated
    public void Rejects_sneak_breaking_or_persisted_chat(string input)
    {
        Assert.False(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("train stats")]          // interactive stat editor — blocks on input, must run off-gate
    [InlineData("train STATS")]          // case-insensitive
    [InlineData("  train   stats  ")]    // trimmed + collapsed spacing
    public void Recognizes_interactive_train_stats_editor(string input)
    {
        Assert.True(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("train")]                // level-up — synchronous self-mutation, stays gated
    [InlineData("train stats extra")]    // not the exact editor syntax — falls through to gated path
    [InlineData("train foo")]
    public void Rejects_train_level_and_malformed_train(string input)
    {
        Assert.False(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("sysop")]                // bare → help menu (read-only)
    [InlineData("sysop status")]
    [InlineData("sysop stat room 5")]    // stat → status; args don't change the sub-command
    [InlineData("sys list evil")]        // sysop abbreviates to "sys"
    [InlineData("sysop buffers")]
    [InlineData("sysop diag")]
    [InlineData("sysop report")]
    [InlineData("sysop map")]
    public void Recognizes_read_only_sysop_subcommands(string input)
    {
        Assert.True(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("attack goblin")]
    [InlineData("north")]
    [InlineData("get all")]
    [InlineData("cast 'fireball'")]
    [InlineData("rest")]
    [InlineData("stat")]                 // top-level stat sheet reads live state — intentionally gated
    [InlineData("inventory")]            // ditto
    [InlineData(".say something")]       // say prefix — breaks sneak
    [InlineData(">order minion")]        // pet-order targeting prefix
    public void Rejects_mutating_or_prefixed_commands(string? input)
    {
        Assert.False(CommandParser.IsOffGateCommand(input));
    }

    [Theory]
    [InlineData("sysop spawn 100")]      // mutates the room — must stay gated
    [InlineData("sysop heal")]
    [InlineData("sysop setlevel 5")]
    [InlineData("sysop giveitem 466")]
    [InlineData("sysop grantability bob 186 0")]
    [InlineData("sysop reset")]
    [InlineData("sysop configure genrate 15")]
    [InlineData("sysop restart")]
    [InlineData("sysop cleanup")]
    public void Rejects_mutating_sysop_subcommands(string input)
    {
        Assert.False(CommandParser.IsOffGateCommand(input));
    }
}
