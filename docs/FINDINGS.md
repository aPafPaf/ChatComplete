# Chat completion — live findings (3.29.3c)

Probe: `probe.py` + `probe.js`. It uses the existing client-control Frida session
(`ClientControl`), attaches once, and loads `probe.js` beside the control script.

## Verified live (2026-11-21)

| What | Where |
|---|---|
| Chat input key handler | `FUN_140180a90(this, edit, keyEvent)` |
| Chat object (`this`) | captured from the handler; stable for the session (`0x590af526f70` in the live run) |
| Edit control | `*(chat + 0x3f0)` |
| Editable text | `std::wstring` at `edit + 0x338` (`*(edit + 0x298) + 0xa0`) |
| Finished line | `FUN_14017cdd0(chat, std::wstring* line)` — the probe sees `/hhee` |
| Send/click handler | `FUN_14017f020(chat)` |
| Insert text | `FUN_1418903f0(edit, position, string_view*)`, returns 1 on success |
| ChatBox object size | `0x6b8` (`FUN_1401aff50` allocates it); console variant `0x6c8` |
| UI layout loader | `FUN_1418b08d0(control, std::wstring* path)` |
| Find child by name | `FUN_1418b6420(root, std::wstring* name)` |
| Add child | `FUN_1418b5b10(parent, index, child)` (used by `FUN_140187fb0`) |
| HUD layout | `Metadata/UI/InGameState/HUD/HUD.ui` (chat is part of HUD) |
| Control constructors table | RVAs around `0x1447805b0` (ChatBox `0x178040`, console `0x1783e0`, …) |
| Native hook | inline hook at `0x140180a90` (15-byte prologue + trampoline), verified live; logs key/text |
| Handler dispatch | not a static vtable: the caller iterates a listener list and does `call [rax+0x18]` at `0x141992ae` |

The key event struct: `+4` is the key code (e.g. `0x48` for `h`, `0x1B` for Esc). The
handler only acts when `chat+0x420 != 0` and `chat+0x498 != 0`.

`FUN_1418903f0` inserts a `{wchar_t* ptr, size_t length}` view at a position; it updates
the client's own text buffer and cursor. It needs the chat input to be open and in the
right state. In the probe it returned `1`, but with the chat closed the text stayed empty.

## Gotchas found

- `PostMessage(WM_KEYDOWN)` reaches `FUN_140180a90`; sending both `WM_KEYDOWN` and
  `WM_CHAR` inserts each character twice (`/hhee`).
- `SendInput`/focus is unreliable while another window is foreground. The probe's
  `--keys`/`--post` modes need the game focused.
- A chat object captured once survives area changes in the live session; re-capture after
  a client restart.
- A queued `readOnFrame` RPC can leave the Python process waiting if the client stops
  producing frames. Add a timeout before using it in a tool.

## Next native step

**Solved 2026-11-21 — the native list works.** `NativeSuggest.dll` hooks
`FUN_140181010`, builds suggestion items with the client's own sequence, sets
`suggest+0x1e8 |= 0x800`, and handles Tab with `WM_CHAR`. Screenshots confirm the
client's own list renders and filters, and Tab completes `/a` → `/accuracy`.

Key facts for the working path:

- `FUN_140181010(chat)` clears via `FUN_1401808d0(chat)` and then builds rows.
- Item: `pool = FUN_1418b6a00(*FUN_1418b6d20(), 0x6b8)`;
  `mem = pool->vtbl[+8](pool, 0x6b8)`; `item = FUN_1418c24a0(mem)`;
  `item+8 = 0x6b8`; init `item+0x220`; attach `item->vtbl[+0x138](item, suggest+0x170)`.
- Text setter `item+0x298 -> vtbl[+0x10]` **moves** the string and destroys the source,
  so the source must be a client-allocator string (`FUN_140130840`).
- `FUN_141904470(suggest, count, item, stringIndex)` adds the row;
  `stringIndex = (chat+0x410 - chat+0x408)/0x20` before appending the string.
- Append strings like the client: grow with `FUN_1401b1bd0` only when
  `chat+0x410 == chat+0x418`, otherwise `FUN_140130840(end, src); end += 0x20`.
