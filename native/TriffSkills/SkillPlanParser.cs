using System.Globalization;

namespace TriffView.TriffSkills;

// A single "train skill X to level N" line from a plan file.
internal sealed record PlanRequirement(string SkillName, int Level);

// A parsed plan. Requirements is ordered by first appearance in the
// source file so that downstream analysis output is deterministic.
internal sealed record SkillPlan(string Name, IReadOnlyList<PlanRequirement> Requirements);

internal static class SkillPlanParser
{
    // Case-insensitive for the same reason SkillIdCache.Map is: plan files are
    // hand-written, and "Survey iv" is the same requirement as "Survey IV".
    private static readonly Dictionary<string, int> RomanLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["I"] = 1,
        ["II"] = 2,
        ["III"] = 3,
        ["IV"] = 4,
        ["V"] = 5,
    };

    // Parses "Skill Name <level>" lines. Malformed lines are skipped rather than
    // throwing: plan files are community-authored and one bad line must not cost the
    // whole plan. A skill listed more than once keeps its highest level.
    public static SkillPlan Parse(string name, string contents)
    {
        var order = new List<string>();

        // OrdinalIgnoreCase because SkillIdCache.Map resolves names that way: under an
        // ordinal comparer "Survey 3" and "survey 4" survive as two separate requirements
        // that both resolve to the same typeID, and the matrix then shows one skill twice
        // with conflicting verdicts. Merging them here keeps the highest level, which is
        // the same rule already applied to an exact repeat.
        var levels = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in (contents ?? string.Empty).Split('\n'))
        {
            // Trim also strips the '\r' of a CRLF file.
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lastSpace = line.LastIndexOf(' ');
            if (lastSpace < 0)
            {
                continue;
            }

            var skillName = line[..lastSpace];
            if (!TryParseLevel(line[(lastSpace + 1)..], out var level))
            {
                continue;
            }

            if (levels.TryGetValue(skillName, out var existing))
            {
                if (level > existing)
                {
                    levels[skillName] = level;
                }
            }
            else
            {
                levels[skillName] = level;
                order.Add(skillName);
            }
        }

        var requirements = new List<PlanRequirement>(order.Count);
        foreach (var skillName in order)
        {
            requirements.Add(new PlanRequirement(skillName, levels[skillName]));
        }

        return new SkillPlan(name, requirements);
    }

    private static bool TryParseLevel(string token, out int level)
    {
        if (RomanLevels.TryGetValue(token, out level))
        {
            return true;
        }

        // NumberStyles.None rejects signs and whitespace; a level is a bare positive integer.
        // EVE skills only go to V, so anything outside 1-5 is a malformed line rather than a
        // requirement - "Survey 0" and "Survey 50" would otherwise become requirements the
        // evaluator can never satisfy, or that every character trivially satisfies.
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out level)
            && level >= 1 && level <= 5)
        {
            return true;
        }

        level = 0;
        return false;
    }
}
