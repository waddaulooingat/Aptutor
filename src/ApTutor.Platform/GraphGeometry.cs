// Graph-spec-rendering plan — the shared layout/geometry logic behind both graph renderers (see
// GraphSpec's remarks: an Avalonia control in the Shell, an inline-SVG helper in Content Admin, each
// a thin adapter over this rather than sharing literal draw calls, since a desktop app and a
// server-rendered web page can't do that). Pure data-in/data-out, no UI dependency, so both projects
// (which already depend on this one for GraphSpec itself) can share identical axis-scaling behavior
// without duplicating it.

namespace ApTutor.Platform;

/// Resolved plot bounds for one axis pair, always in DATA space (the same units as GraphPoint.X/Y).
/// Normalize{X,Y} map a data value to [0,1] (0 = axis minimum, 1 = axis maximum); a renderer then
/// multiplies by its own pixel width/height. Y is intentionally NOT flipped here — a data plot's Y
/// grows upward while both Avalonia's Canvas and SVG's coordinate space grow downward, and that flip
/// is a pixel-space concern each renderer applies itself, not a geometry concern shared between them.
public sealed record GraphBounds(double XMin, double XMax, double YMin, double YMax)
{
    public double NormalizeX(double x) => XMax > XMin ? (x - XMin) / (XMax - XMin) : 0.5;
    public double NormalizeY(double y) => YMax > YMin ? (y - YMin) / (YMax - YMin) : 0.5;
}

public static class GraphGeometry
{
    /// Inward padding applied only to an auto-scaled bound (never to an explicit GraphAxis.Min/Max,
    /// which the content author stated deliberately) — purely cosmetic, so a point sitting exactly at
    /// a data extreme doesn't render flush against the plot's border.
    private const double AutoScalePadding = 0.08;

    /// Each axis's Min/Max is resolved independently: an explicit value on the GraphAxis wins outright
    /// for that bound; an absent one is auto-scaled from every point across every segment PLUS every
    /// reference value on that axis (so a reference line below/above all segment data — e.g. a "v=0"
    /// guide — still fits inside the plot). A degenerate axis (every value identical, or nothing to
    /// plot on it at all) falls back to a fixed +/-1 window around that value (or a plain 0..1 window
    /// if there's truly nothing) so bounds are never zero-width, which NormalizeX/Y would otherwise
    /// have to guard against on every call instead of once, here.
    public static GraphBounds ComputeBounds(GraphSpec spec)
    {
        var (xMin, xMax) = ResolveAxisRange(
            spec.XAxis,
            spec.Segments.SelectMany(s => s.Points.Select(p => p.X))
                .Concat(spec.ReferenceValues?.Where(r => r.Axis == GraphAxisKind.X).Select(r => r.Value) ?? Enumerable.Empty<double>()));
        var (yMin, yMax) = ResolveAxisRange(
            spec.YAxis,
            spec.Segments.SelectMany(s => s.Points.Select(p => p.Y))
                .Concat(spec.ReferenceValues?.Where(r => r.Axis == GraphAxisKind.Y).Select(r => r.Value) ?? Enumerable.Empty<double>()));
        return new GraphBounds(xMin, xMax, yMin, yMax);
    }

    private static (double Min, double Max) ResolveAxisRange(GraphAxis axis, IEnumerable<double> values)
    {
        if (axis.Min is { } explicitMin && axis.Max is { } explicitMax)
            return (explicitMin, explicitMax);

        var list = values.ToList();
        if (list.Count == 0)
            return (axis.Min ?? 0, axis.Max ?? 1);

        var dataMin = list.Min();
        var dataMax = list.Max();
        if (dataMax <= dataMin)
        {
            dataMin -= 1;
            dataMax += 1;
        }

        var padding = (dataMax - dataMin) * AutoScalePadding;
        return (axis.Min ?? dataMin - padding, axis.Max ?? dataMax + padding);
    }
}
