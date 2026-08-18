using System.Collections.Concurrent;
using System.Net;
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

    [Fact]
    public void UploadCombatLogsReportsANoOverlapWindowAsAnErrorNotASilentSkip()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        try
        {
            var messages = new ConcurrentQueue<string>();
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
            using var controller = Controller(credentials, messages, gamelogsPath: gamelogsDir);

            controller.HandleWebMessage(
                "triffview:upload-combat-logs",
                JsonNode.Parse("""{"fromUtc":"2000-01-01T00:00:00Z","toUtc":"2000-01-01T00:01:00Z"}""")!.AsObject());

            Assert.True(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
                    && json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)
                    // The message too, not just the envelope: any error on this
                    // path would satisfy the type/action pair, including one
                    // raised before Export ever ran.
                    && json.Contains("No EVE logs overlap", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)));
        }
        finally
        {
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    // Not async: nothing here is awaited (the flow under test is async void and
    // is observed through the message queue), and an async method with no await
    // is a CS1998 warning, which CI's --warnaserror turns into a build failure.
    [Fact]
    public void UploadCombatLogsComposesExportAndUploadAgainstAFakeHandlerAndCleansUpItsTempFile()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            WriteFixtureGamelog(
                gamelogsDir, "20260101000000_1_Pilot_One.txt", "Pilot One", start,
                "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            var messages = new ConcurrentQueue<string>();
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
            using var controller = Controller(credentials, messages, gamelogsPath: gamelogsDir, handler: handler);

            controller.HandleWebMessage(
                "triffview:upload-combat-logs",
                JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{start.AddMinutes(1):O}}"}""")!.AsObject());

            Assert.True(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)));
            var reply = messages.Last(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal));

            // These fields (fileCount, characters, startUtc, endUtc) are exactly
            // what the merge in UploadCombatLogs pulls from the
            // CombatLogExportResult rather than from upload.ToState() alone --
            // UploadAsync never sees the export, so a regression there would show
            // up here as fileCount:0 / characters:[] / default timestamps. The
            // window in particular is checked nowhere else: the Discord content
            // line is formatted from the export result directly, not from the
            // merged one, so a dropped merge of those two fields is invisible
            // everywhere but here.
            Assert.Contains("\"succeeded\":true", reply, StringComparison.Ordinal);
            Assert.Contains("\"fileCount\":1", reply, StringComparison.Ordinal);
            Assert.Contains("\"characters\":[\"Pilot One\"]", reply, StringComparison.Ordinal);
            Assert.Contains($"\"startUtc\":\"{start:O}\"", reply, StringComparison.Ordinal);
            Assert.Contains($"\"endUtc\":\"{start.AddMinutes(1):O}\"", reply, StringComparison.Ordinal);
            Assert.Equal(1, handler.RequestCount);
            Assert.True(SpinWait.SpinUntil(
                () => !Directory.EnumerateFiles(TriffViewController.CombatLogUploadTempDir).Any(),
                TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    /// <summary>
    /// The case SuggestFileName's window/result split exists for: a manual UTC
    /// range builds a CombatLogFightWindow with no Characters (BuildCombatLogWindow
    /// constructs it before any log is read), so the staged temp file is named
    /// "triffview-fight-…" -- but the archive Discord actually receives must be
    /// named from the export result, which does know the pilot.
    /// </summary>
    [Fact]
    public void TheDiscordFileNameCarriesThePilotEvenForAManualTimeRange()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            WriteFixtureGamelog(
                gamelogsDir, "20260101000000_1_Pilot_One.txt", "Pilot One", start,
                "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            var messages = new ConcurrentQueue<string>();
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
            using var controller = Controller(credentials, messages, gamelogsPath: gamelogsDir, handler: handler);

            controller.HandleWebMessage(
                "triffview:upload-combat-logs",
                JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{start.AddMinutes(1):O}}"}""")!.AsObject());

            Assert.True(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)));

            // The multipart body's Content-Disposition line names the file that
            // was actually posted -- read as raw bytes rather than parsed, the
            // same way this suite already inspects LastRequestBytes elsewhere.
            // Unquoted rather than quoted: MultipartFormDataContent only quotes a
            // filename when it holds characters a bare token can't (see
            // CombatLogUploadStubServerTests.ExtractName's own comment on the
            // same quirk for part names), and this sanitized filename never does.
            var bodyText = Encoding.UTF8.GetString(handler.LastRequestBytes ?? Array.Empty<byte>());
            Assert.Contains(
                "filename=triffview-Pilot-One-20260101-0000Z.zip",
                bodyText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    [Fact]
    public void UploadCombatLogsReportsDroppedFilesOnASuccessfulUpload()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            // One more than CombatLogExport's 64-file cap (MaxFiles), so this
            // export is forced to drop exactly one.
            for (var i = 0; i < 65; i++)
            {
                WriteFixtureGamelog(
                    gamelogsDir, $"20260101000000_{i}_Pilot_{i}.txt", $"Pilot {i}", start,
                    "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");
            }

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            var messages = new ConcurrentQueue<string>();
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
            using var controller = Controller(credentials, messages, gamelogsPath: gamelogsDir, handler: handler);

            controller.HandleWebMessage(
                "triffview:upload-combat-logs",
                JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{start.AddMinutes(1):O}}"}""")!.AsObject());

            Assert.True(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)));
            var reply = messages.Last(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal));

            Assert.Contains("\"succeeded\":true", reply, StringComparison.Ordinal);
            Assert.Contains("\"droppedFileCount\":1", reply, StringComparison.Ordinal);

            var bodyText = Encoding.UTF8.GetString(handler.LastRequestBytes ?? Array.Empty<byte>());
            Assert.Contains("1 additional matching log file(s) were not included", bodyText, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    /// <summary>
    /// Drives the sweep through <c>Start()</c>, which is its only call site.
    /// That also reads the developer's real
    /// <c>%APPDATA%\TriffHud\triffview-settings.json</c> and installs a
    /// SetWinEventHook, neither of which TriffViewController exposes a seam for
    /// -- both read-only or unhooked by Dispose(), and already exercised the
    /// same way by every other Start() test in this suite.
    /// </summary>
    [Fact]
    public void StartSweepsStaleFightArchivesAndStagingFilesButKeepsFreshOnes()
    {
        var tempDir = TriffViewController.CombatLogUploadTempDir;
        Directory.CreateDirectory(tempDir);
        var staleZip = Path.Combine(tempDir, $"triffview-fight-{Guid.NewGuid():N}.zip");
        var staleStaging = Path.Combine(tempDir, $"triffview-fight-{Guid.NewGuid():N}.zip.{Guid.NewGuid():N}.tmp");
        var freshZip = Path.Combine(tempDir, $"triffview-fight-{Guid.NewGuid():N}.zip");
        var unrelatedOld = Path.Combine(tempDir, $"unrelated-{Guid.NewGuid():N}.zip");
        // Deliberately outside CombatLogUploadTempDir, same generated name shape
        // and same age as the stale zip above -- proves the sweep cannot reach a
        // user's own deliberately-saved export sharing SuggestFileName's output.
        var outsideStaleZip = Path.Combine(Path.GetTempPath(), $"triffview-fight-{Guid.NewGuid():N}.zip");
        File.WriteAllText(staleZip, "stale");
        File.WriteAllText(staleStaging, "stale-staging");
        File.WriteAllText(freshZip, "fresh");
        File.WriteAllText(unrelatedOld, "unrelated");
        File.WriteAllText(outsideStaleZip, "outside");
        var twoDaysAgo = DateTime.UtcNow.AddDays(-2);
        File.SetLastWriteTimeUtc(staleZip, twoDaysAgo);
        File.SetLastWriteTimeUtc(staleStaging, twoDaysAgo);
        File.SetLastWriteTimeUtc(unrelatedOld, twoDaysAgo);
        File.SetLastWriteTimeUtc(outsideStaleZip, twoDaysAgo);

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
            Assert.True(File.Exists(outsideStaleZip));
        }
        finally
        {
            foreach (var path in new[] { staleZip, staleStaging, freshZip, unrelatedOld, outsideStaleZip })
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }

    /// <summary>
    /// The staging path is derived from the deterministic SuggestFileName, so a
    /// second concurrent upload of the same window would compress into the same
    /// file and each run's finally would delete it under the other. Nothing on
    /// the web side prevents that -- there is no client-side in-flight state this
    /// half can rely on -- so the guard has to be here.
    /// </summary>
    [Fact]
    public void UploadCombatLogsRefusesASecondRunWhileOneIsStillInFlight()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        using var release = new ManualResetEventSlim(false);
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            WriteFixtureGamelog(
                gamelogsDir, "20260101000000_1_Pilot_One.txt", "Pilot One", start,
                "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");

            // Holds the first upload open at the POST, so the second request is
            // sent while the first is provably still running rather than
            // whenever the scheduler happens to get to it.
            var handler = new FakeHttpMessageHandler(_ =>
            {
                release.Wait(TimeSpan.FromSeconds(30));
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            });
            var messages = new ConcurrentQueue<string>();
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
            using var controller = Controller(credentials, messages, gamelogsPath: gamelogsDir, handler: handler);
            var body = JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{start.AddMinutes(1):O}}"}""")!.AsObject();

            controller.HandleWebMessage("triffview:upload-combat-logs", body);
            Assert.True(SpinWait.SpinUntil(() => handler.RequestCount >= 1, TimeSpan.FromSeconds(10)));

            controller.HandleWebMessage("triffview:upload-combat-logs", body);
            // The observable is that the second run posts nothing at all. It is
            // not enough to watch the request count: without the guard the second
            // run still never reaches Discord, because its Export tries to move a
            // fresh archive over the one the first run holds open and fails with
            // a sharing violation -- which is exactly the corruption the guard
            // exists to prevent, and it surfaces as an extra upload-combat-logs
            // error message.
            Assert.False(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
                    && json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(3)));
            Assert.Equal(1, handler.RequestCount);

            release.Set();
            Assert.True(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)));
            Assert.Equal(
                1,
                messages.Count(json => json.Contains("\"type\":\"triffview:combat-log-upload\"", StringComparison.Ordinal)));
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            release.Set();
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    /// <summary>
    /// UploadAsync only redacts the outcomes it can classify (server responses,
    /// OperationCanceledException, HttpRequestException). Anything else escapes
    /// to UploadCombatLogs' generic catch, which is the last place that can strip
    /// the token before it reaches PostError -- and an exception message is the
    /// one outbound string that can carry the whole request URI without anyone
    /// having put it there.
    /// </summary>
    [Fact]
    public void UploadCombatLogsRedactsTheWebhookTokenOutOfAnUnclassifiedException()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            WriteFixtureGamelog(
                gamelogsDir, "20260101000000_1_Pilot_One.txt", "Pilot One", start,
                "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");

            // Neither OperationCanceledException nor HttpRequestException, so
            // UploadAsync's own redaction never sees it.
            var handler = new FakeHttpMessageHandler(request =>
                throw new InvalidOperationException($"exploded talking to {request.RequestUri}"));
            var messages = new ConcurrentQueue<string>();
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1234/sekrit-token-value"));
            using var controller = Controller(credentials, messages, gamelogsPath: gamelogsDir, handler: handler);

            controller.HandleWebMessage(
                "triffview:upload-combat-logs",
                JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{start.AddMinutes(1):O}}"}""")!.AsObject());

            Assert.True(SpinWait.SpinUntil(
                () => messages.Any(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal)
                    && json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(10)));
            var reply = messages.Last(json => json.Contains("\"type\":\"triffview:error\"", StringComparison.Ordinal));
            Assert.DoesNotContain("sekrit-token-value", reply, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    /// <summary>
    /// Regression for the hazard fixed in UploadCombatLogs' catch: the success post
    /// (<c>_postToHud(new { type = "triffview:combat-log-upload", ... })</c>) is inside the
    /// same try as everything else, so a WebView torn down between the disposal check and
    /// that post throws from inside the try, straight into the catch. If that catch called
    /// PostError directly -- an unguarded _postToHud itself -- it would throw the same
    /// exception a second time, unhandled, out of this async void method, reaching the
    /// thread pool and taking the whole process down.
    ///
    /// postToHud here throws unconditionally, the same way a torn-down WebView would fail
    /// every post rather than only the first one it is asked to make. It cannot be observed
    /// through TriffViewDiagnostics.LogPath -- that is a real, shared file this fork's own
    /// running instance was found to be actively writing to on the machine this test was
    /// developed on, and TriffViewDiagnostics.Log silently drops its write on any contention
    /// (by design, see its own header comment), which made that assertion flake against a
    /// live app rather than against this fix. The invocation count is proof enough instead:
    /// exactly two throwing posts (the success post, then the catch's guarded retry through
    /// PostError) followed by silence and a completed cleanup is only reachable if the second
    /// throw was caught rather than left to reach the thread pool -- an unguarded regression
    /// would take the whole test process down before ever reaching the assertions below,
    /// rather than fail one of them.
    /// </summary>
    [Fact]
    public void UploadCombatLogsSurvivesThePostToHudForItsSuccessReportThrowing()
    {
        var gamelogsDir = CreateFixtureGamelogsDir();
        try
        {
            var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            WriteFixtureGamelog(
                gamelogsDir, "20260101000000_1_Pilot_One.txt", "Pilot One", start,
                "[ 2026.01.01 00:00:05 ] (combat) hits you for 10 damage\r\n");

            var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
            var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
            var postCount = 0;

            using var controller = new TriffViewController(
                Dispatcher.CurrentDispatcher,
                _ =>
                {
                    Interlocked.Increment(ref postCount);
                    throw new ObjectDisposedException("webview torn down between the disposal check and a post");
                },
                reassertHudTopmost: () => { },
                applySettingsAlwaysOnTop: _ => { },
                credentials,
                gamelogsDir,
                new HttpClient(handler));

            controller.HandleWebMessage(
                "triffview:upload-combat-logs",
                JsonNode.Parse($$"""{"fromUtc":"{{start:O}}","toUtc":"{{start.AddMinutes(1):O}}"}""")!.AsObject());

            Assert.True(SpinWait.SpinUntil(
                () => Interlocked.CompareExchange(ref postCount, 0, 0) >= 2,
                TimeSpan.FromSeconds(10)));

            // Reaching the unconditional finally (temp file cleanup) despite both posts
            // throwing is itself part of the proof: an unhandled exception on this
            // async void's continuation would have unwound past the finally, not through
            // it, on its way to the thread pool.
            Assert.True(SpinWait.SpinUntil(
                () => !Directory.EnumerateFiles(TriffViewController.CombatLogUploadTempDir).Any(),
                TimeSpan.FromSeconds(5)));

            // No third attempt -- confirms the method returned after the catch rather
            // than looping or retrying the post again.
            Thread.Sleep(200);
            Assert.Equal(2, Interlocked.CompareExchange(ref postCount, 0, 0));
        }
        finally
        {
            Directory.Delete(gamelogsDir, recursive: true);
        }
    }

    private static TriffViewController Controller(
        MemoryCredentials credentials,
        ConcurrentQueue<string> messages,
        string? gamelogsPath = null,
        HttpMessageHandler? handler = null)
    {
        return new TriffViewController(
            Dispatcher.CurrentDispatcher,
            value => messages.Enqueue(JsonSerializer.Serialize(value)),
            reassertHudTopmost: () => { },
            applySettingsAlwaysOnTop: _ => { },
            credentials,
            gamelogsPath,
            handler == null ? null : new HttpClient(handler));
    }

    /// <summary>
    /// Stands in for Discord's endpoint the same way StubWebhookServer does, but
    /// as an in-process HttpMessageHandler rather than a real socket -- this is
    /// what lets a genuine TriffViewController (built with a fixture Gamelogs
    /// path from the constructor's gamelogsPath seam) be exercised end to end without
    /// touching the network or the credential store's Discord-host allowlist
    /// twice.
    /// </summary>
    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _requestCount;

        /// <summary>
        /// Written on whatever thread HttpClient resumes on and read from the
        /// test thread, so it goes through Interlocked for the same reason
        /// StubWebhookServer's counter does.
        /// </summary>
        public int RequestCount => Interlocked.CompareExchange(ref _requestCount, 0, 0);

        public byte[]? LastRequestBytes { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBytes = request.Content == null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            // After the body capture, so a test that waits on the count and then
            // reads the bytes can never observe the count without them.
            Interlocked.Increment(ref _requestCount);
            return respond(request);
        }
    }

    private static string CreateFixtureGamelogsDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "triffview-upload-fixture", Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>
    /// Matches the real Gamelog header format CombatLogExport.cs parses:
    /// "Listener:" is the pilot identity, "Session Started:" is read as a
    /// fallback window anchor, but SelectLogs actually windows against the
    /// file's real filesystem LastWriteTimeUtc (i.e. "now", when this writes
    /// it) -- which is why every fixture window below starts in the past and
    /// leaves its end open-ended, the same shape already proven out by the
    /// no-overlap test above.
    /// </summary>
    private static void WriteFixtureGamelog(string dir, string fileName, string listener, DateTime sessionStartUtc, string body)
    {
        File.WriteAllText(
            Path.Combine(dir, fileName),
            "Gamelog\r\n\r\n" +
            $"            Listener: {listener}\r\n" +
            $"  Session Started: {sessionStartUtc:yyyy.MM.dd HH:mm:ss}\r\n\r\n" +
            body);
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
