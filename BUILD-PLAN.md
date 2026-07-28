# Advanced Test Prepper for High School — Master Build Plan

The top-level roadmap. Hand this to Claude Code as the project brief; it references the per-phase
spec files already produced. Execute phases in order within each track; tracks A→D have the
dependencies noted. Every phase is bounded with acceptance criteria — build to them and stop for
review before the next.

> AP® is a trademark of the College Board, which is not affiliated with and does not endorse this
> product. Reference "AP" only descriptively in copy; never in the product name, domain, or logo.

## Product shape (what we're building toward)

- **Core — $49 one-time, fully offline.** Curriculum, the execution visualizer (tracer), and a
  verified item bank. No account, no server, no Claude API in the loop. This is the anchor and the
  funnel.
- **Plus — $99/year per course, hosted.** Everything in Core plus fresh adaptive practice, new
  timed mock exams, current-year updates, and capped live AI tutoring — served through our web
  service, which is the only thing that holds the Claude key.
- **Flagship course: Computer Science A.** Chosen because its live visualizer is genuinely novel
  and the free official incumbent (Khan Academy) does not cover CS A.

## Architecture (three tiers + one principle)

```
Desktop client (Avalonia, cross-platform)
  ├─ Core: DAG, scene engine, TRACER, verified item bank — runs fully offline
  └─ Plus features call ▼
Web service (our server; holds the Claude key)
  ├─ Auth / entitlement (paid Plus user?)          ── Stripe
  ├─ Usage meter + fair-use cap (per user)
  ├─ Content store (verified pre-gen content; CDN)  ── most requests stop HERE
  └─ Claude API  ── ONLY for live/adaptive generation, ONLY within cap
```

**The principle:** Claude is not in the hot path. The service serves verified pre-generated content
for the overwhelming majority of requests (near-zero cost, instant) and calls the Claude API only
for genuinely live, capped features. This is what keeps Plus profitable and lets Core run with no
backend at all. The tracer runs locally on the client — the headline feature costs nothing per use.

## Stack decisions (locked)

- **Client:** Avalonia + SkiaSharp on .NET 8 (cross-platform: Windows + macOS). *Not WPF* — WPF
  would lock us to Windows and force a rewrite for a consumer product. Everything below the renderer
  (tracer, scene state, DAG) is UI-agnostic and ports as-is.
- **Modularity:** courses are `ICourseModule` plugins (`CourseModule.cs`). The shell is
  course-agnostic; adding a course adds a module, not platform code.
- **Parsing (CS A tracer):** ANTLR4 Java grammar → C#, restrict at a validator pass (see PHASE3).
- **Backend:** .NET web service; Stripe for billing; object storage + CDN for the content store.
- **Claude usage:** build-time content factory + runtime capped calls; key server-side only.

---

## TRACK A — Desktop Core (the offline $49 product)

### Phase 0 — Solution scaffold
Goal: `AdvancedTestPrepper.sln` with projects `Platform`, `Curriculum`, `Scene`, `Tracer.Csa`,
`Client` (Avalonia), and `Tests` (xUnit). Add the provided files to their projects. CI builds green.
Acceptance: solution builds on Windows and macOS; empty Avalonia window launches.

### Phase 1 — DAG loader + course-agnostic shell
Seeds: `SkillDag.cs`, `CourseModule.cs`. Depends on: 0.
Goal: load `apcsa-skill-dag.json` via `SkillDagLoader`; register a CS A `ICourseModule`; render the
left rail from `CourseRegistry`; node tree + detail pane + mastery persistence.
Acceptance: the six tests from `CLAUDE-HANDOFF.md §7` pass; shell shows the CS A unit tree driven
entirely through `ICourseModule` (no course-specific code in the shell).

### Phase 2 — Scene engine + first three primitives
Seeds: `SceneState.cs`, `SceneRenderer.cs`, `PHASE2-HANDOFF.md`. Depends on: 1.
Goal: SkiaSharp primitives `memCell`/`refArrow`/`heapObject`; forward+inverse stepping; the
reference-vs-value demo fixture as the default walkthrough.
Acceptance: the six criteria in `PHASE2-HANDOFF.md`; step-back restores prior state exactly.

### Phase 3 — Java-subset tracer (the long pole)
Seeds: `Tracer.cs`, `PHASE3-HANDOFF.md`. Depends on: 2.
Goal: ANTLR parse → subset validator → tree-walking interpreter emitting `VisualStep[]` for Units
1–5 constructs; wire it as the CS A module's `IStepProvider` with `SupportsLiveInput = true`.
Acceptance: the closing-loop test — feed the phase-2 demo Java through the tracer and get a step
stream whose deltas match the hand-written fixture; determinism; unsupported-construct halts cleanly.

### Phase 4 — TTS + guided walkthroughs
Depends on: 3. Goal: narrate each `VisualStep.Caption`; port the GeoTutor TTS layer; per-step audio
synced to the highlight. Acceptance: a walkthrough plays end-to-end with narration and step controls.

