# ChatComplete

Native command and argument suggestions in [LocalExile](https://github.com/LocalExile/LocalExile)'s
client chat. No overlay: the rows are the client's own suggestion rows, drawn above the
input, with live preview, a yellow selection and Tab/Enter support.

- Client: **3.29.3c** (Windows)
- LocalExile: **0.6.0-beta**
- DLL: `NativeSuggest.dll`, built from `native/`
- Mod: `mods/chatcomplete/` (a LocalExile script mod)

## Features

### What it suggests

- **Commands.** Type `/` to list every command. Search matches any part of the name;
  `*` lists everything or acts as a wildcard (`spawn*maven`); a query with regex
  metacharacters (`^`, `$`, `[]`, `|`, ...) is a case-insensitive regex.
- **Server values.** Monsters, items, gems, areas, NPCs, quest flags and stats come from
  the running Gateway, not a hand-written list.
- **Context.** Subcommands, enums and flags appear in the right position:
  `/charges `, `/item_mod set `, `/ascendancy `, `/hit `, `/cluster_jewel `.
- **After a value.** `/spawn_mob Sand Spitter ` offers `friendly` and the four
  rarities; `/tattoo_apply <name> ` offers the passive nodes; `/item Mageblood 1 `
  offers `magic`, `rare` and `max`; `/mob_mods 0x1 ` offers the monster affixes.
- **Monsters by id.** `/entity ` lists the area's living monsters, `/entity <id> `
  offers its verbs (including `kill`), and `/entity <id> skill ` offers that actor's
  own skills only.
- **Static client data.** Passive nodes, Atlas maps, quest presets, tattoos, unique
  jewels, item mods, standard stash tabs, cluster jewel themes and installed
  mods/modules.
- **Names, not opaque ids.** Areas show as `The Twilight Strand (1_1_1)`; passives,
  mods and other entries show their human name while the id is what gets typed. Detail
  is searchable too.

### How it feels

- **Native.** Real suggestion rows in the client's own chat, above the input: no
  overlay, live preview, yellow selection, no empty or filler rows.
- **Keyboard.** Up/Down move through the whole list and preview the row; holding an
  arrow repeats. Tab accepts and puts the caret at the end. Enter sends even while the
  list is open. Esc closes it.
- **Sized to the result.** The window is as tall as the matches, up to 12 rows.

### Freshness

- Catalogs come from the live server and refresh on the first area or `/ccc`; the DLL
  notices changed files on the next keystroke, so `/cc reload` is only a manual
  fallback.
- Commands added by other mods appear without editing a file.

## Install

### Waypoint

Add this repository's URL to Waypoint's Browse panel and install ChatComplete. Waypoint
installs the release zip into `Mods/Binaries/chatcomplete/`.

```
https://github.com/aPafPaf/ChatComplete
```

### By hand

Download `chatcomplete-0.4.0.zip` from the repository's releases and extract it into
`Mods/Binaries/chatcomplete/`, so that the folder holds:

```
Mods/Binaries/chatcomplete/
  mod.json
  settings.json
  chatcomplete.cs
  README.md
  bin/NativeSuggest.dll
```

Start LocalExile as usual. A Gateway that is already running loads the mod within a
second. The mod ships disabled (opt-in): enable it with `/mod_enable chatcomplete`.
`/mod` lists the mod and why it did not load, if it did not.

The mod injects `NativeSuggest.dll` into `Game.patched.exe`, including a client started
after the Gateway. It looks for a new client every 1.5 seconds. The DLL stays loaded
until the client exits.

## Usage

| Keys | What they do |
|---|---|
| `/` | list commands |
| `Up` / `Down` | move through the whole list; the input previews the selected row |
| `Tab` | accept the selected row, caret at the end |
| `Enter` | send the message (the list is hidden first) |
| `Esc` | close the list |

| Command | What it does |
|---|---|
| `/ccc` | server command: rewrite the catalogs from the live connection |
| `/cc reload` | client command: re-read the catalog files now (never reaches the server) |

Value catalogs come from the Gateway's `CompletionValues` (`area`, `monster`, `item`,
`gem`, `stat`, `npc`, `quest_flag`). `/entity` is written by the mod from the gm-commands
`MonsterActions` API, one skill catalog per living actor. Finite keyword choices are
parsed from the command's usage string into `ChatComplete.states.txt`; free text, large
numeric ranges, dates and paths are not enumerated.

## How it works

Two parts, because the data and the UI live on different sides:

```
LocalExile Gateway (mods/chatcomplete/chatcomplete.cs)
  |  injects NativeSuggest.dll into Game.patched.exe
  |  writes %TEMP%\ChatComplete.commands.txt, args.txt, kind.<kind>.txt, states.txt
  v
Client (NativeSuggest.dll)
     hooks Chat_RebuildSuggestions and Chat_HandleKey
     reads the catalogs and fills the client's own list
```

- The mod runs in the Gateway. It cannot draw the list, but it sees every loaded
  module's and mod's commands (`conn.Commands.Usages`) and the game's completion values
  (`Program.GameLogicOrNull.CompletionValues`).
