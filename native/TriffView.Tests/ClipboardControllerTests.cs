using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.Eve;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class ClipboardControllerTests : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-clipboard", Guid.NewGuid().ToString("N"));

    public ClipboardControllerTests() => TriffSkillsPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { TriffSkillsPaths.ClearOverride(); }
    }

    [Fact]
    public void CopyPlanWritesTheFileContentsBehindATitleLine()
    {
        var written = new List<string>();
        using var controller = Controller(() => string.Empty, written.Add, new ConcurrentQueue<string>());

        // PlanStore.EnsureSeeded created "Core Ship Skills" during construction.
        controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = PlanStore.StarterPlanName });

        var text = Assert.Single(written);
        Assert.StartsWith($"# {PlanStore.StarterPlanName}", text, StringComparison.Ordinal);
        Assert.Contains("CPU Management IV", text, StringComparison.Ordinal);
    }

    [Fact]
    public void CopyPlanReportsAnErrorForAnUnknownPlan()
    {
        var written = new List<string>();
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => string.Empty, written.Add, messages);

        controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = "No Such Plan" });

        Assert.Empty(written);
        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("copy-plan", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void CopyPlanReportsAnErrorWhenTheClipboardWriteThrows()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => string.Empty, _ => throw new InvalidOperationException("clipboard busy"), messages);

        controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = PlanStore.StarterPlanName });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("copy-plan", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void ImportFromClipboardStripsTheTitleAndReportsTheCandidateName()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => "# Mastadon\r\nCPU Management IV\r\n", _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0001", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("clipboard-preview", StringComparison.Ordinal) && json.Contains("Mastadon", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AnInvalidCandidateNameStillProducesACommittablePreview()
    {
        // "CON" is a reserved Windows device name. The plan itself is fine, so
        // ok must stay true and the reason must arrive as nameError.
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => "# CON\r\nCPU Management IV\r\n", _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0002", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(
                () => messages.Any(json =>
                    json.Contains("clipboard-preview", StringComparison.Ordinal) &&
                    json.Contains("req_0002", StringComparison.Ordinal) &&
                    json.Contains("nameError", StringComparison.Ordinal) &&
                    json.Contains("reserved", StringComparison.OrdinalIgnoreCase)),
                SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AnEmptyClipboardReportsADiagnosticRatherThanSilence()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => string.Empty, _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0003", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("req_0003", StringComparison.Ordinal) && json.Contains("clipboard", StringComparison.OrdinalIgnoreCase)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AReadThatThrowsSurfacesAsAnError()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(() => throw new InvalidOperationException("clipboard busy"), _ => { }, messages);

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0004", ["revision"] = 1 });

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("import-clipboard", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    private static TriffSkillsController Controller(
        Func<string> readClipboard,
        Action<string> writeClipboard,
        ConcurrentQueue<string> messages)
        => new(
            value => messages.Enqueue(JsonSerializer.Serialize(value)),
            new MemoryCredentials(),
            new EsiClient(new HttpClient(new StubHandler()), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0"),
            new ControlledSso(),
            TimeProvider.System,
            saveState: () => null,
            readClipboard: readClipboard,
            writeClipboard: writeClipboard);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    // Local copy to match this repo's established per-test-class pattern (see
    // ControllerLifecycleTests, CombatLogUploadFlowTests, CombatLogUploadWebhookTests) rather
    // than promoting to a shared internal type for a fourth caller.
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

    private sealed class ControlledSso : IEveSsoClient
    {
        public Task<EveValidatedToken> AuthorizeAsync(TimeSpan timeout, CancellationToken cancellationToken)
            => throw new NotImplementedException("Not exercised by clipboard tests.");

        public Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
            => throw new NotImplementedException("Not exercised by clipboard tests.");
    }
}
