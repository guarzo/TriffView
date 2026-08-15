# Rebuild fork/main on upstream/main

Date: 2026-08-15
Status: **planned — not yet executed**

## Goal

Replace the merge at `f2f7140` with a linear history: `upstream/main` plus a
small stack of fork commits. The measure of success is not tidiness — it is that
the fork stops *overwriting* files upstream actively develops, so the next sync
is a fast-forward plus our additions.

## The problem this fixes

Against `upstream/main` the fork is +5198 / -347 across 27 files. Almost none of
that is the burden:

| Category | Size | Sync cost |
|---|---|---|
| Files upstream does not have | ~4800 lines | none — cannot conflict |
| `native/TriffView/TriffViewSubsystem.cs` | +335 / **-97** | all of it |

The 97 deleted lines are the debt. They exist because both sides independently
built preview-position memory: upstream's `TriffViewPreviewPositionMemory` and
the fork's `RememberedPreviewFrames`. Same key `(Handle, ProcessId)`, same
resolution order (saved layout → remembered frame → title fallback → default).

Upstream's has exactly one defect: its default path calls
`DefaultStackRect(index)` unconditionally, so a slot that is "next in line" but
still held by a client that outlived a closure earlier in the stack gets used
anyway, and two previews land on the same rectangle. The fork's `FindFreeSlot`
probes past held slots.

**So we adopt upstream's implementation and port only the fix.** That is the
whole point: ~25 lines of divergence in a shared file instead of 97 plus two
whole classes.

## What happens to each file

**Adopt upstream's, drop ours (4 files, 728 lines):**

- `native/TriffView/RememberedPreviewFrames.cs` (230) — superseded
- `native/TriffView/PreviewFrameResolver.cs` (45) — superseded; upstream's
  `TriffViewPreviewPositionMemory.Resolve` is already a pure static over four
  nullable rectangles and is testable the same way
- `tests/TriffView.Tests/RememberedPreviewFramesTests.cs` (298) — ported, see below
- `tests/TriffView.Tests/PreviewFrameResolverTests.cs` (155) — ported, see below

Upstream's `TriffViewPreviewPositionMemory.cs` and
`native/TriffView.PreviewPositionTests/` are **kept**, not deleted. The merge
removed both; this plan does not.

**Port forward as a small additive fix (~25 lines):**

Free-slot probing onto `TriffViewPreviewPositionMemory`, plus its use in
`ResolveFrameRect`'s default argument. This is the only intended change to an
upstream-owned file.

**Replay unchanged — pure additions, no conflict risk (21 files):**

`native/TriffAlerts/*` (combat log export + manifest, 504),
`native/TriffView/TriffViewDiagnostics.cs` (108),
`native/TriffView/TriffViewRect.cs` (33),
`native/TriffView/PreviewPointerGesture.cs` (26),
`app/src/tools/TriffViewSettings.jsx` (122), `app/src/styles.css` (44),
`README.md` (9), `docs/**` (2342), `.github/workflows/build.yml` (30),
`.github/workflows/release.yml` (57),
`tests/TriffView.Tests/{CombatLogExportTests,PreviewPointerGestureTests}.cs`,
`tests/TriffView.Tests/TriffView.Tests.csproj`, `run-tests.ps1`.

**Rebuilt (1 file):**

`native/TriffView/TriffViewSubsystem.cs` — keep the fork's additive wiring
(alerts dispatch, combat-log export handler, diagnostics), drop every
position-memory substitution so upstream's stands.

## Test coverage is kept, not traded away

Adopting upstream's implementation must not cost the fork's 30 tests. It does
not, because upstream's types are testable:

- `PreviewFrameResolverTests` (11 tests) exercise resolution *order*. They port
  onto `TriffViewPreviewPositionMemory.Resolve(saved, remembered, title, default)`
  nearly verbatim — same four inputs, same precedence.
- `RememberedPreviewFramesTests` free-slot cases (6) move onto the new fix.
- Prune/remember/recycle cases port onto `PurgeExcept` / `Remember` / `TryGet`.
- The signature-escaping cases (4) are **dropped as obsolete**, not lost:
  they defend `ComposeSignature`'s string packing against control-character
  injection, and upstream's `BeginContext` takes typed arguments with no string
  composition, so the failure mode does not exist.

