# Porting ChatComplete to a new client build

Everything the DLL needs is resolved at load time from byte signatures, so a client
update usually means **regenerating signatures**, not editing code.

```
Client 3.29.3c · image base 0x140000000 · signatures: signatures.json
Generator / scanner: tools/gen_signatures.py
Generated header:    native/Signatures.generated.h
Human-readable list: SIGNATURES.md (full patterns)
```

## 1. TL;DR

```powershell
# 1. point CLIENT in tools/gen_signatures.py at the new exe, then:
python tools\gen_signatures.py gen

# 2. check the new exe without launching it (prints the new RVAs and deltas):
python tools\gen_signatures.py scan <path\to\new\Game.exe>
#    (or validate the configured CLIENT path:)
python tools\gen_signatures.py selfcheck

# 3. rebuild and inject:
cmake --build native\build --config Release --target nativesuggest
python inject.py --dll native\build\Release\NativeSuggest.dll
Get-Content "$env:TEMP\NativeSuggest.log" -Tail 30
```

At load the DLL prints one line per function:

```
sig Chat_RebuildSuggestions = 0x7ff6cd1c1010
sig Chat_HandleKey = 0x7ff6cd1c0a90
...
```

Good: no suffix. Problems:

| Log text | Meaning |
|---|---|
| `(shifted)` | resolved but not at the stored 3.29.3c RVA — expected after a client update |
| `sig not found: X` | pattern gone; recover it manually (section 4) |
| `api fallback to 3.29.3c RVA for X` | signature failed and the old address is used — **hooks are probably wrong, do not type in chat** |

## 2. How resolution works

1. **Fast path** — the stored 3.29.3c RVA is checked against the pattern. If it still
   matches, it is used and no scan happens (zero cost on the same build).
2. **Scan** — otherwise the whole `.text` section is searched for the masked pattern.
   First unique match wins.
3. **Fallback** — if nothing matches, the 3.29.3c RVA is used and a warning is logged.

Signature format: hex bytes with `??` for masked bytes, e.g.

```
48 8B C4 48 89 58 10 48 89 48 08 55 56 57 41 54 41 55 41 56 41 57 ...
```

Masked bytes are relocatable or address-dependent:
- `rel32`/`rel8` of `call`, `jmp`, `jcc`;
- RIP-relative `disp32`;
- absolute immediates that look like addresses (`>= 0x10000000`).

The generator (capstone) masks them automatically and extends the pattern until it is
unique in `.text` (24–168 bytes in the current set).

## 3. Function table (3.29.3c)

| Name in Ghidra | RVA | Used for |
|---|---|---|
| `Chat_RebuildSuggestions` | `0x140181010` | hook: rebuild the list on text change |
| `Chat_HandleKey` | `0x140180a90` | hook: Tab, Up/Down preview, Enter-send |
| `Chat_ClearSuggestions` | `0x1401808d0` | clear + **hide** list (its hide path can crash; unused) |
| `Suggest_ClearAll` | `0x141903830` | clear entries + items without hiding |
| `UI_GetFactory` | `0x1418b6d20` | UI factory singleton |
| `UI_GetPoolBySize` | `0x1418b6a00` | pool for `0x6b8` items |
| `SuggestItem_Ctor` | `0x1418c24a0` | item constructor |
| `WStringVector_Insert` | `0x1401b1bd0` | grow the `chat+0x408` string vector |
| `Suggest_AddItem` | `0x141904470` | add a row |
| `Suggest_SetVisible` | `0x1418b0a30` | set `suggest+0x1e8` bit `0x800` |
| `WString_CopyCtor` | `0x140130840` | client-allocator `std::wstring` copy |
| `WString_Dtor` | `0x140134500` | `std::wstring` destructor |
| `Suggest_SelectRow` | `0x141905080` | native selection — **do not call while populating** |
| `Suggest_ApplySelected` | `0x1401805c0` | native apply — not used for our rows |
| `SuggestItem_SetText` | `0x1418c2c10` | moves text into the item |
| `SuggestItem_Attach` | `0x1418b5370` | attach item to the list |
| `UI_PoolAlloc` | `0x1418ba930` | pool allocator `(pool, size)` |
| `UI_PoolFree` | `0x1418ba820` | pool free `(pool, item)` |
| `SuggestItem_Delete` | `0x1405c8ad0` | deleting destructor (generic free!) |
| `UI_AddChild` | `0x1418b5b10` | parent/child list |

