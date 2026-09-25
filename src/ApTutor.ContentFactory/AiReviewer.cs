// AI Content Agent — interim review pass (see the AI-content-agent handoff). The real blocker right
// now is landing a human subject-matter expert, not review throughput, so this stands in for that
// human reviewer TEMPORARILY: it runs the same rubric a human is asked to check (correctness,
// difficulty-appropriateness, clarity, no ambiguous/multiple defensible answers, topical alignment —
// see PromptTemplates.SystemForAiReview) and either approves or flags a draft for later human
// re-review. This is an explicit, labeled trust-tier compromise, never a permanent replacement — see
// ContentGenerationService/LearnContentGenerationService for how a verdict here is turned into
// Verified/AiReviewed state, which keeps AI-approved content distinctly tagged from human-verified
// content everywhere it's stored.

using System.Text.Json;
using ApTutor.Content;
using ApTutor.Curriculum;

namespace ApTutor.ContentFactory;

public enum AiReviewVerdict { Approve, Flag }

public sealed record AiReviewResult(AiReviewVerdict Verdict, string Reasoning);

/// ReviewNodeContentAsync/ReviewLearnContentAsync are `virtual` — not because production ever
/// overrides them, but so tests can subclass with a canned verdict (the same "protected/public
/// virtual leaf" seam S3ContentStore uses for its two real-I/O methods) instead of needing a fake
/// HTTP handler wired all the way through ClaudeClient just to test how a verdict gets acted on.
public class AiReviewer
{
    private readonly ClaudeClient _client;

    public AiReviewer(ClaudeClient client) => _client = client;

    public virtual async Task<AiReviewResult> ReviewNodeContentAsync(DagNode node, Difficulty difficulty, NodeContentPack pack, CancellationToken ct = default)
    {
        var schema = GenerationSchema.AiReviewSchema();
        var system = PromptTemplates.SystemForAiReview();
        var user = PromptTemplates.ForNodeContentReview(node, difficulty, pack);
        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_review_verdict", ct);
        return ParseVerdict(inputJson);
    }

    public virtual async Task<AiReviewResult> ReviewLearnContentAsync(DagNode node, LearnContent content, CancellationToken ct = default)
    {
        var schema = GenerationSchema.AiReviewSchema();
        var system = PromptTemplates.SystemForAiReview();
        var user = PromptTemplates.ForLearnContentReview(node, content);
        var inputJson = await _client.GenerateToolInputAsync(system, user, schema, "emit_review_verdict", ct);
        return ParseVerdict(inputJson);
    }

    private static AiReviewResult ParseVerdict(JsonElement inputJson)
    {
        var verdictText = inputJson.GetProperty("verdict").GetString() ?? "";
        var reasoning = inputJson.TryGetProperty("reasoning", out var reasoningEl) ? reasoningEl.GetString() ?? "" : "";

        // Anything other than an exact "approve" — an unrecognized string, a typo, a refusal to
        // choose — resolves to Flag, not Approve: the whole point of this rubric is "when in doubt,
        // don't ship it unattended," so a malformed verdict itself is treated as doubt, never as a
        // silent pass.
        var verdict = string.Equals(verdictText, "approve", StringComparison.OrdinalIgnoreCase)
            ? AiReviewVerdict.Approve
            : AiReviewVerdict.Flag;

        return new AiReviewResult(verdict, reasoning);
    }
}
