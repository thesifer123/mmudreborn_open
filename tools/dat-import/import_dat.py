#!/usr/bin/env python3
"""Build the engine's `game_data` PostgreSQL schema from the game's Btrieve DAT files.

Usage:
    python3 tools/dat-import/import_dat.py --dat-dir <dir> [--hse-dir <dir>] --out game_data.sql
    psql -v ON_ERROR_STOP=1 -f game_data.sql <connection options>

The output is ONE self-contained SQL script. It runs in a single transaction, drops the
`game_data` schema (if present) and recreates every table the engine reads, then bulk-loads the
rows with COPY. Re-running it simply replaces `game_data`; player tables (schema `public`) are
never touched.

Only the Python 3 standard library is used. DAT file names are matched case-insensitively.

Btrieve layout handled here (v5/v6 style files):
  * page 0 is the file header: page size at byte 8, logical record length at byte 22 and the
    physical slot size at byte 24 (all little-endian uint16);
  * data pages start after page 0; each has a 6-byte page header followed by fixed-size slots;
    a slot is an optional usage prefix (slot size - record length bytes, the first two bytes of
    which are a usage counter) followed by the record bytes;
  * the text-block file is different: it is a B-tree whose index pages (type 0x4400) hold
    30-byte entries that point at fragment pages (type 0x5600) holding the encoded text.
"""

from __future__ import annotations

import argparse
import os
import re
import struct
import sys
from collections import defaultdict

PAGE_HEADER_SIZE = 6

# Logical name -> DAT file name. Only these ten are needed to build game_data; the other files
# in a DAT set (bank, gang, item-ownership, user) hold runtime state and are ignored.
DAT_FILES = {
    "races": "wccrace2.dat",
    "classes": "wccclas2.dat",
    "spells": "wccspel2.dat",
    "monsters": "wccknms2.dat",
    "items": "wccitem2.dat",
    "shops": "wccshop2.dat",
    "rooms": "wccmp002.dat",
    "messages": "wccmsg2.dat",
    "actions": "wccacts2.dat",
    "textblocks": "wcctext2.dat",
}


# =============================================================================
# Low-level readers
# =============================================================================

def read_btrieve_header(data):
    page_size = struct.unpack_from("<H", data, 8)[0]
    record_len = struct.unpack_from("<H", data, 22)[0]
    phys_rec_size = struct.unpack_from("<H", data, 24)[0]
    if phys_rec_size == 0:
        phys_rec_size = record_len + 2
    return page_size, record_len, phys_rec_size


FCR_SIGNATURE = b"FC"         # file-control page of the page-typed (v6) file format
DATA_PAGE_TYPE = 0x44         # 'D': fixed-record data page in the page-typed format


def has_typed_pages(data):
    return data[:2] == FCR_SIGNATURE


def iter_btrieve_records(data, data_pages_only=True):
    """Yield (record_bytes, usage) for every record slot (page 0 is the header).

    In the page-typed format every page starts with a type marker; only data pages ('D') hold
    records. File-control, page-allocation and index pages hold other structures whose bytes can
    parse as plausible-looking records, so they are skipped. Files in the older, untyped format
    are read page by page without that check."""
    page_size, record_len, phys_rec_size = read_btrieve_header(data)
    file_size = len(data)
    recs_per_page = (page_size - PAGE_HEADER_SIZE) // phys_rec_size
    total_pages = file_size // page_size
    prefix_size = phys_rec_size - record_len
    typed = data_pages_only and has_typed_pages(data)

    for page_num in range(1, total_pages):
        page_offset = page_num * page_size
        if typed and data[page_offset + 1] != DATA_PAGE_TYPE:
            continue
        for slot in range(recs_per_page):
            slot_offset = page_offset + PAGE_HEADER_SIZE + slot * phys_rec_size
            if slot_offset + phys_rec_size > file_size:
                break
            usage = struct.unpack_from("<H", data, slot_offset)[0] if prefix_size >= 2 else 0
            rec_offset = slot_offset + prefix_size
            if rec_offset + record_len > file_size:
                break
            yield data[rec_offset:rec_offset + record_len], usage


def _cut_at_nul(raw):
    nul = raw.find(b"\x00")
    return raw[:nul] if nul >= 0 else raw


def read_name(data, offset, length):
    """A record name: NUL-terminated, trimmed. Returns None when the bytes are not plain 7-bit
    text (such slots are free/unused space, not real records)."""
    raw = _cut_at_nul(data[offset:offset + length])
    if any(b >= 0x80 for b in raw):
        return None
    return raw.decode("ascii").strip()


def read_text(data, offset, length):
    """A text field: NUL-terminated, trimmed, decoded as CP437 (the game's character set; the
    engine writes CP437 back to the wire, so the original bytes round-trip)."""
    return _cut_at_nul(data[offset:offset + length]).decode("cp437").strip()


def is_valid_name(name):
    return bool(name) and any(c.isalpha() for c in name)


class Cursor:
    """Sequential little-endian field reader over one record."""

    def __init__(self, data):
        self.data = data
        self.off = 0

    def skip(self, n):
        self.off += n

    def i8u(self):
        v = self.data[self.off]
        self.off += 1
        return v

    def i16(self):
        v = struct.unpack_from("<h", self.data, self.off)[0]
        self.off += 2
        return v

    def i32(self):
        v = struct.unpack_from("<i", self.data, self.off)[0]
        self.off += 4
        return v

    def u8s(self, n):
        v = list(self.data[self.off:self.off + n])
        self.off += n
        return v

    def i16s(self, n):
        v = list(struct.unpack_from("<%dh" % n, self.data, self.off))
        self.off += 2 * n
        return v

    def i32s(self, n):
        v = list(struct.unpack_from("<%di" % n, self.data, self.off))
        self.off += 4 * n
        return v

    def name(self, n):
        v = read_name(self.data, self.off, n)
        self.off += n
        return v

    def text(self, n):
        v = read_text(self.data, self.off, n)
        self.off += n
        return v


def i32_at(data, offset):
    return struct.unpack_from("<i", data, offset)[0]


