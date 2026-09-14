using System;
using System.Linq;
using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #161 / stock fidelity: party invites must follow the stock model — a player can hold pending
// follow-invites from MULTIPLE leaders at once (INVITE never rejects a second
// inviter; the conflict resolves at FOLLOW time). A prior invite from someone else must NOT block
// a new invite.
public sealed class PartyInviteTests
{
    private static GameWorld NewWorld()
    {
        var db = new InMemoryGameDatabase();
        db.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, Name = "Square", Description = "x" };
        return new GameWorld(db, new InMemoryPlayerRepository());
    }

    private static Player AddPlayer(GameWorld world, string name)
    {
        var player = new Player { Name = name, CurrentMapNumber = 1, CurrentRoomNumber = 1, Client = new RecordingGameClient() };
        ((RecordingGameClient)player.Client).Player = player;
        world.AddPlayer(player);
        return player;
    }

    // Re-inviting a player who is ALREADY following you is a silent no-op in stock (the group add
    // returns 0, nobody is notified, the inviter still sees the confirmation). Otherwise they land in both
    // Members and Invited and `party` lists them twice -- once ranked, once "[Invited]" -- with the
    // phantom row never clearing, since the join that purges pending invites already happened.
    // Reported live after a board restart: Raijin shown as Backrank AND [Invited].
    [Fact]
    public void Re_inviting_a_current_follower_is_a_silent_no_op_with_no_phantom_invited_row()
    {
        var world = NewWorld();
        var leader = AddPlayer(world, "Fujin");
        var follower = AddPlayer(world, "Raijin");

        Assert.True(world.TryInviteToParty(leader, follower, out _, out _));
        Assert.True(world.TryJoinParty(follower, leader, out _, out _, out _, out _));

        // Already following: the group add is refused, so no slot is written and the invitee
        // is told nothing -- but the inviter still gets the unconditional confirmation.
        Assert.True(world.TryInviteToParty(leader, follower, out var msg, out var inviteeMsg));
        Assert.Equal("You have invited Raijin to follow you.", msg);
        Assert.Equal(string.Empty, inviteeMsg);
        Assert.False(world.HasPartyInvite("Raijin", "Fujin"));

        // And the party lists them exactly once, as a member rather than an invitee.
        var snapshot = world.GetPartySnapshot(leader);
        Assert.NotNull(snapshot);
        var rows = snapshot!.Members.Where(m => m.Name.Equals("Raijin", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Single(rows);
        Assert.False(rows[0].IsInvited);
    }

    [Fact]
    public void A_player_already_invited_by_someone_else_can_still_be_invited()
    {
        var world = NewWorld();
        var alpha = AddPlayer(world, "Alpha");
        var bravo = AddPlayer(world, "Bravo");
        var charlie = AddPlayer(world, "Charlie");

        Assert.True(world.TryInviteToParty(alpha, charlie, out _, out _));
        // The reported bug: Bravo's invite was rejected because Charlie already had Alpha's invite.
        Assert.True(world.TryInviteToParty(bravo, charlie, out var bravoMsg, out _));
        Assert.Equal("You have invited Charlie to follow you.", bravoMsg);

        // Charlie holds BOTH pending invites.
        Assert.True(world.HasPartyInvite("Charlie", "Alpha"));
        Assert.True(world.HasPartyInvite("Charlie", "Bravo"));
    }

    [Fact]
    public void Charlie_can_follow_either_leader_who_invited_him()
    {
        var world = NewWorld();
        var alpha = AddPlayer(world, "Alpha");
        var bravo = AddPlayer(world, "Bravo");
        var charlie = AddPlayer(world, "Charlie");

        world.TryInviteToParty(alpha, charlie, out _, out _);
        world.TryInviteToParty(bravo, charlie, out _, out _);

        // Charlie chooses Bravo; the join succeeds against the second inviter.
        Assert.True(world.TryJoinParty(charlie, bravo, out var msg, out var leaderName, out _, out _));
        Assert.Equal("Bravo", leaderName);
        Assert.Contains("following Bravo", msg);

        // Joining clears all his pending invites (incl. Alpha's), so a stale follow now needs a re-invite.
        Assert.False(world.HasPartyInvite("Charlie", "Alpha"));
        Assert.False(world.HasPartyInvite("Charlie", "Bravo"));
    }

    [Fact]
    public void Inviting_someone_already_in_a_party_is_allowed_so_they_can_switch()
    {
        var world = NewWorld();
        var alpha = AddPlayer(world, "Alpha");
        var bravo = AddPlayer(world, "Bravo");
        var charlie = AddPlayer(world, "Charlie");

        // Charlie joins Alpha's party.
        world.TryInviteToParty(alpha, charlie, out _, out _);
        Assert.True(world.TryJoinParty(charlie, alpha, out _, out _, out _, out _));

        // INVITE has NO already-in-a-party gate — Bravo may invite Charlie even though Charlie is
        // in Alpha's party. Charlie can then FOLLOW Bravo to switch (leaving Alpha's party cleanly).
        Assert.True(world.TryInviteToParty(bravo, charlie, out var msg, out _));
        Assert.Contains("invited Charlie", msg);

        Assert.True(world.TryJoinParty(charlie, bravo, out _, out var newLeader, out _, out _));
        Assert.Equal("Bravo", newLeader);
        Assert.Equal("Bravo", world.GetPartyLeaderName("Charlie"));
    }

    [Fact]
    public void Inviting_a_user_who_has_forgotten_you_is_rejected()
    {
        var world = NewWorld();
        var alpha = AddPlayer(world, "Alpha");
        var bravo = AddPlayer(world, "Bravo");

        // INVITE: the sole "You cannot invite %s." case is the forgotten-user test — the target has
        // FORGOTTEN/ignored the inviter.
        bravo.IgnoredPlayerNames.Add("Alpha");
        Assert.False(world.TryInviteToParty(alpha, bravo, out var msg, out _));
        Assert.Equal("You cannot invite Bravo.", msg);
    }

    [Fact]
    public void Uninviting_from_one_party_leaves_the_other_invite_intact()
    {
        var world = NewWorld();
        var alpha = AddPlayer(world, "Alpha");
        var bravo = AddPlayer(world, "Bravo");
        var charlie = AddPlayer(world, "Charlie");

        world.TryInviteToParty(alpha, charlie, out _, out _);
        world.TryInviteToParty(bravo, charlie, out _, out _);

        // Alpha removes (uninvites) Charlie; Bravo's pending invite must survive.
        Assert.True(world.TryRemoveFromParty(alpha, "Charlie", out _, out _, out _));
        Assert.False(world.HasPartyInvite("Charlie", "Alpha"));
        Assert.True(world.HasPartyInvite("Charlie", "Bravo"));

        // And Charlie can still follow Bravo.
        Assert.True(world.TryJoinParty(charlie, bravo, out _, out var leaderName, out _, out _));
        Assert.Equal("Bravo", leaderName);
    }
}
