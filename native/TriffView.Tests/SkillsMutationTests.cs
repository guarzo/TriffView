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

        // State is posted on the failure path too, and must show no pin.
        var last = messages.Last(json => json.Contains("triffskills:state", StringComparison.Ordinal));
        var state = JsonNode.Parse(last)!;
        var pinnedIds = state["pinnedCharacterIds"]?.AsArray().Select(node => (long?)node).ToArray() ?? [];
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

    private void SeedCharacter(long characterId)
    {
        var state = new TriffSkillsState();
        var character = state.Upsert(characterId);
        character.CharacterName = $"Pilot {characterId}";
        Assert.True(state.TrySave(out var error), error);
    }

    private static TriffSkillsController Controller(ConcurrentQueue<string> messages, Func<string?>? saveState)
        => new(
            // Serialize with the same options production uses to post to the WebView2 client
            // (WebMessageJson.Options, camelCase) so these tests inspect byte-identical messages
            // to what the web side actually receives, rather than a differently-cased view.
            value => messages.Enqueue(JsonSerializer.Serialize(value, WebMessageJson.Options)),
            new MemoryCredentials(),
            new EsiClient(new HttpClient(new StubHandler()), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }, "TriffView.Tests/1.0"),
            new ControlledSso(),
            TimeProvider.System,
            saveState);

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
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
