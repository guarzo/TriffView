namespace TriffView.TriffSkills;

internal sealed record PlanSummary(string Name, int RequirementCount);

internal sealed record MatrixCell(
    long CharacterId,
    string PlanName,
    PlanReadiness Readiness,
    DateTimeOffset? EstimatedFinishUtc,
    IReadOnlyList<MissingRequirement> MissingSkills,
    IReadOnlyList<string> UnknownSkills);

internal sealed record SkillMatrix(IReadOnlyList<PlanSummary> Plans, IReadOnlyList<MatrixCell> Cells);

// The wire shape TriffSkillsController.PostState splices into its posted state object,
// under the "plans" and "matrix" keys respectively. Pulled out to a WPF-free type so the
// projection - in particular readiness = cell.Readiness.ToString(), the encoding a plain
// enum-equality test on SkillMatrix cannot catch a regression in - is itself serializable
// and assertable from the scratch harness. Property names are PascalCase here because
// PostState's JsonOptions (TriffSkillsController.cs) applies JsonNamingPolicy.CamelCase at
// serialize time, same as every other type posted to the webview.
internal sealed record MatrixWire(IReadOnlyList<object> Plans, IReadOnlyList<object> Matrix);

internal static class TriffSkillsMatrix
{
    // Scores every character against every plan. There is no path here that omits a
    // pair: a character whose last fetch failed is scored off its last-good skills and
    // queue, and a plan naming an unresolvable skill is scored Missing rather than
    // dropped. The UI renders the full matrix and shows failure as cell or row state,
    // so an omitted pair would read as a hole rather than as a problem.
    public static SkillMatrix Build(
        IReadOnlyList<TriffSkillsCharacter> characters,
        IReadOnlyList<SkillPlan> plans,
        IReadOnlyDictionary<string, int> skillIds)
    {
        var summaries = new List<PlanSummary>(plans.Count);
        foreach (var plan in plans)
        {
            summaries.Add(new PlanSummary(plan.Name, plan.Requirements.Count));
        }

        var cells = new List<MatrixCell>(characters.Count * plans.Count);
        foreach (var character in characters)
        {
            // Last-good data, whatever its age. ApplyFetchFailure leaves both in place
            // precisely so this stays scoreable; FetchedUtc is what labels it stale.
            var trained = character.TrainedLevels ?? new Dictionary<int, int>();
            var queue = character.Queue ?? new List<QueueEntry>();

            foreach (var plan in plans)
            {
                var analysis = SkillPlanEvaluator.Evaluate(plan, skillIds, trained, queue);
                cells.Add(new MatrixCell(
                    character.CharacterId,
                    plan.Name,
                    analysis.Readiness,
                    analysis.EstimatedFinishUtc,
                    analysis.MissingSkills,
                    analysis.UnknownSkills));
            }
        }

        return new SkillMatrix(summaries, cells);
    }

    // Byte-identical extraction of the two inline .Select(...) projections PostState used
    // to build directly inside its anonymous state object (commit 876c33b). Moved here,
    // WPF-free, purely so the wire encoding is reachable from the scratch harness -
    // TriffSkillsMatrixWireTests.cs serializes this and asserts on the JSON text, which is
    // what actually catches a reverted `readiness = cell.Readiness.ToString()`; asserting
    // PlanReadiness equality on SkillMatrix, as the Build tests do, cannot. PostState now
    // splices Plans and Matrix in verbatim where the two blocks used to live, changing no
    // field name, no nesting, and no value encoding.
    public static MatrixWire ToWire(SkillMatrix matrix)
    {
        var plans = matrix.Plans.Select(plan => (object)new
        {
            plan.Name,
            plan.RequirementCount,
        }).ToArray();

        var cells = matrix.Cells.Select(cell => (object)new
        {
            cell.CharacterId,
            cell.PlanName,
            // Explicit string, not the enum. System.Text.Json serializes an enum
            // as its integer value by default, and the UI keys READINESS_META
            // off "Ready" / "Training" / "Missing" - an integer would silently
            // render every cell as Missing via its fallback.
            readiness = cell.Readiness.ToString(),
            cell.EstimatedFinishUtc,
            missingSkills = cell.MissingSkills.Select(skill => new
            {
                skill.SkillName,
                skill.Level,
            }).ToArray(),
            cell.UnknownSkills,
        }).ToArray();

        return new MatrixWire(plans, cells);
    }
}
