# Skill Planner usability redesign

Date: 2026-08-21
Status: approved design, ready for an implementation plan
Branch: `worktree-skills-planner-usability`

## Problem

The Skill Planner renders a plans-by-characters grid: plans as rows, characters
as columns, each cell a small square whose colour encodes readiness and whose
partial fill encodes the share of requirements already trained
(`app/src/tools/TriffSkills.tsx:792-923`).

With 40 characters this produces 40 columns with vertically rotated names,
forcing constant horizontal scrolling to find one character. The glyph fill was
an appealing idea but does not carry information legibly at cell size. There is
also no way to move a plan through the clipboard: import is a modal that expects
the plan text pasted into a textarea, and export does not exist.

## Intended outcome

- Finding one character among 40 is fast, with no horizontal scrolling.
- The default view answers "who can fly this?", which is the question the tool
  is actually opened for.
- A whole plan can be copied to, and imported from, the clipboard as a single
  unit, with import reading the clipboard directly rather than requiring a paste.
- The glyph grid is gone.

## Repository evidence and constraints

Facts established by inspection, with sources.

**The target view already exists.** Selecting a plan today renders characters
grouped by readiness (`app/src/tools/TriffSkills.tsx:419-448`). The redesign
promotes that panel to be the main view rather than inventing one.

**TriffSkills owns its own state file.** `%APPDATA%\TriffView\TriffSkills\state.json`
(`native/TriffSkills/TriffSkillsState.cs:16-22`) — note `TriffView`, not the
`TriffHud` directory that holds `triffview-settings.json`. New persisted
preferences belong here, and to `TriffSkillsState.Normalize()`
(`native/TriffSkills/TriffSkillsState.cs:128`), not to
`TriffViewSettings.Normalize()`.

**`Normalize()` runs on both load and save.** `TrySave` calls it before writing
(`native/TriffSkills/TriffSkillsState.cs:116`), and `Deserialize` calls it on
read (`:199`). Orphan cleanup placed there needs no additional call sites.

**`EveClientWindow.StableKey` is not involved.** TriffSkills characters are
ESI-authenticated and keyed by numeric `CharacterId`
(`native/TriffSkills/TriffSkillsState.cs:35`); `Normalize()` already drops
non-positive ids and de-duplicates. The character-name-versus-window-handle
fallback documented in CLAUDE.md belongs to the preview subsystem and does not
reach this code.

**Clipboard export has a working path; clipboard read does not.** `copyText()`
(`app/src/nativeBridge.js:53`) posts `copy-text`, handled at
`native/MainWindow.xaml.cs:900` via `System.Windows.Clipboard.SetText`, and is
already used by `app/src/tools/TriffFleets.tsx:277`. By contrast `readClipboard()`
(`app/src/nativeBridge.js:63`) has **no callers anywhere**, and on the native
path it posts `read-clipboard` and immediately returns `""`; the native reply
(`native/MainWindow.xaml.cs:913-921`) is a bare `{type:"clipboard", text}` with
no correlation id, consumed only by the focused-paste-target handler at
`app/src/nativeBridge.js:1050`. It cannot support a request/response import.

**The matrix builder must survive the matrix UI.** `TriffSkillsMatrix.BuildCompact`
(`native/TriffSkills/TriffSkillsMatrix.cs:20`) produces the per-plan ready counts
the new plan rail displays and the per-character readiness the new roster
displays. `MatrixBoundsTests` (`native/TriffView.Tests/MatrixPerformanceTests.cs`)
asserts its shape and a 1.5 MB payload ceiling at 30x100; the live case is 40x7.

**Plan import is already a validated two-phase commit.** `preview-plan` then
`commit-plan`, with a revision, a 10-minute preview lifetime, and collision
handling (`native/TriffSkills/PlanImportWorkflow.cs:38-95`); `PlanStore.CommitValidated`
writes atomically and re-reads to verify. Clipboard import reuses this rather
than introducing a second write path.

**`CommitValidated` compares requirements, not names.** `SameRequirements`
(`native/TriffSkills/PlanStore.cs:201`) checks only the requirement sequence, so
committing a validated preview under a different validated name is safe.

