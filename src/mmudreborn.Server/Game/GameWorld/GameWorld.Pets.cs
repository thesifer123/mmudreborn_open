using mmudreborn.Game;
using mmudreborn.Game.Combat;

namespace mmudreborn.Server;

// Player-pet model — summoned ("angel") and charmed creatures owned by a player. Ground truth is the
// stock: a pet is a monster whose owner
// field holds the owner's NAME; summoned creatures are monster Type 37 ("angels")
// and are dismissed when the owner leaves/dies, while charmed wild monsters revert to ownerless.
// Pets follow their owner room-to-room on the fast pass and break the bond after the
// abandon counter passes the limit. Pets assist their owner in combat via the stock
// monster-vs-monster path. NOTE: the reciprocal direction —
// hostile monsters choosing to target a pet via the autocombat engagement table — is the separate
// "monster-vs-monster autocombat" backlog item and is intentionally NOT built here.
public partial class GameWorld
{
    // An abandon counter above 15 breaks the owner bond.
    public const int PetFollowAbandonLimit = 15;
    // The summoner child-list size: a player may hold at most 10 living summoned pets.
    public const int MaxPlayerSummonedPets = 10;
    // The "angel" monster Type (37). The dismiss pass removes owned
    // monsters of this type on owner leave/death; other (charmed) pets are not removed, only freed.
    public const int SummonedAngelMonsterType = 37;

    // Summon (ability 12): spawn the ability-value template as a monster
    // in the caster's room, then write the caster's name into the new monster's owner field
    // and clear the charm-fresh latch — i.e. it becomes a summoned pet ("angel"). Returns false (and
    // no spawn) when the per-owner summoned cap is already reached.
    public bool TrySummonPlayerPet(Player owner, int templateId, out MonsterInstance? pet)
    {
        pet = null;
        if (CountPlayerSummonedPets(owner.Name) >= MaxPlayerSummonedPets)
            return false;

        if (!TrySpawnMonsterInRoom(owner.CurrentMapNumber, owner.CurrentRoomNumber, templateId,
                ignoreRoomRestrictions: true, out var spawned, out _) || spawned == null)
            return false;

        spawned.PlayerOwnerName = owner.Name;
        spawned.IsSummonedCreature = true;
        spawned.FollowAbandonTicks = 0;
        spawned.IsCharmFresh = true;   // a summon sets the charm-fresh latch — no free swing until it has settled
        pet = spawned;
        return true;
    }

    // Charm (ability 6): write the caster's name into an EXISTING monster's owner
    // field. The monster keeps its template type, so it is NOT a summoned "angel" — it reverts to a
    // wild monster (rather than vanishing) if the bond later breaks.
    public void AttachCharmedPet(MonsterInstance monster, string ownerName)
    {
        monster.PlayerOwnerName = ownerName;
        monster.IsSummonedCreature = false;
        monster.FollowAbandonTicks = 0;
        monster.IsCharmFresh = true;   // a charm sets the charm-fresh latch — no free swing until it has settled
    }

    public IEnumerable<MonsterInstance> GetPlayerPets(string playerName)
    {
        lock (_monsterLock)
        {
            return _roomMonsters.Values
                .SelectMany(list => list)
                .Where(m => !m.IsDead && m.IsOwnedBy(playerName))
                .ToList();
        }
    }

    public int CountPlayerSummonedPets(string playerName)
        => GetPlayerPets(playerName).Count(m => m.IsSummonedCreature);

    // Owner changed rooms: bring every owned monster from the old room to the owner's current room and
    // reset its abandon counter (a pet follows via the owner's travel trail each fast tick). A
    // knocked-down pet can't follow this pulse — it stays and lets the abandon counter advance.
    public void MovePlayerPetsToFollow(Player owner, int fromMap, int fromRoom)
    {
        List<MonsterInstance> followers;
        lock (_monsterLock)
        {
            if (!_roomMonsters.TryGetValue((fromMap, fromRoom), out var list))
                return;
            followers = list.Where(m => !m.IsDead && m.IsOwnedBy(owner.Name) && !m.IsKnockedDown).ToList();
            if (followers.Count == 0)
                return;

            var destKey = (owner.CurrentMapNumber, owner.CurrentRoomNumber);
            if (!_roomMonsters.TryGetValue(destKey, out var destList))
            {
                destList = [];
                _roomMonsters[destKey] = destList;
            }

            foreach (var pet in followers)
            {
                list.Remove(pet);
                pet.MapNumber = owner.CurrentMapNumber;
                pet.RoomNumber = owner.CurrentRoomNumber;
                pet.FollowAbandonTicks = 0;
                destList.Add(pet);
            }

            if (list.Count == 0)
                _roomMonsters.TryRemove((fromMap, fromRoom), out _);
        }

        foreach (var pet in followers)
        {
            BroadcastToRoom(owner.CurrentMapNumber, owner.CurrentRoomNumber,
                $"{MudAnsi.Green}The {pet.DisplayName} follows {owner.Name} into the room.{MudAnsi.Reset}",
                reprompt: true, prependLineBreak: true);
        }
    }

