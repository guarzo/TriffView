# Combat log export uploads to a Discord webhook

Date: 2026-08-16
Status: **designed** — not yet implemented.

## Outcome

The Combat log export tab gains a second way to deliver an archive: instead of
saving a zip and uploading it to Discord by hand, the user configures one
webhook URL once and presses **Upload to Discord**. The archive is built exactly
as it is today, POSTed as a Discord attachment, and the temporary copy deleted.

The existing save-to-disk path does not change — not its buttons, not its
message contract, not `CombatLogExport.Export`. Upload is additive alongside it.

Nothing new is written to `triffview-settings.json`. The webhook URL is a
credential and lives in Windows Credential Manager; whether one is configured is
derived at state-post time. `TriffViewSettings.Normalize()` is untouched.

## Why a webhook, and why only a webhook

Discord webhooks need one pasted URL — no OAuth, no app registration to ship a
client ID for, no browser round-trip, no token refresh. Dropbox and every other
destination considered need all of that, and the feature's whole point is to be
the short path between a fight ending and an AAR existing.

The export already assumes Discord: `DiscordAttachmentLimitBytes` has been a
constant in `CombatLogExport.cs` since the export shipped, the panel already
warns when an archive exceeds it, and the README describes the zip as "ready to
upload to Discord for eve-intel". This design finishes a sentence the code was
already halfway through.

No `IUploadDestination` abstraction. There is one destination and no second one
planned; the interface would be written to a single implementation and would
have to be redesigned the moment a real second destination arrived with
different auth, different limits, and a different result shape.

## The webhook URL is a credential, and is treated as one

Anyone holding a Discord webhook URL can post into that channel. It is a bearer
token that happens to be shaped like a URL, and the three consequences below all
follow from taking that literally.

**It is stored in Windows Credential Manager**, via the existing
`ICredentialStore` (`native/Eve/EveCredentialStore.cs`) that TriffSkills and
TriffFleets already use for EVE refresh tokens. Target:

```
TriffView.CombatLogExport.DiscordWebhook
```

`native/TriffView.Tests/OAuthLoopbackTests.cs` already asserts that the
TriffSkills and TriffFleets credential prefixes neither match nor nest inside
one another. That assertion is extended to cover this target — a prefix
collision would make one subsystem's cleanup sweep delete another's secret.

Storing it in `triffview-settings.json` was rejected. That file is plaintext, is
what users are asked to send when reporting a problem, and has a backup
export/restore path that would carry the secret into a file shared even more
freely.

**It is never sent back out to the web UI.** The guarantee is one-directional,
and stating it precisely matters because the obvious stronger phrasing is false.

The user types the URL into a React input, so it necessarily exists in web state
and travels web → native as a WebView2 message, like every other setting in this
app (`app/src/nativeBridge.js:8-11`, `MainWindow.xaml.cs:648-650`). That leg is
unavoidable without a separate native dialog for one field — which was
considered and rejected: it would be the only non-React setting in the app, and
the leg it removes is in-process, on the user's own machine, under their own
session. That is not a boundary this app can defend, and pretending otherwise
buys inconsistency for no security.

What *is* guaranteed is the return leg. Once stored, the secret is never
serialized back. The state post carries only:

```json
"combatLogWebhook": { "configured": true, "description": "discord.com/api/webhooks/1234…" }
```

Both fields are derived from the credential store at post time, with the token
segment omitted. So the stored secret cannot appear in a state post, a saved
settings file, a diagnostics entry, an error message, or a screenshot of the
settings window — which is the set of places it would realistically escape from.
The web UI clears its input on a successful save, so it does not sit in the DOM
after it has been handed over.

**Reading it must not be able to break startup.** `Start()` calls `PostState()`
directly and unprotected (`TriffViewSubsystem.cs:99`), and `ICredentialStore.Read`
throws for every Win32 failure other than "not found"
(`native/Eve/EveCredentialStore.cs:22-43`). An unavailable or corrupted
credential store would therefore take down app startup on a code path that has
nothing to do with combat logs. The read is wrapped: any failure resolves to
`configured: false` plus a diagnostics-log warning, never an exception. A user
whose credential store is broken gets a webhook that appears unconfigured, which
is recoverable, rather than an app that will not start.

