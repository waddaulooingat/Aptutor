# AP CS A Tutor — Claude Code Handoff

Hand this file to Claude Code as the working brief. It assumes the two companion files
`apcsa-skill-dag.json` and `SkillDag.cs` are in the repo.

---

## 1. What we're building

A desktop tutor for **AP Computer Science A (Java)**, modeled on an existing app (GeoTutor).
It teaches by *animating program execution*: as Java runs, the UI shows memory, references,
arrays, the call stack, and recursion trees updating step by step. It also grades
free-response-style method writing against point rubrics.

Reuse from GeoTutor (do not reinvent): the skill-DAG model, mastery tracker, Neural TTS
narration layer, WPF shell patterns, phased build.

New for this course: a **Java-subset execution tracer** and a **program-state scene engine**.
These are the two hard parts. Everything else is plumbing.

## 2. Stack & conventions

- **.NET 8, C# 12**, `<Nullable>enable</Nullable>`, `<LangVersion>latest</LangVersion>`.
- **WPF** shell; **SkiaSharp** (`SKElement`) for the scene canvas.
- **System.Text.Json** for all serialization (no Newtonsoft).
- Records for immutable data; `sealed` by default; `StringComparer.Ordinal` for id maps.
- Namespaces: `ApTutor.Curriculum`, `ApTutor.Tracer`, `ApTutor.Scene`, `ApTutor.Shell`.
- One xUnit test project (`ApTutor.Tests`). Every non-UI component ships with tests.
- No secrets in source. TTS/API keys via `appsettings.json` + user-secrets.

## 3. Architecture — the one principle that matters

**The tracer and the scene engine are decoupled by a serializable delta stream.**

```
Java source ──▶ Tracer ──▶ TraceStep[]  (each carries a SceneDelta)  ──▶ Scene engine ──▶ SkiaSharp
                                        (JSON-serializable)
```

The tracer never touches Skia. The scene engine never parses Java. They agree only on the
`SceneDelta` protocol (§5). This lets us test the tracer headlessly, swap the interpreter
later, and even run the tracer out-of-process if needed.

The scene engine holds mutable visual state and applies deltas **forward to step** and their
**inverses to step back** (§5.3), so the student can scrub execution both directions.

## 4. Tracer step model

Scope the interpreter to the **CED Java subset only**: primitives; `String`, `Math`, wrapper
classes; `if`/`else`/loops; user classes with fields/constructors/methods; `static`;
1D/2D arrays; `ArrayList<E>`; single inheritance + `super`/`@Override`; recursion.
**Out of scope:** threads, generics beyond `ArrayList<E>`, lambdas, streams, interfaces beyond
`Comparable`, exceptions beyond the standard unchecked ones the tracer raises itself.

The tracer executes and emits a `TraceStep` per **meaningful event**, not per token:

```csharp
public enum StepKind { Declare, Assign, Eval, Branch, LoopIter,
                       Call, Return, ArrayWrite, FieldWrite, Alloc, Throw }

public sealed record TraceStep(
    int      Index,       // 0-based, monotonic
    int      SourceLine,  // 1-based, drives lineHighlight
    StepKind Kind,
    string   Caption,     // one-line human description; also fed to TTS
    SceneDelta Delta);    // how the visual state changes at this step
```

A run is just `IReadOnlyList<TraceStep>`. The shell holds a cursor; stepping = move cursor and
apply/invert the delta at that index.

## 5. SceneDelta protocol (the contract)

### 5.1 Primitives the scene engine must render
From `apcsa-skill-dag.json → scenePrimitives`:
`memCell, refArrow, heapObject, indexedStrip, grid2d, stackFrame, callTree, lineHighlight,
exprBubble, boolTruthGlow`. Each node in the DAG declares which it needs via its `viz` field —
use that to prioritize and to write per-node visual tests.

### 5.2 Delta ops
A `SceneDelta` is an ordered list of `SceneOp`s applied atomically for one step. Model as a
discriminated set (abstract record + sealed subtypes):

