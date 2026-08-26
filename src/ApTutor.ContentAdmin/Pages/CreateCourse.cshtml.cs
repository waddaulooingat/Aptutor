using System.Text.RegularExpressions;
using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

/// Entry point for Content Admin's "Create new course" (see the Shell-display-only/
/// course-authoring plan's Part C) — the top-level authoring action, one level above "Generate
/// unit structure": drafts a brand-new course's unit list, not any unit's node structure.
[EnableRateLimiting("content-mutations")]
public sealed partial class CreateCourseModel : PageModel
{
    // Lowercase letters/digits, starting with a letter — matches the shape of every existing
    // courseId ("csa", "worldhistory") and is safe to use directly as an S3 key segment.
    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex CourseIdPattern();

    // "AP" as a standalone word, not as a substring of an unrelated word (e.g. "Mapping").
    [GeneratedRegex(@"\bAP\b")]
    private static partial Regex ApAbbreviationPattern();

    private readonly CourseCatalog _catalog;
    private readonly CourseCreationService _creation;

    public CreateCourseModel(CourseCatalog catalog, CourseCreationService creation)
    {
        _catalog = catalog;
        _creation = creation;
    }

    public string? Error { get; private set; }

    public void OnGet()
    {
    }

    public IActionResult OnPost(string courseId, string courseName, string displayName, string? guidance)
    {
        courseId = (courseId ?? "").Trim();
        courseName = (courseName ?? "").Trim();
        displayName = (displayName ?? "").Trim();

        if (!CourseIdPattern().IsMatch(courseId))
        {
            Error = "Course id must be lowercase letters and numbers only, starting with a letter (e.g. \"physics1\").";
            return Page();
        }
        if (string.IsNullOrWhiteSpace(courseName) || string.IsNullOrWhiteSpace(displayName))
        {
            Error = "Enter both a course name and a display name.";
            return Page();
        }
        // Trademark guardrail — see DagMeta's remarks. Course name can freely say "AP Physics 1"
        // (it's internal/descriptive context for generation); display name is shown to students
        // directly and must never say "AP" or "Advanced Placement".
        if (ApAbbreviationPattern().IsMatch(displayName) || displayName.Contains("Advanced Placement", StringComparison.OrdinalIgnoreCase))
        {
            Error = "The display name can't say \"AP\" or \"Advanced Placement\" — that's shown directly to students. Put that in the course name field instead.";
            return Page();
        }
        if (_catalog.TryGet(courseId, out _))
        {
            Error = $"A course with id \"{courseId}\" already exists.";
            return Page();
        }

        // TryStartGeneration itself refuses a duplicate job for a courseId already in flight —
        // either way, the SME lands on the same polling page.
        _creation.TryStartGeneration(courseId, courseName, displayName, string.IsNullOrWhiteSpace(guidance) ? null : guidance.Trim());
        return RedirectToPage("/CreatingCourse", new { course = courseId });
    }
}
