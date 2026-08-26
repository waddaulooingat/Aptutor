using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using ApTutor.Content;
using ApTutor.Curriculum;
using Microsoft.Extensions.Options;

namespace ApTutor.ContentAdmin.Services;

public sealed class S3ContentStoreOptions
{
    public string Bucket { get; set; } = "";
    public string Region { get; set; } = "";
}

/// S3 is the source of truth for approved course content — git is source code only (see the plan).
/// Layout:
///   manifest.json                                    — top-level index of courses
///   courses/&lt;courseId&gt;/manifest.json                  — per-course node hash/version index, plus
///                                                        the course's current structure pointer
///   courses/&lt;courseId&gt;/nodes/&lt;nodeId&gt;/&lt;hash&gt;.json      — one IMMUTABLE object per approved version
///   courses/&lt;courseId&gt;/structure/&lt;hash&gt;.json           — one IMMUTABLE object per approved DAG
///                                                        revision (units/nodes/prereqs — see
///                                                        ApTutor.Curriculum.SkillDag)
///   courses/&lt;courseId&gt;/rejections/&lt;nodeId&gt;-&lt;ticks&gt;.json — one object per rejection event
///
/// Content-addressed by design: a node's key is derived from its own content hash, so re-approving
/// unchanged content writes the same key with the same bytes (a harmless idempotent no-op) and
/// approving changed content always lands on a brand-new key, leaving the previous version's object
/// untouched rather than overwriting it. This is what makes the Shell's Refresh a correct, cheap
/// diff — it downloads exactly the versions whose hash changed, nothing more. (An earlier version of
/// this store wrote to a fixed, overwritten-in-place path; that path is no longer written to for new
/// approvals — see the plan's Part A for the migration.)
///
/// Unverified drafts never touch S3 at all (see ContentGenerationService) — only Approve writes
/// here. Both manifests use conditional writes (ETag IfMatch, bounded retry on 412; IfNoneMatch "*"
/// for a course's first-ever write) since they're read-merge-write and more than one writer is
/// possible.
public class S3ContentStore
{
    // Bumped from 1 -> 2 when NodeManifestEntry moved from a single hash to a list of versions —
    // informational only (nothing branches on this value), the actual backward-compat handling for
    // manifests still on the old shape lives in NodeManifestEntryConverter.
    private const int SchemaVersion = 2;
    private const int MaxManifestWriteAttempts = 5;

    // ApTutor.Content.ContentHash.CanonicalOptions, not a locally-defined instance — this app's
    // hash and ApTutor.Client's independently-recomputed hash of the same pack must be byte-for-byte
    // identical, which is only guaranteed if both sides serialize through the one shared options
    // instance rather than two separately-constructed (if similar-looking) ones.
    private static readonly JsonSerializerOptions CanonicalJsonOptions = ContentHash.CanonicalOptions;

    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ILogger<S3ContentStore> _logger;

    public S3ContentStore(IAmazonS3 s3, IOptions<S3ContentStoreOptions> options, ILogger<S3ContentStore> logger)
    {
        _s3 = s3;
        _bucket = string.IsNullOrWhiteSpace(options.Value.Bucket)
            ? throw new InvalidOperationException("ContentStore:Bucket is not configured.")
            : options.Value.Bucket;
        _logger = logger;
    }

    /// Called once at startup. Without s3:ListBucket, S3 returns 403 (not 404) for a GetObject on a
    /// missing key — indistinguishable from "credentials are broken" unless something disambiguates
    /// it deliberately, once, loudly, at boot. A GET of the top-level manifest either succeeds,
    /// 404s (bucket reachable, nothing approved anywhere yet — fine), or throws for any other reason
    /// (not fine — surfaces here instead of on the first SME's first click).
    public async Task ValidateConnectivityAsync(CancellationToken ct = default)
    {
        var (manifest, _) = await TryGetObjectAsync<TopLevelManifest>("manifest.json", ct);
        _logger.LogInformation(
            "S3 content store reachable (bucket '{Bucket}'). Top-level manifest {State}.",
            _bucket, manifest is null ? "does not exist yet" : $"has {manifest.Courses.Count} course(s)");
    }

    public async Task<CourseManifest> GetCourseManifestAsync(string courseId, CancellationToken ct = default)
    {
        var (manifest, _) = await TryGetObjectAsync<CourseManifest>(CourseManifestKey(courseId), ct);
        return manifest ?? CourseManifest.Empty(SchemaVersion);
    }

