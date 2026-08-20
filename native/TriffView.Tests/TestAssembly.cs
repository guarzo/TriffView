using System.Runtime.CompilerServices;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace TriffView.Tests;

/// <summary>
/// Keeps the test suite out of the real diagnostics log.
///
/// Several tests construct a real <c>TriffViewController</c> and call <c>Start()</c>, which
/// writes startup and environment entries through <see cref="TriffViewDiagnostics"/>. Without
/// this redirect those land in %APPDATA%\TriffHud\triffview-diagnostics.log alongside genuine
/// sessions, where they are hard to tell apart and actively misleading: a run of this suite
/// produced six "startup" entries under one process id and reported the DPI-unaware test host's
/// coordinate space, and both were briefly diagnosed as real application bugs.
///
/// Overriding APPDATA does not work for this - Environment.GetFolderPath asks the shell for the
/// known folder rather than reading the variable - so the log path has its own override.
/// </summary>
internal static class DiagnosticsIsolation
{
    [ModuleInitializer]
    internal static void RedirectDiagnosticsLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "TriffViewTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable(TriffViewDiagnostics.LogDirectoryOverrideVariable, directory);
    }
}
