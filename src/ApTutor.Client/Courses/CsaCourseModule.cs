// Composition-root wiring for the CS A course. This is the one place allowed to know "CS A"
// exists — the shell (MainWindow) talks only to ICourseModule/CourseRegistry (build-plan Phase 1
// acceptance: "no course-specific code in the shell"). StepProvider is wired to the real Phase 3
// tracer as of Phase 4. Content is a composite of the Phase 6 fixture bank (guaranteed to exist,
// so the mock exam never regresses) plus Phase 7's file-based authored content — empty today since
// nobody has run ApTutor.ContentFactory yet, but the wiring is real and ready for when they do.

using ApTutor.Content;
using ApTutor.Curriculum;
using ApTutor.Platform;
using ApTutor.Tracer;

namespace ApTutor.Client.Courses;

public sealed class CsaCourseModule : ICourseModule
{
    public string CourseId => "csa";
    public string DisplayName => "Computer Science A";
    public SkillGraph Dag { get; }

    public IReadOnlyList<IScenePrimitiveRenderer> Primitives => Array.Empty<IScenePrimitiveRenderer>();
    public IStepProvider StepProvider { get; }
    public IContentSource Content { get; }
    public IAttemptGrader? Grader => null;

    /// contentDir defaults to "<app dir>/content/csa" — where a human running
    /// `ApTutor.ContentFactory generate --course csa ...` then `review` would produce verified
    /// "<nodeId>.json" packs. Overridable so tests can point at an isolated fixture directory.
    public CsaCourseModule(string dagJsonPath, string? contentDir = null)
    {
        Dag = SkillDagLoader.Load(dagJsonPath);
        var dir = contentDir ?? Path.Combine(AppContext.BaseDirectory, "content", "csa");
        StepProvider = new TracerStepProvider(dir);
        Content = new CompositeContentSource(new FixtureContentSource(), new FileContentSource(dir));
    }
}

/// build-plan.md Phase 3: "wire it as the CS A module's IStepProvider with SupportsLiveInput =
/// true." GetStepsForInput traces arbitrary Java live; GetSteps serves the reference-vs-value demo
/// on u2.1 directly (it IS the tracer's own closing-loop fixture, see TracerTests), and falls back
/// to Phase 7's authored content pack for every other node.
file sealed class TracerStepProvider : IStepProvider
{
    private readonly IStepProvider _authored;

    public TracerStepProvider(string contentDir) => _authored = new AuthoredStepProvider(contentDir);

    public bool SupportsLiveInput => true;

    public IReadOnlyList<VisualStep> GetSteps(string nodeId, string exampleId)
    {
        if (nodeId == "u2.1") return Trace(SampleProgram.ReferenceVsValueDemoJava);

        try
        {
            return _authored.GetSteps(nodeId, exampleId);
        }
        catch (InvalidOperationException ex)
        {
            throw new NotImplementedException(
                $"No authored walkthrough for node '{nodeId}' yet — generate + verify it via ApTutor.ContentFactory.", ex);
        }
    }

    public IReadOnlyList<VisualStep> GetStepsForInput(string nodeId, string userInput) => Trace(userInput);

    private static IReadOnlyList<VisualStep> Trace(string javaSource)
    {
        var result = new Tracer.Tracer().Trace(javaSource);
        if (!result.Completed)
            throw new InvalidOperationException(result.HaltReason ?? "Tracing failed.");
        return result.Steps.Select(s => new VisualStep(s.Index, s.Caption, s.Delta, s.SourceLine)).ToList();
    }
}

/// Merges the Phase 6 fixture bank with Phase 7's file-based authored content: practice items are
/// the union of both sources; walkthrough text prefers the authored (verified, human-reviewed)
/// version and only falls back to the fixture source's stub message if none exists yet.
file sealed class CompositeContentSource : IContentSource
{
    private readonly IContentSource _fixture;
    private readonly IContentSource _authored;

    public CompositeContentSource(IContentSource fixture, IContentSource authored)
    {
        _fixture = fixture;
        _authored = authored;
    }

    public string GetWalkthroughText(string nodeId, string exampleId)
    {
        try
        {
            return _authored.GetWalkthroughText(nodeId, exampleId);
        }
        catch (InvalidOperationException)
        {
            return _fixture.GetWalkthroughText(nodeId, exampleId);
        }
    }

    public IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId) =>
        _fixture.GetPracticeItems(nodeId).Concat(_authored.GetPracticeItems(nodeId)).ToList();
}

