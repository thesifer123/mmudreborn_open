using mmudreborn.Data.Models;
using mmudreborn.Game;
using mmudreborn.Server;
using mmudreborn.UnitTests.Fakes;
using Xunit;

namespace mmudreborn.UnitTests;

// Bug #94: the dark-elf queen (a Room.NPC primary) carries CreateSpell #456 "summon weaponmaster",
// which should conjure her escort #342 when she spawns. Stock fires a spawned
// monster's CreateSpell on EVERY spawn path; reborn previously fired it only on the
// lair/pressure path, so Room.NPC primaries never summoned their escorts. These tests pin the fix:
// the escort is summoned on player entry, exactly once per primary life, and re-fires after a respawn.
public sealed class NpcPrimaryCreateSpellTests
{
    private const int QueenId = 343;
    private const int WeaponmasterId = 342;
    private const int SummonSpellId = 456;
    private const int SummonAbility = 12;

    [Fact]
    public void Room_npc_primary_with_a_summon_create_spell_spawns_its_escort_on_entry()
    {
        var world = BuildWorld(queenRegenTimeHours: 0);

        EnterRoom(world);

        var monsters = world.GetMonstersInRoom(1, 1);
        Assert.Contains(monsters, m => m.Template.Number == QueenId);
        Assert.Contains(monsters, m => m.Template.Number == WeaponmasterId);
    }

    [Fact]
    public void Escort_is_not_re_summoned_while_the_primary_stays_alive()
    {
        var world = BuildWorld(queenRegenTimeHours: 0);

        EnterRoom(world);
        EnterRoom(world); // a second entry must not stack a second escort

        var weaponmasters = world.GetMonstersInRoom(1, 1).Count(m => m.Template.Number == WeaponmasterId);
        Assert.Equal(1, weaponmasters);
    }

    [Fact]
    public void Escort_re_summons_after_the_primary_dies_and_respawns()
    {
        var world = BuildWorld(queenRegenTimeHours: 0);

        EnterRoom(world);
        var queen = world.GetMonstersInRoom(1, 1).Single(m => m.Template.Number == QueenId);

        // Kill the queen and her escort, then re-enter: the queen revives in place and her CreateSpell
        // must fire again (stock re-runs the spawn on respawn).
        foreach (var monster in world.GetMonstersInRoom(1, 1))
            monster.CurrentHP = 0;
        Assert.True(queen.IsDead);

        EnterRoom(world);

        var monsters = world.GetMonstersInRoom(1, 1);
        Assert.Contains(monsters, m => m.Template.Number == QueenId && !m.IsDead);
        Assert.Equal(1, monsters.Count(m => m.Template.Number == WeaponmasterId && !m.IsDead));
    }

    [Fact]
    public void Lazy_room_init_before_entry_still_summons_the_escort_on_entry()
    {
        var world = BuildWorld(queenRegenTimeHours: 0);

        // Force lazy room init (lair/query path) so the queen is created WITHOUT firing her CreateSpell;
        // the escort must still appear once a player enters.
        var preEntry = world.GetMonstersInRoom(1, 1);
        Assert.Contains(preEntry, m => m.Template.Number == QueenId);
        Assert.DoesNotContain(preEntry, m => m.Template.Number == WeaponmasterId);

        EnterRoom(world);

        Assert.Contains(world.GetMonstersInRoom(1, 1), m => m.Template.Number == WeaponmasterId);
    }

    private const int DragonId = 851;
    private const int TapestryId = 1002;
    private const int TapestrySummonSpellId = 1101;

    [Fact]
    public void Create_spell_summon_with_zero_value_falls_back_to_minbase_template()
    {
        // Bug #185: the colossal midnight dragon's CreateSpell "dragon summon tapestry" (#1101) carries
        // the summon ability (12) with AbilVal 0 and the escort template in MinBase (1002, the ancient
        // tapestry) — and is an AREA-targeted (3) create spell. The world-level spawn path used to read
        // ONLY the ability value, so a zero-value summon never spawned and the tapestry never appeared.
        // It must now fall back to MinBase like every other summon path.
        var world = BuildTapestryWorld();

        EnterRoom(world);

        var monsters = world.GetMonstersInRoom(1, 1);
        Assert.Contains(monsters, m => m.Template.Number == DragonId);
        Assert.Contains(monsters, m => m.Template.Number == TapestryId);
    }

    private static GameWorld BuildTapestryWorld()
    {
        var database = new InMemoryGameDatabase();

        database.Rooms[(1, 1)] = new Room { MapNumber = 1, RoomNumber = 1, NPC = DragonId };

        database.Monsters[DragonId] = new Monster
        {
            Number = DragonId,
            Name = "colossal midnight dragon",
            HP = 100,
            Type = 1,
            CreateSpell = TapestrySummonSpellId,
        };

        database.Monsters[TapestryId] = new Monster
        {
            Number = TapestryId,
            Name = "ancient tapestry",
            HP = 100,
            Type = 1,
        };

        database.Spells[TapestrySummonSpellId] = new GameSpell
        {
            Number = TapestrySummonSpellId,
            Name = "dragon summon tapestry",
            Targets = 3,          // area create spell (the real #1101 type)
            MinBase = TapestryId, // template id lives in MinBase when the summon AbilVal is 0
            MaxBase = TapestryId,
            Abilities = { [SummonAbility] = 0 },
        };

        return new GameWorld(database, new InMemoryPlayerRepository());
    }

    private static void EnterRoom(GameWorld world)
    {
        world.NotifyPlayerEnteredRoom(new Player { CurrentMapNumber = 1, CurrentRoomNumber = 1 });
    }

    private static GameWorld BuildWorld(int queenRegenTimeHours)
    {
        var database = new InMemoryGameDatabase();

        database.Rooms[(1, 1)] = new Room
        {
            MapNumber = 1,
            RoomNumber = 1,
            NPC = QueenId,
        };

        database.Monsters[QueenId] = new Monster
        {
            Number = QueenId,
            Name = "dark-elf queen",
            HP = 100,
            Type = 1,
            CreateSpell = SummonSpellId,
            RegenTime = queenRegenTimeHours,
        };

        database.Monsters[WeaponmasterId] = new Monster
        {
            Number = WeaponmasterId,
            Name = "dark-elf weaponsmaster",
            HP = 100,
            Type = 1,
        };

        database.Spells[SummonSpellId] = new GameSpell
        {
            Number = SummonSpellId,
            Name = "summon weaponmaster",
            Targets = 1, // self/ally — the world-clean summon path
            Abilities = { [SummonAbility] = WeaponmasterId },
        };

        return new GameWorld(database, new InMemoryPlayerRepository());
    }
}