### Phase 5 — Remaining primitives + content integration + FRQ sandbox
Depends on: 3, and Track B Phase 7 for content. Goal: build the rest of the primitives
(`indexedStrip`, `grid2d`, `callTree`, `exprBubble`, `boolTruthGlow`), extend the tracer/validator to
Units 6–10, load the verified item bank via `IContentSource`, and add the local FRQ sandbox
(bundled `javac`/`java`) with rubric grading via `IAttemptGrader`.
Acceptance: every DAG node renders its declared `viz`; item bank drives practice; an FRQ attempt is
compiled, run, and scored against a rubric.

### Phase 6 — Mock exam + mastery reporting
Depends on: 5. Goal: timed MCQ/FRQ mode; weak-node surfacing back into the DAG frontier; progress
reporting. Acceptance: a full timed mock runs; weak nodes reappear in `MasteryTracker.Available()`.

**End of Track A = a shippable, fully-offline Core product.**

---

## TRACK B — Content authoring pipeline (build-time; run by you, not the user)

### Phase 7 — Content factory + verification
Depends on: 1 (needs the DAG). Runs offline; parallelizes with Track A after Phase 1.
Goal: a tool that uses the Claude API at BUILD TIME to draft practice items + explanations +
walkthrough scripts per DAG node, then a human/expert verification pass, then export to the format
`IContentSource` loads (and later, the content store).
Guardrails: 100% original items — never College Board question text. Every item carries a verified
flag; nothing ships unverified. One-time API spend (~$10–50), by you.
Acceptance: a verified item bank for Units 1–5 exists and loads in Core (Phase 5 consumes it).

---

## TRACK C — Hosted Plus backend (the $99/yr recurring tier)

### Phase 8 — Web service skeleton + auth + billing
Depends on: nothing in A (can start early); needs Phase 7's content format.
Goal: .NET web service; account/auth; Stripe subscription + entitlement check ("is this a paid Plus
user for course X?"). Acceptance: a test user can subscribe via Stripe (test mode) and the service
returns correct entitlement.

### Phase 9 — Content store + serve verified content
Depends on: 7, 8. Goal: load the verified item bank into object storage behind a CDN; service
endpoints serve pre-gen content by node/example. Most requests resolve here with no Claude call.
Acceptance: client (Plus) fetches walkthroughs/practice from the service; p95 latency is cache-fast;
zero Claude calls for pre-gen content.

### Phase 10 — Claude proxy + metering + caps
Depends on: 8. Goal: the only path that calls the Claude API. Per-user usage meter; fair-use cap
enforced BEFORE each live call; Haiku-default with prompt caching; server-side key only.
Acceptance: live generation works for entitled users; a user at cap is blocked gracefully; usage is
metered per user; no key ever reaches the client.

### Phase 11 — Plus features in the client
Depends on: 9, 10. Goal: adaptive hints on a student's specific wrong answer, "ask the tutor" chat,
fresh mock generation — all routed through the service, all under cap. Core stays fully offline;
these appear only for Plus.
Acceptance: a Plus user gets adaptive hints and chat; a Core user sees none of it and needs no network.

---

## TRACK D — Ship

### Phase 12 — Packaging, signing, licensing
Depends on: 6 (Core), 11 (Plus). Goal: signed installers for Windows + macOS; license/activation for
Plus tied to entitlement. Acceptance: signed builds install clean on both OSes; Plus unlock works.

### Phase 13 — Pilot + price finalization
Depends on: 12. Goal: small paid pilot to measure REAL per-active-user live-token COGS; confirm the
$99 Plus margin (or adjust). Acceptance: COGS/active user measured; pricing decision grounded in data,
not the illustrative numbers used so far.

---

## Critical path & parallelism

- **Long pole: Phase 3 (tracer).** It gates Phases 4–6. Start it as soon as Phase 2's delta protocol
  is stable, and give it your best model (Opus) time.
- **Track B (content) parallelizes** with Track A after Phase 1 — it only needs the DAG.
- **Track C (backend) can start early** (Phase 8 needs no client), but Phases 9/11 need Phase 7's
  content format and the client from Track A.
- Ship Core (Track A + B) first as the $49 product; bring Plus (Track C) online as the upsell.

## Guardrails carried across every phase

- **Trademark:** no "AP"/"Advanced Placement" in product name, domain, or logo; descriptive use + the
  disclaimer only. Legal review before branding/selling.
- **Copyright:** all content original; never reproduce College Board questions/rub-ric text; no
  training on their content. Model rubric *structure*, author original items.
- **Java subset is a hard boundary** (PHASE3 §4): unsupported constructs halt with a clear message,
  never a wrong animation.
- **Determinism:** same input + seed ⇒ identical steps. Traces are fixtures.
- **COGS discipline:** Claude out of the hot path; pre-gen + cache; Haiku for live; per-user caps;
  key server-side only.

## Build tooling & cost

Build on a Claude Code **Max subscription** (5x if pacing lighter, 20x if daily) — flat and
predictable, ~$400–$1,200 total across the build. Default to **Sonnet** for routine phases; reserve
**Opus** for Phase 3 (the tracer) and the trickier backend logic (Phase 10). Cap parallel subagents
in `CLAUDE.md` to avoid runaway metered spend if you ever switch to API billing.

### First command for Claude Code
> Create the .NET 8 solution per Phase 0 (Avalonia client + the listed projects), add all provided
> `.cs` files and `apcsa-skill-dag.json` to their projects, and implement Phase 1 with its acceptance
> tests. Do not start Phase 2. Stop for review.
