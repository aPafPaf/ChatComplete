# Changelog

All dates are 2026-11-21 unless noted. Client build: 3.29.3c.

## 0.4.1 - 2026-11-28

### Fixed
- The release declares LocalExile **0.6.1-beta** as its minimum. 0.4.0 said 0.6.0-beta, but the `/entity`
  code needs LocalExile#1354 (`MonsterActions`), which shipped in 0.6.1-beta.

## 0.4.0 - 2026-11-28

### Added
- `/entity` catalogs: the ids of the area's living monsters (`ChatComplete.kind.entity`) and each actor's own skill names
  (`entity_skill`), refreshed on the area tick; after `/entity <id>` the verbs are suggested, including `kill`.
  The DLL prefers the per-actor `entity_skill.0x<id>` catalog, so `/entity <id> skill ` lists that actor's skills only.

### Changed
- Ported to the 0.6.x LocalExile API: `Player` (was `ClientState`), `GameEvents.AreaTick`, `player.Area.Monsters`,
  `player.Character.ClassRow`, and the Game module's `MonsterActions.Names` (LocalExile#1354).
- `UsageGrammar`: the `/entity` verb list is `skills|skill|kill|life|anim|list` (the removed `auto` is gone).
- Needs LocalExile#1354 (`MonsterActions`), shipped in 0.6.1-beta; a stock 0.6.0-beta cannot load this version.

## NativeSuggest — current

### Added
- Contextual argument suggestions from each command's usage string: subcommands,
  enums and flags, through a small state machine (`ChatComplete.states.txt`).
  50 commands / 265 states; `/tp` keeps its area catalog, `/kill` shows its keywords
  before the monster catalog, and `charges`/`item_mod`/`descendancy` have explicit
  overrides. `ChatComplete.usage.txt` keeps the live usage table for inspection.
- Catalog detail is searchable: area suggestions show `Name (Id)` and match a typed
  name (`/goto the twi`), and other kinds search their detail as well as their value.
- Small numeric sets and ranges (`[created 0|1]`, `[type 1-4]`, `[model 0-2]`) are
  suggested as numbers.
- Framework-owned catalogs `mod` and `module`: `/mod_enable`, `/mod_disable` and
  `/mod_reload` suggest mod ids, `/module_reload` suggests modules. The mod reaches
  them by reflection and falls back to the game module's `CompletionValues` on a
  released Gateway that does not have the LocalExile change yet.
- Optional support for a `CommandGrammar` declared by the Gateway (forms of keywords,
  choices, catalogs and numbers): the mod prefers it and parses usage when there is
  none. The stock Gateway has no such declarations, so this release simply falls back
  to usage; no LocalExile change is required.
- Ascendancy names by class: `/ascendancy normal ` suggests `Name`, `/ascendancy phrecia `
  suggests `LeagueName` from `client.db`'s `Ascendancy` table, filtered by `conn.ClassRow`.
  This is mod-side only; the stock Gateway already has the table.
- `tools/coverage.py`: audits every command's arguments against the generated states and
  kind files, and points out likely gaps where `client.db` already has a matching table
  (`ascendancy`, `passive`, `tattoo`, `mod`, `atlas`, ...). `--strict` fails on broken
  kind files or likely gaps; `--json` writes the full report.
- Static catalogs from `client.db`: `/add_passive` (`PassiveSkills.PassiveSkillGraphId` +
  name), `/atlas_complete` (`AtlasNode.Id` + `WorldAreas` name), `/quest_preset`
  (`CharacterStartQuestState.Id`), `/tattoo` and `/tattoo_apply` (tattoo item names),
  `/unique_jewel` (`UniqueJewelCatalogue.Names`), `/item_mod` (item-domain `Mods`),
  `/item_tab` (standard stash tabs), `/cluster_jewel` (`ClusterJewels.Themes` by size),
  `/mob_mods` (monster affixes), and `/mod_*`/`/module_reload` (installed mods/modules).
- Arguments after a catalog value: states can start with `@kind` (an exact catalog value)
  or `*` (a runtime word). `/spawn_mob Sand Spitter ` now offers `friendly` and the four
  rarities, `/tattoo_apply <name> ` the passive nodes, `/item Mageblood ` the rarity/`max`
  flags, and `/mob_mods 0x1 ` the monster affixes. The repeatable audit workflow is in
  [docs/AUDIT.md](docs/AUDIT.md). Wildcard states (`*`) also cover
  arguments after numbers: `/hit fire 100 ` -> `ranged`/`freeze`,
  `/cluster_jewel large 8 2 ` -> the themes, `/item Mageblood 1 ` -> rarity/`max`.
  The custom name after `/spawn_mob ... rare` is free text by design. The audit is at
  0 gaps, no deferred list, and `--strict` passes.
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

