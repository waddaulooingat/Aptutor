using System.Text.Json;
using ApTutor.Client.Services;
using ApTutor.Content;
using ApTutor.Curriculum;
using Xunit;

namespace ApTutor.Tests;

// Exercises CourseDiscoveryService.DiscoverCoursesAsync's real discovery/skip logic end to end via
// a fake that overrides just the three IAmazonS3-touching leaf methods — same seam pattern as
// ContentSyncService/S3ContentStore.
public class CourseDiscoveryServiceTests
{
    private static string SampleDagJson(string course, string? displayName = null) => $$"""
        {
          "meta": { "course": "{{course}}", "nodeCount": 1{{(displayName is null ? "" : $$""", "displayName": "{{displayName}}" """)}} },
          "scenePrimitives": {},
          "units": [ { "unit": 1, "title": "Unit 1" } ],
          "nodes": [ { "id": "u1.1", "unit": 1, "type": "concept", "title": "Intro", "prereqs": [], "viz": "" } ]
        }
        """;

    [Fact]
    public async Task DiscoverCoursesAsync_NoCoursesInTopLevelIndex_ReturnsEmpty()
    {
        var fake = new FakeCourseDiscoveryService(new Dictionary<string, (string? Hash, string? Json)>());

        var discovered = await fake.DiscoverCoursesAsync();

        Assert.Empty(discovered);
    }

    [Fact]
    public async Task DiscoverCoursesAsync_CourseWithApprovedStructure_IsIncluded()
    {
        var fake = new FakeCourseDiscoveryService(new()
        {
            ["csa"] = ("hash-1", SampleDagJson("Computer Science A")),
        });

        var discovered = await fake.DiscoverCoursesAsync();

        var course = Assert.Single(discovered);
        Assert.Equal("csa", course.CourseId);
        Assert.True(course.Graph.Exists("u1.1"));
    }

    [Fact]
    public async Task DiscoverCoursesAsync_CourseInIndexButNoStructureHashYet_IsSkipped()
    {
        var fake = new FakeCourseDiscoveryService(new()
        {
            ["newcourse"] = (null, null), // in the top-level index, but StructureHash is null
        });

        var discovered = await fake.DiscoverCoursesAsync();

        Assert.Empty(discovered);
    }

    [Fact]
    public async Task DiscoverCoursesAsync_MalformedStructure_IsSkipped_DoesNotThrow_OtherCoursesStillReturned()
    {
        var fake = new FakeCourseDiscoveryService(new()
        {
            ["broken"] = ("hash-bad", "{ not valid json"),
            ["csa"] = ("hash-1", SampleDagJson("Computer Science A")),
        });

        var discovered = await fake.DiscoverCoursesAsync();

        var course = Assert.Single(discovered);
        Assert.Equal("csa", course.CourseId);
    }

    [Fact]
    public async Task DiscoverCoursesAsync_MultipleCourses_AllIncluded()
    {
        var fake = new FakeCourseDiscoveryService(new()
        {
            ["csa"] = ("hash-1", SampleDagJson("Computer Science A")),
            ["worldhistory"] = ("hash-2", SampleDagJson("AP World History: Modern", displayName: "World History")),
        });

        var discovered = await fake.DiscoverCoursesAsync();

        Assert.Equal(2, discovered.Count);
        Assert.Contains(discovered, c => c.CourseId == "csa");
        Assert.Contains(discovered, c => c.CourseId == "worldhistory");
    }

    /// Overrides only the three leaf methods that actually touch IAmazonS3, so DiscoverCoursesAsync's
    /// real discovery/skip logic runs for real. Keyed by courseId -> (StructureHash, raw DAG JSON) —
    /// a null Hash simulates a course registered in the top-level index with no structure approved
    /// yet.
    private sealed class FakeCourseDiscoveryService : CourseDiscoveryService
    {
        private readonly Dictionary<string, (string? Hash, string? Json)> _courses;

        public FakeCourseDiscoveryService(Dictionary<string, (string? Hash, string? Json)> courses)
            : base(null!, "test-bucket") => _courses = courses;

        protected override Task<IReadOnlyList<string>> GetCourseIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(_courses.Keys.ToList());

        protected override Task<string?> GetStructureHashAsync(string courseId, CancellationToken ct) =>
            Task.FromResult(_courses.GetValueOrDefault(courseId).Hash);

        protected override Task<SkillGraph?> GetStructureAsync(string courseId, string hash, CancellationToken ct)
        {
            var json = _courses.GetValueOrDefault(courseId).Json;
            if (json is null) return Task.FromResult<SkillGraph?>(null);

            try
            {
                return Task.FromResult<SkillGraph?>(SkillDagLoader.LoadFromJson(json));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                return Task.FromResult<SkillGraph?>(null);
            }
        }
    }
}
