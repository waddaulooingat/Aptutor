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

    // Phase 5: the 5 new primitives (indexedStrip, grid2d, callTree, exprBubble, boolTruthGlow)
    // render together — traced from real source, not a hand-authored fixture — without throwing.
    [Fact]
    public void Render_Phase5Primitives_FromRealTrace_DoesNotThrow()
    {
        const string source = """
            class Demo {
                static int fib(int n) {
                    if (n < 2) return n;
                    return fib(n - 1) + fib(n - 2);
                }
                public static void main(String[] args) {
                    int[] nums = new int[3];
                    nums[1] = 99;
                    int[][] grid = new int[2][2];
                    grid[0][1] = 5;
                    ArrayList<Integer> list = new ArrayList<>();
                    list.add(7);
                    int result = fib(4);
                }
            }
            """;

        var trace = new Tracer.Tracer().Trace(source);
        Assert.True(trace.Completed, trace.HaltReason);

        var state = new SceneState();
        foreach (var step in trace.Steps)
        {
            state.ClearFlashes();
            state.Apply(step.Delta);
        }

        Assert.NotEmpty(state.Arrays);
        Assert.NotEmpty(state.Grids);
        Assert.NotEmpty(state.CallTree);

        // exprBubble/boolTruthGlow (state + renderer) exist as primitives but aren't wired into
        // the interpreter's expression evaluation in this phase — feed them directly to confirm
        // the renderer still handles a scene that combines all 5 primitives at once.
        state.Apply(new SceneDelta(new SceneOp[]
        {
            new ExprPush("e1", "nums[1] > 0"),
            new BoolGlow("e1", true),
            new ExprResolve("e1", "true"),
        }));

        var info = new SKImageInfo(1000, 700);
        using var surface = SKSurface.Create(info);
        var renderer = new SceneRenderer();

        var exception = Record.Exception(() => renderer.Render(state, surface.Canvas, info));

        Assert.Null(exception);

        using var image = surface.Snapshot();
        Assert.True(image.Width > 0 && image.Height > 0);
    }
}
