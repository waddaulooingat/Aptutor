using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ApTutor.ContentAdmin.Pages;

public enum NodeReviewStatus { NotStarted, PendingReview, Live }

public sealed record NodeRow(string NodeId, string Title, NodeReviewStatus Status);
public sealed record UnitGroup(int Unit, string Title, IReadOnlyList<NodeRow> Nodes);
public sealed record CourseSection(string CourseId, string DisplayName, IReadOnlyList<UnitGroup> Units);

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
        if (manifest.Nodes.ContainsKey(node.Id)) return NodeReviewStatus.Live;

        // Checked regardless of the manifest state — "generate a new attempt" on an already-live
        // node should show as pending, not silently stay Live until the SME approves the redo.
        var job = _generation.GetJob(courseId, node.Id);
        return job?.Status == GenerationStatus.Succeeded ? NodeReviewStatus.PendingReview : NodeReviewStatus.NotStarted;
    }
}