Any test that does not port cleanly is a finding to report, not a test to bend
until it passes.

## Steps

1. **Preserve the current state.** Tag `bbaac5a` as `pre-upstream-merge` and keep
   the merge commit `f2f7140` reachable on a local branch until this lands. The
   merged tree is the reference for what a working integration looks like.

2. **Branch from upstream.** `git checkout -b fork-v2 upstream/main`.

3. **Replay the 21 additive files** with `git checkout bbaac5a -- <paths>`. These
   need no reconciliation; verify by diffing each against `bbaac5a`.

4. **Rebuild `TriffViewSubsystem.cs`.** Start from the merged `f2f7140` version —
   it already integrates the fork's wiring with upstream's code and builds
   clean — then revert the position-memory swap: restore `_positionMemory`,
   `PreviewClientIdentity`, and upstream's `ResolveFrameRect` /
   `DefaultStackRect`, and delete `RememberedFrameKey`,
   `ResetRememberedFramesOnProfileChange`, `PruneRememberedFrames`,
   `TitleFallbackRect`, `DefaultFrameRect`. Working *backwards* from a known-good
   integration is less error-prone than re-applying 335 lines onto upstream's
   file from scratch.

5. **Port the free-slot fix** onto `TriffViewPreviewPositionMemory`, with the
   comment explaining why (a slot can be both next in line and still held).

6. **Port the tests** per the section above, and update
   `tests/TriffView.Tests/TriffView.Tests.csproj` compile includes: drop
   `RememberedPreviewFrames.cs` / `PreviewFrameResolver.cs`, add
   `TriffViewPreviewPositionMemory.cs`.

7. **Verify** — every gate, on Linux:
   ```bash
   export PATH="$HOME/.dotnet:$PATH"
   dotnet build native/TriffView.csproj -c Release \
     -p:EnableSourceControlManagerQueries=false -p:EnableWindowsTargeting=true
   dotnet build native/TriffView.Tests/TriffView.Tests.csproj -c Release \
     -p:EnableSourceControlManagerQueries=false -p:EnableWindowsTargeting=true
   dotnet test tests/TriffView.Tests/TriffView.Tests.csproj -c Release \
     -p:EnableSourceControlManagerQueries=false
   cd app && npm ci && npm run build
   ```
   Neither `-p:` flag belongs in a committed file. Expect the combat-log tests to
   stay at their current count; the position tests will change count as they port.

8. **Commit as ~6 logical commits**, in dependency order: preview-slot fix →
   diagnostics → pointer gesture / rect → TriffAlerts service → combat log export
   and manifest → UI, docs, workflows.

9. **Confirm the divergence shrank.** `git diff --numstat upstream/main HEAD`
   should show **zero deletions** against upstream-owned files, except the
   deliberate lines the free-slot fix replaces.

10. **Force-push `fork/main`** — only after step 9 and explicit go-ahead. History
    rewrite; safe now that PRs #14 and #15 are merged, and `pre-upstream-merge`
    is the escape hatch.

## Risks

- **The behaviour claim is load-bearing.** Everything rests on upstream's memory
  being behaviour-identical outside the stacking bug. Evidence: identical key,
  identical precedence, and its `ResolveFrameRect` handles the character-select
  case through a title fallback. If a ported test fails, that assumption is
  wrong and the plan needs revisiting — do not adjust the test to fit.
- **Losing granular history.** 30 fork commits collapse to ~6. `pre-upstream-merge`
  preserves the originals.
- **`FindFreeSlot` semantics may not transplant cleanly.** It takes
  `Func<int, Rectangle> slotRect`; upstream's `DefaultStackRect` is an instance
  method over `_profile`. The port may need a small lambda at the call site.
- **The rejected per-preview-label-forms design doc** (1319 lines across plan and
  spec) carries forward. It is a doc, so it costs nothing at sync time and it
  records why not to attempt that approach again. Drop it only on request.

## Out of scope

Contributing anything to upstream.