    /// The bucket-wide index of every course that's ever had anything approved — see CourseCatalog,
    /// which uses this to discover the course list from S3 instead of a static appsettings.json map.
    public async Task<TopLevelManifest> GetTopLevelManifestAsync(CancellationToken ct = default)
    {
        var (manifest, _) = await TryGetObjectAsync<TopLevelManifest>("manifest.json", ct);
        return manifest ?? TopLevelManifest.Empty(SchemaVersion);
    }

    /// Resolves the most recently-approved version from the course manifest and fetches that exact
    /// versioned object — a node can have several independently-approved versions now (see the
    /// multi-set library plan), this always returns the newest one, e.g. for Content Admin's Review
    /// page to show as context. The Shell doesn't use this — it downloads and rotates among every
    /// version itself (see ApTutor.Client.Services.ContentSyncService).
    public async Task<NodeContentPack?> TryGetLiveNodeAsync(string courseId, string nodeId, CancellationToken ct = default)
    {
        var manifest = await GetCourseManifestAsync(courseId, ct);
        if (!manifest.Nodes.TryGetValue(nodeId, out var entry)) return null;

        var (pack, _) = await TryGetObjectAsync<NodeContentPack>(NodeKey(courseId, nodeId, entry.Latest.Hash), ct);
        return pack;
    }

    /// Fetches the course's current curriculum structure (units/nodes/prereqs) — see the
    /// Shell-display-only/course-authoring plan. Null if no structure has ever been approved for
    /// this course yet (a brand-new course with a unit list but no DAG filled in, or a course
    /// nobody has migrated onto S3-delivered structure at all).
    public async Task<SkillDag?> TryGetLiveStructureAsync(string courseId, CancellationToken ct = default)
    {
        var manifest = await GetCourseManifestAsync(courseId, ct);
        if (manifest.StructureHash is not { } hash) return null;

        var (dag, _) = await TryGetObjectAsync<SkillDag>(StructureKey(courseId, hash), ct);
        return dag;
    }

    /// Same content-addressing shape as ApproveNodeAsync: the structure object itself is immutable
    /// and never overwritten (a changed DAG always lands on a new hash-named key), but unlike node
    /// content, the manifest's structure pointer is REPLACED, not appended to — see
    /// ManifestMerge.SetStructure's remarks for why a course only ever has one live curriculum.
    public async Task ApproveStructureAsync(string courseId, SkillDag structure, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(structure, CanonicalJsonOptions);
        var hash = ContentHash.Compute(structure);
        var now = DateTimeOffset.UtcNow;

        await PutObjectAsync(StructureKey(courseId, hash), body, ifMatch: null, ifNoneMatch: null, ct);

        var courseManifest = await UpdateWithRetryAsync<CourseManifest>(
            CourseManifestKey(courseId),
            current => ManifestMerge.SetStructure(current ?? CourseManifest.Empty(SchemaVersion), hash, now),
            ct);
        await ReindexCourseInTopLevelManifestAsync(courseId, courseManifest, now, ct);
    }

    public async Task ApproveNodeAsync(string courseId, string nodeId, NodeContentPack approvedPack, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(approvedPack, CanonicalJsonOptions);
        var hash = ContentHash.Compute(approvedPack);
        var now = DateTimeOffset.UtcNow;

        // Content-addressed key: writing the same hash's content to the same key twice is an
        // idempotent no-op (identical bytes); a changed pack always lands on a new key, so this
        // never needs a conditional write the way the manifest updates below do.
        await PutObjectAsync(NodeKey(courseId, nodeId, hash), body, ifMatch: null, ifNoneMatch: null, ct);

        var courseManifest = await UpdateWithRetryAsync<CourseManifest>(
            CourseManifestKey(courseId),
            current => ManifestMerge.UpsertNode(current ?? CourseManifest.Empty(SchemaVersion), nodeId, hash, now),
            ct);
        await ReindexCourseInTopLevelManifestAsync(courseId, courseManifest, now, ct);
    }

