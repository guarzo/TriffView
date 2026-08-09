using System.IO;

namespace TriffView.TriffSkills;

// Owns the on-disk plans directory. The user populates it: plan files are dropped into
// %APPDATA%\TriffHud\TriffSkills\plans by hand, and this reads whatever is there.
// Path-injectable and I/O-only so it can be exercised in tests against a temp directory.
internal static class PlanCache
{
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
