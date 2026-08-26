using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

/// Same "more deliberate than node-content review" posture as ReviewUnit — see its remarks. Every
/// unit's title is editable inline before Approve; unit numbers are assigned by generation, not
/// hand-typed, to avoid the SME accidentally introducing a gap or duplicate.
[EnableRateLimiting("content-mutations")]
public sealed class ReviewCourseModel : PageModel
{
    private readonly CourseCreationService _creation;
    private readonly CourseCatalog _catalog;
    private readonly S3ContentStore _store;
    private readonly ILogger<ReviewCourseModel> _logger;

    public ReviewCourseModel(CourseCreationService creation, CourseCatalog catalog, S3ContentStore store, ILogger<ReviewCourseModel> logger)
    {
        _creation = creation;
        _catalog = catalog;
        _store = store;
        _logger = logger;
    }

    public string CourseId { get; private set; } = "";
    public string CourseName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public IReadOnlyList<UnitInfo> ProposedUnits { get; private set; } = Array.Empty<UnitInfo>();
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public IActionResult OnGet(string course)
    {
        CourseId = course;

        var job = _creation.GetJob(course);
        if (job is null || job.Status != CourseCreationStatus.Succeeded)
            return RedirectToPage("/CreateCourse");

        CourseName = job.CourseName;
        DisplayName = job.DisplayName;
        ProposedUnits = job.ProposedUnits!;
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(string course)
    {
        CourseId = course;

        var job = _creation.GetJob(course);
        if (job is null || job.Status != CourseCreationStatus.Succeeded)
        {
            Notice = "This course's draft was already handled — start over if you want to try again.";
            return Page();
        }

        CourseName = job.CourseName;
        DisplayName = job.DisplayName;

        var kept = new List<UnitInfo>();
        foreach (var proposedUnit in job.ProposedUnits!)
        {
            if (!Request.Form.ContainsKey($"keep_{proposedUnit.Unit}")) continue;

            var title = Request.Form[$"title_{proposedUnit.Unit}"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                Error = $"Unit {proposedUnit.Unit} needs a title.";
                ProposedUnits = job.ProposedUnits!;
                return Page();
            }

            kept.Add(new UnitInfo(proposedUnit.Unit, title));
        }

        if (kept.Count == 0)
        {
            Error = "Keep at least one unit, or leave this page to discard the draft and regenerate.";
            ProposedUnits = job.ProposedUnits!;
            return Page();
        }

        // Claim atomically only now, after validating the submitted form — same reasoning as
        // ReviewUnit.OnPostApproveAsync.
        if (!_creation.ClearJob(course, CourseCreationStatus.Succeeded))
        {
            Notice = "This course's draft was already handled.";
            return Page();
        }

        try
        {
            var dag = new SkillDag(
                Meta: new DagMeta(job.CourseName, NodeCount: 0, DisplayName: job.DisplayName),
                ScenePrimitives: new Dictionary<string, string>(),
                Units: kept.OrderBy(u => u.Unit).ToList(),
                Nodes: Array.Empty<DagNode>());

            await _store.ApproveStructureAsync(course, dag);
            await _catalog.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approving new course failed for {CourseId}", course);
            _creation.RestoreJob(job with { ProposedUnits = kept });
            Error = $"Couldn't create this course: {ex.Message}";
            ProposedUnits = kept;
            return Page();
        }

        return RedirectToPage("/Index");
    }
}
