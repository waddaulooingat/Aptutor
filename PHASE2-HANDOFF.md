# AP CS A Tutor — Phase 2 Handoff Addendum

Extends `CLAUDE-HANDOFF.md`. Companion files: `SceneState.cs`, `SceneRenderer.cs`.
Do Phase 1 first. This is the next bounded task; stop at the acceptance criteria for review.

## Scope

Build the scene engine's foundation and the **first three primitives only**:
`memCell`, `heapObject`, `refArrow`. Everything else in the primitive catalog
(`indexedStrip`, `grid2d`, `stackFrame` beyond simple stacking, `callTree`, `exprBubble`,
`boolTruthGlow`) is later work — do not start it.

Two provided files:
- `SceneState.cs` — headless. Applies the phase-2 `SceneOp` subset forward and records a
  per-delta inverse so `StepBack()` scrubs backward. No Skia dependency.
- `SceneRenderer.cs` — draws a `SceneState` onto an `SKCanvas`: stack frames on the left,
  heap objects on the right, arrows from reference cells to their target objects.

## Project wiring

- New project `ApTutor.Scene` holding both files. Add `SkiaSharp` and
  `SkiaSharp.Views.WPF` (**pin 2.88.x**; the renderer uses the 2.88 text API).
- In the shell, host an `SKElement`. Its `PaintSurface` handler calls
  `renderer.Render(state, e.Surface.Canvas, e.Info)`.
- Stepping controls: a "Step" button calls `state.ClearFlashes()` then `state.Apply(delta)`
  then `skElement.InvalidateVisual()`. A "Back" button calls `state.StepBack()` (guard on
  `CanStepBack`) then invalidates. For phase 2, feed deltas from the hand-written fixture
  below — the real tracer arrives in phase 3.

## Test approach — fixtures, not UI

`SceneState` is pure logic; test it directly. Two fixture shapes:

1. **Forward/back round-trip.** Apply a list of deltas, snapshot state, apply one more,
   `StepBack()`, assert state equals the earlier snapshot. This proves inverses are correct.
2. **State assertions.** After a known delta sequence, assert the frame count, a specific
   cell's `Value`/`TargetObjId`, a heap object's fields, and `HighlightLine`.

Renderer tests are lighter: render a fixture to an off-screen `SKSurface`, assert it doesn't
throw and produces non-blank output. Visual correctness is reviewed by eye, not asserted pixel-wise.

## Reference-vs-value demo fixture (the money shot)

This is the sequence that teaches the highest-leverage idea. Encode it as a test **and** as the
default fixture the shell steps through. Java being modeled:

```java
int x = 5;            // step 0
Point p = new Point();// step 1
p.setX(5);            // step 2
Point q = p;          // step 3  (q aliases p — same object)
q.setX(9);            // step 4  (x stays 5; p.x becomes 9 through the alias)
```

Deltas (frame id `main`):
- **0** `LineHighlight(1)`, `MemCellSet("main","x","int","5")`
- **1** `LineHighlight(2)`, `HeapAlloc("1","Point",[("x","0")])`, `RefSet("main","p","1")`
- **2** `LineHighlight(3)`, `FieldSet("1","x","5")`, `MemCellFlash("main","p")`
- **3** `LineHighlight(4)`, `RefSet("main","q","1")`
- **4** `LineHighlight(5)`, `FieldSet("1","x","9")`

Expected end state to assert: one frame `main`; cells `x=5` (value), `p`→#1, `q`→#1; heap
object #1 `Point` with `x=9`. Two arrows point at the same object — that shared target *is* the
lesson. Then `StepBack()` from step 4 must restore `#1.x == 5`.

## Acceptance criteria (write as tests)

- Applying the demo fixture yields the end state above; `q` and `p` both target `"1"`.
- Round-trip: after the full fixture, one `StepBack()` restores `#1.x` to `"5"`; a second
  restores `q` to absent (its `RefSet` inverse removed the freshly-added cell).
- `FramePop` then `StepBack()` restores the popped frame **with all its cells intact**
  (add a separate fixture: push frame, set two cells, pop, step back, assert both cells return).
- `MemCellSet` on an existing name updates in place; its inverse restores the prior value and
  clears the reference flag if one was set.
- `ClearFlashes()` clears all cell flashes; a subsequent write re-flashes only the written cell.
- Renderer renders the demo fixture to an off-screen surface without throwing.

## Notes / carry-forward

- Inverses are in-memory Actions, deliberately **not** serializable. Forward traces serialize
  fine (phase 3); backward scrubbing is a session concern. Don't over-engineer serializable inverses.
- Frame draw order in the renderer is call-order top-down; if you prefer newest-frame-on-top,
  reverse the enumeration in `Render` — cosmetic, no state impact.
- Keep layout constants named in `SceneRenderer`; do not scatter magic numbers when adding
  primitives later.
- The `RefColor` initializer in `SceneRenderer` is written verbosely to avoid a literal; feel
  free to replace with `new SKColor(0x25, 0x63, 0xEB)` for clarity.
