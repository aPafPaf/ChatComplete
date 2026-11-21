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

Arguments are completed for the commands the server declares. The current kinds are
`area`, `monster`, `item`, `gem`, `stat`, `npc` and `quest_flag`; they are exactly the
Gateway's `CompletionValues`.

## How it works

Two parts, because the data and the UI live on different sides:

```
LocalExile Gateway (mods/chatcomplete/chatcomplete.cs)
  |  injects NativeSuggest.dll into Game.patched.exe
  |  writes %TEMP%\ChatComplete.commands.txt, args.txt, kind.<kind>.txt
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
- Mouse clicks on a row are not handled; use Up/Down + Tab.

## Documentation

- [docs/UI-NOTES.md](docs/UI-NOTES.md) - the client's chat UI, coordinates, text
  ownership and event semantics.
- [docs/FINDINGS.md](docs/FINDINGS.md) - addresses, quirks and evidence.
- [docs/PORTING.md](docs/PORTING.md) - signatures, offsets and the port checklist.
- [docs/PROGRESS.md](docs/PROGRESS.md) - history and root causes of the fixes.
- [CHANGELOG.md](CHANGELOG.md) - what changed.

## License

MIT. See [LICENSE](LICENSE).

LocalExile is not an official Grinding Gear Games product and is not affiliated with or
endorsed by Path of Exile or Grinding Gear Games. This project is for local, offline
use with LocalExile.
