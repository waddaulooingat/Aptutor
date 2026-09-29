using ApTutor.Content;

namespace ApTutor.ContentAdmin.Services;

/// One node+difficulty combination the scan found with no approved content yet — the same
/// "NotStarted" definition Index.cshtml.cs already uses per-node (at least one approved set per
/// difficulty per node), just aggregated into a batch instead of requiring a person to click into
/// each node individually. See the AI-content-agent handoff's Part A: "confirm this matches whatever
/// multi-set/difficulty completeness expectations already exist" — it does, deliberately, so this
/// scan and the existing Index page's per-node status always agree on what counts as a gap.
public sealed record ContentGap(string CourseId, string NodeId, Difficulty Difficulty);

/// Everything the scan looked at and did. GapsFound can exceed Triggered when
/// AiContentAgentOptions.MaxGenerationsPerScan capped this run — the remaining gaps are simply left
/// for the next scan, not an error.
public sealed record ScanResult(int GapsFound, int Triggered, int SkippedAlreadyInFlight);

/// One CURRENTLY-LIVE piece of content that the AI touched — either generation, review, or both (see
/// NodeContentPack/LearnContent's own remarks on why those are independent bits). Difficulty is null
/// for learn content (not difficulty-scoped — see LearnContent's remarks); ContentType distinguishes
/// the two, since a node can appear in this list once per difficulty for practice content AND once
/// more for learn content. Deliberately reports only the LATEST version per node+difficulty/type, not
/// every historical version the manifest accumulates (see AiAgentModel's own AiReviewedVersionCount
/// for that all-time total) — this list is "what a human should go look at right now," not an audit
/// of everything the AI has ever produced.
public sealed record AiTouchedItem(string NodeId, string NodeTitle, string ContentType, Difficulty? Difficulty, bool AiGenerated, bool AiReviewed);

/// Part A of the AI Content Agent interim stopgap (see the handoff): finds nodes/difficulties with no
/// approved content yet and triggers generation itself, instead of requiring a person to open
/// Content Admin and click "Generate" per unit. Deliberately on-demand only (a person calls
/// RunScanAsync) rather than scheduled — see the handoff's own "Open items" recommendation to start
/// simple rather than building scheduling infrastructure for what's explicitly a temporary tool.
///
/// Scoped to PRACTICE-ITEM generation only — the "one approved set per difficulty" completeness bar
/// is a practice-item/difficulty concept with no learn-content equivalent (a node has exactly one
/// current explanation, not a per-difficulty library — see LearnContent's remarks), so learn content
/// isn't part of what this scan looks for. Whatever this scan generates still goes through the exact
/// same AI review pass as anything else (see ContentGenerationService.RunAsync) — this class only
/// automates the "notice a gap and click Generate" step, never review/approval.
public sealed class AutonomousContentAgentService
{
    private readonly CourseCatalog _catalog;
    private readonly S3ContentStore _store;
    private readonly ContentGenerationService _generation;

    public AutonomousContentAgentService(CourseCatalog catalog, S3ContentStore store, ContentGenerationService generation)
    {
        _catalog = catalog;
        _store = store;
        _generation = generation;
    }

    private static readonly IReadOnlyList<Difficulty> AllDifficulties = new[] { Difficulty.Easy, Difficulty.Medium, Difficulty.Hard };

    /// Every current gap for a course, without triggering anything — lets the AiAgent page show what
    /// a scan would do before someone commits to running it.
    public async Task<IReadOnlyList<ContentGap>> FindGapsAsync(string courseId, CancellationToken ct = default)
    {
        if (!_catalog.TryGet(courseId, out var course) || course.Graph is not { } graph)
            return Array.Empty<ContentGap>();

        var manifest = await _store.GetCourseManifestAsync(courseId, ct);
        var gaps = new List<ContentGap>();
        foreach (var node in graph.Dag.Nodes)
            foreach (var difficulty in AllDifficulties)
                if (IsGap(node.Id, difficulty, manifest))
                    gaps.Add(new ContentGap(courseId, node.Id, difficulty));

        return gaps;
    }

    /// Finds every current gap and triggers generation for up to MaxGenerationsPerScan of them —
    /// each as an ordinary background TryStartGeneration job (see ContentGenerationService), just
    /// like a person clicking "Generate remaining" but driven by the scan instead of a click per
    /// unit. A gap already claimed by an in-flight job (e.g. a person started it manually moments
    /// ago) is counted as skipped, not triggered again.
    public async Task<ScanResult> RunScanAsync(string courseId, int maxGenerations, CancellationToken ct = default)
    {
        var gaps = await FindGapsAsync(courseId, ct);
        var triggered = 0;
        var skipped = 0;

        foreach (var gap in gaps)
        {
            if (triggered >= maxGenerations) break;

            // allowGraphChoices: false — same posture as Index.cshtml.cs's bulk "Generate remaining"
            // action: graph-based answer choices stay an explicit, per-node SME decision, never
            // something a batch/unattended pass opts into on its own.
            if (_generation.TryStartGeneration(gap.CourseId, gap.NodeId, gap.Difficulty, allowGraphChoices: false, aiGenerated: true))
                triggered++;
            else
                skipped++;
        }

        return new ScanResult(gaps.Count, triggered, skipped);
    }

    private static bool IsGap(string nodeId, Difficulty difficulty, CourseManifest manifest) =>
        !manifest.Nodes.TryGetValue(nodeId, out var entry) || entry.LatestFor(difficulty) is null;

    /// Every currently-live node+difficulty/learn-content combination the AI touched (see
    /// AiTouchedItem's remarks) — the answer to "what did the AI agent actually generate/approve
    /// that's live right now," as opposed to AiAgentModel's cumulative all-time count. Checks learn
    /// content per node too (one extra S3 GET each — acceptable for an on-demand admin page, not a
    /// hot path) even though the gap SCAN above never generates it, since Part B's AI review pass
    /// does apply to manually-triggered learn content generation too.
    public async Task<IReadOnlyList<AiTouchedItem>> FindAiTouchedContentAsync(string courseId, CancellationToken ct = default)
    {
        if (!_catalog.TryGet(courseId, out var course) || course.Graph is not { } graph)
            return Array.Empty<AiTouchedItem>();

        var manifest = await _store.GetCourseManifestAsync(courseId, ct);
        var items = new List<AiTouchedItem>();

        foreach (var node in graph.Dag.Nodes)
        {
            if (manifest.Nodes.TryGetValue(node.Id, out var entry))
            {
                foreach (var difficulty in AllDifficulties)
                {
                    if (entry.LatestFor(difficulty) is { } latest && (latest.AiGenerated || latest.AiReviewed))
                        items.Add(new AiTouchedItem(node.Id, node.Title, "Practice", difficulty, latest.AiGenerated, latest.AiReviewed));
                }
            }

            var learnContent = await _store.TryGetLiveLearnContentAsync(courseId, node.Id, ct);
            if (learnContent is { } learn && (learn.AiGenerated || learn.AiReviewed))
                items.Add(new AiTouchedItem(node.Id, node.Title, "Learn", Difficulty: null, learn.AiGenerated, learn.AiReviewed));
        }

        return items;
    }
}
