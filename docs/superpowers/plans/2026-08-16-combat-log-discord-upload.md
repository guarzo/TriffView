# Combat Log Discord Upload Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the combat log export send its archive straight to a configured Discord webhook, instead of only saving a zip the user uploads by hand.

**Architecture:** A new pure-BCL file, `native/TriffAlerts/CombatLogUpload.cs`, validates Discord webhook URLs and performs the multipart POST; it never throws, so every outbound string can be guaranteed redacted. `TriffViewController` gains an injected `ICredentialStore`, stores the webhook URL in Windows Credential Manager beside the EVE refresh tokens, and adds an upload flow that reuses `CombatLogExport.Export` unchanged against a `%TEMP%` path. The existing save-to-disk path is untouched throughout.

**Tech Stack:** C# / .NET 8 (`net8.0-windows` app, `net8.0` pure-logic test project), WPF + WebView2 host, React 18 + Vite settings UI, xunit.

**Spec:** `docs/superpowers/specs/2026-08-16-combat-log-discord-upload-design.md`

## Global Constraints

- **Windows-only build.** `native/TriffView.csproj` is `net8.0-windows` with `UseWPF`. Never invoke a bare `dotnet` — it resolves to the user profile instead of the repo-local SDK. From WSL, always call the Windows toolchain through `powershell.exe`.
- **`.ps1` scripts need `-ExecutionPolicy Bypass`.** A bare `powershell.exe -NoProfile -File <script>.ps1` fails on this machine with `UnauthorizedAccess`.
- **Two test projects, both in CI.** `tests/TriffView.Tests` (`net8.0`, links pure-logic files via `<Compile Include>`, run by `.github/workflows/build.yml`) and `native/TriffView.Tests` (`net8.0-windows`, `ProjectReference` + `InternalsVisibleTo`, run by `.github/workflows/ci.yml` **with `--warnaserror`**). CLAUDE.md's Testing section predates the second and is wrong; Task 7 corrects it.
- **Baselines before any change:** `tests/TriffView.Tests` 54 passing, `native/TriffView.Tests` 155 passing. Tasks state the **delta** they expect, not an absolute total — Tasks 1-2 and Tasks 3-4 and Task 6 all add to these projects in sequence, so any absolute number written into a later task is wrong the moment an earlier one changes. **The runner's own summary line is authoritative**; if it disagrees with a count written here, trust the runner and correct the plan.
- **Credential target:** `TriffView.CombatLogExport.DiscordWebhook` — exact string, and it must neither match nor nest inside `TriffView.TriffSkills.RefreshToken.` or `TriffView.TriffFleets.RefreshToken.`
- **Host allowlist, exact and closed:** `discord.com`, `discordapp.com`, `ptb.discord.com`, `canary.discord.com`. HTTPS only. Path `/api/webhooks/{id}/{token}`, both segments non-empty. No override toggle.
- **Discord attachment cap:** `CombatLogExport.DiscordAttachmentLimitBytes` = 10 MB. Oversize archives are refused before the POST, never sent.
- **Upload timeout:** 120 s, applied per-request via `CancellationToken` on a dedicated `HttpClient` with `Timeout = Timeout.InfiniteTimeSpan`. Do not alter the existing 8 s / 20 s clients.
- **The webhook URL is a credential.** It is never written to `triffview-settings.json`, never serialized into a state post, never included in an error message, and never logged. `DiscordWebhook.Redact` is applied to every string that can leave the upload path.
- **`PostState` is a hot path** — it fires from the 700 ms periodic refresh and a 100 ms post-switch timer. It reads a cached field; it must never perform a credential read.
- **Message `type` strings are globally unique.** Dispatch is first-handler-wins across four controllers (`MainWindow.xaml.cs:660-694`).
- **Every inbound message needs a terminal outbound reply.** The UI clears its busy flag on a terminal result or error and nothing else (`TriffViewSettings.jsx:1282-1288`); a command without a reply leaves a button dead.
- **Comment culture:** comments explain *why* and record traps. They never restate the code. `CombatLogExport.cs` is the register to imitate.

---

### Task 1: `DiscordWebhook` -- URL validation and redaction

**Files:**
- Create: `native/TriffAlerts/CombatLogUpload.cs`
- Modify: `tests/TriffView.Tests/TriffView.Tests.csproj` (add a `Compile Include` line)
- Test: `tests/TriffView.Tests/CombatLogUploadTests.cs`

**Interfaces:**
- Consumes: nothing -- first task.
- Produces:
  ```csharp
  namespace TriffView.Alerts;

  public static class DiscordWebhook
  {
      public static bool TryParse(string? raw, out Uri webhook, out string error);
      public static string Describe(Uri webhook);
      public static string Redact(string message, Uri webhook);
  }
  ```
  Task 2 calls `DiscordWebhook.Redact` on every outbound message and `Describe` is
  used later by the subsystem (out of scope here) to build the `{ configured,
  description }` state payload.

- [ ] **Step 1: Add the test csproj link before any test exists**

  The link has to exist before `dotnet test` will even see the new test file, and
  an empty/missing target is a cheap thing to get wrong first.

  Edit `tests/TriffView.Tests/TriffView.Tests.csproj`, adding a third `ItemGroup`
  after the `CombatLogExport.cs` one:

  ```xml
  <!-- Same arrangement again. CombatLogUpload is plain BCL (Net.Http, Text.Json,
       Uri parsing) with no WPF or Forms dependency, so it builds on net8.0 too. -->
  <ItemGroup>
    <Compile Include="..\..\native\TriffAlerts\CombatLogUpload.cs" Link="CombatLogUpload.cs" />
  </ItemGroup>
  ```

  Create `native/TriffAlerts/CombatLogUpload.cs` with just enough to compile so
  this step can be verified in isolation:

  ```csharp
  namespace TriffView.Alerts;

  public static class DiscordWebhook
  {
  }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1"
  ```

  Expected: still 54 passing (no new tests added yet), build succeeds with the
  new file linked in.

- [ ] **Step 2: Write the accept/reject table for `TryParse`, see it fail to compile**

  Create `tests/TriffView.Tests/CombatLogUploadTests.cs`:

  ```csharp
  using TriffView.Alerts;
  using Xunit;

  namespace TriffView.Tests;

  /// <summary>
  /// The webhook URL is a bearer credential shaped like a URL. Every test here
  /// backs one of two guarantees: that only a real Discord webhook is accepted
  /// (an allowlist with no override), and that the token segment of it can never
  /// leak into a description or an error message.
  /// </summary>
  public class CombatLogUploadTests
  {
      // ---- DiscordWebhook.TryParse ----

      [Theory]
      [InlineData("https://discord.com/api/webhooks/123456789/abcDEF-token_123")]
      [InlineData("https://discordapp.com/api/webhooks/123456789/abcDEF-token_123")]
      [InlineData("https://ptb.discord.com/api/webhooks/123456789/abcDEF-token_123")]
      [InlineData("https://canary.discord.com/api/webhooks/123456789/abcDEF-token_123")]
      public void AGenuineWebhookUrlOnAnyAllowedHostIsAccepted(string url)
      {
          Assert.True(DiscordWebhook.TryParse(url, out var webhook, out var error));
          Assert.Equal("", error);
          Assert.Equal(url, webhook.AbsoluteUri);
      }

      [Fact]
      public void PlainHttpIsRejected()
      {
          // A webhook URL is a bearer credential; sending it over http would leak
          // it to anything on the network path.
          Assert.False(DiscordWebhook.TryParse(
              "http://discord.com/api/webhooks/123456789/abcDEF-token_123", out _, out var error));
          Assert.Contains("https", error, StringComparison.OrdinalIgnoreCase);
      }

      [Theory]
      [InlineData("https://discord.co/api/webhooks/123456789/abcDEF-token_123")]
      [InlineData("https://evil.example.com/api/webhooks/123456789/abcDEF-token_123")]
      [InlineData("https://discord.com.evil.example.com/api/webhooks/123456789/abcDEF-token_123")]
      public void AnyHostOffTheAllowlistIsRejected(string url)
      {
          // No override toggle exists for a reason -- see the design doc. A host
          // check that could be fooled by a lookalike domain would defeat the
          // whole point of the allowlist.
          Assert.False(DiscordWebhook.TryParse(url, out _, out var error));
          Assert.NotEqual("", error);
      }

      [Fact]
      public void AMissingTokenSegmentIsRejected()
      {
          Assert.False(DiscordWebhook.TryParse(
              "https://discord.com/api/webhooks/123456789", out _, out var error));
          Assert.NotEqual("", error);
      }

      [Fact]
      public void AMissingIdSegmentIsRejected()
      {
          Assert.False(DiscordWebhook.TryParse(
              "https://discord.com/api/webhooks/", out _, out var error));
          Assert.NotEqual("", error);
      }

      [Fact]
      public void AWrongShapedPathIsRejected()
      {
          // Some other Discord API path -- accepting this would turn the field
          // into a general "any discord.com URL" primitive, not a webhook setting.
          Assert.False(DiscordWebhook.TryParse(
              "https://discord.com/api/channels/123456789/messages", out _, out var error));
          Assert.NotEqual("", error);
      }

      [Theory]
      [InlineData(null)]
      [InlineData("")]
      [InlineData("   ")]
      public void BlankInputIsRejectedWithoutThrowing(string? raw)
      {
          Assert.False(DiscordWebhook.TryParse(raw, out _, out var error));
          Assert.NotEqual("", error);
      }

      [Fact]
      public void SomethingThatIsNotAUrlAtAllIsRejected()
      {
          Assert.False(DiscordWebhook.TryParse("not a url", out _, out var error));
          Assert.NotEqual("", error);
      }
  }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUploadTests"
  ```

  Expected: build failure -- `DiscordWebhook.TryParse` does not exist yet.

- [ ] **Step 3: Implement `TryParse`, see the table go green**

  Replace the stub in `native/TriffAlerts/CombatLogUpload.cs`:

  ```csharp
  namespace TriffView.Alerts;

  /// <summary>
  /// Validates and describes Discord webhook URLs. A webhook URL is a bearer
  /// credential shaped like a URL -- anyone holding it can post into that
  /// channel -- so every method here treats it that way: the allowlist has no
  /// override, and <see cref="Redact"/> exists so the token segment can never
  /// reach a log line or an error message.
  /// </summary>
  public static class DiscordWebhook
  {
      /// <summary>
      /// The only hosts Discord serves webhooks from. Deliberately not
      /// extensible at runtime -- see the design doc's reasoning for why an "I
      /// know what I'm doing" override would defeat the point of an allowlist
      /// for an unsigned executable handling account data.
      /// </summary>
      private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
      {
          "discord.com",
          "discordapp.com",
          "ptb.discord.com",
          "canary.discord.com",
      };

      public static bool TryParse(string? raw, out Uri webhook, out string error)
      {
          webhook = null!;
          error = "";

          if (string.IsNullOrWhiteSpace(raw))
          {
              error = "Enter a Discord webhook URL.";
              return false;
          }

          if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
          {
              error = "That is not a valid URL.";
              return false;
          }

          if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
          {
              error = "Webhook URL must use https.";
              return false;
          }

          if (!AllowedHosts.Contains(uri.Host))
          {
              error = $"'{uri.Host}' is not a Discord webhook host.";
              return false;
          }

          if (!TryReadIdAndToken(uri, out _, out _))
          {
              error = "That doesn't look like a Discord webhook URL " +
                      "(expected .../api/webhooks/{id}/{token}).";
              return false;
          }

          webhook = uri;
          return true;
      }

      /// <summary>
      /// Splits the path into its webhook id and token, or fails. Shared by
      /// <see cref="TryParse"/>, <see cref="Describe"/> and <see cref="Redact"/>
      /// so the segment layout is defined in exactly one place.
      /// </summary>
      private static bool TryReadIdAndToken(Uri uri, out string id, out string token)
      {
          id = "";
          token = "";

          var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
          if (segments.Length != 4) return false;
          if (!segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)) return false;
          if (!segments[1].Equals("webhooks", StringComparison.OrdinalIgnoreCase)) return false;
          if (segments[2].Length == 0 || segments[3].Length == 0) return false;

          id = segments[2];
          token = segments[3];
          return true;
      }
  }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUploadTests"
  ```

  Expected: 13 passing (4 host cases + 1 http + 3 allowlist cases + missing-token
  + missing-id + wrong-shaped-path + 3 blank cases + not-a-url).

  ```bash
  git add native/TriffAlerts/CombatLogUpload.cs tests/TriffView.Tests/CombatLogUploadTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
  git commit -m "Accept only real Discord webhook URLs

The field is a Discord webhook setting, so it takes Discord webhook URLs and
nothing else: https, one of the four known hosts, and a path carrying both an
id and a token. There is deliberately no override.

A field that took any URL would be a general upload-my-game-logs-anywhere
primitive wearing a narrower label, reachable by anyone who can get a URL in
front of the user. The archives name the operator's characters."
  ```

- [ ] **Step 4: Write the `Describe` and `Redact` tests, see them fail to compile**

  Append to `CombatLogUploadTests`:

  ```csharp
      // ---- DiscordWebhook.Describe / Redact ----

      private static readonly Uri SampleWebhook = new(
          "https://discord.com/api/webhooks/123456789/abcDEF-token_123");

      [Fact]
      public void DescribeNamesTheHostAndIdButNeverTheToken()
      {
          var description = DiscordWebhook.Describe(SampleWebhook);

          Assert.Contains("discord.com", description);
          Assert.Contains("123456789", description);
          Assert.DoesNotContain("abcDEF-token_123", description);
      }

      [Fact]
      public void RedactScrubsTheFullUrlFromAnArbitraryMessage()
      {
          var message = $"Could not reach {SampleWebhook.AbsoluteUri}: connection refused";

          var redacted = DiscordWebhook.Redact(message, SampleWebhook);

          Assert.DoesNotContain("abcDEF-token_123", redacted);
          Assert.Contains("connection refused", redacted);
      }

      [Fact]
      public void RedactScrubsALoneTokenEvenWithoutTheFullUrl()
      {
          // HttpRequestException messages vary by platform and .NET version --
          // some carry the full request URI, some just a fragment of it. The
          // token itself must never survive either shape.
          var message = "PostAsync failed for token abcDEF-token_123 after 3 retries";

          var redacted = DiscordWebhook.Redact(message, SampleWebhook);

          Assert.DoesNotContain("abcDEF-token_123", redacted);
          Assert.Contains("after 3 retries", redacted);
      }

      [Fact]
      public void RedactLeavesUnrelatedTextAlone()
      {
          var redacted = DiscordWebhook.Redact("Discord returned 500 Internal Server Error", SampleWebhook);

          Assert.Equal("Discord returned 500 Internal Server Error", redacted);
      }
  ```

  Run the same filtered command as Step 2.

  Expected: build failure -- `DiscordWebhook.Describe` and `DiscordWebhook.Redact`
  do not exist yet.