`NativeSuggest.cpp` resolves `Chat_RebuildSuggestions`, `Chat_HandleKey`,
`Suggest_ClearAll`, `UI_GetFactory`, `UI_GetPoolBySize`, `SuggestItem_Ctor`,
`WStringVector_Insert`, `Suggest_AddItem`, `Suggest_SetVisible`, `WString_CopyCtor` and
`WString_Dtor`. The rest are documented so a future feature can be added without
redoing the RE.

## 4. If a signature fails — manual recovery

Work in this order. Each method gives the address, then you regenerate signatures.

### 4.1. Find `Chat_RebuildSuggestions` (the anchor)

It is called on every chat text change and is the function that contains the item
creation block. Anchor it by one of:

- **Call from the key handler**: `Chat_HandleKey` (find that first, 4.2) calls
  `Chat_RebuildSuggestions` indirectly after text updates; in Ghidra use
  `Search → For Direct References` on the item constructor `SuggestItem_Ctor`.
- **String vector write**: find code that references `chat+0x408` / `chat+0x410`
  (`mov [rcx+0x410], ...` with `add ..., 0x20` nearby).
- **The item creation block** is distinctive in the decompiler:
  `FUN_1418b6d20` → `FUN_1418b6a00(..., 0x6b8)` → vtable `+8` → ctor → `+8 = 0x6b8`.

### 4.2. Find `Chat_HandleKey`

- It is the function whose 3rd arg is a key event and which does
  `if (*(int*)(event+4) == 9/0xd/0x20)`, `0x26`, `0x28`, `0x1b`.
- It reads `chat+0x420` and `chat+0x498` at the top and calls `Suggest_SelectRow`.
- In the probe, the key handler is reached from the listener dispatch
  `call [rax+0x18]` (see FINDINGS.md); the return address identifies it.

### 4.3. Find the constructor / item methods

- `SuggestItem_Ctor` writes the item vtable (3.29.3c: `0x1434ae370`). Find the vtable
  by `Search → For Direct References` on `0x1434ae370`; the constructor is the
  function that writes it at offset 0.
- From the vtable, without any signature:
  - `vtable+0x40` = deleting destructor (`SuggestItem_Delete`);
  - `vtable+0x138` = attach (`SuggestItem_Attach`);
  - `vtable+0x2a8` = height update.
- The text setter is reached through the sub-object at `item+0x298`:
  `*(item+0x298)` is its vtable, `vtable+0x10` is the setter.

### 4.4. Find the pool / factory

- The builder calls `UI_GetFactory()` (thread-local factory pointer) and
  `UI_GetPoolBySize(factory, 0x6b8)`.
- `UI_GetPoolBySize` is a small dispatcher on the size (`0x298`, `0x6b8`, `0x280`, …);
  the `0x6b8` case returns `factory+0x50`.
- The pool allocator is `*(pool) + 8`; free is `*(pool) + 0x10`. Signatures are optional
  here if you can read the vtable.

### 4.5. Find the string helpers

- `WString_CopyCtor` is the function that allocates with `0x140144e30` and copies
  `size*2+2` bytes; `WString_Dtor` checks `capacity >= 8` and frees.
- `WStringVector_Insert` is a `vector<std::wstring>` insert; it is a template
  instantiation and may collide with other instantiations, hence the 168-byte
  signature. If it breaks, anchor it from its call site inside the builder
  (`call` right after `chat+0x410 == chat+0x418`).

## 5. Structure offsets (3.29.3c) and how to verify

All offsets are into the objects below; verify them with `probe.js` RPCs
(`suggest_info`, `dump`, `state`) before trusting a build.

### ChatBox (`chat`)

