# Per-preview label overlay forms

Date: 2026-08-13
Status: designed, not implemented

## Problem

When `LabelBackgroundTransparent` is enabled, preview labels are drawn by
`TriffViewLabelOverlayForm` — a single borderless layered form sized to the
entire virtual desktop. Every repaint covers the whole surface.

Measured on a 17920x3492 three-monitor desktop with 6 EVE clients:

- one repaint covers **62,576,640 px** and costs **~40,000 us** on the UI thread
- it fires on every topology or active-client change, so the stall lands while
  the user is switching clients
- one preview drag produced 35 invalidations coalescing into 4 repaints,
  roughly **160 ms** of blocked UI thread, which is the visible "label lags
  behind the mouse" behaviour

v1.6.3 already removed the repaints that drew unchanged content (44% of
`SetItems` calls painted, now 4.5% — a 9.5x reduction). The repaints that
remain are still ~40 ms each.

**Baseline.** This design is written against `fork/main` at v1.6.3, where
`SetItems` already skips repaints via `ItemsEqual`. It is not written against
`upstream/main`, which predates that fix and still invalidates unconditionally.
The implementation branch is based on `fork/main` for the same reason: the new
controller replaces the very method v1.6.3 changed, so building on
`upstream/main` would guarantee a conflict when merging back.

## Why the obvious fix does not work

Bounding the invalidation to only the changed rectangles was implemented and
measured at ~30x cheaper per paint. **It leaves visible ghost labels** wherever a
preview moved or a client closed.

Instrumentation established that the invalidation logic was correct — the right
rectangles were invalidated, the resulting paint clip matched them exactly, and
the item list held the right contents. The pixels simply never updated.

Five strategies were then tested on the target hardware, one preview drag each:

| mode | invalidates | paints | avg paint us | avg clip px | ghost |
|------|------------:|-------:|-------------:|------------:|-------|
| full (control) | 35 | 4 | 40,228 | 62,576,640 | no |
| bounded + `Update()` | 1070 | 14 | 3,371 | 4,696,511 | yes |
| bounded, `DoubleBuffered=false` | 598 | 14 | 4,900 | 4,626,493 | yes |
| `RedrawWindow` + `RDW_UPDATENOW` | 332 | 4 | 9,853 | 15,861,771 | yes |
| bounded + re-apply layered attrs | 670 | 15 | 3,215 | 4,337,210 | yes |

**Every bounded mode ghosts.** A partial repaint does not reach the composited
surface of a layered window (`WS_EX_LAYERED` plus a `TransparencyKey`).

Therefore the repaint must stay full-surface, and the only way to make it cheap
is to make the surface small.

## Why the form exists at all

`ReservedLabelHeight()` returns 0 when `LabelBackgroundTransparent` is true.
When it is false, the preview overlay reserves a 24-32 px band and draws the
label itself inside `DrawPreviewChrome`, cheaply (~499 us paints), with no
second window.

When it is true the DWM thumbnail fills the whole preview frame, and DWM
composites thumbnails **above** the preview overlay's own painting. So the label
must live on a separate topmost window. This is a real constraint, not an
accident, and it rules out folding labels back into the preview overlay.

Note `LabelBackgroundTransparent` defaults to **false**. This work benefits users
who have opted into transparent labels; everyone else is unaffected.

## Design

One small topmost layered form per preview, replacing the single
desktop-spanning form.

### Structure

A controller owns `Dictionary<nint, LabelForm>` keyed by **window handle**,
mirroring `_previews` (`Dictionary<nint, PreviewState>`). Handle is the natural
identity: collision-free, already the identity used for previews, and stable for
the lifetime of the client window.

**The handle must be added to the item contract.**
`TriffViewLabelOverlayItem` currently carries no handle
(`TriffViewSubsystem.cs:2927`), and `RefreshLabelOverlay` enumerates
`_previews.Values`, discarding the dictionary keys
(`TriffViewSubsystem.cs:2556`). The handle is nonetheless reachable without
restructuring: `PreviewState.Client` is an `EveClientWindow`
(`TriffViewSubsystem.cs:2874`) and `_previews` is keyed by `client.Handle`
(`TriffViewSubsystem.cs:2132`). So the change is to add a handle field to the
item record, populated from `state.Client.Handle`. Without this the
handle-keyed identity is not implementable.

