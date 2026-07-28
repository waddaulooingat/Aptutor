// AP Tutor — Phase 2: SkiaSharp renderer for the first three scene primitives.
// Draws SceneState onto an SKCanvas. Reads state only; never mutates it.
// Primitives covered: memCell (value cells in a frame), heapObject (right column),
// refArrow (line from a reference cell to its target heap object).
//
// Pinned API: SkiaSharp 2.88.x. Text via SKFont + canvas.DrawText(text, x, y, align, font, paint).
// Wire into WPF with an SKElement whose PaintSurface handler calls Render(state, e.Surface.Canvas, e.Info).

using SkiaSharp;

namespace ApTutor.Scene;

public sealed class SceneRenderer
{
    // Layout constants (device-independent px). Tune later; keep them named, not magic.
    private const float Pad = 24f;
    private const float CellW = 190f, CellH = 34f, CellGap = 8f;
    private const float FrameGap = 18f, FrameHeaderH = 26f, FramePadInner = 10f;
    private const float ObjW = 210f, ObjRowH = 30f, ObjHeaderH = 28f, ObjGap = 20f;
    private const float Corner = 6f;

    private static readonly SKColor Ink = new(0x1A, 0x1A, 0x1A);
    private static readonly SKColor Muted = new(0x6B, 0x72, 0x80);
    private static readonly SKColor FrameFill = new(0xF4, 0xF6, 0xF8);
    private static readonly SKColor CellFill = new(0xFF, 0xFF, 0xFF);
    private static readonly SKColor ObjFill = new(0xEE, 0xF2, 0xFF);
    private static readonly SKColor FlashBorder = new(0xF5, 0x9E, 0x0B);
    private static readonly SKColor RefColor = new(0x2563, 0xEB >> 8, 0xEB & 0xFF); // #2563EB
    private static readonly SKColor Line = new(0xD1, 0xD5, 0xDB);

    // Cache heap-object rects each frame so refArrows can target them.
    private readonly Dictionary<string, SKRect> _objRects = new(StringComparer.Ordinal);

    public void Render(SceneState state, SKCanvas canvas, SKImageInfo info)
    {
        canvas.Clear(SKColors.White);
        _objRects.Clear();

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, Color = Line };
        using var textPaint = new SKPaint { IsAntialias = true, Color = Ink };
        using var mutedPaint = new SKPaint { IsAntialias = true, Color = Muted };
        using var font = new SKFont(SKTypeface.FromFamilyName("Cascadia Code") ?? SKTypeface.Default, 14f);
        using var headFont = new SKFont(SKTypeface.Default, 13f) { Embolden = true };

        float heapLeft = info.Width - Pad - ObjW;

        // ---- Heap column (draw first so we know target rects for arrows) ----
        float hy = Pad;
        foreach (var obj in state.Heap.Values)
        {
            float h = ObjHeaderH + Math.Max(1, obj.Fields.Count) * ObjRowH + FramePadInner;
            var rect = new SKRect(heapLeft, hy, heapLeft + ObjW, hy + h);
            _objRects[obj.Id] = rect;

            fill.Color = ObjFill;
            canvas.DrawRoundRect(rect, Corner, Corner, fill);
            canvas.DrawRoundRect(rect, Corner, Corner, stroke);
            DrawText(canvas, $"{obj.ClassName}  #{obj.Id}", rect.Left + FramePadInner, rect.Top + 19f, headFont, textPaint);

            float fy = rect.Top + ObjHeaderH + 6f;
            foreach (var kv in obj.Fields)
            {
                DrawText(canvas, kv.Key, rect.Left + FramePadInner, fy + 15f, font, mutedPaint);
                DrawText(canvas, kv.Value, rect.Left + ObjW * 0.55f, fy + 15f, font, textPaint);
                fy += ObjRowH;
            }
            hy += h + ObjGap;
        }

        // ---- Call stack (bottom frame lowest); collect ref anchors as we go ----
        var refAnchors = new List<(SKPoint from, string target)>();
        float totalStackH = state.Frames.Sum(FrameHeight) + Math.Max(0, state.Frames.Count - 1) * FrameGap;
        float fyTop = Math.Max(Pad, info.Height - Pad - totalStackH);