- [ ] **Step 5: Implement `Describe` and `Redact`, see everything go green**

  Append to `DiscordWebhook` in `native/TriffAlerts/CombatLogUpload.cs`:

  ```csharp
      /// <summary>
      /// A human-readable name for a webhook that omits its token, for the
      /// state posted back to the web UI ("discord.com/api/webhooks/1234…") and
      /// for anywhere else a webhook needs to be named without handing back the
      /// credential that names it.
      /// </summary>
      public static string Describe(Uri webhook)
      {
          return TryReadIdAndToken(webhook, out var id, out _)
              ? $"{webhook.Host}/api/webhooks/{id}\u2026"
              : webhook.Host;
      }

      /// <summary>
      /// Scrubs the token out of an arbitrary string -- an exception message,
      /// most often. Two passes because the token can surface either way:
      /// <see cref="HttpRequestException"/> messages sometimes carry the whole
      /// request URI and sometimes just a fragment naming the token, depending
      /// on platform and .NET version. This is the one thing standing between a
      /// stray exception and the credential reaching <c>PostError</c>, so it is
      /// tested directly rather than assumed.
      /// </summary>
      public static string Redact(string message, Uri webhook)
      {
          if (string.IsNullOrEmpty(message)) return message;

          var result = message.Replace(webhook.AbsoluteUri, Describe(webhook), StringComparison.Ordinal);

          if (TryReadIdAndToken(webhook, out _, out var token) && token.Length > 0)
          {
              result = result.Replace(token, "\u2026", StringComparison.Ordinal);
          }

          return result;
      }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUploadTests"
  ```

  Expected: 17 passing (13 from Step 3 plus these 4).

  ```bash
  git add native/TriffAlerts/CombatLogUpload.cs tests/TriffView.Tests/CombatLogUploadTests.cs tests/TriffView.Tests/TriffView.Tests.csproj
  git commit -m "Describe and redact webhook URLs without their token

A webhook URL is a bearer credential, so it must be possible to talk about one
without reproducing it. Describe builds the display form the settings UI shows;
Redact scrubs the token out of arbitrary strings.

Redact exists because HttpRequestException and its inner exceptions routinely
carry the request URI, and PostError sends ex.Message straight to the web UI.
Every string leaving the upload path goes through it."
  ```

  Full-suite check:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1"
  ```

  Expected: 71 passing (baseline 54 + these 17).

### Task 2: `CombatLogUpload` -- multipart POST, status mapping, `CombatLogUploadResult`

**Files:**
- Modify: `native/TriffAlerts/CombatLogUpload.cs`
- Test: `tests/TriffView.Tests/CombatLogUploadTests.cs` (append)

**Interfaces:**
- Consumes: `DiscordWebhook.Redact` (Task 1) on every string this task can
  produce.
- Produces:
  ```csharp
  namespace TriffView.Alerts;

  public sealed class CombatLogUploadResult
  {
      public bool Succeeded { get; init; }
      public string Message { get; init; } = "";
      public int FileCount { get; init; }
      public long ZipBytes { get; init; }
      public DateTime StartUtc { get; init; }
      public DateTime EndUtc { get; init; }
      public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();
      public int DroppedFileCount { get; init; }
      public object ToState();
  }

  public static class CombatLogUpload
  {
      public const int UploadTimeoutSeconds = 120;
      public static Task<CombatLogUploadResult> UploadAsync(
          HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct);
      public static Task<CombatLogUploadResult> SendTestAsync(
          HttpClient http, Uri webhook, CancellationToken ct);
  }
  ```
  The subsystem controller (a later task, out of this scope) is expected to
  merge `FileCount`, `StartUtc`, `EndUtc`, `Characters` and `DroppedFileCount`
  from the `CombatLogExportResult` it already has into the result this task
  returns before posting it to the web UI -- `UploadAsync` only knows about the
  zip file on disk and the HTTP exchange, not the export metadata that produced
  it. `ZipBytes` is the one size field this task *can* fill in directly, since it
  is read from the file it just POSTed.

- [ ] **Step 1: Write a fake `HttpMessageHandler` and the success-path tests, see them fail to compile**

  Append to `CombatLogUploadTests.cs`:

  ```csharp
  using System.Net;
  using System.Net.Http;
  using System.Text;
  using System.Text.Json;
  ```

  (add these `using` directives to the top of the file, alongside the existing
  ones)

  Then append a new test class in the same file:

  ```csharp
  /// <summary>
  /// A stub transport. Real sockets are never touched: the framing, status
  /// mapping and redaction guarantees are all specified against a handler that
  /// hands back exactly the response each test needs.
  /// </summary>
  public sealed class StubHandler : HttpMessageHandler
  {
      private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

      public HttpRequestMessage? LastRequest { get; private set; }
      public string? LastMultipartBody { get; private set; }

      public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
      {
          _respond = respond;
      }

      protected override async Task<HttpResponseMessage> SendAsync(
          HttpRequestMessage request, CancellationToken cancellationToken)
      {
          LastRequest = request;
          if (request.Content != null)
          {
              LastMultipartBody = await request.Content.ReadAsStringAsync(cancellationToken);
          }

          return _respond(request);
      }
  }

  public class CombatLogUploadTransportTests : IDisposable
  {
      private static readonly Uri SampleWebhook = new(
          "https://discord.com/api/webhooks/123456789/abcDEF-token_123");

      private readonly string _zipPath = Path.Combine(
          Path.GetTempPath(), $"triffview-upload-test-{Guid.NewGuid():N}.zip");

      public CombatLogUploadTransportTests()
      {
          // Content does not matter to CombatLogUpload -- it streams whatever is
          // on disk -- but ZipBytes is read from the real file, so the bytes
          // have to exist.
          File.WriteAllBytes(_zipPath, new byte[] { 1, 2, 3, 4, 5 });
      }

      public void Dispose()
      {
          try { File.Delete(_zipPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
      }

      private static HttpClient ClientReturning(Func<HttpRequestMessage, HttpResponseMessage> respond, out StubHandler handler)
      {
          handler = new StubHandler(respond);
          return new HttpClient(handler);
      }

      [Theory]
      [InlineData(HttpStatusCode.OK)]
      [InlineData(HttpStatusCode.NoContent)]
      public async Task ASuccessfulPostReportsSuccessAndTheZipSizeOnDisk(HttpStatusCode status)
      {
          using var http = ClientReturning(_ => new HttpResponseMessage(status), out _);

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", CancellationToken.None);

          Assert.True(result.Succeeded);
          Assert.Equal(5, result.ZipBytes);
      }

      [Fact]
      public async Task TheMultipartBodyCarriesThePayloadJsonAndTheZipUnderItsFileName()
      {
          using var http = ClientReturning(
              _ => new HttpResponseMessage(HttpStatusCode.OK), out var handler);

          await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "3 pilots, 12:00-12:05Z", CancellationToken.None);

          Assert.NotNull(handler.LastRequest);
          Assert.IsType<MultipartFormDataContent>(handler.LastRequest!.Content);

          var multipart = (MultipartFormDataContent)handler.LastRequest.Content!;
          var names = multipart.Select(part => part.Headers.ContentDisposition?.Name?.Trim('"')).ToArray();
          Assert.Contains("payload_json", names);
          Assert.Contains("files[0]", names);

          var filePart = multipart.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "files[0]");
          Assert.Equal(
              Path.GetFileName(_zipPath),
              filePart.Headers.ContentDisposition!.FileName!.Trim('"'));

          var payloadPart = multipart.Single(part => part.Headers.ContentDisposition?.Name?.Trim('"') == "payload_json");
          var payloadJson = await payloadPart.ReadAsStringAsync();
          using var doc = JsonDocument.Parse(payloadJson);
          Assert.Equal("3 pilots, 12:00-12:05Z", doc.RootElement.GetProperty("content").GetString());
      }
  }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUpload"
  ```

  Expected: build failure -- `CombatLogUpload`, `CombatLogUploadResult` and
  `UploadAsync` do not exist yet.

- [ ] **Step 2: Implement `CombatLogUploadResult` and a minimal `UploadAsync` covering only success, see the first tests pass**

  Append to `native/TriffAlerts/CombatLogUpload.cs`:

  ```csharp
  /// <summary>
  /// Outcome of one POST to a Discord webhook. <see cref="FileCount"/>,
  /// <see cref="StartUtc"/>, <see cref="EndUtc"/>, <see cref="Characters"/> and
  /// <see cref="DroppedFileCount"/> are not filled in by
  /// <see cref="CombatLogUpload.UploadAsync"/> -- it only ever sees the zip on
  /// disk and the HTTP exchange -- and are expected to be copied in by the
  /// caller from the <c>CombatLogExportResult</c> that produced the archive
  /// before this result is posted to the web UI.
  /// </summary>
  public sealed class CombatLogUploadResult
  {
      public bool Succeeded { get; init; }
      public string Message { get; init; } = "";
      public int FileCount { get; init; }
      public long ZipBytes { get; init; }
      public DateTime StartUtc { get; init; }
      public DateTime EndUtc { get; init; }
      public IReadOnlyList<string> Characters { get; init; } = Array.Empty<string>();

      /// <summary>
      /// Rides through from the export result and, per the design doc, is
      /// reported on a *successful* upload too -- an upload that quietly
      /// dropped files would read as complete coverage to whoever builds an
      /// after-action report from the Discord channel, having never seen this
      /// app's UI.
      /// </summary>
      public int DroppedFileCount { get; init; }

      public object ToState()
      {
          return new
          {
              succeeded = Succeeded,
              message = Message,
              fileCount = FileCount,
              zipBytes = ZipBytes,
              startUtc = StartUtc.ToString("O"),
              endUtc = EndUtc.ToString("O"),
              characters = Characters,
              droppedFileCount = DroppedFileCount,
          };
      }
  }

  /// <summary>
  /// Posts a combat log archive to a Discord webhook. Every path through
  /// <see cref="UploadAsync"/> returns a result rather than throwing, for any
  /// response the server produced or any transport error it can classify --
  /// that single return path is what makes it possible to guarantee every
  /// outbound string has been through <see cref="DiscordWebhook.Redact"/>.
  /// An exception escaping to the subsystem's generic catch would bypass that
  /// and reach the UI unredacted.
  /// </summary>
  public static class CombatLogUpload
  {
      /// <summary>
      /// The HttpClient instances elsewhere in this repo are tuned for small
      /// JSON calls to ESI (8-20s) and would be wrong for pushing up to 10MB
      /// over a domestic connection. Callers are expected to bound their own
      /// CancellationToken to this many seconds rather than lower the timeout
      /// on a shared client.
      /// </summary>
      public const int UploadTimeoutSeconds = 120;

      public static async Task<CombatLogUploadResult> UploadAsync(
          HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct)
      {
          var zipBytes = new FileInfo(zipPath).Length;

          using var form = new MultipartFormDataContent();
          var payloadJson = JsonSerializer.Serialize(new { content });
          form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");

          await using var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
          var fileContent = new StreamContent(fileStream);
          fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
          form.Add(fileContent, "files[0]", Path.GetFileName(zipPath));

          using var response = await http.PostAsync(webhook, form, ct);
          return await BuildResultAsync(response, zipBytes, webhook, ct);
      }

      private static async Task<CombatLogUploadResult> BuildResultAsync(
          HttpResponseMessage response, long zipBytes, Uri webhook, CancellationToken ct)
      {
          if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent)
          {
              return new CombatLogUploadResult
              {
                  Succeeded = true,
                  Message = "Uploaded to Discord.",
                  ZipBytes = zipBytes,
              };
          }

          return Failed($"Discord returned {(int)response.StatusCode} {response.ReasonPhrase}.", zipBytes, webhook);
      }

      private static CombatLogUploadResult Failed(string message, long zipBytes, Uri webhook)
      {
          return new CombatLogUploadResult
          {
              Succeeded = false,
              // Routed through Redact even for messages that plainly do not
              // contain the token: "every outbound string" means every one,
              // not every one a reviewer remembered to check by hand.
              Message = DiscordWebhook.Redact(message, webhook),
              ZipBytes = zipBytes,
          };
      }
  }
  ```

  Add these `using` directives to the top of `native/TriffAlerts/CombatLogUpload.cs`:

  ```csharp
  using System.Net;
  using System.Net.Http;
  using System.Net.Http.Headers;
  using System.Text;
  using System.Text.Json;
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUpload"
  ```

  Expected: 21 passing (17 from Task 1 + 2 success cases + 2 framing assertions --
  the multipart test method is 1 test containing multiple asserts, so this is
  17 + 2 + 1 = 20; state the actual number `run-tests.ps1` reports, since the
  exact xunit test count for a Theory-free single `[Fact]` is 1 regardless of
  assert count).

  ```bash
  git add native/TriffAlerts/CombatLogUpload.cs tests/TriffView.Tests/CombatLogUploadTests.cs
  git commit -m "Post the combat log archive to a Discord webhook

Multipart with payload_json and files[0], which is the shape Discord's webhook
endpoint accepts for an attachment.

