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

**It never reaches the web UI.** The state post carries only:

```json
"combatLogWebhook": { "configured": true, "description": "discord.com/api/webhooks/1234…" }
```

Both fields are derived from the credential store at post time. The token
segment is never serialized, so it cannot appear in a WebView2 message, a
DevTools network pane, or a screenshot of the settings window.

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
of game logs is left in `%TEMP%`. On startup, files matching
`triffview-fight-*.zip` in the temp directory older than a day are deleted,
best-effort and non-fatal.

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
    DateTime StartUtc; DateTime EndUtc; IReadOnlyList<string> Characters

static class CombatLogUpload
    Task<CombatLogUploadResult> UploadAsync(
        HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct)
```

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
redacted `description` once configured. **Send test** posts a text-only message
so the user finds out the webhook works before a fight they cared about fails to
upload.

Each existing path gains an **Upload to Discord** button beside its Export
button, disabled when no webhook is configured or a run is in flight. A separate
`combatLogUpload` state object holds upload result, error and busy, so the two
statuses do not overwrite each other.

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
- one end-to-end upload against a **local stub HTTP server**, proving multipart
  framing and that the temporary zip is deleted on both success and failure

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
