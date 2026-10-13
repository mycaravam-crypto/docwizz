# docwizz

**Point it at a repository and find out what's undocumented, what breaks the architecture, and how a request travels
through the code. Then generate the docs that can be derived from the code itself.**

docwizz reads C#, Java/Spring, PHP, Vue, React, Angular, TypeScript, SQL and deployment files and builds a model of the code:
symbols, endpoints, calls, dependencies and external systems. Everything it reports comes from that model. It never
guesses intent, and it labels every item as *detected*, *inferred* or *AI-drafted*.

| You get | |
|---|---|
| **Gaps** | public, complex or side-effecting code without docs, ranked by how much it needs them |
| **Architecture** | layer violations, cycles, risky patterns, optional custom and security rules |
| **Docs** | Markdown or HTML pages with Mermaid diagrams: API, request flows, system context, data, deployment |
| **CI gate** | fails a pull request only on problems *that change introduces* |
| **Extras** | context for coding agents, CycloneDX SBOM, package-update suggestions, requirement traceability |

It never runs your build, tests or package tools (unless you ask `remediate --validate` to) and never sends code
anywhere: AI drafting is opt-in and local only.

## Contents

1. [Quick start](#quick-start)
2. [Workflow](#workflow): [gaps](#find-the-gaps) · [architecture](#check-the-architecture) ·
   [docs](#generate-the-docs) · [AI drafts](#draft-missing-docs-with-a-local-ai) ·
   [change impact](#see-what-a-change-affects) · [CI](#gate-pull-requests)
3. [Beyond docs](#beyond-docs): coding agents, dependencies, traceability
4. [Configuration](#configuration)
5. [What it reads](#what-it-reads)
6. [Commands](#commands)
7. [Development](#development)

## Quick start

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus Node for Java, Vue, React, Angular or TypeScript.

```bash
git clone https://github.com/mycaravam-crypto/docwizz && cd docwizz
dotnet build src/DocWizz -c Release
npm ci --prefix scanner-vue        # only for Java/Vue/React/Angular/TypeScript
alias docwizz="dotnet $PWD/src/DocWizz/bin/Release/net10.0/DocWizz.dll"
```

Then, in your repository:

```bash
docwizz setup .
```

`setup` inventories projects and solutions, detects the stack, writes a `docwizz.yaml` from what it finds, analyzes the
code and architecture, generates `docs/` and runs the quality gate:

```console
Setup complete, with findings: they are about the repository; docwizz itself ran without errors.

Status:         SUCCESS_WITH_FINDINGS
Stack:          csharp (45 symbols), java (15 symbols), …; ASP.NET Core, Entity Framework Core, Spring Boot, Vue, React, Angular; backend + frontend
Layers:         ui (7), state (1), client (4), api (3), application (6), domain (3), infrastructure (3)
Configuration:  ./docwizz.yaml (written)
Documentation:  ./docs
Coverage:       37% (profile default)
Architecture:   3 findings, 1 cycles
Check:          FAIL — coverage 37% < 80%; 10 critical > 0; 3 violations > 0; 1 cycles > 0

Follow-up:
  - review architecture.allow: the allowed dependencies are defaults, not your architecture's intent
  …
```

- `SUCCESS_WITH_FINDINGS` (exit 0) is the normal first result: the findings are your starting point.
- `FAILED` (exit 1) means a stage could not run, for example an invalid `docwizz.yaml`.
- An existing `docwizz.yaml` is only overwritten with `--force`.
- In a repository with several solutions, `setup` offers to pick the projects to document (see
  [Multi-project repositories](#multi-project-repositories)).

To try it without your own code, run the steps below on the bundled sample in [fixture/](fixture/): an ASP.NET API
with Vue and React frontends and some deliberate mistakes. All output in this README comes from it.

## Workflow

`setup` runs these steps once. Afterwards, run each on its own, in this order: find gaps, check the architecture,
generate the docs, optionally draft with AI, then review and gate every change.

### Find the gaps

```console
$ docwizz analyze fixture
Documentation  ███████░░░░░░░░░░░░░ 37%

  controller         33%  (3)
  endpoint           35%  (10)
  service            50%  (2)

Critical (10)
────────────────────────────────────────
  backend/Api/MaterialController.cs:18  Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)
    undocumented, missing: summary, param  [public; 6 params; side effects: db, event; pattern endpoint]
  ...
```

Each item says where it is, what is missing and *why* it needs docs. Trivial code such as a two-line getter is never
flagged. What counts as documented depends on the [profile](#profiles); written docs are also checked for
[quality](#doc-quality).

### Check the architecture

```console
$ docwizz architecture fixture
Architecture (3 violations, 1 cycles)
────────────────────────────────────────
  ARCH-001  domain → infrastructure  [high]
    backend/Domain/Material.cs → backend/Infrastructure/SqlMaterialRepository.cs
  ARCH-002  ui → http  [medium]
    frontend/src/components/MaterialTable.vue → GET /api/materials  (MaterialTable calls HTTP directly)
  ARCH-003  cycle: backend/Domain ↔ backend/Infrastructure
```

`setup` guesses layers and allowed dependencies; review them in [`docwizz.yaml`](#architecture-rules) so they reflect
your intent.

### Generate the docs

```console
$ docwizz generate fixture docs --html
80 pages → docs (commit 619426e): 80 changed

  Scanned       33 source files → 96 symbols, 10 endpoints, 6 projects, 4 routes
  Coverage      ███████░░░░░░░░░░░░░  37%  4 documented, 3 partial, 8 missing (profile default)
  Doc quality   █████████████████░░░  86%  of 7 items with written docs
  Gaps          10 critical, 1 warning → docs/quality.html
  Architecture  3 violations, 1 cycle → docs/architecture.html
  Pages         80 Markdown + HTML: 80 changed, 0 unchanged
  Time          5.4s  (scan 4.5s · write pages 0.7s · metadata 0.1s)

  Open          docs/index.html
  Next          docwizz generate fixture --ai   drafts 8 missing summaries
                docwizz check fixture   gates coverage in CI
```

The first line is the stable one-line result for scripts; the report below it says what was found and where to look.

You get `index`, `architecture`, `api`, `frontend` and `quality` pages, one page per module, and `views/` for context,
containers, components, data and deployment. `api.md` traces every endpoint to where it ends up:

```markdown
- `GET /api/stock/{sku}`: StockController → ErpClient → erp.example.com
- `PUT /api/materials/{id}`: MaterialController → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)
```

- **HTML** (`--html`): an offline site with sidebar, search (`/`), outline and dark mode. Open `docs/index.html`.
  Mermaid diagrams load from a pinned CDN.
- **Incremental**: only changed pages are rewritten. Pages you write yourself (e.g. `docs/architecture/`) are linked,
  never overwritten. After upgrading docwizz, `--refresh-html-on-version-change` rewrites all HTML pages.
- **Machine-readable**: `docs/.docwizz/documentation.json` lists every item's required sections, where each comes from
  (`written`, `fact`, `inferred`, `ai`) and its evidence (`file:line`). `search.json` indexes components, endpoints,
  routes, keys and modules.

**Route screenshots (optional, no AI).** With a running frontend and Playwright Chromium installed
(`npx playwright@1.63.0 install chromium`):

```bash
# once: sign in interactively and save the browser session (docwizz never sees a password)
npx playwright codegen --save-storage=.auth/docwizz-state.json http://localhost:5173

docwizz generate . --html --screenshots --base-url http://localhost:5173 --storage-state .auth/docwizz-state.json
```

Screenshots land in `docs/screenshots/` and are linked from the frontend page. Without `--storage-state` routes are
captured anonymously; with it, each route continues the previous route's session, so rotating refresh tokens keep
working. Slow pages: add `--screenshot-delay <ms>`. Hash routers are opened as `/#/route`. Dynamic routes, `/logout`, HTTP
errors, redirects, expired sessions and pages whose API calls answer 401/403 are skipped with a warning.
**The state file holds session tokens:** keep it out of Git, and use a low-privilege test account with sanitized data.
Limits are listed in [CLI.md](CLI.md#options); for Windows installation errors, authenticated capture and diagnostics,
see [Playwright screenshot troubleshooting](docs/PLAYWRIGHT_SCREENSHOTS.md).

### Draft missing docs with a local AI

```bash
ollama pull qwen2.5-coder:7b
docwizz generate . --ai
```

A self-hosted model ([Ollama](https://ollama.com) or any OpenAI-compatible server) drafts summaries, behaviour, side
effects, errors and usage for undocumented items, plus an overview for each module (folder) of three or more members.

- **Local only.** The server must resolve to a loopback or private address; proxies are bypassed and Ollama `…-cloud`
  models are refused.
- **Grounded.** Every sentence cites the facts it rests on; sentences that cite nothing are dropped.
- **Never authoritative.** Drafts are marked 🤖, fill only empty sections and never close a gap.
- **Sent once.** Drafts are cached in `docs/.docwizz/ai-cache.json` by code hash, answers the model got wrong too,
  so a later `--ai` run sends only changed code. Without `--ai`, cached drafts are reused and nothing is sent.
- **Rates written docs.** Each written doc gets an advisory 1–5 usefulness score in `quality.md`; `check` ignores it.

Configure the server under [AI](#ai).

### See what a change affects

```console
$ docwizz diff . HEAD~2 HEAD
Documentation impact (vs HEAD~2, at HEAD)

Changed (11)
  ~ CSharpScanner.Doc(Microsoft.CodeAnalysis.ISymbol)
  ...
Added (3)
  + Generator.HostKind(System.Collections.Generic.List<string>)
...
Test traceability (2 of 14 changed symbols linked) — a link means test code uses the symbol; it is not code coverage
  ~ Orders.Place(int)  ← OrdersTests.Places
  No linked test (12)
    + Orders.Refund(int)  [high]
```

`diff` lists changed symbols, affected pages, updated package versions and the test code linked to each symbol
(directly or through an interface or implementation). Without refs it compares against the last `generate`.

### Gate pull requests

```console
$ docwizz check fixture
check: FAIL — coverage 37% < 80%; 10 critical > 0; 3 violations > 0; 1 cycles > 0

$ docwizz check . --since origin/main
check: FAIL — introduced 0 critical gaps, 1 violations
```

`check` exits 1 when a [threshold](#configuration) fails. With `--since`, only what the change *introduces* counts —
critical gaps, violations and docs that now contradict the code — so an existing codebase can adopt it without fixing
everything first. *Possibly stale* docs (signature changed, comment didn't) are listed but don't fail.

As a GitHub Action it posts one pull request comment, updated on every push, including ADR candidates such as a new
external system or layer dependency:

```yaml
on: pull_request
permissions: { contents: read, pull-requests: write }
jobs:
  docwizz:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with: { fetch-depth: 0 }          # the base commit must be in the checkout
      - uses: mycaravam-crypto/docwizz@main
        with:
          path: .                         # directory to scan
          # base: origin/main             # default: the pull request's base commit
          # comment: 'false'              # only the job summary
          # fail: 'false'                 # report, never fail
```

## Beyond docs

### Context for coding agents

- `docwizz context . --for <symbol|file|folder|endpoint> --budget 4000` prints token-budgeted facts with provenance.
- `docwizz mcp .` runs a read-only MCP server so agents query symbols, flows, impact and the gate instead of searching
  files.

See [CLI.md](CLI.md#8-context-for-coding-agents) and the [AGENTS.md template](templates/AGENTS.md).

### Dependencies

- `docwizz sbom . sbom.json` exports a CycloneDX 1.6 inventory of the direct dependencies declared in NuGet, npm, Maven
  and Gradle manifests. Nothing is installed or resolved ([details](CLI.md#sbom-inventory)).
- `docwizz remediate` suggests package updates with evidence. It never touches your working tree:

```console
$ docwizz remediate . --package Newtonsoft.Json --to 13.0.3 --validate
DEP-001  Newtonsoft.Json 12.0.3, 13.0.1 → 13.0.3  [major]  (nuget; requested)

Suggested patch (versions are managed centrally; apply with git apply):
  -    <PackageVersion Include="Newtonsoft.Json" Version="12.0.3" />
  +    <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />

Impact (static):
  - projects: src/Api/Api.csproj, src/Worker/Worker.csproj; through project references: tests/Api.Tests/Api.Tests.csproj  [detected]
  - 1 symbols import the package's namespaces (1 public): Api.Codec (src/Api/Codec.cs:3)  [inferred]
  - not assessed: APIs removed or changed between 12.0.3/13.0.1 and 13.0.3

Validation (executed in a temporary copy; passing restore, build and tests is evidence, not proof of behavioral compatibility):
  - restore: pass  `dotnet restore Demo.slnx`  3.2s
  - build: pass  `dotnet build Demo.slnx --no-restore`  5.5s
  - test: pass, 1 passed, 0 failed  `dotnet test Demo.slnx --no-build`  2.2s
Status: validated   Confidence: high
```

Each suggestion gives a copy-paste `dotnet add` command, or a patch when versions are managed centrally; npm, Maven and
Gradle are reported without one for now. Impact covers referencing projects, importing types, affected endpoints and
linked tests. `--validate` copies the repository to a temporary folder, applies the change and runs restore, build and
test there; this executes repository code. Status is `unvalidated`, `validated`, `potentially-breaking` or
`not-actionable`. Without `--package`, it proposes `remediation.targets` and aligns NuGet versions that differ across
projects.

### Requirements and traceability

For projects that keep requirements as YAML evidence:

- `docwizz trace` reports requirement → architecture → test gaps.
- `docwizz evidence-diff` compares two evidence snapshots and the tests they affect.
- `docwizz requirements` shows which Azure DevOps work items a symbol was changed for, from exported commit/PR history:
  `confirmed` (a directly linked commit changed it) or `candidate` (only a PR link), with commit, file and lines.
  `generate --ai --requirements` and `context --requirements` hand that bounded, cited context to a local model or
  agent. See [requirement context](docs/requirement-context.md).
- `docwizz product` drafts a work product from a template, code and evidence; `product check` verifies references and
  freshness. Illustrative V-Modell XT 2.4 templates are in [templates/vmodell-xt/](templates/vmodell-xt/) (not
  official templates or a conformity claim).

See [project evidence](docs/project-evidence.md) and [CLI.md](CLI.md#product-drafts).

## Configuration

`setup` writes `docwizz.yaml` from what the repository shows; `docwizz init` writes plain defaults. docwizz looks for it
in the scanned directory, then the current one. This repository's own [docwizz.yaml](docwizz.yaml) is a working example.

```yaml
profile: aspnet                     # what needs docs; see Profiles
check:
  min_coverage: 80                  # % of items that need docs and have them
  min_quality: 90                   # optional: % of written docs with no quality flags
  max_critical: 0
  fail_on: medium                   # lowest violation severity that fails check
  max_complexity: 20                # optional: fail on any symbol above this
  require_tests: high               # optional: with --since, fail on new high-level symbols no test links to
architecture:
  layers:                           # path globs, first match wins
    api: ["*/Api/*.cs", "*/Controllers/*.cs"]
    domain: ["*/Domain/*.cs"]
  allow:                            # who may depend on whom; `http` = calling HTTP directly
    api: [application, domain, infrastructure]
    domain: []
tests: ["tests/*", "*.Tests/*"]     # only used to link tests to the code they exercise
exclude: ["legacy/*"]               # left out entirely
comment_docs: true                  # count a // comment above a C# member as its summary
```

Every option, its default and precedence: [CLI.md](CLI.md#options).

### Profiles

A profile decides which items need docs and which sections (`summary`, `param`, `returns`, … or derived ones such as
`endpoint`, `authorization`, `side_effects`).

| Profile | For |
|---|---|
| `default` | general code: endpoints, controllers, services and components by role, a summary for everything else |
| `software` | developer reference: full signatures, returns, exceptions, effects |
| `aspnet` | ASP.NET Core backends: endpoints and the building blocks around them |
| `vue` | Vue frontends: component contracts (props, emits, state), stores, composables |
| `api` | consumers of the HTTP API: every endpoint's contract |
| `architecture` | building blocks and how they connect, rather than every member |
| `technical-publication` | task-oriented docs for readers outside the code; examples required |
| `iso-42010`, `iso-15289` | also require hand-written pages in `docs/architecture/` (stakeholders, concerns, decisions, …) |

Use your own with `--profile team.yaml` or `profile: team.yaml`, in the shape of [Profiles.cs](src/DocWizz/Model/Profiles.cs).
Coverage is measured against the profile; docwizz never claims compliance with a standard.

Besides XML doc and JSDoc, plain comments count above a minimal-API `Map*` call, before a Vue component's first block
or function, and above a Vue prop in `defineProps`.

### Doc quality

Coverage says a section exists; quality says whether it can be trusted.

| Flag | Basis | Fires when |
|---|---|---|
| `param-drift` | fact | a `<param name>` that isn't one of the parameters |
| `returns-on-void` | fact | `<returns>` on a method that returns nothing (`void`, `Task`) |
| `empty-inheritdoc` | fact | `<inheritdoc/>` with nothing to inherit; counts as no docs |
| `placeholder` | inferred | TODO/FIXME/TBD, template boilerplate, or under three words |
| `echo` | inferred | the summary only repeats the name, types or parameters ("Gets the stock level" on `GetStockLevel`) |

### Architecture rules

| Rule | Fires when | Default severity |
|---|---|---|
| ARCH-001 | a layer depends on one it may not (`allow`) | high |
| ARCH-002 | UI code calls HTTP directly instead of through a client | medium |
| ARCH-003 | two modules depend on each other (cycle) | — |
| ARCH-004 | a forbidden dependency that skips an allowed layer | low |

Override severities with `architecture.severity`. Pages also list *risks* that break no rule: entities returned from the
API, business logic in controllers, domain code bound to external packages.

**Custom rules** say what code (`from`) must not reach (`forbid`), with optional exceptions:

```yaml
architecture:
  rules:
    - id: ARCH-DOMAIN-001
      severity: high                                   # default medium
      description: The domain stays persistence-ignorant.
      from: { layer: domain }
      forbid: { package: [Microsoft.EntityFrameworkCore, org.springframework.data] }   # by namespace prefix
    - id: ARCH-API-001
      from: { tag: controller }
      forbid: { to: { tag: dbcontext }, edge: [injects, accesses] }
    - id: BC-ORDERING-001
      from: { path: "src/Ordering/*" }
      forbid: { to: { path: "src/Billing/*" } }
      except: { path: "src/Billing/Contracts/*" }
```

Selectors match `layer`, `path`, `kind`, `tag` and `name` (globs where it makes sense); `kind`, `tag` and `name` also
match through the containing type. `edge` takes edge kinds from [the code model](ARCHITECTURE.md#the-code-model).
Custom findings count for `architecture` and `check` like built-in ones; an invalid rule stops docwizz with a message.

### Security rules

Opt in with `security: { enabled: true }`. Findings support a security review; they are not an ISO/IEC 27001
assessment.

| Rule | Fires when | Default severity |
|---|---|---|
| SEC-001 | a read endpoint declares neither authorization nor anonymous access | medium |
| SEC-002 | a mutating endpoint doesn't require authorization | high |
| SEC-003 | API-layer code uses a DbContext | medium |
| SEC-004 | domain code imports a security or web framework | medium |
| SEC-005 | an unauthenticated endpoint can reach a sensitive external system (`security.sensitive`) | high |

Authorization is read from ASP.NET (`[Authorize]`, `RequireAuthorization()`, …) and Spring (`@PreAuthorize`,
`@Secured`, …) endpoint metadata. Policies configured elsewhere, such as a fallback policy or a filter chain, are
invisible, so each finding says "unless".

### Multi-project repositories

- `docwizz discover .` inventories projects, solutions and references without a full scan.
- `docwizz setup . --select-projects` picks projects interactively and writes a `scope:` block; `--all-projects`
  clears it; `--yes` skips prompts in CI.
- Generated docs then navigate index → solutions → projects → modules → components.

The `scope:` schema is in [CLI.md](CLI.md#scope); limits and the acceptance baseline in
[MULTI_PROJECT_ACCEPTANCE.md](MULTI_PROJECT_ACCEPTANCE.md). Classic non-SDK ASP.NET MVC projects are inventoried, but
conventional MVC routing is not fully modelled as endpoints.

### Remediation

```yaml
remediation:
  targets: { Newtonsoft.Json: 13.0.3 }   # versions to propose
  validate:                              # default: restore, build, test with dotnet
    - { name: restore, run: "dotnet restore {target}" }   # {target}: the solution, else each affected project
    - { name: build, run: "dotnet build {target} --no-restore -warnaserror" }
    - { name: test, run: "dotnet test {tests} --no-build" }   # {tests}: the solution, else each affected test project
  timeout_minutes: 20                    # per step
```

### AI

`OLLAMA_HOST` (default `localhost:11434`) and `DOCWIZZ_MODEL` (default `qwen2.5-coder:7b`) select the Ollama server and
model. docwizz sends four requests at once; set `OLLAMA_NUM_PARALLEL=4` on the Ollama server so it runs them together,
which roughly doubles drafting speed on one GPU (by default Ollama may run them one at a time). For an
OpenAI-compatible server (vLLM, NVIDIA NIM, LiteLLM):

```yaml
ai: { provider: openai-compatible, endpoint: http://10.0.0.5:8000/v1, model: <name>, api_key_env: DOCWIZZ_AI_KEY }
```

The key is read only from that environment variable. More in [CLI.md](CLI.md#5-ai-assisted-documentation).

## What it reads

| Source | What it gets |
|---|---|
| C# (Roslyn) | types, members, doc comments, complexity, calls, DI, controllers and minimal APIs (routes, inputs, responses, auth), middleware, EF `DbContext`s |
| Java / Spring | controllers, services, repositories, entities, `@*Mapping` endpoints, injection, `application.yml` |
| PHP | classes, interfaces, traits, enums, functions, PHPDoc, complexity, calls, constructor injection (no framework routes yet) |
| Vue, React, Angular, TS/JS | components, props, emits, hooks, state, stores, routes, HTTP calls linked to the endpoints they reach |
| SQL | tables, procedures, functions, views, which code calls them, migrations in order |
| Projects | `.csproj`/`.sln`, `package.json`, `pom.xml`, Gradle: packages, frameworks, what is executable |
| Configuration | `appsettings*.json`, `.env`, Kubernetes ConfigMaps, Helm values: key *names* and who reads them, never values |
| Deployment | compose, Kubernetes, Dockerfiles, Terraform/Bicep, Helm, CI pipelines |

External systems (databases, caches, brokers, HTTP APIs, identity, storage, e-mail) are *detected* when a call shows
them (`UseNpgsql`, `fetch('https://…')`), *inferred* when only a package points to them, and *unknown* otherwise.

## Commands

| Command | Does |
|---|---|
| `docwizz setup [dir]` | first run: discover, configure, analyze, generate, check |
| `docwizz discover [dir]` | read-only project and solution inventory |
| `docwizz analyze <dir>` | documentation gaps and quality |
| `docwizz architecture <dir>` | layers, violations, cycles, security findings |
| `docwizz generate <dir> [out]` | write the docs (`--html`, `--screenshots`, `--ai`) |
| `docwizz diff <dir> [ref]`, `docwizz diff [dir] <base> <head>` | changed symbols, affected pages, linked tests |
| `docwizz check <dir> [--since <ref>]` | quality gate; exit 1 on failure |
| `docwizz context <dir> --for <target>` | token-budgeted facts for coding agents |
| `docwizz mcp <dir>` | read-only MCP server for coding agents |
| `docwizz sbom [dir] [out]` | CycloneDX 1.6 from direct manifest dependencies |
| `docwizz remediate <dir>` | package update suggestions, optionally validated |
| `docwizz trace <dir> <evidence.yaml>` | requirement → architecture → test gaps |
| `docwizz evidence-diff <dir> <before.yaml> <after.yaml>` | changed evidence and affected tests |
| `docwizz requirements import\|show <dir> ...` | work items behind a symbol, from exported history |
| `docwizz product [check] <dir> <template.yaml> [out.md]` | work-product draft from code and evidence |
| `docwizz init [dir]` | manual setup: starter `docwizz.yaml` with every default |
| `docwizz scan <dir> [model.json]` | advanced: dump the raw code model, for debugging |

Common options: `--profile <name|file.yaml>`, `--format json` (for `analyze`, `check`, `architecture`, `diff`,
`remediate`, …), `--timings`. `docwizz help <command>` explains one command; `docwizz --version` prints version and
commit. [CLI.md](CLI.md) is the full reference: every flag, default, precedence rule and exit code.

## Development

```bash
dotnet test --project tests/DocWizz.Tests   # unit, component and journey tests (journeys need npm ci --prefix scanner-vue)
./test.sh                                   # end-to-end tests against fixture/
```

- How it works: [ARCHITECTURE.md](ARCHITECTURE.md) · what's next: [ROADMAP.md](ROADMAP.md)
- Benchmarks and advice for large repositories: [bench/](bench/README.md)
- Every merge to `main` is released automatically; label a PR `release:minor` or `release:major` for bigger bumps
  ([versioning](CLI.md#versioning)).
