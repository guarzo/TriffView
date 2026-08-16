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
                    && json.Contains("\"action\":\"upload-combat-logs\"", StringComparison.Ordinal)),
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

            // These fields (fileCount, characters) are exactly what the merge in
            // UploadCombatLogs pulls from the CombatLogExportResult rather than
            // from upload.ToState() alone -- UploadAsync never sees the export, so
            // a regression there would show up here as fileCount:0 / characters:[].
            Assert.Contains("\"succeeded\":true", reply, StringComparison.Ordinal);
            Assert.Contains("\"fileCount\":1", reply, StringComparison.Ordinal);
            Assert.Contains("\"characters\":[\"Pilot One\"]", reply, StringComparison.Ordinal);
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
    /// path from Task 3's constructor seam) be exercised end to end without
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
