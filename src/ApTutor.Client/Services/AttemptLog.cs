using System.Text.Json;
using ApTutor.Content;

namespace ApTutor.Client.Services;

/// One answered question, ever — the Shell's local, per-installation student-performance record
/// (see the difficulty-and-progress-tracking plan's Part B). Never synced to S3, never visible to
/// Content Admin or any SME; a deliberate, narrow exception to "the Shell is display-only" — that
/// principle governs course/content AUTHORING, not a student's own local performance history.
///
/// PackHash identifies exactly which approved set the question came from — necessary because
/// PracticeItem.Id (e.g. "u1.2-q1") is only unique WITHIN one generated set, not across a node's
/// whole multi-set library (see the multi-set library plan): two independently-approved sets at the
/// same difficulty each restart their own "-q1", "-q2", ... numbering. Without the hash, "replay the
/// exact missed question" (see AttemptAnalysis) could replay a completely different question that
/// happens to share the same id.
public sealed record AttemptRecord(
    DateTimeOffset Timestamp,
    string CourseId,
    string NodeId,
    Difficulty Difficulty,
    string PackHash,
    string QuestionId,
    bool Correct);

/// Append-only JSON Lines log — one line per answered question, never rewritten in place. A plain
/// per-write rewrite (AppSettingsStore's atomic-replace pattern) doesn't fit here: this grows for as
/// long as the student uses the app, and rewriting the whole file on every single answer would be
/// needless work for what's still just a single-process desktop app with no concurrent writers.
/// Malformed trailing lines (a write cut short by a crash) are skipped on read rather than taking
/// down the whole log, same posture as ContentPackStore's malformed-file handling.
public static class AttemptLogStore
{
    public static string DefaultDir => AppSettingsStore.DefaultDir;

    private static string PathFor(string dir) => Path.Combine(dir, "attempts.jsonl");

    public static void Append(string dir, AttemptRecord record)
    {
        Directory.CreateDirectory(dir);
        File.AppendAllText(PathFor(dir), JsonSerializer.Serialize(record) + Environment.NewLine);
    }

    public static IReadOnlyList<AttemptRecord> LoadAll(string dir)
    {
        var path = PathFor(dir);
        if (!File.Exists(path)) return Array.Empty<AttemptRecord>();

        var records = new List<AttemptRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<AttemptRecord>(line) is { } record)
                    records.Add(record);
            }
            catch (JsonException)
            {
                // A single truncated/corrupt line (e.g. a write cut short by a crash) shouldn't
                // discard every other attempt ever recorded.
            }
        }
        return records;
    }
}
