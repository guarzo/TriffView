using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using TriffView.Eve;
using TriffView.TriffSkills;
using Xunit;

namespace TriffView.Tests;

public class SkillsMutationTests : IDisposable
{
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "triffskills-mutation", Guid.NewGuid().ToString("N"));

    public SkillsMutationTests() => TriffSkillsPaths.OverrideRoot(_dir);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        finally { TriffSkillsPaths.ClearOverride(); }
    }

    [Fact]
    public void SetPinnedIsReflectedInTheNextState()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null);

        controller.HandleWebMessage("triffskills:set-pinned", JsonNode.Parse("""{"characterId":9001,"pinned":true}""")!.AsObject());

        JsonNode? state = null;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                state = messages
                    .Select(json => JsonNode.Parse(json))
                    .LastOrDefault(node => (string?)node?["type"] == "triffskills:state");
                var pinnedIds = state?["pinnedCharacterIds"]?.AsArray().Select(node => (long?)node).ToArray() ?? [];
                return pinnedIds.Contains(9001L);
            }, SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void AFailedSaveRestoresThePriorPins()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => "disk full");

        controller.HandleWebMessage("triffskills:set-pinned", JsonNode.Parse("""{"characterId":9001,"pinned":true}""")!.AsObject());

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("set-pinned", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        // State is posted on the failure path too, and must show no pin. SetPinned posts
        // the error and then the state synchronously in the same call, so by the time the
        // spin-wait above observes the error, both messages are already enqueued — but
        // messages.Last(...) would still be wrong to use: at the instant the error first
        // becomes visible, the last "state" message in the queue could still be the one
        // PostState posted during controller construction (before this handler ever ran),
        // which trivially has no pin and would make this assertion pass for the wrong
        // reason. Take the snapshot after the wait, find the error's position in it, and
        // require the specific state message that follows it.
        var snapshot = messages.ToArray();
        var errorIndex = Array.FindIndex(snapshot, json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("set-pinned", StringComparison.Ordinal));
        var stateAfterError = snapshot
            .Skip(errorIndex + 1)
            .Select(json => JsonNode.Parse(json))
            .FirstOrDefault(node => (string?)node?["type"] == "triffskills:state");
        Assert.NotNull(stateAfterError);
        var pinnedIds = stateAfterError!["pinnedCharacterIds"]?.AsArray().Select(node => (long?)node).ToArray() ?? [];
        Assert.DoesNotContain(9001L, pinnedIds);
    }

    [Fact]
    public void AFailedSaveRestoresThePriorGroupName()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: null);
        controller.HandleWebMessage("triffskills:save-group", JsonNode.Parse("""{"name":"Haulers","characterIds":[9001]}""")!.AsObject());

        using var failing = Controller(messages, saveState: () => "disk full");
        failing.HandleWebMessage("triffskills:save-group", JsonNode.Parse("""{"originalName":"Haulers","name":"Renamed","characterIds":[9001]}""")!.AsObject());

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("save-group", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        var last = messages.Last(json => json.Contains("triffskills:state", StringComparison.Ordinal));
        var state = JsonNode.Parse(last)!;
        var groupNames = state["characterGroups"]?.AsArray().Select(node => (string?)node?["name"]).ToArray() ?? [];
        Assert.Contains("Haulers", groupNames);
        Assert.DoesNotContain("Renamed", groupNames);
    }

    [Fact]
    public void ADuplicateGroupNameIsRejected()
    {
        SeedCharacter(9001);
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null);

        controller.HandleWebMessage("triffskills:save-group", JsonNode.Parse("""{"name":"Haulers","characterIds":[9001]}""")!.AsObject());
        controller.HandleWebMessage("triffskills:save-group", JsonNode.Parse("""{"name":"haulers","characterIds":[9001]}""")!.AsObject());

        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json => json.Contains("triffskills:error", StringComparison.Ordinal) && json.Contains("save-group", StringComparison.Ordinal)), SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void SelectPlanIsReflectedAsSelectedPlanNameInTheNextState()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null);

        controller.HandleWebMessage("triffskills:select-plan", new JsonObject { ["planName"] = PlanStore.StarterPlanName });

        JsonNode? state = null;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                state = messages
                    .Select(json => JsonNode.Parse(json))
                    .LastOrDefault(node => (string?)node?["type"] == "triffskills:state");
                return (string?)state?["selectedPlanName"] == PlanStore.StarterPlanName;
            }, SettleTimeout),
            string.Join(Environment.NewLine, messages));
    }

    [Fact]
    public void ACommitWithANameOverrideAndARevisionMismatchIsRejected()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null, handler: new SkillResolvingHandler());

        controller.HandleWebMessage("triffskills:preview-plan", JsonNode.Parse("""
            {"requestId":"req_mismatch","revision":1,"name":"Alpha Plan","contents":"CPU Management IV"}
            """)!.AsObject());
        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json =>
            {
                var node = JsonNode.Parse(json);
                return (string?)node?["type"] == "triffskills:plan-preview" && (string?)node?["requestId"] == "req_mismatch" && (bool?)node?["ok"] == true;
            }), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        messages.Clear();

        // The preview was stored under revision 1; committing against revision 2 must be
        // treated the same as a preview that no longer exists, regardless of the name
        // override supplied alongside it.
        controller.HandleWebMessage("triffskills:commit-plan", JsonNode.Parse("""
            {"requestId":"req_mismatch","revision":2,"replace":false,"name":"Renamed Alpha"}
            """)!.AsObject());

        JsonNode? commit = null;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                commit = messages
                    .Select(json => JsonNode.Parse(json))
                    .FirstOrDefault(node => (string?)node?["type"] == "triffskills:plan-commit" && (string?)node?["requestId"] == "req_mismatch");
                return commit is not null;
            }, SettleTimeout),
            string.Join(Environment.NewLine, messages));

        Assert.False((bool?)commit!["ok"], commit.ToJsonString());
        Assert.True((bool?)commit["expired"], commit.ToJsonString());
        Assert.False(File.Exists(Path.Combine(TriffSkillsPaths.PlansDir, "Alpha Plan.txt")));
        Assert.False(File.Exists(Path.Combine(TriffSkillsPaths.PlansDir, "Renamed Alpha.txt")));
    }

    [Fact]
    public void ACommitRejectedForAnInvalidNameLeavesThePendingPreviewIntactForRetry()
    {
        var messages = new ConcurrentQueue<string>();
        using var controller = Controller(messages, saveState: () => null, handler: new SkillResolvingHandler());

        controller.HandleWebMessage("triffskills:preview-plan", new JsonObject
        {
            ["requestId"] = "req_retry",
            ["revision"] = 1,
            ["name"] = "Beta Plan",
            ["contents"] = "CPU Management IV",
        });
        Assert.True(
            SpinWait.SpinUntil(() => messages.Any(json =>
            {
                var node = JsonNode.Parse(json);
                return (string?)node?["type"] == "triffskills:plan-preview" && (string?)node?["requestId"] == "req_retry" && (bool?)node?["ok"] == true;
            }), SettleTimeout),
            string.Join(Environment.NewLine, messages));

        messages.Clear();

        // "CON" is a reserved Windows device name: the override is rejected, but per
        // PlanImportWorkflow.Commit the pending preview is deliberately left in place
        // rather than removed.
        controller.HandleWebMessage("triffskills:commit-plan", new JsonObject
        {
            ["requestId"] = "req_retry",
            ["revision"] = 1,
            ["replace"] = false,
            ["name"] = "CON",
        });

        JsonNode? firstCommit = null;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                firstCommit = messages
                    .Select(json => JsonNode.Parse(json))
                    .FirstOrDefault(node => (string?)node?["type"] == "triffskills:plan-commit" && (string?)node?["requestId"] == "req_retry");
                return firstCommit is not null;
            }, SettleTimeout),
            string.Join(Environment.NewLine, messages));
        Assert.False((bool?)firstCommit!["ok"], firstCommit.ToJsonString());
        Assert.False((bool?)firstCommit["expired"], firstCommit.ToJsonString());
        Assert.False(File.Exists(Path.Combine(TriffSkillsPaths.PlansDir, "Beta Plan.txt")));

        messages.Clear();

        // Retry under the same requestId/revision with a corrected name — no new preview
        // is requested, exactly as the clipboard-import dialog's retry-in-place flow does.
        controller.HandleWebMessage("triffskills:commit-plan", new JsonObject
        {
            ["requestId"] = "req_retry",
            ["revision"] = 1,
            ["replace"] = false,
            ["name"] = "Beta Plan Renamed",
        });

        JsonNode? secondCommit = null;
        Assert.True(
            SpinWait.SpinUntil(() =>
            {
                secondCommit = messages
                    .Select(json => JsonNode.Parse(json))
                    .FirstOrDefault(node => (string?)node?["type"] == "triffskills:plan-commit" && (string?)node?["requestId"] == "req_retry");
                return secondCommit is not null;
            }, SettleTimeout),
            string.Join(Environment.NewLine, messages));
        Assert.True((bool?)secondCommit!["ok"], secondCommit.ToJsonString());
        Assert.True(File.Exists(Path.Combine(TriffSkillsPaths.PlansDir, "Beta Plan Renamed.txt")));
    }

    private void SeedCharacter(long characterId)
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(characterId);
        character.CharacterName = $"Pilot {characterId}";
        Assert.True(state.TrySave(out var error), error);
    }

    private static TriffSkillsController Controller(ConcurrentQueue<string> messages, Func<string?>? saveState, HttpMessageHandler? handler = null)
        => new(
            // Serialize with the same options production uses to post to the WebView2 client
            // (WebMessageJson.Options, camelCase) so these tests inspect byte-identical messages
            // to what the web side actually receives, rather than a differently-cased view.
            value => messages.Enqueue(JsonSerializer.Serialize(value, WebMessageJson.Options)),
            new MemoryCredentials(),
            new EsiClient(new HttpClient(handler ?? new StubHandler()), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0"),
            new ControlledSso(),
            TimeProvider.System,
            saveState);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    // Resolves "CPU Management" (the base skill name after SkillPlanParser strips the roman
    // numeral level) as a genuine EVE skill, so the plan-preview/commit tests below can
    // exercise a preview that actually succeeds end to end rather than failing at ESI
    // resolution like StubHandler. Local copy to match this repo's established
    // per-test-class pattern (see the identical class in ClipboardControllerTests).
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
    // ControllerLifecycleTests, ClipboardControllerTests, CombatLogUploadFlowTests,
    // CombatLogUploadWebhookTests) rather than promoting to a shared internal type.
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
            => throw new NotImplementedException("Not exercised by skills-mutation tests.");

        public Task<EveValidatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
            => throw new NotImplementedException("Not exercised by skills-mutation tests.");
    }
}
