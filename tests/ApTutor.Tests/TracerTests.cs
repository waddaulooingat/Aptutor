using ApTutor.Scene;
using ApTutor.Tracer;
using Xunit;

namespace ApTutor.Tests;

// PHASE3-HANDOFF.md "Acceptance criteria — close the loop".
public class TracerTests
{
    private const string ReferenceVsValueDemoSource = """
        class Point {
            int x;
            void setX(int x) { this.x = x; }
        }

        class Demo {
            public static void main(String[] args) {
                int x = 5;
                Point p = new Point();
                p.setX(5);
                Point q = p;
                q.setX(9);
            }
        }
        """;

    private static SceneState ApplyAll(TraceResult result)
    {
        var state = new SceneState();
        foreach (var step in result.Steps)
        {
            state.ClearFlashes();
            state.Apply(step.Delta);
        }
        return state;
    }

    // "feed the phase-2 reference-vs-value Java ... into Tracer.Trace, and assert it emits a
    // TraceStep[] whose deltas match the hand-written phase-2 fixture — same heap id '1', same
    // two RefSets to that id, final FieldSet('1','x','9')." Plus: "when the animation looks
    // identical whether fed the fixture or the tracer output, phase 3 is done" — checked by
    // applying the tracer's own output to a SceneState and comparing end state to phase 2's.
    [Fact]
    public void Trace_ReferenceVsValueDemo_ClosesTheLoopWithPhase2Fixture()
    {
        var result = new Tracer.Tracer().Trace(ReferenceVsValueDemoSource);

        Assert.True(result.Completed, result.HaltReason);

        var allOps = result.Steps.SelectMany(s => s.Delta.Ops).ToList();
        var heapAllocs = allOps.OfType<HeapAlloc>().ToList();
        var refSets = allOps.OfType<RefSet>().ToList();
        var fieldSets = allOps.OfType<FieldSet>().ToList();

        var point = Assert.Single(heapAllocs);
        Assert.Equal("1", point.ObjId);

        var refsToPoint = refSets.Where(r => r.TargetObjId == "1").ToList();
        Assert.Equal(2, refsToPoint.Count); // p -> #1, q -> #1

        var lastFieldSet = fieldSets.Last(f => f.ObjId == "1" && f.Field == "x");
        Assert.Equal("9", lastFieldSet.Value);

        // End state must match phase 2's hand-authored fixture exactly (ReferenceVsValueDemo.cs).
        var traced = ApplyAll(result);
        var expected = new SceneState();
        foreach (var (_, delta) in ReferenceVsValueDemo.Steps)
        {
            expected.ClearFlashes();
            expected.Apply(delta);
        }

        var tracedFrame = Assert.Single(traced.Frames);
        var expectedFrame = Assert.Single(expected.Frames);
        Assert.Equal(expectedFrame.Cell("x")!.Value, tracedFrame.Cell("x")!.Value);
        Assert.Equal(expectedFrame.Cell("p")!.TargetObjId, tracedFrame.Cell("p")!.TargetObjId);
        Assert.Equal(expectedFrame.Cell("q")!.TargetObjId, tracedFrame.Cell("q")!.TargetObjId);

        var tracedObj = Assert.Single(traced.Heap.Values);
        var expectedObj = Assert.Single(expected.Heap.Values);
        Assert.Equal(expectedObj.ClassName, tracedObj.ClassName);
        Assert.Equal(
            expectedObj.Fields.Single(f => f.Key == "x").Value,
            tracedObj.Fields.Single(f => f.Key == "x").Value);
    }

    // "A method call emits FramePush then per-parameter MemCellSet/RefSet, and FramePop on
    // return; running it through SceneState then StepBack per step returns to empty state."
    [Fact]
    public void MethodCall_EmitsFramePushParamsAndFramePop_StepBackRoundTripsToEmpty()
    {
        var result = new Tracer.Tracer().Trace(ReferenceVsValueDemoSource);
        Assert.True(result.Completed, result.HaltReason);

        var calls = result.Steps.Where(s => s.Kind == StepKind.Call && s.Delta.Ops.OfType<FramePush>().Any()).ToList();
        Assert.Equal(2, calls.Count); // p.setX(5) and q.setX(9)

        foreach (var call in calls)
        {
            var push = Assert.Single(call.Delta.Ops.OfType<FramePush>());
            var paramSet = Assert.Single(call.Delta.Ops.OfType<MemCellSet>());
            Assert.Equal(push.FrameId, paramSet.FrameId);
            Assert.Equal("x", paramSet.Name);

            var pop = Assert.Single(
                result.Steps.Where(s => s.Kind == StepKind.Return).SelectMany(s => s.Delta.Ops.OfType<FramePop>()),
                p => p.FrameId == push.FrameId);
            Assert.Equal(push.FrameId, pop.FrameId);
        }

        // Forward through every step, then StepBack the same number of times: back to empty.
        var state = new SceneState();
        foreach (var step in result.Steps)
        {
            state.ClearFlashes();
            state.Apply(step.Delta);
        }
        for (var i = 0; i < result.Steps.Count; i++)
            state.StepBack();

        Assert.Empty(state.Frames);
        Assert.Empty(state.Heap);
    }

    // "An out-of-subset program (e.g. contains a lambda) returns Completed == false with a
    // HaltReason naming the construct and line — no partial garbage stream."
    [Theory]
    [InlineData("""
        class Demo {
            public static void main(String[] args) {
                Runnable r = () -> System.out.println("hi");
            }
        }
        """, "lambda")]
    [InlineData("""
        class Demo {
            public static void main(String[] args) {
                try {
                    int x = 1;
                } catch (Exception e) {
                }
            }
        }
        """, "try")]
    public void Trace_OutOfSubsetProgram_HaltsCleanlyWithNoSteps(string source, string expectedConstructFragment)
    {
        var result = new Tracer.Tracer().Trace(source);

        Assert.False(result.Completed);
        Assert.NotNull(result.HaltReason);
        Assert.Contains(expectedConstructFragment, result.HaltReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Steps);
    }

    // "Same source + same seed => identical steps across two runs (determinism)."
    [Fact]
    public void Trace_SameSourceAndSeed_ProducesIdenticalSteps()
    {
        const string source = """
            class Demo {
                public static void main(String[] args) {
                    double a = Math.random();
                    double b = Math.random();
                    int i = 0;
                    while (i < 3) {
                        i = i + 1;
                    }
                }
            }
            """;

        var run1 = new Tracer.Tracer().Trace(source, randomSeed: 7);
        var run2 = new Tracer.Tracer().Trace(source, randomSeed: 7);

        Assert.True(run1.Completed, run1.HaltReason);
        Assert.True(run2.Completed, run2.HaltReason);
        Assert.Equal(run1.Steps.Count, run2.Steps.Count);
        for (var i = 0; i < run1.Steps.Count; i++)
        {
            Assert.Equal(run1.Steps[i].Kind, run2.Steps[i].Kind);
            Assert.Equal(run1.Steps[i].Caption, run2.Steps[i].Caption);
            Assert.Equal(run1.Steps[i].Delta.Ops.Count, run2.Steps[i].Delta.Ops.Count);
        }
    }
}
