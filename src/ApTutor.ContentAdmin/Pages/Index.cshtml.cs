using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

public enum NodeReviewStatus { NotStarted, Generating, PendingReview, Live }

// Status is now per difficulty, not a single value — a node can be Live at "medium" while still
// NotStarted at "hard" (see the difficulty-levels plan). AllDifficulties gives the view a stable
// iteration order without hardcoding the enum's members in the .cshtml.
public static class Difficulties
{
    public static readonly IReadOnlyList<Difficulty> All = new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard };
}

public sealed record NodeRow(string NodeId, string Title, IReadOnlyDictionary<Difficulty, NodeReviewStatus> StatusByDifficulty);
public sealed record UnitGroup(int Unit, string Title, IReadOnlyList<NodeRow> Nodes)
{
    /// How many nodes in this unit are NotStarted at a given difficulty — backs the per-difficulty
    /// "Generate remaining" bulk action below.
    public int NotStartedCount(Difficulty difficulty) => Nodes.Count(n => n.StatusByDifficulty[difficulty] == NodeReviewStatus.NotStarted);
}

/// HasStructure is false for a course that exists (it's in the S3 course index) but has no approved
/// DAG yet — e.g. one just created via "Create new course" (unit list approved, no nodes filled in
/// per unit yet). Units is empty in that case; the view shows a distinct message rather than an
/// unexplained blank section.
public sealed record CourseSection(string CourseId, string DisplayName, bool HasStructure, IReadOnlyList<UnitGroup> Units);

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

    /// Bulk-triggers Generate for every "Not started" node in one unit, at one chosen difficulty —
    /// a convenience over clicking into each node's Review page individually when building out a
    /// course's content library unit by unit. Scoped to a single difficulty per click (not "every
    /// difficulty at once") since each difficulty is its own independent generation job and review
    /// pass — bulk-firing all three at once would triple the SME's simultaneous review queue with
    /// no way to ask for just one. Each node still gets its own independent background job exactly
    /// like a single Generate click (TryStartGeneration already refuses a duplicate for a
    /// node+difficulty that's already got one), and every draft still needs its own individual
    /// human review/approve afterward — this only bulks the "kick off generation" step.
    public async Task<IActionResult> OnPostGenerateUnitAsync(string course, int unit, Difficulty difficulty)
    {
        if (!_catalog.TryGet(course, out var courseInfo) || courseInfo.Graph is not { } graph)
            return NotFound();

        var manifest = await _store.GetCourseManifestAsync(course);
        foreach (var node in graph.Dag.Nodes.Where(n => n.Unit == unit))
        {
            if (StatusFor(course, node, difficulty, manifest) == NodeReviewStatus.NotStarted)
                _generation.TryStartGeneration(course, node.Id, difficulty);
        }

        return RedirectToPage("/Index");
    }

    private CourseSection BuildSection(CourseInfo course, CourseManifest manifest)
    {
        if (course.Graph is not { } graph)
            return new CourseSection(course.CourseId, course.DisplayName, HasStructure: false, Array.Empty<UnitGroup>());

        var unitTitles = graph.Dag.Units.ToDictionary(u => u.Unit, u => u.Title);

        var units = graph.Dag.Nodes
            .GroupBy(n => n.Unit)
            .OrderBy(g => g.Key)
            .Select(g => new UnitGroup(
                g.Key,
                unitTitles.TryGetValue(g.Key, out var title) ? title : $"Unit {g.Key}",
                g.OrderBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n => new NodeRow(
                        n.Id,
                        n.Title,
                        Difficulties.All.ToDictionary(d => d, d => StatusFor(course.CourseId, n, d, manifest))))
                    .ToList()))
            .ToList();

        return new CourseSection(course.CourseId, course.DisplayName, HasStructure: true, units);
    }

    private NodeReviewStatus StatusFor(string courseId, DagNode node, Difficulty difficulty, CourseManifest manifest)
    {
        // Checked regardless of the manifest state — "generate a new attempt" on an already-live
        // node should show as in-flight/pending, not silently stay Live until the SME approves it.
        var job = _generation.GetJob(courseId, node.Id, difficulty);
        if (job?.Status == GenerationStatus.Running) return NodeReviewStatus.Generating;
        if (job?.Status == GenerationStatus.Succeeded) return NodeReviewStatus.PendingReview;

        if (manifest.Nodes.TryGetValue(node.Id, out var entry) && entry.LatestFor(difficulty) is not null)
            return NodeReviewStatus.Live;
        return NodeReviewStatus.NotStarted;
    }
}
