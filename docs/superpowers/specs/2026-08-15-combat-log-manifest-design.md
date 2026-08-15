# Combat log export manifest

Date: 2026-08-15
Status: **designed, approved — not yet implemented**

## Outcome

The combat log export writes a `triffview-manifest.json` entry alongside the
game logs, recording export provenance and — the reason this exists — the fact
that every character in the archive is one human's. eve-intel cannot recover
that from the logs: a gamelog says who was listening, never who was at the
keyboard.

TriffView is the only place the fact exists, and it exists for free. Every log
the export collects comes from one machine's Gamelogs folder, and one machine is
one player — so every character with a log there is that player's.

**Assumption, recorded deliberately:** one machine, one human. A shared machine
would break the linkage, and the design does not defend against it. That is a
decision, not an oversight: it is not a situation this project has. Note the
attribution comes from folder membership, *not* from the live client windows
TriffView enumerates for previews — those are current state at export time and
would silently drop a character who closed their client before the export ran.

## Why the linkage matters

eve-intel's `_assemble` (`.claude/skills/fight-aar/parse_fights.py:234`) derives
its friendly set from combat events:

```python
own = {e["pilot"] for e in fight["events"]}
```

The comment above it reaches for a guarantee the code does not quite implement:

> Friendly-set, deliberately wider than `participants`: a contributed log proves
> the character is ours whether or not they fought, so excluding them from the
> fight must not promote them to a hostile when a fleetmate's friendly fire or
> stray ewar names them as a counterpart.

A contributed log proves it, but `own` is built from events inside the detected
window, not from archive membership. A character whose log was exported but who
produced no in-window events is absent from `own`, and a fleetmate's friendly
fire or stray ewar naming them promotes them to a hostile.

Beyond the friendly set, knowing the characters share an operator changes the
analysis itself. Five alts flown by one human is not five pilots: attention was
split five ways, and per-pilot decision review reads differently when the
"pilot" was one of five windows someone was cycling between.

## Compatibility

The manifest is inert until eve-intel opts in, and cannot break it in the
meantime. Verified against `parse_fights.py` at `guarzo/eve-intel@main`:

- `main` extracts the archive and then globs `sorted(src.rglob("*.txt"))`
  (line 329). A `.json` entry is extracted and never looked at.
- `SKILL.md` step 2 is "Read fight.json, never raw logs", so the LLM side does
  not read archive members either.

**The manifest must never be named `*.txt`.** `parse_file` keys a file with no
`Listener:` header as `Unknown<path>` (line 168) and counts any line starting
with `[` (line 172). A `.txt` manifest would become a phantom pilot, and a
top-level JSON array would feed it unparsed lines. The `.json` extension is
load-bearing, not cosmetic — a test asserts the archive contains no `*.txt`
entry other than the game logs.

Consuming the manifest is separate work in eve-intel: widen `own` to include
every character the manifest lists, and surface provenance in the AAR. Absent a
manifest, current behavior must be unchanged — archives predating this change
stay readable.

## Disclosure

The manifest is always written. No opt-in, no extra confirmation: exporting logs
for upload is already the act of sharing this, and gating it behind a checkbox
would add a decision without adding a choice.

Two lines of existing copy become inaccurate and must change, concisely — the
archive is no longer only game logs:

- `app/src/tools/TriffViewSettings.jsx:1827` — "Game logs only, copied as-is.
  Chat logs are never included."
- `README.md:59` — "Logs are copied exactly as EVE wrote them, one file per
  character session, and only from the Gamelogs folder."

The substance of both promises survives and is worth keeping: logs are still
copied byte-for-byte, and chat logs are still never included. Only the "nothing
else is in the archive" implication is now wrong, so the edit is a short clause
naming the manifest, not a rewrite.

## Schema

`triffview-manifest.json`, at the archive root. Named for the tool rather than
`manifest.json` because eve-intel already calls its own provenance block in
`fight.json` a manifest, and the collision would mislead.