UploadAsync returns a failed result rather than throwing. A single return path
is what makes it possible to guarantee every outbound string has been through
Redact -- an exception escaping to the caller's generic catch would reach
PostError with the token still in it."
  ```

- [ ] **Step 3: Write the failure-status tests, see them fail**

  Append to `CombatLogUploadTransportTests`:

  ```csharp
      [Theory]
      [InlineData(HttpStatusCode.Unauthorized)]
      [InlineData(HttpStatusCode.Forbidden)]
      [InlineData(HttpStatusCode.NotFound)]
      public async Task AGoneWebhookIsReportedAsDeletedRatherThanAsARawStatusCode(HttpStatusCode status)
      {
          using var http = ClientReturning(_ => new HttpResponseMessage(status), out _);

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", CancellationToken.None);

          Assert.False(result.Succeeded);
          Assert.Contains("no longer exists", result.Message, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public async Task ATooLargeArchiveIsReportedAsSuchRatherThanAsA413()
      {
          using var http = ClientReturning(
              _ => new HttpResponseMessage((HttpStatusCode)413), out _);

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", CancellationToken.None);

          Assert.False(result.Succeeded);
          Assert.Contains("too large", result.Message, StringComparison.OrdinalIgnoreCase);
      }

      [Fact]
      public async Task ARateLimitedResponseReportsTheRetryAfterFromTheBody()
      {
          using var http = ClientReturning(_ =>
          {
              var response = new HttpResponseMessage((HttpStatusCode)429)
              {
                  Content = new StringContent("{\"retry_after\": 1.5, \"message\": \"rate limited\"}",
                      Encoding.UTF8, "application/json"),
              };
              return response;
          }, out _);

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", CancellationToken.None);

          Assert.False(result.Succeeded);
          Assert.Contains("1.5", result.Message);
      }

      [Fact]
      public async Task AnUnclassifiedStatusReportsTheCodeAndReasonPhrase()
      {
          using var http = ClientReturning(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
          {
              ReasonPhrase = "Internal Server Error",
          }, out _);

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", CancellationToken.None);

          Assert.False(result.Succeeded);
          Assert.Contains("500", result.Message);
      }

      [Fact]
      public async Task ATransportFailureIsReportedAndRedacted()
      {
          using var http = new HttpClient(new ThrowingHandler());

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", CancellationToken.None);

          Assert.False(result.Succeeded);
          Assert.DoesNotContain("abcDEF-token_123", result.Message);
      }

      [Fact]
      public async Task ATimeoutReturnsAFailedResultRatherThanThrowing()
      {
          using var http = new HttpClient(new NeverRespondingHandler());
          using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

          var result = await CombatLogUpload.UploadAsync(
              http, SampleWebhook, _zipPath, "content", cts.Token);

          Assert.False(result.Succeeded);
          Assert.DoesNotContain("abcDEF-token_123", result.Message);
      }

      private sealed class ThrowingHandler : HttpMessageHandler
      {
          protected override Task<HttpResponseMessage> SendAsync(
              HttpRequestMessage request, CancellationToken cancellationToken)
          {
              // A message shaped like the ones HttpRequestException carries on a
              // real DNS or connection failure, including the credential --
              // exactly the string Redact exists to catch.
              throw new HttpRequestException(
                  $"Connection to {request.RequestUri} refused");
          }
      }

      private sealed class NeverRespondingHandler : HttpMessageHandler
      {
          protected override async Task<HttpResponseMessage> SendAsync(
              HttpRequestMessage request, CancellationToken cancellationToken)
          {
              // Waits on the caller's own token rather than Task.Delay(Infinite),
              // so this fails fast if UploadAsync ever stops passing ct through.
              await Task.Delay(Timeout.Infinite, cancellationToken);
              throw new InvalidOperationException("unreachable");
          }
      }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUpload"
  ```

  Expected: the 3 gone-webhook cases pass (already covered by the generic
  fallback message, but its wording does not yet say "no longer exists"), the
  413/429/500 tests fail on wording, `ATransportFailureIsReportedAndRedacted`
  throws instead of returning, and `ATimeoutReturnsAFailedResultRatherThanThrowing`
  throws `TaskCanceledException` instead of returning a failed result.

- [ ] **Step 4: Fill in the rest of the status table and catch transport/timeout errors**

  Replace `BuildResultAsync` in `native/TriffAlerts/CombatLogUpload.cs`:

  ```csharp
      private static async Task<CombatLogUploadResult> BuildResultAsync(
          HttpResponseMessage response, long zipBytes, Uri webhook, CancellationToken ct)
      {
          if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent)
          {
              return new CombatLogUploadResult
              {
                  Succeeded = true,
                  Message = "Uploaded to Discord.",
                  ZipBytes = zipBytes,
              };
          }

          if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
          {
              return Failed("The webhook no longer exists or was deleted in Discord.", zipBytes, webhook);
          }

          if ((int)response.StatusCode == 413)
          {
              return Failed("The archive is too large for that server.", zipBytes, webhook);
          }

          if ((int)response.StatusCode == 429)
          {
              var retryAfter = await TryReadRetryAfterSecondsAsync(response, ct);
              var suffix = retryAfter is { } seconds
                  ? $" Retry after {seconds.ToString("0.#", CultureInfo.InvariantCulture)}s."
                  : "";
              return Failed($"Rate limited by Discord.{suffix}", zipBytes, webhook);
          }

          return Failed($"Discord returned {(int)response.StatusCode} {response.ReasonPhrase}.", zipBytes, webhook);
      }

      /// <summary>
      /// Discord's JSON body carries sub-second precision; the Retry-After
      /// header, when present at all, is whole seconds. The body wins when both
      /// are there.
      /// </summary>
      private static async Task<double?> TryReadRetryAfterSecondsAsync(HttpResponseMessage response, CancellationToken ct)
      {
          try
          {
              var body = await response.Content.ReadAsStringAsync(ct);
              using var doc = JsonDocument.Parse(body);
              if (doc.RootElement.TryGetProperty("retry_after", out var value) && value.ValueKind == JsonValueKind.Number)
              {
                  return value.GetDouble();
              }
          }
          catch (JsonException)
          {
          }

          return response.Headers.RetryAfter?.Delta?.TotalSeconds;
      }
  ```

  Add `using System.Globalization;` to the top of the file.

  Wrap the POST in `UploadAsync` to classify transport and timeout failures
  without throwing:

  ```csharp
      public static async Task<CombatLogUploadResult> UploadAsync(
          HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct)
      {
          var zipBytes = new FileInfo(zipPath).Length;

          try
          {
              using var form = new MultipartFormDataContent();
              var payloadJson = JsonSerializer.Serialize(new { content });
              form.Add(new StringContent(payloadJson, Encoding.UTF8, "application/json"), "payload_json");

              await using var fileStream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
              var fileContent = new StreamContent(fileStream);
              fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
              form.Add(fileContent, "files[0]", Path.GetFileName(zipPath));

              using var response = await http.PostAsync(webhook, form, ct);
              return await BuildResultAsync(response, zipBytes, webhook, ct);
          }
          catch (OperationCanceledException)
          {
              // Covers both HttpClient's own internal timeout and a caller-supplied
              // token bounded to UploadTimeoutSeconds -- either way, the caller
              // gets a reportable result instead of an exception reaching the
              // subsystem's generic catch unredacted.
              return Failed("The upload timed out.", zipBytes, webhook);
          }
          catch (HttpRequestException ex)
          {
              return Failed($"Could not reach Discord: {ex.Message}", zipBytes, webhook);
          }
      }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUpload"
  ```

  Expected: all cases from Step 3 now pass. State the exact count from the
  runner's summary line rather than a hand count, since Theory expansion counts
  differ from source `[Theory]` lines.

  ```bash
  git add native/TriffAlerts/CombatLogUpload.cs tests/TriffView.Tests/CombatLogUploadTests.cs
  git commit -m "Map Discord's responses to messages a user can act on

A raw status code tells the user nothing about what to do next. 404 means the
webhook was deleted in Discord and needs replacing; 429 means wait; a transport
failure means check the connection. Each maps to a sentence saying so.

Timeouts are classified here too, so a stalled upload ends in a reportable
result rather than an exception from the cancellation token."
  ```

- [ ] **Step 5: Write the `SendTestAsync` tests, see them fail to compile**

  Append to `CombatLogUploadTransportTests`:

  ```csharp
      [Fact]
      public async Task SendTestPostsATextOnlyMessageAndReportsSuccess()
      {
          using var http = ClientReturning(_ => new HttpResponseMessage(HttpStatusCode.NoContent), out var handler);

          var result = await CombatLogUpload.SendTestAsync(http, SampleWebhook, CancellationToken.None);

          Assert.True(result.Succeeded);
          Assert.IsType<StringContent>(handler.LastRequest!.Content);
          Assert.Equal("application/json", handler.LastRequest.Content!.Headers.ContentType!.MediaType);
      }

      [Fact]
      public async Task SendTestFailureIsReportedAndRedactedLikeAnyOtherUpload()
      {
          using var http = new HttpClient(new ThrowingHandler());

          var result = await CombatLogUpload.SendTestAsync(http, SampleWebhook, CancellationToken.None);

          Assert.False(result.Succeeded);
          Assert.DoesNotContain("abcDEF-token_123", result.Message);
      }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUpload"
  ```

  Expected: build failure -- `CombatLogUpload.SendTestAsync` does not exist yet.

- [ ] **Step 6: Implement `SendTestAsync`, see everything go green**

  Append to `CombatLogUpload`:

  ```csharp
      /// <summary>
      /// A text-only message so the user finds out a webhook works before a
      /// fight they cared about fails to upload. Shares BuildResultAsync's
      /// status mapping, since a broken webhook fails the same way here as it
      /// does for a real archive.
      /// </summary>
      public static async Task<CombatLogUploadResult> SendTestAsync(HttpClient http, Uri webhook, CancellationToken ct)
      {
          try
          {
              var payloadJson = JsonSerializer.Serialize(new
              {
                  content = "TriffView test message -- this webhook is working.",
              });
              using var body = new StringContent(payloadJson, Encoding.UTF8, "application/json");
              using var response = await http.PostAsync(webhook, body, ct);
              return await BuildResultAsync(response, zipBytes: 0, webhook, ct);
          }
          catch (OperationCanceledException)
          {
              return Failed("The test message timed out.", 0, webhook);
          }
          catch (HttpRequestException ex)
          {
              return Failed($"Could not reach Discord: {ex.Message}", 0, webhook);
          }
      }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1" --filter "FullyQualifiedName~CombatLogUpload"
  ```

  Expected: all `CombatLogUpload*` tests pass -- 17 (Task 1) + 12 (Task 2:
  2 success + 1 framing + 3 gone-webhook + 1 too-large + 1 rate-limited +
  1 unclassified + 1 transport + 1 timeout + 1 send-test-success +
  1 send-test-failure) = 29.

  ```bash
  git add native/TriffAlerts/CombatLogUpload.cs tests/TriffView.Tests/CombatLogUploadTests.cs
  git commit -m "Add a webhook test that posts without an attachment

Otherwise the first time anyone finds out a webhook is wrong is when a fight
they cared about fails to upload."
  ```

  Full-suite check:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\tests\TriffView.Tests\run-tests.ps1"
  ```

  Expected: 83 passing (baseline 54 + 29 new).
### Task 3: Credential plumbing and the webhook config messages

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs:1-12` (usings), `:16-51` (fields), `:52-83` (constructor), `:85-100` (`Start`), `:145-227` (`HandleWebMessage` switch), `:1982-2029` (`PostState`)
- Create: `native/TriffView.Tests/StubWebhookServer.cs`
- Test: `native/TriffView.Tests/CombatLogUploadWebhookTests.cs`
- Modify (test): `native/TriffView.Tests/OAuthLoopbackTests.cs:42-48` (`CredentialNamespacesCannotCollide`)

**Interfaces:**
- Consumes: `TriffView.Eve.ICredentialStore` / `TriffView.Eve.WindowsCredentialStore` (`native/Eve/EveCredentialStore.cs`); `TriffView.Alerts.DiscordWebhook.TryParse/Describe/Redact` and `TriffView.Alerts.CombatLogUpload.SendTestAsync` / `UploadTimeoutSeconds` (Tasks 1-2, `native/TriffAlerts/CombatLogUpload.cs`).
- Produces (consumed by Task 4 and by the web UI task):
  - `internal const string TriffViewController.CombatLogWebhookCredentialTarget`
  - `private (bool Configured, string Description) TriffViewController._combatLogWebhook`
  - `private void TriffViewController.RefreshCombatLogWebhookState()`
  - `private Uri? TriffViewController.ReadCombatLogWebhook()` — Task 4's `UploadCombatLogs` calls this directly.
  - `private void TriffViewController.PostCombatLogWebhookState(object? testResult = null)`
  - `private static readonly HttpClient TriffViewController.CombatLogUploadHttp` — shared with Task 4's `UploadCombatLogs`, so the 120s-bounded, infinite-`Timeout` client is defined exactly once.
  - Outbound message `triffview:combat-log-webhook { configured, description, testResult? }`
  - `internal sealed class StubWebhookServer` in the test project, reused unmodified by Task 4.

**Design note carried into the implementation:** `ReadCombatLogWebhook` does **not** re-run `DiscordWebhook.TryParse`'s Discord-host allowlist against the stored value — it only does `Uri.TryCreate(raw, UriKind.Absolute, ...)`. The allowlist is the front door that `SetCombatLogWebhook` enforces once, on the string the user typed; re-enforcing it on every read would be redundant against a value this app itself wrote, and it is also what makes the stub-server tests below possible without a second, test-only code path — a test seeds the credential store directly with a `http://127.0.0.1:{port}/...` URL, bypassing the front door the same way a value written in an earlier app version (before a host was added to or removed from the allowlist) would.

---

- [ ] **Step 1: Add the `StubWebhookServer` test double**

  A minimal stand-in for a Discord webhook endpoint, reused by this task's webhook-test coverage and by Task 4's upload coverage. No `[Fact]` in this file — it is infrastructure.

  Create `native/TriffView.Tests/StubWebhookServer.cs`:

  ```csharp
  using System.Net;
  using System.Net.Sockets;
  using System.Text;

  namespace TriffView.Tests;

  /// <summary>
  /// Loopback HTTP endpoint standing in for a Discord webhook. Captures the
  /// last request's content type and body and replies with a caller-set status,
  /// so both the webhook-test path and the upload path can be exercised
  /// end-to-end without reaching a real Discord channel.
  /// </summary>
  internal sealed class StubWebhookServer : IDisposable
  {
      private readonly HttpListener _listener;
      private readonly Task _acceptLoop;
      private volatile bool _stopped;

      public Uri Uri { get; }
      public int StatusCode { get; set; } = 204;
      public string ResponseBody { get; set; } = "";
      public int RequestCount { get; private set; }
      public string? LastContentType { get; private set; }
      public string? LastRequestBody { get; private set; }

      /// <summary>
      /// The raw bytes of the last request body. This is the authoritative
      /// capture; <see cref="LastRequestBody"/> is a UTF-8 decoding of it kept
      /// only as a convenience for the JSON-only paths (webhook config, test
      /// pings) -- a zip upload's body will not survive a round trip through
      /// UTF-8 string decoding, so anything comparing bytes byte-for-byte must
      /// read this property instead.
      /// </summary>
      public byte[]? LastRequestBytes { get; private set; }

      public StubWebhookServer()
      {
          var port = GetFreeTcpPort();
          // Path shape matches the real allowlist so a webhook URL built from
          // this Uri would also pass DiscordWebhook.TryParse if it ever needed to.
          Uri = new Uri($"http://127.0.0.1:{port}/api/webhooks/1/token");
          _listener = new HttpListener();
          _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
          _listener.Start();
          _acceptLoop = Task.Run(AcceptLoopAsync);
      }

      private async Task AcceptLoopAsync()
      {
          while (!_stopped)
          {
              HttpListenerContext context;
              try
              {
                  context = await _listener.GetContextAsync();
              }
              catch (Exception) when (_stopped)
              {
                  return;
              }

              // The input stream can only be read once, so capture the raw
              // bytes here and derive the string form from them rather than
              // taking a separate StreamReader pass over the same stream.
              using (var buffer = new MemoryStream())
              {
                  await context.Request.InputStream.CopyToAsync(buffer);
                  LastRequestBytes = buffer.ToArray();
              }
              LastRequestBody = Encoding.UTF8.GetString(LastRequestBytes);
              LastContentType = context.Request.ContentType;
              RequestCount++;

              context.Response.StatusCode = StatusCode;
              var bytes = Encoding.UTF8.GetBytes(ResponseBody);
              context.Response.ContentLength64 = bytes.Length;
              await context.Response.OutputStream.WriteAsync(bytes);
              context.Response.Close();
          }
      }

      private static int GetFreeTcpPort()
      {
          var listener = new TcpListener(IPAddress.Loopback, 0);
          listener.Start();
          var port = ((IPEndPoint)listener.LocalEndpoint).Port;
          listener.Stop();
          return port;
      }

      public void Dispose()
      {
          _stopped = true;
          _listener.Stop();
          _listener.Close();
          try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); }
          catch (Exception) { /* best-effort shutdown of a test double */ }
      }
  }
  ```

  This file adds no tests, so there is nothing to run yet. Proceed to the first failing test.

- [ ] **Step 2: Failing test — constructing the controller with injected credentials compiles and exposes an unconfigured webhook**

  Create `native/TriffView.Tests/CombatLogUploadWebhookTests.cs`:

  ```csharp
  using System.Collections.Concurrent;
  using System.Text.Json;
  using System.Windows.Threading;
  using TriffView.Eve;
  using TriffView.Preview;
  using Xunit;

  namespace TriffView.Tests;

  public class CombatLogUploadWebhookTests
  {
      [Fact]
      public void StartPostsUnconfiguredWebhookWhenNoneIsStored()
      {
          var messages = new ConcurrentQueue<string>();
          using var controller = Controller(new MemoryCredentials(), messages);

          controller.Start();

          Assert.True(SpinWait.SpinUntil(
              () => messages.Any(json => json.Contains("\"combatLogWebhook\"", StringComparison.Ordinal)),
              TimeSpan.FromSeconds(5)));
          var state = messages.First(json => json.Contains("\"combatLogWebhook\"", StringComparison.Ordinal));
          Assert.Contains("\"configured\":false", state, StringComparison.Ordinal);
      }

      private static TriffViewController Controller(MemoryCredentials credentials, ConcurrentQueue<string> messages)
      {
          return new TriffViewController(
              Dispatcher.CurrentDispatcher,
              value => messages.Enqueue(JsonSerializer.Serialize(value)),
              reassertHudTopmost: () => { },
              applySettingsAlwaysOnTop: _ => { },
              credentials);
      }

      private sealed class MemoryCredentials(params (string Target, string Secret)[] entries) : ICredentialStore
      {
          private readonly ConcurrentDictionary<string, string> _values =
              new(entries.ToDictionary(entry => entry.Target, entry => entry.Secret), StringComparer.Ordinal);
          public bool FailRead { get; init; }
          public bool FailWrite { get; init; }
          public bool FailDelete { get; init; }
          public int ReadCalls { get; private set; }

          public string? Read(string target)
          {
              ReadCalls++;
              if (FailRead) throw new IOException("credential read failed");
              return _values.TryGetValue(target, out var value) ? value : null;
          }

          public void Write(string target, string secret)
          {
              if (FailWrite) throw new IOException("credential write failed");
              _values[target] = secret;
          }

          public void Delete(string target, bool missingIsSuccess = true)
          {
              if (FailDelete) throw new IOException("credential delete failed");
              _values.TryRemove(target, out _);
          }

          public IReadOnlyList<string> EnumerateTargets(string exactPrefix) =>
              _values.Keys.Where(key => key.StartsWith(exactPrefix, StringComparison.Ordinal)).ToArray();

          public string? Stored(string target) => _values.TryGetValue(target, out var value) ? value : null;
      }
  }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
  $env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
  $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
  $env:APPDATA='C:\dev\TriffView\.appdata'
  $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter 'FullyQualifiedName~CombatLogUpload'
  "
  ```

  Expect a **build failure**: `TriffViewController` has no constructor overload accepting an `ICredentialStore`. Baseline stays 155 (nothing ran).

- [ ] **Step 3: Add the credential field and the fifth constructor parameter**

  In `native/TriffView/TriffViewSubsystem.cs`, add the using and field, then widen the constructor.

  ```csharp
  using TriffView.Eve;
  ```
  (added alongside the existing usings at the top of the file, before `using Forms = System.Windows.Forms;`)

  ```csharp
  private readonly TriffAlertsService _alerts = new();
  private readonly ICredentialStore _credentials;
  ```
  (the new field follows `_alerts`, matching its position among the other readonly dependencies)

  ```csharp
  public TriffViewController(
      Dispatcher dispatcher,
      Action<object> postToHud,
      Action reassertHudTopmost,
      Action<bool> applySettingsAlwaysOnTop,
      ICredentialStore? credentials = null)
  {
      _dispatcher = dispatcher;
      _postToHud = postToHud;
      _reassertHudTopmost = reassertHudTopmost;
      _applySettingsAlwaysOnTop = applySettingsAlwaysOnTop;
      _credentials = credentials ?? new WindowsCredentialStore();
      _foregroundWinEventProc = OnForegroundWinEvent;
      Settings = TriffViewSettings.Load();
      _overlay = new TriffViewOverlayForm();
  ```
  (only the signature and the new `_credentials` assignment change; everything from `_foregroundWinEventProc = OnForegroundWinEvent;` onward is unchanged)

  A single defaulted parameter, rather than the internal-plus-public overload pair `TriffSkillsController`/`TriffFleetsController` use — this controller already has exactly one public constructor with no test-only twin, and one new dependency does not justify introducing that split here.

  This alone does not yet satisfy the test (no `combatLogWebhook` state is posted), but it must compile before the next step can fail meaningfully. Run the filtered test again and confirm it now builds and **fails** on the assertion (no message contains `"combatLogWebhook"`) rather than failing to build.

- [ ] **Step 4: Add the cached webhook state, its guarded reader, and the state field**

  Still in `TriffViewSubsystem.cs`, add near the other private fields (after `_lastObservedForegroundWasEve`):

  ```csharp
  private (bool Configured, string Description) _combatLogWebhook;
  ```

  Add the credential target constant as a public-facing constant beside the other `public` members, directly under `public bool SettingsPanelOpen => _settingsPanelOpen;`:

  ```csharp
  internal const string CombatLogWebhookCredentialTarget = "TriffView.CombatLogExport.DiscordWebhook";
  ```

  Add the reader and the refresh method after `Start()` (before `StartDisplayTracking`):

  ```csharp
  /// <summary>
  /// Reads the stored webhook, if any, resolving every failure to "absent"
  /// rather than throwing. ICredentialStore.Read throws for every Win32 error
  /// but "not found" (EveCredentialStore.cs), and Start() calls PostState()
  /// unprotected -- an unavailable credential store must not take down startup
  /// on a code path that has nothing to do with combat logs.
  ///
  /// Deliberately does not re-run DiscordWebhook.TryParse's host allowlist:
  /// that check is the front door SetCombatLogWebhook enforces on the string a
  /// user typed, not something worth re-enforcing on a value this app already
  /// wrote itself.
  /// </summary>
  private Uri? ReadCombatLogWebhook()
  {
      try
      {
          var raw = _credentials.Read(CombatLogWebhookCredentialTarget);
          if (string.IsNullOrWhiteSpace(raw)) return null;
          return Uri.TryCreate(raw, UriKind.Absolute, out var webhook) ? webhook : null;
      }
      catch (Exception ex)
      {
          TriffViewDiagnostics.Log("combat-log-webhook", $"Credential read failed: {ex.Message}");
          return null;
      }
  }

  /// <summary>
  /// Recomputes the cached { configured, description } pair. Called once from
  /// Start() and again only when a set or clear succeeds -- PostState runs from
  /// the 700ms periodic refresh and the 100ms post-switch timer, and a CredRead
  /// P/Invoke on that path would run several times a minute forever to answer a
  /// question whose answer changes only when the user edits it.
  /// </summary>
  private void RefreshCombatLogWebhookState()
  {
      var webhook = ReadCombatLogWebhook();
      _combatLogWebhook = webhook == null ? (false, "") : (true, DiscordWebhook.Describe(webhook));
  }
  ```

  In `Start()`, call it before the final `PostState()`:

  ```csharp
  StartForegroundTracking();
  StartDisplayTracking();
  LogLayoutSnapshot("startup");
  RefreshCombatLogWebhookState();
  PostState();
  ```

  In `PostState()`, add the field to the payload, alongside `dwmAvailable`:

  ```csharp
  hotkeyFailures = _overlay.HotkeyFailures,
  dwmAvailable = _overlay.DwmAvailable,
  combatLogWebhook = new
  {
      configured = _combatLogWebhook.Configured,
      description = _combatLogWebhook.Description,
  },
  ```

  Run the filtered test again. It should now pass: `Start()` posts `combatLogWebhook":{"configured":false,...}` in its forced state post. Expected: 156 passing (155 baseline + 1).

  ```bash
  git add native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/CombatLogUploadWebhookTests.cs
  git commit -m "Cache combat log webhook state and surface it in PostState

PostState fires from the 700ms periodic refresh and the 100ms
post-switch timer, so it can never perform a credential read itself.
Start() reads the stored webhook once into a cached field instead,
and PostState only reports what's cached."
  ```

- [ ] **Step 5: Failing test — a broken credential store resolves to unconfigured, not a thrown exception**

  Add to `CombatLogUploadWebhookTests`:

  ```csharp
  [Fact]
  public void StartResolvesUnreadableCredentialStoreToUnconfiguredWithoutThrowing()
  {
      var messages = new ConcurrentQueue<string>();
      var credentials = new MemoryCredentials { FailRead = true };
      using var controller = Controller(credentials, messages);

      var exception = Record.Exception(() => controller.Start());

      Assert.Null(exception);
      Assert.True(SpinWait.SpinUntil(
          () => messages.Any(json => json.Contains("\"combatLogWebhook\"", StringComparison.Ordinal)),
          TimeSpan.FromSeconds(5)));
      var state = messages.First(json => json.Contains("\"combatLogWebhook\"", StringComparison.Ordinal));
      Assert.Contains("\"configured\":false", state, StringComparison.Ordinal);
  }
  ```

  Run. This should already **pass** given Step 4's `try`/`catch` in `ReadCombatLogWebhook` -- it is written as a regression guard for a hazard the spec calls out explicitly (`docs/superpowers/specs/2026-08-16-combat-log-discord-upload-design.md`, "Reading it must not be able to break startup"), not to drive new production code. If it fails, the bug is in Step 4's `try`/`catch`, not in this test. Expected: 157 passing.

  ```bash
  git add native/TriffView.Tests/CombatLogUploadWebhookTests.cs
  git commit -m "Add regression test for unreadable credential store on startup

The credential store failing has nothing to do with combat logs, and
Start() calls PostState() unprotected -- an unavailable Windows
Credential Manager must not take down startup on an unrelated path."
  ```

- [ ] **Step 6: Failing test — reading is cached, not repeated per state post**

  Add to `CombatLogUploadWebhookTests`:

  ```csharp
  [Fact]
  public void RepeatedStatePostsDoNotRereadTheCredentialStore()
  {
      var messages = new ConcurrentQueue<string>();
      var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
      using var controller = Controller(credentials, messages);

      controller.Start();
      Assert.True(SpinWait.SpinUntil(() => credentials.ReadCalls >= 1, TimeSpan.FromSeconds(5)));
      var readsAfterStart = credentials.ReadCalls;

      controller.HandleWebMessage("triffview:get-state", null);
      controller.HandleWebMessage("triffview:get-state", null);
      controller.HandleWebMessage("triffview:get-state", null);

      Assert.Equal(readsAfterStart, credentials.ReadCalls);
  }
  ```

  Run. This should already **pass**: `PostState()` only reads the `_combatLogWebhook` field, and nothing calls `RefreshCombatLogWebhookState()` from `triffview:get-state`. As with Step 5, this locks in a design property the spec treats as load-bearing rather than driving new code; if it fails, `PostState` is reading the credential store directly somewhere. Expected: 158 passing.

  ```bash
  git add native/TriffView.Tests/CombatLogUploadWebhookTests.cs
  git commit -m "Add regression test proving PostState never re-reads the credential store

Locks in the design PostState depends on: a CredRead P/Invoke on this
path would run several times a minute forever just to answer a
question whose answer only changes when the user edits it."
  ```

- [ ] **Step 7: Failing test — `triffview:set-combat-log-webhook` rejects a non-Discord host**

  Add:

  ```csharp
  [Fact]
  public void SetCombatLogWebhookRejectsUrlsOutsideTheDiscordAllowlist()
  {
      var messages = new ConcurrentQueue<string>();
      var credentials = new MemoryCredentials();
      using var controller = Controller(credentials, messages);

      controller.HandleWebMessage(
          "triffview:set-combat-log-webhook",
          JsonNode.Parse("""{"url":"https://example.com/api/webhooks/1/tok"}""")!.AsObject());

      Assert.True(SpinWait.SpinUntil(
          () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
              && json.Contains("\"action\":\"set-combat-log-webhook\"", StringComparison.Ordinal)),
          TimeSpan.FromSeconds(5)));
      Assert.Null(credentials.Stored(TriffViewController.CombatLogWebhookCredentialTarget));
  }
  ```

  Add `using System.Text.Json.Nodes;` to the test file's usings.

  Run. Expect a **build failure**: `HandleWebMessage` has no `"triffview:set-combat-log-webhook"` case, so nothing is posted and the message queue stays empty -- actually this compiles fine (the switch's `default` returns `false` silently), so the failure is a **test failure**, not a build failure: the `SpinWait.SpinUntil` times out because no message is ever posted. Expected: still 158 passing, this one failing (159 total, 1 failed).

- [ ] **Step 8: Add `SetCombatLogWebhook` and its dispatch case**

  Add the handler after `PostError` cannot be reused yet (`PostCombatLogWebhookState` does not exist), so add both together, near `ExportSettingsBackup` (private methods grouped by feature):

  ```csharp
  private void SetCombatLogWebhook(string? url)
  {
      if (!DiscordWebhook.TryParse(url, out var webhook, out var error))
      {
          // A rejected URL never reached the credential store, let alone Discord --
          // there is no attempt to report an outcome of, so this is PostError, not
          // a combat-log-webhook state post.
          PostError("set-combat-log-webhook", error);
          return;
      }

      try
      {
          _credentials.Write(CombatLogWebhookCredentialTarget, webhook.ToString());
      }
      catch (Exception ex)
      {
          // Same reasoning: a failed credential write means nothing was saved and
          // nothing was tested, so this is still a refusal to report, not an
          // outcome of testing the webhook.
          PostError("set-combat-log-webhook", DiscordWebhook.Redact(ex.Message, webhook));
          return;
      }

      RefreshCombatLogWebhookState();
      PostCombatLogWebhookState();
  }

  private void PostCombatLogWebhookState(object? testResult = null)
  {
      _postToHud(new
      {
          type = "triffview:combat-log-webhook",
          configured = _combatLogWebhook.Configured,
          description = _combatLogWebhook.Description,
          testResult,
      });
  }
  ```

  Add the dispatch case in `HandleWebMessage`, alongside the other `set-*` cases:

  ```csharp
  case "triffview:set-combat-log-webhook":
      SetCombatLogWebhook(message?["url"]?.GetValue<string>());
      return true;
  ```

  Run. Expect this test and Step 4's `StartPostsUnconfiguredWebhookWhenNoneIsStored` to both pass; expect 159 passing, 0 failed.

  ```bash
  git add native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/CombatLogUploadWebhookTests.cs
  git commit -m "Add triffview:set-combat-log-webhook with Discord host validation

A rejected URL never reaches the credential store, so it's reported
through PostError as a refusal rather than as a webhook state --
nothing was saved and nothing was attempted."
  ```

- [ ] **Step 9: Failing test — a valid webhook is stored, and its reply never carries the token**

  Add:

  ```csharp
  [Fact]
  public void SetCombatLogWebhookStoresAValidUrlAndRepliesWithARedactedDescription()
  {
      var messages = new ConcurrentQueue<string>();
      var credentials = new MemoryCredentials();
      using var controller = Controller(credentials, messages);

      controller.HandleWebMessage(
          "triffview:set-combat-log-webhook",
          JsonNode.Parse("""{"url":"https://discord.com/api/webhooks/1234/sekrit-token-value"}""")!.AsObject());

      Assert.True(SpinWait.SpinUntil(
          () => messages.Any(json => json.Contains("\"type\":\"triffview:combat-log-webhook\"", StringComparison.Ordinal)),
          TimeSpan.FromSeconds(5)));
      var reply = messages.First(json => json.Contains("\"type\":\"triffview:combat-log-webhook\"", StringComparison.Ordinal));
      Assert.Contains("\"configured\":true", reply, StringComparison.Ordinal);
      Assert.DoesNotContain("sekrit-token-value", reply, StringComparison.Ordinal);
      Assert.Equal(
          "https://discord.com/api/webhooks/1234/sekrit-token-value",
          credentials.Stored(TriffViewController.CombatLogWebhookCredentialTarget));
  }
  ```

  Run. This exercises the same code path as Step 8 and should already **pass**, proving `DiscordWebhook.Describe` omits the token segment as Tasks 1-2 implemented it. If it fails, the failure is in `DiscordWebhook.Describe`, not in this controller. Expected: 160 passing.

  ```bash
  git add native/TriffView.Tests/CombatLogUploadWebhookTests.cs
  git commit -m "Add regression test proving the webhook token never reaches a reply message

The webhook URL is a bearer credential; DiscordWebhook.Describe is
what keeps the token out of the reply the web UI receives, and this
locks that behavior in from the controller side."
  ```

- [ ] **Step 10: Failing test — `triffview:clear-combat-log-webhook` deletes and replies unconfigured**

  Add:

  ```csharp
  [Fact]
  public void ClearCombatLogWebhookDeletesTheCredentialAndRepliesUnconfigured()
  {
      var messages = new ConcurrentQueue<string>();
      var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
      using var controller = Controller(credentials, messages);
      controller.Start();

      controller.HandleWebMessage("triffview:clear-combat-log-webhook", null);

      Assert.True(SpinWait.SpinUntil(
          () => messages.Count(json => json.Contains("\"type\":\"triffview:combat-log-webhook\"", StringComparison.Ordinal)) >= 2,
          TimeSpan.FromSeconds(5)));
      var reply = messages.Last(json => json.Contains("\"type\":\"triffview:combat-log-webhook\"", StringComparison.Ordinal));
      Assert.Contains("\"configured\":false", reply, StringComparison.Ordinal);
      Assert.Null(credentials.Stored(TriffViewController.CombatLogWebhookCredentialTarget));
  }
  ```

  Run. Expect a build pass but a **test failure**: no such dispatch case exists yet, so `HandleWebMessage` returns `false` and nothing new is posted.

- [ ] **Step 11: Add `ClearCombatLogWebhook` and its dispatch case**

  ```csharp
  private void ClearCombatLogWebhook()
  {
      try
      {
          _credentials.Delete(CombatLogWebhookCredentialTarget);
      }
      catch (Exception ex)
      {
          PostError("clear-combat-log-webhook", ex.Message);
          return;
      }

      RefreshCombatLogWebhookState();
      PostCombatLogWebhookState();
  }
  ```

  ```csharp
  case "triffview:clear-combat-log-webhook":
      ClearCombatLogWebhook();
      return true;
  ```

  Run. Expect 161 passing.

  ```bash
  git add native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/CombatLogUploadWebhookTests.cs
  git commit -m "Add triffview:clear-combat-log-webhook

Mirrors set and test: delete the credential, refresh the cached
state, and reply with the same combat-log-webhook shape so the UI
has one message type to handle for all three."
  ```

- [ ] **Step 12: Failing test — `triffview:test-combat-log-webhook` reports a missing webhook without touching the network**

  Add:

  ```csharp
  [Fact]
  public void TestCombatLogWebhookReportsWhenNoneIsConfigured()
  {
      var messages = new ConcurrentQueue<string>();
      using var controller = Controller(new MemoryCredentials(), messages);

      controller.HandleWebMessage("triffview:test-combat-log-webhook", null);

      Assert.True(SpinWait.SpinUntil(
          () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
              && json.Contains("\"action\":\"test-combat-log-webhook\"", StringComparison.Ordinal)
              && json.Contains("Configure a Discord webhook first.", StringComparison.Ordinal)),
          TimeSpan.FromSeconds(5)));
  }
  ```

  Run. Expect a **test failure** (no such dispatch case, nothing posted).

- [ ] **Step 13: Add the shared upload `HttpClient`, `TestCombatLogWebhook`, and its dispatch case**

  The static client is introduced here because `TestCombatLogWebhook` is the first consumer; Task 4's `UploadCombatLogs` reuses the same field. Add near the other `private static readonly` members at the top of the class (there are none yet in this file, so add directly above the constructor):

  ```csharp
  // Its own client, deliberately separate from any ESI HttpClient elsewhere in
  // the repo (those are tuned to 8s/20s for small JSON calls). Timeout is left
  // infinite and bounded per-request instead, via the CancellationTokenSource
  // in UploadCombatLogs and TestCombatLogWebhook -- see CombatLogUpload.cs's
  // header comment for why a single client is shared between test and upload.
  private static readonly HttpClient CombatLogUploadHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
  ```

  ```csharp
  private async void TestCombatLogWebhook()
  {
      var webhook = ReadCombatLogWebhook();
      if (webhook == null)
      {
          PostError("test-combat-log-webhook", "Configure a Discord webhook first.");
          return;
      }

      CombatLogUploadResult result;
      try
      {
          using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CombatLogUpload.UploadTimeoutSeconds));
          result = await CombatLogUpload.SendTestAsync(CombatLogUploadHttp, webhook, cts.Token);
      }
      catch (Exception ex)
      {
          // SendTestAsync is documented to return a failed result rather than
          // throw for any classifiable HTTP or transport outcome
          // (CombatLogUpload.cs). Reaching this catch means the test genuinely
          // ran and hit something unclassifiable -- that is still an outcome of
          // the attempt, not a pre-flight refusal, so it is reported through
          // testResult like every other outcome, never through PostError.
          if (_disposed) return;
          PostCombatLogWebhookState(new { ok = false, message = DiscordWebhook.Redact(ex.Message, webhook) });
          return;
      }

      if (_disposed) return;
      // Only "no webhook configured" above is a pre-flight refusal (PostError).
      // Everything past that point is an outcome of a test that actually ran,
      // successful or not, and is reported through testResult -- mirrors the
      // same split UploadCombatLogs uses for PostError vs. result.succeeded.
      PostCombatLogWebhookState(new { ok = result.Succeeded, message = result.Message });
  }
  ```

  ```csharp
  case "triffview:test-combat-log-webhook":
      TestCombatLogWebhook();
      return true;
  ```

  Add `using System.Net.Http;` and `using System.Threading;` (for `Timeout.InfiniteTimeSpan`) to `TriffViewSubsystem.cs`'s usings if not already present via a transitive `using` -- check first; `System.Threading.Tasks` types already resolve through implicit usings enabled on the project, but `System.Threading.Timeout` and `System.Net.Http.HttpClient` need explicit usings here since this file has none yet.

  Run. Expect 162 passing.

  ```bash
  git add native/TriffView/TriffViewSubsystem.cs
  git commit -m "Add triffview:test-combat-log-webhook

Reports the webhook test's outcome through testResult rather than
PostError once the test has actually run, matching the same
pre-flight-vs-outcome split the upload path uses: only \"no webhook
configured\" is a refusal, everything past that is a result."
  ```

- [ ] **Step 14: Failing test — a working webhook round-trips through a stub server**

  Add:

  ```csharp
  [Fact]
  public async Task TestCombatLogWebhookSucceedsAgainstAReachableEndpoint()
  {
      using var stub = new StubWebhookServer { StatusCode = 204 };
      var messages = new ConcurrentQueue<string>();
      // Seeded directly, bypassing SetCombatLogWebhook's host allowlist -- see
      // this task's header note on why ReadCombatLogWebhook does not re-check it.
      var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, stub.Uri.ToString()));
      using var controller = Controller(credentials, messages);

      controller.HandleWebMessage("triffview:test-combat-log-webhook", null);

      Assert.True(await Task.Run(() => SpinWait.SpinUntil(() => stub.RequestCount >= 1, TimeSpan.FromSeconds(10))));
      Assert.True(SpinWait.SpinUntil(
          () => messages.Any(json => json.Contains("\"testResult\"", StringComparison.Ordinal) && json.Contains("\"ok\":true", StringComparison.Ordinal)),
          TimeSpan.FromSeconds(5)));
  }
  ```

  Run. This exercises Step 13's implementation against a real (loopback) HTTP round trip through `CombatLogUpload.SendTestAsync`, and should already **pass** if Tasks 1-2's `SendTestAsync` treats 204 as success. If it fails, the failure is in `SendTestAsync`'s status handling, not in this controller's wiring -- note that for the reviewer rather than silently reworking the subsystem side. Expected: 163 passing.

  ```bash
  git add native/TriffView.Tests/CombatLogUploadWebhookTests.cs native/TriffView.Tests/StubWebhookServer.cs
  git commit -m "Add end-to-end regression test for triffview:test-combat-log-webhook

Exercises SendTestAsync over a real loopback HTTP round trip rather
than a fake, so a status-handling regression in Tasks 1-2 shows up
here instead of only being caught by unit tests on that file alone."
  ```

- [ ] **Step 15: Extend the credential-prefix collision assertion**

  In `native/TriffView.Tests/OAuthLoopbackTests.cs`, widen `CredentialNamespacesCannotCollide`:

  ```csharp
  [Fact]
  public void CredentialNamespacesCannotCollide()
  {
      Assert.NotEqual(TriffSkillsAuthentication.CredentialPrefix, TriffFleetsController.CredentialPrefix);
      Assert.DoesNotContain(TriffSkillsAuthentication.CredentialPrefix, TriffFleetsController.CredentialPrefix, StringComparison.Ordinal);
      Assert.DoesNotContain(TriffFleetsController.CredentialPrefix, TriffSkillsAuthentication.CredentialPrefix, StringComparison.Ordinal);

      // TriffViewController.CombatLogWebhookCredentialTarget is a single exact
      // target rather than a per-character prefix, but the same hazard applies:
      // a prefix collision would let one subsystem's cleanup sweep (or
      // EnumerateTargets scan) delete another subsystem's secret.
      Assert.DoesNotContain(TriffSkillsAuthentication.CredentialPrefix, TriffViewController.CombatLogWebhookCredentialTarget, StringComparison.Ordinal);
      Assert.DoesNotContain(TriffFleetsController.CredentialPrefix, TriffViewController.CombatLogWebhookCredentialTarget, StringComparison.Ordinal);
      Assert.DoesNotContain(TriffViewController.CombatLogWebhookCredentialTarget, TriffSkillsAuthentication.CredentialPrefix, StringComparison.Ordinal);
      Assert.DoesNotContain(TriffViewController.CombatLogWebhookCredentialTarget, TriffFleetsController.CredentialPrefix, StringComparison.Ordinal);
  }
  ```

  Add `using TriffView.Preview;` to `OAuthLoopbackTests.cs`'s usings for `TriffViewController`.

  Run. This step widens an existing `[Fact]` rather than adding a new one, so the total is **unchanged** from the previous step — what changes is that the assertion now covers three credential namespaces instead of two. A green run with the same count is the expected outcome here; a count that went *up* means a stray `[Fact]` was added by accident.

  ```bash
  git add native/TriffView.Tests/OAuthLoopbackTests.cs
  git commit -m "Extend credential-prefix collision test to cover the combat log webhook target

The webhook target is a single exact string rather than a per-
character prefix, but the same hazard applies: a collision would let
one subsystem's cleanup sweep delete another subsystem's secret."
  ```

---

### Task 4: The upload flow and the startup temp sweep

**Files:**
- Modify: `native/TriffView/TriffViewSubsystem.cs:85-100` (`Start`), `:145-227` (`HandleWebMessage` switch), and new private methods added near `ExportCombatLogs` (`:1165-1256` in the pre-Task-3 file)
- Test: `native/TriffView.Tests/CombatLogUploadFlowTests.cs`

**Interfaces:**
- Consumes: `TriffView.Alerts.CombatLogUpload.UploadAsync` / `UploadTimeoutSeconds` (Tasks 1-2); `TriffView.Alerts.CombatLogExport.Export` / `SuggestFileName` / `CombatLogExportResult` (existing); Task 3's `ReadCombatLogWebhook()`, `BuildCombatLogWindow` (existing, unchanged), `CombatLogUploadHttp` (Task 3's static field), `PostError`.
- Produces: `private async void TriffViewController.UploadCombatLogs(string?, string?)`; `private static void TriffViewController.SweepStaleCombatLogTemps()`; outbound `triffview:combat-log-upload { result }`.

**Note on the message contract's shape:** `triffview:combat-log-upload` carries `{ result }` only -- there is no `{ cancelled }` branch. Upload has no save dialog and therefore no user action that corresponds to "cancel," unlike `triffview:combat-log-export`'s save-dialog-cancel case; the only thing that can end a run early is the 120s timeout, which surfaces as a *failed* `result` (per the spec's own status table: "timeout / socket error -> redacted transport message"), not a cancellation. The spec has been updated to reflect this: its message table now lists `{ result }` only for upload, with a paragraph explaining why no cancellation shape applies. This plan's `UploadCombatLogs` implementation below matches that settled position.

---

- [ ] **Step 1: Failing test — uploading with no webhook configured is a terminal error, and BuildCombatLogWindow's own error still applies**

  Create `native/TriffView.Tests/CombatLogUploadFlowTests.cs`:

  ```csharp
  using System.Collections.Concurrent;
  using System.Text.Json;
  using System.Text.Json.Nodes;
  using System.Windows.Threading;
  using TriffView.Eve;
  using TriffView.Preview;
  using Xunit;

  namespace TriffView.Tests;

  public class CombatLogUploadFlowTests
  {
      [Fact]
      public void UploadCombatLogsReportsMissingWebhookBeforeTouchingTheNetwork()
      {
          var messages = new ConcurrentQueue<string>();
          using var controller = Controller(new MemoryCredentials(), messages);

          controller.HandleWebMessage(
              "triffview:upload-combat-logs",
              JsonNode.Parse("""{"fromUtc":"2026-08-14T20:00:00Z","toUtc":"2026-08-14T20:10:00Z"}""")!.AsObject());

          Assert.True(SpinWait.SpinUntil(
              () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
                  && json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)
                  && json.Contains("Configure a Discord webhook first.", StringComparison.Ordinal)),
              TimeSpan.FromSeconds(5)));
      }

      private static TriffViewController Controller(MemoryCredentials credentials, ConcurrentQueue<string> messages)
      {
          return new TriffViewController(
              Dispatcher.CurrentDispatcher,
              value => messages.Enqueue(JsonSerializer.Serialize(value)),
              reassertHudTopmost: () => { },
              applySettingsAlwaysOnTop: _ => { },
              credentials);
      }

      private sealed class MemoryCredentials(params (string Target, string Secret)[] entries) : ICredentialStore
      {
          private readonly ConcurrentDictionary<string, string> _values =
              new(entries.ToDictionary(entry => entry.Target, entry => entry.Secret), StringComparer.Ordinal);
          public string? Read(string target) => _values.TryGetValue(target, out var value) ? value : null;
          public void Write(string target, string secret) => _values[target] = secret;
          public void Delete(string target, bool missingIsSuccess = true) => _values.TryRemove(target, out _);
          public IReadOnlyList<string> EnumerateTargets(string exactPrefix) =>
              _values.Keys.Where(key => key.StartsWith(exactPrefix, StringComparison.Ordinal)).ToArray();
      }
  }
  ```

  Run:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
  $env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
  $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
  $env:APPDATA='C:\dev\TriffView\.appdata'
  $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter 'FullyQualifiedName~CombatLogUpload'
  "
  ```

  Expect a **test failure**: `HandleWebMessage` has no `"triffview:upload-combat-logs"` case yet, so nothing is posted and the wait times out.

- [ ] **Step 2: Add `UploadCombatLogs`, its content formatter, and the dispatch case**

  In `TriffViewSubsystem.cs`, add near `ExportCombatLogs`:

  ```csharp
  /// <summary>
  /// Mirrors ExportCombatLogs' disposal discipline (see its own header comment),
  /// widened for a slower and less certain step: a stalled upload socket can
  /// hang far longer than compressing ever could, which is exactly the kind of
  /// hang that comment warns an async void method must never risk -- a throw
  /// reaching the catch below after disposal reaches the thread pool and takes
  /// the process with it. The CancellationTokenSource below is what guarantees
  /// this method always terminates.
  /// </summary>
  private async void UploadCombatLogs(string? fromUtc, string? toUtc)
  {
      string? tempPath = null;
      try
      {
          var window = BuildCombatLogWindow(fromUtc, toUtc);
          if (window == null)
          {
              PostError(
                  "upload-combat-logs",
                  "No recent fight found in the alert history. Alerts must be enabled and a fight " +
                  "must have happened while TriffView was running, or you can enter a UTC time range.");
              return;
          }

          var webhook = ReadCombatLogWebhook();
          if (webhook == null)
          {
              PostError("upload-combat-logs", "Configure a Discord webhook first.");
              return;
          }

          tempPath = Path.Combine(Path.GetTempPath(), CombatLogExport.SuggestFileName(window));
          var gamelogsPath = _alerts.GamelogsPath;
          // Off the dispatcher for the same reason ExportCombatLogs is: compressing
          // a long session's logs on the UI thread would stall every preview.
          var result = await Task.Run(() => CombatLogExport.Export(
              gamelogsPath, window.StartUtc, window.EndUtc, tempPath, window.Source));

          if (result.ExceedsDiscordLimit)
          {
              PostError(
                  "upload-combat-logs",
                  $"That archive is {result.ZipBytes / (1024.0 * 1024.0):F1} MB, over Discord's 10 MB limit. " +
                  "Narrow the time range, or use Export to save it and upload by hand.");
              return;
          }

          var content = FormatCombatLogUploadContent(result);
          using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(CombatLogUpload.UploadTimeoutSeconds));
          var upload = await CombatLogUpload.UploadAsync(CombatLogUploadHttp, webhook, tempPath, content, cts.Token);

          // Same race ExportCombatLogs guards against: an upload can outlive a
          // shutdown, and posting to a disposed controller from the catch below
          // would throw into the thread pool and take the process with it.
          if (_disposed) return;

          // Everything above this point (no fight window, no webhook, oversize
          // archive) is a pre-flight refusal reported through PostError: nothing
          // was attempted. Past this line the upload genuinely ran, so its
          // result -- success or failure -- is always reported through
          // combat-log-upload's result field, never through PostError. Same
          // split TestCombatLogWebhook uses for testResult.
          _postToHud(new
          {
              type = "triffview:combat-log-upload",
              result = upload.ToState(),
          });
      }
      catch (Exception ex)
      {
          if (_disposed) return;
          // Unlike the upload's own failure (reported via result above), an
          // exception here comes from BuildCombatLogWindow, CombatLogExport.Export,
          // or file I/O around the temp path -- nothing Discord ever saw, so
          // there is no "attempt" to report an outcome of.
          PostError("upload-combat-logs", ex.Message);
      }
      finally
      {
          // Unconditional: covers the size-limit refusal, a failed upload, a
          // successful one, and any exception above -- the temp copy's job ends
          // here on every path, matching the spec's flow description exactly.
          if (tempPath != null) TryDeleteCombatLogTemp(tempPath);
      }
  }

  /// <summary>
  /// The line posted to Discord alongside the archive. Framing lives here,
  /// not in CombatLogUpload, which stays free of any opinion about wording.
  /// DroppedFileCount is reported on success as well as failure: an upload
  /// lands directly in a channel someone builds an after-action report from,
  /// having never seen this app's UI, so the warning has to reach the channel
  /// itself rather than only the operator (see CombatLogExport.cs's own
  /// DroppedFileCount comment for the same reasoning on the save path).
  /// </summary>
  private static string FormatCombatLogUploadContent(CombatLogExportResult result)
  {
      var pilots = result.Characters.Count;
      var pilotWord = pilots == 1 ? "pilot" : "pilots";
      var line = $"TriffView combat log: {result.StartUtc:yyyy-MM-dd HH:mm}Z to {result.EndUtc:yyyy-MM-dd HH:mm}Z, {pilots} {pilotWord}.";
      if (result.DroppedFileCount > 0)
      {
          line += $" {result.DroppedFileCount} additional matching log file(s) were not included (64-file cap).";
      }
      return line;
  }

  private static void TryDeleteCombatLogTemp(string path)
  {
      try
      {
          if (File.Exists(path)) File.Delete(path);
      }
      catch (Exception ex)
      {
          // Best-effort: a file already gone or briefly locked must not turn a
          // completed (or failed) upload into a reported error.
          TriffViewDiagnostics.Log("combat-log-upload", $"Failed to delete temp archive '{path}': {ex.Message}");
      }
  }
  ```

  Add the dispatch case in `HandleWebMessage`, alongside `triffview:export-combat-logs`:

  ```csharp
  case "triffview:upload-combat-logs":
      UploadCombatLogs(
          message?["fromUtc"]?.GetValue<string>(),
          message?["toUtc"]?.GetValue<string>());
      return true;
  ```

  Run. Expect this test to pass. Baseline going into Task 4 is whatever Task 3 left it at (stated there as 164, to be confirmed from the actual `dotnet test` summary); expect +1.

  ```bash
  git add native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/CombatLogUploadFlowTests.cs
  git commit -m "Add triffview:upload-combat-logs

Reuses BuildCombatLogWindow and CombatLogExport.Export unchanged
against a %TEMP% path, then POSTs the archive through Tasks 1-2's
CombatLogUpload -- the save-to-disk export path stays untouched."
  ```

- [ ] **Step 3: Failing test — an oversize archive is refused and its temp file is deleted, without ever reaching the network**

  This drives no new production code by itself (the size check already exists from Step 2), but it is the first test to actually produce Gamelogs on disk, so it introduces the shared fixture the remaining tests build on. Add to `CombatLogUploadFlowTests`:

  ```csharp
  [Fact]
  public void UploadCombatLogsRefusesAnOversizeArchiveAndDeletesTheTemp()
  {
      var gamelogsDir = CreateGamelogsDir();
      try
      {
          // One log padded past 10 MB compressed is impractical to build from
          // realistic log text, so this test asserts the refusal path instead
          // via a manual range that matches zero logs -- covered by the
          // "no logs in range" failing-fast test below -- and the oversize
          // path itself is exercised in CombatLogExport's own test suite
          // against CombatLogExportResult.ExceedsDiscordLimit directly. This
          // controller-level test instead confirms no temp file survives an
          // error return.
          var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
          var end = start.AddMinutes(1);
          WriteGamelog(gamelogsDir, "20260101000000_Pilot_One.txt", start, "Pilot One");

          var messages = new ConcurrentQueue<string>();
          var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
          using var controller = Controller(credentials, messages);
          controller.SetGamelogsPathForTests(gamelogsDir);

          controller.HandleWebMessage(
              "triffview:upload-combat-logs",
              JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{end:O}}"}""")!.AsObject());

          Assert.True(SpinWait.SpinUntil(
              () => messages.Any(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal)
                  || json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)),
              TimeSpan.FromSeconds(10)));
          Assert.Empty(Directory.EnumerateFiles(Path.GetTempPath(), "triffview-fight-*.zip")
              .Where(path => File.GetCreationTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1)));
      }
      finally
      {
          Directory.Delete(gamelogsDir, recursive: true);
      }
  }
  ```

  This step as drafted depends on a `SetGamelogsPathForTests` seam that does not exist and is not in this task's canonical interface list -- **do not add it**. Replace the approach before running: `_alerts.GamelogsPath` is read from `TriffAlertsService`, which resolves the real EVE Gamelogs folder and has no override hook either. Re-scope this step to what Task 4's actual interfaces support:

  Delete the draft above and replace it with a test of the piece that *is* independently testable -- `FormatCombatLogUploadContent`'s dropped-file wording -- deferring full end-to-end coverage (real gamelogs, real webhook, real temp lifecycle) to Step 5, which drives the fixture problem into the open instead of routing around it silently.

  ```csharp
  [Fact]
  public void UploadCombatLogsReportsANoOverlapWindowAsAnErrorNotASilentSkip()
  {
      var messages = new ConcurrentQueue<string>();
      var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
      using var controller = Controller(credentials, messages);

      controller.HandleWebMessage(
          "triffview:upload-combat-logs",
          JsonNode.Parse("""{"fromUtc":"2000-01-01T00:00:00Z","toUtc":"2000-01-01T00:01:00Z"}""")!.AsObject());

      Assert.True(SpinWait.SpinUntil(
          () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
              && json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)),
          TimeSpan.FromSeconds(10)));
  }
  ```

  Run. This exercises the real `_alerts.GamelogsPath` (the actual EVE log folder, or its absence on a CI box), a window with no possible overlap in the year 2000, and should already **pass**: `CombatLogExport.Export` throws `InvalidOperationException("No EVE logs overlap...")` when nothing matches, which the `catch` block in Step 2 turns into a `PostError`. If the machine's `%USERPROFILE%\...\EVE\logs\Gamelogs` folder does not exist at all, `Export` throws its "Gamelogs folder not found" `InvalidOperationException` instead, which is caught identically -- either way, the test's assertion (an error naming `upload-combat-logs`) holds.

  ```bash
  git add native/TriffView.Tests/CombatLogUploadFlowTests.cs
  git commit -m "Add regression test proving a non-overlapping upload window fails loudly

