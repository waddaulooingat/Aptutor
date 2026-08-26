using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApTutor.ContentAdmin.Pages;

public sealed class CreatingCourseModel : PageModel
{
    private readonly CourseCreationService _creation;

    public CreatingCourseModel(CourseCreationService creation) => _creation = creation;

    public string CourseId { get; private set; } = "";
    public bool IsRunning { get; private set; }
    public string? Error { get; private set; }

    public IActionResult OnGet(string course)
    {
        CourseId = course;

        var job = _creation.GetJob(course);
        if (job is null)
            return RedirectToPage("/CreateCourse");

        if (job.Status == CourseCreationStatus.Running)
        {
            IsRunning = true;
            return Page();
        }

        if (job.Status == CourseCreationStatus.Succeeded)
        {
            // Job stays in memory — ReviewCourse reads it directly to show the draft for approval.
            return RedirectToPage("/ReviewCourse", new { course });
        }

        // Failed: clear so this courseId is free to try again, then show the error right here.
        if (_creation.ClearJob(course, CourseCreationStatus.Failed))
            Error = job.Error;
        else
            return RedirectToPage("/CreateCourse");

        return Page();
    }
}
