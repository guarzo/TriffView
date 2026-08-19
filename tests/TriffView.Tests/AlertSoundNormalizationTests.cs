using TriffView.Alerts;
using Xunit;

public class AlertSoundNormalizationTests
{
    // Every id offered by ALERT_SOUND_OPTIONS in app/src/tools/TriffViewSettings.jsx. The three
    // soft tones were added because the original four all peak in the 2-5 kHz band, where a
    // repeated alert becomes painful fastest; they are additive, so a user who chose one of the
    // originals keeps it.
    public static readonly TheoryData<string> ShippedSoundIds =
        new() { "none", "chime", "bell", "pulse", "alarm", "woop", "siren", "ding" };

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
