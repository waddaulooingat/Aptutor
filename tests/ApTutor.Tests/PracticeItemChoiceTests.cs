using System.Text.Json;
using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// PracticeItemChoice is the wire shape every practice-item answer option is written in (see the
// graph-spec-rendering plan) — plain text (every choice ever approved before this schema existed)
// or a static line/point graph. PracticeItemChoiceConverter is the one piece of genuinely new,
// non-trivial logic here: it must keep parsing every already-approved choice (a bare JSON string)
// exactly as before, with no migration step, while also handling the new object shape.
public class PracticeItemChoiceTests
{
    [Fact]
    public void Deserialize_OldBareStringShape_BecomesATextOnlyChoice()
    {
        var choice = JsonSerializer.Deserialize<PracticeItemChoice>("\"int x = 5;\"");

        Assert.NotNull(choice);
        Assert.Equal("int x = 5;", choice!.Text);
        Assert.Null(choice.Graph);
        Assert.False(choice.IsGraph);
    }

    [Fact]
    public void Serialize_ThenDeserialize_TextChoice_RoundTripsExactly()
    {
        var original = PracticeItemChoice.OfText("The JVM interprets bytecode.");

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<PracticeItemChoice>(json);

        Assert.Equal(original, roundTripped);
        Assert.False(roundTripped!.IsGraph);
    }

    [Fact]
    public void Serialize_AlwaysWritesTheCurrentObjectShape_NotTheLegacyBareString()
    {
        var json = JsonSerializer.Serialize(PracticeItemChoice.OfText("hello"));

        Assert.StartsWith("{", json);
        Assert.Contains("\"Text\":\"hello\"", json);
    }

    [Fact]
    public void Serialize_ThenDeserialize_GraphChoice_RoundTripsTheFullGraphSpec()
    {
        var graph = new GraphSpec(
            XAxis: new GraphAxis("Time", "s"),
            YAxis: new GraphAxis("Velocity", "m/s", Min: 0, Max: 20),
            Segments: new[]
            {
                new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(5, 10) }, Solid: true),
                new GraphSegment(new[] { new GraphPoint(5, 10), new GraphPoint(10, 10) }, Solid: false, Label: "constant velocity"),
            },
            ReferenceValues: new[] { new GraphReferenceValue(GraphAxisKind.Y, 10, "v_t") });
        var original = PracticeItemChoice.OfGraph(graph);

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<PracticeItemChoice>(json);

        Assert.NotNull(roundTripped);
        Assert.True(roundTripped!.IsGraph);
        Assert.Null(roundTripped.Text);

        var g = roundTripped.Graph!;
        Assert.Equal("Time", g.XAxis.Label);
        Assert.Equal("s", g.XAxis.Unit);
        Assert.Equal("Velocity", g.YAxis.Label);
        Assert.Equal(0, g.YAxis.Min);
        Assert.Equal(20, g.YAxis.Max);
        Assert.Equal(2, g.Segments.Count);
        Assert.True(g.Segments[0].Solid);
        Assert.False(g.Segments[1].Solid);
        Assert.Equal("constant velocity", g.Segments[1].Label);
        Assert.Equal(2, g.Segments[0].Points.Count);
        Assert.Equal(10, g.Segments[0].Points[1].Y);
        Assert.NotNull(g.ReferenceValues);
        Assert.Equal(GraphAxisKind.Y, g.ReferenceValues![0].Axis);
        Assert.Equal("v_t", g.ReferenceValues[0].Label);
    }

    [Fact]
    public void GraphAxis_MinMaxUnit_AreOptional()
    {
        var axis = new GraphAxis("Position");

        Assert.Null(axis.Unit);
        Assert.Null(axis.Min);
        Assert.Null(axis.Max);
    }

    [Fact]
    public void GraphSpec_ReferenceValues_DefaultsToNull_NotAnEmptyList()
    {
        var graph = new GraphSpec(new GraphAxis("X"), new GraphAxis("Y"), Array.Empty<GraphSegment>());

        Assert.Null(graph.ReferenceValues);
    }
}
