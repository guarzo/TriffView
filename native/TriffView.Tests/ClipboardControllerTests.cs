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
        // ok must stay true and the reason must arrive as nameError. The skill
        // in the clipboard must actually resolve here (unlike the always-503
        // StubHandler used elsewhere in this class) because assertion 3 below
        // needs the preview to genuinely succeed and be stored as pending.
        //
        // The clipboard is a mutable local rather than a fixed string so the
        // cycles below can feed a real copy-plan export back into the next
        // import-clipboard read, the same way a human round-tripping through
        // the OS clipboard would.
        var messages = new ConcurrentQueue<string>();
        var clipboard = "# CON\r\nCPU Management IV\r\n";
        using var controller = Controller(() => clipboard, text => clipboard = text, messages, new SkillResolvingHandler());

        controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = "req_0002", ["revision"] = 1 });

        JsonNode? preview = null;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                preview = messages
                    .Select(json => JsonNode.Parse(json))
                    .FirstOrDefault(node => (string?)node?["type"] == "triffskills:clipboard-preview" && (string?)node?["requestId"] == "req_0002");
                return preview is not null;
            }, SettleTimeout),
            string.Join(Environment.NewLine, messages));

        // 1. The plan itself parsed and resolved fine.
        Assert.True((bool?)preview!["ok"], preview.ToJsonString());
        Assert.Equal(1, (int?)preview["requirementCount"]);

        // 2. The candidate name failed validation and is reported through nameError, not diagnostics.
        Assert.Equal(string.Empty, (string?)preview["name"]);
        Assert.False(string.IsNullOrEmpty((string?)preview["nameError"]));

        // 3. The real discriminator: on a buggy implementation that previews under the
        // candidate name, PlanImportWorkflow.PreviewAsync rejects "CON" before storing a
        // pending preview, so this commit finds nothing and the file is never written.
        controller.HandleWebMessage("triffskills:commit-plan", new JsonObject
        {
            ["requestId"] = "req_0002",
            ["revision"] = 1,
            ["replace"] = false,
            ["name"] = "Imported CON Plan",
        });

        var path = Path.Combine(TriffSkillsPaths.PlansDir, "Imported CON Plan.txt");
        Assert.True(
            SpinWait.SpinUntil(() => File.Exists(path), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        // 4. The saved plan file must never carry the "# name" title line back out of
        // import — that line exists only in the clipboard's copy-plan format, never in a
        // committed plan on disk.
        AssertNoTitleLine(path);

        // 5. Repeat export (copy-plan) -> import (import-clipboard) -> commit (replace)
        // three times on the same plan. This is the accumulation regression the spec
        // warns about: every export prepends a fresh "# name" title, so if import ever
        // stopped stripping it, each cycle would leave one more stale title line sitting
        // in the saved file.
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            messages.Clear();
            var requestId = $"req_cycle_{cycle}";

            controller.HandleWebMessage("triffskills:copy-plan", new JsonObject { ["planName"] = "Imported CON Plan" });
            Assert.StartsWith("# Imported CON Plan", clipboard, StringComparison.Ordinal);

            controller.HandleWebMessage("triffskills:import-clipboard", new JsonObject { ["requestId"] = requestId, ["revision"] = 1 });
            Assert.True(
                SpinWait.SpinUntil(() => messages.Any(json => json.Contains("clipboard-preview", StringComparison.Ordinal) && json.Contains(requestId, StringComparison.Ordinal)), SettleTimeout),
                string.Join(Environment.NewLine, messages));

            controller.HandleWebMessage("triffskills:commit-plan", new JsonObject
            {
                ["requestId"] = requestId,
                ["revision"] = 1,
                ["replace"] = true,
                ["name"] = "Imported CON Plan",
            });
            Assert.True(
                SpinWait.SpinUntil(() => messages.Any(json =>
                {
                    var node = JsonNode.Parse(json);
                    return (string?)node?["type"] == "triffskills:plan-commit" && (string?)node?["requestId"] == requestId && (bool?)node?["ok"] == true;
                }), SettleTimeout),
                string.Join(Environment.NewLine, messages));

            AssertNoTitleLine(path);
        }
    }

    // Parses the file line-by-line rather than substring-matching the whole contents, so
    // this can't be satisfied by coincidence (e.g. a skill name that happens to contain "#").
    private static void AssertNoTitleLine(string path)
    {
        var lines = File.ReadAllLines(path);
        Assert.DoesNotContain(lines, line => line.TrimStart().StartsWith('#'));
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
        ConcurrentQueue<string> messages,
        HttpMessageHandler? handler = null)
        => new(
            value => messages.Enqueue(JsonSerializer.Serialize(value)),
            new MemoryCredentials(),
            new EsiClient(new HttpClient(handler ?? new StubHandler()), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0"),
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

    // Resolves "CPU Management" (the base skill name after SkillPlanParser strips the roman
    // numeral level) as a genuine EVE skill, so tests using it can exercise a preview that
    // actually succeeds end to end rather than failing at ESI resolution like StubHandler.
    private sealed class SkillResolvingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v3/universe/ids/") return Task.FromResult(Response(System.Net.HttpStatusCode.OK, "{\"inventory_types\":[{\"id\":3426,\"name\":\"CPU Management\"}]}"));
            if (path == "/v3/universe/types/3426/") return Task.FromResult(Response(System.Net.HttpStatusCode.OK, "{\"group_id\":255}"));
            if (path == "/v1/universe/groups/255/") return Task.FromResult(Response(System.Net.HttpStatusCode.OK, "{\"category_id\":16}"));
            return Task.FromResult(Response(System.Net.HttpStatusCode.NotFound, "{\"error\":\"unexpected\"}"));
        }

        private static HttpResponseMessage Response(System.Net.HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
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
