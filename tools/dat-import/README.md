# DAT importer

`import_dat.py` builds the engine's `game_data` PostgreSQL schema from the game's Btrieve
`.dat` files. It writes one self-contained SQL script that you load with `psql`.

**This repository ships no game data.** You need your own copy of the game's DAT files.

## Prerequisites

- Python 3.8 or newer (standard library only, nothing to install).
- A PostgreSQL server (the repo's `docker-compose.yml` starts one) and the `psql` client.
- A directory holding the game's DAT files. File names are matched case-insensitively. The
  importer reads these ten:

  | File | Table(s) |
  |------|----------|
  | `wccrace2.dat` | `Races` |
  | `wccclas2.dat` | `Classes` |
  | `wccspel2.dat` | `Spells` |
  | `wccknms2.dat` | `Monsters` |
  | `wccitem2.dat` | `Items` |
  | `wccshop2.dat` | `Shops` |
  | `wccmp002.dat` | `Rooms`, `RoomExits`, `RoomDescriptions` |
  | `wccmsg2.dat` | `Messages` |
  | `wccacts2.dat` | `Actions` |
  | `wcctext2.dat` | `TextBlocks`, `TextBlockLinks` |

  The remaining files in a DAT set (bank, gang, item ownership, users) hold runtime state and
  are not used.
- Optional: a directory of gang-house description files (`WCC<room><map>.HSE`). Gang-house
  rooms have no name or description in the DAT. With `--hse-dir`, each room takes its name and
  description from its file. Without it, those rooms get a placeholder name
  (`Gang House <n> Room`) and no description.

## Generate the SQL

```sh
python3 tools/dat-import/import_dat.py --dat-dir /path/to/dat --out game_data.sql
# with gang-house description files:
python3 tools/dat-import/import_dat.py --dat-dir /path/to/dat --hse-dir /path/to/hse --out game_data.sql
```

Use `--out -` to write the SQL to stdout. The row count per table is printed to stderr.

## Load into PostgreSQL

The script runs in a single transaction. It drops the `game_data` schema (if it exists) and
recreates it, so re-running it replaces all game data. Player data in the `public` schema is not
touched. The target database must already exist.

Plain `psql`:

```sh
psql -v ON_ERROR_STOP=1 -h localhost -U mmudreborn -d mmudreborn -f game_data.sql
```

With the repo's docker-compose PostgreSQL (container `mmudreborn-postgres`):

```sh
docker compose up -d postgres
docker exec -i mmudreborn-postgres psql -v ON_ERROR_STOP=1 -U mmudreborn -d mmudreborn < game_data.sql
```

Then start the server. On startup it creates the player tables and applies its own schema
checks on top of `game_data`.

## Notes

- Only records on the files' data pages are imported. Leftover bytes on index and free pages
  are ignored, and duplicate keys are resolved in favour of records that are in use.
- Text is decoded as CP437, the game's character set.
- Some game data needs corrections that the DAT files don't contain: fixes made by hand on a
  running server, descriptions kept in external text files, and so on. Apply those as your own
  SQL after loading.
