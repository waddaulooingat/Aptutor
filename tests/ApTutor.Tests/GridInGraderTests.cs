using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// GridInGrader (see the PSAT-content-generation plan) — grading a typed grid-in answer against a
// PracticeItem's CorrectAnswer, accepting exact text or a numerically-equivalent form.
public class GridInGraderTests
{
    [Theory]
    [InlineData("3", "3", true)]
    [InlineData(" 3 ", "3", true)] // whitespace trimmed
    [InlineData("ABC", "abc", true)] // case-insensitive for non-numeric answers
    [InlineData("4", "3", false)]
    public void IsCorrect_ExactTextMatch(string given, string correct, bool expected) =>
        Assert.Equal(expected, GridInGrader.IsCorrect(given, correct));

    [Theory]
    [InlineData("0.75", "3/4", true)] // decimal matches an equivalent fraction
    [InlineData("3/4", "0.75", true)] // and the reverse
    [InlineData("6/8", "3/4", true)] // an unsimplified but equal fraction
    [InlineData("0.5", "3/4", false)]
    public void IsCorrect_NumericEquivalence(string given, string correct, bool expected) =>
        Assert.Equal(expected, GridInGrader.IsCorrect(given, correct));

    [Theory]
    [InlineData(null, "3")]
    [InlineData("", "3")]
    [InlineData("   ", "3")]
    [InlineData("3", null)]
    [InlineData("3", "")]
    public void IsCorrect_NullOrBlankInput_IsNeverCorrect(string? given, string? correct) =>
        Assert.False(GridInGrader.IsCorrect(given, correct));

    [Fact]
    public void IsCorrect_MalformedFraction_FallsBackToTextComparison()
    {
        // "3/" has no denominator to parse — TryParseNumeric must not throw, just fail to parse
        // numerically and fall back to (failing) exact text comparison.
        Assert.False(GridInGrader.IsCorrect("3/", "3/4"));
    }
}
