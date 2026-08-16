using ApTutor.Content;
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

    public IndexModel(CourseCatalog catalog) => _catalog = catalog;

    public IReadOnlyList<CourseSection> Courses { get; private set; } = Array.Empty<CourseSection>();

    public void OnGet()
    {
        Courses = _catalog.All
            .OrderBy(c => c.DisplayName, StringComparer.Ordinal)
            .Select(BuildSection)
            .ToList();
    }

    private static CourseSection BuildSection(CourseInfo course)
    {
        var unitTitles = course.Graph.Dag.Units.ToDictionary(u => u.Unit, u => u.Title);

        var units = course.Graph.Dag.Nodes
            .GroupBy(n => n.Unit)
            .OrderBy(g => g.Key)
            .Select(g => new UnitGroup(
                g.Key,
                unitTitles.TryGetValue(g.Key, out var title) ? title : $"Unit {g.Key}",
                g.OrderBy(n => n.Id, StringComparer.Ordinal)
                    .Select(n => new NodeRow(n.Id, n.Title, StatusFor(course, n)))
                    .ToList()))
            .ToList();

        return new CourseSection(course.CourseId, course.DisplayName, units);
    }

    private static NodeReviewStatus StatusFor(CourseInfo course, DagNode node)
    {
        var pack = ContentPackStore.TryLoad(course.ContentDir, node.Id);
        if (pack is null) return NodeReviewStatus.NotStarted;
        return pack.Verified ? NodeReviewStatus.Live : NodeReviewStatus.PendingReview;
    }
}