**Limits.** 50 characters maximum (`TriffSkillsState.cs:68`) against a 40-character
roster; 200 plan files; 512 KiB per plan file; 600 KiB bridge payload for plan
previews; 1,200,000 characters per web message (`MainWindow.xaml.cs:111`).

**The evaluator cannot rank by training time.** `SkillPlanEvaluator.Evaluate`
(`native/TriffSkills/SkillPlanEvaluator.cs:53`) knows current levels and queue
entries only. Skill rank and attribute multipliers are not held anywhere in the
app, so "cheapest to train" is not computable. Distance can only be expressed as
a count of missing requirements.

## Design

### Observable behaviour

**Left rail.** Brand and counts, `Add character`, `Refresh characters`, then the
plan list — each row a plan name with its ready ratio (`5/40`) — then `Copy plan`,
`Import from clipboard`, `Open plans folder`, `Reload plans`. Seven plans fit the
214px rail without scrolling; forty characters never enter it.

**Main pane.** The selected plan fills the pane, headed by its name and
requirement count, with two tabs: **Readiness** and **Train next**.

*Readiness tab.* A filter box and group chips, then characters in groups:

| Order | Group | Contents |
|---|---|---|
| 1 | Pinned | Pinned characters, whatever their readiness |
| 2 | Ready | All requirements met |
| 3 | Training | Requirements in the skill queue, with ETA |
| 4 | Locked | Trained but inactive |
| 5 | Missing | Not trained, sorted fewest-missing first |
| 6 | Unknown | Skill names that did not resolve |
| 7 | Unscored | No successful skill snapshot yet |

**Every character appears in exactly one group, always.** `Unscored` is not
optional padding: `SkillPlanEvaluator.Evaluate` returns it whenever a character
has no successful fetch (`native/TriffSkills/SkillPlanEvaluator.cs:61-63`), which
is the state of every newly authenticated character until its first refresh
lands, and of any character whose first refresh failed. It is also the group that
`READINESS_ORDER` already carries as its sixth entry
(`app/src/tools/TriffSkills.tsx:94`).

Omitting it would be a lockout rather than a cosmetic gap: with forget and
re-authenticate relocated into the character row, a character with no row has no
way to be removed or repaired. The roster must therefore be driven by "every
character in `state.characters`", with readiness selecting the group — never by
enumerating groups and hoping they cover the roster.

Each group header carries the readiness mark as a colour key and a count. Rows
read `Aiga Otsolen … Ready`, `Zuelo Parvi … Training — 2d 4h`,
`Gustav Oswaldo … Missing 6`, with group membership shown inline as a small tag.
Clicking a row expands its outstanding requirements in place, with a
`Copy missing skills` action.

*Train next tab.* Characters not ready for the selected plan, ordered by fewest
missing requirements, each row showing that count. Expanding a row fetches and
lists the specific skills and levels still needed. This is a distance ordering,
not a cost ordering — see the evaluator constraint above. Cross-plan "cheapest
unlock" is out of scope.

The expansion is lazy by necessity, not preference: the compact matrix carries
counts only (`native/TriffSkills/TriffSkillsMatrix.cs:59-70`), and the specific
missing requirements come from the existing one-character/one-plan detail request
(`native/TriffSkills/TriffSkillsController.cs:556-591`). Rendering every
character's skills up front would mean up to 50 detail requests on tab open, or a
new batched projection. Neither is worth it when the ordering already answers the
question and only the expanded row needs the detail. One request per expansion
matches how the current cell selection already behaves.

**Character management.** Forgetting a character and re-authenticating one move
into the expanded character row, which is where a character's own detail now
lives. The expansion carries `Forget character` behind the existing
confirm step, the existing re-auth prompt when `needsReauth` is set, the stale-data
flag, and any per-character error — the same content the character detail panel
renders today (`app/src/tools/TriffSkills.tsx:360-395`), reached by expanding a
row rather than by selecting a column header.

**Pinning and groups.** The star on each row toggles that character's pin. The
chip row above the roster ends with `+ Group`, which prompts for a name and
creates an empty group; each chip carries a context action to rename or delete
it. Group membership is set from the expanded character row, which lists the
groups with a checkbox each. Renaming a group preserves its membership; deleting
one removes the group without touching the characters.

