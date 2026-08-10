using System.IO;
using System.Text;

namespace TriffView.TriffSkills;

// Owns the on-disk plans directory: seeds it on first run and reads whatever is there.
// The user populates it from then on - plan files are dropped into
// %APPDATA%\TriffHud\TriffSkills\plans by hand or written by the import flow.
// Path-injectable and I/O-only so it can be exercised in tests against a temp directory.
//
// Named Store rather than Cache: nothing here is derived data that can be re-fetched.
// The directory is the authoritative copy of the user's plans, and losing it loses them.
internal static class PlanStore
{
    public const string StarterPlanName = "Core Ship Skills";

    // Written on first run so the tool opens with a populated matrix instead of an empty
    // grid and no obvious next step - the plans folder is otherwise invisible until the
    // user goes looking for it.
    //
    // Original content, deliberately: skill plans circulate widely in the EVE community
    // and shipping someone else's would put text of unknown provenance into a GPL-3.0
    // repository under the maintainer's name. This is a plain list of core support skills
    // that nearly every character trains anyway, written for this purpose.
    //
    // The two leading lines are comments only by convention: SkillPlanParser skips any
    // line whose last whitespace-delimited token is not a level, which is what drops them.
    // Any line added here must end in a non-level token - a comment ending in a bare digit
    // would silently become a requirement.
    private const string StarterPlanContents = """
        # Core support skills - the ones nearly every ship benefits from.
        # Format: a skill name, then a level as 1-5 or I-V. Delete this file if you like.
        CPU Management IV
        Power Grid Management IV
        Capacitor Management III
        Capacitor Systems Operation III
        Mechanics IV
        Hull Upgrades III
        Shield Operation III
        Shield Management III
        Navigation IV
        Afterburner III
        Evasive Maneuvering III
        Warp Drive Operation II
        Long Range Targeting III
        Target Management III
        Weapon Upgrades III
        Drones III
        """;

    // Seeds the starter plan, keyed on the plans directory not existing at all. That is
    // the only reliable first-run signal available: every other path that touches this
    // folder (import, "open plans folder") creates it, so its absence means the user has
    // reached none of them. Deliberately not keyed on "the folder is empty" - a user who
    // deletes the starter plan, or every plan, should not have one reappear.
    //
    // Best-effort by contract. A failure here costs a convenience file, so it must not
    // propagate out into the constructor that calls it and take the whole tool down.
    public static void EnsureSeeded(string plansDir)
    {
        try
        {
            if (Directory.Exists(plansDir)) return;

            Directory.CreateDirectory(plansDir);
            File.WriteAllText(
                Path.Combine(plansDir, StarterPlanName + ".txt"),
                // CRLF because the user opens this in whatever Windows hands them, and
                // Notepad on older Windows renders a lone LF as one unbroken line.
                StarterPlanContents.ReplaceLineEndings("\r\n"),
                new UTF8Encoding(false)
            );
        }
        catch (Exception)
        {
            // Permission-denied %APPDATA%, a read-only profile, antivirus holding the
            // directory: the tool works fine with no plans at all, so there is nothing
            // worth reporting and nothing to retry.
        }
    }

    // Parses every .txt into a plan named after its file stem. A file that cannot be read
    // or parsed is skipped rather than failing the whole load - one bad file must not cost
    // the user every other plan.
    //
    // Deliberately broad: File.ReadAllText can throw UnauthorizedAccessException (a
    // permission-denied ACL, a read-only/system attribute, or the "directory where a
    // file was expected" case) as readily as IOException (a lock), and neither derives
    // from the other. SkillPlanParser.Parse tolerates malformed lines internally but is
    // not guaranteed against every pathological input. This is the same per-file
    // isolation contract RefreshCharactersAsync's per-character catch enforces
    // (TriffSkillsController.cs) - one bad item must degrade by exactly one item, so the
    // catch here has to cover whatever that one item can throw, not just the case that
    // was easiest to name.
    public static IReadOnlyList<SkillPlan> LoadAll(string plansDir)
    {
        if (!Directory.Exists(plansDir)) return Array.Empty<SkillPlan>();

        var plans = new List<SkillPlan>();
        foreach (var path in Directory.GetFiles(plansDir, "*.txt").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                plans.Add(SkillPlanParser.Parse(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)));
            }
            catch (Exception)
            {
                // Unreadable (locked, permission-denied, missing) or unparseable: skip
                // this one file, keep the rest.
            }
        }

        return plans;
    }
}