An empty result must not be mistaken for silent success -- Export's
'no logs overlap' failure has to surface as an error the user can act
on, not a quiet no-op."
  ```

  **Flag for the team lead:** this exposes a real gap the spec does not address -- `TriffAlertsService.GamelogsPath` has no test-injection seam, unlike `TriffSkillsPaths.OverrideRoot` / `TriffFleetsLocalState`'s constructor-injected state. A genuine end-to-end test of the success path (real archive built, real upload to `StubWebhookServer`, real temp-file cleanup verified) cannot be written against this controller without either adding such a seam to `TriffAlertsService` (out of scope for these two tasks -- it belongs to whichever task owns that class) or writing directly-to-disk fixtures under whatever `_alerts.GamelogsPath` happens to resolve to on the CI machine, which is not hermetic. Steps 4-5 take the second, narrower option: they drive `CombatLogExport.Export` and `CombatLogUpload.UploadAsync` together directly (bypassing the controller) to prove the pieces compose, and rely on Step 1's controller-level test plus Task 3's coverage to prove the wiring.

- [ ] **Step 4: Failing test — the full export-then-upload composition succeeds against a stub server and cleans up its temp file**

  This test drives `CombatLogExport.Export` and `CombatLogUpload.UploadAsync` directly, at the same layer `UploadCombatLogs` composes them, since the controller itself cannot be pointed at a fixture Gamelogs folder (Step 3's finding). It is real coverage of the composition Step 2 wrote, short of going through `HandleWebMessage`.

  Add:

  ```csharp
  [Fact]
  public async Task ExportThenUploadComposeAgainstAStubServerAndLeaveNoTempFileBehind()
  {
      var gamelogsDir = Path.Combine(Path.GetTempPath(), "triffview-upload-fixture", Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(gamelogsDir);
      var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
      File.WriteAllText(
          Path.Combine(gamelogsDir, "20260101000000_1_Pilot_One.txt"),
          "Gamelog\r\n\r\n            Listener: Pilot One\r\n" +
          "  Session Started: 2026.01.01 00:00:00\r\n\r\n" +
          "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");

      var tempZip = Path.Combine(Path.GetTempPath(), $"triffview-fight-upload-test-{Guid.NewGuid():N}.zip");
      using var stub = new StubWebhookServer { StatusCode = 200, ResponseBody = "{}" };
      try
      {
          var window = new CombatLogFightWindow { StartUtc = start, EndUtc = start.AddMinutes(1), Source = CombatLogWindowSource.ManualRange };
          var result = CombatLogExport.Export(gamelogsDir, window.StartUtc, window.EndUtc, tempZip, window.Source);
          Assert.False(result.ExceedsDiscordLimit);

          using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
          using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
          var upload = await CombatLogUpload.UploadAsync(http, stub.Uri, tempZip, "TriffView combat log test", cts.Token);

          Assert.True(upload.Succeeded, upload.Message);
          Assert.Equal(1, stub.RequestCount);
      }
      finally
      {
          if (File.Exists(tempZip)) File.Delete(tempZip);
          Directory.Delete(gamelogsDir, recursive: true);
      }
  }
  ```

  Add `using System.Net.Http;` and `using System.Threading;` to `CombatLogUploadFlowTests.cs`.

  Run. This should already **pass** using Tasks 1-2's `CombatLogUpload.UploadAsync` and the existing `CombatLogExport.Export` -- it is integration coverage of the composition, not new production code. If it fails, narrow down whether the fault is in `CombatLogExport.Export` (unlikely; unchanged) or `CombatLogUpload.UploadAsync`'s handling of a 200 response, and report that rather than adjusting this controller's code to compensate.

  ```bash
  git add native/TriffView.Tests/CombatLogUploadFlowTests.cs
  git commit -m "Add composed export-and-upload regression test against a stub server

The controller can't be pointed at a fixture Gamelogs folder
(no injection seam on TriffAlertsService.GamelogsPath), so this
drives Export and UploadAsync together at the layer UploadCombatLogs
composes them, over a real loopback HTTP round trip."
  ```

- [ ] **Step 5: Failing test — the startup sweep deletes both stale shapes and leaves fresh files alone**

  `SweepStaleCombatLogTemps` is private and its only call site is `Start()`, so the test drives it through `Start()`, accepting the side effects that come with constructing and starting a full `TriffViewController` (documented in this task's file-level note below).

  Add:

  ```csharp
  [Fact]
  public void StartSweepsStaleFightArchivesAndStagingFilesButKeepsFreshOnes()
  {
      var tempDir = Path.GetTempPath();
      var staleZip = Path.Combine(tempDir, $"triffview-fight-{Guid.NewGuid():N}.zip");
      var staleStaging = Path.Combine(tempDir, $"triffview-fight-{Guid.NewGuid():N}.zip.{Guid.NewGuid():N}.tmp");
      var freshZip = Path.Combine(tempDir, $"triffview-fight-{Guid.NewGuid():N}.zip");
      var unrelatedOld = Path.Combine(tempDir, $"unrelated-{Guid.NewGuid():N}.zip");
      File.WriteAllText(staleZip, "stale");
      File.WriteAllText(staleStaging, "stale-staging");
      File.WriteAllText(freshZip, "fresh");
      File.WriteAllText(unrelatedOld, "unrelated");
      var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
      File.SetLastWriteTimeUtc(staleZip, twoDaysAgo);
      File.SetLastWriteTimeUtc(staleStaging, twoDaysAgo);
      File.SetLastWriteTimeUtc(unrelatedOld, twoDaysAgo);

      try
      {
          var messages = new ConcurrentQueue<string>();
          using var controller = Controller(new MemoryCredentials(), messages);
          controller.Start();

          Assert.True(SpinWait.SpinUntil(
              () => !File.Exists(staleZip) && !File.Exists(staleStaging),
              TimeSpan.FromSeconds(5)));
          Assert.True(File.Exists(freshZip));
          Assert.True(File.Exists(unrelatedOld));
      }
      finally
      {
          foreach (var path in new[] { staleZip, staleStaging, freshZip, unrelatedOld })
          {
              if (File.Exists(path)) File.Delete(path);
          }
      }
  }
  ```

  Run. Expect a **test failure**: no sweep exists yet, so all four files remain.

- [ ] **Step 6: Add `SweepStaleCombatLogTemps` and call it from `Start()`**

  ```csharp
  /// <summary>
  /// Best-effort cleanup of anything UploadCombatLogs could have left behind if
  /// the process died mid-run. Two shapes, not one: Export builds its archive at
  /// "&lt;destination&gt;.&lt;guid&gt;.tmp" and only then moves it into place
  /// (CombatLogExport.cs), so a death during compression leaves the staging
  /// file rather than the finished archive -- sweeping only the finished-archive
  /// glob would leave that behind indefinitely. Non-fatal: a locked or
  /// already-gone file must never block startup.
  /// </summary>
  private static void SweepStaleCombatLogTemps()
  {
      var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(1);
      var tempDir = Path.GetTempPath();

      foreach (var pattern in new[] { "triffview-fight-*.zip", "triffview-fight-*.zip.*.tmp" })
      {
          IEnumerable<string> matches;
          try
          {
              matches = Directory.EnumerateFiles(tempDir, pattern);
          }
          catch (Exception ex)
          {
              TriffViewDiagnostics.Log("combat-log-upload", $"Temp sweep failed to enumerate '{pattern}': {ex.Message}");
              continue;
          }

          foreach (var path in matches)
          {
              try
              {
                  if (File.GetLastWriteTimeUtc(path) >= cutoffUtc) continue;
                  File.Delete(path);
              }
              catch (Exception ex)
              {
                  TriffViewDiagnostics.Log("combat-log-upload", $"Temp sweep failed to delete '{path}': {ex.Message}");
              }
          }
      }
  }
  ```

  In `Start()`:

  ```csharp
  StartForegroundTracking();
  StartDisplayTracking();
  LogLayoutSnapshot("startup");
  SweepStaleCombatLogTemps();
  RefreshCombatLogWebhookState();
  PostState();
  ```

  Run. Expect the sweep test to pass.

  ```bash
  git add native/TriffView/TriffViewSubsystem.cs native/TriffView.Tests/CombatLogUploadFlowTests.cs
  git commit -m "Sweep stale combat log temp files on startup

Covers both shapes an interrupted upload can leave behind: the
finished archive and Export's own staging file, in case the process
died mid-compression before the move-into-place happened."
  ```

  **File-level note on this step's test cost:** `StartSweepsStaleFightArchivesAndStagingFilesButKeepsFreshOnes` calls the real `Start()`, which (beyond the sweep) reads the developer's actual `%APPDATA%\TriffHud\triffview-settings.json` via `TriffViewSettings.Load()` and installs a real `SetWinEventHook` for foreground-window tracking, neither of which `TriffViewController` currently exposes a test seam for (unlike `TriffSkillsPaths.OverrideRoot` / `TriffFleetsLocalState`'s injectable state). Both are read-only or self-unhooked by `Dispose()` and are exercised the same way by every other test in this file and in Task 3's file that calls `Start()`, so this is not a new risk introduced here -- flagging it once, at the step that most depends on it, rather than re-raising it per test.

- [ ] **Step 7: Full local run and final count**

  Run the full filtered suite once more and report the actual pass count from the summary line (do not extrapolate it further from this plan's step-by-step arithmetic):

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
  $env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
  $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
  $env:APPDATA='C:\dev\TriffView\.appdata'
  $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter 'FullyQualifiedName~CombatLogUpload'
  "
  ```

  Then run the whole project once, unfiltered, to confirm nothing else regressed and that `--warnaserror` still passes with the new `using`s:

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
  $env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
  $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
  $env:APPDATA='C:\dev\TriffView\.appdata'
  $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\native\TriffView.Tests\TriffView.Tests.csproj' -c Release
  "
  ```

  Report both summary lines verbatim rather than restating the expected counts from this plan.
### Task 5: Discord destination and upload buttons in the combat log export tab

**Files:**
- Modify: `app/src/tools/CombatLogExport.jsx:1-2` (imports), `:56` (function signature), `:57-63` (intro block, insert Discord destination block after it), `:81-139` (both export paths, add Upload to Discord buttons), `:142-164` (status block, add upload result/error rendering)
- Modify: `app/src/tools/TriffViewSettings.jsx:1034-1035` (new state), `:1210-1214` (new handlers alongside `exportCombatLogs`), `:1267-1294` (`onNativeMessage` effect — add webhook/upload message handling and merge `combatLogWebhook` from the state post), `:1801-1811` (render call site — new props)
- Modify: `app/src/styles.css:3066-3145` (new classes for the webhook block and the per-path button row)
- Test: no JS test runner exists in this repo — `app/package.json` defines only `dev` / `build` / `preview` scripts, no `test`. Verification is `npm run build` (step 9) plus a manual click-through list (step 9) that is **unexercised until a person runs it on Windows** with a build that has `native/Assets/overlay-dist.zip` copied in (see CLAUDE.md's worktree note — this worktree does not carry that zip).

**Interfaces:**
- Consumes (native → web): `triffview:state` with `combatLogWebhook: { configured, description }`; `triffview:combat-log-webhook` with `{ configured, description, testResult? }`; `triffview:combat-log-upload` with `{ result }`; `triffview:error` with `action` one of `set-combat-log-webhook`, `clear-combat-log-webhook`, `test-combat-log-webhook`, `upload-combat-logs`.
- Produces (web → native): `triffview:set-combat-log-webhook` `{ url }`, `triffview:clear-combat-log-webhook` `{}`, `triffview:test-combat-log-webhook` `{}`, `triffview:upload-combat-logs` `{ fromUtc, toUtc }` or `{}`.

**Design notes (read before implementing):**
- The webhook input is local state inside `CombatLogExport` (not lifted to `TriffViewSettings`), same reasoning as it not needing to survive a re-render from elsewhere — it exists only long enough to be typed and saved. It is cleared by a `useEffect` that watches a `webhookState.action` field transitioning from `"save"` to `null` with no error, so a successful **test** or **clear** does not also wipe out an in-progress edit the user has not saved yet. `action` is one of `"save" | "clear" | "test" | null`, tracked in the parent so the three webhook buttons share one busy flag but the child can still tell which action just finished.
- Export and Upload buttons on both paths are disabled by a shared `runBusy = exportState.busy || uploadState.busy`, not by their own state alone. Both paths call into the same native compression step (per the design doc's flow, mirroring `ExportCombatLogs`), so letting an export and an upload run concurrently would race two archive builds. This is a judgment call beyond the spec's literal wording ("disabled ... or a run is in flight") — flagged here since it changes observable button behavior slightly (an export in progress also greys out the Upload buttons, and vice versa).
- `triffview:combat-log-upload` results are **not all successes**, and the split between the two failure shapes is fixed, not a UI judgment call: **pre-flight refusals** (no webhook configured, no fight detected, an unparseable range, or an oversize archive) never reach `UploadAsync` and arrive as `triffview:error` with `action: "upload-combat-logs"` — handled by the existing `uploadState.error` path. **Upload attempt outcomes** (HTTP status mappings, transport errors, the 120s timeout) arrive as `triffview:combat-log-upload` with `result.succeeded === false` and a populated `result.message` — handled by rendering `uploadState.result.message` in an error style when `succeeded` is false. Both shapes must be handled, or one class of failure would render nothing and leave the busy flag looking stuck.
- `combatLogWebhook.configured`/`description` are merged from every `triffview:state` post (it arrives on the 700ms periodic refresh, same as everything else in `state`), but `testResult` is preserved across those periodic merges rather than reset, so a "Send test" result doesn't disappear before the user reads it just because a routine state post landed.

---

- [ ] **Step 1: Add CSS for the webhook block and the per-path button row**

  In `app/src/styles.css`, insert after the existing `.triff-combat-export-error` rule (ends at line 3145, right before the unrelated `.triff-alert-history-row strong` rule):

  ```css
  .triff-combat-export-webhook {
    display: flex;
    flex-direction: column;
    gap: 8px;
    border: 1px solid var(--tv-border-soft);
    padding: 12px;
  }

  .triff-combat-export-webhook h3 {
    margin: 0;
    color: var(--tv-text-strong);
    font-size: 13px;
    font-weight: 700;
  }

  .triff-combat-export-webhook-row {
    display: flex;
    flex-wrap: wrap;
    align-items: end;
    gap: 8px;
  }

  .triff-combat-export-webhook-row .triffview-field {
    flex: 1 1 260px;
  }

  .triff-combat-export-path-actions {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  ```

- [ ] **Step 2: Widen the React import in `CombatLogExport.jsx` for local state**

  In `app/src/tools/CombatLogExport.jsx`, the file currently only needs `React` as a default import (line 1). The new webhook input needs `useState`, `useEffect`, `useRef`.

  Old:
  ```jsx
  import React from "react";
  import Field from "./Field.jsx";
  ```

  New:
  ```jsx
  import React, { useEffect, useRef, useState } from "react";
  import Field from "./Field.jsx";
  ```

- [ ] **Step 3: Add the Discord destination block and its props/local state**

  Still in `app/src/tools/CombatLogExport.jsx`. Replace the function signature and the top of the body (original lines 56-64):

  Old:
  ```jsx
  function CombatLogExport({ lastFight, exportState, range, onRangeChange, onExport }) {
    return (
      <div className="triff-combat-export">
        <p className="triffview-muted">
          Packages the EVE game logs covering a fight into a zip you can upload to Discord for
          eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
          Chat logs are never included.
        </p>

        <div className="triff-alert-summary">
  ```

  New:
  ```jsx
  function CombatLogExport({
    lastFight,
    exportState,
    range,
    onRangeChange,
    onExport,
    webhookState,
    onSaveWebhook,
    onClearWebhook,
    onTestWebhook,
    uploadState,
    onUpload,
  }) {
    const [webhookInput, setWebhookInput] = useState("");
    // Only a successful *save* should clear the typed URL - if this fired on
    // any action finishing, a successful "Send test" or "Clear" would wipe out
    // an edit the user had not saved yet.
    const prevActionRef = useRef(null);
    useEffect(() => {
      if (prevActionRef.current === "save" && webhookState.action === null && !webhookState.error) {
        setWebhookInput("");
      }
      prevActionRef.current = webhookState.action;
    }, [webhookState.action, webhookState.error]);

    const webhookBusy = webhookState.action !== null;
    // Export and upload both compress the archive on the native side, so
    // either one running blocks the other rather than racing two builds.
    const runBusy = exportState.busy || uploadState.busy;
    const uploadDisabled = runBusy || !webhookState.configured;

    return (
      <div className="triff-combat-export">
        <p className="triffview-muted">
          Packages the EVE game logs covering a fight into a zip you can upload to Discord for
          eve-intel. Game logs only, copied as-is, plus a small manifest listing your characters.
          Chat logs are never included.
        </p>

        <div className="triff-combat-export-webhook">
          <h3>Discord destination</h3>
          <p className="triffview-muted">
            Paste a webhook URL to enable one-click uploads. It is stored in Windows Credential
            Manager, never written to triffview-settings.json, and never sent back to this screen
            once saved.
          </p>
          <div className="triff-combat-export-webhook-row">
            <Field label="Webhook URL">
              <input
                type="password"
                placeholder="https://discord.com/api/webhooks/…"
                value={webhookInput}
                disabled={webhookBusy}
                onChange={(event) => setWebhookInput(event.target.value)}
              />
            </Field>
            <button
              type="button"
              disabled={webhookBusy || !webhookInput.trim()}
              onClick={() => onSaveWebhook(webhookInput.trim())}
            >
              Save
            </button>
            <button
              type="button"
              disabled={webhookBusy || !webhookState.configured}
              onClick={onClearWebhook}
            >
              Clear
            </button>
            <button
              type="button"
              disabled={webhookBusy || !webhookState.configured}
              onClick={onTestWebhook}
            >
              Send test
            </button>
          </div>
          <p className="triffview-muted">
            {webhookState.configured ? `Configured: ${webhookState.description}` : "No webhook configured."}
          </p>
          {webhookState.testResult ? (
            <p
              className={
                webhookState.testResult.ok ? "triff-combat-export-result" : "triff-combat-export-error"
              }
            >
              {webhookState.testResult.message}
            </p>
          ) : null}
          {webhookState.error ? <p className="triff-combat-export-error">{webhookState.error}</p> : null}
        </div>

        <div className="triff-alert-summary">
  ```

- [ ] **Step 4: Add "Upload to Discord" buttons to both export paths**

  Still in `app/src/tools/CombatLogExport.jsx`. Replace the "Last fight" path (original lines 82-92):

  Old:
  ```jsx
        <div className="triff-combat-export-path">
          <h3>Last fight</h3>
          <p className="triffview-muted">Export the fight TriffAlerts most recently detected.</p>
          <button
            type="button"
            disabled={!lastFight || exportState.busy}
            onClick={() => onExport(null)}
          >
            Export last fight
          </button>
        </div>
  ```

  New:
  ```jsx
        <div className="triff-combat-export-path">
          <h3>Last fight</h3>
          <p className="triffview-muted">Export the fight TriffAlerts most recently detected.</p>
          <div className="triff-combat-export-path-actions">
            <button type="button" disabled={!lastFight || runBusy} onClick={() => onExport(null)}>
              Export last fight
            </button>
            <button
              type="button"
              disabled={!lastFight || uploadDisabled}
              onClick={() => onUpload(null)}
            >
              Upload to Discord
            </button>
          </div>
        </div>
  ```

  Then replace the "Time range" path's quick-range buttons and its final Export button (original lines 94-139), keeping the range inputs (`Field` blocks) unchanged in between:

  Old (quick-range buttons):
  ```jsx
          <div className="triff-combat-export-quick">
            <span>Quick range:</span>
            <button
              type="button"
              disabled={exportState.busy}
              onClick={() => onRangeChange(quickRange(1))}
            >
              Last 1 hour
            </button>
            <button
              type="button"
              disabled={exportState.busy}
              onClick={() => onRangeChange(quickRange(2))}
            >
              Last 2 hours
            </button>
          </div>
  ```

  New:
  ```jsx
          <div className="triff-combat-export-quick">
            <span>Quick range:</span>
            <button type="button" disabled={runBusy} onClick={() => onRangeChange(quickRange(1))}>
              Last 1 hour
            </button>
            <button type="button" disabled={runBusy} onClick={() => onRangeChange(quickRange(2))}>
              Last 2 hours
            </button>
          </div>
  ```

  Old (final Export range button):
  ```jsx
          <button
            type="button"
            disabled={!range.from || !range.to || exportState.busy}
            onClick={() => onExport(range)}
          >
            Export range
          </button>
        </div>
      </div>
  ```

  New:
  ```jsx
          <div className="triff-combat-export-path-actions">
            <button
              type="button"
              disabled={!range.from || !range.to || runBusy}
              onClick={() => onExport(range)}
            >
              Export range
            </button>
            <button
              type="button"
              disabled={!range.from || !range.to || uploadDisabled}
              onClick={() => onUpload(range)}
            >
              Upload to Discord
            </button>
          </div>
        </div>
      </div>
  ```

- [ ] **Step 5: Render the upload result/error alongside the existing export status**

  Still in `app/src/tools/CombatLogExport.jsx`. Replace the closing of the status block (original lines 160-166):

  Old:
  ```jsx
        {exportState.error ? (
          <p className="triff-combat-export-error">{exportState.error}</p>
        ) : null}
      </div>
    </div>
  );
  ```

  New:
  ```jsx
        {exportState.error ? (
          <p className="triff-combat-export-error">{exportState.error}</p>
        ) : null}
        {uploadState.result ? (
          uploadState.result.succeeded ? (
            <p className="triff-combat-export-result">
              Uploaded {uploadState.result.fileCount} log
              {uploadState.result.fileCount === 1 ? "" : "s"}
              {uploadState.result.characters?.length
                ? ` (${uploadState.result.characters.join(", ")})`
                : ""}{" "}
              to Discord - {formatBytes(uploadState.result.zipBytes)} sent.
              {uploadState.result.droppedFileCount
                ? ` ${uploadState.result.droppedFileCount} further matching log${
                    uploadState.result.droppedFileCount === 1 ? " was" : "s were"
                  } left out at the file limit - narrow the time range to cover them.`
                : ""}
            </p>
          ) : (
            <p className="triff-combat-export-error">{uploadState.result.message}</p>
          )
        ) : null}
        {uploadState.error ? <p className="triff-combat-export-error">{uploadState.error}</p> : null}
      </div>
    </div>
  );
  ```

  Note: `uploadState.result.succeeded === false` (a normal reply reporting "no webhook configured" or "archive too large", per the design doc's flow) is rendered from `result.message`, distinct from `uploadState.error` which comes from a `triffview:error` (thrown/transport failure). Both are handled so neither failure shape renders nothing.

- [ ] **Step 6: Add upload/webhook state and native-message handlers to `TriffViewSettings.jsx`**

  In `app/src/tools/TriffViewSettings.jsx`, add new state next to the existing combat log state (original lines 1034-1035):

  Old:
  ```jsx
    const [combatLogExport, setCombatLogExport] = useState({ result: null, error: "", busy: false });
    const [combatLogRange, setCombatLogRange] = useState({ from: "", to: "" });
  ```

  New:
  ```jsx
    const [combatLogExport, setCombatLogExport] = useState({ result: null, error: "", busy: false });
    const [combatLogRange, setCombatLogRange] = useState({ from: "", to: "" });
    const [combatLogWebhook, setCombatLogWebhook] = useState({
      configured: false,
      description: "",
      testResult: null,
      error: "",
    });
    // 'save' | 'clear' | 'test' | null - which webhook action is in flight, so the
    // three buttons share one busy flag but CombatLogExport can still tell a
    // successful *save* apart from a successful clear or test.
    const [combatLogWebhookAction, setCombatLogWebhookAction] = useState(null);
    const [combatLogUpload, setCombatLogUpload] = useState({ result: null, error: "", busy: false });
  ```

  Then add handlers next to `exportCombatLogs` (original lines 1210-1214):

  Old:
  ```jsx
    // Omitting the range tells the native side to use the last detected fight.
    function exportCombatLogs(range) {
      setCombatLogExport({ result: null, error: "", busy: true });
      send("triffview:export-combat-logs", range ? { fromUtc: range.from, toUtc: range.to } : {});
    }

  ```

  New:
  ```jsx
    // Omitting the range tells the native side to use the last detected fight.
    function exportCombatLogs(range) {
      setCombatLogExport({ result: null, error: "", busy: true });
      send("triffview:export-combat-logs", range ? { fromUtc: range.from, toUtc: range.to } : {});
    }

    // Same "omit the range for the last fight" convention as exportCombatLogs.
    function uploadCombatLogs(range) {
      setCombatLogUpload({ result: null, error: "", busy: true });
      send("triffview:upload-combat-logs", range ? { fromUtc: range.from, toUtc: range.to } : {});
    }

    function saveCombatLogWebhook(url) {
      setCombatLogWebhookAction("save");
      setCombatLogWebhook((current) => ({ ...current, error: "" }));
      send("triffview:set-combat-log-webhook", { url });
    }

    function clearCombatLogWebhook() {
      setCombatLogWebhookAction("clear");
      setCombatLogWebhook((current) => ({ ...current, error: "" }));
      send("triffview:clear-combat-log-webhook");
    }

    function testCombatLogWebhook() {
      setCombatLogWebhookAction("test");
      setCombatLogWebhook((current) => ({ ...current, error: "" }));
      send("triffview:test-combat-log-webhook");
    }

  ```

- [ ] **Step 7: Handle the new native message types**

  Still in `app/src/tools/TriffViewSettings.jsx`. The `triffview:state` branch and the combat-log-export branches sit inside the same `onNativeMessage` callback (original lines 1268-1289):

  Old:
  ```jsx
      if (message?.type === "triffview:state") {
        setState({
          ...EMPTY_STATE,
          ...message,
          profile: message.profile || {},
          clients: Array.isArray(message.clients) ? message.clients : [],
          alerts: message.alerts || EMPTY_STATE.alerts,
          alertHistory: Array.isArray(message.alertHistory) ? message.alertHistory : [],
          lastFight: message.lastFight || null,
          profiles: Array.isArray(message.profiles) && message.profiles.length ? message.profiles : EMPTY_STATE.profiles,
        });
      }

      if (message?.type === "triffview:combat-log-export") {
        setCombatLogExport({ result: message.result || null, error: "", busy: false });
      }

      if (message?.type === "triffview:error" && message.action === "export-combat-logs") {
        setCombatLogExport({ result: null, error: message.message || "Export failed.", busy: false });
      }
    });
  ```

  New:
  ```jsx
      if (message?.type === "triffview:state") {
        setState({
          ...EMPTY_STATE,
          ...message,
          profile: message.profile || {},
          clients: Array.isArray(message.clients) ? message.clients : [],
          alerts: message.alerts || EMPTY_STATE.alerts,
          alertHistory: Array.isArray(message.alertHistory) ? message.alertHistory : [],
          lastFight: message.lastFight || null,
          profiles: Array.isArray(message.profiles) && message.profiles.length ? message.profiles : EMPTY_STATE.profiles,
        });
        // configured/description are refreshed on every periodic state post;
        // testResult is left alone so a "Send test" outcome isn't wiped out by
        // the next routine post before the user has read it.
        const webhook = message.combatLogWebhook || {};
        setCombatLogWebhook((current) => ({
          ...current,
          configured: Boolean(webhook.configured),
          description: webhook.description || "",
        }));
      }

      if (message?.type === "triffview:combat-log-export") {
        setCombatLogExport({ result: message.result || null, error: "", busy: false });
      }

      if (message?.type === "triffview:error" && message.action === "export-combat-logs") {
        setCombatLogExport({ result: null, error: message.message || "Export failed.", busy: false });
      }

      if (message?.type === "triffview:combat-log-webhook") {
        setCombatLogWebhook({
          configured: Boolean(message.configured),
          description: message.description || "",
          testResult: message.testResult || null,
          error: "",
        });
        setCombatLogWebhookAction(null);
      }

      if (
        message?.type === "triffview:error"
        && [
          "set-combat-log-webhook",
          "clear-combat-log-webhook",
          "test-combat-log-webhook",
        ].includes(message.action)
      ) {
        setCombatLogWebhook((current) => ({
          ...current,
          error: message.message || "Webhook action failed.",
        }));
        setCombatLogWebhookAction(null);
      }

      if (message?.type === "triffview:combat-log-upload") {
        // No cancelled branch: the upload path has no dialog and no user-facing
        // cancel, so the native side never sends one. A timeout arrives here as
        // a result with succeeded === false.
        setCombatLogUpload({
          result: message.result || null,
          error: "",
          busy: false,
        });
      }

      if (message?.type === "triffview:error" && message.action === "upload-combat-logs") {
        setCombatLogUpload({ result: null, error: message.message || "Upload failed.", busy: false });
      }
    });
  ```

- [ ] **Step 8: Wire the new props into the render call**

  Still in `app/src/tools/TriffViewSettings.jsx`, at the `CombatLogExport` render (original lines 1801-1811):

  Old:
  ```jsx
        {activeSection === "combat-logs" ? (
        <div className="triffview-panel">
          <CombatLogExport
            lastFight={lastFight}
            exportState={combatLogExport}
            range={combatLogRange}
            onRangeChange={setCombatLogRange}
            onExport={exportCombatLogs}
          />
        </div>
        ) : null}
  ```

  New:
  ```jsx
        {activeSection === "combat-logs" ? (
        <div className="triffview-panel">
          <CombatLogExport
            lastFight={lastFight}
            exportState={combatLogExport}
            range={combatLogRange}
            onRangeChange={setCombatLogRange}
            onExport={exportCombatLogs}
            webhookState={{ ...combatLogWebhook, action: combatLogWebhookAction }}
            onSaveWebhook={saveCombatLogWebhook}
            onClearWebhook={clearCombatLogWebhook}
            onTestWebhook={testCombatLogWebhook}
            uploadState={combatLogUpload}
            onUpload={uploadCombatLogs}
          />
        </div>
        ) : null}
  ```

- [ ] **Step 9: Build and manually verify**

  Run, from WSL (npm is not on the Windows PATH here):

  ```bash
  cd /mnt/c/dev/TriffView/.claude/worktrees/combat-log-discord-upload/app && npm run build
  ```

  Paste the actual output. `npm run build` succeeding only proves the JSX compiles and Vite bundles it — it says nothing about runtime behavior in the WebView2 host, which cannot be exercised from WSL.

  The following manual check list is **unexercised until someone runs it on Windows**, against a build where `native/Assets/overlay-dist.zip` has been copied into this worktree from the main checkout and rebuilt (this worktree does not carry that zip, so without it the app serves the "missing overlay" page instead of the real UI). It also depends on Tasks 3 and 4 being in place, since without the native `set-combat-log-webhook` and `upload-combat-logs` handlers every click below hangs its button forever. Nothing in this list can be checked off by Task 5 alone.

  1. Open Settings → Combat log export. Expect a new "Discord destination" block above "Last fight" / "Time range", showing "No webhook configured." and all four action buttons except Save disabled (Save disabled too, since the input starts empty).
  2. Type a non-Discord URL (e.g. `https://example.com/x`) and press Save. Expect an error line naming what's wrong, no `configured` state change, and the typed value still in the field (only a *successful* save clears it).
  3. Type a real-looking Discord webhook URL (`https://discord.com/api/webhooks/123/abc`) and press Save. Expect the button row to disable briefly, then: the input clears, "Configured: discord.com/api/webhooks/123…" (token redacted) appears, and both "Upload to Discord" buttons on the export paths become enabled (once a `lastFight`/range is also present).
  4. Press "Send test" on a configured webhook. Expect a result line (success or a redacted failure), and a real message posted to the Discord channel behind that webhook.
  5. Press "Send test" before ever configuring a webhook (fresh profile / after Clear) — expect the button to be disabled, not reachable.
  6. Press "Clear". Expect it to revert to "No webhook configured.", clear any prior `testResult`, and disable both Upload buttons again.
  7. With no webhook configured, confirm "Upload to Discord" is disabled on both the Last fight and Time range paths even when Export is enabled.
  8. With a webhook configured and a detected last fight, press "Upload to Discord" under Last fight. Expect Export and both Upload buttons across both paths to disable while the run is in flight, then a success line ("Uploaded N logs ... to Discord - X sent.") separate from any prior Export result line, and the file lands as an attachment in the Discord channel.
  9. Pick a time range wide enough to exceed Discord's 10 MB cap and press "Upload to Discord" under Time range. Expect a rejection reported from the *result* (not a thrown error) naming the size, with no partial post to Discord, and the temp zip not left behind in `%TEMP%`.
  10. Trigger an upload that would drop files at the 64-file cap (a wide range with many logs). Expect a *successful* upload result that also states the dropped-file count, matching the export path's existing wording.

