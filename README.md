# mmudreborn

A from-scratch C# reimplementation of a classic BBS-era multi-user dungeon (MUD) game
engine, written against .NET 8 with PostgreSQL for persistence.

The goal is behavioural fidelity to the original: combat rounds, spell effects, monster
spawning and movement, quests and dialogue scripts, shops and banking, parties, stealth,
and the exact wire output that period MUD clients (such as MegaMUD) parse.

## What this repository is not

**It contains no game content.** There is no world data, no monster/item/spell tables,
no room descriptions, no help text, no area maps and no original binaries. The code
that reads and serves a world is here; the world is not. You supply it yourself (see
[Loading game data](#loading-game-data)).

## Architecture

This project is a **door module**, not a standalone telnet server. It implements the
game itself and exposes a session interface; the telnet/BBS transport, login, and door
hosting live in a separate project ([CWGamingServ][cwg]).

- `src/mmudreborn.Server` — the game engine (commands, combat, world tick, persistence)
- `src/Shared` — shared contracts
- `tests/mmudreborn.UnitTests` — unit tests, no database required
- `tools/dat-import` — converts DAT files into the engine's `game_data` schema

[cwg]: https://github.com/thesifer123/CWGamingServ_Public

## Prerequisites

- .NET 8 SDK
- PostgreSQL (a development instance is provided: `docker compose up -d`)
- Python 3 (standard library only), for the DAT importer
- A checkout of [CWGamingServ][cwg] **as a sibling directory**, because the projects
  reference it. The directory names matter, so clone both like this:

  ```bash
  git clone https://github.com/thesifer123/CWGamingServ_Public.git CWGamingServ
  git clone https://github.com/thesifer123/mmudreborn_open.git mmudreborn
  ```

  ```
  parent/
    CWGamingServ/
    mmudreborn/
  ```

## Build and test

```bash
dotnet build mmudreborn.sln
dotnet test tests/mmudreborn.UnitTests/mmudreborn.UnitTests.csproj
```

## Loading game data

The engine reads its world (rooms, exits, monsters, items, spells, shops, classes, races,
messages, actions and text blocks) from the `game_data` schema in PostgreSQL, and will not
start until that schema exists.

Load DAT files by converting them with the bundled importer, then applying the result:

```bash
# 1. Convert a directory of DAT files into a SQL script
python3 tools/dat-import/import_dat.py --dat-dir /path/to/dat-files --out game_data.sql

# 2. Load it into PostgreSQL (replaces any existing game_data)
docker exec -i mmudreborn-postgres \
  psql -U mmudreborn -d mmudreborn -v ON_ERROR_STOP=1 < game_data.sql
```

See [`tools/dat-import/README.md`](tools/dat-import/README.md) for the options and for
loading into a PostgreSQL server that isn't the bundled container.

**DAT files are not provided and never will be.** If you choose to use them, obtaining
them and having the right to use them is entirely your responsibility.

Two optional extras are also operator-supplied:

- `help_topics.json` in the repository root — a JSON object mapping help topic names to
  their text. Without it, `help` has nothing to show.
- ANSI area maps and sign text in `src/mmudreborn.Server/Data/assets/` — see the
  README there. Without them, `map` has nothing to draw.

## Configuration

The engine reads its PostgreSQL connection string from `MMUDREBORN_POSTGRES_CONNECTION`,
defaulting to the throwaway development credentials in `docker-compose.yml`:

```
Host=localhost;Port=5432;Database=mmudreborn;Username=mmudreborn;Password=mmudreborn
```

Use your own credentials for anything beyond local development.

## The first sysop

A fresh install has no game sysops. Create a character, log it out, then set the flag in the
game database:

```sql
UPDATE Players SET IsSysop = 1 WHERE Name = 'YourCharacter';
```

Do this while the character is logged out, or the next save will overwrite it. Board (BBS)
sysop rights are separate and are covered in the [CWGamingServ][cwg] README.

## Running

`dotnet run --project src/mmudreborn.Server` prepares the database and loads the world,
then exits; players connect through [CWGamingServ][cwg], which hosts this module as a door.

## Status

Actively developed. The unit tests pin the formulas and the message strings that clients
parse.

## Licence

Public domain, under [The Unlicense](LICENSE). No rights reserved: copy it, change it,
rename it, sell it, or ship it as your own. No credit or permission needed.

[CWGamingServ][cwg], the host this module plugs into, is a separate project under its own
licence (MIT), which requires keeping its copyright notice when you redistribute it.

### Third-party names and content

The Unlicense covers only the original code and documentation written for this project,
and grants no rights in anything the maintainer does not own. Any game, product, company
or software names mentioned in this repository, in this README, in source comments or
elsewhere, are the property of their respective owners. They are used only to describe
compatibility or behaviour, and no affiliation with or endorsement by those owners is
implied.

No third-party game data, text, art or binaries are included. Such material remains the
property of its respective copyright holders and is subject to their licences and terms.
Anyone who chooses to use it with this software is solely responsible for having the right
to do so.
