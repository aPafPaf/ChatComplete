# Changelog

All dates are 2026-11-21 unless noted. Client build: 3.29.3c.

## NativeSuggest — current

### Added
- Local server mod `chatcomplete` (`mods/chatcomplete/`, installed by `install_mod.ps1` into the
  gitignored `LocalExile/Mods/Binaries/`): injects `NativeSuggest.dll` into every local
  `Game.patched.exe` (new PIDs too), writes the catalogs from the live connection
  (`conn.Commands.Usages`) and the Gateway's `CompletionValues` (all seven argument
  kinds), and serves `/ccc` to rewrite them on demand.
- Automatic catalog reload in the DLL: it compares file stamps on the next rebuild
  (500 ms throttle) and reloads without `/cc reload`; `/cc reload` remains a manual
  fallback and still works without the mod.
- Client UI notes: [UI-NOTES.md](docs/UI-NOTES.md) - UI architecture, coordinate quirks,
  text/ownership rules, event semantics and the conclusions for extending the list.
- Command and argument suggestions in the client's own list (no overlay):
  `/spawn_mob`, `/statue`, `/kill` (monsters), `/item`, `/gem`, `/goto`, `/tp`, `/npc`,
  `/npc_place`, `/npc_face`, `/quest_flag`, `/stat`, `/set_stat`.
- Live preview: Up/Down changes the input to the selected row immediately.
- Yellow highlight of the selected row (`item+0x288`, BGRA).
- `*` as “list everything” and as a wildcard inside a query (`spawn*maven`); regex when
  the query has regex metacharacters.
- Substring command search on any part of the name (`/mob` → `/spawn_mob`), prefix first.
- Exact command stays listed (`/god` → `/god`).
- Auto-repeat scrolling: holding Up/Down moves through the whole match list.
- Tab accepts with the caret at the end (no trailing space); Enter sends even with the
  list open.
- 50 ms watchdog hides the list when the chat closes, whichever way it was closed.
- `/cc reload` chat command: reloads `commands.txt`/`args.txt` and forgets the loaded
  kind catalogs in the running DLL, without restarting the client. It is intercepted on
  Enter and never sent to the server.
- Byte-signature resolution at load (`tools/gen_signatures.py`, `Suggest_ClearAll`
  included), so a client update only needs new signatures.

### Changed
- Distributed as a standalone repository: `mods/chatcomplete/` with `mods.json` for
  Waypoint, a GitHub Actions build and release workflow (the release zip is built from
  the tag), and `dllPath` defaults to empty, meaning `bin/NativeSuggest.dll` in the mod's folder. `/mod_enable chatcomplete` turns it on (the mod ships disabled).
- The list is drawn **above the input** and aligned with it: `suggest+0x150` is X, so it
  is set to `edit+0x150`; the height goes to `+0x26c`. This fixes the left/right drift
  (the client was writing the height into X).
- The window shows `min(12, matches)` rows — no empty or filler rows.
- Command and argument catalogs are generated from `gm-commands.dll` and
  `data/client.db` by `tools/export_args.py` (102 commands) when the mod is not
  installed; with the mod they come from the running Gateway.
- `ChatComplete.args.txt` lines for commands the server does not declare are filled
  from a small fallback map (`spawn_mob`, `statue`, `kill`, `item`, `gem`, `goto`,
  `tp`, `npc`, `npc_place`, `npc_face`, `quest_flag`, `set_stat`, `stat`).

### Fixed
- Allocator corruption from calling `Suggest_SelectRow` while populating.
- Crash in `WStringVector_Insert+0xd2` from an unconditional vector grow.
- Crash in `UI_ApplyFlagState` (`0x1418b0a79`) when hiding the list during chat close:
  clearing no longer uses `Chat_ClearSuggestions`; the parent is validated (SEH) and the
  list is only touched while `chat+0x498` is set.
- Enter swallowed by a visible/empty list: every clear also clears the `0x800` flag.
- Holding an arrow did nothing: auto-repeat (`event+8 = 1`) is handled.
- Wrong text read through the vtable getter (returns `std::wstring` by value).

## Earlier (kept for history)

- Frida probe for the ChatBox, input text and submitted lines.
- Tab-only completion DLLs (`ChatComplete5/7`) using `WM_CHAR`.
- Win32 overlay experiments (`ChatComplete11/12`): proven, then rejected — an extra
  window is not acceptable.