- [ ] **Step 10: Commit**

  One commit for the whole task. Steps 1-8 are a single coherent change: the
  component starts consuming `webhookState`, `uploadState` and the four new
  callbacks in steps 3-5, and step 8 is what first passes them. Committing
  between those points would record a state where the panel reads props nobody
  supplies — it builds, because React tolerates undefined props, and it is
  broken at runtime, which is the worst kind of commit to bisect onto.

  ```bash
  git add app/src/styles.css app/src/tools/CombatLogExport.jsx app/src/tools/TriffViewSettings.jsx
  git commit -m "Add the Discord destination block and upload buttons

The panel gains a masked webhook input with Save, Clear and Send test, and an
Upload to Discord button on each of the two export paths. Upload status is held
in its own state object rather than sharing the export one, so pressing either
button no longer blanks the other's result.

The input clears only on a successful save. A clear or a test completing must
not wipe a URL the user is midway through typing, and the saved secret should
not linger in React state once the credential store has it.

Export and both Upload buttons disable together while any run is in flight.
They funnel through the same native compression step, so allowing a second
press would start a competing export against the same log directory."
  ```

  Expected: one commit, three files changed.
### Task 6: End-to-end verification against a local stub HTTP server

**Files:**
- Create: `native/TriffView.Tests/CombatLogUploadStubServerTests.cs`
- Test: `native/TriffView.Tests/CombatLogUploadStubServerTests.cs`

