using ApTutor.ContentAdmin.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ApTutor.ContentAdmin.Pages;

/// GapCount is the practice-item completeness gap only (see AutonomousContentAgentService's
/// remarks); AiReviewedVersionCount is every approved node-version across every difficulty this
/// course has that the AI reviewer — not a human — approved, i.e. Part C's "queryable so a future
/// human SME pass can find and prioritize it" made concrete as a per-course count on this page.
public sealed record CourseAgentSummary(string CourseId, string DisplayName, int GapCount, int AiReviewedVersionCount);

// A scan can trigger real, billed generation calls (up to MaxGenerationsPerScan of them from one
// click) — same rate-limiting posture as Index/Review's own generation-triggering handlers.
[EnableRateLimiting("content-mutations")]
public sealed class AiAgentModel : PageModel
{
    private readonly CourseCatalog _catalog;
    private readonly S3ContentStore _store;
    private readonly AutonomousContentAgentService _agent;
    private readonly IOptions<AiContentAgentOptions> _options;

    public AiAgentModel(CourseCatalog catalog, S3ContentStore store, AutonomousContentAgentService agent, IOptions<AiContentAgentOptions> options)
    {
        _catalog = catalog;
        _store = store;
        _agent = agent;
        _options = options;
    }

    public bool Enabled { get; private set; }
    public int MaxGenerationsPerScan { get; private set; }
    public IReadOnlyList<CourseAgentSummary> Courses { get; private set; } = Array.Empty<CourseAgentSummary>();
    public string? Notice { get; private set; }

    public async Task OnGetAsync()
    {
        Enabled = _options.Value.Enabled;
        MaxGenerationsPerScan = _options.Value.MaxGenerationsPerScan;
        Courses = await BuildSummariesAsync();
    }

    /// Runs one scan-and-fill pass for a single course (see AutonomousContentAgentService.RunScanAsync)
    /// — a person kicks this off on demand; there's no scheduled/background version (see the
    /// AI-content-agent handoff's own recommendation to start on-demand for an interim tool).
    /// Refuses to run while the interim pipeline is disabled — an unattended generation pass with no
    /// AI review resolving it afterward would just pile up unreviewed drafts, not help anything.
    public async Task<IActionResult> OnPostRunScanAsync(string course)
    {
        if (!_catalog.TryGet(course, out var courseInfo) || courseInfo.Graph is null)
            return NotFound();

        if (!_options.Value.Enabled)
        {
            Notice = "The AI Content Agent is currently disabled (AiContentAgent:Enabled is false in configuration) — enable it before running a scan.";
        }
        else
        {
            var result = await _agent.RunScanAsync(course, _options.Value.MaxGenerationsPerScan);
            Notice = result.SkippedAlreadyInFlight > 0
                ? $"Scan complete for {courseInfo.DisplayName}: {result.GapsFound} gap(s) found, {result.Triggered} generation(s) triggered, {result.SkippedAlreadyInFlight} already in flight."
                : $"Scan complete for {courseInfo.DisplayName}: {result.GapsFound} gap(s) found, {result.Triggered} generation(s) triggered.";
        }

        Enabled = _options.Value.Enabled;
        MaxGenerationsPerScan = _options.Value.MaxGenerationsPerScan;
        Courses = await BuildSummariesAsync();
        return Page();
    }

    private async Task<IReadOnlyList<CourseAgentSummary>> BuildSummariesAsync()
    {
        var summaries = new List<CourseAgentSummary>();
        foreach (var course in _catalog.All.OrderBy(c => c.DisplayName, StringComparer.Ordinal))
        {
            if (course.Graph is null)
            {
                summaries.Add(new CourseAgentSummary(course.CourseId, course.DisplayName, GapCount: 0, AiReviewedVersionCount: 0));
                continue;
            }

            var gaps = await _agent.FindGapsAsync(course.CourseId);
            var manifest = await _store.GetCourseManifestAsync(course.CourseId);
            var aiReviewedCount = manifest.Nodes.Values.Sum(entry => entry.Versions.Count(v => v.AiReviewed));
            summaries.Add(new CourseAgentSummary(course.CourseId, course.DisplayName, gaps.Count, aiReviewedCount));
        }
        return summaries;
    }
}
