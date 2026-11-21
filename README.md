# ChatComplete

Native command and argument suggestions in [LocalExile](https://github.com/LocalExile/LocalExile)'s
client chat. No overlay: the rows are the client's own suggestion rows, drawn above the
input, with live preview, a yellow selection and Tab/Enter support.

- Client: **3.29.3c** (Windows)
- LocalExile: **0.5.2-beta**
- DLL: `NativeSuggest.dll`, built from `native/`
- Mod: `mods/chatcomplete/` (a LocalExile script mod)

## Features

- Type `/` to list every command. Matching is case-insensitive on any part of the name;
  prefix matches come first, and a fully typed command stays in the list (`/god`).
- Command arguments complete from the server's own catalogs: monsters, items, gems,
  areas, NPCs, quest flags and stats.
- Contextual choices from each command's usage are offered in the right position:
  subcommands (`/charges ` -> add/clear, `/item_mod set ` -> enchant/explicit/...),
  enums (`/ascendancy ` -> normal/phrecia, `/hit ` -> fire/cold/...), flags
  (`ranged`, `freeze`) and `/tp ` -> list plus areas. `/kill ` lists `all`, `boss`
  and `near` before the monster catalog. Small numeric sets such as
  `[created 0|1]` and `[type 1-4]` are offered too.
- Framework catalogs where the Gateway provides them: `/mod_enable`, `/mod_disable`
  and `/mod_reload` suggest installed mods, `/module_reload` suggests modules, and
  `/commands ` suggests command names. On a released Gateway without these framework
  kinds the mod still loads and those commands simply have no value suggestions.
- If a Gateway provides a `CommandGrammar` for a command, the mod prefers it and falls
  back to parsing the usage otherwise. This is optional; the stock Gateway needs no
  change for this release.
- `/ascendancy normal ` and `/ascendancy phrecia ` suggest the names available to the
  character's class (from `client.db`'s `Ascendancy` table: normal uses `Name`, phrecia
  uses `LeagueName`).
- More static data from `client.db`: `/add_passive ` shows passive node names and inserts
  `PassiveSkillGraphId`; `/atlas_complete ` shows map names and inserts `AtlasNode.Id`;
  `/quest_preset ` lists `CharacterStartQuestState` ids; `/tattoo ` and `/tattoo_apply `
  list tattoo item names; `/unique_jewel ` lists the Gateway's unique-jewel catalogue;
  `/item_mod ` lists the item-domain `Mods` (Id, name), `/item_tab ` the standard stash
  tab names, `/cluster_jewel ` the size's themes, `/mob_mods ` the monster affixes, and
  `/mod_*` / `/module_reload ` the installed mods and modules.
- Arguments after a value keep going: `/spawn_mob Sand Spitter ` offers
  `friendly`, `normal`, `magic`, `rare`, `unique`; `/tattoo_apply <name> ` offers the
  passive nodes; `/item Mageblood ` offers `magic`, `rare`, `max`; `/mob_mods 0x1 `
  offers the monster affixes. The same works after numbers and flags:
  `/hit fire 100 ` -> `ranged`/`freeze`, `/cluster_jewel large 8 2 ` -> the themes,
  `/item Mageblood 1 ` -> `magic`/`rare`/`max`.
- A state prefix may be `@kind` (an exact catalog value, possibly several words) or
  `*` (one runtime word such as a monster id or a count). The custom monster name after
  the rarity in `/spawn_mob` is arbitrary text: it is deliberately not a catalog.
- Catalog entries show and search their detail: areas appear as
  `The Twilight Strand (1_1_1)` and match a typed name, while other kinds search
  their ids/tags as well as their values.
- `*` lists everything, or acts as a wildcard inside a query (`spawn*maven`); a query
  with regex metacharacters (`^`, `$`, `[]`, `|`, ...) is a case-insensitive regex.
- Up/Down move through the whole match list (up to 4000 entries), scroll the 12-row
  window and preview the row in the input. Holding the arrow works.
- Tab accepts the selected row and puts the caret at the end. Enter sends the message
  even while the list is open. Esc closes the list.
- The window is sized to the matches: no empty or filler rows.
- The catalog comes from the running server, so commands added by other mods appear
  without editing any file. `/ccc` rewrites it; the DLL also re-reads changed files on
  the next keystroke.

## Install

### Waypoint

Add this repository's URL to Waypoint's Browse panel and install ChatComplete. Waypoint
installs the release zip into `Mods/Binaries/chatcomplete/`.

```
https://github.com/aPafPaf/ChatComplete
```

### By hand

Download `chatcomplete-0.2.0.zip` from the repository's releases and extract it into
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
`gem`, `stat`, `npc`, `quest_flag`). Finite keyword choices are parsed from the command's
usage string into `ChatComplete.states.txt`; free text, large numeric ranges, dates and
paths are not enumerated.

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
