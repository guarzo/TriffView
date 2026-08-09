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
}