```csharp
public abstract record SceneOp;

// stack + primitives
public sealed record FramePush(string FrameId, string MethodSig, IReadOnlyList<string> Params) : SceneOp;
public sealed record FramePop (string FrameId, string? ReturnValue) : SceneOp;
public sealed record MemCellSet(string FrameId, string Name, string Type, string Value) : SceneOp;
public sealed record MemCellFlash(string FrameId, string Name) : SceneOp;

// heap + references
public sealed record HeapAlloc(string ObjId, string ClassName, IReadOnlyList<(string Field,string Value)> Fields) : SceneOp;
public sealed record FieldSet (string ObjId, string Field, string Value) : SceneOp;
public sealed record RefSet   (string FrameId, string VarName, string? TargetObjId) : SceneOp; // null = null ref

// arrays
public sealed record ArrayAlloc(string ArrId, int Length) : SceneOp;
public sealed record ArrayWrite(string ArrId, int Index, string Value) : SceneOp;
public sealed record Grid2dAlloc(string GridId, int Rows, int Cols) : SceneOp;
public sealed record Grid2dWrite(string GridId, int Row, int Col, string Value) : SceneOp;

// control-flow overlays
public sealed record LineHighlight(int Line) : SceneOp;
public sealed record ExprPush(string ExprId, string Text) : SceneOp;
public sealed record ExprResolve(string ExprId, string Value) : SceneOp;
public sealed record BoolGlow(string ExprId, bool Value) : SceneOp;

// recursion
public sealed record CallTreeNode(string NodeId, string? ParentId, string Label) : SceneOp;
public sealed record CallTreeReturn(string NodeId, string Value) : SceneOp;

public sealed record SceneDelta(IReadOnlyList<SceneOp> Ops);
```

### 5.3 Invertibility (for step-back)
Two options — **pick per-op invertibility** as the default:
- Each op has a computable inverse given prior state (`MemCellSet` inverse = restore previous
  value; `FramePush` inverse = `FramePop`; `ArrayWrite` inverse = write old value). The engine
  records the old value when applying, so it can invert without recomputation.
- Fallback for anything awkward: full state snapshot every K steps + replay-forward. Only add
  this if a clean inverse proves painful.

Serialize the whole run to JSON so traces are cacheable and testable as fixtures.

## 6. Phase plan (build order)

1. **Shell + DAG loader** ← `SkillDag.cs` is done. Wire it into a WPF shell: unit/node tree,
   mastery state persisted to disk, node detail pane. *(This phase, see §7.)*
2. **Scene engine primitives** — the 10 SkiaSharp primitives + a `SceneState` that applies
   `SceneOp`s forward/back. Build `memCell` → `refArrow` → `heapObject` first (reference-vs-value
   is the highest-leverage moment). Drive it from hand-written fixture deltas before the tracer exists.
3. **Java-subset tracer** — parser + stepping interpreter over §4's subset, emitting `TraceStep[]`.
4. **TTS + guided walkthroughs** — narrate `TraceStep.Caption` per step; port GeoTutor's TTS layer.
5. **FRQ sandbox + autograder** — bundle `javac`/`java`, compile+run student code, score against
   per-`*.frq`-node rubrics.
6. **Mock exam + mastery reporting** — timed MCQ/FRQ; feed weak nodes back into the DAG frontier.

## 7. Phase 1 — concrete deliverables & acceptance criteria

Build now:
- `ApTutor.Curriculum` project containing `SkillDag.cs` (provided).
- `SkillDagLoader.Load("apcsa-skill-dag.json")` returns a validated `SkillGraph`.
- WPF shell with: left tree grouped by unit; node detail (title, type, prereqs, viz);
  "Mark mastered" action; frontier list from `MasteryTracker.Available()`; progress bar from
  `PercentComplete`. Persist mastered-ids to `%APPDATA%/ApTutor/progress.json`.

Acceptance (write these as tests):
- Loads the shipped JSON; `Nodes.Count == meta.nodeCount`.
- **Validation throws** on: duplicate id, unknown prereq, and an injected cycle.
- `TopologicalOrder`: every node appears after all its prereqs.
- With nothing mastered, `Available()` returns exactly the roots (nodes with empty prereqs).
- Mastering all of a node's prereqs makes it appear in `Available()` on the next call.
- Round-trip: persist progress, reload, mastery state matches.

Do **not** start the tracer or Skia work in phase 1. Get the DAG-driven shell solid first.

## 8. Guardrails

- **Copyright.** Released College Board FRQs and rubrics are their IP. Do not paste FRQ text
  into the repo or reproduce it in the app's content files. Reference questions by
  DAG node id (`u6.frq`, etc.) and model the **rubric structure** (point items), which the
  student's own practice attempts are graded against. Original practice items are fine to author.
- **Java subset is a hard boundary.** If a construct is outside §4's list, the tracer should
  emit a clear "unsupported construct" step rather than guessing — surfacing the limit beats
  a wrong animation.
- **Determinism.** Traces must be reproducible: seed any `Math.random()` used in examples.
- **Test-first on the two hard parts.** Tracer and scene engine each get fixture-based tests
  (input Java → expected `TraceStep[]`; input delta → expected `SceneState`) before UI polish.

---

### Suggested first commit for Claude Code
> Create the .NET 8 solution `ApTutor.sln` with projects `ApTutor.Curriculum`,
> `ApTutor.Shell` (WPF), and `ApTutor.Tests` (xUnit). Add `SkillDag.cs` to Curriculum and
> `apcsa-skill-dag.json` as content copied to output. Implement the Phase 1 shell and all
> Phase 1 acceptance tests in §7. Stop there for review.