def i16_at(data, offset):
    return struct.unpack_from("<h", data, offset)[0]


def join_lines(lines):
    return "\n".join(line for line in lines if line).strip()


def abilities(ids, vals):
    out = {}
    for i in range(10):
        out["Abil-%d" % i] = ids[i]
    for i in range(10):
        out["AbilVal-%d" % i] = vals[i]
    return out


# =============================================================================
# Record parsers. Each returns an ordered dict of column -> value, or None for a slot that is
# not a real record. Column order is the table's column order.
# =============================================================================

def parse_race(rec):
    c = Cursor(rec)
    number = c.i16()
    name = c.name(29)
    c.skip(1)
    mins = c.i16s(6)              # INT WIL STR HEA AGL CHM minimums
    hp_bonus = c.i16()
    c.skip(4)
    abil_ids = c.i16s(10)
    base_cp = c.i16()
    abil_vals = c.i16s(10)
    c.skip(6)
    exp_table = c.i16()
    c.skip(2)
    maxs = c.i16s(6)
    if number <= 0 or number > 500 or not is_valid_name(name):
        return None
    row = {"Number": number, "Name": name}
    for key, v in zip(("mINT", "mWIL", "mSTR", "mHEA", "mAGL", "mCHM"), mins):
        row[key] = v
    for key, v in zip(("xINT", "xWIL", "xSTR", "xHEA", "xAGL", "xCHM"), maxs):
        row[key] = v
    row.update({"HPPerLVL": hp_bonus, "ExpTable": exp_table, "BaseCP": base_cp})
    row.update(abilities(abil_ids, abil_vals))
    return row


def parse_class(rec):
    c = Cursor(rec)
    number = c.i16()
    name = c.name(29)
    c.skip(1)
    min_hp = c.i16()
    max_hp = c.i16()
    exp_table = c.i16()
    c.skip(6)
    abil_ids = c.i16s(10)
    magery_type = c.i16()
    magery_lvl = c.i16()
    weapon = c.i16()
    armour = c.i16()
    combat = c.i16()
    abil_vals = c.i16s(10)
    if number <= 0 or number > 500 or not is_valid_name(name):
        return None
    row = {
        "Number": number, "Name": name, "MinHits": min_hp, "MaxHits": max_hp,
        "ExpTable": exp_table, "MageryType": magery_type, "MageryLVL": magery_lvl,
        "WeaponType": weapon, "ArmourType": armour, "CombatLVL": combat,
    }
    row.update(abilities(abil_ids, abil_vals))
    return row


def parse_spell(rec):
    c = Cursor(rec)
    number = c.i16()
    name = c.name(29)
    c.skip(1)
    desc_a = c.text(50)
    c.skip(1)
    desc_b = c.text(50)
    c.skip(1)
    c.skip(2)
    cast_msg_a = c.i32()
    c.skip(22)
    level_cap = c.i8u()
    c.skip(1)
    msg_style = c.i8u()
    c.skip(3)
    abil_vals = c.i16s(10)
    energy = c.i16()
    level = c.i16()
    min_dmg = c.i16()
    max_dmg = c.i16()
    spell_type = c.i16()
    type_resists = c.i16()
    difficulty = c.i16()
    dur_rand = c.i16()            # byte 202: duration random multiplier per effective level
    target = c.i16()
    duration = c.i16()
    attack_type = c.i16()
    c.skip(2)
    resist_ability = c.i16()
    magery = c.i16()
    abil_ids = c.i16s(10)
    cast_msg_b = c.i32()
    mana = c.i16()
    max_inc = c.i8u()
    max_inc_lvls = c.i8u()
    magery_lvl = c.i16()
    min_inc = c.i8u()
    min_inc_lvls = c.i8u()
    dur_inc = c.i8u()
    dur_inc_lvls = c.i8u()
    short = c.name(5)
    if number <= 0 or not is_valid_name(name):
        return None
    row = {
        "Number": number, "Name": name, "Short": short or "",
        "ReqLevel": level, "EnergyCost": energy, "ManaCost": mana,
        "MinBase": min_dmg, "MaxBase": max_dmg, "Diff": difficulty,
        "TypeOfResists": type_resists, "Targets": target, "Duration": duration,
        "AttType": attack_type, "Magery": magery, "MageryLvl": magery_lvl, "Cap": level_cap,
        "MaxIncLvls": max_inc_lvls, "MaxInc": max_inc, "MinIncLvls": min_inc_lvls,
        "MinInc": min_inc, "DurIncLvls": dur_inc_lvls, "DurInc": dur_inc,
        "Learnable": 1 if spell_type > 0 else 0, "SpellType": spell_type,
    }
    row.update(abilities(abil_ids, abil_vals))
    row.update({
        "CastMsgA": cast_msg_a, "CastMsgB": cast_msg_b, "MsgStyle": msg_style,
        "ResistAbility": resist_ability,
        "Description": join_lines([desc_a, desc_b]),
        # Only a positive multiplier means "random spread"; anything else is treated as none.
        "DurRand": dur_rand if dur_rand > 0 else 0,
    })
    return row


