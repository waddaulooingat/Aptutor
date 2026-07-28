# Advanced Test Prepper for High School — Master Build Plan

The top-level roadmap. Hand this to Claude Code as the project brief; it references the per-phase
spec files already produced. Execute phases in order within each track; tracks A→D have the
dependencies noted. Every phase is bounded with acceptance criteria — build to them and stop for
review before the next.

> AP® is a trademark of the College Board, which is not affiliated with and does not endorse this
> product. Reference "AP" only descriptively in copy; never in the product name, domain, or logo.

## Product shape (what we're building toward)

- **Core — one-time purchase, fully offline.** Curriculum, generated animated walkthroughs, and a verified
  item bank — plus, for CS A specifically, the live execution visualizer (tracer). No account, no
  server, no Claude API in the loop. This is the anchor and the funnel.
- **Plus — recurring per-course subscription, hosted.** Everything in Core plus fresh adaptive practice, new
  timed mock exams, current-year updates, and capped live AI tutoring — served through our web
  service, which is the only thing that holds the Claude key.
- **Flagship course: Computer Science A.** Chosen because its live visualizer is genuinely novel
  and the free official incumbent (Khan Academy) does not cover CS A.

## Course model — how courses get added (READ THIS BEFORE ADDING A SECOND COURSE)

There are two kinds of course. The default is cheap and is the actual product; only CS A is the
expensive kind. Getting this distinction right is what makes "courses on the left, generated content
per course" true.

**Generated-content course — the default (Physics, Chemistry, Biology, Statistics, ...).**
Claude generates the entire course at authoring time: the skill DAG, the practice items, the
explanations, and the step-by-step **animated walkthroughs, authored as `SceneDelta` streams**. The
platform replays those through the same scrubbing / TTS / mastery UI. No course-specific engine, no
interpreter, no per-course rendering code. Adding one is a *content-generation* task (Track B) + a
verification pass + registering an `ICourseModule` with `SupportsLiveInput = false`. This is the
product.

**Live-engine course — CS A only, for now.**
CS A additionally has the live **tracer**: a real Java interpreter that animates the student's *own
arbitrary code*, correctly and deterministically. That is a premium differentiator, and it needs a
real engine precisely because generated-on-the-fly traces can be wrong. It is the *only* reason
Phase 3 exists. It is **not** a per-course requirement — no other course needs an engine to be a
great, sellable course. A future course could add a live engine as a Tier-2 upgrade if it's ever
worth it, but that's optional and rare.

**Shared primitive library — build once, reuse everywhere.**
The visual vocabulary — labeled boxes, arrows, axes/plots, points, lines, highlights — is general and
lives in the `Scene` project, not in any course. Physics vectors and free-body diagrams are mostly
arrows + labeled points + axes drawn from this shared set. Build it rich once (Phase 5); most courses
draw from it with **zero** new rendering code. A genuinely bespoke visual is the rare exception a
course adds itself.

**So adding Physics =** generate its DAG + items + walkthroughs (Claude, Track B) → verify → register
a module that reuses the shared primitives. Platform, shell, scrubbing, TTS, mock-exam engine, and the
whole backend are reused untouched. It is content work, not engineering.

> The one cost that never disappears: **verification.** Generated content can contain errors, and a
> paid product cannot ship a wrong explanation. Every course pays for a human/expert check before it
> ships. That's a quality gate, not an engine — but it's real per-course effort.

## Architecture (three tiers + one principle)

