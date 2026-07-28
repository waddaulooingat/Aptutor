// AP Tutor — Phase 3: Java-subset tracer (skeleton / public surface).
// Target: .NET 8, C# 12, nullable enabled.
// Pipeline:  source ──ANTLR──▶ parse tree ──▶ AST ──▶ SubsetValidator ──▶ Interpreter ──▶ TraceStep[]
// The interpreter emits a TraceStep at each meaningful event; each step carries the
// SceneDelta (from ApTutor.Scene) that advances the visualization. Do NOT hand-roll a Java
// parser — generate one from the community ANTLR4 Java grammar and restrict at the AST layer.
//
// This file is the shape Claude Code fills in. Method bodies marked TODO are the phase-3 work.

using ApTutor.Scene; // SceneOp, SceneDelta
using Antlr4.Runtime;

namespace ApTutor.Tracer;

// ---------- Public output (mirrors CLAUDE-HANDOFF §4, now concrete) ----------

public enum StepKind { Declare, Assign, Eval, Branch, LoopIter, Call, Return, ArrayWrite, FieldWrite, Alloc, Throw, Unsupported }

public sealed record TraceStep(
    int Index,
    int SourceLine,
    StepKind Kind,
    string Caption,     // one line, also fed to TTS
    SceneDelta Delta);

public sealed record TraceResult(
    IReadOnlyList<TraceStep> Steps,
    bool Completed,               // false if halted on Unsupported/Throw
    string? HaltReason);

// ---------- Facade ----------

public sealed class Tracer
{
    private readonly ISubsetValidator _validator;

    public Tracer(ISubsetValidator? validator = null)
        => _validator = validator ?? new CedSubsetValidator();

    /// Entry point: trace `entryMethod` (default main) in the given Java source.
    public TraceResult Trace(string javaSource, string entryMethod = "main", int? randomSeed = 42)
    {
        var lexer = new JavaLexer(new AntlrInputStream(javaSource));
        var errors = new CollectingErrorListener();
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);

        var parser = new JavaParser(new CommonTokenStream(lexer));
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        var tree = parser.compilationUnit();
        if (errors.FirstMessage is { } syntaxError)
            return new TraceResult(Array.Empty<TraceStep>(), false, $"Syntax error: {syntaxError}");

        try
        {
            _validator.Validate(tree);
        }
        catch (UnsupportedConstructException ex)
        {
            return new TraceResult(Array.Empty<TraceStep>(), false,
                $"Unsupported construct '{ex.Construct}' at line {ex.Line}.");
        }

        return new Interpreter(randomSeed).Run(tree, entryMethod);
    }

    // Implements both: Lexer.AddErrorListener needs IAntlrErrorListener&lt;int&gt;,
    // Parser.AddErrorListener needs IAntlrErrorListener&lt;IToken&gt; (what BaseErrorListener
    // alone provides).
    private sealed class CollectingErrorListener : IAntlrErrorListener<int>, IAntlrErrorListener<IToken>
    {
        public string? FirstMessage { get; private set; }

        public void SyntaxError(
            TextWriter output, IRecognizer recognizer, int offendingSymbol, int line, int charPositionInLine,
            string msg, RecognitionException e)
            => FirstMessage ??= $"line {line}:{charPositionInLine} {msg}";

        public void SyntaxError(
            TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine,
            string msg, RecognitionException e)
            => FirstMessage ??= $"line {line}:{charPositionInLine} {msg}";
    }
}

// ---------- Subset boundary (hard limit; CLAUDE-HANDOFF §4) ----------

public interface ISubsetValidator
{
    /// Walk the AST; throw UnsupportedConstructException at the first out-of-subset node.
    void Validate(object compilationUnitAst);
}

public sealed class UnsupportedConstructException(string construct, int line)
    : Exception($"Unsupported construct '{construct}' at line {line}.")
{
    public string Construct { get; } = construct;
    public int Line { get; } = line;
}

