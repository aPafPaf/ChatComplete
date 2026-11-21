"""Export commands and argument catalogs for NativeSuggest.

Writes to %TEMP%:
  ChatComplete.commands.txt        every /command found in the gm-commands mod
  ChatComplete.args.txt            "<command> <kind>[,<kind>...]"
  ChatComplete.kind.<kind>.txt     "value<TAB>detail" per line

Kinds match the Gateway's CompletionValues: area, monster, item, gem, stat, npc,
quest_flag. Data comes from LocalExile/data/client.db.

Run from the repository root:
    python tools/export_args.py
"""
import os
import re
import sqlite3
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent


def find_localexile_root() -> Path | None:
    """The checkout that holds LocalExile/: $LOCALEXILE_ROOT, else this repo or one above it."""
    if env := os.environ.get("LOCALEXILE_ROOT"):
        return Path(env)
    for base in (HERE, *HERE.parents):
        if (base / "LocalExile" / "data" / "client.db").exists():
            return base
    return None


ROOT = find_localexile_root()
TEMP = Path(tempfile.gettempdir())
DB = ROOT / "LocalExile" / "data" / "client.db" if ROOT else Path("LocalExile/data/client.db")
MOD_DLL = ROOT / "LocalExile" / "Mods" / "Binaries" / "gm-commands" / "gm-commands.dll" if ROOT else Path("LocalExile/Mods/Binaries/gm-commands/gm-commands.dll")

# command -> argument kinds, by position. Kinds are the Gateway's CompletionValues.
ARGS = {
    "spawn_mob": ["monster"],
    "statue": ["monster"],
    "kill": ["monster"],
    "item": ["item"],
    "gem": ["gem"],
    "goto": ["area"],
    "tp": ["area"],
    "npc": ["npc"],
    "npc_place": ["npc"],
    "npc_face": ["npc"],
    "quest_flag": ["quest_flag"],
    "set_stat": ["stat"],
    "stat": ["stat"],
}

USAGE_RE = re.compile(r"^/([a-z][a-z0-9_]*)(?:\s|$)")


def mod_commands():
    """Command names from the gm-commands DLL string literals ("/name usage...")."""
    if not MOD_DLL.exists():
        return set()
    data = MOD_DLL.read_bytes()
    names = set()
    texts = [m.group().decode("ascii", errors="ignore") for m in re.finditer(rb"[\x20-\x7e]{4,}", data)]
    texts += [m.group().decode("utf-16le", errors="ignore")
              for m in re.finditer(rb"(?:[\x20-\x7e]\x00){4,}", data)]
    for text in texts:
        if not text.startswith("/"):
            continue
        if "/" in text[1:].split(" ", 1)[0]:
            continue  # a path such as /home/runner/...
        hit = USAGE_RE.match(text)
        if hit:
            names.add(hit.group(1))
    return names


def connect():
    return sqlite3.connect(f"file:{DB}?mode=ro", uri=True)


def rows(con, sql):
    return con.execute(sql).fetchall()


def export_catalogs():
    if not DB.exists():
        print(f"missing {DB}", file=sys.stderr)
        return False
    con = connect()
    try:
        gem_rows = {r[0] for r in rows(con, "SELECT BaseItemTypesKey FROM SkillGems")}
        data = {
            "area": rows(con, "SELECT Id, Name FROM WorldAreas WHERE Id IS NOT NULL AND Id <> 'NULL'"),
            "monster": rows(con, "SELECT Id, Name FROM MonsterVarieties WHERE Id IS NOT NULL"),
            "item": [r for r in rows(con, "SELECT _row, Id, Name FROM BaseItemTypes "
                                           "WHERE Name IS NOT NULL AND Name NOT LIKE '[%'")
                     if r[0] not in gem_rows],
            "gem": rows(con, "SELECT DISTINCT b.Name FROM SkillGems g JOIN BaseItemTypes b "
                             "ON b._row = g.BaseItemTypesKey WHERE b.Name IS NOT NULL"),
            "stat": rows(con, "SELECT Id FROM Stats WHERE Id IS NOT NULL AND Id <> ''"),
            "npc": rows(con, "SELECT Id, Name FROM NPCs WHERE Id IS NOT NULL"),
            "quest_flag": rows(con, "SELECT Id FROM QuestFlags WHERE Id IS NOT NULL"),
        }
    finally:
        con.close()

    for kind, table in data.items():
        out = TEMP / f"ChatComplete.kind.{kind}.txt"
        seen = set()
        with out.open("w", encoding="utf-8", newline="\n") as f:
            for row in table:
                value = row[-2] if kind == "item" else row[0]
                detail = row[-1] if len(row) > 1 else ""
                if not value or value in seen:
                    continue
                seen.add(value)
                f.write(f"{value}\t{detail or ''}\n")
        print(f"wrote {out} ({len(seen)} entries)")
    return True


def export_commands(names):
    out = TEMP / "ChatComplete.commands.txt"
    out.write_text("\n".join(sorted(names)) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(names)} commands)")

    kind_out = TEMP / "ChatComplete.args.txt"
    lines = ["# command kinds...  (kinds: area, monster, item, gem, stat, npc, quest_flag)"]
    for command, kinds in sorted(ARGS.items()):
        if names and command not in names:
            continue
        lines.append(f"{command} {','.join(kinds)}")
    kind_out.write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"wrote {kind_out} ({len(lines) - 1} commands with arguments)")


def main():
    names = mod_commands()
    export_commands(names)
    if not export_catalogs():
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