**Freshness.** The current per-row `Current` / `Stale` label
(`app/src/tools/TriffSkills.tsx:440`) becomes a badge shown only when a character
is actually stale. In the common case every row said `Current`, which is noise.

**Filtering.** The filter box matches character names as you type. Group chips
narrow to one group. Pinned characters always surface in the Pinned group
regardless of readiness, but are still subject to the filter box.

### Persisted state

Three fields added to `TriffSkillsState`:

```json
{
  "characters": [ "… unchanged …" ],
  "selectedCharacterId": 0,
  "pinnedCharacterIds": [ 9011, 9042 ],
  "characterGroups": [
    { "name": "Haulers", "characterIds": [ 9011, 9033 ] }
  ],
  "selectedPlanName": "Mastadon"
}
```

`Normalize()` gains these invariants, consistent with how it already treats
`Characters`:

- `pinnedCharacterIds`: drop ids absent from `Characters`, drop non-positive ids,
  de-duplicate, cap at `MaxCharacters`.
- `characterGroups`: trim names; drop empty names; de-duplicate names
  case-insensitively, first wins; cap the name at 32 characters; cap the list at
  20 groups; within each group drop ids absent from `Characters`, de-duplicate,
  and cap at `MaxCharacters`. **An empty group is valid and must be kept** — a
  group is created before anyone is put in it, and because `TrySave` normalizes
  before it writes (`native/TriffSkills/TriffSkillsState.cs:116`), an invariant
  that dropped memberless groups would delete every new group in the same call
  that created it.
- `selectedPlanName`: trim, cap length. Not validated against the plan list —
  plans are files loaded separately — so the controller resolves an unknown name
  to the first available plan without rewriting state.

**Orphan cleanup must not run inside a rollback.** `Normalize()` mutates in place
and `TrySave` calls it *before* writing
(`native/TriffSkills/TriffSkillsState.cs:116`), while the forget-character
rollback restores only `Characters` and `SelectedCharacterId`
(`native/TriffSkills/TriffSkillsAuthentication.cs:86-104`). Left alone, a forget
whose save fails would restore the character but leave its pins and group
memberships already stripped from memory — silent partial data loss on a path
that reports success at rolling back.

The fix is to extend that existing rollback rather than restructure `Normalize()`:
capture `pinnedCharacterIds` and `characterGroups` alongside the existing
`previous` character clone and `previousSelection`, and restore all four together
when `_saveState()` fails. This keeps the snapshot next to the mutation it
protects, matching how the character clone is already handled.

**Backward compatibility.** All three fields are additive and default to empty.
An existing `state.json` written by the current build deserialises unchanged, so
no migration is required.

Downgrading is lossy, and deliberately not defended against. `TriffSkillsState`
has no `[JsonExtensionData]`, so an older build deserialises the file, drops the
three fields it does not know, and erases them on its next save
(`native/TriffSkills/TriffSkillsState.cs:116-120`). The cost is re-pinning and
re-creating groups, which is minutes of work and no character or plan data;
versioning machinery to protect a downgrade path nobody has asked for is not
worth its weight.

### Native and web contract

Clipboard import is handled entirely on the native side. The broken
`readClipboard()` path is not used, and is left as-is rather than repaired,
since nothing calls it.

New messages, web to native:

| Message | Payload | Effect |
|---|---|---|
| `triffskills:import-clipboard` | `{requestId}` | Native reads `Clipboard.GetText()`, derives a candidate name from a leading `#` comment line, and runs the existing `PlanImportWorkflow.PreviewAsync` |
| `triffskills:copy-plan` | `{planName}` | Native reads the plan file, prepends a `# <name>` title line, and calls the existing `CopyText` |
| `triffskills:set-pinned` | `{characterId, pinned}` | Toggles a pin, saves state |
| `triffskills:save-group` | `{name, characterIds, originalName?}` | Creates or renames a group |
| `triffskills:delete-group` | `{name}` | Removes a group |
| `triffskills:select-plan` | `{planName}` | Persists the selected plan |

New message, native to web:

| Message | Payload |
|---|---|
| `triffskills:clipboard-preview` | `{requestId, ok, name, requirementCount, diagnostics}` |

