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
