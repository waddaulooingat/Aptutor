using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApTutor.ContentAdmin.Pages;

/// Polling page for learn-content generation (see the learn-quiz-mode-switch plan's Part B) —
/// mirrors Generating.cshtml.cs exactly, minus the difficulty axis this content type doesn't have.
/// difficulty here is carried through purely so the SME lands back on the same difficulty tab of
/// Review they started from — it plays no role in the learn-content generation itself.
public sealed class GeneratingLearnModel : PageModel
{
    private readonly LearnContentGenerationService _generation;

    public GeneratingLearnModel(LearnContentGenerationService generation) => _generation = generation;

    public string CourseId { get; private set; } = "";
    public string NodeId { get; private set; } = "";
    public Difficulty Difficulty { get; private set; }
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public IActionResult OnGet(string course, string node, Difficulty difficulty = Difficulty.Medium)
    {
        CourseId = course;
        NodeId = node;
        Difficulty = difficulty;

        var job = _generation.GetJob(course, node);
        if (job is null)
            return RedirectToPage("/Review", new { course, node, difficulty });

        if (job.Status == GenerationStatus.Running)
        {
            IsRunning = true;
            return Page();
        }

        if (job.Status == GenerationStatus.Succeeded)
            return RedirectToPage("/Review", new { course, node, difficulty });

        if (_generation.ClearJob(course, node, GenerationStatus.Failed))
            Error = job.Error;
        else
            return RedirectToPage("/Review", new { course, node, difficulty });

        return Page();
    }
}
