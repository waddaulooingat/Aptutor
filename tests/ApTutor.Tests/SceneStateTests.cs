using ApTutor.Scene;
using Xunit;

namespace ApTutor.Tests;

// PHASE2-HANDOFF.md acceptance criteria (the SceneState ones; renderer criterion is in
// SceneRendererTests.cs).
public class SceneStateTests
{
    private static SceneState ApplyAll(IEnumerable<SceneDelta> deltas)
    {
        var state = new SceneState();
        foreach (var delta in deltas)
        {
            state.ClearFlashes();
            state.Apply(delta);
        }
        return state;
    }

    // "Applying the demo fixture yields the end state above; q and p both target '1'."
    [Fact]
    public void ReferenceVsValueDemo_EndState_MatchesExpected()
    {
        var state = ApplyAll(ReferenceVsValueDemo.Steps.Select(s => s.Delta));

        var frame = Assert.Single(state.Frames);
        Assert.Equal("main", frame.Id);

        var x = frame.Cell("x");
        Assert.NotNull(x);
        Assert.Equal("5", x!.Value);
        Assert.Null(x.TargetObjId);

        var p = frame.Cell("p");
        var q = frame.Cell("q");
        Assert.NotNull(p);
        Assert.NotNull(q);
        Assert.Equal("1", p!.TargetObjId);
        Assert.Equal("1", q!.TargetObjId); // same target — the aliasing lesson

        var obj = Assert.Single(state.Heap.Values);
        Assert.Equal("Point", obj.ClassName);
        Assert.Equal("9", obj.Fields.Single(f => f.Key == "x").Value);
    }

    // "Round-trip: after the full fixture, one StepBack() restores #1.x to '5'; a second
    // restores q to absent (its RefSet inverse removed the freshly-added cell)."
    [Fact]
    public void ReferenceVsValueDemo_StepBackTwice_RestoresPriorStates()
    {
        var state = ApplyAll(ReferenceVsValueDemo.Steps.Select(s => s.Delta));

        state.StepBack(); // undo step 4: q.setX(9)
        Assert.Equal("5", state.Heap["1"].Fields.Single(f => f.Key == "x").Value);

        state.StepBack(); // undo step 3: Point q = p
        Assert.Null(state.Frames.Single().Cell("q"));
    }

    // "FramePop then StepBack() restores the popped frame with all its cells intact."
    [Fact]
    public void FramePop_ThenStepBack_RestoresFrameWithCellsIntact()
    {
        var state = new SceneState();
        state.Apply(new SceneDelta(new SceneOp[] { new FramePush("f", "f()") }));
        state.Apply(new SceneDelta(new SceneOp[] { new MemCellSet("f", "a", "int", "1") }));
        state.Apply(new SceneDelta(new SceneOp[] { new MemCellSet("f", "b", "int", "2") }));
        state.Apply(new SceneDelta(new SceneOp[] { new FramePop("f") }));

        Assert.Empty(state.Frames);

        state.StepBack();

        var frame = Assert.Single(state.Frames);
        Assert.Equal("1", frame.Cell("a")!.Value);
        Assert.Equal("2", frame.Cell("b")!.Value);
    }

    // "MemCellSet on an existing name updates in place; its inverse restores the prior value
    // and clears the reference flag if one was set."
    [Fact]
    public void MemCellSet_OnExistingCell_UpdatesInPlace_InverseRestoresPriorValueAndRef()
    {
        var state = new SceneState();
        state.Apply(new SceneDelta(new SceneOp[] { new FramePush("f", "f()") }));
        state.Apply(new SceneDelta(new SceneOp[]
            { new HeapAlloc("1", "Obj", Array.Empty<KeyValuePair<string, string>>()) }));
        state.Apply(new SceneDelta(new SceneOp[] { new RefSet("f", "r", "1") })); // r -> #1

        state.Apply(new SceneDelta(new SceneOp[] { new MemCellSet("f", "r", "int", "42") }));
        var cell = state.Frames.Single().Cell("r")!;
        Assert.Equal("42", cell.Value);
        Assert.Null(cell.TargetObjId);

        state.StepBack();
        cell = state.Frames.Single().Cell("r")!;
        Assert.Equal("1", cell.TargetObjId);
    }

    // "ClearFlashes() clears all cell flashes; a subsequent write re-flashes only the
    // written cell."
    [Fact]
    public void ClearFlashes_ClearsAll_SubsequentWriteReflashesOnlyThatCell()
    {
        var state = new SceneState();
        state.Apply(new SceneDelta(new SceneOp[] { new FramePush("f", "f()") }));
        state.Apply(new SceneDelta(new SceneOp[]
        {
            new MemCellSet("f", "a", "int", "1"),
            new MemCellSet("f", "b", "int", "2"),
        }));

        var frame = state.Frames.Single();
        Assert.True(frame.Cell("a")!.Flash);
        Assert.True(frame.Cell("b")!.Flash);

        state.ClearFlashes();
        Assert.False(frame.Cell("a")!.Flash);
        Assert.False(frame.Cell("b")!.Flash);

        state.Apply(new SceneDelta(new SceneOp[] { new MemCellSet("f", "a", "int", "3") }));
        Assert.True(frame.Cell("a")!.Flash);
        Assert.False(frame.Cell("b")!.Flash);
    }
}
