// AP Tutor — Phase 3: Java-subset tracer (skeleton / public surface).
// Target: .NET 8, C# 12, nullable enabled.
// Pipeline:  source ──ANTLR──▶ parse tree ──▶ AST ──▶ SubsetValidator ──▶ Interpreter ──▶ TraceStep[]
// The interpreter emits a TraceStep at each meaningful event; each step carries the
// SceneDelta (from ApTutor.Scene) that advances the visualization. Do NOT hand-roll a Java
// parser — generate one from the community ANTLR4 Java grammar and restrict at the AST layer.
//
// This file is the shape Claude Code fills in. Method bodies marked TODO are the phase-3 work.

using ApTutor.Scene; // SceneOp, SceneDelta

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
        // TODO(1): parse with ANTLR-generated Java parser -> CompilationUnit AST.
        // TODO(2): _validator.Validate(ast) -> reject/flag unsupported constructs (see subset below).
        // TODO(3): new Interpreter(ast, emitter, randomSeed).Run(entryMethod).
        // Return emitter.Result(). On UnsupportedConstruct/JavaThrow, stop and set HaltReason.
        throw new NotImplementedException();
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
        // TODO: visitor that throws UnsupportedConstructException on the first banned node.
        // Prefer failing loud here over a wrong animation later.
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
