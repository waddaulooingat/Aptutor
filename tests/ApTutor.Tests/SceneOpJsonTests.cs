using System.Text.Json;
using ApTutor.Scene;
using Xunit;

namespace ApTutor.Tests;

// Phase 7: SceneOp needs to round-trip through JSON (content packs on disk, and later the content
// store over HTTP) as the exact same record types — not just an untyped blob. This exercises the
// [JsonPolymorphic]/[JsonDerivedType] "op" discriminator added to SceneState.cs.
public class SceneOpJsonTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IEnumerable<object[]> AllOpKinds()
    {
        yield return new object[] { new FramePush("main", "void foo()") };
        yield return new object[] { new FramePop("main") };
        yield return new object[] { new MemCellSet("main", "x", "int", "5") };
        yield return new object[] { new MemCellFlash("main", "x") };
        yield return new object[] { new HeapAlloc("1", "Point", new[] { new KeyValuePair<string, string>("x", "0") }) };
        yield return new object[] { new FieldSet("1", "x", "9") };
        yield return new object[] { new RefSet("main", "p", "1") };
        yield return new object[] { new RefSet("main", "p", null) };
        yield return new object[] { new LineHighlight(3) };
        yield return new object[] { new ArrayAlloc("a1", "int", new[] { "0", "0", "0" }) };
        yield return new object[] { new ArrayWrite("a1", 1, "99") };
        yield return new object[] { new Grid2dAlloc("g1", 2, 2, "int", "0") };
        yield return new object[] { new Grid2dWrite("g1", 1, 1, "7") };
        yield return new object[] { new CallTreeNode("c1", null, "int fib(int)") };
        yield return new object[] { new CallTreeReturn("c1", "3") };
        yield return new object[] { new ExprPush("e1", "n < 2") };
        yield return new object[] { new ExprResolve("e1", "true") };
        yield return new object[] { new BoolGlow("e1", true) };
    }

    [Theory]
    [MemberData(nameof(AllOpKinds))]
    public void SceneOp_RoundTripsThroughJson(SceneOp op)
    {
        var json = JsonSerializer.Serialize(op, Options);
        var back = JsonSerializer.Deserialize<SceneOp>(json, Options);

        Assert.IsType(op.GetType(), back);
        // Records with IReadOnlyList<T> properties (HeapAlloc.Fields, ArrayAlloc.InitialValues)
        // don't get structural list equality for free, so compare via re-serialization instead of
        // Assert.Equal(op, back) — two different List<T> instances with identical contents aren't
        // "equal" under default record equality, but they ARE the same content, which is what
        // round-tripping through JSON is actually supposed to preserve.
        Assert.Equal(json, JsonSerializer.Serialize(back, Options));
    }

    [Fact]
    public void SceneDelta_WithMixedOps_RoundTripsThroughJson()
    {
        var delta = new SceneDelta(new SceneOp[]
        {
            new FramePush("main", "void main()"),
            new MemCellSet("main", "x", "int", "5"),
            new HeapAlloc("1", "Point", Array.Empty<KeyValuePair<string, string>>()),
        });

        var json = JsonSerializer.Serialize(delta, Options);
        var back = JsonSerializer.Deserialize<SceneDelta>(json, Options)!;

        Assert.Equal(delta.Ops.Count, back.Ops.Count);
        Assert.Equal(json, JsonSerializer.Serialize(back, Options));
    }

    [Fact]
    public void SceneOp_SerializesWithOpDiscriminatorProperty()
    {
        var json = JsonSerializer.Serialize<SceneOp>(new LineHighlight(3), Options);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("lineHighlight", doc.RootElement.GetProperty("op").GetString());
    }
}