def parse_monster(rec):
    c = Cursor(rec)
    number = c.i32()
    c.skip(50)
    name = c.name(29)
    c.skip(1)
    group = c.i16()
    c.skip(2)
    exp_multi = c.i32()
    group_index = c.i16()
    c.skip(2)
    c.skip(4)
    weapon = c.i32()
    dr = c.i16()
    ac = c.i16()
    group_match = c.i16()         # byte 108: group cohesion id for leader/follower formation
    follow = c.i16()
    mr = c.i16()
    bs_defense = c.i16()
    experience = c.i32()
    hp = c.i16()
    energy = c.i16()
    hp_regen = c.i16()
    abil_ids = c.i16s(10)
    abil_vals = c.i16s(10)
    game_limit = c.i16()
    active = c.i16()
    mon_type = c.i16()
    max_followers = c.i8u()       # byte 172: how many followers a leader drags along
    undead = c.i8u()
    align = c.i16()
    c.skip(2)
    regen_time = c.i16()
    c.skip(4)
    move_msg = c.i32()
    death_msg = c.i32()
    drop_items = c.i32s(10)
    drop_uses = c.i16s(10)
    drop_pers = c.u8s(10)
    c.skip(2)
    runic, platinum, gold, silver, copper = c.i32s(5)
    greet_txt = c.i32()
    charm_lvl = c.i16()
    c.skip(2)
    desc_txt = c.i32()
    atk_type = c.u8s(5)
    c.skip(1)
    atk_acc = c.i16s(5)
    atk_per = c.u8s(5)
    c.skip(1)
    atk_min = c.i16s(5)
    atk_max = c.i16s(5)
    c.skip(2)
    atk_hit_msg = c.i32s(5)
    atk_dodge_msg = c.i32s(5)
    atk_miss_msg = c.i32s(5)
    atk_eng = c.i16s(5)
    c.skip(2)
    talk_txt = c.i32()
    charm_res = c.i16()
    c.skip(2)
    atk_hit_spell = c.i16s(5)
    death_spell = c.i16()
    c.skip(14)
    create_spell = c.i16()
    mid_spell = c.i16s(5)
    mid_per = c.u8s(5)
    mid_lvl = c.u8s(5)
    desc_lines = []
    for _ in range(4):
        desc_lines.append(c.text(70))
        c.skip(1)
    gender = c.i8u()

    if number <= 0 or not is_valid_name(name):
        return None

    # Average damage over the attack slots that are in use.
    total, count = 0.0, 0
    for i in range(5):
        if atk_type[i] > 0 and atk_per[i] > 0:
            total += (atk_min[i] + atk_max[i]) / 2.0
            count += 1
    avg_dmg = round(total / count, 1) if count else 0.0

    # Attack, drop and mid-combat spell lists are stored packed: in-use entries first, in record
    # order, then zero-filled.
    attacks = [i for i in range(5) if atk_type[i] > 0 or atk_per[i] > 0]
    drops = [i for i in range(10) if drop_items[i] > 0]
    mids = [i for i in range(5) if mid_spell[i] > 0]

    def packed(indices, values, width):
        out = [values[i] for i in indices]
        return out + [0] * (width - len(out))

    row = {
        "Number": number, "Name": name, "Description": join_lines(desc_lines),
        "Weapon": weapon, "ArmourClass": ac, "DamageResist": dr, "FollowPercent": follow,
        "MagicRes": mr, "BSDefense": bs_defense, "EXP": experience, "ExpMulti": exp_multi,
        "HP": hp, "Energy": energy, "AvgDmg": avg_dmg, "GreetTXT": greet_txt,
        "HPRegen": hp_regen, "CharmLVL": charm_lvl, "Type": mon_type, "Undead": undead,
        "Align": align, "RegenTime": regen_time, "GameLimit": game_limit, "Runic": runic,
        "Platinum": platinum, "Gold": gold, "Silver": silver, "Copper": copper,
        "DeathSpell": death_spell, "CreateSpell": create_spell, "Active": active,
        "Group": group, "GroupIndex": group_index, "Gender": gender, "DescTxt": desc_txt,
    }
    row.update(abilities(abil_ids, abil_vals))
    for col, values in (("AtkType", atk_type), ("AtkAcc", atk_acc), ("AtkPer", atk_per),
                        ("AtkMin", atk_min), ("AtkMax", atk_max), ("AtkEng", atk_eng),
                        ("AtkHitSpell", atk_hit_spell)):
        for i, v in enumerate(packed(attacks, values, 5)):
            row["%s-%d" % (col, i)] = v
    for col, values in (("DropItem", drop_items), ("DropPer", drop_pers)):
        for i, v in enumerate(packed(drops, values, 10)):
            row["%s-%d" % (col, i)] = v
    for col, values in (("MidSpell", mid_spell), ("MidSpellPer", mid_per), ("MidSpellLvl", mid_lvl)):
        for i, v in enumerate(packed(mids, values, 5)):
            row["%s-%d" % (col, i)] = v
    for col, values in (("AtkHitMsg", atk_hit_msg), ("AtkDodgeMsg", atk_dodge_msg),
                        ("AtkMissMsg", atk_miss_msg)):
        for i, v in enumerate(packed(attacks, values, 5)):
            row["%s-%d" % (col, i)] = v
    row.update({"MoveMsg": move_msg, "DeathMsg": death_msg, "TalkTxt": talk_txt,
                "CharmRes": charm_res})
    for i, v in enumerate(packed(drops, drop_uses, 10)):
        row["DropUses-%d" % i] = v
    row.update({"GroupMatch": group_match, "MaxFollowers": max_followers})
    return row


# Item record byte offsets of fields read directly (not through the sequential cursor).
ITEM_NEGATE_OFFSET = 876          # 10 x int32 spell numbers negated while the item is worn
ITEM_OPEN_COIN_OFFSETS = (944, 948, 952, 956, 960)   # runic, platinum, gold, silver, copper
ITEM_READ_TEXTBLOCK_OFFSET = 1044
ITEM_DESTRUCT_MSG_OFFSET = 1048
ITEM_ROBABLE_OFFSET = 1067