**And it is read once, not per state post.** `PostState` is not a cold path — it
runs from the 700 ms periodic refresh whenever client topology or state changes
(`TriffViewSubsystem.cs:415`), and again on a 100 ms timer after every client
switch. Deriving the webhook state inside it would put a `CredRead` P/Invoke on
that path, several times a minute, forever, to answer a question whose answer
changes only when the user edits it.

So the derived `{ configured, description }` pair is held in a controller field,
computed once during `Start()` and recomputed only when a set or clear succeeds.
`PostState` reads the field. This also means the guarded read above exists in
exactly one place rather than on every post.

**It never reaches an error message.** `PostError` posts `ex.Message` straight
through to the UI (`TriffViewSubsystem.cs:2088`), and `HttpRequestException` and
its inner exceptions routinely carry the request URI — which *is* the
credential. Every string that can leave the upload path passes through
`DiscordWebhook.Redact` first. This is the single easiest way for this feature
to leak the secret, and the redaction is therefore tested directly rather than
assumed.

## Only real Discord webhook URLs are accepted

`DiscordWebhook.TryParse` requires all of:

- scheme `https`
- host exactly one of `discord.com`, `discordapp.com`, `ptb.discord.com`,
  `canary.discord.com`
- path matching `/api/webhooks/{id}/{token}`, both segments non-empty

Anything else is rejected at the point of entry with a message naming what was
wrong, and nothing is written to the credential store.

A field that accepted any URL would not be a Discord webhook setting. It would
be a general "upload my game logs to a stranger's server" primitive, presented
in the UI as something narrower and safer than it is, and reachable by anyone
who can get a URL in front of the user. The logs name their characters. That
this app is an unsigned executable handling account data is exactly why the
allowlist is not negotiable, and there is no override toggle — an escape hatch
labelled "I know what I'm doing" is the thing a social-engineering script tells
someone to flip.

The cost is that a new Discord host would need a release to support. Discord has
not added one in years, and the failure mode is a clear rejection rather than a
silent misdelivery.

## Oversize archives are refused before the POST

Discord caps attachments at 10 MB for servers without boosts.
`CombatLogExportResult.ExceedsDiscordLimit` already computes this. When it is
true the upload stops, the temporary zip is deleted, and the user is told the
actual size and offered the two things that help: narrow the time range, or use
Export to disk and upload by hand.

Sending anyway and surfacing Discord's HTTP 413 would make the user wait through
a doomed upload of a file the app already knew was too big.

Boosted servers (50 MB / 100 MB tiers) cannot use their headroom under this
rule. That is accepted: the tier is not discoverable from a webhook URL without
an extra API call, and the conservative limit fails safe.

## Flow

`UploadCombatLogs` mirrors `ExportCombatLogs`, including its disposal discipline.

1. `BuildCombatLogWindow(fromUtc, toUtc)` — reused unchanged, so upload and
   export resolve "last fight" and a manual range identically.
2. Read the webhook. Missing → "Configure a Discord webhook first." and stop.
3. `Task.Run(() => CombatLogExport.Export(gamelogs, start, end, tempPath, source))`
   with `tempPath` under `%TEMP%`, named by the existing `SuggestFileName`.
   Off the dispatcher for the same reason the save path is: compressing a long
   session would otherwise stall every preview.
4. `ExceedsDiscordLimit` → delete the temp zip, report, stop.
5. `await CombatLogUpload.UploadAsync(...)`.
6. `finally` → delete the temp zip, on every path including failure.
7. `_disposed` guard, then post `triffview:combat-log-upload`.

**Timeout.** The upload runs under its own `CancellationTokenSource` at 120
seconds. The `HttpClient` instances elsewhere in the repo are configured at 8
and 20 seconds, which are right for a JSON call to ESI and wrong for pushing 10
MB over a domestic connection. Rather than widening one of those and changing
timeout behaviour for ESI calls, the upload path gets its own static
`HttpClient` with `Timeout` left infinite and the bound applied per-request
through the cancellation token.

