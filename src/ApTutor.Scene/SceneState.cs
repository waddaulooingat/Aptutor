// AP Tutor — Phase 2: Scene state + delta application.
// Target: .NET 8, C# 12, nullable enabled. No Skia dependency here (headlessly testable).
// Implements the SceneDelta protocol subset needed for the first three primitives:
//   memCell  -> CellView inside a FrameView
//   heapObject -> HeapObjectView in Heap
//   refArrow -> a CellView whose TargetObjId points into Heap
// Forward application records an inverse per delta so the shell can step backward.

namespace ApTutor.Scene;

// ---------- Delta protocol (phase-2 subset of CLAUDE-HANDOFF §5.2) ----------

public abstract record SceneOp;

public sealed record FramePush(string FrameId, string MethodSig) : SceneOp;
public sealed record FramePop(string FrameId) : SceneOp;
public sealed record MemCellSet(string FrameId, string Name, string Type, string Value) : SceneOp;
public sealed record MemCellFlash(string FrameId, string Name) : SceneOp;
public sealed record HeapAlloc(string ObjId, string ClassName,
                              IReadOnlyList<KeyValuePair<string, string>> Fields) : SceneOp;
public sealed record FieldSet(string ObjId, string Field, string Value) : SceneOp;
public sealed record RefSet(string FrameId, string VarName, string? TargetObjId) : SceneOp; // null = null ref
public sealed record LineHighlight(int Line) : SceneOp;

public sealed record SceneDelta(IReadOnlyList<SceneOp> Ops);

// ---------- Mutable view model the renderer reads ----------

public sealed class CellView
{
    public required string Name { get; set; }
    public string Type { get; set; } = "";
    public string Value { get; set; } = "";     // shown when not a reference
    public string? TargetObjId { get; set; }     // non-null => draw a refArrow instead of Value
    public bool Flash { get; set; }              // transient highlight after a write
}

public sealed class FrameView
{
    public required string Id { get; init; }
    public required string MethodSig { get; init; }
    public List<CellView> Cells { get; } = new();
    public CellView? Cell(string name) => Cells.FirstOrDefault(c => c.Name == name);
}

public sealed class HeapObjectView
{
    public required string Id { get; init; }
    public required string ClassName { get; init; }
    public List<KeyValuePair<string, string>> Fields { get; } = new();
}

// ---------- Scene state ----------

public sealed class SceneState
{
    public List<FrameView> Frames { get; } = new(); // index 0 = bottom of stack
    public Dictionary<string, HeapObjectView> Heap { get; } = new(StringComparer.Ordinal);
    public int? HighlightLine { get; private set; }

    private readonly Stack<Action> _undo = new(); // one composite undo per applied delta

    public bool CanStepBack => _undo.Count > 0;

    /// Clears all transient flashes (call once per forward step, before applying the delta).
    public void ClearFlashes()
    {
        foreach (var f in Frames)
            foreach (var c in f.Cells)
                c.Flash = false;
    }

    public void Apply(SceneDelta delta)
    {
        var inverses = new List<Action>(delta.Ops.Count);
        foreach (var op in delta.Ops)
            inverses.Add(ApplyOp(op));
        inverses.Reverse(); // undo in reverse order of application
        _undo.Push(() => { foreach (var inv in inverses) inv(); });
    }

    public void StepBack()
    {
        if (_undo.Count > 0) _undo.Pop().Invoke();
    }

    private Action ApplyOp(SceneOp op) => op switch
    {
        FramePush p => DoFramePush(p),
        FramePop p => DoFramePop(p),
        MemCellSet p => DoMemCellSet(p),
        MemCellFlash p => DoMemCellFlash(p),
        HeapAlloc p => DoHeapAlloc(p),
        FieldSet p => DoFieldSet(p),
        RefSet p => DoRefSet(p),
        LineHighlight p => DoLineHighlight(p),
        _ => throw new NotSupportedException($"Phase 2 does not handle {op.GetType().Name}.")
    };

    private FrameView RequireFrame(string id) =>
        Frames.FirstOrDefault(f => f.Id == id)
        ?? throw new InvalidOperationException($"No frame '{id}'.");

    private Action DoFramePush(FramePush p)
    {
        var frame = new FrameView { Id = p.FrameId, MethodSig = p.MethodSig };
        Frames.Add(frame);
        return () => Frames.Remove(frame);
    }

    private Action DoFramePop(FramePop p)
    {
        var frame = RequireFrame(p.FrameId);
        var index = Frames.IndexOf(frame);
        Frames.RemoveAt(index);
        return () => Frames.Insert(index, frame); // restore whole frame with its cells
    }

    private Action DoMemCellSet(MemCellSet p)
    {
        var frame = RequireFrame(p.FrameId);
        var cell = frame.Cell(p.Name);
        if (cell is null)
        {
            var added = new CellView { Name = p.Name, Type = p.Type, Value = p.Value, Flash = true };
            frame.Cells.Add(added);
            return () => frame.Cells.Remove(added);
        }
        var (oldType, oldVal, oldRef, oldFlash) = (cell.Type, cell.Value, cell.TargetObjId, cell.Flash);
        cell.Type = p.Type; cell.Value = p.Value; cell.TargetObjId = null; cell.Flash = true;
        return () => { cell.Type = oldType; cell.Value = oldVal; cell.TargetObjId = oldRef; cell.Flash = oldFlash; };
    }

    private Action DoMemCellFlash(MemCellFlash p)
    {
        var cell = RequireFrame(p.FrameId).Cell(p.Name)
                   ?? throw new InvalidOperationException($"No cell '{p.Name}' in '{p.FrameId}'.");
        var old = cell.Flash; cell.Flash = true;
        return () => cell.Flash = old;
    }

    private Action DoHeapAlloc(HeapAlloc p)
    {
        var obj = new HeapObjectView { Id = p.ObjId, ClassName = p.ClassName };
        obj.Fields.AddRange(p.Fields);
        Heap[p.ObjId] = obj;
        return () => Heap.Remove(p.ObjId);
    }

    private Action DoFieldSet(FieldSet p)
    {
        if (!Heap.TryGetValue(p.ObjId, out var obj))
            throw new InvalidOperationException($"No heap object '{p.ObjId}'.");
        var i = obj.Fields.FindIndex(kv => kv.Key == p.Field);
        if (i < 0)
        {
            obj.Fields.Add(new(p.Field, p.Value));
            return () => obj.Fields.RemoveAll(kv => kv.Key == p.Field);
        }
        var old = obj.Fields[i];
        obj.Fields[i] = new(p.Field, p.Value);
        return () => obj.Fields[i] = old;
    }

    private Action DoRefSet(RefSet p)
    {
        var frame = RequireFrame(p.FrameId);
        var cell = frame.Cell(p.VarName);
        if (cell is null)
        {
            var added = new CellView { Name = p.VarName, Type = "ref", TargetObjId = p.TargetObjId, Flash = true };
            frame.Cells.Add(added);
            return () => frame.Cells.Remove(added);
        }
        var (oldRef, oldVal, oldFlash) = (cell.TargetObjId, cell.Value, cell.Flash);
        cell.TargetObjId = p.TargetObjId; cell.Value = p.TargetObjId is null ? "null" : ""; cell.Flash = true;
        return () => { cell.TargetObjId = oldRef; cell.Value = oldVal; cell.Flash = oldFlash; };
    }

    private Action DoLineHighlight(LineHighlight p)
    {
        var old = HighlightLine; HighlightLine = p.Line;
        return () => HighlightLine = old;
    }
}
