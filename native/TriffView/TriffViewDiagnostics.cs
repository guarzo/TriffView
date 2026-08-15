using System.Drawing;
using System.IO;
using System.Text;
using Forms = System.Windows.Forms;

namespace TriffView;

/// <summary>
/// Local-only diagnostics for this fork. Records geometry and the code paths that write it,
/// so intermittent preview problems can be diagnosed from a user's log rather than guessed at.
///
/// Deliberately records raw numbers and no verdicts. An earlier version judged whether a
/// rectangle was "on screen" by comparing it against Screen.AllScreens bounds, and that
/// judgement proved wrong: a preview the user had deliberately placed and could see was
/// reported as off screen, because the two are not reliably in the same coordinate space on a
/// mixed arrangement. Recording the rectangle and the monitor bounds side by side keeps the
/// log honest and leaves the interpretation to whoever reads it.
///
/// Not intended for upstream: the app reports notable events through PostError/PostState to the
/// web UI and has no file logger.
/// </summary>
internal static class TriffViewDiagnostics
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TriffHud",
        "triffview-diagnostics.log");

    public static void Log(string category, string message)
    {
        // Diagnostics must never take the app down, and there is no channel to report a
        // logging failure through that would not itself be a logging failure.
        try
        {
            lock (Gate)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                // Bounded so an unattended session cannot fill the disk.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                }

                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{category}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignored deliberately: see above.
        }
    }

    /// <summary>Current monitor rectangles, as the app sees them.</summary>
    public static string Monitors()
    {
        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0) return "<none>";

        var builder = new StringBuilder();
        for (var index = 0; index < screens.Length; index++)
        {
            if (index > 0) builder.Append(", ");
            var b = screens[index].Bounds;
            builder.Append(
                $"{screens[index].DeviceName}{(screens[index].Primary ? "*" : "")}=" +
                $"[{b.Left},{b.Top} {b.Width}x{b.Height}]");
        }

        return builder.ToString();
    }

    public static string Describe(Rectangle rect) =>
        $"[{rect.Left},{rect.Top} {rect.Width}x{rect.Height}]";

    /// <summary>
    /// Records a preview layout write and the call path that produced it. The stack trace is
    /// the point of the exercise: preview positions have been observed changing to values the
    /// user did not choose, and this identifies which code did it.
    /// </summary>
    public static void RecordLayoutWrite(string source, string key, Rectangle rect)
    {
        Log(
            "layout-write",
            $"[{source}] key='{key}' rect={Describe(rect)} monitors={Monitors()}" +
            Environment.NewLine + Environment.StackTrace);
    }

    /// <summary>
    /// Records a failed attempt to bring an EVE client to the foreground. Windows refuses
    /// SetForegroundWindow from a background process under several conditions, including while
    /// the user holds a key down, which matches reports of previews not responding to clicks
    /// while a push-to-talk key is held.
    /// </summary>
    public static void RecordActivationFailure(string character, nint handle, bool foregroundMatches)
    {
        Log(
            "activation-failed",
            $"character='{character}' handle=0x{handle:X} " +
            $"foregroundIsTarget={foregroundMatches} monitors={Monitors()}");
    }
}
