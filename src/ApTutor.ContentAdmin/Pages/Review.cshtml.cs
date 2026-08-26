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
    private readonly S3ContentStore _store;
    private readonly RejectionLog _rejectionLog;
    private readonly ILogger<ReviewModel> _logger;

    public ReviewModel(
        CourseCatalog catalog, ContentGenerationService generation, S3ContentStore store, RejectionLog rejectionLog, ILogger<ReviewModel> logger)
    {
        _catalog = catalog;
        _generation = generation;
        _store = store;
        _rejectionLog = rejectionLog;
        _logger = logger;
    }

    public string CourseId { get; private set; } = "";
    public string NodeId { get; private set; } = "";
    public string NodeTitle { get; private set; } = "";
    public string CourseDisplayName { get; private set; } = "";
    public NodeContentPack? Pack { get; private set; }
    public bool PackIsPending { get; private set; }
    public string? Notice { get; private set; }
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string course, string node)
    {
        if (!TryLoadContext(course, node, out var courseInfo, out var actionResult))
            return actionResult!;

        var job = _generation.GetJob(course, node);
        switch (job?.Status)
        {
            case GenerationStatus.Running:
                // Don't show a stale/empty review view — send the SME to the page that's actually
                // watching this generation.
                return RedirectToPage("/Generating", new { course, node });

            case GenerationStatus.Succeeded:
                // An unapproved draft always wins over whatever's live — this is how "generate a
                // new attempt" on an already-approved node lets the SME review the replacement.
                Pack = job.Pack;
                PackIsPending = true;
                break;

            case GenerationStatus.Failed:
                Error = job.Error;
                Pack = await _store.TryGetLiveNodeAsync(course, node);
                PackIsPending = false;
                break;

            default:
                Pack = await _store.TryGetLiveNodeAsync(course, node);
                PackIsPending = false;
                break;
        }

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

        var job = _generation.GetJob(course, node);
        if (job is not { Status: GenerationStatus.Succeeded })
        {
            // No draft to act on — either this was already approved (double-click/retry after
            // success) or nothing was ever generated. Check the live store to tell which.
            await ShowAlreadyHandledOrNothingToApproveAsync(course, node);
            return Page();
        }

        var pack = job.Pack!;

        // Per-item keep/discard: each question has a "keep_<itemId>" checkbox (checked by
        // default) and a "reason_<itemId>" textarea. A discarded item requires a reason — same
        // "no silent rejection" rule the whole-node Reject already enforces. Validated BEFORE
        // claiming the job below — a validation failure must leave the draft in place so the SME
        // can fix the reason and resubmit, not lose it.
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
                PackIsPending = true;
                return Page();
            }
            discarded.Add((item.Id, item.Prompt, reason));
        }

        // Claim the job atomically, only now that validation passed — a losing concurrent request
        // (double-click, browser retry) falls into the "already handled" branch above instead of
        // both writers racing to S3.
        if (!_generation.ClearJob(course, node, GenerationStatus.Succeeded))
        {
            await ShowAlreadyHandledOrNothingToApproveAsync(course, node);
            return Page();
        }

        var approvedPack = pack with { PracticeItems = kept, Verified = true };
        try
        {
            await _store.ApproveNodeAsync(course, node, approvedPack);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Approve failed for {Course}/{Node}; restoring draft so it can be retried", course, node);
            _generation.RestoreJob(job);
            Error = "Approving failed — please try again in a moment.";
            Pack = pack;
            PackIsPending = true;
            return Page();
        }

        if (discarded.Count > 0)
            await _rejectionLog.RecordItemsAsync(course, node, discarded);

        Notice = discarded.Count == 0
            ? "Approved. This content is now live."
            : kept.Count == 0
                ? "Approved with no questions kept — the explanation is live, but you'll want to generate new questions for this topic."
                : $"Approved with {kept.Count} question(s) live. {discarded.Count} discarded.";
        Pack = approvedPack;
        PackIsPending = false;
        return Page();
    }

    public async Task<IActionResult> OnPostRejectAsync(string course, string node, string reason)
    {
        if (!TryLoadContext(course, node, out var courseInfo, out var actionResult))
            return actionResult!;

        if (string.IsNullOrWhiteSpace(reason))
        {
            Error = "Please say why you're rejecting this before it can be discarded.";
            if (_generation.GetJob(course, node) is { Status: GenerationStatus.Succeeded } pending)
            {
                Pack = pending.Pack;
                PackIsPending = true;
            }
            return Page();
        }

        if (!_generation.ClearJob(course, node, GenerationStatus.Succeeded))
        {
            Notice = "There was nothing pending to reject for this topic.";
            return Page();
        }

        try
        {
            await _rejectionLog.RecordAsync(course, node, reason.Trim());
        }
        catch (Exception ex)
        {
            // The draft is already discarded from the SME's point of view either way — a failure
            // to durably log *why* doesn't need to become their problem, just ours to notice.
            _logger.LogError(ex, "Failed to record rejection reason for {Course}/{Node} (draft was already discarded)", course, node);
        }

        Notice = "Rejected. You can generate a new attempt for this topic any time.";
        return Page();
    }

    private async Task ShowAlreadyHandledOrNothingToApproveAsync(string course, string node)
    {
        var live = await _store.TryGetLiveNodeAsync(course, node);
        if (live is not null)
        {
            Notice = "This topic is already approved.";
            Pack = live;
            PackIsPending = false;
        }
        else
        {
            Error = "There's nothing generated for this topic yet.";
        }
    }

    private bool TryLoadContext(string course, string node, out CourseInfo courseInfo, out IActionResult? actionResult)
    {
        CourseId = course;
        NodeId = node;
        actionResult = null;
        courseInfo = null!;

        if (!_catalog.TryGet(course, out courseInfo) || courseInfo.Graph is not { } graph || !graph.Exists(node))
        {
            actionResult = NotFound();
            return false;
        }

        CourseDisplayName = courseInfo.DisplayName;
        NodeTitle = graph.Node(node).Title;
        return true;
    }
}