def parse_item(rec):
    c = Cursor(rec)
    number = c.i32()
    c.skip(2)
    game_limit = c.i16()
    c.skip(8)
    c.skip(157)
    name = c.name(29)
    c.skip(1)
    desc = []
    for _ in range(9):
        desc.append(c.text(60))
        c.skip(1)
    c.skip(2)
    weight = c.i16()
    item_type = c.i16()
    abil_ids = c.i16s(20)
    uses = c.i16()
    c.skip(2)
    cost = c.i16()
    class_rest = c.i16s(10)
    c.skip(6)
    min_hit = c.i16()
    max_hit = c.i16()
    armour_class = c.i16()
    race_rest = c.i16s(10)
    c.skip(20)
    c.skip(40)                    # negate list, read below as 10 x int32
    weapon_type = c.i16()
    armour_type = c.i16()
    worn = c.i16()
    accuracy = c.i16()
    damage_resist = c.i16()
    gettable = c.i8u()
    c.skip(1)
    str_req = c.i16()
    c.skip(10 + 20 + 30)          # includes the open-coin block, read below by offset
    speed = c.i16()
    c.skip(2)
    abil_vals = c.i16s(20)
    c.skip(2)
    hit_msg = c.i32()
    miss_msg = c.i32()
    c.skip(8)                     # read-textblock + destruct message, read below by offset
    c.skip(12)
    not_droppable = c.i8u()
    currency = c.i8u()
    retain_after_uses = c.i8u()
    c.skip(1)                     # robable, read below by offset
    destroy_on_death = c.i8u()

    if number <= 0 or not is_valid_name(name):
        return None

    read_tb = i32_at(rec, ITEM_READ_TEXTBLOCK_OFFSET)
    if read_tb < 0 or read_tb > 100000:
        read_tb = 0               # uninitialised on items that are never read
    destruct = i32_at(rec, ITEM_DESTRUCT_MSG_OFFSET)
    if destruct <= 0 or destruct > 100000:
        destruct = 0              # 0 = use the default phrase; out-of-range = uninitialised
    coins = [i32_at(rec, off) for off in ITEM_OPEN_COIN_OFFSETS]
    coins = [v if 0 <= v <= 10000000 else 0 for v in coins]
    negate = [i32_at(rec, ITEM_NEGATE_OFFSET + 4 * i) for i in range(10)]

    row = {
        "Number": number, "Name": name, "Description": join_lines(desc),
        "Limit": game_limit, "Encum": weight, "ItemType": item_type, "UseCount": uses,
        "Price": cost, "Currency": currency, "Min": min_hit, "Max": max_hit,
        "ArmourClass": armour_class, "DamageResist": damage_resist,
        "WeaponType": weapon_type, "ArmourType": armour_type, "Worn": worn, "Accy": accuracy,
        "Gettable": gettable, "StrReq": str_req, "Speed": speed,
        "NotDroppable": not_droppable, "DestroyOnDeath": destroy_on_death,
        "RetainAfterUses": retain_after_uses, "HitMsg": hit_msg, "MissMsg": miss_msg,
        # The item record has no "in game" flag; the column exists for the engine's schema and
        # is always 1.
        "InGame": 1,
    }
    for i in range(10):
        row["ClassRest-%d" % i] = class_rest[i]
    for i in range(10):
        row["RaceRest-%d" % i] = race_rest[i]
    row.update(abilities(abil_ids[:10], abil_vals[:10]))
    row.update({"DestructMsg": destruct, "Robable": rec[ITEM_ROBABLE_OFFSET],
                "ReadTextBlock": read_tb})
    for i in range(10):
        row["NegateSpell-%d" % i] = negate[i]
    for col, v in zip(("OpenRunic", "OpenPlatinum", "OpenGold", "OpenSilver", "OpenCopper"), coins):
        row[col] = v
    return row


def parse_shop(rec):
    c = Cursor(rec)
    number = c.i32()
    name = c.name(39)
    c.skip(2)
    desc = []
    for _ in range(3):
        desc.append(c.text(52))
        c.skip(1)
    shop_type = c.i16()
    min_lvl = c.i16()
    max_lvl = c.i16()
    markup = c.i16()
    c.skip(2)
    class_rest = c.i8u()
    c.skip(1)
    items = c.i32s(20)
    max_qty = c.i16s(20)
    c.skip(40)                    # current quantity (runtime state)
    rgn_time = c.i16s(20)
    rgn_amount = c.i16s(20)
    rgn_percent = c.u8s(20)
    if number <= 0 or not is_valid_name(name):
        return None
    row = {"Number": number, "Name": name, "ShopType": shop_type, "MinLvl": min_lvl,
           "MaxLvl": max_lvl, "MarkupPercent": markup, "ClassRest": class_rest}
    for col, values in (("Item", items), ("ItemMax", max_qty), ("ItemTime", rgn_time),
                        ("ItemAmt", rgn_amount), ("ItemPer", rgn_percent)):
        for i in range(20):
            row["%s-%d" % (col, i)] = values[i]
    # Shop descriptions are usually blank padding; keep only real printable text.
    text = join_lines(desc)
    clean = "".join(ch for ch in text if ch.isprintable() or ch == "\n").strip()
    row["Description"] = clean if len(clean) >= 5 else ""
    return row


def parse_message(rec):
    c = Cursor(rec)
    number = c.i32()
    line1 = c.text(74)
    c.skip(8)
    line2 = c.text(74)
    c.skip(8)
    line3 = c.text(74)
    if number <= 0:
        return None
    # All-blank messages are kept: several message ids are deliberately blank and referenced as
    # "print nothing". Phantom blanks from unused slots are filtered by load_records().
    return {"Number": number, "Line1": line1, "Line2": line2, "Line3": line3}


ACTION_COLUMNS = ["SingleToUser", "SingleToRoom", "UserToUser", "UserToOtherUser", "UserToRoom",
                  "MonsterToUser", "MonsterToRoom", "InventoryToUser", "InventoryToRoom",
                  "FloorItemToUser", "FloorItemToRoom"]


def parse_action(rec):
    c = Cursor(rec)
    name = c.name(29)
    c.skip(1)
    lines = []
    for _ in range(11):
        lines.append(c.text(74))
        c.skip(6)
    if not is_valid_name(name):
        return None
    row = {"Name": name}
    row.update(dict(zip(ACTION_COLUMNS, lines)))
    row["DisplayOrder"] = None    # assigned by the engine on first load
    return row


# ---------------------------------------------------------------------------------------------
# Rooms
# ---------------------------------------------------------------------------------------------

EXIT_KEYS = ["N", "S", "E", "W", "NE", "NW", "SE", "SW", "U", "D"]
EXIT_DIRECTIONS = ["north", "south", "east", "west", "northeast", "northwest",
                   "southeast", "southwest", "up", "down"]
EXIT_TYPE_MAP_CHANGE = 8
ROOM_DESC_MARKER = "FILE DESCRIPTION"
_COLOUR_CODE = re.compile(r";\{[A-Za-z0-9]+")


