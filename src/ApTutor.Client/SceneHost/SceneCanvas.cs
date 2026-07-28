// Hosts ApTutor.Scene's SKCanvas-based SceneRenderer inside Avalonia. Avalonia 12 has no
// SkiaSharp.Views.WPF-style SKElement; instead a Control draws via a custom draw operation that
// leases the platform's live SKCanvas through ISkiaSharpApiLeaseFeature (PHASE2-HANDOFF.md's
// "wire into WPF with an SKElement" instruction, adapted for Avalonia — see the Phase 0 comment
// left in SceneRenderer.cs).

using ApTutor.Scene;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace ApTutor.Client.SceneHost;

public sealed class SceneCanvas : Control
{
    public static readonly StyledProperty<SceneState?> SceneStateProperty =
        AvaloniaProperty.Register<SceneCanvas, SceneState?>(nameof(SceneState));

    public SceneState? SceneState
    {
        get => GetValue(SceneStateProperty);
        set => SetValue(SceneStateProperty, value);
    }

    private readonly SceneRenderer _renderer = new();

    static SceneCanvas() => AffectsRender<SceneCanvas>(SceneStateProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (SceneState is { } state)
            context.Custom(new SceneDrawOp(new Rect(Bounds.Size), state, _renderer));
    }

    private sealed class SceneDrawOp(Rect bounds, SceneState state, SceneRenderer renderer) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Dispose() { }

        public bool Equals(ICustomDrawOperation? other) => false;

        public bool HitTest(Point p) => false;

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (lease is null) return;

            using var l = lease.Lease();
            var canvas = l.SkCanvas;
            var width = Math.Max(1, (int)Bounds.Width);
            var height = Math.Max(1, (int)Bounds.Height);
            var info = new SKImageInfo(width, height);

            // SceneRenderer.Render() starts with an unclipped canvas.Clear(), which would
            // otherwise wipe the whole shared window surface rather than just this control's
            // region — Avalonia does not clip custom draw operations for us.
            canvas.Save();
            canvas.ClipRect(new SKRect(0, 0, width, height));
            renderer.Render(state, canvas, info);
            canvas.Restore();
        }
    }
}