```json
{
  "schema": "triffview.combat-log-export/1",
  "tool": { "name": "TriffView", "version": "1.6.4" },
  "exportedUtc": "2026-08-15T20:14:03Z",
  "window": {
    "startUtc": "2026-08-14T12:00:00Z",
    "endUtc": "2026-08-14T12:05:00Z",
    "source": "last-fight"
  },
  "operator": {
    "characters": [
      { "name": "Alpha", "id": 98000001, "files": ["20260814_115000_98000001.txt"] },
      { "name": "Bravo", "id": null, "files": ["20260814_090000.txt", "20260814_121500.txt"] }
    ]
  },
  "unattributedFiles": [],
  "droppedFileCount": 0
}
```

**`operator`** carries the linkage structurally: everything nested under it
belongs to one human. No operator id — a shared machine is not a concern this
project has, so an id would buy nothing beyond correlating separate exports,
which nothing asks for. The nesting leaves room to add one later without
breaking the schema.

**`characters[].id`** is the numeric character id from the gamelog filename,
`YYYYMMDD_HHMMSS_<characterID>.txt`. Nullable: older clients wrote
`YYYYMMDD_HHMMSS.txt` with no id. Names are what eve-intel keys pilots off
today, so the id is additive — it survives a character rename, which a name does
not.

**`characters[].files`** is an array because a pilot who relogged mid-fight has
several session files, which the export already collects deliberately.

**`unattributedFiles`** lists exported logs with no readable `Listener:` header.
These are exported today with an empty character name and no way to notice.

**`window.source`** is `last-fight`, `manual-range`, or `unspecified`,
distinguishing a window derived from combat alerts from one typed by hand, with
`unspecified` meaning the caller did not state which. It tells eve-intel how
much to trust the window's edges.

## Implementation shape

`CombatLogFightWindow` gains a `Source` property (enum, `LastFight` /
`ManualRange`). `DetectLastFight` sets `LastFight`; the manual branch of
`BuildCombatLogWindow` sets `ManualRange`. The window object is already in scope
at the single call site, which today decomposes it
(`TriffViewSubsystem.cs:1155`):

```csharp
// before
CombatLogExport.Export(gamelogsPath, window.StartUtc, window.EndUtc, destination)

// after
CombatLogExport.Export(gamelogsPath, window.StartUtc, window.EndUtc, destination, window.Source)
```

so `Export` gains a trailing optional parameter:

```csharp
public static CombatLogExportResult Export(
    string gamelogsPath, DateTime startUtc, DateTime endUtc, string destinationZipPath,
    CombatLogWindowSource source = CombatLogWindowSource.Unspecified)
```

`Unspecified` is the default so existing callers and the current tests compile
untouched; it serializes as `"unspecified"` rather than being omitted, so a
consumer can tell "not stated" from "not known".

The manifest is written as a final archive entry inside the existing staging
block in `CombatLogExport.Export`, so a failed manifest write cleans up like any
other failure and leaves nothing at the destination.

Character id parsing is new but small, and has one trap. The filename stem is
split on `_`; an id is read only from a stem of the exact shape
`<8 digits>_<6 digits>_<digits>`, taking the third segment. A looser rule — "the
trailing all-digit segment" — misparses the id-less `20260814_090000.txt` by
reading its six-digit *time* as a character id. Anything not matching that exact
shape yields null. Files are grouped by the `Listener:` name `SelectLogs`
already captures.

**Deliberately unchanged:** `FileCount` and `RawBytes` stay logs-only. The
settings panel renders "Exported N logs", so counting the manifest would be a
quiet UI regression, and `RawBytes` means "bytes of game log" everywhere else it
appears.

## Testing

- The manifest exists at the archive root and is valid JSON.
- Every exported character appears once, with all of its session files.
- `id` is parsed when the filename carries one and null when it does not,
  including the `20260814_090000.txt` case where a looser rule would read the
  time segment as an id.
- A log with no `Listener:` header lands in `unattributedFiles`, not `operator`.
- `window.source` reflects detection versus a manual range.
- The panel copy and README no longer claim the archive is only game logs.
- `droppedFileCount` matches the 64-file cap path.
- `FileCount` is unchanged by the manifest's presence — regression guard on the
  settings panel copy.
- The archive contains no `*.txt` entry that is not a game log — encodes the
  eve-intel contract so a later change cannot quietly break it.

`exportedUtc` is asserted for shape, not value; the export takes no clock
dependency for one field.

## Out of scope

The eve-intel change that consumes this. It is a separate repository and a
separate PR: widen `own` from the manifest, fall back to current behavior when
absent, and surface provenance in the report.
