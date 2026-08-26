// One-off migration tool. Two jobs, both pushing pre-existing local data up into S3 through the
// exact same S3ContentStore paths Content Admin's own UI uses, so the wire format is guaranteed
// correct rather than hand-reconstructed:
//
//   1. Content generated/verified BEFORE the S3 migration (local content-pack files under
//      src/ApTutor.Client/content/<courseId>/*.json) -> S3ContentStore.ApproveNodeAsync.
//   2. Each course's baked-in DAG (apcsa-skill-dag.json / apwh-skill-dag.json, still shipped with
//      this build via ApTutor.Curriculum's transitive Content items) -> ApproveStructureAsync, now
//      that course structure is itself S3-delivered content (see the Shell-display-only/
//      course-authoring plan). Without this, Content Admin's Index page has nothing to render for
//      either course even though their node content is already live in S3.
//
// Needs the WRITE-scoped AWS credential (the one Content Admin itself uses), NOT the Shell's
// read-only one — this calls PutObject. Set TUTORAI_CONTENT_BUCKET / TUTORAI_CONTENT_REGION plus
// AWS_ACCESS_KEY_ID / AWS_SECRET_ACCESS_KEY (or rely on `aws configure`'s saved credentials) before
// running. Only ever uploads content packs already marked Verified: true — unreviewed drafts are
// skipped, never pushed. Structure backfill is idempotent (content-addressed — re-running it after
// a real DAG regeneration in Content Admin would just be a no-op against the same hash, never
// clobbering newer approved structure with this stale local copy... though in practice you should
// only ever need to run this once, before any course has an S3-approved structure yet).
//
// Run once: dotnet run --project tools/ApTutor.BackfillS3/ApTutor.BackfillS3.csproj

using Amazon;
using Amazon.S3;
using ApTutor.Content;
using ApTutor.ContentAdmin.Services;
using ApTutor.Curriculum;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var bucket = Environment.GetEnvironmentVariable("TUTORAI_CONTENT_BUCKET");
var region = Environment.GetEnvironmentVariable("TUTORAI_CONTENT_REGION");
if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(region))
{
    Console.Error.WriteLine("Set TUTORAI_CONTENT_BUCKET and TUTORAI_CONTENT_REGION first.");
    return 1;
}

// courseId -> (local content directory, local DAG file name). Content dir matches
// CsaCourseModule/WorldHistoryCourseModule's own defaults; DAG file name matches what
// ApTutor.Curriculum ships — run this from the repo root.
var courses = new Dictionary<string, (string ContentDir, string DagFileName)>
{
    ["csa"] = (Path.Combine("src", "ApTutor.Client", "content", "csa"), "apcsa-skill-dag.json"),
    ["worldhistory"] = (Path.Combine("src", "ApTutor.Client", "content", "worldhistory"), "apwh-skill-dag.json"),
};

using var s3 = new AmazonS3Client(new AmazonS3Config { RegionEndpoint = RegionEndpoint.GetBySystemName(region) });
var store = new S3ContentStore(
    s3, Options.Create(new S3ContentStoreOptions { Bucket = bucket, Region = region }), NullLogger<S3ContentStore>.Instance);

var uploaded = 0;
var skipped = 0;
foreach (var (courseId, (dir, dagFileName)) in courses)
{
    var dagPath = Path.Combine(AppContext.BaseDirectory, dagFileName);
    if (File.Exists(dagPath))
    {
        var graph = SkillDagLoader.Load(dagPath); // validates the DAG the same way Content Admin/the Shell would
        await store.ApproveStructureAsync(courseId, graph.Dag);
        Console.WriteLine($"[{courseId}] uploaded structure ({graph.Dag.Nodes.Count} nodes, {graph.Dag.Units.Count} units).");
    }
    else
    {
        Console.WriteLine($"[{courseId}] no local DAG file found at '{dagPath}' — skipping structure backfill.");
    }

    if (!Directory.Exists(dir))
    {
        Console.WriteLine($"[{courseId}] no local content directory at '{dir}' — skipping content backfill.");
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

Console.WriteLine($"\nDone. Uploaded {uploaded} node pack(s), skipped {skipped} (unverified).");
return 0;