    /// Shared tail of ApproveNodeAsync/ApproveStructureAsync — both end by pointing the top-level
    /// course index at this course's just-updated manifest hash, so the Shell/Content Admin can
    /// discover which courses exist (and whether any of their manifests changed) from one object
    /// without listing every course's manifest individually.
    private async Task ReindexCourseInTopLevelManifestAsync(string courseId, CourseManifest courseManifest, DateTimeOffset now, CancellationToken ct)
    {
        var courseManifestHash = ComputeHash(JsonSerializer.Serialize(courseManifest, CanonicalJsonOptions));
        await UpdateWithRetryAsync<TopLevelManifest>(
            "manifest.json",
            current => ManifestMerge.UpsertCourse(current ?? TopLevelManifest.Empty(SchemaVersion), courseId, new CourseIndexEntry(courseManifestHash, now)),
            ct);
    }

    private async Task<T> UpdateWithRetryAsync<T>(string key, Func<T?, T> merge, CancellationToken ct) where T : class
    {
        for (var attempt = 1; attempt <= MaxManifestWriteAttempts; attempt++)
        {
            var (current, etag) = await TryGetObjectAsync<T>(key, ct);
            var updated = merge(current);
            var body = JsonSerializer.Serialize(updated, CanonicalJsonOptions);

            try
            {
                await PutObjectAsync(key, body, ifMatch: etag, ifNoneMatch: etag is null ? "*" : null, ct);
                return updated;
            }
            catch (AmazonS3Exception ex) when (IsPreconditionFailed(ex))
            {
                // Deliberately catches on the LAST attempt too — falling through to the loop's
                // natural exit (and the InvalidOperationException below) rather than letting the
                // raw AmazonS3Exception escape here, which would leak an S3-specific exception type
                // out of what's supposed to be this store's one, deliberate failure mode.
                _logger.LogWarning(
                    "Conditional write to '{Key}' lost a race (attempt {Attempt}/{Max}); re-reading and retrying.",
                    key, attempt, MaxManifestWriteAttempts);
            }
        }

        throw new InvalidOperationException($"Could not update '{key}' after {MaxManifestWriteAttempts} attempts — too much write contention.");
    }

    // These two are the only methods that actually touch IAmazonS3 — kept as a narrow protected
    // seam (rather than hand-stubbing the entire IAmazonS3 interface, which has a huge surface) so
    // tests can exercise the real conditional-write retry loop above (UpdateWithRetryAsync) end to
    // end by overriding just these two leaf operations, without needing a full S3 double.
    protected virtual async Task<(T? Value, string? ETag)> TryGetObjectAsync<T>(string key, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
            using var reader = new StreamReader(response.ResponseStream);
            var body = await reader.ReadToEndAsync(ct);
            return (JsonSerializer.Deserialize<T>(body, CanonicalJsonOptions), response.ETag);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return (null, null);
        }
    }

    protected virtual async Task PutObjectAsync(string key, string body, string? ifMatch, string? ifNoneMatch, CancellationToken ct)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            ContentBody = body,
            ContentType = "application/json",
        };
        if (ifMatch is not null) request.IfMatch = ifMatch;
        if (ifNoneMatch is not null) request.IfNoneMatch = ifNoneMatch;

        await _s3.PutObjectAsync(request, ct);
    }

    // A missing key surfaces as 404 (NoSuchKey/NotFound) when the caller has s3:ListBucket on the
    // relevant prefix — which the IAM policy this app ships with grants specifically so this check
    // is unambiguous. Without that permission S3 would return 403 for a missing key too, which is
    // exactly the ambiguity ValidateConnectivityAsync exists to catch at startup instead of here.
    private static bool IsNotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

    private static bool IsPreconditionFailed(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.PreconditionFailed || string.Equals(ex.ErrorCode, "PreconditionFailed", StringComparison.Ordinal);

    // Only for hashing the course manifest itself (for the top-level index's CourseIndexEntry) —
    // a different concern from ContentHash.Compute, which is specifically for NodeContentPack and
    // shared with ApTutor.Client so both sides agree on a node's hash. Nothing outside this class
    // needs to recompute a manifest's own hash, so no need to share this one.
    private static string ComputeHash(string content) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static string CourseManifestKey(string courseId) => $"courses/{courseId}/manifest.json";
    private static string NodeKey(string courseId, string nodeId, string hash) => $"courses/{courseId}/nodes/{nodeId}/{hash}.json";
    private static string StructureKey(string courseId, string hash) => $"courses/{courseId}/structure/{hash}.json";
}