```
Desktop client (Avalonia, cross-platform)
  ├─ Core: DAG, scene engine, generated walkthroughs, verified item bank — offline (CS A adds the live TRACER)
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

## TRACK A — Desktop Core (the offline paid product)

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

### Phase 3 — Java-subset tracer (CS A's live engine — the long pole, and CS-A-only)
Seeds: `Tracer.cs`, `PHASE3-HANDOFF.md`. Depends on: 2. **This phase exists only for CS A** — see
Course model. Other courses skip it entirely.
Goal: ANTLR parse → subset validator → tree-walking interpreter emitting `VisualStep[]` for Units
1–5 constructs; wire it as the CS A module's `IStepProvider` with `SupportsLiveInput = true`.
Acceptance: the closing-loop test — feed the phase-2 demo Java through the tracer and get a step
stream whose deltas match the hand-written fixture; determinism; unsupported-construct halts cleanly.

### Phase 4 — TTS + guided walkthroughs
Depends on: 3. Goal: narrate each `VisualStep.Caption`; port the GeoTutor TTS layer; per-step audio
synced to the highlight. Acceptance: a walkthrough plays end-to-end with narration and step controls.

### Phase 5 — Shared primitive library + content integration + FRQ sandbox
Depends on: 3, and Track B Phase 7 for content.
Goal (platform — reused by every course): build out the **shared primitive library** in the `Scene`
project — the general visual vocabulary (`indexedStrip`, `grid2d`, `callTree`, `exprBubble`,
`boolTruthGlow`, plus generic labeled boxes / arrows / axes / points / lines) that all courses draw
from. Treat these as course-agnostic; a Physics or Chem walkthrough should render from this same set
with no new rendering code. Then load verified content via `IContentSource`.
Goal (CS-A-specific): extend the tracer/validator to Units 6–10, and add the local FRQ sandbox
(bundled `javac`/`java`) with rubric grading via `IAttemptGrader`.
Acceptance: every CS A DAG node renders its declared `viz` from the shared library; the item bank +
authored walkthroughs drive practice; an FRQ attempt is compiled, run, and scored against a rubric.

### Phase 6 — Mock exam + mastery reporting
Depends on: 5. Goal: timed MCQ/FRQ mode; weak-node surfacing back into the DAG frontier; progress
reporting. Acceptance: a full timed mock runs; weak nodes reappear in `MasteryTracker.Available()`.

**End of Track A = a shippable, fully-offline Core product.**

---

## TRACK B — Content authoring pipeline (build-time; run by you, not the user)

### Phase 7 — Content factory + verification (this is how EVERY course is authored)
Depends on: 1 (needs a DAG). Runs offline; parallelizes with Track A after Phase 1.
Goal: a tool that uses the Claude API at BUILD TIME to generate, per DAG node, a course's practice
items, explanations, and **animated walkthroughs authored as `SceneDelta` streams** — then a
human/expert verification pass, then export to the format `IContentSource` (and the content store)
load. This same pipeline authors CS A now and Physics/Chem/Bio later; a generated-content course is
produced *entirely here*, with no new engineering. This is the engine of the whole multi-course
product — the thing that makes "you generate the content" real.
Guardrails: 100% original items — never College Board question text. Nothing ships unverified — a paid
product cannot ship a wrong explanation. One-time API spend per course (~$10–50), by you.
Acceptance: a verified item bank + authored walkthroughs for CS A Units 1–5 exist and load in Core
(Phase 5 consumes them). The exporter is course-agnostic — pointing it at a new DAG produces a new
course's content with no code changes.

---

## TRACK C — Hosted Plus backend (the recurring tier)

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
Plus margin (or adjust). Acceptance: COGS/active user measured; pricing decision grounded in data,
not the illustrative numbers used so far.

---

## Critical path & parallelism

- **Long pole: Phase 3 (tracer) — but CS-A-only.** It gates CS A's Phases 4–6 and deserves your best
  model (Opus) time. No other course needs it, so it never gates anything again.
- **Adding a course is a Track B task, not engineering.** A new generated-content course (Physics,
  Chem, ...) = generate its DAG + items + walkthroughs (Phase 7) → verify → register an
  `ICourseModule` (`SupportsLiveInput = false`) that reuses the shared primitives. Platform, shell,
  scrubbing, TTS, mock-exam, and backend are all reused untouched.
- **Track B (content) parallelizes** with Track A after Phase 1 — it only needs the DAG.
- **Track C (backend) can start early** (Phase 8 needs no client), but Phases 9/11 need Phase 7's
  content format and the client from Track A.
- Ship Core (Track A + B) first as the paid product; bring Plus (Track C) online as the upsell.

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
