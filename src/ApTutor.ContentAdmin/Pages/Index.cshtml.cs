using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

public enum NodeReviewStatus { NotStarted, Generating, PendingReview, Live }

public sealed record NodeRow(string NodeId, string Title, NodeReviewStatus Status);
public sealed record UnitGroup(int Unit, string Title, IReadOnlyList<NodeRow> Nodes);
public sealed record CourseSection(string CourseId, string DisplayName, IReadOnlyList<UnitGroup> Units);

// Only OnPostGenerateUnitAsync spends real, billed API money — see Program.cs for why this
// attribute (which applies to every handler on this page, not just that one) is the right
// trade-off for Razor Pages' per-page endpoint granularity.
[EnableRateLimiting("content-mutations")]
public sealed class IndexModel : PageModel
{
    private readonly CourseCatalog _catalog;
    private readonly S3ContentStore _store;
    private readonly ContentGenerationService _generation;

    public IndexModel(CourseCatalog catalog, S3ContentStore store, ContentGenerationService generation)
    {
        _catalog = catalog;
        _store = store;
        _generation = generation;
    }

    public IReadOnlyList<CourseSection> Courses { get; private set; } = Array.Empty<CourseSection>();

    public async Task OnGetAsync()
    {
        var sections = new List<CourseSection>();
        foreach (var course in _catalog.All.OrderBy(c => c.DisplayName, StringComparer.Ordinal))
        {
            // One manifest fetch covers every node in the course — checking S3 per node (69+ nodes
            // for CS A alone) would be dozens of remote calls just to render this page.
            var manifest = await _store.GetCourseManifestAsync(course.CourseId);
            sections.Add(BuildSection(course, manifest));
        }
        Courses = sections;
    }

    /// Bulk-triggers Generate for every "Not started" node in one unit — a convenience over
    /// clicking into each node's Review page individually when building out a course's content
    /// library unit by unit. Each node still gets its own independent background job exactly like a
    /// single Generate click (TryStartGeneration already refuses a duplicate for a node that's
    /// already got one), and every draft still needs its own individual human review/approve
    /// afterward — this only bulks the "kick off generation" step, nothing downstream of it.
    public async Task<IActionResult> OnPostGenerateUnitAsync(string course, int unit)
    {
        if (!_catalog.TryGet(course, out var courseInfo))
            return NotFound();

        var manifest = await _store.GetCourseManifestAsync(course);
        foreach (var node in courseInfo.Graph.Dag.Nodes.Where(n => n.Unit == unit))
        {
            if (StatusFor(course, node, manifest) == NodeReviewStatus.NotStarted)
                _generation.TryStartGeneration(course, node.Id);
        }

        return RedirectToPage("/Index");
    }

    private CourseSection BuildSection(CourseInfo course, CourseManifest manifest)
    {
        var unitTitles = course.Graph.Dag.Units.ToDictionary(u => u.Unit, u => u.Title);

        var units = course.Graph.Dag.Nodes
            .GroupBy(n => n.Unit)
            .OrderBy(g => g.Key)
            .Select(g => new UnitGroup(
                g.Key,
                unitTitles.TryGetValue(g.Key, out var title) ? title : $"Unit {g.Key}",
                g.OrderBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n => new NodeRow(n.Id, n.Title, StatusFor(course.CourseId, n, manifest)))
                    .ToList()))
            .ToList();

        return new CourseSection(course.CourseId, course.DisplayName, units);
    }

    private NodeReviewStatus StatusFor(string courseId, DagNode node, CourseManifest manifest)
    {
        // Checked regardless of the manifest state — "generate a new attempt" on an already-live
        // node should show as in-flight/pending, not silently stay Live until the SME approves it.
        var job = _generation.GetJob(courseId, node.Id);
        if (job?.Status == GenerationStatus.Running) return NodeReviewStatus.Generating;
        if (job?.Status == GenerationStatus.Succeeded) return NodeReviewStatus.PendingReview;

        if (manifest.Nodes.ContainsKey(node.Id)) return NodeReviewStatus.Live;
        return NodeReviewStatus.NotStarted;
    }
}
