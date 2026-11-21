# ChatComplete — progress and history

Current state (2026-11-21, client 3.29.3c): **working**. The client's own suggestion
list is filled with commands and argument values, with live preview, yellow selection,
scrolling and Tab/Enter behaviour. See [README.md](../README.md) for usage and
[CHANGELOG.md](../CHANGELOG.md) for the change log. `/cc reload` re-reads the command and
argument catalogs in the running DLL (intercepted on Enter, never sent to the server).

The same tool can run as a LocalExile mod (`mods/chatcomplete/`): the Gateway injects the DLL into
every local client, writes the catalogs from the live command table and
`CompletionValues`, and exposes `/ccc`. The DLL also notices changed catalog files and
reloads on the next keystroke, so `/cc reload` is only a fallback.

## Timeline

1. **Frida probe** (`probe.js`, `probe.py`): captured the ChatBox from the key handler,
   read the input as an MSVC `std::wstring` at `edit+0x338` (size `+0x10`, capacity
   `+0x18`), read submitted lines in `Chat_ParseLine`.
2. **Tab-only DLLs** (`ChatComplete5/7`): inline hook on `Chat_HandleKey`, appended the
   missing suffix with `WM_CHAR`. `FUN_1418903f0` returns 1 but does not change the
   visible text, so `WM_CHAR` is the insertion path.
3. **Win32 overlay** (`ChatComplete11/12`): proven (58 entries) but rejected by the user
   — an extra window is not acceptable.
4. **Native list** (`NativeSuggest`): builds real `SuggestItem`s in the client's own
   list. Working, with the fixes below.

## Structure offsets (3.29.3c)

ChatBox (`chat`):

| Offset | Meaning |
|---|---|
| `+0x1e8` | suggestion flags; bit `0x800` = list visible |
| `+0x3f0` | edit control |
| `+0x400` | suggestion list object |
| `+0x408` / `+0x410` / `+0x418` | string vector begin / end / capacity (`0x20` per string) |
| `+0x488` | parent list object (`+0xb8` child vector) |
| `+0x490` / `+0x498` | selected row index / selected (bool) |
| `+0x4a8` / `+0x4b0` / `+0x4b8` | entries begin / end / capacity (`0x18` per row) |

Edit (`chat+0x3f0`): text `std::wstring` at `+0x338`; selection start/end at
`+0x6e0`/`+0x6e8`; text label at `+0x298` (setter `vtable+0x10`); X/Y at
`+0x150`/`+0x154`.

Suggestion list element: **X `+0x150`, Y `+0x154`, height `+0x26c`, content width
`+0x268`**. The client writes the list height into `+0x150` (its own quirk), which is
why the list drifted right as it grew; we pin `+0x150 = edit+0x150`.

Suggestion item (`0x6b8`): size `+0x8`, element ref `+0x5c`, parent marker `+0x148`,
height `+0x154`, flags `+0x1e8` (bit 0 = owned), init sub-object `+0x220`, text
sub-object `+0x298` (text at `+0x338`), row background colour `+0x288` (BGRA).

## Root causes found and fixed

- **Item creation** is exactly the client's `Chat_RebuildSuggestions` block:
  factory → pool `0x6b8` → alloc `(pool, 0x6b8)` → ctor → `+8 = 0x6b8` → init
  `+0x220` → attach `item->vtable[+0x138](item, suggest+0x170)` → text setter
  `(+0x298)->vtable[+0x10]` → `Suggest_AddItem(suggest, count, item, stringIndex)` →
  append the string to `chat+0x408` (grow only when `end == capacity`) → show.
- **`Suggest_SelectRow` (`FUN_141905080`) must not be called while populating**: it
  registers the row as a child control; the later `delete` uses the generic free on pool
  memory (`SuggestItem_Delete`), corrupting the allocator (`0xc0000005`/`0xc0000094` in
  `Heap_SlabFree`).
- **String append** must follow the client: grow with `WStringVector_Insert` only when
  `end == capacity`, else `WString_CopyCtor(end, src); end += 0x20`. An unconditional
  grow corrupted the vector and crashed in `WStringVector_Insert+0xd2`.
- **Text setter moves**: `SuggestItem_SetText` takes ownership and destroys the source,
  so the source must hold a client-allocated buffer (`WString_CopyCtor`). Short strings
  (< 8) live inline in the struct; the copy ctor copies that inline buffer.
- **Reading the input through the vtable getter is wrong**: it returns `std::wstring` by
  value, so the hidden return pointer is the first argument. Use `edit+0x338`.
- **`Chat_ClearSuggestions` while the chat closes** dereferences the list's parent
  (`suggest+0x148 → +0x208`), which may already be freed — crash in `UI_ApplyFlagState`
  at `0x1418b0a79`. Clear with `Suggest_ClearAll` + destroy the `chat+0x408` strings, and
  only touch the list while `chat+0x498` (chat visible) is set. A 50 ms watchdog
  (`WndProc` subclass + `WM_TIMER`) hides the list when the chat becomes invisible.
- **Enter with a visible list is consumed** by the client to "apply the suggestion" (a
  no-op for our rows), so nothing is sent. Hide the list on the first Enter and let the
  client send. The same happens with a **visible but empty** list, so every clear must
  also clear `0x800` (Tab included); the watchdog clears stale flags.
- **Auto-repeat** of Up/Down arrives with `event+8 = 1`; only handling `event+8 == 0`
  makes holding the arrow do nothing.
- **X drift**: `suggest+0x150` is X, and the client writes the height there. Setting
  `+0x150 = edit+0x150` pins the list to the input; the height goes to `+0x26c`.
- **Live preview** writing the input triggers the text-change handler; a
  `g_suppressRebuild` flag skips `Chat_RebuildSuggestions` for that write so the list
  stays open.
- **Yellow selection**: `item+0x288` is the row background (BGRA). Write `0xFFFFC800`
  for the selected row and `0` for the others.

## Artifacts

| File | Purpose |
|---|---|
| `native/NativeSuggest.cpp` | the DLL (hooks `Chat_RebuildSuggestions`, `Chat_HandleKey`) |
| `native/Signatures.generated.h` | signatures resolved at load |
| `tools/gen_signatures.py` | regenerate/validate signatures |
| `tools/export_args.py` | rebuild command/argument catalogs |
| `inject.py` | load the DLL |
| `probe.js` / `probe.py` | live probe and test helpers |
| `PORTING.md` | porting guide, offsets, recovery |
| `UI-NOTES.md` | client UI architecture, coordinates, text, events, conclusions |
| `SIGNATURES.md`, `signatures.json` | generated signature tables |
| `cap.ps1` | screenshot helper |

## Next

1. More completion kinds (uniques, cosmetics, passive ids) where the data exists.
2. Port to 3.29.3d — signatures resolve at load; see `PORTING.md`.

## Ghidra names

All functions used here are renamed in `poe1 3.29.rep` (`Chat_*`, `Suggest*`, `UI_*`,
`WString*`, `Heap_*`). If the names are gone, the project was not saved after renaming.

