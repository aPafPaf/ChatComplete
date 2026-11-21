# ChatComplete

Native command and argument suggestions in the LocalExile client's own chat UI. No
overlay: the rows are real suggestion rows of the client, drawn above the input.

Type `/` to list commands, or part of a command and its arguments:

- `/spawn_mob`, `/statue`, `/kill` - monsters
- `/item` - items, `/gem` - gems
- `/goto`, `/tp` - areas
- `/npc`, `/npc_place`, `/npc_face` - NPCs
- `/quest_flag` - quest flags
- `/stat`, `/set_stat` - stats

Search is case-insensitive on any part of the name, `*` is a wildcard (or lists
everything), and a query with regex metacharacters is a regular expression. Up/Down
move through the whole list and preview the row in the input; Tab accepts it; Enter
sends even while the list is open. The list never has empty rows.

## Requirements

- LocalExile **0.5.2-beta** (or later, after it is checked against the release).
- Windows. The mod injects a native DLL into the local client; it does not work on the
  Linux build of LocalExile.
- Client **3.29.3c** (the build this DLL was tested with; signatures are resolved at
  load, so later builds may work but are not promised).

## Install

With Waypoint: add this repository's URL to its Browse panel and install ChatComplete.

By hand: extract the release zip into `Mods/Binaries/chatcomplete/`, so the folder holds
`mod.json`, `settings.json`, `chatcomplete.cs` and `bin/NativeSuggest.dll`. The Gateway picks
the mod up while it runs. `/mod` shows whether it loaded; enable it with `/mod_enable chatcomplete` (the mod ships
disabled, like other opt-in mods).

The mod injects the DLL into `Game.patched.exe`, including a client started after the
Gateway. The first injection happens within about 1.5 seconds of the client appearing.

## Commands

| Command | What it does |
|---|---|
| `/ccc` | rewrites the catalogs from the live server (commands and argument values) |
| `/cc reload` | asks the client DLL to re-read the catalog files now (a fallback; the DLL also notices changes itself) |

`/cc reload` is intercepted by the client and never reaches the server.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `dllPath` | empty | `bin/NativeSuggest.dll` in this folder; set a path to use another DLL |
| `processName` | `Game.patched` | the client process to inject into |
| `intervalMs` | `1500` | how often to look for a new client |
| `autoInject` | `true` | turn injection off while keeping the rest of the mod |

Settings are edited by Waypoint or in `data/mods/chatcomplete.json`.

## Notes

- The mod is local-only: it injects into a process on the same machine. It does not
  contact any network service.
- Antivirus or SmartScreen may warn about the DLL injection. The DLL and the mod are
  built from the sources in this repository.
- Catalogs are written to `%TEMP%\ChatComplete.*` for the client DLL to read.

This is an unofficial project, not affiliated with or endorsed by Grinding Gear Games.
