using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace ApTutor.ContentAdmin.Pages;

// Generate is the endpoint that spends real, billed API money — see Program.cs for why this
// attribute (which applies to every handler on this page, not just Generate) is the right
// trade-off for Razor Pages' per-page endpoint granularity.
[EnableRateLimiting("content-mutations")]
public sealed class ReviewModel : PageModel
{
    private readonly CourseCatalog _catalog;
    private readonly ContentGenerationService _generation;
    private readonly GitRepoService _git;
    private readonly RejectionLog _rejectionLog;

    public ReviewModel(CourseCatalog catalog, ContentGenerationService generation, GitRepoService git, RejectionLog rejectionLog)
    {
        _catalog = catalog;
        _generation = generation;
        _git = git;
        _rejectionLog = rejectionLog;
    }

    public string CourseId { get; private set; } = "";
    public string NodeId { get; private set; } = "";
    public string NodeTitle { get; private set; } = "";
    public string CourseDisplayName { get; private set; } = "";
    public NodeContentPack? Pack { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }

    public IActionResult OnGet(string course, string node)
    {
        if (!TryLoadContext(course, node, out var courseInfo, out var actionResult))
            return actionResult!;

        // A generation is already running for this node — don't show a stale/empty review view,
        // send the SME to the page that's actually watching it.
        if (_generation.GetJob(course, node) is { Status: GenerationStatus.Running })
            return RedirectToPage("/Generating", new { course, node });

        Pack = ContentPackStore.TryLoad(courseInfo.ContentDir, node);
        return Page();
    }

    public IActionResult OnPostGenerate(string course, string node)
    {
        if (!TryLoadContext(course, node, out _, out var actionResult))
            return actionResult!;

        // TryStartGeneration itself refuses a duplicate job for a node already in flight — either
        // way, the SME lands on the same polling page.
        _generation.TryStartGeneration(course, node);
        return RedirectToPage("/Generating", new { course, node });
    }

    public async Task<IActionResult> OnPostApproveAsync(string course, string node)
    {
        if (!TryLoadContext(course, node, out var courseInfo, out var actionResult))
            return actionResult!;

        var pack = ContentPackStore.TryLoad(courseInfo.ContentDir, node);
        if (pack is null)
        {
            Error = "There's nothing generated for this topic yet.";
            return Page();
        }

        if (pack.Verified)
        {
            // Idempotent: a double-click or retried request lands here again after the first one
            // already succeeded — treat it as success rather than re-running the git pipeline.
            Notice = "This topic is already approved.";
            Pack = pack;
            return Page();
        }

        // Per-item keep/discard: each question has a "keep_<itemId>" checkbox (checked by
        // default) and a "reason_<itemId>" textarea. A discarded item requires a reason — same
        // "no silent rejection" rule the whole-node Reject already enforces.
        var kept = new List<PracticeItem>();
        var discarded = new List<(string ItemId, string ItemPrompt, string Reason)>();
        foreach (var item in pack.PracticeItems)
        {
            if (Request.Form.ContainsKey($"keep_{item.Id}"))
            {
                kept.Add(item);
                continue;
            }

            var reason = Request.Form[$"reason_{item.Id}"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(reason))
            {
                Error = $"Please say why you're discarding \"{item.Prompt}\" before approving the rest.";
                Pack = pack;
                return Page();
            }
            discarded.Add((item.Id, item.Prompt, reason));
        }

        ContentPackStore.Save(courseInfo.ContentDir, pack with { PracticeItems = kept, Verified = true });

        var absolutePath = ContentPackStore.PathFor(courseInfo.ContentDir, node);
        var relativePath = Path.GetRelativePath(_git.CloneDir, absolutePath).Replace('\\', '/');
        var commitMessage = discarded.Count == 0
            ? $"content-admin: approve {course}/{node}"
            : $"content-admin: approve {course}/{node} ({discarded.Count} item(s) discarded)";
        await _git.CommitAndPushAsync(new[] { relativePath }, commitMessage);

        if (discarded.Count > 0)
            await _rejectionLog.RecordItemsAsync(courseInfo, node, discarded);

        Notice = discarded.Count == 0
            ? "Approved. This content is now live."
            : kept.Count == 0
                ? "Approved with no questions kept — the explanation is live, but you'll want to generate new questions for this topic."
                : $"Approved with {kept.Count} question(s) live. {discarded.Count} discarded.";
        Pack = ContentPackStore.TryLoad(courseInfo.ContentDir, node);
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(string course, string node, string reason)
    {
        if (!TryLoadContext(course, node, out var courseInfo, out var actionResult))
            return actionResult!;

        if (string.IsNullOrWhiteSpace(reason))
        {
            Error = "Please say why you're rejecting this before it can be discarded.";
            Pack = ContentPackStore.TryLoad(courseInfo.ContentDir, node);
            return Page();
        }

        var pack = ContentPackStore.TryLoad(courseInfo.ContentDir, node);
        if (pack is null)
        {
            Notice = "There was nothing pending to reject for this topic.";
            return Page();
        }

        if (pack.Verified)
        {
            Error = "This topic is already approved and live — it can't be rejected here.";
            Pack = pack;
            return Page();
        }

        ContentPackStore.Delete(courseInfo.ContentDir, node);
        await _rejectionLog.RecordAsync(courseInfo, node, reason.Trim());

        Notice = "Rejected. You can generate a new attempt for this topic any time.";
        return Page();
    }

    private bool TryLoadContext(string course, string node, out CourseInfo courseInfo, out IActionResult? actionResult)
    {
        CourseId = course;
        NodeId = node;
        actionResult = null;
        courseInfo = null!;

        if (!_catalog.TryGet(course, out courseInfo) || !courseInfo.Graph.Exists(node))
        {
            actionResult = NotFound();
            return false;
        }

        CourseDisplayName = courseInfo.DisplayName;
        NodeTitle = courseInfo.Graph.Node(node).Title;
        return true;
    }
}