| Offset | Meaning | Verify |
|---|---|---|
| `+0x1e8` | suggestion flags; bit `0x800` = list visible | `suggest_info().visible800` |
| `+0x150` | suggestion list **X** (float); `edit+0x150` is the input's X | screenshots |
| `+0x154` | suggestion list Y (float); `edit+0x154` is the input's Y | screenshots |
| `+0x26c` | suggestion list height (float), `rows × 32` | `suggest_info()` |
| `+0x3f0` | edit control pointer | `state().edit` |
| `+0x400` | suggestion list object | `suggest_info().suggest` |
| `+0x408` | string vector begin | `suggest_info().strings` |
| `+0x410` | string vector end | `strings = (end-begin)/0x20` |
| `+0x418` | string vector capacity | must be `>= end` |
| `+0x420` | chat controller | non-null when in game |
| `+0x488` | parent list object (`+0xb8` is its child vector) | `suggest_info().parent488` |
| `+0x490` | selected row index | `suggest_info().sel490` |
| `+0x498` | selected row visible (bool) | `suggest_info().vis498` |
| `+0x4a8` | suggestion entries begin (`0x18` per row) | `suggest_info().count` |
| `+0x4b0` | suggestion entries end | |
| `+0x4b8` | suggestion entries capacity | |

### Edit control (`chat+0x3f0`)

| Offset | Meaning | Verify |
|---|---|---|
| `+0x298` | text sub-object (vtable; `+0x48` holds the text) | `state().text` |
| `+0x338` | raw `std::wstring` of the input: data `+0`, size `+0x10`, cap `+0x18` | `state().text` |
| `+0x6e0` | selection start (caret anchor) | read after typing |
| `+0x6e8` | selection end / caret | read after typing |

**Do not call the `+0x298` getter with `(sub, &out)`.** It returns `std::wstring` by
value, so the hidden return pointer is the first argument (RCX) and `this` is RDX.
Reading `edit+0x338` directly is simpler and is what the probe and the DLL use.

### Suggestion item (`0x6b8`)

| Offset | Meaning |
|---|---|
| `+0x8` | allocation size (`0x6b8`), needed by the pool free path |
| `+0x5c` | element reference (parent/guard) |
| `+0x148` | parent marker compared by `UI_AddChild` |
| `+0x154` | row height (float) |
| `+0x1e8` | flags; bit 0 = owned/in-list |
| `+0x220` | init sub-object; `vtable+0x10(.,1)` runs after the ctor |
| `+0x288` | row background colour (BGRA bytes); the client sets dark red on selection |
| `+0x298` | text sub-object; setter is `vtable+0x10`, text payload at `+0x48` (**item+0x338**) |

### Vtable slots used

| Object | Slot | Function |
|---|---|---|
| pool | `+8` | alloc `(pool, size)` |
| pool | `+0x10` | free `(pool, item)` |
| item | `+0x40` | deleting destructor |
| item | `+0x138` | attach to list |
| item | `+0x2a8` | height changed |
| text sub-object | `+0x10` | move-setter |

The **edit's** text label at `edit+0x298` is a different class (3.29.3c vtable
`0x1430ed368` vs `0x14307458` for the edit). Slot `+0x10` is the setter in both, but
the edit's setter notifies the text-change handler. Around a programmatic input write
(live preview) the rebuild must be suppressed, or the list closes.

### Live preview / highlight

- Preview: build a client `std::wstring` with `WString_CopyCtor`, then call
  `(edit+0x298)->vtable[+0x10](label, &ws)` with the rebuild suppressed.
- Caret: the edit keeps the selection start/end at `edit+0x6e0`/`+0x6e8`. Set both to
  the text length after a programmatic write to collapse the caret at the end
  (`Chat_InsertTextView` also clamps these fields around insertions).
- Accept (Tab): write the row through the same path, with the caret collapsed at the end
  (no trailing space).
- Enter: with the list visible the client consumes Enter to "apply the suggestion" and
  never sends. The hook clears/hides our list on the first Enter (repeat flag 0) and
  falls through, so the client takes its normal send path and one Enter sends the line.
- Every clear must also clear the visible flag `suggest+0x1e8 & 0x800` (Tab, empty
  result, chat close). A visible-but-empty list still makes the client consume Enter as
  "apply", so the command is never sent. The 50 ms watchdog also clears a stale flag.
