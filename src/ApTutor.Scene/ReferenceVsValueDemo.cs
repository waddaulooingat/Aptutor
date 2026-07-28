// PHASE2-HANDOFF.md "Reference-vs-value demo fixture (the money shot)". Encoded once here so
// both the fixture tests and the shell's step-through demo consume the exact same deltas.
//
// java:
//   int x = 5;             // step 0
//   Point p = new Point(); // step 1
//   p.setX(5);             // step 2
//   Point q = p;           // step 3  (q aliases p — same object)
//   q.setX(9);             // step 4  (x stays 5; p.x becomes 9 through the alias)
//
// Step 0 also carries the FramePush the handoff doc's delta list omits — SceneState.DoMemCellSet
// requires the frame to already exist, so "execution enters main" has to push it before setting x.

namespace ApTutor.Scene;

public static class ReferenceVsValueDemo
{
    public const string Frame = "main";

    public static IReadOnlyList<(string Caption, SceneDelta Delta)> Steps { get; } = new (string, SceneDelta)[]
    {
        ("int x = 5;", new SceneDelta(new SceneOp[]
        {
            new FramePush(Frame, "main"),
            new LineHighlight(1),
            new MemCellSet(Frame, "x", "int", "5"),
        })),
        ("Point p = new Point();", new SceneDelta(new SceneOp[]
        {
            new LineHighlight(2),
            new HeapAlloc("1", "Point", new[] { new KeyValuePair<string, string>("x", "0") }),
            new RefSet(Frame, "p", "1"),
        })),
        ("p.setX(5);", new SceneDelta(new SceneOp[]
        {
            new LineHighlight(3),
            new FieldSet("1", "x", "5"),
            new MemCellFlash(Frame, "p"),
        })),
        ("Point q = p;", new SceneDelta(new SceneOp[]
        {
            new LineHighlight(4),
            new RefSet(Frame, "q", "1"),
        })),
        ("q.setX(9);", new SceneDelta(new SceneOp[]
        {
            new LineHighlight(5),
            new FieldSet("1", "x", "9"),
        })),
    };
}
