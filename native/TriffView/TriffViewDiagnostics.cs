using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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

    /// <summary>
    /// Environment variable that redirects the log somewhere else.
    ///
    /// This exists because the test suite constructs a real <c>TriffViewController</c> and calls
    /// <c>Start()</c>, which logs - so every `dotnet test` run wrote startup and environment
    /// entries into the developer's or user's live diagnostics file, interleaved with real
    /// sessions and indistinguishable from them at a glance. That is not hypothetical: six test
    /// initializations were read as a real repeated-initialization bug, and the DPI awareness of
    /// the DPI-unaware test host was read as the app reporting inconsistent awareness. Both
    /// diagnoses were wrong and both came from this pollution.
    ///
    /// Overriding APPDATA does not help - Environment.GetFolderPath resolves the known folder
    /// through the shell rather than the environment variable, so it returns the real roaming
    /// path regardless.
    /// </summary>
    internal const string LogDirectoryOverrideVariable = "TRIFFVIEW_DIAGNOSTICS_DIR";

    public static string LogPath
    {
        get
        {
            var overrideDirectory = Environment.GetEnvironmentVariable(LogDirectoryOverrideVariable);
            var directory = string.IsNullOrWhiteSpace(overrideDirectory)
                ? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TriffHud")
                : overrideDirectory;

            return Path.Combine(directory, "triffview-diagnostics.log");
        }
    }

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

                // Bounded so an unattended session cannot fill the disk, but kept for two
                // generations rather than one. A single rotation discards everything older the
                // moment it fires, and the report that prompts someone to look at this file
                // usually arrives after the session that caused it - which is precisely when the
                // interesting entries have just been thrown away.
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    if (File.Exists(LogPath + ".1")) File.Move(LogPath + ".1", LogPath + ".2", overwrite: true);
                    File.Move(LogPath, LogPath + ".1", overwrite: true);
                }

                File.AppendAllText(
                    LogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] [{category}] {message}{Environment.NewLine}");
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceIndex, ref DisplayDevice info, uint flags);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    private static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary>
    /// Records the machine this session is running on, once at startup.
    ///
    /// The point is comparison, not curiosity: every performance measurement this project has
    /// was taken on one 24-core workstation, so a report of "it stutters" from elsewhere cannot
    /// currently be placed against anything. Core count, RAM, GPU and desktop area are the
    /// dimensions the known costs actually scale with.
    ///
    /// Deliberately no verdict - no "this machine is underpowered" line. Same rule the rest of
    /// this file follows: record the numbers and leave the reading to whoever reads them.
    ///
    /// Uses EnumDisplayDevices and GlobalMemoryStatusEx rather than WMI: WMI would mean a new
    /// package reference, and Win32_VideoController takes seconds to answer, which is a poor
    /// trade for one line written once.
    /// </summary>
    public static void RecordEnvironment(string appVersion, string settingsSummary)
    {
        try
        {
            var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            var totalRam = GlobalMemoryStatusEx(ref memory)
                ? $"{memory.TotalPhys / 1024.0 / 1024 / 1024:F1}GB"
                : "unknown";

            var adapters = new List<string>();
            for (uint index = 0; index < 8; index++)
            {
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, index, ref device, 0)) break;

                // Bit 0 is DISPLAY_DEVICE_ACTIVE. Inactive adapters are noise here.
                if ((device.StateFlags & 0x1) == 0) continue;
                var name = device.DeviceString?.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !adapters.Contains(name)) adapters.Add(name);
            }

            // SM_CXVIRTUALSCREEN / SM_CYVIRTUALSCREEN, in whatever coordinate space this process
            // is handed - which for a system-DPI-aware process is the primary monitor's scaled
            // space, not physical pixels. Recorded as-is, without conversion: the app's own
            // geometry lives in that same space, and "correcting" it here would produce a number
            // matching nothing else in this log.
            var virtualWidth = GetSystemMetrics(78);
            var virtualHeight = GetSystemMetrics(79);

            Log(
                "environment",
                $"version={appVersion} os={Environment.OSVersion.Version} cores={Environment.ProcessorCount} " +
                $"ram={totalRam} gpu={(adapters.Count > 0 ? string.Join(" + ", adapters) : "unknown")} " +
                $"virtualDesktop={virtualWidth}x{virtualHeight} ({(long)virtualWidth * virtualHeight}px) " +
                $"dpiAwareness={DescribeDpiAwareness()} monitors={Monitors()} {settingsSummary}");
        }
        catch (Exception ex)
        {
            Log("environment", $"failed to record environment: {ex.Message}");
        }
    }

    /// <summary>
    /// The process's DPI awareness, which decides what coordinate space every other number in
    /// this log is expressed in. Without it a reader cannot tell whether a rectangle is in
    /// physical pixels or the primary monitor's scaled space - a distinction that has already
    /// caused one measurement here to be misread as a DPI bug.
    /// </summary>
    private static string DescribeDpiAwareness()
    {
        try
        {
            var context = GetThreadDpiAwarenessContext();
            if (AreDpiAwarenessContextsEqual(context, new nint(-4))) return "per-monitor-v2";
            if (AreDpiAwarenessContextsEqual(context, new nint(-3))) return "per-monitor";
            if (AreDpiAwarenessContextsEqual(context, new nint(-2))) return "system";
            if (AreDpiAwarenessContextsEqual(context, new nint(-1))) return "unaware";
            return $"0x{context:X}";
        }
        catch
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Records a preview layout write and the call path that produced it. The stack trace is
    /// the point of the exercise: preview positions have been observed changing to values the
    /// user did not choose, and this identifies which code did it.
    /// </summary>
    /// <summary>
    /// The application frames of the current call stack, without the framework plumbing.
    ///
    /// A raw <see cref="Environment.StackTrace"/> from a UI event handler is mostly WinForms
    /// message dispatch and the WPF dispatcher loop - the same dozen frames every time, telling
    /// nobody anything. Measured on a real log: stack traces were 488KB of a 1.78MB file, 27% of
    /// the whole thing, in a file capped at 2MB that discards the previous generation when it
    /// rotates. That is diagnostic history being evicted by boilerplate.
    ///
    /// So the trace stops at the first frame outside this app's own code. The frames that
    /// identify what moved a preview - the entire reason these traces are recorded - are all
    /// above that boundary. A depth cap bounds anything unexpected.
    /// </summary>
    private static string ApplicationStack()
    {
        const int maxFrames = 12;

        var kept = new List<string>();
        foreach (var raw in Environment.StackTrace.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            var frame = raw.Trim();

            // The diagnostics plumbing itself is never the answer to "what wrote this".
            if (frame.Contains(nameof(TriffViewDiagnostics), StringComparison.Ordinal)) continue;
            if (frame.Contains("System.Environment", StringComparison.Ordinal)) continue;

            // First frame outside our own code ends the useful part of the trace.
            if (!frame.Contains("TriffView", StringComparison.Ordinal)) break;

            kept.Add("   " + frame);
            if (kept.Count >= maxFrames) break;
        }

        return kept.Count == 0
            ? "   <no application frames>"
            : string.Join(Environment.NewLine, kept);
    }

    public static void RecordLayoutWrite(string source, string key, Rectangle rect)
    {
        Log(
            "layout-write",
            // DPI awareness rides along because it decides what coordinate space `rect` is
            // expressed in, and this app has been observed reporting different awareness on
            // different runs of the same version. A rectangle recorded without it cannot safely
            // be compared against one from another session - which is exactly the comparison
            // anyone reading this entry is trying to make.
            $"[{source}] key='{key}' rect={Describe(rect)} dpiAwareness={DescribeDpiAwareness()} " +
            $"monitors={Monitors()}" +
            Environment.NewLine + ApplicationStack());
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
