using System;
using System.Windows.Media;

namespace TriffView.Audio;

/// <summary>
/// Plays an alert's configured sound natively, replacing the web UI's own <c>&lt;audio&gt;</c>
/// playback (removed from App.jsx alongside this). A single <see cref="MediaPlayer"/> is reused
/// for every alert rather than one per event: <see cref="MediaPlayer"/> has no queue of its own,
/// so two alerts landing close together simply cut one another off. That is accepted rather than
/// built around - per-event cooldowns already keep alerts from landing back-to-back in the
/// common case, and a pool of players would be real complexity for a rare, harmless overlap.
/// </summary>
internal sealed class AlertSoundPlayer
{
    private readonly MediaPlayer _player = new();

    // The soundId currently (or most recently) being opened, purely so a MediaFailed callback -
    // which arrives with no context of its own - can say which sound it was complaining about.
    private string? _currentSoundId;

    public AlertSoundPlayer()
    {
        // MediaPlayer.Open/Play both report load/playback failures asynchronously through this
        // event rather than through an exception or return value from Open/Play themselves. Without
        // subscribing, the try/catch below catches essentially nothing and a bad file, unsupported
        // codec, or missing audio device fails completely silently: no exception, no log line, no
        // sound - indistinguishable from everything having worked.
        _player.MediaFailed += OnMediaFailed;
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        TriffViewDiagnostics.Log("audio-alert-sound", $"failed to play '{_currentSoundId}': {e.ErrorException?.Message}");
    }

    /// <summary>
    /// Fire-and-forget: <see cref="Play"/> is called from <c>ProcessPendingAlerts</c>, which runs
    /// on the WPF dispatcher, so nothing here may block it. <see cref="MediaPlayer.Open"/> and
    /// <see cref="MediaPlayer.Play"/> both return immediately and do their work asynchronously.
    /// </summary>
    public void Play(string soundId, double volume)
    {
        if (string.IsNullOrWhiteSpace(soundId) || string.Equals(soundId, "none", StringComparison.OrdinalIgnoreCase))
            return;

        var uri = SoundUri(soundId);
        if (uri is null) return;

        _currentSoundId = soundId;
        try
        {
            _player.Volume = Math.Clamp(volume, 0, 1);
            _player.Open(uri);
            _player.Play();
        }
        catch (Exception ex)
        {
            // Never let a bad codec load or a missing audio device take the dispatcher down with
            // it - the flash and tray branches beside this one in ProcessPendingAlerts still ran.
            // Most real failures surface via MediaFailed instead of here (see the constructor).
            TriffViewDiagnostics.Log("audio-alert-sound", $"failed to play '{soundId}': {ex.Message}");
        }
    }

    private static Uri? SoundUri(string soundId) => soundId.ToLowerInvariant() switch
    {
        "alarm" => new Uri("pack://application:,,,/Assets/sounds/alarm.wav", UriKind.Absolute),
        "woop" => new Uri("pack://application:,,,/Assets/sounds/woop.wav", UriKind.Absolute),
        "siren" => new Uri("pack://application:,,,/Assets/sounds/siren.wav", UriKind.Absolute),
        "ding" => new Uri("pack://application:,,,/Assets/sounds/ding.wav", UriKind.Absolute),
        _ => null,
    };
}
