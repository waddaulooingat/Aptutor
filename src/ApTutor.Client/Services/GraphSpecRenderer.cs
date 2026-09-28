// AP Tutor — graph-spec-rendering plan, Part 3: the Shell's renderer for a GraphSpec answer choice
// (see ApTutor.Platform.GraphSpec's remarks for why this is a separate, thin adapter rather than
// shared draw calls with Content Admin's SVG helper — a desktop app and a server-rendered web page
// can't literally share drawing code, only the geometry/scaling logic behind it, see GraphGeometry).
//
// Built from plain Avalonia controls composed onto a Canvas — matching this codebase's established
// style (see MathTextRenderer) of composing standard controls rather than a custom-rendered Visual
// with a DrawingContext override.

using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using ApTutor.Platform;

namespace ApTutor.Client.Services;

public static class GraphSpecRenderer
{
    public static Control Build(GraphSpec spec, double width = 260, double height = 170)
    {
        var bounds = GraphGeometry.ComputeBounds(spec);
        var canvas = new Canvas { Width = width, Height = height, Background = Brushes.White };

        if (spec.ReferenceValues is { } references)
            foreach (var reference in references)
                AddReferenceLine(canvas, bounds, reference, width, height);

        foreach (var segment in spec.Segments)
            AddSegment(canvas, bounds, segment, width, height);

        // Plain numeric corner labels rather than full tick marks/gridlines — enough to make the
        // plot's scale legible for a multiple-choice answer option without building a general-purpose
        // charting axis (out of scope — see GraphSpec's own remarks on what this schema covers).
        AddCornerLabel(canvas, FormatNumber(bounds.XMin), 2, height - 13);
        AddCornerLabel(canvas, FormatNumber(bounds.XMax), width - 26, height - 13);
        AddCornerLabel(canvas, FormatNumber(bounds.YMax), 2, 1);
        AddCornerLabel(canvas, FormatNumber(bounds.YMin), 2, height - 26);

        var plot = new Border
        {
            Width = width,
            Height = height,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Child = canvas,
        };

        var xLabel = new TextBlock
        {
            Text = FormatAxisLabel(spec.XAxis),
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var yLabel = new TextBlock
        {
            Text = FormatAxisLabel(spec.YAxis),
            FontSize = 11,
            RenderTransform = new RotateTransform(-90),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var plotColumn = new StackPanel { Orientation = Orientation.Vertical };
        plotColumn.Children.Add(plot);
        plotColumn.Children.Add(xLabel);

        var root = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        root.Children.Add(yLabel);
        root.Children.Add(plotColumn);
        return root;
    }

    private static void AddSegment(Canvas canvas, GraphBounds bounds, GraphSegment segment, double width, double height)
    {
        var points = new Points();
        foreach (var point in segment.Points)
            points.Add(ToCanvasPoint(bounds, point, width, height));

        var polyline = new Polyline { Stroke = Brushes.Black, StrokeThickness = 2, Points = points };
        if (!segment.Solid)
            polyline.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        canvas.Children.Add(polyline);

        if (segment.Label is { } label && segment.Points.Count > 0)
        {
            var last = ToCanvasPoint(bounds, segment.Points[^1], width, height);
            var text = new TextBlock { Text = label, FontSize = 10, Foreground = Brushes.DimGray };
            Canvas.SetLeft(text, Math.Clamp(last.X + 3, 0, Math.Max(0, width - 4)));
            Canvas.SetTop(text, Math.Clamp(last.Y - 12, 0, Math.Max(0, height - 12)));
            canvas.Children.Add(text);
        }
    }

    private static void AddReferenceLine(Canvas canvas, GraphBounds bounds, GraphReferenceValue reference, double width, double height)
    {
        Line line;
        Point labelPos;
        if (reference.Axis == GraphAxisKind.X)
        {
            var x = bounds.NormalizeX(reference.Value) * width;
            line = new Line { StartPoint = new Point(x, 0), EndPoint = new Point(x, height) };
            labelPos = new Point(Math.Clamp(x + 2, 0, Math.Max(0, width - 40)), 1);
        }
        else
        {
            var y = (1 - bounds.NormalizeY(reference.Value)) * height;
            line = new Line { StartPoint = new Point(0, y), EndPoint = new Point(width, y) };
            labelPos = new Point(2, Math.Clamp(y - 12, 0, Math.Max(0, height - 12)));
        }

        line.Stroke = Brushes.Gray;
        line.StrokeThickness = 1;
        line.StrokeDashArray = new AvaloniaList<double> { 2, 2 };
        canvas.Children.Add(line);

        var text = new TextBlock { Text = reference.Label, FontSize = 9, Foreground = Brushes.Gray };
        Canvas.SetLeft(text, labelPos.X);
        Canvas.SetTop(text, labelPos.Y);
        canvas.Children.Add(text);
    }

    private static Point ToCanvasPoint(GraphBounds bounds, GraphPoint point, double width, double height) =>
        new(bounds.NormalizeX(point.X) * width, (1 - bounds.NormalizeY(point.Y)) * height);

    private static void AddCornerLabel(Canvas canvas, string text, double left, double top)
    {
        var block = new TextBlock { Text = text, FontSize = 9, Foreground = Brushes.DimGray };
        Canvas.SetLeft(block, left);
        Canvas.SetTop(block, top);
        canvas.Children.Add(block);
    }

    private static string FormatAxisLabel(GraphAxis axis) =>
        axis.Unit is null ? axis.Label : $"{axis.Label} ({axis.Unit})";

    private static string FormatNumber(double value) =>
        value == Math.Floor(value) && !double.IsInfinity(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##", CultureInfo.InvariantCulture);
}
