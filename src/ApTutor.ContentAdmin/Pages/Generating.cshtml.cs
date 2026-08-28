using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApTutor.ContentAdmin.Pages;

public sealed class GeneratingModel : PageModel
{
    private readonly ContentGenerationService _generation;

    public GeneratingModel(ContentGenerationService generation) => _generation = generation;

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

        var job = _generation.GetJob(course, node, difficulty);
        if (job is null)
            return RedirectToPage("/Review", new { course, node, difficulty });

        if (job.Status == GenerationStatus.Running)
        {
            IsRunning = true;
            return Page();
        }

        if (job.Status == GenerationStatus.Succeeded)
        {
            // Job stays in memory — Review reads it directly to show the draft for approval.
            // Clearing here would throw the generated content away before anyone's seen it.
            return RedirectToPage("/Review", new { course, node, difficulty });
        }

        // Failed: clear so the node is free to try again, then show the error right here (not lost
        // in a redirect). If ClearJob loses the race (e.g. cleared elsewhere already), the state is
        // already resolved one way or another — just send the SME back to Review to see it.
        if (_generation.ClearJob(course, node, difficulty, GenerationStatus.Failed))
            Error = job.Error;
        else
            return RedirectToPage("/Review", new { course, node, difficulty });

        return Page();
    }
}