- Highlight: write the BGRA colour to `item+0x288` (yellow `0xFFFFC800`); clear it
  with `0` on every other row. The client overwrites it on its own selection, so paint
  after calling the original key handler.
- Scrolling: keep the full match list in `g_all` and show `min(12, matches)` rows from
  `g_offset`; no empty or filler rows, so a short list is a short window. **Handle
  Up/Down on every call, including auto-repeat** (`event+8 = 1`); requiring
  `event+8 == 0` makes holding the arrow do nothing. Up/Down are handled by us (the
  original key handler is not called); shift `g_offset` when the selection leaves the
  window and `RebuildWindow`. Keep `suggest+0x490` = window-relative index and
  `+0x498` = 1. The height may vary because X is pinned separately (see Position below).
- Clearing: use `Suggest_ClearAll` and destroy the `chat+0x408` strings yourself. Do
  **not** call `Chat_ClearSuggestions` while the chat may close: its hide path notifies
  `suggest+0x148` (the parent), which can already be destroyed and crashes in
  `UI_ApplyFlagState` (`suggest+0x148 → +0x208`). Guard every list operation with
  `chat+0x498` (chat visible) and validate the parent before `Suggest_SetVisible`.
- Position: **`suggest+0x150` is X, `+0x154` is Y, `+0x26c` is height.** The client
  writes the height into X, so the list drifts right as it grows; set
  `+0x150 = edit+0x150` (input X), `+0x154 = edit+0x154 - height - 2`,
  `+0x26c = height` (item row height `+0x154`, 33.0). `edit+0x150/+0x154` are the
  input's X/Y in the same coordinate space.

### Command and argument catalogs

- `%TEMP%\ChatComplete.commands.txt`: command names.
- `%TEMP%\ChatComplete.args.txt`: `<command> <kind>[,<kind>...]` by position.
- `%TEMP%\ChatComplete.kind.<kind>.txt`: `value<TAB>detail`.
- `tools/export_args.py` builds all three from the gm-commands DLL usages and
  `data/client.db` (WorldAreas, MonsterVarieties, BaseItemTypes, SkillGems, Stats,
  NPCs, QuestFlags). Kinds match the Gateway's `CompletionValues`.

## 6. Validation checklist after a port

1. Load the DLL; the log lists every signature with no `(shifted)` and no
   `sig not found` / `fallback`.
2. Open chat, type `/` → the client's own list shows up to 8 commands.
3. Type `/he` → exactly `/heal`, `/help`.
4. Press Down → `suggest_info().vis498 == 1` (selection flag).
5. Press Tab → input becomes `/heal` or `/help`; list closes.
6. Press Esc / clear the input; the list stays closed.
7. Play for a minute and open/close chat repeatedly — the client must stay responsive.

## 7. Pitfalls that must stay fixed

- **Never call `Suggest_SelectRow` (`FUN_141905080`) while building the list.** It adds
  the row as a child control; the later `delete` uses the generic allocator on pool
  memory → heap corruption (`0xc0000005`/`0xc0000094` in `Heap_SlabFree`).
- **Append strings through the client's branch only**: grow with
  `WStringVector_Insert` when `end == capacity`, otherwise `WString_CopyCtor(end, src);
  end += 0x20`. Unconditional grow corrupts the vector.
- **The text setter moves** the string and destroys the source. The source must own a
  **client-allocated** buffer (`WString_CopyCtor`), never a CRT `std::wstring` buffer.
- **Use the item's own text setter**, not the row's string entry, for display.
- Keep strict prefix matching and the 8-row cap; 32 rows overflow the chat panel.
- Handle only Tab; Up/Down/Esc are the client's own code and work with our rows.

## 8. Probe helpers (no rebuild needed)

`probe.js` exports used for verification:

| Export | Purpose |
|---|---|
| `use_chat(addr)` | attach the probe to a ChatBox |
| `state()` | chat/edit/text |
| `suggest_info()` | count, flags, selection, first rows |
| `dump()` | raw heads of chat/edit/suggest structures |
| `simulate_key(vk)` | call `Chat_HandleKey` with a synthetic event |
| `force_text(s)` | set the edit text through the client setter |

Typical port check (no game restart):

```powershell
python probe.py --post --seconds 2
```
