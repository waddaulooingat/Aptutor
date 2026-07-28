# AP CS A Tutor — Phase 3 Handoff Addendum

Extends `CLAUDE-HANDOFF.md` and `PHASE2-HANDOFF.md`. Companion file: `Tracer.cs`.
Do phases 1–2 first. This is the largest phase; scope the first cut per §6 below and stop for review.

## The two open decisions, resolved

**Parsing.** Do **not** hand-roll a Java parser. Generate one from the community **ANTLR4 Java
grammar** (`Java.g4`) into C#. Parse *full* Java, then reject anything outside the CED subset in a
separate `SubsetValidator` pass over the AST (`Tracer.cs → CedSubsetValidator`). Restricting at the
grammar is brittle; restricting at a validator gives clean, line-numbered "unsupported construct"
errors — which is exactly the guardrail behavior we want.

**Execution.** A **tree-walking interpreter** over our own AST, not JDK instrumentation. We need a
step hook at every mutation to drive the scene, and a tree-walker gives that control for free.
Bundled `javac`/`java` stays in scope but only for the phase-5 FRQ sandbox ("run it for real"),
never for the tutor's visualization.

## Pipeline

```
source ──ANTLR──▶ parse tree ──(builder)──▶ our AST ──▶ SubsetValidator ──▶ Interpreter ──▶ TraceStep[]
```

Interpreter holds: a `Heap` (sequential ids "1","2",…), a call stack of `Frame`s (ids `main`,
`f#1`,`f#2`,…), and a `TraceEmitter`. It walks statements/expressions and calls `emitter.Emit(...)`
at each event below. The emitted `SceneOp`s are the *same records phase 2 already renders* — this is
the whole point of the decoupling.

## Step-emission points → SceneOp map

Emit one `TraceStep` per row. `line` = the source line; `caption` feeds TTS.

| Java event | StepKind | SceneOps emitted |
|---|---|---|
| local var declared / assigned (primitive or String) | Declare / Assign | `LineHighlight`, `MemCellSet(frame,name,type,value)` |
| `new T(...)` | Alloc | `LineHighlight`, `HeapAlloc(id,T,fieldsAtInit)` (+ `RefSet` if bound to a var this step) |
| reference assignment `p = expr` / `q = p` | Assign | `LineHighlight`, `RefSet(frame,var,objId?)` |
| field write `obj.f = v` (incl. inside a mutator) | FieldWrite | `LineHighlight`, `FieldSet(objId,f,v)`, `MemCellFlash` on the receiver var if visible |
| method entry | Call | `FramePush(f#n,sig)`, then one `MemCellSet`/`RefSet` per parameter |
| method return | Return | `FramePop(f#n)` (carry return value in caption; consumer may bind it next step) |
| `if/else` branch taken | Branch | `LineHighlight` on the chosen branch |
| loop iteration boundary | LoopIter | `LineHighlight`, `MemCellSet` for the loop var if it changed |
| out-of-subset node reached | Unsupported | none; set `HaltReason`, stop |
| interpreter-raised error (e.g. NPE, index OOB) | Throw | none; set `HaltReason`, stop |

Arrays / `ArrayList` / 2D / recursion add `ArrayAlloc`/`ArrayWrite`/`Grid2d*`/`CallTree*` ops — but
those primitives aren't built until later scene-engine phases, so keep them behind the same subset
gate for now (validator flags them as "not yet supported" rather than emitting unrenderable ops).

## Conventions

- **Heap ids**: allocation order, starting at "1". Never reuse. This is what makes traces match
  fixtures byte-for-byte.
- **Frame ids**: `main` for entry, then `f#1`, `f#2`, … in call order.
- **Determinism**: seed `Math.random()` from `Tracer.Trace(randomSeed:)`. Same source + seed ⇒
  identical `TraceStep[]`. Tests depend on this.
- **String**: first cut treats `String` as a cell value (not a heap object / `indexedStrip`).
  Revisit when the `indexedStrip` primitive lands.

## First-cut scope (bound the work)

Cover **Units 1–5 constructs only**: primitives & expressions, objects & method calls, String/Math/
wrapper methods, if/else, while/for/nested loops, and user-written classes (fields, constructors,
accessors, mutators, static, `this`). That's enough to trace the demo and most single-class methods.

**Defer** (validator flags as unsupported for now): arrays, `ArrayList`, 2D arrays, inheritance,
recursion. They come online as their scene primitives are built.

## Acceptance criteria — close the loop

The headline test: feed the **phase-2 reference-vs-value Java** (the `Point p = new Point(); q = p;
q.setX(9);` program) into `Tracer.Trace`, and assert it emits a `TraceStep[]` whose deltas match the
hand-written phase-2 fixture — same heap id `"1"`, same two `RefSet`s to that id, final `FieldSet("1",
"x","9")`. Phase 2 rendered that stream by hand; phase 3 must now *generate* it. When the animation
looks identical whether fed the fixture or the tracer output, phase 3 is done.

Also assert:
- A method call emits `FramePush` then per-parameter `MemCellSet`/`RefSet`, and `FramePop` on return;
  running it through `SceneState` then `StepBack` per step returns to empty state.
- An out-of-subset program (e.g. contains a lambda) returns `Completed == false` with a `HaltReason`
  naming the construct and line — no partial garbage stream.
- Same source + same seed ⇒ identical steps across two runs (determinism).

## Carry-forward

- The interpreter is the bulk of the effort; everything else in `Tracer.cs` is provided shape.
- Keep the emitter dumb: the interpreter *knows* what changed and emits the exact op. Don't diff
  whole-heap snapshots to derive deltas — it's slower and loses the caption/intent.
- FRQ autograding, MCQ, and rubric modeling remain phases 5–6. Not here.