def clean_room_line(line):
    return _COLOUR_CODE.sub("", line.replace("@", " ")).strip()


def exit_summary(target_map, target_room, exit_type, p1, p2):
    s = "%d/%d" % (target_map, target_room)
    if exit_type == 6:
        if p1 & 2:
            s += " (Hidden/Searchable)"
        elif p1 & 1:
            s += " (Hidden/Passable)"
        elif p2 != 0:
            s += " (Hidden/Needs %d Actions)" % abs(p2)
        else:
            s += " (Hidden)"
    elif exit_type == 7:
        s += " (Door)"
    elif exit_type == 10:
        s += " (Text Msg: %d)" % p1
    elif exit_type == 11:
        s += " (Gate)"
    elif exit_type == 12:
        s += " (Remote Action: %d)" % p1
    elif exit_type == 4:
        s += " (Toll: %d gold)" % p1
    return s


def parse_room(rec, hse_rooms):
    c = Cursor(rec)
    map_number = c.i32()
    room_number = c.i32()
    c.skip(253)
    name = c.name(53)
    desc_lines = [clean_room_line(c.text(71)) for _ in range(7)]
    c.skip(13)
    exits = c.i32s(10)
    exit_types = c.i16s(10)
    para1 = c.i32s(10)
    para2 = c.i16s(10)
    para3 = c.i32s(10)
    para4 = c.i32s(10)
    c.skip(60)                    # current monsters (runtime state)
    room_type = c.i16()
    c.skip(2)
    shop = c.i32()
    c.skip(30)
    min_index = c.i16()           # byte 1122
    max_index = c.i16()
    c.skip(2)
    by_number = c.i32()           # byte 1128: spawn exactly this monster number here
    light = c.i16()
    gang_house = c.i16()
    room_items = c.i32s(17)       # byte 1136: items in the room as shipped
    c.skip(34 + 2)
    hidden_items = c.i32s(15)
    c.skip(30 + 2)
    runic, platinum, gold, silver, copper = c.i32s(5)
    c.skip(20)
    max_regen = c.i32()
    monster_type = c.i16()
    c.skip(2)
    attributes = c.i32()
    c.skip(4)
    death_room = c.i32()
    exit_room = c.i32()
    c.skip(34 + 30)
    cmd = c.i32()
    c.skip(4)
    delay = c.i16()
    max_area = c.i16()            # byte 1470: area-wide monster cap (read from the control room)
    c.skip(4)
    control_room = c.i32()
    npc = c.i32()
    placed_items = c.i32s(10)     # byte 1484: items re-placed when the world starts
    c.skip(8 + 4)
    spell = c.i32()

    if map_number < 1 or map_number > 99 or room_number < 1 or room_number > 9999:
        return None

    description = "\n".join(line for line in desc_lines if line)
    if desc_lines and desc_lines[0].upper() == ROOM_DESC_MARKER:
        # The text lives in an external description file; never show the marker as prose.
        description = ""

    if not is_valid_name(name):
        # Gang-house rooms ship without a name; keep them (they carry exits, shops and scripts).
        if gang_house <= 0:
            return None
        hse = hse_rooms.get((map_number, room_number))
        if hse:
            name, description = hse
        else:
            name, description = "Gang House %d Room" % gang_house, ""

    exit_rows = []
    exit_strs = {}
    for i, key in enumerate(EXIT_KEYS):
        target = exits[i]
        if target == 0:
            exit_strs[key] = "0"
            continue
        etype = exit_types[i]
        p1, p2 = para1[i], para2[i]
        target_map = p1 if etype == EXIT_TYPE_MAP_CHANGE and p1 > 0 else map_number
        exit_rows.append({
            "MapNumber": map_number, "RoomNumber": room_number, "DirectionIndex": i,
            "Direction": EXIT_DIRECTIONS[i], "TargetMap": target_map, "TargetRoom": target,
            "ExitType": etype, "Para1": p1, "Para2": p2, "Para3": para3[i], "Para4": para4[i],
        })
        exit_strs[key] = exit_summary(target_map, target, etype, p1, p2)

    # Visible items: the static placed list first, then any extra copies present in the
    # shipped room-items array (counted per item id, so duplicate fixtures are preserved).
    static_ids = [p for p in placed_items if p > 0]
    live_ids = [p for p in room_items if p > 0]
    placed = [str(p) for p in static_ids]
    for item_id in dict.fromkeys(live_ids):
        placed.extend([str(item_id)] * max(0, live_ids.count(item_id) - static_ids.count(item_id)))

    ground_copper = runic * 1000000 + platinum * 10000 + gold * 100 + silver * 10 + copper

    row = {
        "Map Number": map_number, "Room Number": room_number, "Name": name,
        "Description": description, "Light": light, "Shop": shop, "NPC": npc, "CMD": cmd,
        "Spell": spell, "Delay": delay, "Placed": ",".join(placed),
        "HiddenItems": ",".join(str(p) for p in hidden_items if p > 0),
        "GroundCurrency": ground_copper,
    }
    for key in EXIT_KEYS:
        row[key] = exit_strs[key]
    row.update({
        "MaxRegen": max_regen, "MonsterType": monster_type, "MinIndex": min_index,
        "MaxIndex": max_index, "DeathRoom": death_room, "ExitRoom": exit_room,
        "GangHouse": gang_house, "ControlRoom": control_room, "Attributes": attributes,
        "RoomType": room_type, "MaxArea": max_area, "ByNumber": by_number,
    })
    return row, exit_rows


def load_rooms(data, hse_rooms):
    rooms, exits, descriptions = [], [], {}
    seen_rooms, seen_exits = set(), set()
    for rec, _usage in iter_btrieve_records(data):
        try:
            parsed = parse_room(rec, hse_rooms)
        except (struct.error, IndexError, UnicodeDecodeError):
            continue
        if parsed is None:
            continue
        room, exit_rows = parsed
        key = (room["Map Number"], room["Room Number"])
        if key in seen_rooms:
            continue
        seen_rooms.add(key)
        rooms.append(room)
        for ex in exit_rows:
            ekey = (ex["MapNumber"], ex["RoomNumber"], ex["DirectionIndex"])
            if ekey not in seen_exits:
                seen_exits.add(ekey)
                exits.append(ex)
        # The room-description table holds every room that has description text (for gang-house
        # rooms, the text of their external description file when one was supplied).
        if room["Description"].strip():
            descriptions[key] = room["Description"]
    return rooms, exits, descriptions


