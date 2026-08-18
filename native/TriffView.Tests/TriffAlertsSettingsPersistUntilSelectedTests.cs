using System.Text.Json;
using TriffView.Alerts;
using TriffView.Preview;
using Xunit;

namespace TriffView.Tests;

public class TriffAlertsSettingsPersistUntilSelectedTests
{
    [Fact]
    public void DefaultsToFalse()
    {
        var settings = TriffAlertsSettings.CreateDefault();

        Assert.False(settings.PersistUntilSelected);
    }

    [Fact]
    public void RoundTripsThroughToState()
    {
        // ToState() is declared as `object` and returns an anonymous type
        // (TriffAlertsService.cs:60-75). Do NOT reach into it with `dynamic`: anonymous
        // types are internal to their assembly, cross-assembly dynamic binding on one is
        // at best fragile, and — because dynamic binding is deferred to runtime — a missing
        // member would not fail the build, so the TDD red step would not mean what it says.
        // Assert on the serialized JSON instead. That is also what actually crosses the
        // WebView2 boundary to the settings UI, so it is the more faithful test.
        var settings = TriffAlertsSettings.CreateDefault();
        settings.PersistUntilSelected = true;

        var json = JsonSerializer.Serialize(settings.ToState());

        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetProperty("persistUntilSelected").GetBoolean());
    }

    [Fact]
    public void SettingsWrittenByAnOlderBuild_KeyAbsent_DeserializesToFalse()
    {
        // No "persistUntilSelected" key at all -- simulates a settings.json
        // written before this feature existed.
        const string json = """
        {
            "profiles": [],
            "alerts": {
                "defaultsVersion": 2,
                "enabled": true,
                "pveMode": true,
                "masterVolume": 0.5,
                "events": {}
            }
        }
        """;

        var settings = TriffViewSettings.FromJson(json);

        Assert.False(settings.Alerts.PersistUntilSelected);
    }

    [Fact]
    public void UnknownKeyInAlertsJson_IsIgnoredRatherThanThrowing()
    {
        // Simulates a NEWER build's settings.json being read by this build,
        // or a hand-edited file with an extra key. System.Text.Json ignores
        // unrecognized members by default; this pins that behavior for the
        // alerts section specifically.
        const string json = """
        {
            "profiles": [],
            "alerts": {
                "defaultsVersion": 2,
                "enabled": true,
                "pveMode": true,
                "masterVolume": 0.5,
                "persistUntilSelected": true,
                "someFutureAlertsKey": "unrecognized-value",
                "events": {}
            }
        }
        """;

        var settings = TriffViewSettings.FromJson(json);

        Assert.True(settings.Alerts.PersistUntilSelected);
        Assert.True(settings.Alerts.Enabled);
    }
}
