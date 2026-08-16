using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public void ClearCombatLogWebhookDeletesTheCredentialAndRepliesUnconfigured()
    {
        var messages = new ConcurrentQueue<string>();
        var credentials = new MemoryCredentials((TriffViewController.CombatLogWebhookCredentialTarget, "https://discord.com/api/webhooks/1/tok"));
        using var controller = Controller(credentials, messages);
        controller.Start();

        controller.HandleWebMessage("triffview:clear-combat-log-webhook", null);

        // One, not two: Start() reports the webhook inside triffview:state, so
        // the clear is the first message of this type the UI ever sees.
        Assert.True(SpinWait.SpinUntil(
            () => messages.Count(json => json.Contains("\"type\":\"triffview:combat-log-webhook\"", StringComparison.Ordinal)) >= 1,
            TimeSpan.FromSeconds(5)));
        var reply = messages.Last(json => json.Contains("\"type\":\"triffview:combat-log-webhook\"", StringComparison.Ordinal));
        Assert.Contains("\"configured\":false", reply, StringComparison.Ordinal);
        Assert.Null(credentials.Stored(TriffViewController.CombatLogWebhookCredentialTarget));
    }

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
