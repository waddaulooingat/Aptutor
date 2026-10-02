// "explain it to me" experiment (explain-it-to-me-experiment branch only — see its own handoff) — a
// live, grounded conversational tutor for the node currently being viewed. Wraps
// ApTutor.ContentFactory.ClaudeClient's plain-conversational SendMessageAsync (see that file's own
// remarks on why this is the one place in the Shell it's used live rather than at build time). Keeps
// its own conversation history so a follow-up question has prior turns as context, the same way a
// real chat does — this class owns exactly that state and nothing else (no UI, no audio).

using ApTutor.ContentFactory;

namespace ApTutor.Client.Services;

public sealed class TutorChatService
{
    private readonly ClaudeClient _client;
    private readonly string _systemPrompt;
    private readonly List<(string Role, string Text)> _history = new();

    public TutorChatService(ClaudeClient client, string nodeTitle, string lessonContent)
    {
        _client = client;
        _systemPrompt = $"""
            You are a friendly, patient tutor helping a student understand one specific lesson, out
            loud in conversation — not writing a textbook passage. Explain clearly and
            conversationally, the way a good human tutor sitting next to the student would. Keep
            answers reasonably short (a few sentences to a short paragraph): this is a spoken
            back-and-forth, not a reading assignment, and the student can always ask for more detail.

            Ground everything in the lesson content below — this is specifically what the student is
            looking at right now, not a general question about the subject:

            Lesson: {nodeTitle}
            ---
            {lessonContent}
            ---
            """;
    }

    public IReadOnlyList<(string Role, string Text)> History => _history;

    /// The opening turn — no student question yet, just "explain this to me" implied by opening the
    /// conversation at all.
    public Task<string> ExplainAsync(CancellationToken ct = default) =>
        SendAsync("Explain this lesson to me.", ct);

    public Task<string> AskFollowUpAsync(string question, CancellationToken ct = default) =>
        SendAsync(question, ct);

    private async Task<string> SendAsync(string userText, CancellationToken ct)
    {
        _history.Add(("user", userText));
        try
        {
            var reply = await _client.SendMessageAsync(_systemPrompt, _history, ct);
            _history.Add(("assistant", reply));
            return reply;
        }
        catch
        {
            // Don't leave a dangling user turn with no reply in history — a retried question should
            // read as a fresh attempt, not a duplicate alongside the failed one.
            _history.RemoveAt(_history.Count - 1);
            throw;
        }
    }
}