**Why the timeout is load-bearing.** `ExportCombatLogs` is `async void`, with a
`_disposed` guard and a comment recording that a throw from its catch reaches
the thread pool and takes the process down. Adding a network call widens that
window from seconds of compression to however long a stalled socket hangs. The
UI disables its buttons while a run is in flight, so an upload that never
returns leaves the tab permanently dead with no way out. A bounded timeout that
always terminates in a reportable error is what prevents that.

**Orphaned temporaries.** If the process dies between step 3 and step 6, a zip
of game logs is left in `%TEMP%`. Two shapes have to be swept, not one:
`Export` compresses into `<destination>.<guid>.tmp` and only then moves it into
place (`CombatLogExport.cs:208-233`), so a death *during* compression leaves the
staging file rather than the finished archive. On startup, both
`triffview-fight-*.zip` and `triffview-fight-*.zip.*.tmp` in the temp directory,
older than a day, are deleted — best-effort and non-fatal. Sweeping only the
first glob would leave the privacy-sensitive artifact behind indefinitely in
exactly the case where the app crashed.

## Message contract

Four new `type` strings. Dispatch is first-handler-wins across four controllers
(`MainWindow.xaml.cs:660-694`), so these are checked to be unique repo-wide.

| Direction | Type | Payload |
|---|---|---|
| web → native | `triffview:set-combat-log-webhook` | `{ url }` |
| web → native | `triffview:clear-combat-log-webhook` | — |
| web → native | `triffview:test-combat-log-webhook` | — |
| web → native | `triffview:upload-combat-logs` | `{ fromUtc, toUtc }` |
| native → web | `triffview:combat-log-upload` | `{ result }` or `{ cancelled }` |
| native → web | `triffview:combat-log-webhook` | `{ configured, description, testResult? }` |

**Every inbound message has a terminal outbound reply.** The UI clears its busy
flag on a terminal result or error and nothing else
(`app/src/tools/TriffViewSettings.jsx:1282-1288`), so a command with no reply
leaves a button disabled forever. `set`, `clear` and `test` all answer with
`triffview:combat-log-webhook`: the first two carry the new configured state,
and `test` carries it plus a `testResult` of `{ ok, message }`. Failures on any
of the four still go out as `triffview:error` with a matching `action`, which is
the pattern the export path already uses.

`triffview:combat-log-upload` is deliberately **not** a reuse of
`triffview:combat-log-export`. That message's `result.path` is a save location
the UI prints verbatim ("Exported … to {path}"); on an upload it would be a
temporary file that no longer exists by the time the message is read. Two
messages also let the tab hold export and upload status independently, so
pressing one does not blank the other's result.

## New native file

`native/TriffAlerts/CombatLogUpload.cs`. Plain BCL — `System.Net.Http`, no WPF
and no WinForms — so it links into `tests/TriffView.Tests` the same way
`CombatLogExport.cs` does, and is equally reachable from
`native/TriffView.Tests` through its project reference.

```
static class DiscordWebhook
    bool TryParse(string? raw, out Uri webhook, out string error)
    string Describe(Uri webhook)              // token segment omitted
    string Redact(string message, Uri webhook)

sealed class CombatLogUploadResult
    bool Succeeded; string Message; int FileCount; long ZipBytes;
    DateTime StartUtc; DateTime EndUtc; IReadOnlyList<string> Characters;
    int DroppedFileCount

static class CombatLogUpload
    Task<CombatLogUploadResult> UploadAsync(
        HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct)
```

`DroppedFileCount` carries through from `CombatLogExportResult` and **is
reported on success**, not only on failure. `CombatLogExport.cs:60-67` gives the
reason in its own words: an export that quietly covered only part of a window
would read as complete coverage in the resulting AAR. That reasoning is stronger
on this path than on the save path. A saved zip passes through a human who saw
the panel's warning before uploading it; an uploaded one lands directly in a
channel where someone builds an after-action report from it, having never seen
this app's UI. So a successful upload that dropped files says so in the same
message that reports the success, and the `content` line posted to Discord names
the count too — the warning has to reach the channel, not just the operator.

