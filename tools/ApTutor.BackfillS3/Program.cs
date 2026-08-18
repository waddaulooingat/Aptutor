// One-off migration tool: pushes content that was generated/verified BEFORE the S3 migration
// (local content-pack files under src/ApTutor.Client/content/<courseId>/*.json — generated via the
// old ApTutor.ContentFactory CLI or the git-era Content Admin) up into the bucket. Goes through the
// exact same S3ContentStore.ApproveNodeAsync path Content Admin's own Approve button uses, so the
// node/manifest wire format is guaranteed correct rather than hand-reconstructed.
//
// Needs the WRITE-scoped AWS credential (the one Content Admin itself uses), NOT the Shell's
// read-only one — this calls PutObject. Set TUTORAI_CONTENT_BUCKET / TUTORAI_CONTENT_REGION plus
// AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY (or rely on `aws configure`'s saved credentials) before
// running. Only ever uploads packs already marked Verified: true — unreviewed drafts are skipped,
// never pushed.
//
// Run once: dotnet run --project tools/ApTutor.BackfillS3/ApTutor.BackfillS3.csproj

using Amazon;
using Amazon.S3;
using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var bucket = Environment.GetEnvironmentVariable("TUTORAI_CONTENT_BUCKET");
var region = Environment.GetEnvironmentVariable("TUTORAI_CONTENT_REGION");
if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(region))
{
    Console.Error.WriteLine("Set TUTORAI_CONTENT_BUCKET and TUTORAI_CONTENT_REGION first.");
    return 1;
}

// courseId -> local content directory. Matches CsaCourseModule/WorldHistoryCourseModule's own
// defaults — run this from the repo root.
var courses = new Dictionary<string, string>
{
    ["csa"] = Path.Combine("src", "ApTutor.Client", "content", "csa"),
    ["worldhistory"] = Path.Combine("src", "ApTutor.Client", "content", "worldhistory"),
};

using var s3 = new AmazonS3Client(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });
var store = new S3ContentStore(
    s3, Options.Create(new S3ContentStoreOptions { Bucket = bucket, Region = region }), NullLogger<S3ContentStore>.Instance);

var uploaded = 0;
var skipped = 0;
foreach (var (courseId, dir) in courses)
{
    if (!Directory.Exists(dir))
    {
        Console.WriteLine($"[{courseId}] no local content directory at '{dir}' — skipping.");
        continue;
    }

    foreach (var pack in ContentPackStore.LoadAll(dir))
    {
        if (!pack.Verified)
        {
            Console.WriteLine($"[{courseId}] skipping '{pack.NodeId}' — not verified.");
            skipped++;
            continue;
        }

        await store.ApproveNodeAsync(courseId, pack.NodeId, pack);
        Console.WriteLine($"[{courseId}] uploaded '{pack.NodeId}'.");
        uploaded++;
    }
}

Console.WriteLine($"\nDone. Uploaded {uploaded}, skipped {skipped} (unverified).");
return 0;
