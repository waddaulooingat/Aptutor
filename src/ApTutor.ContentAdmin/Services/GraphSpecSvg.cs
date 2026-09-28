// Graph-spec-rendering plan, Part 4: Content Admin's SVG renderer for a GraphSpec answer choice on
// the Review page — built on the same GraphGeometry the Shell's Avalonia renderer uses (see
// ApTutor.Client.Services.GraphSpecRenderer, and GraphSpec's own remarks on why these are two
// separate, thin adapters rather than shared draw calls: a desktop app and a server-rendered web
// page can't share literal drawing code). Output is a self-contained inline SVG string with no
// external resources — meant to be embedded via @Html.Raw in Review.cshtml. Every piece of
// model-generated text (axis/segment/reference labels) is HTML-encoded before going into the
// markup, since this content originates from Claude's generation, not a trusted human author.

using System.Globalization;
using System.Net;
using System.Text;
using ApTutor.Platform;

namespace ApTutor.ContentAdmin.Services;

public static class GraphSpecSvg
{
    public static string Render(GraphSpec spec, double width = 260, double height = 170)
    {
        var bounds = GraphGeometry.ComputeBounds(spec);
        var svg = new StringBuilder();
        svg.Append(Inv($"<svg width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\" xmlns=\"http://www.w3.org/2000/svg\" style=\"background:#fff;border:1px solid #333;display:block;\">"));

        if (spec.ReferenceValues is { } references)
            foreach (var reference in references)
                AppendReferenceLine(svg, bounds, reference, width, height);

        foreach (var segment in spec.Segments)
            AppendSegment(svg, bounds, segment, width, height);

        // Plain numeric corner labels rather than full tick marks/gridlines — same scope as the
        // Shell's renderer (see its own remarks); enough to make the plot's scale legible for a
        // multiple-choice option without a general-purpose charting axis.
        AppendText(svg, 3, height - 4, FormatNumber(bounds.XMin), "#555", 9);
        AppendText(svg, width - 26, height - 4, FormatNumber(bounds.XMax), "#555", 9);
        AppendText(svg, 3, 11, FormatNumber(bounds.YMax), "#555", 9);
        AppendText(svg, 3, height - 17, FormatNumber(bounds.YMin), "#555", 9);

        svg.Append("</svg>");

        var xLabel = WebUtility.HtmlEncode(FormatAxisLabel(spec.XAxis));
        var yLabel = WebUtility.HtmlEncode(FormatAxisLabel(spec.YAxis));

        return $"""
            <div style="display:inline-flex; align-items:center; gap:4px; font-family:inherit;">
              <div style="writing-mode:vertical-rl; transform:rotate(180deg); font-size:11px; white-space:nowrap;">{yLabel}</div>
              <div>
                {svg}
                <div style="text-align:center; font-size:11px;">{xLabel}</div>
              </div>
            </div>
            """;
    }

    private static void AppendSegment(StringBuilder svg, GraphBounds bounds, GraphSegment segment, double width, double height)
    {
        var pointsAttr = string.Join(" ", segment.Points.Select(p =>
        {
            var (x, y) = ToSvgPoint(bounds, p, width, height);
            return Inv($"{x:0.##},{y:0.##}");
        }));
        var dashAttr = segment.Solid ? "" : " stroke-dasharray=\"4,3\"";
        svg.Append(Inv($"<polyline points=\"{pointsAttr}\" fill=\"none\" stroke=\"black\" stroke-width=\"2\"{dashAttr} />"));

        if (segment.Label is { } label && segment.Points.Count > 0)
        {
            var (x, y) = ToSvgPoint(bounds, segment.Points[^1], width, height);
            AppendText(svg, x + 3, y - 3, label, "dimgray", 10);
        }
    }

    private static void AppendReferenceLine(StringBuilder svg, GraphBounds bounds, GraphReferenceValue reference, double width, double height)
    {
        double x1, y1, x2, y2, labelX, labelY;
        if (reference.Axis == GraphAxisKind.X)
        {
            var x = bounds.NormalizeX(reference.Value) * width;
            (x1, y1, x2, y2) = (x, 0, x, height);
            (labelX, labelY) = (Math.Min(x + 2, width - 40), 10);
        }
        else
        {
            var y = (1 - bounds.NormalizeY(reference.Value)) * height;
            (x1, y1, x2, y2) = (0, y, width, y);
            (labelX, labelY) = (2, Math.Max(y - 3, 10));
        }

        svg.Append(Inv($"<line x1=\"{x1:0.##}\" y1=\"{y1:0.##}\" x2=\"{x2:0.##}\" y2=\"{y2:0.##}\" stroke=\"gray\" stroke-width=\"1\" stroke-dasharray=\"2,2\" />"));
        AppendText(svg, labelX, labelY, reference.Label, "gray", 9);
    }

    private static (double X, double Y) ToSvgPoint(GraphBounds bounds, GraphPoint point, double width, double height) =>
        (bounds.NormalizeX(point.X) * width, (1 - bounds.NormalizeY(point.Y)) * height);

    private static void AppendText(StringBuilder svg, double x, double y, string text, string color, double fontSize) =>
        svg.Append(Inv($"<text x=\"{x:0.##}\" y=\"{y:0.##}\" fill=\"{color}\" font-size=\"{fontSize}\">{WebUtility.HtmlEncode(text)}</text>"));

    private static string FormatAxisLabel(GraphAxis axis) =>
        axis.Unit is null ? axis.Label : $"{axis.Label} ({axis.Unit})";

    private static string FormatNumber(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