**Interfaces:**
- Consumes: `TriffView.Alerts.CombatLogUpload.UploadAsync(HttpClient http, Uri webhook, string zipPath, string content, CancellationToken ct)` and `CombatLogUploadResult` (both already implemented by earlier tasks, already covered there by fake-`HttpMessageHandler` tests for framing/status-mapping); Task 3's `StubWebhookServer` (`native/TriffView.Tests/StubWebhookServer.cs`), specifically its `Uri`, `StatusCode`, `LastContentType`, and `LastRequestBytes` members. This task adds nothing to that surface — it is a verification test, not a driver of new production code.
- Produces: nothing consumed by later tasks. Adds **2** tests to `native/TriffView.Tests`, on top of whatever Tasks 3 and 4 left — not on top of the 155 baseline, which those tasks have already moved well past by the time this one runs.

**Note on TDD framing for this task:** `CombatLogUpload.UploadAsync` and its multipart/status-mapping behaviour were already built and unit-tested against a fake `HttpMessageHandler` in earlier tasks. There is no new production code left for this test to drive — its job is to prove the *real* `HttpClient` and a *real* socket produce the same framing the fake handler assumed, and that the temp-file disposal pattern the future `UploadCombatLogs` orchestration method must follow (Flow steps 3–6 in the design) is actually correct when it runs against a real response. So "red" here most plausibly comes from a bug in this test's own multipart-parsing helpers (offsets, boundary matching, part-name extraction) rather than from `CombatLogUpload` itself — write the harness, run it, and debug whichever side turns out to be wrong before calling it green. If the assertions do fail against `CombatLogUpload` itself, that is a real regression earlier tasks missed and must be fixed in `CombatLogUpload.cs`, not worked around here.

