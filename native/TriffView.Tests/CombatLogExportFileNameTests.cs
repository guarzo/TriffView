using TriffView.Alerts;
using Xunit;

namespace TriffView.Tests;

/// <summary>
/// SuggestFileName's own unit tests already live in
/// tests/TriffView.Tests/CombatLogExportTests.cs, which links CombatLogExport.cs
/// directly rather than referencing TriffView.csproj. This file duplicates only
/// the SuggestFileName cases here too, because this project (with the real
/// ProjectReference) is the one the fork's Discord-upload filename change is
/// actually verified against -- see CombatLogUploadFlowTests.cs for the
/// integration-level coverage of the same feature.
/// </summary>
public class CombatLogExportFileNameTests
{
    private static readonly DateTime Noon = new(2026, 8, 18, 20, 10, 0, DateTimeKind.Utc);

    [Fact]
    public void NoPilotsFallsBackToTheGenericName()
    {
        var window = new CombatLogFightWindow { StartUtc = Noon };

        Assert.Equal("triffview-fight-20260818-2010Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void OnePilotCarriesJustTheName()
    {
        var window = new CombatLogFightWindow { StartUtc = Noon, Characters = new[] { "Talon Vex" } };

        Assert.Equal("triffview-Talon-Vex-20260818-2010Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void ThreePilotsAddTheExtraCountAfterTheLeadName()
    {
        // Characters is taken as already sorted ordinal-ignore-case ascending
        // (both producers sort it that way) -- "Talon Vex" is first alphabetically.
        var window = new CombatLogFightWindow
        {
            StartUtc = Noon,
            Characters = new[] { "Talon Vex", "Bravo Pilot", "Charlie Pilot" },
        };

        Assert.Equal("triffview-Talon-Vex+2-20260818-2010Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void ApostrophesAreDroppedNotReplaced()
    {
        var window = new CombatLogFightWindow { StartUtc = Noon, Characters = new[] { "O'Neill" } };

        Assert.Equal("triffview-ONeill-20260818-2010Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void ANameThatSanitizesToNothingFallsBackToTheGenericForm()
    {
        var window = new CombatLogFightWindow { StartUtc = Noon, Characters = new[] { "''!!" } };

        Assert.Equal("triffview-fight-20260818-2010Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void MixedJunkCharactersAreStrippedAndHyphenRunsCollapse()
    {
        var window = new CombatLogFightWindow { StartUtc = Noon, Characters = new[] { "Ex!!  Cha--r*(acter" } };

        Assert.Equal("triffview-Ex-Cha-racter-20260818-2010Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void TheTimestampIsMinutePrecisionUtcWithATrailingZ()
    {
        var window = new CombatLogFightWindow
        {
            StartUtc = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Characters = new[] { "Alpha" },
        };

        Assert.Equal("triffview-Alpha-20260304-0506Z.zip", CombatLogExport.SuggestFileName(window));
    }

    [Fact]
    public void TheResultOverloadUsesTheResultsCharactersNotTheWindows()
    {
        // The whole point of the window/result split: a manual-range window has
        // no characters yet, but the export result that ran against it always
        // does. See SuggestFileName(CombatLogExportResult)'s own header comment.
        var result = new CombatLogExportResult
        {
            StartUtc = Noon,
            Characters = new[] { "Talon Vex", "Bravo Pilot" },
        };

        Assert.Equal("triffview-Talon-Vex+1-20260818-2010Z.zip", CombatLogExport.SuggestFileName(result));
    }
}