    // World-tick pass (run on the combat cadence): advance the follow-abandon counter for pets that
    // are separated from their owner, break the bond past the limit, and let in-room pets assist their
    // owner against hostile monsters via the stock monster-vs-monster swing.
    public void ProcessPlayerPetsTick(DateTime now)
    {
        List<MonsterInstance> pets;
        lock (_monsterLock)
        {
            pets = _roomMonsters.Values.SelectMany(list => list)
                .Where(m => !m.IsDead && m.HasPlayerOwner).ToList();
        }

        foreach (var pet in pets)
        {
            // Stock clears the charm-fresh latch the next time the monster acts in combat; the
            // pet-update pass is that next chance, so a freshly summoned/charmed pet stops being "fresh"
            // after one pulse.
            pet.IsCharmFresh = false;

            var owner = FindOnlinePlayer(pet.PlayerOwnerName!);
            bool ownerHere = owner != null && !owner.IsUnconscious
                && owner.CurrentMapNumber == pet.MapNumber && owner.CurrentRoomNumber == pet.RoomNumber;

            if (!ownerHere)
            {
                // Each pulse the pet can't reach its owner bumps the abandon counter; past
                // the limit the bond breaks (summoned "angels" are removed, charmed monsters go wild).
                if (++pet.FollowAbandonTicks > PetFollowAbandonLimit)
                    BreakPetBond(pet);
                continue;
            }

            pet.FollowAbandonTicks = 0;
            TryPetAssistAttack(pet, owner!, now);
        }
    }

    // The pet swings at one hostile monster that is fighting its owner (a living, non-owned monster in
    // the room engaged with the owner), at most once per combat round. Renders the stock third-person
    // room lines from the monster-vs-monster path.
    private void TryPetAssistAttack(MonsterInstance pet, Player owner, DateTime now)
    {
        if (now < pet.PetNextAttackAtUtc)
            return;

        MonsterInstance? target;
        lock (_monsterLock)
        {
            if (!_roomMonsters.TryGetValue((pet.MapNumber, pet.RoomNumber), out var list))
                return;
            target = list.FirstOrDefault(m => !m.IsDead && !m.HasPlayerOwner && m.HasEngagedPlayer(owner.Name));
        }
        if (target == null)
            return;

        pet.PetNextAttackAtUtc = GetNextCombatPulseUtc(now);

        var result = CombatEngine.MonsterVsMonsterAttack(pet, target);
        string atk = Capitalize(pet.DisplayName);
        string def = target.DisplayName;
        string line = result.Outcome switch
        {
            CombatEngine.MonsterVsMonsterOutcome.Kill => $"{atk} just killed {def}.",
            CombatEngine.MonsterVsMonsterOutcome.Hit => $"{atk} just attacked {def}.",
            CombatEngine.MonsterVsMonsterOutcome.Glance => $"{atk}'s attack just glanced off of {def}'s armour.",
            CombatEngine.MonsterVsMonsterOutcome.Dodge => $"{Capitalize(def)} just dodged an attack from {pet.DisplayName}.",
            _ => $"{atk} just missed an attack against {def}.",
        };
        BroadcastToRoom(pet.MapNumber, pet.RoomNumber, $"{MudAnsi.BrightWhite}{line}{MudAnsi.Reset}", reprompt: true, prependLineBreak: true);

        if (result.Outcome == CombatEngine.MonsterVsMonsterOutcome.Kill && target.TryBeginDeathProcessing())
            RemoveDeadMonster(target);
    }

    // When the owner leaves the realm
    // or dies, SUMMONED creatures vanish (stock removes its "angels"); CHARMED wild monsters merely
    // lose their owner and revert to ownerless wild monsters. (V1.11p data carries no monster of the
    // "angel" Type 37, so the summoned-vs-charmed flag — not the template Type — is the faithful
    // discriminator: a summon is the realm's transient "angel".)
    public void DismissPlayerPets(string playerName)
    {
        foreach (var pet in GetPlayerPets(playerName))
            BreakPetBond(pet);
    }

    // The bond breaks (owner-leave dismissal OR the follow-abandon limit): a SUMMONED creature is
    // removed; a CHARMED monster reverts to an ownerless wild monster.
    private void BreakPetBond(MonsterInstance pet)
    {
        if (pet.IsSummonedCreature)
            RemovePetFromRoom(pet);
        else
            pet.ClearPlayerOwnership();
    }

    // Remove a living pet from its room with no respawn scheduling (it was never a lair/NPC spawn).
    private void RemovePetFromRoom(MonsterInstance pet)
    {
        lock (_monsterLock)
        {
            var key = (pet.MapNumber, pet.RoomNumber);
            if (_roomMonsters.TryGetValue(key, out var list) && list.Remove(pet))
            {
                AdjustGlobalCount(pet.Template.Number, -1);
                if (list.Count == 0)
                    _roomMonsters.TryRemove(key, out _);
            }
        }
    }

    private static string Capitalize(string text)
        => string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
