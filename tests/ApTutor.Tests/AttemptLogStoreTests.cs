using ApTutor.Client.Services;
using ApTutor.Content;
using Xunit;

namespace ApTutor.Tests;

public class AttemptLogStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aptutor-attemptlog-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static AttemptRecord Sample(string questionId = "u1.1-q1", bool correct = true) => new(
        DateTimeOffset.UtcNow, "csa", "u1.1", Difficulty.Medium, "hash-abc", questionId, correct);

    [Fact]
    public void LoadAll_NoFileYet_ReturnsEmpty() =>
        Assert.Empty(AttemptLogStore.LoadAll(_dir));

    [Fact]
    public void Append_ThenLoadAll_RoundTripsExactly()
    {
        var record = Sample();

        AttemptLogStore.Append(_dir, record);
        var loaded = AttemptLogStore.LoadAll(_dir);

        Assert.Equal(new[] { record }, loaded);
    }

    [Fact]
    public void Append_MultipleTimes_AccumulatesRatherThanOverwriting()
    {
        AttemptLogStore.Append(_dir, Sample("u1.1-q1", correct: false));
        AttemptLogStore.Append(_dir, Sample("u1.1-q1", correct: true));
        AttemptLogStore.Append(_dir, Sample("u1.1-q2", correct: true));

        Assert.Equal(3, AttemptLogStore.LoadAll(_dir).Count);
    }

    [Fact]
    public void LoadAll_TrailingCorruptLine_SkipsItButKeepsTheRest()
    {
        AttemptLogStore.Append(_dir, Sample("u1.1-q1"));
        File.AppendAllText(Path.Combine(_dir, "attempts.jsonl"), "{ not valid json\n");
        AttemptLogStore.Append(_dir, Sample("u1.1-q2"));

        var loaded = AttemptLogStore.LoadAll(_dir);

        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, a => a.QuestionId == "u1.1-q1");
        Assert.Contains(loaded, a => a.QuestionId == "u1.1-q2");
    }
}