- The DLL runs in the client. It cannot see the Gateway, but it owns the chat UI.
- The mod writes catalogs to `%TEMP%`; the DLL reads them. It reloads by itself when the
  files change (checked on the next rebuild, at most twice a second).
- `ChatComplete.states.txt` maps a command and the literal words already typed to the
  tokens that may follow. The DLL prefers the longest matching prefix, filters the
  partial word, and inserts the full line on Tab.
- The DLL resolves its function addresses from byte signatures at load
  (`native/Signatures.generated.h`), so a client build change usually needs only new
  signatures, not source edits.

A Gateway may declare a `CommandGrammar` for a command; when it does, the mod uses that
declaration instead of parsing the command's usage. This is optional: the stock Gateway
needs no change, and the release works entirely from usage parsing.

## Build from source

Requirements: Windows, CMake 3.20+, Visual Studio 2022 (MSVC, x64). The client must be
started through LocalExile's `scripts/dev/live.ps1` or `LocalExile.exe`; any other launch
can update the client and break the setup.

```powershell
cmake -S native -B native/build -G "Visual Studio 17 2022" -A x64
cmake --build native/build --config Release --target nativesuggest
```

The DLL is `native/build/Release/NativeSuggest.dll`. The release workflow builds it from
the tag and puts it into the release zip.

### Development installation

From a LocalExile checkout:

```powershell
powershell -ExecutionPolicy Bypass -File install_mod.ps1 -Repo C:\LocalExile
```

Without `-Repo`, the script expects this repository inside the checkout at
`<checkout>/Tools/ChatComplete` (its development location). It copies
`mods/chatcomplete/` and the built DLL into `Mods/Binaries/chatcomplete/` (the DLL in
`bin/`). The Gateway hot-reloads the mod.

Without the mod (DLL only), from a LocalExile checkout:

```powershell
python inject.py --dll native/build/Release/NativeSuggest.dll
```

`inject.py` uses LocalExile's `scripts/dev/client_control.py` Frida session.

### Catalogs without the mod

`tools/export_args.py` writes the same catalog files from `gm-commands.dll` and
`data/client.db`. It expects a LocalExile checkout; see
[docs/PORTING.md](docs/PORTING.md) for the formats.

## Coverage audit

Run `/ccc` in game once, then:

```powershell
python tools/coverage.py            # summary
python tools/coverage.py --all      # every command
python tools/coverage.py --strict   # fail on broken kind files or likely gaps
python tools/coverage.py --json out/coverage.json
```

The maintenance workflow (when to run it, how to read it, how to fix a gap and the
release gate) is in [docs/AUDIT.md](docs/AUDIT.md). It reads the mod's
`%TEMP%\ChatComplete.usage.txt`, `states.txt`, `args.txt` and `kind.*.txt`, plus
`LocalExile/data/client.db`, and reports:

- commands with arguments but no suggestions;
- **likely gaps** where the command has no catalog but `client.db` already has a table
  (`ascendancy`, `passive`, `tattoo`, `mod`, `atlas`, ...), so the next cheap win is
  obvious;
- commands with a catalog but still-free arguments;
- `@kind` references whose kind file is missing or empty.

Current snapshot: 128 commands, 60 with suggestions, 22 catalog kinds, **0 gaps**.
`--strict` passes. `/ascendancy` was found this way: its `[name]` had no catalog even
though `Ascendancy(Name, LeagueName)` was already in the client tables.

## Compatibility and limits

- **Windows only.** The mod injects a native DLL with `LoadLibraryW`; the Linux build of
  LocalExile is not supported.
- Tested with the **3.29.3c** client. Function addresses are found by signatures at
  load, so **3.29.3d** may work, but it is not promised until checked.
- The mod is local-only: it injects into a process on the same machine and contacts no
  network service.
- Antivirus or SmartScreen may warn about DLL injection. Both parts are built from the
  sources here.
- Catalog files are shared through `%TEMP%\ChatComplete.*` and are rewritten when a
  player enters an area or runs `/ccc`.
- Keyword coverage is as good as the usage strings: commands with free text, large
  numeric ranges, dates or value-only alternatives keep only the catalog values they
  declare.
- Mouse clicks on a row are not handled; use Up/Down + Tab.

## Documentation

- [docs/UI-NOTES.md](docs/UI-NOTES.md) - the client's chat UI, coordinates, text
  ownership and event semantics.
- [docs/FINDINGS.md](docs/FINDINGS.md) - addresses, quirks and evidence.
- [docs/PORTING.md](docs/PORTING.md) - signatures, offsets and the port checklist.
- [docs/AUDIT.md](docs/AUDIT.md) - the argument-coverage audit and release gate.
- [docs/PROGRESS.md](docs/PROGRESS.md) - history and root causes of the fixes.
- [CHANGELOG.md](CHANGELOG.md) - what changed.

## License

MIT. See [LICENSE](LICENSE).

LocalExile is not an official Grinding Gear Games product and is not affiliated with or
endorsed by Path of Exile or Grinding Gear Games. This project is for local, offline
use with LocalExile.
