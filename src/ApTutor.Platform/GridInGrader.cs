namespace ApTutor.Platform;

/// Grades a typed grid-in answer (see the PSAT-content-generation plan) against a PracticeItem's
/// CorrectAnswer. Accepts an exact text match OR a numerically-equivalent one (e.g. "0.75" for a
/// correct answer written as "3/4") — the same flexibility the real PSAT's grid-in grading allows,
/// rather than forcing the one exact string a student happens to type. Lives in Platform (like
/// GraphGeometry) rather than the Shell specifically so MockExamSession.Submit can grade grid-ins
/// too, not just the Shell's own per-node practice UI.
public static class GridInGrader
{
    public static bool IsCorrect(string? given, string? correctAnswer)
    {
        if (string.IsNullOrWhiteSpace(given) || string.IsNullOrWhiteSpace(correctAnswer)) return false;

        var g = given.Trim();
        var c = correctAnswer.Trim();
        if (string.Equals(g, c, StringComparison.OrdinalIgnoreCase)) return true;

        return TryParseNumeric(g, out var gv) && TryParseNumeric(c, out var cv) && Math.Abs(gv - cv) < 1e-9;
    }

    /// Plain numbers parse directly; "a/b" is evaluated as a fraction first — the one other shape
    /// PromptTemplates.GridInGuidance explicitly tells the model a correctAnswer may take.
    private static bool TryParseNumeric(string text, out double value)
    {
        var slash = text.IndexOf('/');
        if (slash > 0 && slash < text.Length - 1
            && double.TryParse(text[..slash], out var numerator)
            && double.TryParse(text[(slash + 1)..], out var denominator)
            && denominator != 0)
        {
            value = numerator / denominator;
            return true;
        }

        return double.TryParse(text, out value);
    }
}