`commit-plan` gains an optional `name`, validated through `PlanNameValidator`,
so the name edited in the confirm dialog applies without a second preview round
trip. The requirement comparison in `CommitValidated` is unaffected.

The web side never receives the plan text. A 512 KiB plan read from the clipboard
stays native; only the name, count, and diagnostics cross the bridge.

**The state projection gains the three new fields.** `PostState`
(`native/TriffSkills/TriffSkillsController.cs:607-647`) currently projects
characters, plans, matrix, plan issues, warnings, and the plans timestamp — the
new persisted state is not in it, and the roster cannot render pins, group chips,
or the selected plan without it. `triffskills:state` therefore adds
`pinnedCharacterIds`, `characterGroups`, and `selectedPlanName`, projected from
the normalized state so the web never sees an id the native side has already
dropped.

**Mutations snapshot, apply, save, and re-post state.** Each of `set-pinned`,
`save-group`, `delete-group`, and `select-plan` follows the shape the reorder
handler already established (`native/TriffSkills/TriffSkillsController.cs:229-242`):
capture the field it is about to change, apply the change, call `_saveState()`,
and **restore the captured value if the save fails**, then `PostError` with the
action name and `PostState(force: true)` either way.

Restoring is not optional here. `TrySave` runs `Normalize()` against live state
before writing and returns `false` without undoing anything
(`native/TriffSkills/TriffSkillsState.cs:112-125`), so a mutation left in place
after a failed save survives in memory, is served to the web on the next state
request, and can be written by any later save that happens to succeed — the exact
opposite of the "last good state" the failure path is supposed to preserve.

State is posted on the failure path too, not withheld. That is what re-syncs the
web's optimistic update back to the restored truth; suppressing it would leave the
UI showing a pin that no longer exists.

What remains unchanged in `triffskills:state` is `matrix`, which both the rail's
ready ratios and the roster's readiness depend on. `BuildCompact` is untouched.

### Clipboard round trip

Export and import have to agree on where the plan's name lives, and today it
lives nowhere in the file: `PlanStore` writes requirement lines only, and the
name comes from the filename (`native/TriffSkills/PlanStore.cs:149-160`). A naive
`Copy plan` would therefore produce text no import could name.

The parser already skips `#` comment lines
(`native/TriffSkills/SkillPlanParser.cs:60`), so the name travels as one:

```
# Mastadon
Caldari Industrial V
Transport Ships I
```

`Copy plan` prepends `# <plan name>`. Import reads a leading `#` line as the
candidate name **and removes that line from the contents it previews**. Text
pasted from anywhere else still imports — it simply arrives without a candidate
name.

**The strip is load-bearing, not tidiness.** `PlanImportWorkflow` stores the
exact contents it previewed (`native/TriffSkills/PlanImportWorkflow.cs:74-75`)
and `CommitValidated` writes them verbatim
(`native/TriffSkills/PlanStore.cs:149-160`). Relying on the parser to skip the
comment would leave the title line in the saved file, and since export prepends
its own, every clipboard round trip would add another — a plan copied and
re-imported three times would carry three stale title comments, each possibly
naming something the plan is no longer called. Only the leading title line is
removed; any other `#` line the user wrote is preserved.

With the strip in place the on-disk format genuinely is unchanged: plan files
hold requirement lines, the name lives in the filename, and the title line exists
only in clipboard text.

**Only a `#` line is a title.** An earlier draft treated any first line that
failed to parse as the name. That would have silently swallowed a typo'd skill —
`Caldari Battleshp V` would have become the plan's name instead of the parse
diagnostic the user needs (`native/TriffSkills/SkillPlanParser.cs:62-120`).
Requiring the comment marker keeps every malformed requirement reportable.

### Import flow

1. `Import from clipboard` posts `triffskills:import-clipboard`.
2. Native reads the clipboard, takes the candidate name from a leading `#` line
   if one is present, and strips that line from the contents it will preview and
   ultimately save.
