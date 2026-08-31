using ApTutor.Client.Services;
using ApTutor.Content;
using Xunit;

namespace ApTutor.Tests;

public class AttemptAnalysisTests
{
    private static AttemptRecord Attempt(
        DateTimeOffset when, string questionId, bool correct, string packHash = "hash-1",
        string courseId = "csa", string nodeId = "u1.1", Difficulty difficulty = Difficulty.Medium) =>
        new(when, courseId, nodeId, difficulty, packHash, questionId, correct);

    [Fact]
    public void UnresolvedWrongQuestions_NeverAttempted_IsNotListed() =>
        Assert.Empty(AttemptAnalysis.UnresolvedWrongQuestions(Array.Empty<AttemptRecord>(), "csa", "u1.1"));

    [Fact]
    public void UnresolvedWrongQuestions_OnlyWrongAttempt_IsListed()
    {
        var attempts = new[] { Attempt(DateTimeOffset.UtcNow, "q1", correct: false) };

        var unresolved = AttemptAnalysis.UnresolvedWrongQuestions(attempts, "csa", "u1.1");

        var q = Assert.Single(unresolved);
        Assert.Equal("q1", q.QuestionId);
    }

    [Fact]
    public void UnresolvedWrongQuestions_LatestAttemptCorrect_IsResolved_NotListedEvenThoughAnEarlierAttemptMissed()
    {
        var attempts = new[]
        {
            Attempt(DateTimeOffset.UtcNow.AddMinutes(-5), "q1", correct: false),
            Attempt(DateTimeOffset.UtcNow, "q1", correct: true), // the retry that resolved it
        };

        Assert.Empty(AttemptAnalysis.UnresolvedWrongQuestions(attempts, "csa", "u1.1"));
    }

    [Fact]
    public void UnresolvedWrongQuestions_LatestAttemptWrongAfterAnEarlierCorrect_IsListedAgain()
    {
        var attempts = new[]
        {
            Attempt(DateTimeOffset.UtcNow.AddMinutes(-5), "q1", correct: true),
            Attempt(DateTimeOffset.UtcNow, "q1", correct: false),
        };

        Assert.Single(AttemptAnalysis.UnresolvedWrongQuestions(attempts, "csa", "u1.1"));
    }

    [Fact]
    public void UnresolvedWrongQuestions_SameQuestionIdDifferentPackHash_TrackedIndependently()
    {
        // Two different approved sets can reuse the same item id ("q1") — see AttemptRecord's
        // remarks. Missing one shouldn't be conflated with having missed the other.
        var attempts = new[]
        {
            Attempt(DateTimeOffset.UtcNow, "q1", correct: false, packHash: "hash-a"),
            Attempt(DateTimeOffset.UtcNow, "q1", correct: true, packHash: "hash-b"),
        };

        var unresolved = AttemptAnalysis.UnresolvedWrongQuestions(attempts, "csa", "u1.1");

        var q = Assert.Single(unresolved);
        Assert.Equal("hash-a", q.PackHash);
    }

    [Fact]
    public void UnresolvedWrongQuestions_DifferentNodeOrCourse_IsIgnored()
    {
        var attempts = new[]
        {
            Attempt(DateTimeOffset.UtcNow, "q1", correct: false, nodeId: "u1.2"),
            Attempt(DateTimeOffset.UtcNow, "q1", correct: false, courseId: "worldhistory"),
        };

        Assert.Empty(AttemptAnalysis.UnresolvedWrongQuestions(attempts, "csa", "u1.1"));
    }

    [Fact]
    public void WeeklyAccuracyForCourse_NoAttempts_ReturnsRequestedWeekCountAllEmpty()
    {
        var weeks = AttemptAnalysis.WeeklyAccuracyForCourse(Array.Empty<AttemptRecord>(), "csa", weekCount: 4);

        Assert.Equal(4, weeks.Count);
        Assert.All(weeks, w => Assert.Null(w.PercentCorrect));
    }

    [Fact]
    public void WeeklyAccuracyForCourse_AttemptsInCurrentWeek_LandInTheLastBucket()
    {
        var today = new DateOnly(2026, 8, 28); // a Friday
        var attempts = new[]
        {
            Attempt(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), "q1", correct: true),
            Attempt(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero), "q2", correct: false),
        };

        var weeks = AttemptAnalysis.WeeklyAccuracyForCourse(attempts, "csa", weekCount: 3, today: today);

        var lastWeek = weeks[^1];
        Assert.Equal(2, lastWeek.Total);
        Assert.Equal(1, lastWeek.Correct);
        Assert.Equal(0.5, lastWeek.PercentCorrect);
    }

    [Fact]
    public void WeeklyAccuracyForCourse_OlderAttempt_LandsInAnEarlierBucket_NotTheLatestOne()
    {
        var today = new DateOnly(2026, 8, 28);
        var attempts = new[]
        {
            Attempt(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero), "q1", correct: true), // ~2 weeks earlier
        };

        var weeks = AttemptAnalysis.WeeklyAccuracyForCourse(attempts, "csa", weekCount: 3, today: today);

        Assert.Equal(0, weeks[^1].Total);
        Assert.Contains(weeks.Take(weeks.Count - 1), w => w.Total == 1);
    }

    [Fact]
    public void WeeklyAccuracyForCourse_DifferentCourse_IsExcluded()
    {
        var attempts = new[] { Attempt(DateTimeOffset.UtcNow, "q1", correct: true, courseId: "worldhistory") };

        var weeks = AttemptAnalysis.WeeklyAccuracyForCourse(attempts, "csa", weekCount: 2);

        Assert.All(weeks, w => Assert.Equal(0, w.Total));
    }
}
