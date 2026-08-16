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