def load_hse_rooms(hse_dir):
    """Gang-house description files: WCC<room><map:2 digits>.HSE (the prefix may be shortened
    to WC). The first line is the room name; the whole file is the room description."""
    out = {}
    if not hse_dir:
        return out
    for fn in os.listdir(hse_dir):
        if not fn.upper().endswith(".HSE"):
            continue
        digits = re.sub(r"^WC+", "", os.path.splitext(fn)[0].upper())
        if not digits.isdigit() or len(digits) < 3:
            continue
        key = (int(digits[-2:]), int(digits[:-2]))
        with open(os.path.join(hse_dir, fn), "rb") as fh:
            raw = fh.read().decode("cp437")
        lines = [ln.rstrip() for ln in raw.replace("\r\n", "\n").replace("\r", "\n").split("\n")]
        while lines and not lines[-1].strip():
            lines.pop()
        if lines:
            out[key] = (lines[0].strip(), "\n".join(lines))
    return out


# ---------------------------------------------------------------------------------------------
# Text blocks
# ---------------------------------------------------------------------------------------------

TB_INDEX_PAGE = 0x4400
TB_FRAGMENT_PAGE = 0x5600


def decode_fragment(data, page_size, page):
    raw = _cut_at_nul(data[page * page_size + 16:(page + 1) * page_size])
    # Stored text is shifted up by 0x20 (mod 256) and is CP437.
    return bytes((b - 0x20) & 0xFF for b in raw).decode("cp437").replace("\x00", "")


def load_textblocks(data):
    page_size, _record_len, entry_size = read_btrieve_header(data)
    file_size = len(data)
    total_pages = file_size // page_size

    frag_page = {}
    for page in range(total_pages):
        if struct.unpack_from("<H", data, page * page_size)[0] == TB_FRAGMENT_PAGE:
            frag_page[struct.unpack_from("<H", data, page * page_size + 2)[0]] = page

    # Index entry (30 bytes): [8 key bytes][Number i32][LinkTo i32][fragment pointer 4]
    # [usage u16][part number i16][6 key bytes]; the fragment id is the u16 at entry byte 17.
    parts = defaultdict(dict)
    links = {}
    spilled = []
    for page in range(total_pages):
        base = page * page_size
        if struct.unpack_from("<H", data, base)[0] != TB_INDEX_PAGE:
            continue
        page_end = base + page_size
        count = (page_size - 16) // entry_size
        # One slot past the computed count: the last entry of a page can straddle the page end.
        for slot in range(count + 1):
            e = base + 16 + slot * entry_size
            if e + entry_size > file_size:
                break
            number = struct.unpack_from("<i", data, e + 8)[0]
            link_to = struct.unpack_from("<i", data, e + 12)[0]
            frag_id = struct.unpack_from("<H", data, e + 17)[0]
            if e + entry_size > page_end:
                # Its usage/part fields fall on the next page. Such an entry is usually a
                # duplicate separator key, but for some blocks it is the only entry: keep it
                # aside and use it only when nothing else names that block.
                if number > 0 and frag_id in frag_page:
                    spilled.append((number, frag_id, link_to))
                continue
            usage = struct.unpack_from("<H", data, e + 20)[0]
            part = struct.unpack_from("<h", data, e + 22)[0]
            if usage == 0 or number <= 0:
                continue
            parts[number].setdefault(part, frag_id)
            if link_to > 0 and number not in links:
                links[number] = link_to

    for number, frag_id, link_to in spilled:
        if number not in parts:
            parts[number][0] = frag_id
            if link_to > 0 and number not in links:
                links[number] = link_to

    blocks = {}
    for number, plist in parts.items():
        chunks = []
        for part in sorted(plist):
            fid = plist[part]
            if fid in frag_page:
                text = decode_fragment(data, page_size, frag_page[fid])
                if text:
                    chunks.append(text)
        text = "".join(chunks).strip()
        if text:
            blocks[number] = text
    return blocks, links


# ---------------------------------------------------------------------------------------------
# Generic fixed-record loader
# ---------------------------------------------------------------------------------------------

_IDENTITY_KEYS = {"Number", "Name"}


def _content_empty(row):
    for k, v in row.items():
        if k in _IDENTITY_KEYS:
            continue
        if isinstance(v, str):
            if v.strip():
                return False
        elif v:
            return False
    return True


def load_records(data, parser):
    """Parse every slot, drop non-records, and de-duplicate by key. Index and free pages can
    hold leftover bytes that parse as a plausible record: a content-empty record from a slot
    whose usage counter is 0 is discarded, and on a key collision a record from a slot in use
    replaces one from an unused slot."""
    records, index, usage_of = [], {}, {}
    for rec, usage in iter_btrieve_records(data):
        try:
            row = parser(rec)
        except (struct.error, IndexError, UnicodeDecodeError):
            continue
        if row is None:
            continue
        if usage == 0 and _content_empty(row):
            continue
        key = row.get("Number") or row.get("Name")
        if key not in index:
            index[key] = len(records)
            usage_of[key] = usage
            records.append(row)
        elif usage != 0 and usage_of[key] == 0:
            records[index[key]] = row
            usage_of[key] = usage
    return records


# =============================================================================
# Schema
# =============================================================================

def _abil_cols():
    return [("Abil-%d" % i, "bigint") for i in range(10)] + \
           [("AbilVal-%d" % i, "bigint") for i in range(10)]


def _series(prefix, n, sqltype="bigint"):
    return [("%s-%d" % (prefix, i), sqltype) for i in range(n)]


INT0 = "integer DEFAULT 0 NOT NULL"
TEXT0 = "text DEFAULT ''::text NOT NULL"

