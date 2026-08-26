using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApTutor.ContentAdmin.Pages;

public sealed class GeneratingUnitModel : PageModel
{
    private readonly UnitStructureGenerationService _generation;

    public GeneratingUnitModel(UnitStructureGenerationService generation) => _generation = generation;

    public string CourseId { get; private set; } = "";
    public int Unit { get; private set; }
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public IActionResult OnGet(string course, int unit)
    {
        CourseId = course;
        Unit = unit;

        var job = _generation.GetJob(course, unit);
        if (job is null)
            return RedirectToPage("/GenerateUnit", new { course });

        if (job.Status == StructureGenerationStatus.Running)
        {
            IsRunning = true;
            return Page();
        }

        if (job.Status == StructureGenerationStatus.Succeeded)
        {
            // Job stays in memory — ReviewUnit reads it directly to show the draft for approval.
            return RedirectToPage("/ReviewUnit", new { course, unit });
        }

        // Failed: clear so this unit is free to try again, then show the error right here (not
        // lost in a redirect). If ClearJob loses the race, the state is already resolved one way
        // or another — just send the SME back to try again.
        if (_generation.ClearJob(course, unit, StructureGenerationStatus.Failed))
            Error = job.Error;
        else
            return RedirectToPage("/GenerateUnit", new { course });

        return Page();
    }
}
