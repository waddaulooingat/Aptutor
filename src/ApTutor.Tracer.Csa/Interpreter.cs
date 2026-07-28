// AP Tutor — Phase 3: the tree-walking interpreter. Walks the ANTLR parse tree directly (no
// separate custom AST layer — the parse tree already gives labeled-alternative context types
// that are a fine AST for our purposes) and calls TraceEmitter.Emit(...) at each mutation point
// per CLAUDE-HANDOFF §4 / PHASE3-HANDOFF's step-emission table.
//
// Scope: CED Units 1-5 (CLAUDE-HANDOFF §4 / PHASE3-HANDOFF "first-cut scope") — primitives and
// expressions, objects and method calls, String/Math/wrapper methods, if/else, while/for/do-while,
// user classes (fields, constructors, methods, static, this). CedSubsetValidator rejects syntax
// this class can't interpret before it ever runs.
//
// Phase 5 extends this to Units 6-10: 1D arrays (indexedStrip), rectangular 2D arrays (grid2d,
// deliberately NOT modeled as true Java jagged array-of-arrays — see EvalArrayOrGridAccess),
// ArrayList<E>'s add/get/set/size (sharing the indexedStrip backing with plain arrays), single
// inheritance (extends + super(...) chaining + super.method() + overriding via dynamic dispatch),
// and callTree emission for every method call (not just recursive ones — simpler, and harmless
// for non-recursive traces). Interfaces, jagged arrays, and ArrayList methods beyond the four
// above remain out of scope.

using System.Globalization;
using ApTutor.Scene;
using Antlr4.Runtime;

namespace ApTutor.Tracer;

internal sealed class JavaRuntimeException(string message) : Exception(message);

internal sealed class Interpreter
{
    private readonly Dictionary<string, JavaParser.ClassDeclarationContext> _classes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, JValue>> _staticFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _staticsInitialized = new(StringComparer.Ordinal);
    private readonly List<Frame> _callStack = new();
    private readonly Stack<string> _callTreeStack = new(); // top = enclosing call's callTree node id
    private readonly Heap _heap = new();
    private readonly Dictionary<string, List<JValue>> _arrayStore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JValue[,]> _gridStore = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _gridElementType = new(StringComparer.Ordinal);
    private readonly TraceEmitter _emitter = new();
    private readonly Random _random;
    private int _frameCounter;
    private int _arrayCounter;
    private int _gridCounter;
    private int _callTreeCounter;

    // FramePush for the current frame, held back until the next thing that happens in it so it
    // can ride along in the same TraceStep (mirrors PHASE2's fixture: step 0 bundles the entry
    // FramePush with "int x = 5;" rather than showing an empty frame as its own step).
    private SceneOp? _pendingFramePush;

    private static readonly HashSet<string> WrapperClassNames =
        new(StringComparer.Ordinal) { "Integer", "Double", "Boolean", "Character", "Long", "Short", "Byte", "Float" };

    public Interpreter(int? randomSeed) => _random = randomSeed is { } seed ? new Random(seed) : new Random();