/// Hand-authored MCQ fixture bank (Phase 6) — guaranteed to exist regardless of whether anyone has
/// run the Phase 7 content factory yet, so the mock exam always has real, gradable items. All items
/// are original — no College Board question text.
file sealed class FixtureContentSource : IContentSource
{
    private static readonly IReadOnlyList<PracticeItem> Items = new List<PracticeItem>
    {
        new("u1.1-q1", "u1.1", "Which best describes what happens when you run a compiled Java program?",
            new[] { "The .java source is re-parsed line by line as it executes",
                    "The JVM interprets/JIT-compiles bytecode produced by javac from the .java source",
                    "The operating system executes the .java file directly",
                    "The program is translated to machine code by a web browser" },
            1, "javac compiles .java source to .class bytecode; the JVM then runs that bytecode — that indirection is what makes Java \"write once, run anywhere.\""),
        new("u1.1-q2", "u1.1", "What is the required name of the file containing a public class Main?",
            new[] { "main.java", "Main.txt", "Main.java", "any name ending in .class" },
            2, "A top-level public class must live in a file whose name (case-sensitive) matches the class name exactly, plus the .java extension."),

        new("u1.2-q1", "u1.2", "Which declaration is valid Java?",
            new[] { "int x = 3.5;", "boolean flag = 1;", "double d = 7;", "int 2nd = 5;" },
            2, "An int literal widens to double automatically. The others fail: 3.5 doesn't narrow to int, boolean isn't 1/0 in Java, and identifiers can't start with a digit."),
        new("u1.2-q2", "u1.2", "What is the default value of an uninitialized boolean instance field?",
            new[] { "true", "false", "0", "null" },
            1, "Instance fields get type defaults if not explicitly initialized; boolean defaults to false (not 0 — Java booleans aren't numbers)."),

        new("u1.3-q1", "u1.3", "What does 7 / 2 evaluate to in Java?",
            new[] { "3.5", "3", "4", "3.0" },
            1, "Both operands are int, so / is integer division: it truncates toward zero, giving 3."),
        new("u1.3-q2", "u1.3", "What is the value of x after: int x = 2 + 3 * 4;",
            new[] { "20", "14", "24", "9" },
            1, "* binds tighter than +, so this is 2 + (3 * 4) = 2 + 12 = 14."),

        new("u2.1-q1", "u2.1", "After Point p = new Point(); Point q = p; q.x = 9; what is p.x?",
            new[] { "0 (unchanged)", "9", "A compile error", "null" },
            1, "p and q are both references to the SAME object, so a mutation through q is visible through p too — that's the reference-vs-value distinction."),
        new("u2.1-q2", "u2.1", "Which of these is a primitive type, not a reference type?",
            new[] { "String", "int[]", "double", "Object" },
            2, "double (and the other 7 primitives) store their value directly; everything else in the list is a reference to a heap object."),

        new("u2.2-q1", "u2.2", "What does `Point p;` (a local variable, never assigned) contain before first use?",
            new[] { "null, ready to use", "A default Point object", "Nothing usable — the compiler rejects reading it unassigned", "0" },
            2, "Unlike instance fields, local variables have no default value; the compiler flags \"might not have been initialized\" if you read one before assigning it."),
        new("u2.2-q2", "u2.2", "What is `p` after `Point p = new Point();` if Point has no declared constructor?",
            new[] { "A compile error — Point needs an explicit constructor", "A reference to a new Point built by the compiler-supplied no-arg constructor", "null", "An uninitialized Point" },
            1, "Java supplies a public no-arg constructor automatically when a class declares no constructor of its own."),
    };

    public string GetWalkthroughText(string nodeId, string exampleId) =>
        throw new NotImplementedException("Verified walkthrough text needs the Track B content factory (build-plan Phase 7).");

    public IReadOnlyList<PracticeItem> GetPracticeItems(string nodeId) =>
        Items.Where(i => i.NodeId == nodeId).ToList();
}
