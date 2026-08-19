using System.Text.Json.Nodes;
using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// Builds a minimal client-state entry through the same <see cref="ClientAudioStatus.Resolve"/>
/// production code <c>TriffViewSubsystem.PostState</c> calls, rather than re-implementing the
/// "off" fallback here - a test that only re-asserted its own copy of that logic would prove
/// nothing about the real projection.
/// </summary>
internal static class TestHarness
{
    public static JsonObject BuildClientState(uint processId, string characterName, string? audioStatus)
    {
        var statusesByProcessId = audioStatus is null
            ? new Dictionary<uint, string>()
            : new Dictionary<uint, string> { [processId] = audioStatus };

        return new JsonObject
        {
            ["processId"] = processId,
            ["characterName"] = characterName,
            ["audioStatus"] = ClientAudioStatus.Resolve(statusesByProcessId, processId),
        };
    }
}

public class AudioStateProjectionTests
{
    [Fact]
    public void ClientStateIncludesAudioStatus()
    {
        var json = TestHarness.BuildClientState(processId: 1234, characterName: "Pilot",
                                                audioStatus: "monitoring");
        Assert.Equal("monitoring", json["audioStatus"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownAudioStatusFallsBackToOff()
    {
        var json = TestHarness.BuildClientState(processId: 1234, characterName: "Pilot",
                                                audioStatus: null);
        Assert.Equal("off", json["audioStatus"]!.GetValue<string>());
    }
}