Each form:

- is sized and positioned to **the union of its preview's frame and the label
  rectangle it draws**, in **screen** coordinates. Sizing to the frame alone is
  not safe: preview dimensions may be as small as
  `TriffViewPreviewDimensions.Minimum` (16 px,
  `TriffViewSubsystem.cs:16`), while label height is at least 18 px plus inset
  (`TriffViewSubsystem.cs:3056`), so on a small preview the label overflows its
  frame. The desktop-spanning form drew that overflow without issue because it
  owned the whole desktop; a frame-sized form would clip it. Note this
  contradicts the earlier assumption that the drawn label is always inside
  `item.Frame` — that assumption is false at minimum preview size.
- `item.Frame` is client-relative to the virtual desktop origin (built via
  `ToClientRect`), so screen position is `item.Frame` offset by the virtual
  desktop origin — the exact inverse of the transform the current single form
  relies on.
- copies today's window styles verbatim: `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE |
  WS_EX_TRANSPARENT | WS_EX_LAYERED` (+ `WS_EX_TOPMOST` when `AllowTopmost`),
  `TransparencyKey = BackColor = Color.FromArgb(1,2,3)`,
  `ShowWithoutActivation`, `WM_NCHITTEST` -> `HTTRANSPARENT`.
- has `Owner` set, as today, preserving owned-window z-order ordering.
- always repaints **in full**. Partial repaints are known not to work on a
  layered surface; nothing in this design may reintroduce them.

Drawing is the existing `DrawItem` logic, translated so the item's frame maps to
the form's own client area.

### Move versus repaint — the core rule

On each `SetItems`, for each item:

- **only `Frame.Location` differs, `Frame.Size` unchanged** -> reposition via
  `SetBounds` and **do not invalidate**
- **`Frame.Size` differs** -> resize and **fully invalidate**. A resize is not a
  move: `MouseMode.Resize` changes the frame's width and height
  (`TriffViewSubsystem.cs:2438`, clamped against
  `TriffViewPreviewDimensions.Minimum`), and the label's own geometry depends on
  those dimensions — its width is `Frame.Width - inset*2`, and `center` and
  `bottom` positions are computed from `Frame.Height`. Treating a resize as
  move-only would leave the label laid out for the previous size.
- **any other field differs** (Text, TextColor, FontSize, Position,
  BorderThickness) -> reposition if needed, then fully `Invalidate()` that one
  form
- **nothing differs** -> do nothing
- **no form for this handle** -> take one from the pool (or create), position it,
  fully invalidate
- **form whose handle is no longer present** -> hide and return to the pool

This is where the win comes from. A drag changes only `Frame.Location`, so it
produces window moves and zero repaints. Prototype measurement across one drag
plus 50 s of rapid client switching: **960 move-only operations, 0 repaints, 5
creations, 0 destructions.**

**Caveat on that measurement: the prototype never resized a preview.** All 960
move-only operations came from dragging. The move-only half of this rule is
measured; the resize half is reasoned from the code and is unverified. A resize
test on real hardware is required before this ships.

Label content does not change when switching clients — the active-state
highlight is a border drawn by the preview overlay, not part of the label — so
switching produces no label repaints at all.

### Lifecycle

Forms are **pooled, not destroyed**, when a client disappears: hidden and kept
for reuse. Client open/close churn is where this application has historically
accumulated state, and creating and destroying real Win32 windows on every cycle
invites both flicker and handle churn.

Measured standing cost of the prototype's five forms: **+12 GDI, +5 USER
handles**. Fixed and bounded, not a leak. Pooling keeps it that way.

### Integration

The controller **replaces** `TriffViewLabelOverlayForm` at its call sites
(`SetVirtualDesktop`, `AllowTopmost`, `ApplyTopmostPolicy`, `SetItems`,
`Owner`, `Dispose`). The desktop-spanning form class is **deleted**, not kept
behind a flag: leaving both paths in place would mean shipping a known-slow
implementation plus a switch nobody sets, and the slow path is exactly the one a
future contributor might resurrect.

The `TRIFFVIEW_LABEL_REPAINT_MODE` environment variable used during
investigation is prototype-only scaffolding on the throwaway branch and is not
part of this change.

### Edge cases

- `_suppressLabelOverlay` (hide on lost focus): hide all forms.
- Virtual desktop change (monitor reconfiguration): reposition all forms.
- `AllowTopmost` / `ApplyTopmostPolicy`: applies per form.
- Empty item list: hide all forms, as the single form hides itself today.
- Disposal: tear down every form, pooled ones included.
- `LabelBackgroundTransparent == false`: this controller is never used; the
  preview overlay's own label drawing is untouched.

## Verification

Measured on the prototype, on the target hardware:

| | single form | per-form prototype |
|---|---|---|
| repaints during drag + 50 s switching | 4+ per drag | **0** |
| cost per repaint | 40,228 us | 479 us (65,280 px) |
| TriffView CPU while switching | 8.89% | 5.15% |
| dwm CPU while switching | 38.57% | 31.15% |
| GDI / USER handles | 70 / 50 | 79 / 55 |
| ghost labels | no | no |
| z-order problems observed | n/a | none |

Caveats, stated because they matter:

- The per-form run had **5 EVE clients, not 6**, so the CPU and dwm columns are
  not strictly comparable. The repaint count is workload-independent and is the
  number to trust.
- The 479 us figure comes from **5 creation-time paints only**; there were no
  steady-state repaints to measure, because nothing changed content. True
  steady-state cost is unmeasured and probably lower.
- **The prototype never resized a preview.** Every one of the 960 move-only
  operations came from dragging, so the rule's resize branch is unvalidated.
  This is the largest gap in the evidence.
- dwm did not rise, so the concern that this trades application CPU for
  compositor cost appears unfounded — but see the client-count caveat.

### Required before shipping

Beyond unit tests, these must be exercised on Windows with real clients:

- **Resize a preview**, including down to `TriffViewPreviewDimensions.Minimum`,
  and confirm the label re-lays-out correctly and is not clipped. This covers
  both the unvalidated resize branch and the frame-overflow case.
- Drag a preview: confirm no ghost and no label lag.
- Close and reopen clients repeatedly: confirm forms are pooled and GDI/USER
  handle counts stay bounded.
- Rapid client switching: confirm no label falls behind its thumbnail.

## Testing

`TriffViewSubsystem.cs` is untestable as-is: the test project links pure-logic
source files rather than referencing the `net8.0-windows` project.

Extract the **diff decision** — given previous and current item state, produce
the set of create / move / repaint / retire actions — into its own
dependency-free file, with a `Compile Include` added to
`tests/TriffView.Tests`. It uses only `Rectangle` and primitives
(`System.Drawing.Primitives` is cross-platform and already used in tests).

That logic is the part most likely to break and the only part unit-testable.
Cases worth covering: location-only change produces move without repaint;
**size change produces a repaint, not a move**; content change produces repaint;
disappearing handle retires its form; reappearing handle reuses a pooled form;
empty list retires everything.

Window creation, z-order, and repaint behaviour cannot be unit-tested and must
be verified by running on Windows with real clients.

## Out of scope

- **`region.rebuild`**, now the dominant drag cost: 134 calls at 1,911 us
  average, roughly 256 ms per drag, in the preview overlay's window region
  handling. Real, and the next bottleneck once this lands, but a separate
  investigation.
- **WebView2's ~205 MB**, allocated at startup and held for the session. The
  largest single memory item, but fixing it means not auto-showing settings at
  startup, which is a product decision.
- Any change to the non-transparent label path.

## Risks

- **Z-order across many topmost windows.** Windows keeps topmost windows in one
  band whose internal order is not stable. Six windows must each independently
  stay above their thumbnail. No problems were observed in the prototype across
  50 s of rapid switching, including with OBS running, but absence of a fault in
  one session is weaker evidence than the repaint counts.
- **Handle growth if pooling is wrong.** Bounded at +12 GDI / +5 USER when
  correct; unbounded if forms leak per client cycle. The existing sampling
  harness detects this.
- **Prototype-to-production gap.** The prototype keyed forms by label text,
  which collides on duplicate or blank names. This design keys by window handle
  specifically to remove that.