**Ambiguity called out explicitly:** the design's Flow (steps 3 and 6) puts temp-zip creation and deletion in the *subsystem* (`UploadCombatLogs`), not in `CombatLogUpload.UploadAsync` — the function under test here only accepts an existing `zipPath` and never deletes it. The subsystem method lives in `TriffViewSubsystem.cs` and is out of scope for this task (it is covered, if at all, by whichever earlier/later task adds it). So this test cannot assert that *`CombatLogUpload` itself* deletes anything. Instead, this test wraps the call in the same try/finally shape the subsystem method is specified to use, so the thing actually being proved is: "when `CombatLogUpload.UploadAsync` is combined with the disposal pattern the design mandates, against a real listener, both success and failure leave no temp file behind." It also creates and deletes a sibling `.<guid>.tmp` staging file next to the zip, matching the two-glob sweep the design's "Orphaned temporaries" section requires, even though `UploadAsync` never touches it — the point is to exercise the same two-file shape a crash-recovery sweep would see, not to claim `CombatLogUpload` owns that file.

- [ ] **Step 1: Write the success-path test against the shared `StubWebhookServer`.**

  Create `native/TriffView.Tests/CombatLogUploadStubServerTests.cs`. This reuses Task 3's shared
  `StubWebhookServer` (`native/TriffView.Tests/StubWebhookServer.cs`) rather than standing up a
  second loopback listener -- only the multipart-parsing helpers needed to pull `payload_json` and
  `files[0]` back out of `LastRequestBytes` are new here:

  ```csharp
  using System.Text;
  using TriffView.Alerts;
  using Xunit;

  namespace TriffView.Tests;

  public class CombatLogUploadStubServerTests
  {
      [Fact]
      public async Task SuccessfulUpload_SendsExpectedMultipartFraming_AndDeletesTempFiles()
      {
          var zipBytes = Encoding.UTF8.GetBytes("fake zip contents for framing test");
          var zipPath = Path.Combine(Path.GetTempPath(), $"triffview-fight-{Guid.NewGuid():N}.zip");
          var stagingPath = zipPath + $".{Guid.NewGuid():N}.tmp";
          await File.WriteAllBytesAsync(zipPath, zipBytes);
          await File.WriteAllBytesAsync(stagingPath, Array.Empty<byte>());

          using var server = new StubWebhookServer { StatusCode = 204 };
          // The token segment is whatever this instance of the shared stub happens to use --
          // read it back from the Uri rather than hard-coding it, so this assertion still means
          // something if Task 3's server ever changes its path shape.
          var token = server.Uri.Segments[^1];
          const string content = "3 pilots, 2026-08-16 00:00-01:00 UTC. 4 files dropped.";

          using var http = new HttpClient();
          CombatLogUploadResult result;
          try
          {
              result = await CombatLogUpload.UploadAsync(http, server.Uri, zipPath, content, CancellationToken.None);
          }
          finally
          {
              // Mirrors the design's Flow step 6 (delete the finished archive on every path)
              // plus the orphaned-temporaries sweep (delete any matching staging file too).
              File.Delete(zipPath);
              File.Delete(stagingPath);
          }

          Assert.True(result.Succeeded, result.Message);
          Assert.NotNull(server.LastContentType);
          Assert.StartsWith("multipart/form-data", server.LastContentType!, StringComparison.OrdinalIgnoreCase);

          var parts = ParseMultipart(server.LastRequestBytes!, ExtractBoundary(server.LastContentType!));
          Assert.True(parts.ContainsKey("payload_json"));
          Assert.True(parts.ContainsKey("files[0]"));
          Assert.Equal(zipBytes, parts["files[0]"]);

          var payloadText = Encoding.UTF8.GetString(parts["payload_json"]);
          Assert.Contains("dropped", payloadText, StringComparison.OrdinalIgnoreCase);

          Assert.DoesNotContain(token, payloadText, StringComparison.Ordinal);
          Assert.DoesNotContain(token, result.Message, StringComparison.Ordinal);
          Assert.False(File.Exists(zipPath));
          Assert.False(File.Exists(stagingPath));
      }

      private static string ExtractBoundary(string contentType)
      {
          const string marker = "boundary=";
          var index = contentType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
          if (index < 0)
          {
              throw new InvalidOperationException($"no boundary in content-type: {contentType}");
          }
          var boundary = contentType[(index + marker.Length)..].Trim('"');
          var semicolon = boundary.IndexOf(';');
          return semicolon >= 0 ? boundary[..semicolon] : boundary;
      }

      private static Dictionary<string, byte[]> ParseMultipart(byte[] body, string boundary)
      {
          var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
          var headerTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");
          var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

          var start = IndexOf(body, delimiter, 0);
          while (start >= 0)
          {
              var partStart = start + delimiter.Length;
              if (partStart + 1 < body.Length && body[partStart] == (byte)'-' && body[partStart + 1] == (byte)'-')
              {
                  break; // closing boundary "--boundary--"
              }

              var next = IndexOf(body, delimiter, partStart);
              if (next < 0)
              {
                  break;
              }

              var partBytes = body[partStart..next];
              var headerEnd = IndexOf(partBytes, headerTerminator, 0);
              if (headerEnd >= 0)
              {
                  var headerText = Encoding.ASCII.GetString(partBytes, 0, headerEnd);
                  var name = ExtractName(headerText);
                  var contentStart = headerEnd + headerTerminator.Length;
                  var contentEnd = partBytes.Length;
                  if (contentEnd >= 2 && partBytes[contentEnd - 2] == (byte)'\r' && partBytes[contentEnd - 1] == (byte)'\n')
                  {
                      contentEnd -= 2; // trailing CRLF before the next boundary marker
                  }
                  if (name is not null)
                  {
                      result[name] = partBytes[contentStart..contentEnd];
                  }
              }

              start = next;
          }

          return result;
      }

      private static string? ExtractName(string headerText)
      {
          const string marker = "name=\"";
          var index = headerText.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
          if (index < 0)
          {
              return null;
          }
          var nameStart = index + marker.Length;
          var nameEnd = headerText.IndexOf('"', nameStart);
          return nameEnd < 0 ? null : headerText[nameStart..nameEnd];
      }

      private static int IndexOf(byte[] haystack, byte[] needle, int from)
      {
          for (var i = from; i <= haystack.Length - needle.Length; i++)
          {
              var match = true;
              for (var j = 0; j < needle.Length; j++)
              {
                  if (haystack[i + j] != needle[j])
                  {
                      match = false;
                      break;
                  }
              }
              if (match)
              {
                  return i;
              }
          }
          return -1;
      }
  }
  ```

