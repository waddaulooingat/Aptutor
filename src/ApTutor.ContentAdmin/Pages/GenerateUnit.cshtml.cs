using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

/// Entry point for Content Admin's "Generate unit structure" (see the Shell-display-only/
/// course-authoring plan's Part B) — one level above per-node content generation: drafts the node
/// list for a unit, not any node's actual content.
[EnableRateLimiting("content-mutations")]
public sealed class GenerateUnitModel : PageModel
{
    private readonly CourseCatalog _catalog;
    private readonly UnitStructureGenerationService _generation;

    public GenerateUnitModel(CourseCatalog catalog, UnitStructureGenerationService generation)
    {
        _catalog = catalog;
        _generation = generation;
    }

    public string CourseId { get; private set; } = "";
    public string CourseDisplayName { get; private set; } = "";
    public string? Error { get; private set; }

    public IActionResult OnGet(string course)
    {
        if (!_catalog.TryGet(course, out var courseInfo))
            return NotFound();

        CourseId = course;
        CourseDisplayName = courseInfo.DisplayName;
        return Page();
    }

    public IActionResult OnPost(string course, int unit, string unitTitle, string? guidance)
    {
        if (!_catalog.TryGet(course, out var courseInfo))
            return NotFound();

        CourseId = course;
        CourseDisplayName = courseInfo.DisplayName;

        if (unit <= 0 || string.IsNullOrWhiteSpace(unitTitle))
        {
            Error = "Enter a positive unit number and a title.";
            return Page();
        }

        // TryStartGeneration itself refuses a duplicate job for a (course, unit) already in
        // flight — either way, the SME lands on the same polling page.
        _generation.TryStartGeneration(course, unit, unitTitle.Trim(), string.IsNullOrWhiteSpace(guidance) ? null : guidance.Trim());
        return RedirectToPage("/GeneratingUnit", new { course, unit });
    }
}