    public TraceResult Run(JavaParser.CompilationUnitContext compilationUnit, string entryMethod)
    {
        foreach (var typeDecl in compilationUnit.typeDeclaration())
        {
            var classDecl = typeDecl.classDeclaration();
            if (classDecl != null)
                _classes[classDecl.identifier().GetText()] = classDecl;
        }

        JavaParser.MethodDeclarationContext? entryCtx = null;
        string? entryClass = null;
        foreach (var cls in _classes.Values)
        {
            entryCtx = FindMethod(cls, entryMethod, -1); // -1 = match by name only, any arity
            if (entryCtx != null) { entryClass = cls.identifier().GetText(); break; }
        }

        if (entryCtx is null)
            return _emitter.Result(false, $"Entry method '{entryMethod}' not found.");

        var frame = new Frame { Id = "main", MethodSig = entryMethod, DefiningClass = entryClass };
        _callStack.Add(frame);
        _pendingFramePush = new FramePush("main", entryMethod);

        try
        {
            var body = entryCtx.methodBody().block()
                       ?? throw new JavaRuntimeException($"Entry method '{entryMethod}' has no body.");
            var flow = ExecBlockStatements(body.blockStatement(), frame);
            FlushPendingFramePushAlone("main", entryCtx.Start.Line);
            _callStack.RemoveAt(_callStack.Count - 1);
            return _emitter.Result(true, null);
        }
        catch (UnsupportedConstructException ex)
        {
            return _emitter.Result(false, $"Unsupported construct '{ex.Construct}' at line {ex.Line}.");
        }
        catch (JavaRuntimeException ex)
        {
            return _emitter.Result(false, ex.Message);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException))
        {
            // Defensive net: an interpreter bug or a genuine Java-level runtime fault (NPE,
            // division by zero not already caught, etc.) should halt cleanly with a reason,
            // never crash Trace() itself (StepKind.Throw's contract).
            return _emitter.Result(false, $"Runtime error: {ex.Message}");
        }
    }

    // ---------- class / member lookup ----------

    /// formalParameters' grammar shape is `(receiverParameter|formalParameter) (',' formalParameterList)*`
    /// — the first parameter is its own singular accessor, the rest live inside (typically one)
    /// formalParameterList. Flatten both into one ordered list.
    private static IReadOnlyList<JavaParser.FormalParameterContext> AllParams(JavaParser.FormalParametersContext formals)
    {
        var list = new List<JavaParser.FormalParameterContext>();
        if (formals.formalParameter() != null) list.Add(formals.formalParameter());
        foreach (var fpl in formals.formalParameterList())
            list.AddRange(fpl.formalParameter());
        return list;
    }

    private static JavaParser.MethodDeclarationContext? FindMethod(
        JavaParser.ClassDeclarationContext cls, string name, int arity)
    {
        foreach (var bd in cls.classBody().classBodyDeclaration())
        {
            var m = bd.memberDeclaration()?.methodDeclaration();
            if (m is null) continue;
            if (!string.Equals(m.identifier().GetText(), name, StringComparison.Ordinal)) continue;
            var count = AllParams(m.formalParameters()).Count;
            if (arity < 0 || count == arity) return m;
        }
        return null;
    }

    private static bool IsStaticMember(JavaParser.ClassBodyDeclarationContext bd) =>
        bd.modifier().Any(m => m.classOrInterfaceModifier()?.STATIC() != null);

    private static string? ParentOf(JavaParser.ClassDeclarationContext cls) =>
        cls.EXTENDS() != null ? cls.typeType().GetText() : null;

    /// Walks the extends chain starting at `className`, returning the first (method, definingClass)
    /// match. Starting at the receiver's own runtime class and walking *up* only when not found is
    /// what makes overriding/dynamic dispatch fall out for free — no separate override tracking.
    private (JavaParser.MethodDeclarationContext Method, string DefiningClass)? FindMethodInChain(
        string className, string name, int arity)
    {
        var current = className;
        while (current != null && _classes.TryGetValue(current, out var cls))
        {
            var m = FindMethod(cls, name, arity);
            if (m != null) return (m, current);
            current = ParentOf(cls);
        }
        return null;
    }

    private static JavaParser.ConstructorDeclarationContext? FindConstructor(
        JavaParser.ClassDeclarationContext cls, int arity)
    {
        foreach (var bd in cls.classBody().classBodyDeclaration())
        {
            var c = bd.memberDeclaration()?.constructorDeclaration();
            if (c is null) continue;
            var count = AllParams(c.formalParameters()).Count;
            if (count == arity) return c;
        }
        return null;
    }

    private static IEnumerable<(string Name, string Type, JavaParser.ExpressionContext? Init)> InstanceFields(
        JavaParser.ClassDeclarationContext cls)
    {
        foreach (var bd in cls.classBody().classBodyDeclaration())
        {
            var f = bd.memberDeclaration()?.fieldDeclaration();
            if (f is null || IsStaticMember(bd)) continue;
            var typeName = f.typeType().GetText();
            foreach (var d in f.variableDeclarators().variableDeclarator())
                yield return (d.variableDeclaratorId().identifier().GetText(), typeName, d.variableInitializer()?.expression());
        }
    }

    /// Instance fields from the whole extends chain, root ancestor first, so a subclass's own
    /// fields are declared (and can shadow) after its parents' — matches Java's field layout order.
    private IEnumerable<(string Name, string Type, JavaParser.ExpressionContext? Init)> AllInstanceFields(string className)
    {
        if (!_classes.TryGetValue(className, out var cls)) yield break;
        if (ParentOf(cls) is { } parent)
            foreach (var f in AllInstanceFields(parent))
                yield return f;
        foreach (var f in InstanceFields(cls))
            yield return f;
    }

    private static IEnumerable<(string Name, string Type, JavaParser.ExpressionContext? Init)> StaticFieldDecls(
        JavaParser.ClassDeclarationContext cls)
    {
        foreach (var bd in cls.classBody().classBodyDeclaration())
        {
            var f = bd.memberDeclaration()?.fieldDeclaration();
            if (f is null || !IsStaticMember(bd)) continue;
            var typeName = f.typeType().GetText();
            foreach (var d in f.variableDeclarators().variableDeclarator())
                yield return (d.variableDeclaratorId().identifier().GetText(), typeName, d.variableInitializer()?.expression());
        }
    }

    private void EnsureStaticsInitialized(string className)
    {
        if (!_staticsInitialized.Add(className)) return;
        if (!_classes.TryGetValue(className, out var cls)) return;

        var scratch = new Frame { Id = "<static-init>", MethodSig = className };
        var fields = new Dictionary<string, JValue>(StringComparer.Ordinal);
        _staticFields[className] = fields;
        foreach (var (name, type, init) in StaticFieldDecls(cls))
            fields[name] = init != null ? EvalExpression(init, null, scratch) : DefaultFor(type);
    }

    // ---------- object construction (atomic: no per-field-write steps, see PHASE3-HANDOFF's
    //            "new T(...) -> HeapAlloc(id, T, fieldsAtInit)" — a single snapshot op) ----------

    private HeapObject Instantiate(string className, IReadOnlyList<JValue> args)
    {
        if (!_classes.TryGetValue(className, out _))
            throw new JavaRuntimeException($"Unknown class '{className}'.");

        var obj = _heap.Alloc(className);
        var scratch = new Frame { Id = $"<init-{obj.Id}>", MethodSig = className };
        foreach (var (name, type, init) in AllInstanceFields(className))
            obj.Fields[name] = init != null ? EvalExpression(init, null, scratch) : DefaultFor(type);

        RunConstructorChain(className, obj, args);
        return obj;
    }

    /// Runs `className`'s own constructor (if any) against `obj`, chaining to the parent's
    /// constructor first — explicitly via a leading `super(args);` statement, or implicitly
    /// (Java inserts a no-arg super() call when a constructor doesn't start with one). Constructor
    /// bodies never emit their own TraceSteps (see Instantiate's HeapAlloc-is-atomic contract);
    /// only the field mutations they cause show up, baked into that single snapshot.
    private void RunConstructorChain(string className, HeapObject obj, IReadOnlyList<JValue> args)
    {
        if (!_classes.TryGetValue(className, out var cls)) return;
        var parent = ParentOf(cls);
        var ctor = FindConstructor(cls, args.Count);

        if (ctor is null)
        {
            if (parent != null) RunConstructorChain(parent, obj, Array.Empty<JValue>());
            return;
        }

        var ctorFrame = new Frame { Id = $"ctor-{obj.Id}-{className}", MethodSig = className, DefiningClass = className };
        ctorFrame.Declare("this", new JRef(obj.Id));
        BindParameters(ctorFrame, ctor.formalParameters(), args);

        var statements = ctor.constructorBody.blockStatement();
        var startIndex = 0;

        if (statements.Length > 0 && statements[0].statement()?.statementExpression is JavaParser.MethodCallExpressionContext mce)
        {
            var mc = mce.methodCall();
            if (mc.SUPER() != null)
            {
                var superArgs = EvalArgs(mc.arguments(), obj, ctorFrame, silent: true);
                if (parent != null) RunConstructorChain(parent, obj, superArgs);
                startIndex = 1;
            }
            else if (mc.THIS() != null)
            {
                throw new JavaRuntimeException("this(...) constructor chaining is not supported.");
            }
        }

        if (startIndex == 0 && parent != null)
            RunConstructorChain(parent, obj, Array.Empty<JValue>()); // implicit super()

        _callStack.Add(ctorFrame);
        try
        {
            ExecBlockStatements(statements.Skip(startIndex).ToList(), ctorFrame, silent: true, selfObj: obj);
        }
        finally
        {
            _callStack.RemoveAt(_callStack.Count - 1);
        }
    }

    private static void BindParameters(Frame frame, JavaParser.FormalParametersContext formals, IReadOnlyList<JValue> args)
    {
        var list = AllParams(formals);
        for (var i = 0; i < list.Count && i < args.Count; i++)
            frame.Declare(list[i].variableDeclaratorId().identifier().GetText(), args[i]);
    }

    // ---------- statement execution ----------

    private enum Flow { Normal, Return, Break, Continue }

    private readonly struct ExecResult
    {
        public Flow Flow { get; init; }
        public JValue? ReturnValue { get; init; }
        public static readonly ExecResult Normal = new() { Flow = Flow.Normal };
        public static readonly ExecResult Break = new() { Flow = Flow.Break };
        public static readonly ExecResult Continue = new() { Flow = Flow.Continue };
        public static ExecResult Return(JValue? v) => new() { Flow = Flow.Return, ReturnValue = v };
    }

    /// <param name="silent">True while replaying a constructor body during object construction —
    /// field writes still update the heap object's dictionary directly, but emit no TraceSteps
    /// (their effect is captured in the single HeapAlloc snapshot instead).</param>
    /// <param name="selfObj">During construction, the object under construction — writes to "this"
    /// during a silent constructor run go straight to its Fields dict, bypassing FieldSet/Heap.Get
    /// (the object isn't fully in the heap's normal reachable state relative to other steps yet).</param>
    private ExecResult ExecBlockStatements(
        IReadOnlyList<JavaParser.BlockStatementContext> statements, Frame frame,
        bool silent = false, HeapObject? selfObj = null)
    {
        frame.PushScope();
        try
        {
            foreach (var bs in statements)
            {
                var result = ExecBlockStatement(bs, frame, silent, selfObj);
                if (result.Flow != Flow.Normal) return result;
            }
            return ExecResult.Normal;
        }
        finally
        {
            frame.PopScope();
        }
    }

    private ExecResult ExecBlockStatement(
        JavaParser.BlockStatementContext bs, Frame frame, bool silent, HeapObject? selfObj)
    {
        var localDecl = bs.localVariableDeclaration();
        if (localDecl != null)
        {
            ExecLocalVarDecl(localDecl, frame, silent, selfObj);
            return ExecResult.Normal;
        }

        var stmt = bs.statement();
        if (stmt != null) return ExecStatement(stmt, frame, silent, selfObj);

        return ExecResult.Normal; // localTypeDeclaration: nested types, not in our subset's demos
    }

    private void ExecLocalVarDecl(
        JavaParser.LocalVariableDeclarationContext decl, Frame frame, bool silent, HeapObject? selfObj)
    {
        if (decl.typeType() is null)
            throw new UnsupportedConstructException("var (local type inference)", decl.Start.Line);

        var typeName = decl.typeType().GetText();
        foreach (var d in decl.variableDeclarators().variableDeclarator())
        {
            var name = d.variableDeclaratorId().identifier().GetText();
            var line = d.Start.Line;
            if (d.variableInitializer() is null)
            {
                frame.Declare(name, DefaultFor(typeName));
                continue;
            }

            // `int[] a = {1, 2, 3};` — a bare arrayInitializer, not wrapped in `new T[]{...}`, so
            // variableInitializer.expression() is null here; handle it directly.
            var value = d.variableInitializer().arrayInitializer() is { } arrInit
                ? EvalArrayInitializer(arrInit, ArrayRank(typeName).ElementType, selfObj, frame, silent, line)
                : EvalExpression(d.variableInitializer().expression(), selfObj, frame);
            frame.Declare(name, value);
            if (silent) continue;

            EmitDeclareOrAssign(frame.Id, name, typeName, value, line, isDeclaration: true);
        }
    }

    private ExecResult ExecStatement(JavaParser.StatementContext ctx, Frame frame, bool silent, HeapObject? selfObj)
    {
        if (ctx.TRY() != null) throw new UnsupportedConstructException("try/catch", ctx.Start.Line);
        if (ctx.SWITCH() != null) throw new UnsupportedConstructException("switch", ctx.Start.Line);
        if (ctx.SYNCHRONIZED() != null) throw new UnsupportedConstructException("synchronized", ctx.Start.Line);
        if (ctx.THROW() != null) throw new UnsupportedConstructException("throw", ctx.Start.Line);
        if (ctx.ASSERT() != null) throw new UnsupportedConstructException("assert", ctx.Start.Line);

        if (ctx.block() is { } block)
            return ExecBlockStatements(block.blockStatement(), frame, silent, selfObj);

        if (ctx.IF() != null)
        {
            var cond = EvalExpression(ctx.expression(0), selfObj, frame);
            var taken = ((JBool)cond).V;
            var branch = taken ? ctx.statement(0) : ctx.statement(1);
            if (!silent) EmitBranch(frame.Id, branch?.Start.Line ?? ctx.Start.Line);
            return branch != null ? ExecStatement(branch, frame, silent, selfObj) : ExecResult.Normal;
        }

        if (ctx.WHILE() != null && ctx.DO() is null)
        {
            while (((JBool)EvalExpression(ctx.expression(0), selfObj, frame)).V)
            {
                var result = ExecStatement(ctx.statement(0), frame, silent, selfObj);
                if (result.Flow == Flow.Break) break;
                if (result.Flow is Flow.Return) return result;
            }
            return ExecResult.Normal;
        }

        if (ctx.DO() != null)
        {
            do
            {
                var result = ExecStatement(ctx.statement(0), frame, silent, selfObj);
                if (result.Flow == Flow.Break) break;
                if (result.Flow is Flow.Return) return result;
            } while (((JBool)EvalExpression(ctx.expression(0), selfObj, frame)).V);
            return ExecResult.Normal;
        }

        if (ctx.FOR() != null) return ExecFor(ctx, frame, silent, selfObj);

        if (ctx.RETURN() != null)
        {
            var value = ctx.expression().Length > 0 ? EvalExpression(ctx.expression(0), selfObj, frame) : null;
            return ExecResult.Return(value);
        }

        if (ctx.BREAK() != null) return ExecResult.Break;
        if (ctx.CONTINUE() != null) return ExecResult.Continue;

        if (ctx.statementExpression != null)
        {
            EvalExpression(ctx.statementExpression, selfObj, frame, silent);
            return ExecResult.Normal;
        }

        return ExecResult.Normal; // bare ';' or a labeled statement wrapper — no-op for our subset
    }

    private ExecResult ExecFor(JavaParser.StatementContext ctx, Frame frame, bool silent, HeapObject? selfObj)
    {
        var forControl = ctx.forControl();
        if (forControl.enhancedForControl() != null)
            return ExecForEach(ctx, forControl.enhancedForControl(), frame, silent, selfObj);

        frame.PushScope();
        try
        {
            var forInit = forControl.forInit();
            if (forInit?.localVariableDeclaration() is { } initDecl)
                ExecLocalVarDecl(initDecl, frame, silent, selfObj);
            else if (forInit?.expressionList() != null)
                foreach (var e in forInit.expressionList().expression())
                    EvalExpression(e, selfObj, frame, silent);

            var condExpr = forControl.expression();
            string? loopVarName = forInit?.localVariableDeclaration()?.variableDeclarators()
                .variableDeclarator()[0].variableDeclaratorId().identifier().GetText();

            while (condExpr is null || ((JBool)EvalExpression(condExpr, selfObj, frame)).V)
            {
                var result = ExecStatement(ctx.statement(0), frame, silent, selfObj);
                if (result.Flow == Flow.Break) break;
                if (result.Flow is Flow.Return) return result;

                if (forControl.forUpdate != null)
                    foreach (var e in forControl.forUpdate.expression())
                        EvalExpression(e, selfObj, frame, silent);

                if (!silent && loopVarName != null && frame.TryGet(loopVarName, out var v))
                    _emitter.Emit(ctx.Start.Line, StepKind.LoopIter, $"{loopVarName} = {Display(v)}",
                        FlushPending(new LineHighlight(ctx.Start.Line), new MemCellSet(frame.Id, loopVarName, "int", Display(v))));
            }
            return ExecResult.Normal;
        }
        finally
        {
            frame.PopScope();
        }
    }

    private ExecResult ExecForEach(
        JavaParser.StatementContext ctx, JavaParser.EnhancedForControlContext efc, Frame frame, bool silent, HeapObject? selfObj)
    {
        var collection = EvalExpression(efc.expression(), selfObj, frame, silent);
        if (collection is not JArrayRef { ArrId: var arrId })
            throw new JavaRuntimeException("for-each needs an array or ArrayList.");

        var varName = efc.variableDeclaratorId().identifier().GetText();
        var elementType = efc.typeType()?.GetText() ?? "var";

        frame.PushScope();
        try
        {
            // Snapshot the length: mutating the collection mid-iteration is undefined in Java too.
            var count = _arrayStore[arrId].Count;
            for (var i = 0; i < count; i++)
            {
                var element = _arrayStore[arrId][i];
                frame.Declare(varName, element);
                if (!silent)
                    _emitter.Emit(ctx.Start.Line, StepKind.LoopIter, $"{varName} = {Display(element)}",
                        FlushPending(new LineHighlight(ctx.Start.Line), new MemCellSet(frame.Id, varName, elementType, Display(element))));

                var result = ExecStatement(ctx.statement(0), frame, silent, selfObj);
                if (result.Flow == Flow.Break) break;
                if (result.Flow is Flow.Return) return result;
            }
            return ExecResult.Normal;
        }
        finally
        {
            frame.PopScope();
        }
    }

    // ---------- expression evaluation ----------

    private JValue EvalExpression(JavaParser.ExpressionContext ctx, HeapObject? self, Frame frame, bool silent = false)
    {
        switch (ctx)
        {
            case JavaParser.PrimaryExpressionContext p:
                return EvalPrimary(p.primary(), self, frame);

            case JavaParser.BinaryOperatorExpressionContext bop:
                return EvalBinaryOrAssign(bop, self, frame, silent);

            case JavaParser.UnaryOperatorExpressionContext u:
                return EvalUnary(u, self, frame, silent);

            case JavaParser.PostIncrementDecrementOperatorExpressionContext post:
                return EvalPostIncDec(post, self, frame, silent);

            case JavaParser.TernaryExpressionContext t:
                var cond = ((JBool)EvalExpression(t.expression(0), self, frame, silent)).V;
                return EvalExpression(t.expression(cond ? 1 : 2), self, frame, silent);

            case JavaParser.MethodCallExpressionContext mc:
                return EvalMethodCall(mc.methodCall(), self, frame, silent, ctx.Start.Line);

            case JavaParser.MemberReferenceExpressionContext mr:
                return EvalMemberReference(mr, self, frame, silent);

            case JavaParser.CastExpressionContext cast:
                return EvalCast(cast, self, frame, silent);

            case JavaParser.ObjectCreationExpressionContext oc:
                return EvalObjectCreation(oc, self, frame, silent);

            case JavaParser.SquareBracketExpressionContext sb:
                return EvalArrayOrGridAccess(sb, self, frame, silent);

            case JavaParser.ExpressionLambdaContext:
                throw new UnsupportedConstructException("lambda expression", ctx.Start.Line);

            case JavaParser.MethodReferenceExpressionContext:
                throw new UnsupportedConstructException("method reference", ctx.Start.Line);

            case JavaParser.InstanceOfOperatorExpressionContext:
                throw new UnsupportedConstructException("instanceof", ctx.Start.Line);

            case JavaParser.ExpressionSwitchContext:
                throw new UnsupportedConstructException("switch expression", ctx.Start.Line);

            default:
                throw new UnsupportedConstructException(ctx.GetType().Name, ctx.Start.Line);
        }
    }

    private JValue EvalPrimary(JavaParser.PrimaryContext p, HeapObject? self, Frame frame)
    {
        if (p.expression() != null) return EvalExpression(p.expression(), self, frame);
        if (p.literal() != null) return EvalLiteral(p.literal());
        // "super" is the same object as "this" at the value level — it only changes which class
        // method resolution starts from, handled at the call site (EvalMemberReference), not here.
        if (p.THIS() != null || p.SUPER() != null)
            return self != null ? new JRef(self.Id) : frame.TryGet("this", out var t) ? t : throw new JavaRuntimeException("'this'/'super' used outside an instance context.");

        if (p.identifier() != null)
        {
            var name = p.identifier().GetText();
            if (frame.TryGet(name, out var local)) return local;
            if (self != null && self.Fields.TryGetValue(name, out var field)) return field;
            if (frame.TryGet("this", out var thisRef) && thisRef is JRef { ObjId: { } oid } && _heap.Get(oid).Fields.TryGetValue(name, out var f2))
                return f2;
            throw new JavaRuntimeException($"Unresolved identifier '{name}'.");
        }

        throw new UnsupportedConstructException("primary expression", p.Start.Line);
    }

    private static JValue EvalLiteral(JavaParser.LiteralContext lit)
    {
        if (lit.integerLiteral() != null)
        {
            var text = lit.integerLiteral().GetText().TrimEnd('l', 'L').Replace("_", "");
            return new JInt(int.Parse(text, CultureInfo.InvariantCulture));
        }
        if (lit.floatLiteral() != null)
        {
            var text = lit.floatLiteral().GetText().TrimEnd('f', 'F', 'd', 'D').Replace("_", "");
            return new JDouble(double.Parse(text, CultureInfo.InvariantCulture));
        }
        if (lit.BOOL_LITERAL() != null) return new JBool(lit.BOOL_LITERAL().GetText() == "true");
        if (lit.CHAR_LITERAL() != null) return new JChar(Unescape(lit.CHAR_LITERAL().GetText().Trim('\''))[0]);
        if (lit.STRING_LITERAL() != null) return new JString(Unescape(StripQuotes(lit.STRING_LITERAL().GetText())));
        if (lit.NULL_LITERAL() != null) return new JRef(null);
        throw new UnsupportedConstructException("literal", lit.Start.Line);
    }

    private static string StripQuotes(string s) => s.Length >= 2 ? s.Substring(1, s.Length - 2) : s;

    private static string Unescape(string s) => s
        .Replace("\\n", "\n").Replace("\\t", "\t").Replace("\\r", "\r")
        .Replace("\\\"", "\"").Replace("\\'", "'").Replace("\\\\", "\\");

    private JValue EvalCast(JavaParser.CastExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var targetType = ctx.typeType(0).GetText();
        var value = EvalExpression(ctx.expression(), self, frame, silent);
        return targetType switch
        {
            "int" or "short" or "byte" or "long" => new JInt(value switch
            {
                JInt i => i.V, JDouble d => (int)d.V, JChar c => c.V, _ => throw new JavaRuntimeException($"Cannot cast to {targetType}.")
            }),
            "double" or "float" => new JDouble(value switch
            {
                JInt i => i.V, JDouble d => d.V, _ => throw new JavaRuntimeException($"Cannot cast to {targetType}.")
            }),
            "char" => new JChar(value is JInt ci ? (char)ci.V : ((JChar)value).V),
            _ => value, // reference-type cast: no representation change for our subset
        };
    }

    private static readonly HashSet<string> AssignOps =
        new(StringComparer.Ordinal) { "=", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<=", ">>=", ">>>=" };

    private JValue EvalBinaryOrAssign(JavaParser.BinaryOperatorExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var op = ctx.bop.Text;
        if (AssignOps.Contains(op))
        {
            var rhs = EvalExpression(ctx.expression(1), self, frame, silent);
            var value = op == "="
                ? rhs
                : Combine(op[..^1], EvalExpression(ctx.expression(0), self, frame, silent), rhs, ctx.Start.Line);
            AssignTo(ctx.expression(0), value, self, frame, silent, ctx.Start.Line);
            return value;
        }

        var left = EvalExpression(ctx.expression(0), self, frame, silent);
        var right = EvalExpression(ctx.expression(1), self, frame, silent);
        return Combine(op, left, right, ctx.Start.Line);
    }

    // Note on '==' over JString: real Java '==' on String is reference identity, a classic CS A
    // gotcha. Our subset represents String as a plain value (PHASE3-HANDOFF §"Conventions" — not
    // a heap object), so there is no identity to compare; value equality is the only coherent
    // reading of '==' for a value-typed String here.
    private static JValue Combine(string op, JValue left, JValue right, int line)
    {
        if (op == "+" && (left is JString || right is JString))
            return new JString(Display(left) + Display(right));

        switch (op)
        {
            case "&&": return new JBool(((JBool)left).V && ((JBool)right).V);
            case "||": return new JBool(((JBool)left).V || ((JBool)right).V);
            case "==": return new JBool(ValueEquals(left, right));
            case "!=": return new JBool(!ValueEquals(left, right));
        }

        if (left is JInt li && right is JInt ri)
        {
            return op switch
            {
                "+" => new JInt(li.V + ri.V),
                "-" => new JInt(li.V - ri.V),
                "*" => new JInt(li.V * ri.V),
                "/" => ri.V != 0 ? new JInt(li.V / ri.V) : throw new JavaRuntimeException("Division by zero."),
                "%" => ri.V != 0 ? new JInt(li.V % ri.V) : throw new JavaRuntimeException("Division by zero."),
                "<" => new JBool(li.V < ri.V),
                "<=" => new JBool(li.V <= ri.V),
                ">" => new JBool(li.V > ri.V),
                ">=" => new JBool(li.V >= ri.V),
                "&" => new JInt(li.V & ri.V),
                "|" => new JInt(li.V | ri.V),
                "^" => new JInt(li.V ^ ri.V),
                "<<" => new JInt(li.V << ri.V),
                ">>" => new JInt(li.V >> ri.V),
                _ => throw new UnsupportedConstructException($"operator '{op}'", line),
            };
        }

        double ld = ToDouble(left), rd = ToDouble(right);
        return op switch
        {
            "+" => new JDouble(ld + rd),
            "-" => new JDouble(ld - rd),
            "*" => new JDouble(ld * rd),
            "/" => new JDouble(ld / rd),
            "%" => new JDouble(ld % rd),
            "<" => new JBool(ld < rd),
            "<=" => new JBool(ld <= rd),
            ">" => new JBool(ld > rd),
            ">=" => new JBool(ld >= rd),
            _ => throw new UnsupportedConstructException($"operator '{op}'", line),
        };
    }

    private static double ToDouble(JValue v) => v switch
    {
        JInt i => i.V, JDouble d => d.V, JChar c => c.V,
        _ => throw new JavaRuntimeException("Expected a number."),
    };

    private static bool ValueEquals(JValue a, JValue b) => (a, b) switch
    {
        (JRef ra, JRef rb) => ra.ObjId == rb.ObjId,
        (JInt ia, JInt ib) => ia.V == ib.V,
        (JDouble da, JDouble db) => da.V.Equals(db.V),
        (JBool ba, JBool bb) => ba.V == bb.V,
        (JChar ca, JChar cb) => ca.V == cb.V,
        (JString sa, JString sb) => sa.V == sb.V,
        _ => false,
    };

    private JValue EvalUnary(JavaParser.UnaryOperatorExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var op = ctx.prefix.Text;
        if (op is "++" or "--")
        {
            var current = EvalExpression(ctx.expression(), self, frame, silent);
            var updated = Combine(op == "++" ? "+" : "-", current, new JInt(1), ctx.Start.Line);
            AssignTo(ctx.expression(), updated, self, frame, silent, ctx.Start.Line);
            return updated;
        }

        var value = EvalExpression(ctx.expression(), self, frame, silent);
        return op switch
        {
            "-" => value switch
            {
                JInt i => new JInt(-i.V), JDouble d => new JDouble(-d.V),
                _ => throw new JavaRuntimeException("Unary '-' needs a number."),
            },
            "+" => value,
            "!" => new JBool(!((JBool)value).V),
            "~" => new JInt(~((JInt)value).V),
            _ => throw new UnsupportedConstructException($"unary '{op}'", ctx.Start.Line),
        };
    }

    private JValue EvalPostIncDec(
        JavaParser.PostIncrementDecrementOperatorExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var current = EvalExpression(ctx.expression(), self, frame, silent);
        var updated = Combine(ctx.postfix.Text == "++" ? "+" : "-", current, new JInt(1), ctx.Start.Line);
        AssignTo(ctx.expression(), updated, self, frame, silent, ctx.Start.Line);
        return current; // postfix yields the PRE-increment value
    }

    // ---------- lvalues ----------

    private void AssignTo(JavaParser.ExpressionContext lhs, JValue value, HeapObject? self, Frame frame, bool silent, int line)
    {
        if (lhs is JavaParser.PrimaryExpressionContext { } primaryExpr && primaryExpr.primary().identifier() != null)
        {
            var name = primaryExpr.primary().identifier().GetText();
            if (frame.TryGet(name, out _))
            {
                frame.Assign(name, value);
                if (!silent) EmitDeclareOrAssign(frame.Id, name, null, value, line, isDeclaration: false);
                return;
            }

            if (self != null)
            {
                AssignField(self, name, value, silent, line);
                return;
            }

            throw new JavaRuntimeException($"Cannot assign to unresolved identifier '{name}'.");
        }

        if (lhs is JavaParser.MemberReferenceExpressionContext { } memberExpr && memberExpr.identifier() != null)
        {
            var receiver = EvalExpression(memberExpr.expression(), self, frame, silent);
            if (receiver is not JRef { ObjId: { } objId })
                throw new JavaRuntimeException("Cannot assign a field on a null receiver.");
            AssignField(_heap.Get(objId), memberExpr.identifier().GetText(), value, silent, line);
            return;
        }

        if (lhs is JavaParser.SquareBracketExpressionContext sbLhs)
        {
            if (sbLhs.expression(0) is JavaParser.SquareBracketExpressionContext innerLhs &&
                EvalExpression(innerLhs.expression(0), self, frame, silent) is JGridRef g)
            {
                var row = ((JInt)EvalExpression(innerLhs.expression(1), self, frame, silent)).V;
                var col = ((JInt)EvalExpression(sbLhs.expression(1), self, frame, silent)).V;
                WriteGrid(g.GridId, row, col, value, silent, line);
                return;
            }

            var arrBase = EvalExpression(sbLhs.expression(0), self, frame, silent);
            if (arrBase is JArrayRef a)
            {
                var index = ((JInt)EvalExpression(sbLhs.expression(1), self, frame, silent)).V;
                WriteArray(a.ArrId, index, value, silent, line);
                return;
            }

            throw new JavaRuntimeException("Index assignment needs an array or 2D array.");
        }

        throw new UnsupportedConstructException("assignment target", line);
    }

    // ---------- arrays / grids ----------

    /// `g[i][j]` is detected structurally (the base of the outer bracket is itself a bracket
    /// expression whose own base evaluates to a grid) rather than through generic bottom-up
    /// evaluation, because 2D arrays are modeled as a real Grid2dView, not Java's true
    /// array-of-arrays — see the file header note.
    private JValue EvalArrayOrGridAccess(JavaParser.SquareBracketExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var line = ctx.Start.Line;

        if (ctx.expression(0) is JavaParser.SquareBracketExpressionContext inner &&
            EvalExpression(inner.expression(0), self, frame, silent) is JGridRef g)
        {
            var row = ((JInt)EvalExpression(inner.expression(1), self, frame, silent)).V;
            var col = ((JInt)EvalExpression(ctx.expression(1), self, frame, silent)).V;
            return ReadGrid(g.GridId, row, col);
        }

        var baseValue = EvalExpression(ctx.expression(0), self, frame, silent);
        if (baseValue is JArrayRef arr)
        {
            var index = ((JInt)EvalExpression(ctx.expression(1), self, frame, silent)).V;
            return ReadArray(arr.ArrId, index);
        }

        throw new JavaRuntimeException("Index access needs an array or 2D array.");
    }

    private JValue ReadArray(string arrId, int index)
    {
        if (!_arrayStore.TryGetValue(arrId, out var list)) throw new JavaRuntimeException($"No array '{arrId}'.");
        if (index < 0 || index >= list.Count)
            throw new JavaRuntimeException($"Array index {index} out of bounds for length {list.Count}.");
        return list[index];
    }

    private JValue ReadGrid(string gridId, int row, int col)
    {
        if (!_gridStore.TryGetValue(gridId, out var grid)) throw new JavaRuntimeException($"No grid '{gridId}'.");
        if (row < 0 || row >= grid.GetLength(0) || col < 0 || col >= grid.GetLength(1))
            throw new JavaRuntimeException($"Grid index [{row}][{col}] out of bounds.");
        return grid[row, col];
    }

    private void WriteArray(string arrId, int index, JValue value, bool silent, int line)
    {
        if (!_arrayStore.TryGetValue(arrId, out var list)) throw new JavaRuntimeException($"No array '{arrId}'.");
        if (index < 0 || index >= list.Count)
            throw new JavaRuntimeException($"Array index {index} out of bounds for length {list.Count}.");
        list[index] = value;
        if (silent) return;
        _emitter.Emit(line, StepKind.ArrayWrite, $"[{index}] = {Display(value)}",
            FlushPending(new LineHighlight(line), new ArrayWrite(arrId, index, Display(value))));
    }

    /// Appends when `index == list.Count` (used by ArrayList.add) rather than requiring a
    /// dedicated append op — SceneState.DoArrayWrite mirrors this.
    private void AppendArray(string arrId, JValue value, bool silent, int line)
    {
        if (!_arrayStore.TryGetValue(arrId, out var list)) throw new JavaRuntimeException($"No array '{arrId}'.");
        var index = list.Count;
        list.Add(value);
        if (silent) return;
        _emitter.Emit(line, StepKind.ArrayWrite, $"add({Display(value)})",
            FlushPending(new LineHighlight(line), new ArrayWrite(arrId, index, Display(value))));
    }

    private void WriteGrid(string gridId, int row, int col, JValue value, bool silent, int line)
    {
        if (!_gridStore.TryGetValue(gridId, out var grid)) throw new JavaRuntimeException($"No grid '{gridId}'.");
        if (row < 0 || row >= grid.GetLength(0) || col < 0 || col >= grid.GetLength(1))
            throw new JavaRuntimeException($"Grid index [{row}][{col}] out of bounds.");
        grid[row, col] = value;
        if (silent) return;
        _emitter.Emit(line, StepKind.ArrayWrite, $"[{row}][{col}] = {Display(value)}",
            FlushPending(new LineHighlight(line), new Grid2dWrite(gridId, row, col, Display(value))));
    }

    private void AssignField(HeapObject obj, string field, JValue value, bool silent, int line)
    {
        obj.Fields[field] = value;
        if (silent) return;

        var ops = new List<SceneOp> { new LineHighlight(line), new FieldSet(obj.Id, field, Display(value)) };

        // "MemCellFlash on the receiver var if visible" (PHASE3-HANDOFF's field-write row) — a
        // mutation reached through a method call still highlights the variable the *caller* sees,
        // wherever in the active call stack it's visible. "this" is excluded: it's never given a
        // MemCellSet/RefSet of its own (see InvokeMethod), so no cell named "this" exists in the
        // scene for SceneState.DoMemCellFlash to find — flashing it would throw at apply time.
        foreach (var f in _callStack)
            foreach (var cell in f.NamesReferencing(obj.Id))
                if (cell != "this")
                    ops.Add(new MemCellFlash(f.Id, cell));

        _emitter.Emit(line, StepKind.FieldWrite, $"{field} = {Display(value)}", FlushPending(ops.ToArray()));
    }

    // ---------- method calls ----------

    private List<JValue> EvalArgs(JavaParser.ArgumentsContext args, HeapObject? self, Frame frame, bool silent)
    {
        var list = new List<JValue>();
        if (args.expressionList() != null)
            foreach (var e in args.expressionList().expression())
                list.Add(EvalExpression(e, self, frame, silent));
        return list;
    }

    /// Unqualified call — (identifier) arguments, implicit-this inside an instance method.
    private JValue EvalMethodCall(JavaParser.MethodCallContext mc, HeapObject? self, Frame frame, bool silent, int line)
    {
        if (mc.SUPER() != null) throw new UnsupportedConstructException("super(...) call", line);
        if (mc.THIS() != null) throw new UnsupportedConstructException("this(...) constructor chaining", line);

        var name = mc.identifier().GetText();
        var args = EvalArgs(mc.arguments(), self, frame, silent);

        // No receiver: either a static method calling another static method (including recursive
        // self-calls), or an instance method calling a sibling unqualified — either way, resolve
        // starting from wherever the *currently executing* method was defined, not the receiver.
        var searchStartClass = self?.ClassName ?? frame.DefiningClass
            ?? throw new JavaRuntimeException($"Cannot call '{name}(...)': no enclosing class context.");

        return InvokeMethod(self, searchStartClass, name, args, silent, line);
    }

    /// Qualified access — expr '.' (identifier | methodCall | ...): field reads, instance calls,
    /// and (detected by receiver name) static-style calls (Math.foo, String.valueOf, MyClass.bar).
    private JValue EvalMemberReference(JavaParser.MemberReferenceExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var line = ctx.Start.Line;

        // super.method(...): resolve starting one level above wherever the *currently executing*
        // method/constructor was defined (frame.DefiningClass), not above the receiver's own
        // runtime class — correct for multi-level hierarchies, not just parent/child.
        if (ctx.expression() is JavaParser.PrimaryExpressionContext { } superPe && superPe.primary().SUPER() != null)
        {
            if (self is null) throw new JavaRuntimeException("'super' used outside an instance context.");
            if (ctx.methodCall() is not { } superCall) throw new UnsupportedConstructException("super.field access", line);
            var definingClass = frame.DefiningClass ?? self.ClassName;
            var parent = (_classes.TryGetValue(definingClass, out var dc) ? ParentOf(dc) : null)
                         ?? throw new JavaRuntimeException($"'{definingClass}' has no superclass.");
            var superArgs = EvalArgs(superCall.arguments(), self, frame, silent);
            return InvokeMethod(self, parent, superCall.identifier().GetText(), superArgs, silent, line);
        }

        if (ctx.expression() is JavaParser.PrimaryExpressionContext pe && pe.primary().identifier() != null)
        {
            var receiverName = pe.primary().identifier().GetText();
            var shadowed = frame.TryGet(receiverName, out _) || (self?.Fields.ContainsKey(receiverName) ?? false);
            if (!shadowed && ctx.methodCall() != null)
            {
                var callName = ctx.methodCall().identifier().GetText();
                var callArgs = EvalArgs(ctx.methodCall().arguments(), self, frame, silent);

                if (receiverName == "Math") return EvalMathCall(callName, callArgs, line);
                if (receiverName is "Integer" or "Double" or "String") return EvalWrapperStaticCall(receiverName, callName, callArgs, line);
                if (_classes.ContainsKey(receiverName))
                {
                    EnsureStaticsInitialized(receiverName);
                    return InvokeMethod(null, receiverName, callName, callArgs, silent, line);
                }
            }
        }

        var receiverValue = EvalExpression(ctx.expression(), self, frame, silent);

        if (ctx.identifier() != null)
        {
            var fieldName = ctx.identifier().GetText();
            return receiverValue switch
            {
                JRef { ObjId: { } oid } => _heap.Get(oid).Fields[fieldName],
                JArrayRef { ArrId: var arrId } when fieldName == "length" => new JInt(_arrayStore[arrId].Count),
                _ => throw new JavaRuntimeException($"Cannot read field '{fieldName}'."),
            };
        }

        if (ctx.methodCall() != null)
        {
            var name = ctx.methodCall().identifier().GetText();
            var args = EvalArgs(ctx.methodCall().arguments(), self, frame, silent);
            return receiverValue switch
            {
                JString str => EvalStringInstanceCall(str, name, args, line),
                JArrayRef { ArrId: var arrId } => EvalArrayListInstanceCall(arrId, name, args, silent, line),
                JRef { ObjId: { } oid2 } => InvokeMethod(_heap.Get(oid2), _heap.Get(oid2).ClassName, name, args, silent, line),
                JRef { ObjId: null } => throw new JavaRuntimeException($"Cannot call '{name}(...)' on null."),
                _ => throw new JavaRuntimeException($"Cannot call '{name}(...)' on a non-object value."),
            };
        }

        throw new UnsupportedConstructException("member reference (new/generic invocation)", line);
    }

    /// ArrayList<E>'s core four (shares the indexedStrip-backed array store with plain arrays —
    /// nothing else distinguishes "is this really a List" at this representation, which is fine
    /// since we only ever reach here via an explicit method call). remove/contains/etc. are out
    /// of scope for this first cut.
    private JValue EvalArrayListInstanceCall(string arrId, string name, IReadOnlyList<JValue> args, bool silent, int line) => name switch
    {
        "add" => Void(() => AppendArray(arrId, args[0], silent, line)),
        "get" => ReadArray(arrId, ((JInt)args[0]).V),
        "set" => Void(() => WriteArray(arrId, ((JInt)args[0]).V, args[1], silent, line)),
        "size" => new JInt(_arrayStore[arrId].Count),
        _ => throw new UnsupportedConstructException($"ArrayList.{name}(...)", line),
    };

    private JValue Void(Action action) { action(); return VoidMarker; }

    private JValue InvokeMethod(HeapObject? receiver, string searchStartClass, string name, IReadOnlyList<JValue> args, bool silent, int line)
    {
        var found = FindMethodInChain(searchStartClass, name, args.Count)
                    ?? throw new JavaRuntimeException($"No method '{name}' with {args.Count} argument(s) found on '{searchStartClass}' or its superclasses.");
        var (method, definingClass) = found;

        var frameId = $"f#{++_frameCounter}";
        var sig = MethodSig(method);
        var newFrame = new Frame { Id = frameId, MethodSig = sig, DefiningClass = definingClass };
        if (receiver != null) newFrame.Declare("this", new JRef(receiver.Id));
        BindParameters(newFrame, method.formalParameters(), args);

        // callTree: every call gets a node, not just recursive ones — simpler, and harmless for
        // non-recursive traces (a single-node "tree" just never branches).
        var callTreeId = $"c{++_callTreeCounter}";
        var callerCallTreeId = _callTreeStack.Count > 0 ? _callTreeStack.Peek() : null;
        _callTreeStack.Push(callTreeId);

        if (!silent) EmitCallStep(newFrame, method.formalParameters(), args, line, sig, callTreeId, callerCallTreeId);

        _callStack.Add(newFrame);
        JValue? returnValue;
        try
        {
            var result = ExecBlockStatements(method.methodBody().block().blockStatement(), newFrame, silent, receiver);
            returnValue = result.Flow == Flow.Return ? result.ReturnValue : null;
        }
        finally
        {
            _callStack.RemoveAt(_callStack.Count - 1);
            _callTreeStack.Pop();
        }

        if (!silent)
        {
            // Always mark the call-tree node returned (even for void), or it would stay "active"
            // in the visualization forever despite the call having genuinely completed.
            var ops = new List<SceneOp>
            {
                new FramePop(frameId),
                new CallTreeReturn(callTreeId, returnValue != null ? Display(returnValue) : "void"),
            };
            _emitter.Emit(line, StepKind.Return, returnValue != null ? $"return {Display(returnValue)}" : "return", ops.ToArray());
        }

        return returnValue ?? VoidMarker;
    }

    private void EmitCallStep(
        Frame newFrame, JavaParser.FormalParametersContext formals, IReadOnlyList<JValue> args, int line, string sig,
        string callTreeId, string? callerCallTreeId)
    {
        var ops = new List<SceneOp> { new FramePush(newFrame.Id, sig), new CallTreeNode(callTreeId, callerCallTreeId, sig) };
        var paramList = AllParams(formals);
        for (var i = 0; i < paramList.Count && i < args.Count; i++)
        {
            var pname = paramList[i].variableDeclaratorId().identifier().GetText();
            var ptype = paramList[i].typeType().GetText();
            ops.Add(args[i] is JRef r ? new RefSet(newFrame.Id, pname, r.ObjId) : new MemCellSet(newFrame.Id, pname, ptype, Display(args[i])));
        }
        _emitter.Emit(line, StepKind.Call, $"call {sig}", FlushPending(ops.ToArray()));
    }

    private static string MethodSig(JavaParser.MethodDeclarationContext m)
    {
        var ret = m.typeTypeOrVoid().GetText();
        var name = m.identifier().GetText();
        var paramTypes = AllParams(m.formalParameters()).Select(p => p.typeType().GetText());
        return $"{ret} {name}({string.Join(", ", paramTypes)})";
    }

    // ---------- object creation ----------

    private JValue EvalObjectCreation(JavaParser.ObjectCreationExpressionContext ctx, HeapObject? self, Frame frame, bool silent)
    {
        var creator = ctx.creator();
        var line = ctx.Start.Line;

        if (creator.arrayCreatorRest() != null)
            return EvalArrayCreation(creator, self, frame, silent, line);

        if (creator.classCreatorRest()?.classBody() != null) throw new UnsupportedConstructException("anonymous class", line);
        if (creator.classCreatorRest() is null) throw new UnsupportedConstructException("object creation", line);

        var className = creator.createdName().identifier(0).GetText();
        var args = EvalArgs(creator.classCreatorRest().arguments(), self, frame, silent);

        if (WrapperClassNames.Contains(className))
            return args.Count == 1
                ? args[0]
                : throw new JavaRuntimeException($"{className}(...) needs exactly one argument.");
        if (className == "String")
            return new JString(args.Count == 1 ? Display(args[0]) : "");
        if (className == "ArrayList")
            return EvalNewArrayList(creator, silent, line);

        var obj = Instantiate(className, args);
        if (!silent)
            _emitter.Emit(line, StepKind.Alloc, $"new {className}(...)",
                FlushPending(new LineHighlight(line), new HeapAlloc(obj.Id, className,
                    obj.Fields.Select(kv => new KeyValuePair<string, string>(kv.Key, Display(kv.Value))).ToList())));
        return new JRef(obj.Id);
    }

    private JValue EvalNewArrayList(JavaParser.CreatorContext creator, bool silent, int line)
    {
        var elementType = creator.createdName().typeArgumentsOrDiamond().FirstOrDefault()
            ?.typeArguments()?.typeArgument(0)?.GetText() ?? "Object";
        var arrId = $"a{++_arrayCounter}";
        _arrayStore[arrId] = new List<JValue>();
        if (!silent)
            _emitter.Emit(line, StepKind.Alloc, "new ArrayList<>()",
                FlushPending(new LineHighlight(line), new ArrayAlloc(arrId, elementType, Array.Empty<string>())));
        return new JArrayRef(arrId);
    }

    /// Rectangular-only: `new T[n]` (1D) or `new T[r][c]` (2D, fully sized). Jagged creation
    /// (`new T[r][]`) and multi-dimensional brace initializers aren't supported — see the file
    /// header note on why 2D arrays are modeled as grid2d rather than true array-of-arrays.
    private JValue EvalArrayCreation(
        JavaParser.CreatorContext creator, HeapObject? self, Frame frame, bool silent, int line)
    {
        var rest = creator.arrayCreatorRest();
        var elementType = creator.createdName().GetText();

        if (rest.arrayInitializer() != null)
        {
            if (rest.LBRACK().Length != 1)
                throw new UnsupportedConstructException("multi-dimensional array initializer", line);
            return EvalArrayInitializer(rest.arrayInitializer(), elementType, self, frame, silent, line);
        }

        var dims = rest.expression();
        switch (dims.Length)
        {
            case 1:
                var length = ((JInt)EvalExpression(dims[0], self, frame, silent)).V;
                var def = DefaultFor(elementType);
                return AllocArray(elementType, Enumerable.Repeat(def, length).ToList(), silent, line);

            case 2:
                var rows = ((JInt)EvalExpression(dims[0], self, frame, silent)).V;
                var cols = ((JInt)EvalExpression(dims[1], self, frame, silent)).V;
                return AllocGrid(elementType, rows, cols, silent, line);

            default:
                throw new UnsupportedConstructException("array rank > 2", line);
        }
    }

    private JValue EvalArrayInitializer(
        JavaParser.ArrayInitializerContext ctx, string elementType, HeapObject? self, Frame frame, bool silent, int line)
    {
        var values = ctx.variableInitializer()
            .Select(vi => vi.expression() is { } e
                ? EvalExpression(e, self, frame, silent)
                : throw new UnsupportedConstructException("nested array initializer", line))
            .ToList();
        return AllocArray(elementType, values, silent, line);
    }

    private JValue AllocArray(string elementType, IReadOnlyList<JValue> values, bool silent, int line)
    {
        var arrId = $"a{++_arrayCounter}";
        _arrayStore[arrId] = values.ToList();
        if (!silent)
            _emitter.Emit(line, StepKind.Alloc, $"new {elementType}[{values.Count}]",
                FlushPending(new LineHighlight(line), new ArrayAlloc(arrId, elementType, values.Select(Display).ToList())));
        return new JArrayRef(arrId);
    }

    private JValue AllocGrid(string elementType, int rows, int cols, bool silent, int line)
    {
        var gridId = $"g{++_gridCounter}";
        _gridStore[gridId] = new JValue[rows, cols];
        var def = DefaultFor(elementType);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
                _gridStore[gridId][r, c] = def;
        _gridElementType[gridId] = elementType;
        if (!silent)
            _emitter.Emit(line, StepKind.Alloc, $"new {elementType}[{rows}][{cols}]",
                FlushPending(new LineHighlight(line), new Grid2dAlloc(gridId, rows, cols, elementType, Display(def))));
        return new JGridRef(gridId);
    }

    // ---------- built-ins ----------

    private JValue EvalMathCall(string name, IReadOnlyList<JValue> args, int line) => name switch
    {
        "abs" => args[0] switch
        {
            JInt i => new JInt(Math.Abs(i.V)), JDouble d => new JDouble(Math.Abs(d.V)),
            _ => throw new JavaRuntimeException("Math.abs needs a number."),
        },
        "pow" => new JDouble(Math.Pow(ToDouble(args[0]), ToDouble(args[1]))),
        "sqrt" => new JDouble(Math.Sqrt(ToDouble(args[0]))),
        "random" => new JDouble(_random.NextDouble()),
        "max" => args[0] is JInt a0 && args[1] is JInt b0 ? new JInt(Math.Max(a0.V, b0.V)) : new JDouble(Math.Max(ToDouble(args[0]), ToDouble(args[1]))),
        "min" => args[0] is JInt a1 && args[1] is JInt b1 ? new JInt(Math.Min(a1.V, b1.V)) : new JDouble(Math.Min(ToDouble(args[0]), ToDouble(args[1]))),
        "floor" => new JDouble(Math.Floor(ToDouble(args[0]))),
        "ceil" => new JDouble(Math.Ceiling(ToDouble(args[0]))),
        "round" => new JInt((int)Math.Round(ToDouble(args[0]), MidpointRounding.AwayFromZero)),
        _ => throw new UnsupportedConstructException($"Math.{name}(...)", line),
    };

    private static JValue EvalWrapperStaticCall(string className, string name, IReadOnlyList<JValue> args, int line) => (className, name) switch
    {
        ("Integer", "parseInt") => new JInt(int.Parse(((JString)args[0]).V, CultureInfo.InvariantCulture)),
        ("Integer", "valueOf") => args[0] is JString s ? new JInt(int.Parse(s.V, CultureInfo.InvariantCulture)) : args[0],
        ("Integer", "toString") => new JString(Display(args[0])),
        ("Double", "parseDouble") => new JDouble(double.Parse(((JString)args[0]).V, CultureInfo.InvariantCulture)),
        ("Double", "valueOf") => args[0] is JString s2 ? new JDouble(double.Parse(s2.V, CultureInfo.InvariantCulture)) : args[0],
        ("String", "valueOf") => new JString(Display(args[0])),
        _ => throw new UnsupportedConstructException($"{className}.{name}(...)", line),
    };

    private static JValue EvalStringInstanceCall(JString s, string name, IReadOnlyList<JValue> args, int line) => name switch
    {
        "length" => new JInt(s.V.Length),
        "substring" => args.Count == 1
            ? new JString(s.V.Substring(((JInt)args[0]).V))
            : new JString(s.V.Substring(((JInt)args[0]).V, ((JInt)args[1]).V - ((JInt)args[0]).V)),
        "indexOf" => new JInt(s.V.IndexOf(args[0] is JString sub ? sub.V : ((JChar)args[0]).V.ToString(), StringComparison.Ordinal)),
        "compareTo" => new JInt(string.CompareOrdinal(s.V, ((JString)args[0]).V)),
        "equals" => new JBool(args[0] is JString other && other.V == s.V),
        "equalsIgnoreCase" => new JBool(args[0] is JString other2 && string.Equals(other2.V, s.V, StringComparison.OrdinalIgnoreCase)),
        "toUpperCase" => new JString(s.V.ToUpperInvariant()),
        "toLowerCase" => new JString(s.V.ToLowerInvariant()),
        "charAt" => new JChar(s.V[((JInt)args[0]).V]),
        "trim" => new JString(s.V.Trim()),
        "isEmpty" => new JBool(s.V.Length == 0),
        "concat" => new JString(s.V + ((JString)args[0]).V),
        _ => throw new UnsupportedConstructException($"String.{name}(...)", line),
    };

    // ---------- emission helpers ----------

    private void EmitDeclareOrAssign(string frameId, string name, string? declaredType, JValue value, int line, bool isDeclaration)
    {
        var kind = isDeclaration ? StepKind.Declare : StepKind.Assign;
        var caption = isDeclaration ? $"{declaredType} {name} = {Display(value)};" : $"{name} = {Display(value)};";
        SceneOp op = value is JRef r
            ? new RefSet(frameId, name, r.ObjId)
            : new MemCellSet(frameId, name, declaredType ?? InferTypeName(value), Display(value));
        _emitter.Emit(line, kind, caption, FlushPending(new LineHighlight(line), op));
    }

    private void EmitBranch(string frameId, int line) =>
        _emitter.Emit(line, StepKind.Branch, $"branch to line {line}", FlushPending(new LineHighlight(line)));

    private SceneOp[] FlushPending(params SceneOp[] ops)
    {
        if (_pendingFramePush is null) return ops;
        var combined = new SceneOp[ops.Length + 1];
        combined[0] = _pendingFramePush;
        Array.Copy(ops, 0, combined, 1, ops.Length);
        _pendingFramePush = null;
        return combined;
    }

    private void FlushPendingFramePushAlone(string frameId, int line)
    {
        if (_pendingFramePush is null) return;
        var push = _pendingFramePush;
        _pendingFramePush = null;
        _emitter.Emit(line, StepKind.Call, "enter (empty body)", push);
    }

    private static string InferTypeName(JValue v) => v switch
    {
        JInt => "int", JDouble => "double", JBool => "boolean", JChar => "char", JString => "String", _ => "var",
    };

    /// Strips trailing `[]` pairs off a type's text (e.g. "int[][]" -> (2, "int")) — typeType's
    /// GetText() concatenates raw source with no spaces, so this is a plain string peel.
    private static (int Rank, string ElementType) ArrayRank(string typeName)
    {
        var rank = 0;
        var elem = typeName;
        while (elem.EndsWith("[]", StringComparison.Ordinal))
        {
            rank++;
            elem = elem[..^2];
        }
        return (rank, elem);
    }

    private static JValue DefaultFor(string typeName) => typeName switch
    {
        "int" or "short" or "byte" or "long" => new JInt(0),
        "double" or "float" => new JDouble(0),
        "boolean" => new JBool(false),
        "char" => new JChar('\0'),
        _ => new JRef(null),
    };

    private static string Display(JValue v) => v switch
    {
        JInt i => i.V.ToString(CultureInfo.InvariantCulture),
        JDouble d => d.V.ToString(CultureInfo.InvariantCulture),
        JBool b => b.V ? "true" : "false",
        JChar c => c.V.ToString(),
        JString s => s.V,
        JRef r => r.ObjId ?? "null",
        JArrayRef a => a.ArrId,
        JGridRef g => g.GridId,
        JVoid => "void",
        _ => v.ToString() ?? "",
    };

    private sealed record JVoid : JValue;
    private static readonly JValue VoidMarker = new JVoid();
}
