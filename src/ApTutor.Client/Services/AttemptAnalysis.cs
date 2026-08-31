using ApTutor.Content;

namespace ApTutor.Client.Services;

/// Pure, zero-I/O logic over an already-loaded attempt log — mirrors the ContentSyncPlanner pattern
/// (a plain function, no S3/disk in it) so both features below are unit-testable without touching
/// the filesystem.
public static class AttemptAnalysis
{
    public sealed record UnresolvedQuestion(string NodeId, Difficulty Difficulty, string PackHash, string QuestionId);

    /// A question is "unresolved-wrong" if the MOST RECENT attempt at it was incorrect — not "ever
    /// missed once." Answering it correctly on retry resolves it (see the plan's retry semantics);
    /// any earlier miss stops counting the moment a later attempt at the exact same question
    /// succeeds. Grouped by (PackHash, QuestionId) rather than QuestionId alone, for the same reason
    /// AttemptRecord carries PackHash — two different approved sets can reuse the same question id.
    public static IReadOnlyList<UnresolvedQuestion> UnresolvedWrongQuestions(
        IReadOnlyList<AttemptRecord> attempts, string courseId, string nodeId) =>
        attempts
            .Where(a => a.CourseId == courseId && a.NodeId == nodeId)
            .GroupBy(a => (a.PackHash, a.QuestionId))
            .Select(g => g.OrderBy(a => a.Timestamp).Last())
            .Where(latest => !latest.Correct)
            .Select(a => new UnresolvedQuestion(a.NodeId, a.Difficulty, a.PackHash, a.QuestionId))
            .ToList();

    public sealed record WeeklyAccuracy(DateOnly WeekStart, int Correct, int Total)
    {
        /// Null (not 0%) for a week with no attempts at all — the chart needs to tell "practiced,
        /// scored badly" apart from "didn't practice this week," which a 0% bar can't distinguish.
        public double? PercentCorrect => Total == 0 ? null : (double)Correct / Total;
    }

    /// The last <paramref name="weekCount"/> Monday-starting weeks up to and including the one
    /// containing <paramref name="today"/> (defaults to now), oldest first — always exactly
    /// <paramref name="weekCount"/> entries, including weeks with zero attempts, so the chart has a
    /// stable, gap-free x-axis to render against.
    public static IReadOnlyList<WeeklyAccuracy> WeeklyAccuracyForCourse(
        IReadOnlyList<AttemptRecord> attempts, string courseId, int weekCount, DateOnly? today = null)
    {
        var end = today ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var currentWeekStart = StartOfWeek(end);

        var byWeek = attempts
            .Where(a => a.CourseId == courseId)
            .GroupBy(a => StartOfWeek(DateOnly.FromDateTime(a.Timestamp.UtcDateTime)))
            .ToDictionary(g => g.Key, g => (Correct: g.Count(a => a.Correct), Total: g.Count()));

        return Enumerable.Range(0, weekCount)
            .Select(i => currentWeekStart.AddDays(-7 * (weekCount - 1 - i)))
            .Select(weekStart => byWeek.TryGetValue(weekStart, out var stats)
                ? new WeeklyAccuracy(weekStart, stats.Correct, stats.Total)
                : new WeeklyAccuracy(weekStart, 0, 0))
            .ToList();
    }

    private static DateOnly StartOfWeek(DateOnly date)
    {
        var daysSinceMonday = ((int)date.DayOfWeek - (int)DayOfWeek.Monday + 7) % 7;
        return date.AddDays(-daysSinceMonday);
    }
}
