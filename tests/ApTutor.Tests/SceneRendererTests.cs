using ApTutor.Scene;
using SkiaSharp;
using Xunit;

namespace ApTutor.Tests;

// PHASE2-HANDOFF.md: "Renderer renders the demo fixture to an off-screen surface without
// throwing."
public class SceneRendererTests
{
    [Fact]
    public void Render_ReferenceVsValueDemo_DoesNotThrow()
    {
        var state = new SceneState();
        foreach (var (_, delta) in ReferenceVsValueDemo.Steps)
        {
            state.ClearFlashes();
            state.Apply(delta);
        }

        var info = new SKImageInfo(800, 600);
        using var surface = SKSurface.Create(info);
        var renderer = new SceneRenderer();

        var exception = Record.Exception(() => renderer.Render(state, surface.Canvas, info));

        Assert.Null(exception);

        using var image = surface.Snapshot();
        Assert.True(image.Width > 0 && image.Height > 0);
    }
}
