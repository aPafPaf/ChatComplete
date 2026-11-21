"""Audit ChatComplete's argument coverage from the files the mod writes.

Run after a `/ccc` in game (or an area change, which also writes them):

    python tools/coverage.py
    python tools/coverage.py --all
    python tools/coverage.py --strict
    python tools/coverage.py --json out/coverage.json

It reads %TEMP%\\ChatComplete.usage.txt, states.txt, args.txt and kind.*.txt, and
LocalExile/data/client.db when found. Reports:
  - commands with no suggestions at all;
  - free argument labels where client.db has a likely table (ascendancy name, passive,
    tattoo, mod, ...), i.e. the cheap wins;
  - commands with a catalog but still-free arguments;
  - @kind references whose kind file is missing or empty (broken suggestions).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sqlite3
import sys
import tempfile
from collections import Counter, defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# Optional single words that name a value, not a keyword: [speed], [count], [name].
VALUE_WORDS = {
    "name", "id", "count", "n", "level", "quality", "seed", "radius", "speed",
    "filter", "max", "amount", "damage", "index", "type", "section", "row", "node",
    "value", "values", "text", "message", "seconds", "percent", "chance", "number",
    "part", "path", "time", "x", "y", "a", "b", "degrees", "mod", "rarity", "base",
    "item", "gem", "area", "monster", "stat", "npc", "quest", "flag", "mode",
    "channel", "page", "skill", "passive", "tab", "stash", "slot", "sockets",
    "theme", "passives", "effectid",
}

KEYWORDS = {
    "on", "off", "all", "none", "add", "set", "remove", "clear", "list", "show",
    "open", "refund", "boss", "near", "portal", "ranged", "freeze", "start", "stop",
    "reset", "reload", "enable", "disable", "create", "delete", "info", "status",
    "magic", "rare", "normal", "unique", "implicit", "explicit", "enchant",
    "scourge", "hidden", "small", "medium", "large", "physical", "fire", "cold",
    "lightning", "chaos", "attack", "spell", "both", "endurance", "frenzy", "power",
    "cruel", "merciless", "eternal",
}

# Command/label word -> the client table that likely holds its values.
HINTS = {
    "ascend": ["Ascendancy"],
    "class": ["Characters"],
    "gem": ["SkillGems", "BaseItemTypes"],
    "skill": ["ActiveSkills", "SkillGems"],
    "item": ["BaseItemTypes"],
    "monster": ["MonsterVarieties"],
    "area": ["WorldAreas"],
    "zone": ["WorldAreas"],
    "map": ["WorldAreas"],
    "npc": ["NPCs"],
    "quest": ["QuestFlags"],
    "stat": ["Stats"],
    "passive": ["PassiveSkills"],
    "mod": ["Mods"],
    "tattoo": ["PassiveSkillTattoos"],
    "currency": ["CurrencyItems"],
    "stash": ["StashTabs"],
    "tab": ["StashTabs"],
    "atlas": ["AtlasNode"],
    "unique": ["UniqueStashLayout"],
}


def find_repo(start: Path) -> Path | None:
    for base in (start, *start.parents):
        if (base / "LocalExile" / "data" / "client.db").exists():
            return base
    return None


def strip_prose(text: str) -> str:
    out, depth, i = [], 0, 0
    while i < len(text):
        c = text[i]
        if c in "<[":
            depth += 1
        elif c in ">]":
            depth = max(0, depth - 1)
        if c == "(" and depth == 0:
            end = text.find(")", i + 1)
            if end > i:
                i = end + 1
                continue
        out.append(c)
        i += 1
    return "".join(out)


def split_top(text: str, separator: str = "|") -> list[str]:
    parts, start, depth = [], 0, 0
    for i, c in enumerate(text):
        if c in "<[":
            depth += 1
        elif c in ">]":
            depth = max(0, depth - 1)
        elif c == separator and depth == 0:
            parts.append(text[start:i])
            start = i + 1
    parts.append(text[start:])
    return parts


def match_group(text: str, open_at: int) -> int:
    close = "]" if text[open_at] == "[" else ">"
    depth = 0
    for i in range(open_at, len(text)):
        if text[i] == text[open_at]:
            depth += 1
        elif text[i] == close:
            depth -= 1
            if depth == 0:
                return i
    return -1


def looks_numeric(text: str) -> bool:
    text = text.strip()
    if text.isdigit():
        return True
    dash = text.find("-")
    return dash > 0 and text[:dash].isdigit() and text[dash + 1:].isdigit()


def looks_date(text: str) -> bool:
    low = text.lower()
    return "yyyy" in low or "mm-dd" in low or "hh:" in low


def choice_part(part: str) -> tuple[str, str] | None:
    part = part.strip()
    if not part:
        return None
    if " " in part and "<" not in part and "[" not in part:
        return ("value", part)
    low = part.lower()
    if looks_date(part) or (low in VALUE_WORDS and low not in KEYWORDS):
        return ("value", part)
    return ("literal", part)


def labels_in(text: str) -> list[str]:
    """Placeholder labels in one usage alternative (literals/choices are ignored)."""
    labels: list[str] = []
    i = 0
    while i < len(text):
        c = text[i]
        if c.isspace():
            i += 1
            continue
        if c in "[<":
            end = match_group(text, i)
            if end < 0:
                break
            inner = text[i + 1:end].strip()
            if inner and not inner.endswith("...") and inner not in ("...", ".."):
                parts = split_top(inner)
                if len(parts) > 1:
                    for part in parts:
                        found = choice_part(part)
                        if found and found[0] == "value":
                            labels.append(found[1])
                else:
                    low = inner.lower()
                    if looks_numeric(inner) or looks_date(inner):
                        labels.append(inner)
                    elif low in VALUE_WORDS and low not in KEYWORDS:
                        labels.append(inner)
                    elif " " in inner and "<" not in inner and "[" not in inner:
                        labels.append(inner)
                    elif c == "<":
                        labels.append(inner)
                    else:
                        labels.extend(labels_in(inner))
            i = end + 1
            continue
        while i < len(text) and not text[i].isspace() and text[i] not in "[<":
            i += 1
    return labels


def read_usage(path: Path) -> dict[str, str]:
    result = {}
    if not path.exists():
        return result
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if "\t" in line:
            name, usage = line.split("\t", 1)
            result[name] = usage
    return result


def read_args(path: Path) -> dict[str, list[str]]:
    result = {}
    if not path.exists():
        return result
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = line.strip()
        if not line or line.startswith("#") or " " not in line:
            continue
        name, kinds = line.split(" ", 1)
        result[name] = [k for k in kinds.split(",") if k]
    return result


def read_states(path: Path) -> dict[str, list[list[str]]]:
    """command -> [(prefix words, tokens)]."""
    result: dict[str, list[list[str]]] = defaultdict(list)
    if not path.exists():
        return result
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        if not line or line.startswith("#"):
            continue
        parts = line.split("\t")
        if len(parts) < 3:
            continue
        command, prefix, tokens = parts[0], parts[1], parts[2]
        result[command].append([prefix.split() if prefix else [], tokens.split("|")])
    return result


def table_columns(db: Path) -> dict[str, list[str]]:
    result = {}
    try:
        con = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
        try:
            tables = [r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")]
            for table in tables:
                cols = [r[1] for r in con.execute(f'PRAGMA table_info("{table}")')]
                result[table] = cols
        finally:
            con.close()
    except sqlite3.Error as ex:
        print(f"warning: cannot read {db}: {ex}", file=sys.stderr)
    return result


DATA_WORDS = {
    "name", "id", "item", "skill", "gem", "monster", "area", "map", "npc",
    "quest", "stat", "passive", "mod", "tattoo", "currency", "stash", "tab",
    "atlas", "unique", "class", "ascend", "section", "theme", "passives",
}
UNIT_WORDS = {
    "n", "count", "level", "amount", "seed", "speed", "radius", "index",
    "page", "x", "y", "z", "ms", "s", "percent", "chance", "seconds",
    "degrees", "number", "value", "life", "mana", "energy", "shield",
    "peak", "from", "to", "open", "after", "never", "model", "created", "type",
    "json", "hex", "u8", "u16", "u32", "u64",
}


def words_in(text: str) -> set[str]:
    """Lower-case words, splitting camelCase: PassiveSkillGraphId -> passive skill graph id."""
    text = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", text)
    text = re.sub(r"(?<=[A-Z])(?=[A-Z][a-z])", " ", text)
    return set(re.findall(r"[a-z]+", text.lower()))


def data_label(label: str) -> bool:
    words = words_in(label)
    if not words:
        return False
    if words & UNIT_WORDS and not (words & {"name", "id"}):
        return False
    return bool(words & DATA_WORDS)


def hint_words(command: str, labels: list[str]) -> set[str]:
    words = words_in(command)
    for label in labels:
        words.update(words_in(label))
    return words


def hint_tables(command: str, labels: list[str], tables: dict[str, list[str]]) -> list[str]:
    found: list[str] = []
    words = hint_words(command, labels)
    for word in sorted(words):
        if len(word) < 3:
            continue
        for key, names in HINTS.items():
            if not (word == key or word == key + "s" or (len(key) >= 4 and word.startswith(key))):
                continue
            for table in names:
                if table in tables:
                    cols = [c for c in tables[table] if c.lower() in
                            ("id", "name", "leaguename", "character", "row", "_row")]
                    entry = f"{table}({', '.join(cols[:4]) or '?'})"
                    if entry not in found:
                        found.append(entry)
    return found


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--temp", type=Path, default=Path(tempfile.gettempdir()))
    parser.add_argument("--db", type=Path)
    parser.add_argument("--json", type=Path)
    parser.add_argument("--all", action="store_true", help="list every command, not just the first 25")
    parser.add_argument("--strict", action="store_true", help="exit 1 on broken kinds or likely gaps")
    args = parser.parse_args()

    temp = args.temp
    usage_path = temp / "ChatComplete.usage.txt"
    states_path = temp / "ChatComplete.states.txt"
    args_path = temp / "ChatComplete.args.txt"
    if not usage_path.exists():
        print(f"missing {usage_path}; run /ccc in game first", file=sys.stderr)
        return 2

    usages = read_usage(usage_path)
    declared = read_args(args_path)
    states = read_states(states_path)

    db = args.db
    if db is None:
        repo = find_repo(Path(__file__).resolve().parent)
        db = repo / "LocalExile" / "data" / "client.db" if repo else None
    tables = table_columns(db) if db and db.exists() else {}

    rows = []
    kinds_used: set[str] = set()
    broken: list[str] = []
    for command, usage in sorted(usages.items()):
        command_states = states.get(command, [])
        command_kinds = {t[1:] for _, tokens in command_states for t in tokens if t.startswith("@")}
        command_kinds.update(declared.get(command, []))
        kinds_used.update(command_kinds)
        literals = {t for _, tokens in command_states for t in tokens if not t.startswith("@")}
        alternatives = split_top(strip_prose(usage))
        labels: list[str] = []
        for alternative in alternatives:
            labels.extend(labels_in(alternative))
        labels = list(dict.fromkeys(label for label in labels if label))
        data_labels = [label for label in labels if data_label(label)]
        hints = hint_tables(command, data_labels, tables) if not command_kinds else []
        rows.append({
            "command": command,
            "usage": usage,
            "states": len(command_states),
            "kinds": sorted(command_kinds),
            "literals": sorted(literals),
            "labels": labels,
            "data_labels": data_labels,
            "hints": hints,
        })

    for kind in sorted(kinds_used):
        path = temp / f"ChatComplete.kind.{kind}.txt"
        if not path.exists() or path.stat().st_size == 0:
            broken.append(f"{kind} ({'missing' if not path.exists() else 'empty'})")

    with_states = [r for r in rows if r["states"]]
    without_states = [r for r in rows if not r["states"]]
    high = [r for r in rows if not r["kinds"] and r["data_labels"] and r["hints"]]
    for row in rows:
        covered_count = len(declared.get(row["command"], []))
        row["free_labels"] = row["labels"][covered_count:] if covered_count else row["labels"]
    partial = [r for r in rows if r["kinds"] and r["free_labels"]]
    covered = [r for r in rows if r["kinds"] and not r["free_labels"]]

    print("# ChatComplete coverage")
    print()
    print(f"commands: {len(rows)} | with suggestions: {len(with_states)} | without: {len(without_states)}")
    print(f"catalog kinds used: {len(kinds_used)} | broken: {len(broken)}")
    print()

    print(f"## High-priority gaps (no catalog, client.db has data): {len(high)}")
    for row in high if args.all else high[:25]:
        print(f"- `/{row['command']}`: free {row['data_labels']}; hints: {', '.join(row['hints'])}")
    if not args.all and len(high) > 25:
        print(f"- ... {len(high) - 25} more (--all)")
    print()

    no_states_with_args = [r for r in without_states if r["labels"]]
    no_states_no_args = [r for r in without_states if not r["labels"]]
    print(f"## Commands with arguments but no suggestions: {len(no_states_with_args)}")
    for row in no_states_with_args if args.all else no_states_with_args[:25]:
        print(f"- `/{row['command']}`: {row['usage'][:140]}")
    if not args.all and len(no_states_with_args) > 25:
        print(f"- ... {len(no_states_with_args) - 25} more (--all)")
    print()
    print(f"## Commands with no arguments: {len(no_states_no_args)}")
    print(", ".join(f"/{r['command']}" for r in no_states_no_args[:80]) + (" ..." if len(no_states_no_args) > 80 else ""))
    print()

    print(f"## Partial coverage (catalog + still-free labels): {len(partial)}")
    for row in partial if args.all else partial[:25]:
        print(f"- `/{row['command']}`: {', '.join(row['kinds'])} + free {row['free_labels']}")
    if not args.all and len(partial) > 25:
        print(f"- ... {len(partial) - 25} more (--all)")
    print()

    post = []
    for command, entries in sorted(states.items()):
        for prefix, tokens in entries:
            if prefix and (prefix[0].startswith("@") or "*" in prefix):
                post.append((command, " ".join(prefix), tokens))
    print(f"## Post-value transitions (after a catalog value or runtime word): {len(post)}")
    for command, prefix, tokens in post:
        print(f"- `/{command} {prefix}` -> {', '.join(tokens[:6])}" + (" ..." if len(tokens) > 6 else ""))
    print()

    print(f"## Fully covered finite arguments: {len(covered)}")
    print(", ".join(f"/{r['command']}" for r in covered[:60]) + (" ..." if len(covered) > 60 else ""))
    print()

    if broken:
        print("## Broken catalog files")
        for item in broken:
            print(f"- {item}")
        print()

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps({
            "commands": len(rows),
            "with_states": len(with_states),
            "without_states": len(without_states),
            "kinds": sorted(kinds_used),
            "broken": broken,
            "high": high,
            "partial": partial,
            "post_value": [{"command": c, "prefix": p, "tokens": t} for c, p, t in post],
            "rows": rows,
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"wrote {args.json}")

    if args.strict and (broken or high):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
