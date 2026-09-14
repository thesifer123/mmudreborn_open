using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class GameWorld
{
    public bool IsInParty(string playerName)
    {
        lock (_partyLock)
        {
            return _partyLeaderByMember.ContainsKey(playerName);
        }
    }

    public bool IsPartyLeader(string playerName)
    {
        lock (_partyLock)
        {
            return _parties.ContainsKey(playerName);
        }
    }

    public string? GetPartyLeaderName(string playerName)
    {
        lock (_partyLock)
        {
            return _partyLeaderByMember.GetValueOrDefault(playerName);
        }
    }

    public List<Player> GetFollowingPartyMembers(string leaderName)
    {
        lock (_partyLock)
        {
            if (!_parties.TryGetValue(leaderName, out var party))
                return [];

            return party.Members
                .Where(name => !name.Equals(leaderName, StringComparison.OrdinalIgnoreCase))
                .Select(name => _onlinePlayers.GetValueOrDefault(name))
                .Where(player => player != null)
                .Cast<Player>()
                .ToList();
        }
    }

    /// <summary>
    /// All online party members of <paramref name="playerName"/>'s party EXCEPT the caller, resolved
    /// whether the caller is the leader or a follower. Used by SHARE party mode (the
    /// currency share walks the follow-chain regardless of room).
    /// </summary>
    public List<Player> GetAllPartyMembers(string playerName)
    {
        lock (_partyLock)
        {
            var leaderName = _partyLeaderByMember.GetValueOrDefault(playerName);
            if (leaderName == null || !_parties.TryGetValue(leaderName, out var party))
                return [];

            return party.Members
                .Where(name => !name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                .Select(name => _onlinePlayers.GetValueOrDefault(name))
                .Where(player => player != null)
                .Cast<Player>()
                .ToList();
        }
    }

    public bool TryStartDragging(Player dragger, Player target, out string selfMessage, out string? targetMessage)
    {
        selfMessage = "";
        targetMessage = null;

        if (target.Name.Equals(dragger.Name, StringComparison.OrdinalIgnoreCase))
        {
            selfMessage = "Why would you want to drag yourself around?";
            return false;
        }

        if (IsInParty(dragger.Name))
        {
            selfMessage = "You may not drag someone while in a party!";
            return false;
        }

        if (!target.IsUnconscious)
        {
            selfMessage = $"{target.Name} is too healthy to be dragged.";
            return false;
        }

        lock (_dragLock)
        {
            if (_draggerByTarget.ContainsKey(target.Name))
            {
                selfMessage = $"{target.Name} is already being dragged.";
                return false;
            }

            if (_dragTargetByDragger.TryGetValue(dragger.Name, out var currentTargetName))
            {
                selfMessage = currentTargetName.Equals(target.Name, StringComparison.OrdinalIgnoreCase)
                    ? $"{target.Name} is already being dragged."
                    : $"You are already dragging {currentTargetName}.";
                return false;
            }

            _dragTargetByDragger[dragger.Name] = target.Name;
            _draggerByTarget[target.Name] = dragger.Name;
        }

        selfMessage = $"You are now dragging {target.Name}.";
        targetMessage = $"{dragger.Name} is dragging you around.";
        return true;
    }

    public bool TryGetDraggedPlayer(Player dragger, out Player draggedPlayer)
    {
        draggedPlayer = null!;

        string? targetName;
        lock (_dragLock)
        {
            if (!_dragTargetByDragger.TryGetValue(dragger.Name, out targetName))
                return false;
        }

        if (string.IsNullOrWhiteSpace(targetName) || !_onlinePlayers.TryGetValue(targetName, out var target) || !target.IsUnconscious)
        {
            StopDraggingForDragger(dragger);
            return false;
        }

        draggedPlayer = target;
        return true;
    }

    public void StopDraggingForPlayer(Player player, bool notify = true)
    {
        foreach (var (playerName, message) in RemoveDragStatesForPlayer(player.Name, notify))
            SendToPlayer(playerName, message);
    }

    public void StopDraggingTargetIfRecovered(Player target)
    {
        if (target.IsUnconscious)
            return;

        foreach (var (playerName, message) in RemoveDragStateForTarget(target.Name, notify: true))
            SendToPlayer(playerName, message);
    }

    public bool TryStopDraggingForDragger(Player dragger, out string message)
    {
        message = "";
        var messages = RemoveDragStateForDragger(dragger.Name, notify: true);
        var selfMessage = messages.FirstOrDefault(candidate =>
            candidate.PlayerName.Equals(dragger.Name, StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(selfMessage.Message))
            return false;

        message = selfMessage.Message;
        return true;
    }

    private void StopDraggingForDragger(Player dragger)
    {
        foreach (var (playerName, message) in RemoveDragStateForDragger(dragger.Name, notify: true))
            SendToPlayer(playerName, message);
    }

    private List<(string PlayerName, string Message)> RemoveDragStatesForPlayer(string playerName, bool notify)
    {
        var messages = new List<(string PlayerName, string Message)>();

        lock (_dragLock)
        {
            if (_dragTargetByDragger.TryGetValue(playerName, out var targetName))
            {
                _dragTargetByDragger.Remove(playerName);
                _draggerByTarget.Remove(targetName);

                if (notify)
                    messages.Add((playerName, $"You are no longer dragging {targetName}."));
            }

            if (_draggerByTarget.TryGetValue(playerName, out var draggerName))
            {
                _draggerByTarget.Remove(playerName);
                _dragTargetByDragger.Remove(draggerName);

                if (notify)
                    messages.Add((draggerName, $"You are no longer dragging {playerName}."));
            }
        }

        return messages;
    }

    private List<(string PlayerName, string Message)> RemoveDragStateForTarget(string targetName, bool notify)
    {
        var messages = new List<(string PlayerName, string Message)>();

        lock (_dragLock)
        {
            if (!_draggerByTarget.TryGetValue(targetName, out var draggerName))
                return messages;

            _draggerByTarget.Remove(targetName);
            _dragTargetByDragger.Remove(draggerName);

            if (notify)
                messages.Add((draggerName, $"You are no longer dragging {targetName}."));
        }

        return messages;
    }

    private List<(string PlayerName, string Message)> RemoveDragStateForDragger(string draggerName, bool notify)
    {
        var messages = new List<(string PlayerName, string Message)>();

        lock (_dragLock)
        {
            if (!_dragTargetByDragger.TryGetValue(draggerName, out var targetName))
                return messages;

            _dragTargetByDragger.Remove(draggerName);
            _draggerByTarget.Remove(targetName);

            if (notify)
                messages.Add((draggerName, $"You are no longer dragging {targetName}."));
        }

        return messages;
    }

    public bool HasPartyInvite(string playerName, string leaderName)
    {
        lock (_partyLock)
        {
            return HasPartyInviteFrom(playerName, leaderName);
        }
    }

    // --- Pending-invite multi-map helpers. Callers must already hold _partyLock. A player may hold
    // pending follow-invites from several leaders at once (INVITE never blocks a second
    // inviter); _partyInvitesByInvitee maps invitee -> the set of inviting leaders. ---

    private void AddPartyInvite(string invitee, string leaderName)
    {
        if (!_partyInvitesByInvitee.TryGetValue(invitee, out var leaders))
        {
            leaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _partyInvitesByInvitee[invitee] = leaders;
        }
        leaders.Add(leaderName);
    }

    private bool HasPartyInviteFrom(string invitee, string leaderName)
        => _partyInvitesByInvitee.TryGetValue(invitee, out var leaders) && leaders.Contains(leaderName);

    // Drop ONE leader's invite of this invitee (that leader uninvited them / disbanded). Invites from
    // other leaders are left intact so the invitee can still follow one of them.
    private void RemovePartyInvite(string invitee, string leaderName)
    {
        if (_partyInvitesByInvitee.TryGetValue(invitee, out var leaders)
            && leaders.Remove(leaderName) && leaders.Count == 0)
            _partyInvitesByInvitee.Remove(invitee);
    }

    // Drop ALL pending invites for this invitee and scrub them from every inviting leader's Invited
    // list. Used when the invitee joins a party (or leaves / dies) — they can no longer be a pending
    // invitee of anyone.
    private void PurgeAllPartyInvitesFor(string invitee)
    {
        if (!_partyInvitesByInvitee.Remove(invitee, out var leaders))
            return;
        foreach (var leaderName in leaders)
            if (_parties.TryGetValue(leaderName, out var party))
                party.Invited.RemoveWhere(name => name.Equals(invitee, StringComparison.OrdinalIgnoreCase));
    }

    public PartySnapshot? GetPartySnapshot(Player viewer)
    {
        lock (_partyLock)
        {
            if (!_partyLeaderByMember.TryGetValue(viewer.Name, out var leaderName))
                return null;

            if (!_parties.TryGetValue(leaderName, out var party))
                return null;

            var members = new List<PartyMemberSnapshot>();

            foreach (var memberName in party.Members)
            {
                var onlinePlayer = GetAllOnlinePlayers().FirstOrDefault(player => player.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
                if (onlinePlayer == null)
                    continue;

                Database.Classes.TryGetValue(onlinePlayer.ClassId, out var cls);
                bool hasMagicClass = cls != null && cls.MageryType != 0;

                members.Add(new PartyMemberSnapshot
                {
                    Name = onlinePlayer.Name,
                    LastName = onlinePlayer.LastName,
                    ClassName = cls?.Name ?? "",
                    HasMana = hasMagicClass,
                    UsesKai = cls != null && Player.UsesKai(cls),
                    ManaPercent = !hasMagicClass
                        ? 0
                        : onlinePlayer.MaxMana <= 0
                            ? 100
                            : Math.Clamp(onlinePlayer.CurrentMana * 100 / onlinePlayer.MaxMana, 0, 100),
                    HitsPercent = onlinePlayer.MaxHP <= 0 ? 0 : Math.Clamp(onlinePlayer.CurrentHP * 100 / onlinePlayer.MaxHP, 0, 100),
                    Rank = party.Ranks.GetValueOrDefault(memberName, PartyRank.Middle),
                    IsInvited = false,
                    IsPoisoned = onlinePlayer.PoisonLevel > 0,
                    IsResting = onlinePlayer.IsResting,
                    IsMeditating = onlinePlayer.IsMeditating,
                });
            }

            foreach (var invitedName in party.Invited)
            {
                var invitedPlayer = GetAllOnlinePlayers().FirstOrDefault(player => player.Name.Equals(invitedName, StringComparison.OrdinalIgnoreCase));
                CharacterClass? invitedClass = null;
                if (invitedPlayer != null)
                    Database.Classes.TryGetValue(invitedPlayer.ClassId, out invitedClass);

                bool hasMagicClass = invitedClass != null && invitedClass.MageryType != 0;

                members.Add(new PartyMemberSnapshot
                {
                    Name = invitedPlayer?.Name ?? invitedName,
                    LastName = invitedPlayer?.LastName ?? "",
                    ClassName = invitedClass?.Name ?? "",
                    HasMana = hasMagicClass,
                    UsesKai = invitedClass != null && Player.UsesKai(invitedClass),
                    ManaPercent = !hasMagicClass || invitedPlayer == null
                        ? 0
                        : invitedPlayer.MaxMana <= 0
                            ? 100
                            : Math.Clamp(invitedPlayer.CurrentMana * 100 / invitedPlayer.MaxMana, 0, 100),
                    HitsPercent = invitedPlayer == null || invitedPlayer.MaxHP <= 0 ? 0 : Math.Clamp(invitedPlayer.CurrentHP * 100 / invitedPlayer.MaxHP, 0, 100),
                    Rank = party.Ranks.GetValueOrDefault(invitedName, PartyRank.Middle),
                    IsInvited = true,
                    IsPoisoned = invitedPlayer?.PoisonLevel > 0,
                    IsResting = invitedPlayer?.IsResting ?? false,
                    IsMeditating = invitedPlayer?.IsMeditating ?? false,
                });
            }

            members = members
                .OrderBy(member => member.IsInvited ? 1 : 0)
                .ThenBy(member => member.IsInvited ? 0 : GetPartyDisplayRankOrder(member.Rank))
                .ThenBy(member => member.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new PartySnapshot
            {
                LeaderName = leaderName,
                ViewerIsLeader = leaderName.Equals(viewer.Name, StringComparison.OrdinalIgnoreCase),
                Members = members,
            };
        }
    }

    private static int GetPartyDisplayRankOrder(PartyRank rank)
    {
        return rank switch
        {
            PartyRank.Front => 0,
            PartyRank.Middle => 1,
            PartyRank.Back => 2,
            _ => 3,
        };
    }

    public bool TryInviteToParty(Player leader, Player target, out string selfMessage, out string inviteeMessage)
    {
        lock (_partyLock)
        {
            selfMessage = "";
            inviteeMessage = "";

            if (target.Name.Equals(leader.Name, StringComparison.OrdinalIgnoreCase))
            {
                selfMessage = "Why would you invite yourself?";
                return false;
            }

            if (_partyLeaderByMember.TryGetValue(leader.Name, out var currentLeader)
                && !currentLeader.Equals(leader.Name, StringComparison.OrdinalIgnoreCase))
            {
                selfMessage = $"You cannot invite {target.Name}.";
                return false;
            }

            // Bug #161 / stock fidelity: a prior invite from SOMEONE ELSE must NOT block this invite, and
            // the target already being IN a party must NOT block it either — the stock invite
            // add the invitee to each leader's own group slots regardless, and FOLLOW switches parties
            // cleanly (see TryJoinParty / VacatePartyForSwitch). The ONLY stock "You cannot invite %s."
            // case is the forgotten-user test: the target has FORGOTTEN /
            // ignored the inviter. (Was an invented already-in-a-party rejection that stock never sends and
            // that made party-switching unreachable — you could never be invited while in a party.)
            if (target.IgnoredPlayerNames.Contains(leader.Name))
            {
                selfMessage = $"You cannot invite {target.Name}.";
                return false;
            }

            var party = GetOrCreatePartyState(leader.Name);
            int currentCount = party.Members.Count + party.Invited.Count;
            if (currentCount >= MaxPartyMembers)
            {
                selfMessage = $"You cannot invite {target.Name}.";
                return false;
            }

            // Re-inviting someone who is ALREADY following you is a silent no-op, exactly as in the
            // stock. INVITE adds to the group, which scans the inviter's five
            // group slots and returns 0 when the target already occupies one — no slot
            // is written. The invitee notification sits INSIDE `if (cVar1 != '\0')` so they hear
            // nothing, while "You have invited %s to follow you." is printed unconditionally to the
            // inviter afterwards. There is no "already following" refusal string anywhere in
            // INVITE: its only rejections are the Syntax line, "You don't see %s here.", "Why
            // would you invite yourself?" and the forgotten-user "You cannot invite %s."
            //
            // Without this the target landed in Invited while still a ranked member and `party`
            // listed them twice — once ranked, once "[Invited]" — with the phantom row never
            // clearing, since PurgeAllPartyInvitesFor runs at JOIN time and they had already joined.
            // (Reported live: Raijin shown as Backrank AND [Invited].) Being invited while in SOMEONE
            // ELSE'S party is still allowed — that is how party-switching works, see the note above.
            if (party.Members.Any(name => name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)))
            {
                selfMessage = $"You have invited {target.Name} to follow you.";
                inviteeMessage = string.Empty;   // the group add was refused — notify nobody
                return true;
            }

            party.Invited.Add(target.Name);
            AddPartyInvite(target.Name, leader.Name);
            selfMessage = $"You have invited {target.Name} to follow you.";
            inviteeMessage = $"{leader.Name} has invited you to follow {leader.HimHer}.";
            return true;
        }
    }

    public bool TryJoinParty(Player joiner, Player target, out string selfMessage, out string leaderName, out string? leaderMessage, out List<string> disbandedOldPartyMembers)
    {
        lock (_partyLock)
        {
            selfMessage = "";
            leaderName = "";
            leaderMessage = null;
            disbandedOldPartyMembers = [];

            if (target.Name.Equals(joiner.Name, StringComparison.OrdinalIgnoreCase))
            {
                selfMessage = "Why would you follow yourself?";
                return false;
            }

            // FOLLOW has NO already-in-a-party gate — following someone else while
            // already in a party just switches you; the only gate is the invite check below. Stock does it
            // by silently dissolving your old party from your side, orphaning your former members (the
            // "party that shouldn't still be together" bug). We instead vacate the old party CLEANLY once
            // the switch is guaranteed (VacatePartyForSwitch, below). Was an invented "You are already in a
            // party at the present time." refusal that stock never sends.
            leaderName = _partyLeaderByMember.GetValueOrDefault(target.Name) ?? target.Name;
            if (!HasPartyInviteFrom(joiner.Name, leaderName))
            {
                selfMessage = "You must be invited first!";
                return false;
            }

            var party = GetOrCreatePartyState(leaderName);
            if (party.Members.Count >= MaxPartyMembers)
            {
                selfMessage = $"You cannot invite {joiner.Name}.";
                return false;
            }

            // All gates passed — the switch is now guaranteed, so it is safe to vacate the joiner's old
            // party (a failed join above never touches it, so you never lose your party to a bad follow).
            VacatePartyForSwitch(joiner.Name, out disbandedOldPartyMembers);

            // Joining a party clears every pending invite this player held (from this and any other
            // leader) — they can no longer be a pending invitee of anyone.
            PurgeAllPartyInvitesFor(joiner.Name);
            party.Members.Add(joiner.Name);
            _partyLeaderByMember[joiner.Name] = leaderName;
            // Stock fidelity: the group add and FOLLOW never assign a rank to a joining member — they
            // default to Midrank. Only the leader is forced to Frontrank (INVITE sets
            // the inviter's rank byte to 1). A joiner stays in the middle until they explicitly
            // `frontrank`/`backrank`. (We used to auto-place joiners front/back, which is non-stock.)
            party.Ranks[joiner.Name] = PartyRank.Middle;
            ApplyPartyRankModifiers(joiner, PartyRank.Middle);
            selfMessage = $"You are now following {leaderName}";
            leaderMessage = $"{joiner.Name} started to follow you.";
            return true;
        }
    }

    // Cleanly remove `playerName` from whatever party they are currently in, so joining a new party never
    // leaves an orphaned one behind. Mirrors the stock dissolve-on-follow, but without its bug:
    //   - a plain member just leaves (RemoveMemberInternal auto-dissolves a party that drops to leader-only);
    //   - a LEADER's whole party is disbanded, its other members returned in `disbandedMembers` for the
    //     caller to notify;
    //   - a solo player is a no-op.
    // Caller must hold _partyLock.
    private void VacatePartyForSwitch(string playerName, out List<string> disbandedMembers)
    {
        disbandedMembers = [];
        if (!_partyLeaderByMember.TryGetValue(playerName, out var currentLeader))
            return;

        if (currentLeader.Equals(playerName, StringComparison.OrdinalIgnoreCase))
        {
            // The joiner leads their own party — disband it so no members are left following a leader who
            // has walked off to follow someone else.
            if (_parties.TryGetValue(playerName, out var ownParty))
            {
                foreach (var memberName in ownParty.Members
                             .Where(name => !name.Equals(playerName, StringComparison.OrdinalIgnoreCase))
                             .ToList())
                {
                    disbandedMembers.Add(memberName);
                    _partyLeaderByMember.Remove(memberName);
                    ownParty.Ranks.Remove(memberName);
                    RemovePartyInvite(memberName, playerName);
                    if (_onlinePlayers.TryGetValue(memberName, out var member))
                        ApplyPartyRankModifiers(member, PartyRank.Middle);
                }
                foreach (var invitedName in ownParty.Invited.ToList())
                    RemovePartyInvite(invitedName, playerName);
                _partyLeaderByMember.Remove(playerName);
                _parties.Remove(playerName);
                if (_onlinePlayers.TryGetValue(playerName, out var self))
                    ApplyPartyRankModifiers(self, PartyRank.Middle);
            }
        }
        else if (_parties.TryGetValue(currentLeader, out var oldParty))
        {
            RemoveMemberInternal(oldParty, playerName);
        }
    }

    public bool TryRemoveFromParty(Player actor, string targetName, out string actorMessage, out string? targetMessage, out string? removedPlayerName)
    {
        lock (_partyLock)
        {
            actorMessage = "";
            targetMessage = null;
            removedPlayerName = null;

            if (!_partyLeaderByMember.TryGetValue(actor.Name, out var actorLeader))
            {
                actorMessage = "You are not in a party at the present time.";
                return false;
            }

            if (!actorLeader.Equals(actor.Name, StringComparison.OrdinalIgnoreCase))
            {
                actorMessage = $"You cannot uninvite {targetName}.";
                return false;
            }

            if (!_parties.TryGetValue(actorLeader, out var party))
            {
                actorMessage = "You are not in a party at the present time.";
                return false;
            }

            if (!TryResolvePartyRemovalTarget(party, actorLeader, targetName, out var matchedName, out var matchedInvited, out var disambiguationMessage))
            {
                actorMessage = disambiguationMessage ?? $"{targetName} is not in your party.";
                return false;
            }

            if (matchedInvited)
            {
                party.Invited.RemoveWhere(name => name.Equals(matchedName, StringComparison.OrdinalIgnoreCase));
                party.Members.RemoveAll(name => name.Equals(matchedName, StringComparison.OrdinalIgnoreCase));
                party.Ranks.Remove(matchedName);
                RemovePartyInvite(matchedName, actorLeader);
                actorMessage = $"{matchedName} has been removed from your followers.";
                return true;
            }

            RemoveMemberInternal(party, matchedName);
            actorMessage = $"{matchedName} has been removed from your followers.";
            targetMessage = $"You are no longer following {actorLeader}.";
            removedPlayerName = matchedName;
            return true;
        }
    }

    private static bool TryResolvePartyRemovalTarget(PartyState party, string leaderName, string targetName, out string matchedName, out bool matchedInvited, out string? disambiguationMessage)
    {
        matchedName = party.Invited.FirstOrDefault(name => name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            ?? party.Members.FirstOrDefault(name =>
                !name.Equals(leaderName, StringComparison.OrdinalIgnoreCase) &&
                name.Equals(targetName, StringComparison.OrdinalIgnoreCase))
            ?? string.Empty;
        matchedInvited = false;
        disambiguationMessage = null;

        if (!string.IsNullOrEmpty(matchedName))
        {
            string resolvedName = matchedName;
            matchedInvited = party.Invited.Any(name => name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        var matches = party.Invited
            .Concat(party.Members.Where(name => !name.Equals(leaderName, StringComparison.OrdinalIgnoreCase)))
            .Where(name => name.StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 1)
        {
            matchedName = matches[0];
            string resolvedName = matchedName;
            matchedInvited = party.Invited.Any(name => name.Equals(resolvedName, StringComparison.OrdinalIgnoreCase));
            return true;
        }

        if (matches.Count > 1)
            disambiguationMessage = $"Please be more specific. Matching followers: {string.Join(", ", matches)}";

        return false;
    }

    public bool TryLeaveParty(Player player, out string selfMessage, out string? leaderName)
    {
        lock (_partyLock)
        {
            selfMessage = "";
            leaderName = null;

            if (!_partyLeaderByMember.TryGetValue(player.Name, out var currentLeader))
            {
                selfMessage = "You are not in a party at the present time.";
                return false;
            }

            if (currentLeader.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
            {
                selfMessage = "You are the leader of the party; You may not leave it! Use DISBAND PARTY";
                return false;
            }

            if (!_parties.TryGetValue(currentLeader, out var party))
            {
                selfMessage = "You are not in a party at the present time.";
                return false;
            }

            RemoveMemberInternal(party, player.Name);
            selfMessage = $"You are no longer following {currentLeader}.";
            leaderName = currentLeader;
            return true;
        }
    }

    public bool TryDisbandParty(Player leader, out string selfMessage, out List<string> affectedMembers)
    {
        lock (_partyLock)
        {
            selfMessage = "";
            affectedMembers = [];

            if (!_parties.TryGetValue(leader.Name, out var party))
            {
                selfMessage = "You are not in a party at the present time.";
                return false;
            }

            foreach (var memberName in party.Members.Where(name => !name.Equals(leader.Name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                affectedMembers.Add(memberName);
                _partyLeaderByMember.Remove(memberName);
                RemovePartyInvite(memberName, leader.Name);
            }

            foreach (var invitedName in party.Invited.ToList())
            {
                RemovePartyInvite(invitedName, leader.Name);
            }

            _partyLeaderByMember.Remove(leader.Name);
            _parties.Remove(leader.Name);
            selfMessage = "Your party has been disbanded.";
            return true;
        }
    }

    public bool TryCleanupPartyForIndependentTravel(Player player, bool disbandLeader, out PartyTravelCleanupResult? result)
    {
        result = null;

        if (IsPartyLeader(player.Name))
        {
            if (!disbandLeader)
                return false;

            if (!TryDisbandParty(player, out var selfMessage, out var affectedMembers))
                return false;

            result = new PartyTravelCleanupResult
            {
                DisbandedParty = true,
                SelfMessage = selfMessage,
                AffectedMembers = affectedMembers,
            };

            return true;
        }

        if (!TryLeaveParty(player, out var leaveMessage, out var leaderName))
            return false;

        result = new PartyTravelCleanupResult
        {
            SelfMessage = leaveMessage,
            LeaderName = leaderName,
        };

        return true;
    }

    public bool TrySetPartyRank(Player player, PartyRank desiredRank, out string selfMessage, out string? roomMessage)
    {
        lock (_partyLock)
        {
            selfMessage = "";
            roomMessage = null;

            if (!_partyLeaderByMember.TryGetValue(player.Name, out var leaderName)
                || !_parties.TryGetValue(leaderName, out var party))
            {
                selfMessage = "You are not in a party at the present time.";
                return false;
            }

            var currentRank = party.Ranks.GetValueOrDefault(player.Name, PartyRank.Middle);
            if (currentRank == desiredRank)
            {
                selfMessage = desiredRank switch
                {
                    PartyRank.Front => "You are in the front rank of your group.",
                    PartyRank.Back => "You are in the back rank of your group.",
                    _ => "You are in the middle of your group.",
                };
                return false;
            }

            int frontCount = party.Members.Count(name => party.Ranks.GetValueOrDefault(name, PartyRank.Middle) == PartyRank.Front);
            int backCount = party.Members.Count(name => party.Ranks.GetValueOrDefault(name, PartyRank.Middle) == PartyRank.Back);

            if (desiredRank == PartyRank.Back)
            {
                if (leaderName.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
                {
                    selfMessage = "You may not enter the backrank of your own party.";
                    return false;
                }

                if (currentRank == PartyRank.Front)
                {
                    if (frontCount <= 1)
                    {
                        selfMessage = "You are the only one in the front rank.  You may not leave.";
                        return false;
                    }

                    // Move-to-back: block only when the *remaining* front-rankers
                    // would be fewer than the back-rankers — i.e. (front-1) < back.
                    // (We previously wrote (front-1) < (back+1), which is one too strict and blocked the
                    // 2nd front-ranker in a 3-person party from ever moving back.)
                    if ((frontCount - 1) < backCount)
                    {
                        selfMessage = "There would not be enough people left in the frontrank if you did that.";
                        return false;
                    }
                }

                // A Midrank member moving to back: the same comparison with self excluded from both
                // (self is neither front nor back), i.e. front < back.
                if (currentRank == PartyRank.Middle && frontCount < backCount)
                {
                    selfMessage = "There would not be enough people left in the frontrank if you did that.";
                    return false;
                }
            }

            if (desiredRank == PartyRank.Middle)
            {
                // Move-to-middle has ONLY these guards, in this order: the sole
                // front-ranker can't leave, the sole back-ranker can't leave, then leaders can't leave
                // their rank. There is deliberately NO front>=back balance check here — that guard lives
                // only on the move-to-BACK path. (We used to include an extra (front-1) < back check,
                // which stock does not have.)
                if (currentRank == PartyRank.Front && frontCount <= 1)
                {
                    selfMessage = "You are the only one in the front rank.  You may not leave.";
                    return false;
                }

                if (currentRank == PartyRank.Back && backCount <= 1)
                {
                    selfMessage = "You are the only one in the back rank.  You may not leave.";
                    return false;
                }

                if (leaderName.Equals(player.Name, StringComparison.OrdinalIgnoreCase))
                {
                    selfMessage = "You may not leave your current rank in your own party.";
                    return false;
                }
            }

            party.Ranks[player.Name] = desiredRank;
            ApplyPartyRankModifiers(player, desiredRank);
            selfMessage = desiredRank switch
            {
                PartyRank.Front => "You have moved to the front ranks of your group.",
                PartyRank.Back => "You have moved to the back ranks of your group.",
                _ => "You have moved to the middle ranks of your group.",
            };

            roomMessage = desiredRank switch
            {
                PartyRank.Front => $"{player.Name} just moved to the front rank in your group.",
                PartyRank.Back => $"{player.Name} just moved to the back rank in your group.",
                _ => $"{player.Name} just moved to the middle of your group.",
            };
            return true;
        }
    }

    public void RemovePlayerFromParty(Player player)
    {
        lock (_partyLock)
        {
            if (!_partyLeaderByMember.TryGetValue(player.Name, out var leaderName))
                return;

            if (_parties.TryGetValue(player.Name, out var party))
            {
                foreach (var memberName in party.Members.ToList())
                {
                    if (_onlinePlayers.TryGetValue(memberName, out var memberPlayer))
                        ApplyPartyRankModifiers(memberPlayer, PartyRank.Middle);
                    _partyLeaderByMember.Remove(memberName);
                    RemovePartyInvite(memberName, player.Name);
                }

                foreach (var invitedName in party.Invited.ToList())
                {
                    RemovePartyInvite(invitedName, player.Name);
                }

                _parties.Remove(player.Name);
                return;
            }

            if (_parties.TryGetValue(leaderName, out var leaderParty))
            {
                RemoveMemberInternal(leaderParty, player.Name);
            }

            ApplyPartyRankModifiers(player, PartyRank.Middle);
        }
    }

    public void CleanupPartyForDeath(Player player)
    {
        if (IsPartyLeader(player.Name))
        {
            if (TryDisbandParty(player, out _, out var affectedMembers))
            {
                foreach (var memberName in affectedMembers)
                    SendToPlayer(memberName, $"You are no longer following {player.Name}.");

                SendToPlayer(player.Name, "Your party has been disbanded.");
            }

            return;
        }

        if (TryLeaveParty(player, out var selfMessage, out var leaderName))
        {
            if (!string.IsNullOrWhiteSpace(leaderName))
                SendToPlayer(leaderName, $"{player.Name} is no longer following you.");

            SendToPlayer(player.Name, selfMessage);
        }
    }

    private PartyState GetOrCreatePartyState(string leaderName)
    {
        if (_parties.TryGetValue(leaderName, out var existing))
            return existing;

        var created = new PartyState
        {
            LeaderName = leaderName,
        };
        created.Members.Add(leaderName);
        created.Ranks[leaderName] = PartyRank.Front;
        _parties[leaderName] = created;
        _partyLeaderByMember[leaderName] = leaderName;
        if (_onlinePlayers.TryGetValue(leaderName, out var leader))
            ApplyPartyRankModifiers(leader, PartyRank.Front);
        return created;
    }

    private void RemoveMemberInternal(PartyState party, string memberName)
    {
        party.Members.RemoveAll(name => name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
        party.Invited.Remove(memberName);
        party.Ranks.Remove(memberName);
        _partyLeaderByMember.Remove(memberName);
        RemovePartyInvite(memberName, party.LeaderName);
        if (_onlinePlayers.TryGetValue(memberName, out var member))
            ApplyPartyRankModifiers(member, PartyRank.Middle);

        if (party.Members.Count == 1
            && party.Invited.Count == 0
            && party.LeaderName.Equals(party.Members[0], StringComparison.OrdinalIgnoreCase))
        {
            if (_onlinePlayers.TryGetValue(party.LeaderName, out var leader))
                ApplyPartyRankModifiers(leader, PartyRank.Middle);
            _partyLeaderByMember.Remove(party.LeaderName);
            _parties.Remove(party.LeaderName);
        }
    }

    private static void ApplyPartyRankModifiers(Player player, PartyRank rank)
    {
        // The fighter marshal applies a to-hit adjustment (fighter accuracy)
        // and a defence adjustment (the fighter AC term, which raises the "harder to
        // hit" side of the hit formula, so +AC == harder to be hit):
        //   Front: accuracy +15, defence -10  → hits better, easier to be hit (exposed front line)
        //   Back:  accuracy -10, defence +15  → hits worse, harder to be hit (protected back line)
        //   Middle: nothing.
        // The defence half is unconditional; the accuracy half is SKIPPED for jumpkick (attack-type 3) —
        // that gate lives at the CombatEngine accuracy sites, so PartyAccuracyModifier is the ungated value.
        switch (rank)
        {
            case PartyRank.Front:
                player.PartyAccuracyModifier = 15;
                player.PartyDefenceModifier = -10;
                break;
            case PartyRank.Back:
                player.PartyAccuracyModifier = -10;
                player.PartyDefenceModifier = 15;
                break;
            default:
                player.PartyAccuracyModifier = 0;
                player.PartyDefenceModifier = 0;
                break;
        }
    }
}