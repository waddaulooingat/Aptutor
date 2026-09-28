using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// Pure geometry tests for the graph-spec-rendering plan's shared scaling logic (see GraphGeometry's
// remarks) — no Avalonia/Razor dependency, exercised independently of either renderer that consumes it.
public class GraphGeometryTests
{
    private static GraphSpec Spec(GraphAxis? xAxis = null, GraphAxis? yAxis = null, IReadOnlyList<GraphSegment>? segments = null, IReadOnlyList<GraphReferenceValue>? referenceValues = null) =>
        new(
            xAxis ?? new GraphAxis("Time", "s"),
            yAxis ?? new GraphAxis("Velocity", "m/s"),
            segments ?? new[] { new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(10, 20) }) },
            referenceValues);

    [Fact]
    public void ComputeBounds_ExplicitMinAndMax_UsedExactly_NoPadding()
    {
        var spec = Spec(xAxis: new GraphAxis("Time", "s", Min: 0, Max: 10));

        var bounds = GraphGeometry.ComputeBounds(spec);

        Assert.Equal(0, bounds.XMin);
        Assert.Equal(10, bounds.XMax);
    }

    [Fact]
    public void ComputeBounds_NoExplicitBounds_AutoScalesFromSegmentPoints_WithPadding()
    {
        var spec = Spec(segments: new[] { new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(10, 0) }) });

        var bounds = GraphGeometry.ComputeBounds(spec);

        // Padded outward from the raw data range [0,10], not exactly [0,10].
        Assert.True(bounds.XMin < 0);
        Assert.True(bounds.XMax > 10);
    }

    [Fact]
    public void ComputeBounds_OneBoundExplicitOtherAuto_RespectsExplicitExactly()
    {
        var spec = Spec(
            xAxis: new GraphAxis("Time", "s", Min: -5),
            segments: new[] { new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(10, 0) }) });

        var bounds = GraphGeometry.ComputeBounds(spec);

        Assert.Equal(-5, bounds.XMin);
        Assert.True(bounds.XMax > 10); // the auto side still gets padded
    }

    [Fact]
    public void ComputeBounds_ReferenceValueOutsideSegmentRange_ExpandsBoundsToFitIt()
    {
        var spec = Spec(
            segments: new[] { new GraphSegment(new[] { new GraphPoint(0, 5), new GraphPoint(10, 8) }) },
            referenceValues: new[] { new GraphReferenceValue(GraphAxisKind.Y, 0, "v = 0") });

        var bounds = GraphGeometry.ComputeBounds(spec);

        Assert.True(bounds.YMin <= 0);
    }

    [Fact]
    public void ComputeBounds_AllPointsIdenticalOnAnAxis_DoesNotProduceZeroWidthBounds()
    {
        var spec = Spec(segments: new[] { new GraphSegment(new[] { new GraphPoint(3, 3), new GraphPoint(3, 3) }) });

        var bounds = GraphGeometry.ComputeBounds(spec);

        Assert.True(bounds.XMax > bounds.XMin);
        Assert.True(bounds.YMax > bounds.YMin);
    }

    [Fact]
    public void ComputeBounds_NoPointsAndNoExplicitBounds_ReturnsHarmlessDefaultWindow()
    {
        var spec = Spec(segments: Array.Empty<GraphSegment>());

        var bounds = GraphGeometry.ComputeBounds(spec);

        Assert.True(bounds.XMax > bounds.XMin);
        Assert.True(bounds.YMax > bounds.YMin);
    }

    [Fact]
    public void NormalizeX_AtBounds_ReturnsZeroAndOne()
    {
        var bounds = new GraphBounds(XMin: 0, XMax: 10, YMin: 0, YMax: 1);

        Assert.Equal(0, bounds.NormalizeX(0));
        Assert.Equal(1, bounds.NormalizeX(10));
        Assert.Equal(0.5, bounds.NormalizeX(5));
    }

    [Fact]
    public void NormalizeY_ZeroWidthBounds_ReturnsMidpointRatherThanThrowing()
    {
        var bounds = new GraphBounds(XMin: 0, XMax: 1, YMin: 5, YMax: 5);

        Assert.Equal(0.5, bounds.NormalizeY(5));
    }

    [Fact]
    public void ComputeBounds_MultipleSegments_ConsidersPointsAcrossAllOfThem()
    {
        var spec = Spec(segments: new[]
        {
            new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(5, 5) }),
            new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(20, 2) }),
        });

        var bounds = GraphGeometry.ComputeBounds(spec);

        Assert.True(bounds.XMax > 20); // padded beyond the second segment's max, not just the first's
    }
}
