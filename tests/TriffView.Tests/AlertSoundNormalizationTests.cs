using TriffView.Alerts;
using Xunit;

public class AlertSoundNormalizationTests
{
    // Every id offered by ALERT_SOUND_OPTIONS in app/src/tools/TriffViewSettings.jsx.
    public static readonly TheoryData<string> ShippedSoundIds =
        new() { "none", "chime", "bell", "pulse", "ding" };

    // Retired for being harsh: all three peaked in the 2-5 kHz band and ran ~7 dB hotter than the
    // rest of the set.
    public static readonly TheoryData<string> RetiredSoundIds =
        new() { "alarm", "siren", "woop" };

    private static string NormalizedSound(string sound)
    {
        var config = new TriffAlertEventConfig { Sound = sound };
        config.Normalize(new TriffAlertEventConfig());
        return config.Sound;
    }

    // The failure this guards against is silent: NormalizeSound rewrites an id it does not
    // recognise to "none" rather than rejecting it, so a sound added to the settings dropdown and
    // to AlertSoundPlayer but missed here would appear as a working option and simply never play.
    [Theory]
    [MemberData(nameof(ShippedSoundIds))]
    public void ShippedSoundIdSurvivesNormalization(string soundId)
    {
        Assert.Equal(soundId, NormalizedSound(soundId));
    }

    [Theory]
    [InlineData("  Chime  ", "chime")]
    [InlineData("BELL", "bell")]
    public void SoundIdIsTrimmedAndLowercased(string stored, string expected)
    {
        Assert.Equal(expected, NormalizedSound(stored));
    }

    // The point of the remap. These users deliberately chose an audible alert; letting a retired id
    // fall through to "none" would silently stop alerting them, which is worse than the annoyance
    // the retirement exists to fix.
    [Theory]
    [MemberData(nameof(RetiredSoundIds))]
    public void RetiredSoundIdMigratesToPulseRatherThanSilence(string retiredId)
    {
        Assert.Equal("pulse", NormalizedSound(retiredId));
    }

    // Settings are normalized on both load and save, so a migrated value is re-normalized on every
    // subsequent pass. The remap has to be stable or a saved setting could drift.
    [Theory]
    [MemberData(nameof(RetiredSoundIds))]
    public void MigrationIsIdempotent(string retiredId)
    {
        Assert.Equal("pulse", NormalizedSound(NormalizedSound(retiredId)));
    }

    // The retired ids are matched case-insensitively like every other id, so a config written with
    // different casing still migrates rather than falling through to "none".
    [Theory]
    [InlineData("Alarm")]
    [InlineData("  SIREN ")]
    public void RetiredSoundIdMigratesRegardlessOfCasingOrWhitespace(string stored)
    {
        Assert.Equal("pulse", NormalizedSound(stored));
    }

    [Theory]
    [InlineData("")]
    [InlineData("klaxon")]
    public void UnknownSoundIdFallsBackToNone(string soundId)
    {
        Assert.Equal("none", NormalizedSound(soundId));
    }

    // Settings are loaded whole from disk, so a null here is a real deserialization outcome for a
    // config written before the field existed, not a defensive hypothetical.
    [Fact]
    public void NullSoundFallsBackToNone()
    {
        Assert.Equal("none", NormalizedSound(null!));
    }
}
