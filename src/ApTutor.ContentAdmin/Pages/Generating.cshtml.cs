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
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public IActionResult OnGet(string course, string node)
    {
        CourseId = course;
        NodeId = node;

        var job = _generation.GetJob(course, node);
        if (job is null)
            return RedirectToPage("/Review", new { course, node });

        if (job.Status == GenerationStatus.Running)
        {
            IsRunning = true;
            return Page();
        }

        // Terminal state: clear the tracking entry now that the SME is about to see the outcome,
        // so the node is free to be regenerated. Succeeded goes straight to Review; Failed renders
        // right here so the error message isn't lost in a redirect.
        _generation.ClearJob(course, node);

        if (job.Status == GenerationStatus.Failed)
        {
            Error = job.Error;
            return Page();
        }

        return RedirectToPage("/Review", new { course, node });
    }
}