The POST is `multipart/form-data` with `payload_json` carrying the `content`
string the caller passed and `files[0]` carrying the archive under its suggested
filename. The subsystem composes that string from the export result — a single
line giving the UTC window and pilot count — so `CombatLogUpload` stays free of
any opinion about wording and is testable on framing alone.

`UploadAsync` **returns** a failed `CombatLogUploadResult` rather than throwing
for any response the server produced or any transport error it can classify. A
single return path is what makes it possible to guarantee every outbound string
has been through `Redact`; exceptions escaping to the subsystem's generic
`catch` would bypass that and reach `PostError` unredacted.

Status handling is explicit rather than passed through, because a raw status
code tells the user nothing about what to do:

| Response | Reported as |
|---|---|
| 200 / 204 | success, with file count, pilots and size |
| 401 / 403 / 404 | the webhook no longer exists or was deleted in Discord |
| 413 | archive too large for that server |
| 429 | rate limited, with `retry_after` when present |
| other | status code and reason phrase |
| timeout / socket error | redacted transport message |

## Web UI

`app/src/tools/CombatLogExport.jsx` gains a **Discord destination** block above
the two existing export paths: a masked input, Save / Clear / Send test, and the
redacted `description` once configured. The input is cleared on a successful
save, so the secret does not linger in React state or the DOM after it has been
handed to the credential store. **Send test** posts a text-only message so the
user finds out the webhook works before a fight they cared about fails to
upload.

Each existing path gains an **Upload to Discord** button beside its Export
button, disabled when no webhook is configured or a run is in flight. A separate
`combatLogUpload` state object holds upload result, error and busy, so the two
statuses do not overwrite each other. A successful upload that dropped files at
the 64-file cap reports that alongside the success, in the wording the export
path already uses.

## Documentation

The README's combat-log section gains a paragraph on the upload path, beside the
existing "Chat logs are never included" line, stating plainly what leaves the
machine and where it goes: game logs and a manifest naming the operator's
characters, to the Discord channel behind the configured webhook.

`docs/DIAGNOSTICS.md` says "No network activity … Nothing is transmitted
anywhere." That is scoped to the diagnostics log and remains true of it, but it
reads as an app-wide promise. It is rescoped to say so explicitly.

## Verification

Runnable from WSL against the Windows toolchain, and to be shown as output
rather than asserted:

- `scripts/build-native.ps1`
- `tests/TriffView.Tests` — baseline 54 passing
- `native/TriffView.Tests` — baseline 155 passing, builds `--warnaserror`
- `npm run build` in `app/`
- validator accept/reject table, redaction, multipart framing and status
  mapping, against a fake `HttpMessageHandler`
- a dropped-file count survives from `CombatLogExportResult` into a **successful**
  upload result and into the posted `content` line
- a credential store that throws on read resolves to `configured: false` rather
  than propagating, using the existing `MemoryCredentials` fake's `FailRead`
  (`native/TriffView.Tests/ControllerLifecycleTests.cs:303`)
- the new credential target neither matches nor nests inside the TriffSkills or
  TriffFleets prefixes, extending the assertion at
  `native/TriffView.Tests/OAuthLoopbackTests.cs:45`
- one end-to-end upload against a **local stub HTTP server**, proving multipart
  framing and that both the finished archive and the `.tmp` staging file are
  deleted on success and on failure

Requiring the maintainer, and unexercised until then:

- an upload against a **real Discord webhook** — it is the user's secret and
  their channel
- the settings block and buttons rendering and behaving correctly

Note the worktree carries no `native/Assets/overlay-dist.zip`, so an app
launched from it serves the "missing overlay" page. Any UI check must run
against a build that has the zip copied in.

## Excluded

Message body customization, `?thread_id=` thread targeting, username/avatar
override, returning a share link, more than one configured webhook, automatic
retry beyond reporting a 429, boosted-tier size limits, and any destination
other than Discord.

## Related

- `docs/superpowers/specs/2026-08-15-combat-log-export-tab-design.md`
- `docs/superpowers/specs/2026-08-15-combat-log-manifest-design.md`