3. Native previews through the existing workflow. **The preview always runs under
   a valid name**, because `PlanImportWorkflow.PreviewAsync` validates the name
   before it parses anything (`native/TriffSkills/PlanImportWorkflow.cs:53-58`) —
   an empty candidate would fail with a name error and never reach the parser, so
   the requirement count and diagnostics the dialog needs would never be produced.
   When no candidate name is present, the preview uses the placeholder
   `Imported plan`, and the reply's `name` is empty so the dialog knows the user
   must supply one.
4. The dialog shows a name field pre-filled with the candidate, and a verdict
   line: either the requirement count, or the diagnostics. `Import` is disabled
   while the name is empty or invalid.
5. `Import` posts the existing `commit-plan` with the confirmed name. Because
   `CommitValidated` compares requirements and not names
   (`native/TriffSkills/PlanStore.cs:201`), committing under a name different
   from the placeholder is safe. Collision handling, atomic write, and reload
   verification are unchanged.

An empty clipboard, non-text clipboard content, or unparseable text surfaces as
diagnostics in the dialog rather than as a silent no-op.

### Removed

- The matrix table markup (`app/src/tools/TriffSkills.tsx:792-923`) and its CSS.
- `ProgressMark` at cell level. It is retained as the group-header colour key,
  and in the readiness legend.
- Drag-to-reorder: the handlers in `TriffSkills.tsx`, the
  `triffskills:reorder-characters` case in the controller,
  `TriffSkillsState.TryReorderCharacters`, and its tests. Manual ordering has no
  surface once the grid is gone, and pins plus groups replace its purpose.
- The per-row `Current` label, replaced by an exception-only `Stale` badge.

Nothing here is removed without a replacement. In particular the character
detail panel's contents are relocated, not dropped: forget, re-auth, the stale
flag, and per-character errors all move into the expanded character row, as
described under Character management above. Losing the only surface for
forgetting a character would otherwise strand any character whose credentials
had gone bad.

### Error and failure behaviour

Existing behaviour is preserved: stale last-good character data still renders
with a flag, `needsReauth` still surfaces, plan file issues still collapse into
the notices area, and a failed plan write still restores the previous file.

New failure paths:

- Clipboard read fails or the clipboard holds no text: the dialog opens with a
  diagnostic saying so, and `Import` is disabled.
- Clipboard text exceeds the parser or bridge limits: the existing
  `PlanImportWorkflow` limit diagnostics apply unchanged.
- `Copy plan` on a plan whose file has since been deleted: an error notice,
  consistent with existing plan-issue reporting.
- Group name collides with an existing group: rejected with a message, matching
  how plan-name collisions behave.

### Testing

Persistence and controller tests go to `native/TriffView.Tests`, which references
the project and can see `internal` types. `TriffSkillsState`'s tests already live
there (`native/TriffView.Tests/TriffSkillsStateTests.cs`), and
`tests/TriffView.Tests` links no TriffSkills sources at all — extracting
`Normalize()` into a Windows-free file purely to move its tests would fragment
the type for no gain.

In `native/TriffView.Tests`:

- `Normalize()` invariants for pins and groups: orphan cleanup when a character is
  removed, name de-duplication, cap enforcement, and **an empty group surviving a
  save**, which is the case a memberless-group invariant would silently break.
- The forget-character rollback restoring pins and group memberships alongside the
  character when the save fails — the partial-loss hole this design would
  otherwise open.
- Each new mutation handler restoring its captured value when `_saveState()` fails,
  and posting state on the failure path as well as the success path.
- Candidate-name derivation: a leading `#` line becomes the name and is stripped
  from the saved contents; a leading valid requirement does not become a name; a
  leading malformed requirement still produces a parse diagnostic rather than
  being swallowed as a name; a non-leading `#` comment is preserved.
- The clipboard round trip: a plan exported with its `# name` header re-imports
  under the same name and the same requirements, and **the saved file gains no
  title line however many times the cycle repeats**.
- `commit-plan` with a name override, including a name that fails
  `PlanNameValidator`.
- `triffskills:import-clipboard` against an injected clipboard seam. Per CLAUDE.md,
  tests must not touch a real system resource where a seam is available.
- The state projection carrying `pinnedCharacterIds`, `characterGroups`, and
  `selectedPlanName`, and mutation failures posting `triffskills:error`.

