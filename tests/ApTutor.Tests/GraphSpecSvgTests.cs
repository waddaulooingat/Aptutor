using ApTutor.ContentAdmin.Services;
using ApTutor.Platform;
using Xunit;

namespace ApTutor.Tests;

// Content Admin's half of the graph-spec-rendering plan's Part 4 (see GraphSpecSvg's remarks). These
// tests check the emitted markup shape and, critically, that model-generated label text is
// HTML-encoded before being embedded — this content comes from Claude's generation, not a trusted
// human author.
public class GraphSpecSvgTests
{
    private static GraphSpec Spec(string? segmentLabel = null, IReadOnlyList<GraphReferenceValue>? referenceValues = null) =>
        new(
            new GraphAxis("Time", "s"),
            new GraphAxis("Velocity", "m/s"),
            new[] { new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(10, 20) }, Solid: true, Label: segmentLabel) },
            referenceValues);

    [Fact]
    public void Render_ProducesAnSvgElementWithAPolylineForTheSegment()
    {
        var svg = GraphSpecSvg.Render(Spec());

        Assert.Contains("<svg", svg);
        Assert.Contains("</svg>", svg);
        Assert.Contains("<polyline", svg);
    }

    [Fact]
    public void Render_DashedSegment_IncludesStrokeDasharray()
    {
        var spec = Spec() with { Segments = new[] { new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(10, 20) }, Solid: false) } };

        var svg = GraphSpecSvg.Render(spec);

        Assert.Contains("stroke-dasharray", svg);
    }

    [Fact]
    public void Render_SolidSegment_HasNoStrokeDasharrayOnThePolyline()
    {
        var svg = GraphSpecSvg.Render(Spec());

        // The polyline element itself shouldn't carry a dasharray — only a reference line (if any)
        // would, and this spec has none.
        Assert.DoesNotContain("stroke-dasharray", svg);
    }

    [Fact]
    public void Render_ReferenceValue_AddsALineAndALabel()
    {
        var spec = Spec(referenceValues: new[] { new GraphReferenceValue(GraphAxisKind.Y, 0, "v = 0") });

        var svg = GraphSpecSvg.Render(spec);

        Assert.Contains("<line", svg);
        Assert.Contains("v = 0", svg);
    }

    [Fact]
    public void Render_AxisLabelsAppearWithUnits()
    {
        var svg = GraphSpecSvg.Render(Spec());

        Assert.Contains("Time (s)", svg);
        Assert.Contains("Velocity (m/s)", svg);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;")]
    [InlineData("A & B", "A &amp; B")]
    public void Render_SegmentLabelWithHtmlSpecialCharacters_IsEncoded_NeverInjectsRawMarkup(string label, string expectedEncodedFragment)
    {
        var svg = GraphSpecSvg.Render(Spec(segmentLabel: label));

        Assert.Contains(expectedEncodedFragment, svg);
        Assert.DoesNotContain("<script>", svg);
    }

    [Fact]
    public void Render_AxisLabelWithHtmlSpecialCharacters_IsEncoded()
    {
        var spec = Spec() with { XAxis = new GraphAxis("<b>Time</b>", "s") };

        var svg = GraphSpecSvg.Render(spec);

        Assert.DoesNotContain("<b>Time</b>", svg);
        Assert.Contains("&lt;b&gt;Time&lt;/b&gt;", svg);
    }

    [Fact]
    public void Render_UsesInvariantCultureForNumbers_NoCommaDecimalSeparators()
    {
        // Segment points with fractional coordinates would render with a comma decimal separator
        // under some cultures (e.g. de-DE) if number formatting weren't pinned to invariant culture —
        // that would corrupt the SVG's "x,y x,y" point-list syntax, which uses commas as a delimiter.
        var spec = Spec() with { Segments = new[] { new GraphSegment(new[] { new GraphPoint(0, 0), new GraphPoint(7.5, 12.25) }) } };
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            var svg = GraphSpecSvg.Render(spec);
            Assert.DoesNotContain("7,5", svg);
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