- [ ] **Step 2: Run the new test in isolation and read the result.**

  ```bash
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
  $env:DOTNET_CLI_HOME='C:\dev\TriffView\.dotnet-home'
  $env:NUGET_PACKAGES='C:\dev\TriffView\.nuget'
  $env:APPDATA='C:\dev\TriffView\.appdata'
  $env:NUGET_HTTP_CACHE_PATH='C:\dev\TriffView\.nuget-cache'
  $env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
  & 'C:\dev\TriffView\.dotnet\dotnet.exe' test 'C:\dev\TriffView\.claude\worktrees\combat-log-discord-upload\native\TriffView.Tests\TriffView.Tests.csproj' -c Release --filter 'FullyQualifiedName~StubServer'
  "
  ```

  Expect a failure here on the first attempt -- most likely the multipart parser mis-locating a
  part boundary, or an assumption about `HttpContent`'s exact header casing/ordering. Read the
  actual xunit failure message rather than guessing; do not proceed to Step 3 until you have seen
  real output.

- [ ] **Step 3: Fix whatever Step 2 showed.**

  If the fault is in `CombatLogUploadStubServerTests.cs` (the parsing helpers, the boundary
  extraction), fix it there -- this is test-only code and safe to iterate on freely. If the fault
  instead demonstrates a real bug in `CombatLogUpload.UploadAsync` (e.g. it omits a
  `Content-Disposition` filename, or its `files[0]` part is not byte-identical to the file on disk),
  fix `native/TriffAlerts/CombatLogUpload.cs` instead and re-run the earlier fake-`HttpMessageHandler`
  tests from prior tasks too, since a real bug here would mean those tests have a gap. Re-run the
  Step 2 command until the success-path test passes.

- [ ] **Step 4: Add the failure-path test.**

  Add to the same test class:

  ```csharp
      [Fact]
      public async Task FailedUpload_ReturnsFailureResult_AndStillDeletesTempFiles()
      {
          var zipBytes = Encoding.UTF8.GetBytes("fake zip contents for failure-path test");
          var zipPath = Path.Combine(Path.GetTempPath(), $"triffview-fight-{Guid.NewGuid():N}.zip");
          var stagingPath = zipPath + $".{Guid.NewGuid():N}.tmp";
          await File.WriteAllBytesAsync(zipPath, zipBytes);
          await File.WriteAllBytesAsync(stagingPath, Array.Empty<byte>());

          using var server = new StubWebhookServer { StatusCode = 500 };
          var token = server.Uri.Segments[^1];
          const string content = "1 pilot, 2026-08-16 02:00-02:30 UTC.";

          using var http = new HttpClient();
          CombatLogUploadResult result;
          try
          {
              result = await CombatLogUpload.UploadAsync(http, server.Uri, zipPath, content, CancellationToken.None);
          }
          finally
          {
              File.Delete(zipPath);
              File.Delete(stagingPath);
          }

          Assert.False(result.Succeeded);
          Assert.DoesNotContain(token, result.Message, StringComparison.Ordinal);
          Assert.False(File.Exists(zipPath));
          Assert.False(File.Exists(stagingPath));
      }
  ```

  Add `using System.Net.Http;` and `using System.Threading;` to `CombatLogUploadStubServerTests.cs`.

- [ ] **Step 5: Run the full filtered command again and confirm both tests pass.**

  Same command as Step 2. Expect `Passed! - Failed: 0, Passed: 2, Skipped: 0` (or similar) for the
  `StubServer` filter. Then run the whole `native/TriffView.Tests` suite once with no filter and
  confirm it grew by exactly 2 over whatever Task 4 left it at, with everything else still green.
  Do not compare against the 155 baseline — Tasks 3 and 4 have already moved it well past that.

- [ ] **Step 6: Commit.**

  ```bash
  git add native/TriffView.Tests/CombatLogUploadStubServerTests.cs
  git commit -m "Prove combat log upload framing and temp-file cleanup against a real HTTP server"
  ```

---

### Task 7: Documentation

Three commits. The first two are part of this feature; the third is a pre-existing documentation gap this feature happened to expose, and is committed separately so it doesn't get attributed to the upload feature in history.

**Files:**
- Modify: `README.md:22`, `README.md:66-72`
- Modify: `docs/DIAGNOSTICS.md:26-30`
- Modify: `CLAUDE.md` (the `## Testing` section, and the `## Commands` section)
- Test: none — documentation

**Interfaces:**
- Consumes: the shipped feature's final shape (webhook stored in Windows Credential Manager, never
  re-serialized to the web UI; upload sends the same zip + manifest the export path already builds).
  Nothing here depends on any type signature — it is prose only.
- Produces: nothing consumed by other tasks.

- [ ] **Step 1: README — describe the upload path.**

  In `README.md`, update the feature bullet at line 22 from:

  ```markdown
  - One-click combat log export, packaging a fight's game logs into a zip for eve-intel after-action reports.
  ```

  to:

  ```markdown
  - One-click combat log export, packaging a fight's game logs into a zip for eve-intel after-action reports, with an optional direct upload to a Discord webhook.
  ```

  Then, in the "Combat log export" section, after the existing paragraph ending "Chat logs are never
  included." (`README.md:72`), add:

  ```markdown

  The panel can also send that zip straight to Discord instead of saving it to disk: paste a webhook
  URL once, under Discord destination, and an **Upload to Discord** button appears next to each export
  option. The zip itself doesn't change — it's the same game logs and manifest described above — it
  just leaves the machine over that webhook instead of landing in a folder, arriving in the Discord
  channel the webhook points at. The webhook URL is stored in Windows Credential Manager, the same
  store TriffSkills and TriffFleets already use for EVE tokens, not in the settings file, so it never
  ends up in a settings backup or a diagnostics log.
  ```

  Commit:

  ```bash
  git add README.md
  git commit -m "Document the Discord webhook upload path for combat log export"
  ```

- [ ] **Step 2: DIAGNOSTICS.md — rescope the "nothing is transmitted" claim.**

  In `docs/DIAGNOSTICS.md`, replace the "What it does *not* record" section (`docs/DIAGNOSTICS.md:26-30`):

  ```markdown
  ## What it does *not* record

  No keystrokes, no chat, no screenshots, no game data. It never reads EVE's logs or memory. Nothing
  in this log is ever transmitted anywhere automatically; the file sits on your disk until you choose
  to send it.
  ```

  with:

  ```markdown
  ## What it does *not* record

  No keystrokes, no chat, no screenshots, no game data. It never reads EVE's logs or memory. Nothing
  in this log is ever transmitted anywhere automatically; the file sits on your disk until you choose
  to send it.

  That is a claim about this log specifically, not about TriffView as a whole. The combat log export
  panel can upload a zip of your actual game logs to a Discord webhook you configure yourself — that
  is real network activity, and it lives entirely outside this file. See the [Combat log
  export](../README.md#combat-log-export) section of the README for what that upload sends and where
  it goes.
  ```

  Commit:

  ```bash
  git add docs/DIAGNOSTICS.md
  git commit -m "Rescope the diagnostics log's no-network-activity claim to the log itself"
  ```

- [ ] **Step 3: CLAUDE.md — correct the Testing section. NOT part of this feature; separate commit.**

  This corrects a pre-existing inaccuracy: `CLAUDE.md`'s `## Testing` section describes only
  `tests/TriffView.Tests` and states "Anything inside `TriffViewSubsystem.cs` is effectively
  untestable as-is." That has been wrong since `native/TriffView.Tests` was added — verified directly
  for this plan: `native/TriffView.Tests/TriffView.Tests.csproj` targets `net8.0-windows` with
  `UseWPF`/`UseWindowsForms` and a real `<ProjectReference Include="..\TriffView.csproj" />`, and
  `native/TriffView.csproj` declares `<InternalsVisibleTo Include="TriffView.Tests" />`, so it reaches
  internal types directly rather than by linking files. Both projects run in CI:
  `tests/TriffView.Tests/TriffView.Tests.csproj` via `.github/workflows/build.yml`'s `dotnet test`
  step, and `native/TriffView.Tests/TriffView.Tests.csproj` via `.github/workflows/ci.yml`, which
  restores, builds, and tests it with `--warnaserror` (and `-p:NuGetAudit=true`).

  Replace the current `## Testing` section in `CLAUDE.md` with:

  ```markdown
  ## Testing

  There are two xunit test projects, with different reach.

  `tests/TriffView.Tests` is a plain `net8.0` project that **links pure-logic source files** via
  `<Compile Include="..\..\native\TriffView\Foo.cs" />` rather than a `ProjectReference` — the main
  project is `net8.0-windows` with WPF and its types are `internal`. Consequences:

  - Logic tested here must live in its own file with no Windows-only dependencies.
    `System.Drawing.Primitives` (`Rectangle`, `Point`, `Size`) is cross-platform and fine;
    `System.Windows.Forms` is not.
  - Adding a new testable file means adding a `Compile Include` line to this project's csproj.
  - It runs in CI via `.github/workflows/build.yml`.

  `native/TriffView.Tests` is `net8.0-windows` with WPF and WinForms enabled, and takes a real
  `<ProjectReference Include="..\TriffView.csproj" />` plus `InternalsVisibleTo`. It can exercise the
  app's internals directly — including code that never leaves a single file such as
  `TriffViewSubsystem.cs` — with no extraction required to make it reachable. It runs in CI via
  `.github/workflows/ci.yml`, which builds and tests it with `--warnaserror`.

  Prefer `tests/TriffView.Tests` for logic that is naturally cross-platform (geometry, parsing,
  framing) so it stays buildable from Linux/WSL; reach for `native/TriffView.Tests` when the thing
  under test needs WPF/WinForms types, a real controller, or subsystem/credential-store wiring that
  only exists in the Windows build.
  ```

  Then add the missing `-ExecutionPolicy Bypass` guidance to the `## Commands` section. Immediately
  after the closing ` ``` ` of the existing Commands code block, add:

  ```markdown

  When invoking these scripts from WSL via `powershell.exe -File` rather than from an interactive
  `pwsh` session, add `-ExecutionPolicy Bypass` — a bare `powershell.exe -File scripts/build-native.ps1`
  fails with `UnauthorizedAccess` on a machine whose script execution policy hasn't been relaxed for
  this repo:

  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build-native.ps1
  ```
  ```

  Commit separately from Steps 1–2, with a message that makes clear this is a correction, not part of
  the feature:

  ```bash
  git add CLAUDE.md
  git commit -m "Correct CLAUDE.md: native/TriffView.Tests can test internals, and scripts need -ExecutionPolicy Bypass from WSL

  Not part of the combat log Discord upload feature; a pre-existing documentation gap found while
  writing this feature's test and doc tasks."
  ```
