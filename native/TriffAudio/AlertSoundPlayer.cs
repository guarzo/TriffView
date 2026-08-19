using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows;
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

    // Resolved file path per sound id, or null for a sound whose extraction failed (cached so a
    // broken resource is not re-attempted, and re-logged, on every single alert). Populated
    // lazily on first play of each sound; every later play of that sound is a dictionary lookup.
    private readonly ConcurrentDictionary<string, string?> _soundFiles = new(StringComparer.OrdinalIgnoreCase);

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
    /// The only work that is not immediate is extracting the sound to disk, which happens once
    /// per sound id per process (see <see cref="ResolveSoundFile"/>) and is a ~100 KB write.
    /// </summary>
    public void Play(string soundId, double volume)
    {
        if (string.IsNullOrWhiteSpace(soundId) || string.Equals(soundId, "none", StringComparison.OrdinalIgnoreCase))
            return;

        var path = ResolveSoundFile(soundId);
        if (path is null) return;

        _currentSoundId = soundId;
        try
        {
            _player.Volume = Math.Clamp(volume, 0, 1);
            _player.Open(new Uri(path, UriKind.Absolute));
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

    /// <summary>
    /// The alert sounds ship as WPF <c>&lt;Resource&gt;</c> entries, which is what the
    /// single-file release build needs - but <see cref="MediaPlayer"/> cannot open them in place.
    /// Verified against the real built assembly: opening
    /// <c>pack://application:,,,/TriffView;component/Assets/sounds/alarm.wav</c> raises
    /// <c>MediaFailed</c> with <c>NotSupportedException: "Only site-of-origin pack URIs are
    /// supported for media."</c>, while the same bytes written to a temp file and opened as a
    /// file URI open fine. Only <c>pack://siteoforigin:</c> works for media, and
    /// <c>&lt;Resource&gt;</c> does not produce that.
    ///
    /// So the resource is read out with <c>Application.GetResourceStream</c> (which does find it)
    /// and written to a file once, then cached. Any failure degrades to no sound and a
    /// diagnostics line - never an exception into the dispatcher.
    /// </summary>
    private string? ResolveSoundFile(string soundId) => _soundFiles.GetOrAdd(soundId, ExtractSound);

    private static string? ExtractSound(string soundId)
    {
        var resourceUri = SoundResourceUri(soundId);
        if (resourceUri is null) return null;

        var path = Path.Combine(Path.GetTempPath(), "TriffView", "sounds", soundId.ToLowerInvariant() + ".wav");
        try
        {
            var resource = System.Windows.Application.GetResourceStream(resourceUri);
            if (resource is null)
            {
                TriffViewDiagnostics.Log("audio-alert-sound", $"sound resource '{soundId}' not found in the assembly");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (resource.Stream)
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                resource.Stream.CopyTo(file);

            return path;
        }
        catch (IOException ex) when (File.Exists(path))
        {
            // A second TriffView instance already extracted this sound and has the file open in
            // its own MediaPlayer. The bytes are the same; reuse what is already there rather
            // than losing the sound over a write that was never needed.
            TriffViewDiagnostics.Log("audio-alert-sound", $"reusing existing '{soundId}' sound file ({ex.Message})");
            return path;
        }
        catch (Exception ex)
        {
            TriffViewDiagnostics.Log("audio-alert-sound", $"failed to extract sound '{soundId}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Assembly-qualified (<c>/TriffView;component/</c>) rather than the shorter
    /// <c>pack://application:,,,/Assets/...</c>: the short form resolves against
    /// <c>Application.ResourceAssembly</c>, which is only TriffView's own assembly by convention.
    /// The qualified form names the assembly outright and is the exact form verified to work
    /// against the built DLL (see <see cref="ResolveSoundFile"/>).
    /// </summary>
    private static Uri? SoundResourceUri(string soundId) => soundId.ToLowerInvariant() switch
    {
        "alarm" => new Uri("pack://application:,,,/TriffView;component/Assets/sounds/alarm.wav", UriKind.Absolute),
        "woop" => new Uri("pack://application:,,,/TriffView;component/Assets/sounds/woop.wav", UriKind.Absolute),
        "siren" => new Uri("pack://application:,,,/TriffView;component/Assets/sounds/siren.wav", UriKind.Absolute),
        "ding" => new Uri("pack://application:,,,/TriffView;component/Assets/sounds/ding.wav", UriKind.Absolute),
        _ => null,
    };
}
