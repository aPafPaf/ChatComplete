# Argument coverage audit

ChatComplete fills its suggestions from the live command table: each command's `usage`
is parsed into states, and catalog values come from `client.db`, the Gateway or the
client's own catalogues. The audit answers two questions:

- does every command argument have a suggestion, or is it free text by design?
- where does the client data already hold values that the mod does not use yet?

## When to run it

- before a release;
- after adding or removing commands (framework, module or mod);
- after a client or game-data update (`client.db`, extracted data);
- after changing the usage parser, a catalog or a post-value state.

Run `/ccc` in game first (or enter an area) so the `%TEMP%` files are current.

## Files it reads

| File | Content |
|---|---|
| `%TEMP%\ChatComplete.usage.txt` | every command's live usage |
| `%TEMP%\ChatComplete.states.txt` | `<command>`, literal prefix, next tokens (`@kind` = catalog) |
| `%TEMP%\ChatComplete.args.txt` | declared catalog kinds by command |
| `%TEMP%\ChatComplete.kind.<kind>.txt` | catalog values (`value<TAB>detail`) |

It also reads `LocalExile/data/client.db` (or `--db`) for the "data already exists"
hints.

## Running

```powershell
python tools/coverage.py            # summary
python tools/coverage.py --all      # every command
python tools/coverage.py --strict   # exit 1 on a gap or a broken kind file
python tools/coverage.py --json out/coverage.json
```

## Reading the report

- **High-priority gaps** — the command has no catalog but a matching client table
  exists. These are the cheap wins (this is how `/ascendancy`, `/tattoo` and
  `/add_passive` were found).
- **Commands with arguments but no suggestions** — review for missed finite choices;
  many are numeric or free text.
- **Partial coverage** — a catalog exists but later arguments are still free.
- **Post-value transitions** — arguments after a catalog value or a runtime word
  (`@kind` / `*` prefixes); check that the command's tail is listed.
- **Broken catalog files** — a state or `args.txt` names a `@kind` with no non-empty
  `kind.<kind>.txt`. This is always a bug.

## Fixing a gap

1. **Static client data** — add a helper to `chatcomplete.cs`, a kind in `DumpValues`,
   and an override or post-value state in `UsageGrammar.cs`.
2. **Game/Gateway data** — use a public API (`CompletionValues`, `ClusterJewels`,
   `UniqueJewelCatalogue`, `Program.ServerMods`); avoid a LocalExile change unless the
   data is not reachable otherwise.
3. **After a value** — use a state prefix starting with `@kind` (matches an exact
   catalog value, including a multi-word one) or `*` (one runtime word or number).
   A prefix can mix literals and `*` (`cluster_jewel large * *`).
4. **Free text by design** — do not invent a catalog. Document it: custom monster
   name, damage, counts, levels, seeds, coordinates, JSON, runtime stash tabs.
5. Rebuild the DLL if native code changed, install the mod, run `/mod_reload` and
   `/ccc`, rerun the audit and test the input in the client with Tab.

`/mod_reload` only reloads the mod's code; the catalogs are rewritten by `/ccc` or an
area change. The DLL notices changed files on the next keystroke by itself.

## Release gate

- CMake builds `NativeSuggest.dll`;
- `tools/build-index.ps1 -Check` passes;
- `coverage.py --strict` exits 0 with no broken kind files;
- a live pass covers the main commands (`/`, `/spawn_mob`, `/item`, `/goto`,
  `/ascendancy`, `/cluster_jewel`, `/tattoo`, `/item_mod`, `/unique_jewel`);
- only then tag the release.

## Troubleshooting

- **`missing ...usage.txt`** — run `/ccc` in game.
- **A kind is missing/empty** — a command declares or references it, but `DumpValues`
  did not produce it (check the client table or API name).
- **Stale suggestions after a change** — `/mod_reload` reloads code, not catalogs;
  use `/ccc` or change area.
- **Audit false positive** — improve the hint tables in `coverage.py`; do not add a
  fake catalog to silence it.