TABLES = {
    "Actions": [("Name", "text")] + [(c, "text") for c in ACTION_COLUMNS] + [("DisplayOrder", "integer")],
    "Classes": [("Number", "bigint"), ("Name", "text"), ("MinHits", "bigint"), ("MaxHits", "bigint"),
                ("ExpTable", "bigint"), ("MageryType", "bigint"), ("MageryLVL", "bigint"),
                ("WeaponType", "bigint"), ("ArmourType", "bigint"), ("CombatLVL", "bigint")] + _abil_cols(),
    "Items": [("Number", "bigint"), ("Name", "text"), ("Description", "text")] +
             [(c, "bigint") for c in ("Limit", "Encum", "ItemType", "UseCount", "Price", "Currency",
                                      "Min", "Max", "ArmourClass", "DamageResist", "WeaponType",
                                      "ArmourType", "Worn", "Accy", "Gettable", "StrReq", "Speed",
                                      "NotDroppable", "DestroyOnDeath", "RetainAfterUses", "HitMsg",
                                      "MissMsg", "InGame")] +
             _series("ClassRest", 10) + _series("RaceRest", 10) + _abil_cols() +
             [("DestructMsg", INT0), ("Robable", INT0), ("ReadTextBlock", INT0)] +
             _series("NegateSpell", 10, INT0) +
             [(c, INT0) for c in ("OpenRunic", "OpenPlatinum", "OpenGold", "OpenSilver", "OpenCopper")],
    "Messages": [("Number", "bigint"), ("Line1", "text"), ("Line2", "text"), ("Line3", "text")],
    "Monsters": [("Number", "bigint"), ("Name", "text"), ("Description", "text")] +
                [(c, "bigint") for c in ("Weapon", "ArmourClass", "DamageResist", "FollowPercent",
                                         "MagicRes", "BSDefense", "EXP", "ExpMulti", "HP", "Energy")] +
                [("AvgDmg", "double precision")] +
                [(c, "bigint") for c in ("GreetTXT", "HPRegen", "CharmLVL", "Type", "Undead", "Align",
                                         "RegenTime", "GameLimit", "Runic", "Platinum", "Gold", "Silver",
                                         "Copper", "DeathSpell", "CreateSpell", "Active", "Group",
                                         "GroupIndex", "Gender", "DescTxt")] +
                _abil_cols() +
                _series("AtkType", 5) + _series("AtkAcc", 5) + _series("AtkPer", 5) +
                _series("AtkMin", 5) + _series("AtkMax", 5) + _series("AtkEng", 5) +
                _series("AtkHitSpell", 5) + _series("DropItem", 10) + _series("DropPer", 10) +
                _series("MidSpell", 5) + _series("MidSpellPer", 5) + _series("MidSpellLvl", 5) +
                _series("AtkHitMsg", 5, INT0) + _series("AtkDodgeMsg", 5, INT0) +
                _series("AtkMissMsg", 5, INT0) +
                [(c, INT0) for c in ("MoveMsg", "DeathMsg", "TalkTxt", "CharmRes")] +
                _series("DropUses", 10, INT0) + [("GroupMatch", INT0), ("MaxFollowers", INT0)],
    "Races": [("Number", "bigint"), ("Name", "text")] +
             [(c, "bigint") for c in ("mINT", "mWIL", "mSTR", "mHEA", "mAGL", "mCHM",
                                      "xINT", "xWIL", "xSTR", "xHEA", "xAGL", "xCHM",
                                      "HPPerLVL", "ExpTable", "BaseCP")] + _abil_cols(),
    "RoomDescriptions": [("MapNumber", "bigint NOT NULL"), ("RoomNumber", "bigint NOT NULL"),
                         ("Description", "text")],
    "RoomExits": [(c, "bigint") for c in ("MapNumber", "RoomNumber", "DirectionIndex")] +
                 [("Direction", "text")] +
                 [(c, "bigint") for c in ("TargetMap", "TargetRoom", "ExitType",
                                          "Para1", "Para2", "Para3", "Para4")],
    "Rooms": [("Map Number", "bigint"), ("Room Number", "bigint"), ("Name", "text"),
              ("Description", "text")] +
             [(c, "bigint") for c in ("Light", "Shop", "NPC", "CMD", "Spell", "Delay")] +
             [("Placed", "text"), ("HiddenItems", "text"), ("GroundCurrency", "bigint")] +
             [(k, "text") for k in EXIT_KEYS] +
             [(c, "bigint") for c in ("MaxRegen", "MonsterType", "MinIndex", "MaxIndex", "DeathRoom",
                                      "ExitRoom", "GangHouse", "ControlRoom")] +
             [(c, INT0) for c in ("Attributes", "RoomType", "MaxArea", "ByNumber")],
    "Shops": [("Number", "bigint"), ("Name", "text")] +
             [(c, "bigint") for c in ("ShopType", "MinLvl", "MaxLvl", "MarkupPercent", "ClassRest")] +
             _series("Item", 20) + _series("ItemMax", 20) + _series("ItemTime", 20) +
             _series("ItemAmt", 20) + _series("ItemPer", 20) + [("Description", TEXT0)],
    "Spells": [("Number", "bigint"), ("Name", "text"), ("Short", "text")] +
              [(c, "bigint") for c in ("ReqLevel", "EnergyCost", "ManaCost", "MinBase", "MaxBase", "Diff",
                                       "TypeOfResists", "Targets", "Duration", "AttType", "Magery",
                                       "MageryLvl", "Cap", "MaxIncLvls", "MaxInc", "MinIncLvls", "MinInc",
                                       "DurIncLvls", "DurInc", "Learnable", "SpellType")] +
              _abil_cols() +
              [(c, INT0) for c in ("CastMsgA", "CastMsgB", "MsgStyle", "ResistAbility")] +
              [("Description", TEXT0), ("DurRand", INT0)],
    "TextBlockLinks": [("Number", "integer NOT NULL"), ("LinkTo", "integer NOT NULL")],
    "TextBlocks": [("Number", "integer NOT NULL"), ("Text", "text NOT NULL")],
}

