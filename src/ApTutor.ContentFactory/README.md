# ApTutor.ContentFactory

The Phase 7 build-time content pipeline (build-plan.md Track B): the one tool that calls the live
Claude API to generate a course's practice items, explanations, and animated walkthroughs. It is
its own project on purpose — no API key or network dependency ever ships inside `ApTutor.Client`.

This tool was **built but not run** in this session: real generation is real, billed API spend on
your own Anthropic account, so nothing has been generated yet. Everything below is what you'd do
to actually produce and ship content.

## 1. Generate

```
export ANTHROPIC_API_KEY=sk-ant-...

dotnet run --project src/ApTutor.ContentFactory -- generate \
  --dag src/ApTutor.Curriculum/apcsa-skill-dag.json \
  --course csa \
  --content src/ApTutor.Client/content/csa \
  --units 1-5 \
  --difficulty medium \
  --model <a-current-model-id>
```

- `--model` has no default on purpose — model ids change over time, and hardcoding one here risked
  quietly pointing at something stale. Check <https://docs.anthropic.com> for what's current.
- `--units` is optional; omit it to generate every node in the DAG. Ranges/lists both work: `1-5`,
  `1,3,7`, `1-3,9`.
- `--difficulty` is optional (`easy`, `medium`, or `hard`; defaults to `medium`) — see the
  difficulty-levels plan. Run this command once per difficulty you want to stock.
- One `<nodeId>.json` file is written per node under `--content`, each starting `"Verified": false`.
  A failed node (bad JSON, API error) is skipped with a logged reason — it doesn't stop the run or
  leave a partial file.

Expect roughly $10–50 in API spend for a full unit range, per build-plan.md's estimate. Start with
one or two units to sanity-check quality and cost before generating everything.

## 2. Review (required — nothing ships unverified)

```
dotnet run --project src/ApTutor.ContentFactory -- review --content src/ApTutor.Client/content/csa
```

Walks every unverified file, prints the walkthrough text, every practice item (correct choice
marked `*`), and a one-line summary of each walkthrough step's scene ops. For each node:

- `a` — approve. Flips `"Verified": true` in place.
- `r` — reject. Deletes the file so you can regenerate it later (fix the prompt, try again).
- `s` — skip. Leaves it unverified for next time.

This is the human/expert check build-plan.md requires: a paid product cannot ship a wrong
explanation, so **only `Verified: true` files are ever served** — see
`ApTutor.Content.FileContentSource` and `AuthoredStepProvider`, which both skip/throw on anything
still unverified.

## 3. Rebuild the client

`src/ApTutor.Client/content/**/*.json` is a `Content` item with `CopyToOutputDirectory` in
`ApTutor.Client.csproj` — so once you've generated and reviewed content at
`src/ApTutor.Client/content/csa` (the path used above), **rebuild (or re-run) `ApTutor.Client`**
and MSBuild copies it next to the exe, which is where `CsaCourseModule`'s default `contentDir`
(`AppContext.BaseDirectory/content/<courseId>`) actually looks at runtime:

```
dotnet build src/ApTutor.Client
dotnet run --project src/ApTutor.Client
```

From there:

- `Content.GetPracticeItems(nodeId)` merges the Phase 6 fixture bank with whatever verified items
  exist here.
- `StepProvider.GetSteps(nodeId, exampleId)` serves the live tracer's demo for `u2.1` and falls
  back to the authored walkthrough for every other node.

Re-running `review` later (to approve more nodes) needs another rebuild of `ApTutor.Client` to
pick up the change — the copy only happens at build time, not live.

## Reusing this for a new course

Nothing above is CS A-specific except the `--course csa` value and the DAG path. Point `generate`
at a different course's DAG and it produces that course's content the same way — that's the whole
point of Track B (build-plan.md: "a generated-content course is produced *entirely here*, with no
new engineering").
