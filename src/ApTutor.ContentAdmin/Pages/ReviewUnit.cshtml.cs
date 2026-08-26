using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

/// Reviewing a proposed unit structure is deliberately more careful than node-content review's
/// fast per-item keep/discard (see the Shell-display-only/course-authoring plan's Part B) — a bad
/// node id or prereq is expensive to unwind once other content references it, unlike a bad practice
/// question, which is cheap to reject and regenerate. Every field is editable inline before Approve,
/// not just keep-or-discard.
[EnableRateLimiting("content-mutations")]
public sealed class ReviewUnitModel : PageModel
{
    private readonly CourseCatalog _catalog;
    private readonly UnitStructureGenerationService _generation;
    private readonly S3ContentStore _store;
    private readonly ILogger<ReviewUnitModel> _logger;

    public ReviewUnitModel(CourseCatalog catalog, UnitStructureGenerationService generation, S3ContentStore store, ILogger<ReviewUnitModel> logger)
    {
        _catalog = catalog;
        _generation = generation;
        _store = store;
        _logger = logger;
    }

    public string CourseId { get; private set; } = "";
    public string CourseDisplayName { get; private set; } = "";
    public int Unit { get; private set; }
    public string UnitTitle { get; private set; } = "";
    public IReadOnlyList<DagNode> ProposedNodes { get; private set; } = Array.Empty<DagNode>();
    public string? Error { get; private set; }
    public string? Notice { get; private set; }

    public IActionResult OnGet(string course, int unit)
    {
        if (!_catalog.TryGet(course, out var courseInfo))
            return NotFound();

        CourseId = course;
        CourseDisplayName = courseInfo.DisplayName;
        Unit = unit;

        var job = _generation.GetJob(course, unit);
        if (job is null || job.Status != StructureGenerationStatus.Succeeded)
            return RedirectToPage("/GenerateUnit", new { course });

        UnitTitle = job.UnitTitle;
        ProposedNodes = job.ProposedNodes!;
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(string course, int unit, string unitTitle)
    {
        if (!_catalog.TryGet(course, out var courseInfo))
            return NotFound();

        CourseId = course;
        CourseDisplayName = courseInfo.DisplayName;
        Unit = unit;
        UnitTitle = unitTitle;

        var job = _generation.GetJob(course, unit);
        if (job is null || job.Status != StructureGenerationStatus.Succeeded)
        {
            Notice = "This unit's draft was already handled — generate again if you want to make more changes.";
            return Page();
        }

        var kept = new List<DagNode>();
        foreach (var proposedNode in job.ProposedNodes!)
        {
            if (!Request.Form.ContainsKey($"keep_{proposedNode.Id}")) continue;

            var title = Request.Form[$"title_{proposedNode.Id}"].ToString().Trim();
            var typeRaw = Request.Form[$"type_{proposedNode.Id}"].ToString();
            var viz = Request.Form[$"viz_{proposedNode.Id}"].ToString().Trim();
            var prereqs = Request.Form[$"prereqs_{proposedNode.Id}"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (string.IsNullOrWhiteSpace(title) || !Enum.TryParse<NodeType>(typeRaw, ignoreCase: true, out var type))
            {
                Error = $"\"{proposedNode.Id}\" needs a title and a valid type.";
                ProposedNodes = job.ProposedNodes!;
                return Page();
            }

            kept.Add(new DagNode(proposedNode.Id, unit, type, title, prereqs, viz));
        }

        if (kept.Count == 0)
        {
            Error = "Keep at least one node, or leave this page to discard the draft and regenerate.";
            ProposedNodes = job.ProposedNodes!;
            return Page();
        }

        // Claim atomically only now, after validating the submitted form — a losing concurrent
        // submit (double click, browser retry) sees the "already handled" branch above instead of
        // racing this one to write S3 twice.
        if (!_generation.ClearJob(course, unit, StructureGenerationStatus.Succeeded))
        {
            Notice = "This unit's draft was already handled.";
            return Page();
        }

        try
        {
            var baseline = courseInfo.Graph?.Dag ?? StructureMerge.EmptyStructure(course, courseInfo.DisplayName);
            var merged = StructureMerge.UpsertUnit(baseline, unit, unitTitle.Trim(), kept);
            _ = new SkillGraph(merged); // validates duplicate ids, unknown prereqs, and cycles across the WHOLE course

            await _store.ApproveStructureAsync(course, merged);
            await _catalog.RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approving unit structure failed for {Course} unit {Unit}", course, unit);
            _generation.RestoreJob(job with { ProposedNodes = kept });
            Error = $"Couldn't approve this structure: {ex.Message}";
            ProposedNodes = kept;
            return Page();
        }

        return RedirectToPage("/Index");
    }
}