**Roster ordering has no automated coverage, and this design does not add any.**
`app/` has no test script and no test dependency — only `dev`, `build`, and
`preview` (`app/package.json:7-18`) — so grouping, pin precedence, filtering, and
fewest-missing ordering are verified by hand or not at all. Introducing a test
framework to the web project is a repo-level decision outside this change's scope.
The consequence is stated rather than papered over: the logic most likely to
regress here is the least protected, and the manual check below is the only thing
standing behind it.

`MatrixBoundsTests` must continue to pass untouched, confirming `BuildCompact`
was not disturbed.

### Verification

Because this repository cannot be built or run from WSL, the split is explicit.

Can be verified here:

- `tests/TriffView.Tests` and `native/TriffView.Tests` via the explicit-SDK
  invocation documented in CLAUDE.md for worktrees.
- `npm run build` for the web bundle.

Cannot be verified here, and must be exercised on Windows before any completion
claim:

- That `Clipboard.GetText()` returns what is expected from the WPF STA thread in
  this context.
- How the roster reads at 40 rows with real character names.
- **Roster ordering, which has no automated test at all** (see Testing): with the
  full 40-character roster, confirm group order, that pinned characters surface
  above their readiness group, that the filter box and group chips compose, and
  that Missing is ordered fewest-first.
- **That a newly added character appears** — it is `Unscored` until its first
  refresh, and must be visible and removable in that state.
- That the pane behaves on the 100% monitors as well as the 200% primary. The
  Skill Planner is WebView2 content inside the settings window rather than
  preview-overlay geometry, so the mixed-DPI trap documented in CLAUDE.md is not
  expected to apply — but "not expected to" is an inference, not a measurement.

Baseline recorded before any change, in this worktree:
`tests/TriffView.Tests` 182 passed; `native/TriffView.Tests` 318 passed.

## Decisions and alternatives

**Plan rail plus roster pane**, over stacked plan cards and a search-driven view.
Cards make comparing two plans easier but put a 40-row block between each pair of
plans; search is fast when the name is known but leaves no browsable default
state, which is the wrong trade for the primary "who can fly this" question.

**Pins and groups in native `state.json`**, over web `localStorage`. Consistent
with the rest of the subsystem and survives WebView2 profile resets. Note that
orphan cleanup is *not* free from `Normalize()` as first drafted — it needs the
rollback snapshot described under Persisted state, because `Normalize()` mutates
before the save it precedes.

**A `#` title line carries the plan name through the clipboard**, over a bare
requirement list or a separate name-carrying format. It reuses comment support
the parser already has, keeps exported text valid input for any other tool, and
leaves the plan files on disk unchanged.

**Train next expands lazily**, over a batched projection or eager fetch. The
missing-skill detail exists only per character per plan, so eager rendering costs
up to 50 requests on tab open. One request per expansion matches the existing
cell-selection behaviour and needs no new payload to size and regression-test.

**Always confirm the import with a name field**, over inferring silently or
auto-naming with a later rename. Nothing is created blind, and it avoids adding
a rename feature that does not exist today.

**The readiness mark stays as a group-header key**, over deleting it entirely or
replacing it with a per-character progress bar. Colour still separates groups at
a glance; the per-cell partial fill that did not read is what goes.

**Clipboard import handled natively**, over repairing `readClipboard()`. The
existing bridge reply has no correlation id, the repair would touch a shared
paste path used by every text control in the app, and keeping the plan text
native avoids a 512 KiB round trip.

## Out of scope

- Cross-plan "cheapest to train next" ranking; it needs skill rank and attribute
  data the app does not hold.
- Exporting or importing all plans at once.
- Renaming existing plans.
- Filtering to characters with a running EVE client, which would require joining
  ESI identities to window titles.
- Any change to authentication, ESI fetching, or plan file storage semantics.

## Assumptions that may change

- That `Clipboard.GetText()` behaves on the WPF STA thread here as it does for
  the existing `CopyText`. If not, the import message needs an STA dispatch.
- That plans stay few enough for a rail list. Beyond roughly 25 the rail needs
  its own filter; nothing in the design prevents adding one later.
- That the group chips and the filter box together are enough at 40 characters.
  If the roster still feels long, collapsing the Missing group by default is the
  cheapest next step.