/// In scope: primitives; String, Math, Integer/Double; if/else/while/for/for-each;
/// user classes (fields, constructors, methods, static); 1D/2D arrays; ArrayList<E>;
/// single inheritance + super/@Override; recursion.
/// Out of scope: threads, generics beyond ArrayList<E>, lambdas, streams, interfaces
/// beyond Comparable, try/catch (tracer raises unchecked itself), switch, enhanced switch.
public sealed class CedSubsetValidator : ISubsetValidator
{
    public void Validate(object compilationUnitAst)
    {
        if (compilationUnitAst is not JavaParser.CompilationUnitContext tree)
            throw new ArgumentException(
                $"Expected a {nameof(JavaParser.CompilationUnitContext)}.", nameof(compilationUnitAst));
        new Walker().Visit(tree);
    }

    // Parses full Java via the community grammar, then rejects anything outside Units 1-5's
    // first cut here — restricting at a validator (not the grammar) gives clean, line-numbered
    // "unsupported construct" errors instead of a parse failure (PHASE3-HANDOFF §"two open
    // decisions"). Array *types* are deliberately left unrejected (see VisitSquareBracketExpression
    // / VisitArrayCreatorRest below) so `String[] args` on `main` parses fine even though nothing
    // in this cut can actually allocate or index an array.
    private sealed class Walker : JavaParserBaseVisitor<object?>
    {
        public override object? VisitInterfaceDeclaration(JavaParser.InterfaceDeclarationContext context) =>
            throw new UnsupportedConstructException("interface", context.Start.Line);

        public override object? VisitEnumDeclaration(JavaParser.EnumDeclarationContext context) =>
            throw new UnsupportedConstructException("enum", context.Start.Line);

        public override object? VisitRecordDeclaration(JavaParser.RecordDeclarationContext context) =>
            throw new UnsupportedConstructException("record", context.Start.Line);

        public override object? VisitAnnotationTypeDeclaration(JavaParser.AnnotationTypeDeclarationContext context) =>
            throw new UnsupportedConstructException("annotation type", context.Start.Line);

        public override object? VisitModuleDeclaration(JavaParser.ModuleDeclarationContext context) =>
            throw new UnsupportedConstructException("module", context.Start.Line);

        public override object? VisitClassDeclaration(JavaParser.ClassDeclarationContext context)
        {
            // Phase 5: single inheritance (extends) is now in scope for Units 6-10. Interfaces
            // stay out ("interfaces beyond Comparable" per CLAUDE-HANDOFF §4) - no interface
            // method dispatch is implemented.
            if (context.IMPLEMENTS() != null)
                throw new UnsupportedConstructException("interfaces", context.Start.Line);
            return base.VisitClassDeclaration(context);
        }

        public override object? VisitLambdaExpression(JavaParser.LambdaExpressionContext context) =>
            throw new UnsupportedConstructException("lambda expression", context.Start.Line);

        public override object? VisitMethodReferenceExpression(JavaParser.MethodReferenceExpressionContext context) =>
            throw new UnsupportedConstructException("method reference", context.Start.Line);

        public override object? VisitInstanceOfOperatorExpression(JavaParser.InstanceOfOperatorExpressionContext context) =>
            throw new UnsupportedConstructException("instanceof / pattern matching", context.Start.Line);

        public override object? VisitExpressionSwitch(JavaParser.ExpressionSwitchContext context) =>
            throw new UnsupportedConstructException("switch expression", context.Start.Line);

        public override object? VisitSwitchExpression(JavaParser.SwitchExpressionContext context) =>
            throw new UnsupportedConstructException("switch expression", context.Start.Line);

        // Phase 5: 1D/2D arrays and ArrayList<E> are now in scope (indexedStrip/grid2d
        // primitives exist). Array access/creation/initializers and type arguments are no
        // longer rejected here — the interpreter only actually implements rectangular 2D
        // arrays and ArrayList's add/get/set/size, so anything beyond that (jagged arrays,
        // other generic collection types) fails at interpretation time with a clean
        // JavaRuntimeException rather than a validator-level rejection.