        foreach (var frame in state.Frames) // index 0 (bottom) drawn topmost visually is a choice; keep call order top-down
        {
            float fh = FrameHeight(frame);
            var frect = new SKRect(Pad, fyTop, Pad + CellW + 2 * FramePadInner, fyTop + fh);
            fill.Color = FrameFill;
            canvas.DrawRoundRect(frect, Corner, Corner, fill);
            canvas.DrawRoundRect(frect, Corner, Corner, stroke);
            DrawText(canvas, frame.MethodSig, frect.Left + FramePadInner, frect.Top + 18f, headFont, textPaint);

            float cy = frect.Top + FrameHeaderH;
            foreach (var cell in frame.Cells)
            {
                var crect = new SKRect(frect.Left + FramePadInner, cy,
                                       frect.Left + FramePadInner + CellW, cy + CellH);
                fill.Color = CellFill;
                canvas.DrawRoundRect(crect, 4f, 4f, fill);

                stroke.Color = cell.Flash ? FlashBorder : Line;
                stroke.StrokeWidth = cell.Flash ? 2.5f : 1.5f;
                canvas.DrawRoundRect(crect, 4f, 4f, stroke);
                stroke.Color = Line; stroke.StrokeWidth = 1.5f;

                string label = string.IsNullOrEmpty(cell.Type) ? cell.Name : $"{cell.Name}:{cell.Type}";
                DrawText(canvas, label, crect.Left + 8f, crect.MidY + 5f, font, mutedPaint);

                if (cell.TargetObjId is { } target)
                {
                    // refArrow: anchor on right edge of the cell, resolve target after loop.
                    var anchor = new SKPoint(crect.Right - 6f, crect.MidY);
                    refAnchors.Add((anchor, target));
                    canvas.DrawCircle(anchor, 3.5f, new SKPaint { Color = RefColor, IsAntialias = true });
                }
                else
                {
                    DrawText(canvas, cell.Value, crect.Right - 8f, crect.MidY + 5f, font, textPaint, SKTextAlign.Right);
                }
                cy += CellH + CellGap;
            }
            fyTop += fh + FrameGap;
        }

        // ---- refArrows: stack cell -> heap object ----
        using var refPaint = new SKPaint { IsAntialias = true, Color = RefColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
        foreach (var (from, target) in refAnchors)
        {
            if (!_objRects.TryGetValue(target, out var dst)) continue;
            var to = new SKPoint(dst.Left, dst.MidY);
            DrawArrow(canvas, from, to, refPaint);
        }

        // ---- current-line badge (lineHighlight) ----
        if (state.HighlightLine is { } ln)
            DrawText(canvas, $"line {ln}", Pad, Pad - 6f + 14f, headFont, mutedPaint);
    }

    private static float FrameHeight(FrameView f) =>
        FrameHeaderH + f.Cells.Count * (CellH + CellGap) + FramePadInner;

    private static void DrawText(SKCanvas c, string text, float x, float y,
                                 SKFont font, SKPaint paint, SKTextAlign align = SKTextAlign.Left)
        => c.DrawText(text, x, y, align, font, paint);

    private static void DrawArrow(SKCanvas c, SKPoint from, SKPoint to, SKPaint paint)
    {
        // slight horizontal easing so arrows don't overlap cell text
        var mid = new SKPoint((from.X + to.X) / 2f, from.Y);
        using var path = new SKPath();
        path.MoveTo(from);
        path.CubicTo(mid.X, from.Y, mid.X, to.Y, to.X, to.Y);
        c.DrawPath(path, paint);

        // arrowhead at destination
        const float a = 7f;
        var dir = new SKPoint(to.X - mid.X, to.Y - to.Y == 0 ? 0.001f : to.Y - mid.Y);
        float len = MathF.Max(0.001f, MathF.Sqrt(dir.X * dir.X + dir.Y * dir.Y));
        var ux = dir.X / len; var uy = dir.Y / len;
        var left = new SKPoint(to.X - a * ux + a * 0.5f * uy, to.Y - a * uy - a * 0.5f * ux);
        var right = new SKPoint(to.X - a * ux - a * 0.5f * uy, to.Y - a * uy + a * 0.5f * ux);
        using var head = new SKPath();
        head.MoveTo(to); head.LineTo(left); head.LineTo(right); head.Close();
        using var fill = new SKPaint { Color = paint.Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        c.DrawPath(head, fill);
    }
}
