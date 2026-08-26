using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using ApTutor.Content;
using ApTutor.Curriculum;

namespace ApTutor.Client.Services;

public sealed record DiscoveredCourse(string CourseId, SkillGraph Graph);

/// Discovers every course that has an approved structure from S3 — course structure is itself
/// SME-approved, generator-produced material now (see the Shell-display-only/course-authoring
/// plan), not curriculum code baked into the build. A course registered in the top-level index but
/// with no structure.json yet (mid-authoring in Content Admin) is silently skipped, since there's
/// nothing to render for it. Same temporary read-only test-bridge posture as ContentSyncService (see
/// its remarks) — not the shipping design, no Licensing/entitlement service in front of this yet.
public class CourseDiscoveryService
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;

    public CourseDiscoveryService(IAmazonS3 s3, string bucket)
    {
        _s3 = s3;
        _bucket = bucket;
    }

    public async Task<IReadOnlyList<DiscoveredCourse>> DiscoverCoursesAsync(CancellationToken ct = default)
    {
        var courseIds = await GetCourseIdsAsync(ct);
        var discovered = new List<DiscoveredCourse>();

        foreach (var courseId in courseIds)
        {
            var structureHash = await GetStructureHashAsync(courseId, ct);
            if (structureHash is null) continue; // no approved structure yet — nothing to render

            var graph = await GetStructureAsync(courseId, structureHash, ct);
            if (graph is not null)
                discovered.Add(new DiscoveredCourse(courseId, graph));
        }

        return discovered;
    }

    // The only three methods that actually touch IAmazonS3 — protected virtual so tests can
    // exercise the real discovery/skip logic above via a subclass override, same seam pattern as
    // ContentSyncService/S3ContentStore.
    protected virtual async Task<IReadOnlyList<string>> GetCourseIdsAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = "manifest.json" }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var body = await reader.ReadToEndAsync(ct);
            var manifest = JsonSerializer.Deserialize<S3TopLevelManifest>(body, ContentHash.CanonicalOptions);
            return manifest is null ? Array.Empty<string>() : manifest.Courses.Keys.ToList();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<string>();
        }
    }

    protected virtual async Task<string?> GetStructureHashAsync(string courseId, CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = $"courses/{courseId}/manifest.json" }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var body = await reader.ReadToEndAsync(ct);
            var manifest = JsonSerializer.Deserialize<S3CourseManifestForDiscovery>(body, ContentHash.CanonicalOptions);
            return manifest?.StructureHash;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    protected virtual async Task<SkillGraph?> GetStructureAsync(string courseId, string hash, CancellationToken ct)
    {
        try
        {
            using var response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = _bucket, Key = $"courses/{courseId}/structure/{hash}.json" }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var body = await reader.ReadToEndAsync(ct);
            // Goes through SkillDagLoader (not a raw JsonSerializer.Deserialize<SkillDag>) so a
            // structure downloaded from S3 is validated (duplicate ids, unknown prereqs, cycles)
            // exactly the same way one loaded from a local file always has been.
            return SkillDagLoader.LoadFromJson(body);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            // A malformed/invalid structure for one course shouldn't take down discovery for every
            // other course — skip it, same reasoning as ContentPackStore.LoadAll skipping a single
            // corrupt local file rather than throwing.
            return null;
        }
    }

    // Local mirror of the wire shapes this needs — deliberately not a shared type/project reference
    // to ApTutor.ContentAdmin's whole manifest-merge machinery (see ContentSyncService's identical
    // remarks). Only the course-id keys and the structure pointer are needed here, nothing else.
    private sealed record S3TopLevelManifest(int SchemaVersion, Dictionary<string, JsonElement> Courses);
    private sealed record S3CourseManifestForDiscovery(int SchemaVersion, string? StructureHash);
}
