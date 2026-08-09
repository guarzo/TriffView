using System.IO;

namespace TriffView.TriffSkills;

// Pure, I/O-free rules for a plan name arriving in a triffskills:import-plan web
// message. Split out of TriffSkillsController for the same reason PlanCache is its own
// file (PlanCache.cs): TriffSkillsController.cs pulls in HttpClient, CredentialStore and
// the rest of the SSO/ESI plumbing, none of which this logic needs or should have to
// link against just to be exercised in .scratch-tests.
//
// TriffSkills.tsx runs an advisory copy of these same rules (planNameHint) so the user
// gets a friendly message while typing, but that check is renderer-side and cannot be
// trusted to have run at all - a web message can carry any string regardless of what the
// UI allows. This is the authoritative half of that pair, and it rejects rather than
// sanitizes: an unacceptable name is refused outright rather than silently rewritten
// into something the user did not type.
internal static class PlanNameValidator
{
    // Windows reserved device names: reserved as the whole stem or with any extension
    // attached (CON.txt is still CON), case-insensitive. LPT/COM are 1-9 only - there is
    // no LPT0 or COM0 reservation.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    public const int MaxNameLength = 120;

    public static bool TryValidate(string? name, out string error)
    {
        name ??= "";

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Plan name cannot be empty.";
            return false;
        }
        if (name.Length > MaxNameLength)
        {
            error = $"Plan name is too long (max {MaxNameLength} characters).";
            return false;
        }
        if (name != name.Trim())
        {
            error = "Plan name cannot start or end with whitespace.";
            return false;
        }
        // Windows silently strips a trailing dot from a file name, so "foo." and "foo"
        // collide after the fact - reject rather than let that surprise the user later.
        if (name.EndsWith('.'))
        {
            error = "Plan name cannot end with a period.";
            return false;
        }
        // Path.GetInvalidFileNameChars() already covers '/', '\\' and ':' on Windows, so a
        // path separator or a drive-letter colon is caught here too. ".." is made of
        // otherwise-valid characters, so it needs its own check.
        if (name.IndexOfAny(InvalidNameChars) >= 0 || name.Contains(".."))
        {
            error = "Plan name contains characters that are not allowed in a file name.";
            return false;
        }
        // The segment before the *first* dot is what Windows reserves, regardless of
        // what follows - Path.GetFileNameWithoutExtension strips only the last
        // extension and would miss "CON.txt.bak".
        var stem = name.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem))
        {
            error = $"\"{stem}\" is a reserved Windows device name.";
            return false;
        }

        error = "";
        return true;
    }

    // Belt-and-braces defense in depth: if TryValidate's character-level rules somehow
    // miss a case, this is what actually stops a write from landing outside root. Same
    // pattern as EveSettingsController.IsUnder (EveSettingsController.cs).
    public static bool IsWithin(string fullPath, string root)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.GetFullPath(fullPath);
        return candidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