        public override object? VisitStatement(JavaParser.StatementContext context)
        {
            if (context.TRY() != null) throw new UnsupportedConstructException("try/catch", context.Start.Line);
            if (context.SWITCH() != null) throw new UnsupportedConstructException("switch", context.Start.Line);
            if (context.SYNCHRONIZED() != null) throw new UnsupportedConstructException("synchronized", context.Start.Line);
            if (context.THROW() != null) throw new UnsupportedConstructException("throw", context.Start.Line);
            if (context.ASSERT() != null) throw new UnsupportedConstructException("assert", context.Start.Line);
            return base.VisitStatement(context);
        }
    }
}

// ---------- Runtime value model ----------

public abstract record JValue;
public sealed record JInt(int V) : JValue;
public sealed record JDouble(double V) : JValue;
public sealed record JBool(bool V) : JValue;
public sealed record JChar(char V) : JValue;
public sealed record JString(string V) : JValue;         // treated as a cell value in first cut
public sealed record JRef(string? ObjId) : JValue;       // null ObjId => null reference
public sealed record JArrayRef(string ArrId) : JValue;    // 1D array or ArrayList<E>, backs indexedStrip
public sealed record JGridRef(string GridId) : JValue;    // rectangular 2D array, backs grid2d

// ---------- Heap ----------

public sealed class HeapObject
{
    public required string Id { get; init; }
    public required string ClassName { get; init; }
    public Dictionary<string, JValue> Fields { get; } = new(StringComparer.Ordinal);
}

public sealed class Heap
{
    private int _next = 1;
    private readonly Dictionary<string, HeapObject> _objs = new(StringComparer.Ordinal);

    /// Sequential ids in allocation order: "1","2",... (kept stable so traces match fixtures).
    public HeapObject Alloc(string className)
    {
        var obj = new HeapObject { Id = _next++.ToString(), ClassName = className };
        _objs[obj.Id] = obj;
        return obj;
    }

    public HeapObject Get(string id) => _objs[id];
}

// ---------- Environment (call stack + block scopes) ----------

public sealed class Frame
{
    public required string Id { get; init; }         // "main", "f#1", ... (drives FramePush/Pop)
    public required string MethodSig { get; init; }

    /// The class whose method/constructor body this frame is executing — as opposed to the
    /// receiver's runtime class, which may be a subclass. Used to resolve `super.method()` calls
    /// (Phase 5 inheritance) starting one level above where the *current* method was defined,
    /// not one level above the receiver's own class.
    public string? DefiningClass { get; init; }

    private readonly List<Dictionary<string, JValue>> _scopes = new() { new(StringComparer.Ordinal) };

    public void PushScope() => _scopes.Add(new(StringComparer.Ordinal));
    public void PopScope() => _scopes.RemoveAt(_scopes.Count - 1);

    public void Declare(string name, JValue v) => _scopes[^1][name] = v;
    public bool TryGet(string name, out JValue v)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].TryGetValue(name, out v!)) return true;
        v = default!; return false;
    }
    public bool Assign(string name, JValue v)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            if (_scopes[i].ContainsKey(name)) { _scopes[i][name] = v; return true; }
        return false;
    }

    /// Names of currently-visible variables whose value is a reference to the given heap object —
    /// used to flash a caller's variable when a mutation reaches it through a method call
    /// (PHASE3-HANDOFF's field-write step: "MemCellFlash on the receiver var if visible").
    public IEnumerable<string> NamesReferencing(string objId)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            foreach (var (name, value) in _scopes[i])
                if (value is JRef { ObjId: { } oid } && oid == objId)
                    yield return name;
    }
}

// ---------- Emitter: interpreter calls these at mutation points ----------

public sealed class TraceEmitter
{
    private readonly List<TraceStep> _steps = new();
    private int _index;

    public void Emit(int line, StepKind kind, string caption, params SceneOp[] ops)
        => _steps.Add(new TraceStep(_index++, line, kind, caption, new SceneDelta(ops)));

    public TraceResult Result(bool completed, string? haltReason)
        => new(_steps, completed, haltReason);
}

// The Interpreter (not shown) is the bulk of phase 3. Its job is to walk the AST and, at each
// mutation point, call emitter.Emit(...) with the SceneOps below. See PHASE3-HANDOFF.md for the
// full event -> SceneOp emission map and the frame-id / heap-id conventions.