- **Never** call `FUN_141905080` during population: it makes the row a child control,
  and the deleting destructor `FUN_1405c8ad0(item, 1)` frees pool memory with the generic
  allocator → heap corruption.
- Key handler `FUN_140180a90`: Up/Down/Esc are native and safe; Tab's apply
  `FUN_1401805c0` silently fails for our rows, so Tab is completed with `WM_CHAR`.
- The edit text is the raw `std::wstring` at `edit+0x338` (size `+0x10`, cap `+0x18`);
  its vtable getter returns by value and has the hidden return pointer first.

The remaining work is polish (argument hints) and a 3.29.3d port.

## Live selection: colour and preview (2026-11-21)

- `item+0x288` is the row **background colour**, 4 bytes in **BGRA** order. The client
  writes `0a 28 28 fa` (dark red) for the selected row. Writing `00 C8 FF FF`
  (`0xFFFFC800`) gives a visible yellow row.
- Selected index/flag live in the suggestion object at `+0x490`/`+0x498`; the client
  updates them itself in `Chat_HandleKey` on Up/Down.
- The edit's text label is at `edit+0x298`. Its class is **not** the suggestion item's
  label class (3.29.3c vtables `0x14307458` vs `0x1430ed368`), but slot `+0x10` is the
  setter in both. Calling it **notifies the text-change handler**, which runs
  `Chat_RebuildSuggestions` and closes the list; suppress that one call while writing
  a preview.
- `WString_CopyCtor` (`FUN_140130840`) short-string path copies the inline buffer of the
  source struct (size < 8), so a fake source must place the characters inline, not at a
  pointer. Long strings use `*source` as the data pointer.
- Argument catalogs: `%TEMP%\ChatComplete.kind.<kind>.txt` (`value<TAB>detail`), with the
  per-command kind map in `%TEMP%\ChatComplete.args.txt`; `tools/export_args.py` builds
  them from `data/client.db` (monsters = `MonsterVarieties`, 10,691 ids, and the other
  Gateway `CompletionValues` kinds).
- The edit selection/caret is at `edit+0x6e0` (start) and `edit+0x6e8` (end).
  `Chat_InsertTextView` clamps both around insertions. Setting both to the text length
  collapses the caret at the end after a programmatic text write. Tab does **not** add
  a trailing space; the caret simply sits at the end.
- The suggestion list element's position: `+0x150` = **X**, `+0x154` = **Y**,
  `+0x26c` = height, `+0x268` = content width. The client itself writes the list height
  into `+0x150`, which is why the list drifted right as it grew. Setting
  `+0x150 = edit+0x150` (the input's X) pins it; the height goes in `+0x26c`.
- The list can be scrolled by keeping the full match array in the DLL and materialising
  only the visible 12-row window; Up/Down are handled in `Chat_HandleKey` without
  calling the original handler.
- The list height is `min(12, matches) × row height`; there are no empty/filler rows.
  X is pinned to the input's X (`+0x150 = edit+0x150`), so a short list keeps the same
  left edge as a full one.
- A fully typed command is included in the suggestions (matching uses `>=`), so `/god`
  shows `/god`.
- The suggestion list is drawn outside the chat panel, so closing the chat does not hide
  it. A 50 ms watchdog on the game window (WndProc subclass + `WM_TIMER`) hides it when
  `chat+0x498` becomes 0.
- Up/Down auto-repeat arrives with `event+8 = 1`; handle every call, not only
  `event+8 == 0`, or holding the arrow does nothing.
- Enter with a visible list is consumed by the client to "apply the suggestion" (a no-op
  for our rows), so the message is never sent. Clear/hide the list on the first Enter
  and fall through to the client's send path. A **visible but empty** list also eats
  Enter, so every clear must also clear `suggest+0x1e8 & 0x800` (Tab included); a 50 ms
  watchdog clears a stale flag.
- **Never call `Chat_ClearSuggestions` while the chat may be closing.** It hides the
  list and the hide path dereferences the parent at `suggest+0x148` (`+0x208`); that
  parent can already be freed, which crashed in `UI_ApplyFlagState` at
  `0x1418b0a79` (`mov rax,[rcx+0x208]`). Clear with `Suggest_ClearAll` and destroy the
  `chat+0x408` strings instead, and only touch the list while `chat+0x498` is set.
