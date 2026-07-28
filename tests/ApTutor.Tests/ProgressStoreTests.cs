using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// CLAUDE-HANDOFF §7: "Round-trip: persist progress, reload, mastery state matches."
public class ProgressStoreTests
{
    [Fact]
    public void SaveThenLoad_RoundTripsMasteredIds()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aptutor-progress-test-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ProgressStore(path);
            var saved = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal)
            {
                ["csa"] = new[] { "u1.1", "u1.2", "u2.1" },
            };

            store.Save(saved);
            var loaded = new ProgressStore(path).Load();

            Assert.True(loaded.ContainsKey("csa"));
            Assert.Equal(
                saved["csa"].OrderBy(x => x, StringComparer.Ordinal),
                loaded["csa"].OrderBy(x => x, StringComparer.Ordinal));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
