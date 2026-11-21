# Client UI notes (3.29.3c)

What was learned about the LocalExile/Path of Exile UI while building the native chat
suggestions, and the conclusions that follow. Function names match the renamed
`poe1 3.29.rep` project.

## 1. What the UI is

The client UI is plain C++ controls plus `.ui` layouts (`Metadata/UI/...`). There is no
retained-mode widget toolkit:

- every element is a C++ object with a vtable, created through a factory and returned
  from size-class pools;
- elements own child lists and reference-counted sub-objects;
- layout is computed by the element itself (position/size floats) and notifications go
  to the parent (`+0x148`) and the owner (`+0x1e0`);
- text is a custom "text assign" object with an MSVC `std::wstring` inside.

Key helpers:

| Function | Role |
|---|---|
| `UI_GetFactory` | thread-local UI factory |
| `UI_GetPoolBySize` | size class → pool (`0x6b8`, `0x298`, …) |
| `UI_PoolAlloc` / `UI_PoolFree` | slab allocator (`pool, size` / `pool, item`) |
| `UIElement_BaseInit` / `UIElement_Destroy` | element lifetime |
| `UI_AddChild` / `UI_RemoveChild` / `UI_ChildList_Erase` | child lists |
| `UI_SetParent` / `UI_ApplyFlagState` | parent link and visibility/flag propagation |
| `UI_PlaceChild` / `UI_UpdateDesiredSize` | layout |
| `UI_TextAssign` | text into a label |

## 2. Two levels of vtables

An element may embed sub-objects with their own vtables. In a suggestion item:

- `item+0` — item vtable (`0x1434ae370` in 3.29.3c): `+0x40` deleting destructor,
  `+0x138` attach-to-list, `+0x2a8` height changed;
- `item+0x220` — layout/init sub-object (`vtable+0x10(.,1)` after the ctor);
- `item+0x298` — text label (`0x1430ed368`): `+8` getter, `+0x10` setter, `+0x40`
  visible, and more small flag methods.

The **edit's** label at `edit+0x298` is a *different* class (vtable `0x14307458`), but
slot `+0x10` is a setter in both. Calling it notifies the text-change handler.

## 3. Coordinates

Positions are floats in UI units; on a 1788×942 window the screen is roughly
`0.62 × UI + offset`, so pixel values cannot be copied between resolutions.

| Field | Meaning |
|---|---|
| element `+0x150` | **X** |
| element `+0x154` | **Y** |
| suggestion list `+0x26c` | height (`rows × 32`) |
| suggestion list `+0x268` | content width |
| item `+0x154` | row height (33.0) |
| edit `+0x150` / `+0x154` | input X / Y |
| edit `+0x6e0` / `+0x6e8` | selection start / end (caret) |

**Quirk:** the client writes the suggestion list's **height** into its **X** (`+0x150`),
so a 3-row list sits at X≈99 and a 12-row list at X≈396. That is why the list drifted
right as it grew. Pin `suggest+0x150 = edit+0x150` and put the height in `+0x26c`.

The list is **not clipped by the chat panel**: it is drawn above/below the panel and
must be hidden explicitly when the chat closes.

## 4. Text

MSVC `std::wstring` layout: inline buffer of 8 `wchar_t` at `+0`, size at `+0x10`,
capacity at `+0x18`. If capacity > 7 the first qword is a pointer.

- `WString_CopyCtor` (`FUN_140130840`): for size < 8 it copies the **inline buffer of the
  struct**; for size ≥ 8 it copies from `*source`. A fake source must follow this.
- `SuggestItem_SetText` / the label setter **move** the buffer into the element and then
  destroy the source. The source must own a **client-allocated** buffer, or the client
  allocator frees CRT memory.
- The label's getter (`vtable+8`) returns `std::wstring` **by value**: the hidden return
  pointer is the first argument (RCX), `this` is RDX. The decompiler shows them swapped.
- A suggestion item's text lives at `item+0x338` (inside the label's assign object at
  `item+0x2e0`).
- `Text_Measure` and `Text_AcquireLayout` provide line metrics for layout.

## 5. Visibility, selection and colours

- Visibility flag: `element+0x1e8` bit `0x800`; `Suggest_SetVisible` toggles it and
  notifies the layout/parent.
- **Hiding is dangerous during teardown:** `UI_ApplyFlagState` dereferences
  `element+0x148 → parent+0x208`. During chat close that parent can already be freed
  (crash at `0x1418b0a79`). Validate the parent (SEH probe) or clear the flag manually
  and let the client tear down.
- Selection: `suggest+0x490` (row index) and `suggest+0x498` (selected visible, bool).
  The native apply uses the entry's string index (`entry+8`) into `chat+0x408`.
- Row background colour: `item+0x288`, 4 bytes **BGRA**. The client writes a dark red
  `0a 28 28 fa` for the selected row; yellow is `0xFFFFC800`.
- The list's own background box is drawn from the element height (`+0x26c`), so
  reserving rows shows as a big empty box.

## 6. Events

- The key handler is called through a listener list (`call [rax+0x18]` at
  `0x141992ae`), not through a static vtable.
- Event struct: `+0` type, `+4` virtual key, `+8` **repeat flag** (1 for auto-repeat),
  more fields after.
- With a suggestion list visible, the client consumes `Tab`, `Enter` and `Space` to
  "apply the suggestion"; for our rows the native apply is a no-op, so Enter would never
  send. Hide the list on Enter first, then let the client send.
- A **visible but empty** list behaves the same: it eats Enter. Always clear the `0x800`
  flag when clearing.
- Up/Down are single keydowns plus repeats; handle every call.
- Rows bind their own events (`UI_BindEvent` on `item+0x9c`); a mouse click calls the
  native apply, which does not work for hand-built rows.

## 7. Lifetime and allocation

- The UI slab pool allocates 7 MB blocks (`0x6d0020`) of `0x6d0`-byte slots; the returned
  pointer is `slot+0x10`, usable `0x6b8`. A freed slot is recycled without zeroing it.
- A suggestion item must have `item+8 = 0x6b8` (the size the pool free expects).
- `SuggestItem_Delete` (`vtable+0x40`) uses the **generic** free when bit 0 of
  `item+0x1e8` is set; on pool memory that corrupts the heap. The pool free is
  `UI_PoolFree`.
- `Suggest_ClearAll` removes entries and child rows but does not hide. `Chat_ClearSuggestions`
  also hides (risky during close) and destroys `chat+0x408` strings.
- Chat object: `chat+0x3f0` edit, `chat+0x400` suggestion list, `chat+0x408/410/418`
  string vector, `chat+0x498` chat visible.

## 8. Conclusions / rules

1. Build rows with the client's exact creation sequence; do not invent flags.
2. Never call `Suggest_SelectRow` while populating — it registers the row as a child and
   the later destroy uses the wrong allocator.
3. Clear with `Suggest_ClearAll` + destroy the strings; clear `0x800` on every clear.
4. Pin the list's X yourself; never trust the client's X (it is the height).
5. Use client allocations for any string the client will own or free.
6. Hide the list when the chat closes (watchdog); the list is outside the panel.
7. Hand Enter to the client after hiding the list; consume only Tab/Up/Down.
8. Handle auto-repeat events (`event+8 = 1`).
9. Keep list operations on the game thread only.

## 9. Open questions

- Per-label text colour: only the row background (`item+0x288`) was found; the text
  colour is not a simple field (the client draws selected rows with a background).
- The anchor/alignment that makes the client write height into X; a real anchor field
  was not located.
- Mouse-click acceptance for hand-built rows.
- Chat object lifecycle across channel switches and area changes.
