# docwizz
Small tool, creates documentation. Builds a semantic model of the code first; documentation is one output of it.
Finds the code that *needs* docs and doesn't have them, and checks the architecture while it's at it.

```bash
docwizz scan <dir> [model.json]   # write the code model (nodes + edges: facts only)
docwizz analyze <dir>             # documentation report
docwizz check <dir>               # report, exit 1 if thresholds fail (CI)
docwizz architecture <dir>        # layers, layer dependencies, violations, cycles
docwizz generate <dir> [out]      # Markdown + Mermaid docs (default <dir>/docs)
docwizz generate <dir> --ai       # + Claude-drafted summaries for undocumented items (needs ANTHROPIC_API_KEY)
docwizz diff <dir> [ref]          # changed symbols + affected doc pages (default baseline: docs/.docwizz/model.json)
docwizz diff [dir] <base> <head>  # same, between two git refs (docwizz diff HEAD~1 HEAD)
docwizz check <dir> --since <ref> # CI: fail only on critical gaps / violations introduced since <ref>
  --profile <name>                # documentation profile (see below)
  --format json                   # analyze/check/architecture as JSON
./test.sh                         # smoke test against fixture/
```

Vue/TS support needs Node and a one-time `npm ci` in [scanner-vue/](scanner-vue/) (C# works without it).

## Pipeline

Source → analyzers (Roslyn for C#, TypeScript + Vue compiler for `.vue`/`.ts`, `.csproj`/`package.json`) →
**code model** ([CodeModel.cs](src/DocWizz/CodeModel.cs)) → documentation analysis + architecture rules →
**documentation model** → generators (Markdown/Mermaid, JSON). Analyzers never write Markdown.

The code model holds facts only: symbols, signatures, endpoints (route, input, response, authorization), props/emits,
projects and packages, and relationships (`contains`, `calls`, `implements`, `inherits`, `injects`, `creates`, `registers`,
`imports`, `renders`, `routes-to`, `persists`, `publishes`, `subscribes`, `http`, `references`, `depends-on`, `tests`).
Every node records its `language` (csharp, vue, typescript, msbuild, npm); C# types carry roles as tags
(controller, service, repository, entity, dbcontext, background-service, middleware, hub, options).
Test code (`tests:` globs) only contributes `tests` edges.

Each documentation item records which sections its profile requires and where each present section comes from:
`written` (doc comment), `fact` (derived from the model: dependencies, endpoint, input, output, authorization, events,
state — a Vue component's refs, computed values and watchers),
`inferred` (heuristics: side effects) or `ai` (drafts — shown with 🤖, never close a gap), plus the source symbols it
was derived from. `generate` writes it to `docs/.docwizz/documentation.json`.

## Profiles

`default`, `software`, `api`, `architecture`, `technical-publication`, `iso-42010`, `iso-15289` —
see [Profiles.cs](src/DocWizz/Profiles.cs). Reports state coverage *against the profile*; nothing claims compliance
with a standard. `iso-42010`/`iso-15289` also expect human-authored pages in `docs/architecture/`
(stakeholders, concerns, decisions, …) and fail `check` while they are missing.

## Config

`docwizz.yaml` in the scanned dir (or cwd). Without one, `Config.Default` in [Analyzer.cs](src/DocWizz/Analyzer.cs)
applies — copy it as a starting point. `profile:` picks the profile; `patterns:` replaces its patterns. Sections are
XML doc tag names (`summary`, `param`, `returns`, `exception`, `example`, `remarks`, any custom tag) or the derived
ones above. Architecture rules: `layers` (path globs) and `allow`; ARCH-001 forbidden direction, ARCH-002 direct HTTP,
ARCH-003 module cycle, ARCH-004 layer bypassed.

## Generated docs

`index.md` (technology from project files, building blocks by role, sizes), `architecture.md` (dependency view), `api.md`, `frontend.md`, `quality.md`, `modules/*`,
`views/` (context, containers, components, data, deployment) and `architecture-description.md` (structured after
ISO/IEC/IEEE 42010). Pages start with a marker and are regenerated; anything without it — including everything you
write in `docs/architecture/` — is linked, never overwritten.

`--ai` sends each undocumented item's facts (graph neighbours, signature, derived sections, side effects) and its own
source lines to `claude-opus-5` (low effort, server-side refusal fallbacks). Drafts are marked 🤖, never replace
written docs, and are cached in `docs/.docwizz/ai-cache.json` by symbol + body hash together with the symbols they
were generated from, so unchanged code is never sent again. Without `--ai`, cached drafts are still used and nothing is sent.