PRIMARY_KEYS = {
    "RoomDescriptions": ("MapNumber", "RoomNumber"),
    "TextBlockLinks": ("Number",),
    "TextBlocks": ("Number",),
}

INDEXES = [
    'CREATE INDEX idx_actions_display_order ON game_data."Actions" USING btree ("DisplayOrder", "Name");',
]


# =============================================================================
# SQL output
# =============================================================================

def q(ident):
    return '"%s"' % ident.replace('"', '""')


def copy_value(v):
    if v is None:
        return r"\N"
    if isinstance(v, float):
        return repr(v)
    if isinstance(v, int):
        return str(v)
    s = str(v).replace("\x00", "")
    return (s.replace("\\", "\\\\").replace("\t", "\\t")
             .replace("\n", "\\n").replace("\r", "\\r"))


def write_sql(out, tables):
    out.write("-- game_data schema generated from the game's DAT files by tools/dat-import/import_dat.py.\n")
    out.write("-- Load with: psql -v ON_ERROR_STOP=1 -f <this file>\n")
    out.write("-- Replaces the whole game_data schema in one transaction; other schemas are untouched.\n\n")
    out.write("\\set ON_ERROR_STOP on\n")
    out.write("SET client_encoding = 'UTF8';\n")
    out.write("SET standard_conforming_strings = on;\n")
    out.write("SET client_min_messages = warning;\n\n")
    out.write("BEGIN;\n\n")
    out.write("DROP SCHEMA IF EXISTS game_data CASCADE;\n")
    out.write("CREATE SCHEMA game_data;\n\n")

    for table, columns in TABLES.items():
        defs = ",\n".join("    %s %s" % (q(name), sqltype) for name, sqltype in columns)
        out.write("CREATE TABLE game_data.%s (\n%s\n);\n\n" % (q(table), defs))

    for table, columns in TABLES.items():
        rows = tables[table]
        names = [name for name, _ in columns]
        out.write("COPY game_data.%s (%s) FROM stdin;\n" % (q(table), ", ".join(q(n) for n in names)))
        for row in rows:
            out.write("\t".join(copy_value(row[n]) for n in names))
            out.write("\n")
        out.write("\\.\n\n")

    for table, cols in PRIMARY_KEYS.items():
        out.write('ALTER TABLE ONLY game_data.%s ADD CONSTRAINT %s PRIMARY KEY (%s);\n'
                  % (q(table), q(table + "_pkey"), ", ".join(q(c) for c in cols)))
    for stmt in INDEXES:
        out.write(stmt + "\n")
    out.write("\nCOMMIT;\n")


# =============================================================================
# Main
# =============================================================================

def find_dat_files(dat_dir):
    by_lower = {fn.lower(): os.path.join(dat_dir, fn) for fn in os.listdir(dat_dir)}
    found, missing = {}, []
    for key, fn in DAT_FILES.items():
        path = by_lower.get(fn.lower())
        if path and os.path.isfile(path):
            found[key] = path
        else:
            missing.append(fn)
    return found, missing


def read_file(path):
    with open(path, "rb") as fh:
        return fh.read()


def build_tables(paths, hse_rooms):
    tables = {}
    tables["Races"] = load_records(read_file(paths["races"]), parse_race)
    tables["Classes"] = load_records(read_file(paths["classes"]), parse_class)
    tables["Spells"] = load_records(read_file(paths["spells"]), parse_spell)
    tables["Monsters"] = load_records(read_file(paths["monsters"]), parse_monster)
    tables["Items"] = load_records(read_file(paths["items"]), parse_item)
    tables["Shops"] = load_records(read_file(paths["shops"]), parse_shop)
    tables["Messages"] = load_records(read_file(paths["messages"]), parse_message)
    tables["Actions"] = load_records(read_file(paths["actions"]), parse_action)

    rooms, exits, descriptions = load_rooms(read_file(paths["rooms"]), hse_rooms)
    tables["Rooms"] = rooms
    tables["RoomExits"] = exits
    tables["RoomDescriptions"] = [
        {"MapNumber": m, "RoomNumber": r, "Description": d} for (m, r), d in descriptions.items()]

    blocks, links = load_textblocks(read_file(paths["textblocks"]))
    tables["TextBlocks"] = [{"Number": n, "Text": t} for n, t in sorted(blocks.items())]
    tables["TextBlockLinks"] = [{"Number": n, "LinkTo": l} for n, l in sorted(links.items())]
    return tables


def main(argv=None):
    ap = argparse.ArgumentParser(
        description="Convert the game's Btrieve DAT files into a PostgreSQL game_data SQL script.")
    ap.add_argument("--dat-dir", required=True, help="directory holding the game's .dat files")
    ap.add_argument("--hse-dir", help="optional directory of gang-house .HSE description files")
    ap.add_argument("--out", required=True, help="output .sql file ('-' for stdout)")
    args = ap.parse_args(argv)

    if not os.path.isdir(args.dat_dir):
        ap.error("--dat-dir %s is not a directory" % args.dat_dir)
    if args.hse_dir and not os.path.isdir(args.hse_dir):
        ap.error("--hse-dir %s is not a directory" % args.hse_dir)

    paths, missing = find_dat_files(args.dat_dir)
    if missing:
        print("error: missing DAT file(s) in %s: %s" % (args.dat_dir, ", ".join(missing)), file=sys.stderr)
        return 1

    hse_rooms = load_hse_rooms(args.hse_dir)
    tables = build_tables(paths, hse_rooms)

    if args.out == "-":
        write_sql(sys.stdout, tables)
    else:
        with open(args.out, "w", encoding="utf-8", newline="\n") as fh:
            write_sql(fh, tables)

    print("game_data rows:", file=sys.stderr)
    for table in TABLES:
        print("  %-17s %7d" % (table, len(tables[table])), file=sys.stderr)
    if args.hse_dir:
        print("  (%d gang-house description files read)" % len(hse_rooms), file=sys.stderr)
    if args.out != "-":
        print("wrote %s" % args.out, file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
