# docwizz

**Point it at a repository and find out what's undocumented, what breaks the architecture, and how a request travels
through the code.** It then writes the docs that can be derived from the code itself.

docwizz reads C#, Java/Spring, Vue, React, Angular, TypeScript, SQL and deployment files. It builds a model of the code
(symbols, endpoints, calls, dependencies, external systems) and reports on it. Everything it says comes from the code.
It never guesses intent, and it marks whether each item is *detected*, *inferred* or *AI-drafted*.

- **Gaps:** which public, complex or side-effecting code has no docs, ranked by how much it needs them.
- **Architecture:** layer violations, cycles and risky patterns (for example, a controller talking to the database).
- **Docs:** Markdown/HTML pages with Mermaid diagrams: API tables, request flows, system context, data, deployment.
- **CI:** fails a pull request only on problems *that change introduces*.

How it works: [ARCHITECTURE.md](ARCHITECTURE.md). What's next: [ROADMAP.md](ROADMAP.md).

## Quick start

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download). For Vue, React, Angular or TypeScript code you also
need Node.

```bash
git clone https://github.com/mycaravam-crypto/docwizz && cd docwizz
dotnet build src/DocWizz -c Release
npm ci --prefix scanner-vue        # only needed for Vue/React/Angular/TypeScript
alias docwizz="dotnet $PWD/src/DocWizz/bin/Release/net10.0/DocWizz.dll"

docwizz analyze fixture            # try it on the bundled sample app
```

## Examples

All output below is real, from the sample app in [fixture/](fixture/). It is an ASP.NET API with a Vue and React
frontend and some deliberate mistakes.

### What's missing docs?

```console
$ docwizz analyze fixture
Profile: default
Documentation  ███████░░░░░░░░░░░░░ 37%

  controller         33%  (3)
  endpoint           35%  (10)
  service            50%  (2)

Critical (10)
────────────────────────────────────────
  backend/Api/MaterialController.cs:18  Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)
    undocumented, missing: summary, param  [public; 6 params; side effects: db, event; pattern endpoint]
  backend/Application/MaterialService.cs:13  Fixture.Application.MaterialService.CreateAsync(...)
    partial, missing: param  [public; complexity 13; 6 params; side effects: db, event]
  ...
```

Each line tells you where the item is, what is missing, and *why* it needs docs. Trivial code, such as a
two-line getter, is never flagged.

### Does the code follow the architecture?

```console
$ docwizz architecture fixture
Architecture (3 violations, 1 cycles)
────────────────────────────────────────
  ARCH-001  domain → infrastructure  [high]
    backend/Domain/Material.cs → backend/Infrastructure/SqlMaterialRepository.cs
    (Fixture.Domain.Material.Save calls Fixture.Infrastructure.SqlMaterialRepository.AddAsync)
  ARCH-002  ui → http  [medium]
    frontend/src/components/MaterialTable.vue → GET /api/materials  (MaterialTable calls HTTP directly)
  ARCH-003  cycle: backend/Domain ↔ backend/Infrastructure
```

### Write the docs

```console
$ docwizz generate fixture docs --html
32 pages → docs (commit af4fd7c): 32 changed
```

That writes `index`, `architecture`, `api`, `frontend` and `quality` pages, one page per folder under `modules/`, and
`views/` for context, containers, components, data and deployment. With `--html` you also get a browsable site with
search. For example, `api.md` traces every endpoint to where it ends up:

```markdown
| GET | `/api/stock/{sku}` | Stock for one article. | `sku: string` | `string` | — | StockController.cs:12 | `level` |

- `GET /api/stock/{sku}`: StockController → ErpClient → erp.example.com
- `PUT /api/materials/{id}`: MaterialController → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)
```

Regenerating rewrites only the pages that changed. Pages you write yourself, such as anything in `docs/architecture/`,
are linked and never overwritten.

### What does my change affect?

```console
$ docwizz diff . HEAD~2 HEAD
Documentation impact (vs HEAD~2, at HEAD)

Changed (11)
  ~ CSharpScanner.Doc(Microsoft.CodeAnalysis.ISymbol)
  ~ Generator.DeploymentView()
  ...
Added (3)
  + Generator.HostKind(System.Collections.Generic.List<string>)
```

### Gate pull requests

`docwizz check` exits with 1 when thresholds fail. With `--since`, it fails only on what the change *introduces*, so
an old codebase can adopt it without fixing everything first:

```console
$ docwizz check fixture
check: FAIL — coverage 37% < 80%; 10 critical > 0; 3 violations > 0; 1 cycles > 0

$ docwizz check . --since origin/main
check: FAIL — introduced 0 critical gaps, 1 violations
```

As a GitHub Action, it posts the result as one comment on the pull request and updates it on every push. The comment
also lists ADR candidates: a new external system or layer dependency, for a human to record.

```yaml
on: pull_request
permissions: { contents: read, pull-requests: write }
jobs:
  docwizz:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }          # the base commit must be in the checkout
      - uses: mycaravam-crypto/docwizz@main
        with:
          path: .                         # directory to scan
          # base: origin/main             # default: the pull request's base commit
          # comment: 'false'              # only the job summary
          # fail: 'false'                 # report, never fail
```

### Draft the missing summaries with a local AI

```bash
ollama pull qwen2.5-coder:7b
docwizz generate . --ai
```

Drafts are marked 🤖, cite the facts they rest on, and never close a gap or replace what a human wrote.
See [AI drafts](#ai-drafts) for the privacy rules.

## Commands

| Command | Does |
|---|---|
| `docwizz init [dir]` | Write a starter `docwizz.yaml` with every default, ready to edit |
| `docwizz analyze <dir>` | Documentation report |
| `docwizz architecture <dir>` | Layers, layer dependencies, violations, cycles |
| `docwizz check <dir> [--since <ref>]` | Report; exit 1 if thresholds fail (only on new problems with `--since`) |
| `docwizz generate <dir> [out] [--html] [--ai]` | Write the docs (default `<dir>/docs`) |
| `docwizz diff <dir> [ref]` | Changed symbols and affected pages vs the last `generate` (or a git ref) |
| `docwizz diff [dir] <base> <head>` | The same, between two git refs |
| `docwizz scan <dir> [model.json]` | Dump the raw code model (nodes and edges) |

Options: `--profile <name|file.yaml>` picks what counts as documented. `--format json` gives machine-readable
`analyze`/`check`/`architecture` output. Run `./test.sh` to smoke-test against `fixture/`.

## Configuration

Run `docwizz init` and edit the `docwizz.yaml` it writes. docwizz looks for the file in the scanned directory first,
then in the current one. This repository's own [docwizz.yaml](docwizz.yaml) is a working example. The settings people
change most:

```yaml
profile: aspnet                     # what needs docs; see Profiles
check:
  min_coverage: 80                  # % of items that need docs and have them
  min_quality: 90                   # optional: % of written docs with no quality flags
  max_critical: 0
  fail_on: medium                   # lowest violation severity that fails check
  max_complexity: 20                # optional: fail on any symbol above this
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

A pattern lists the sections an item needs. Sections are XML doc tags (`summary`, `param`, `returns`, `exception`,
`example`, `remarks`, or any custom tag) or derived ones that the code itself provides (`dependencies`, `endpoint`,
`input`, `output`, `authorization`, `events`, `state`, `side_effects`).

### Doc quality

Coverage says a section exists. Doc quality says whether written docs can be trusted: the share of items with written
docs that have no quality flag. `analyze` and `quality.md` list each flag and whether it is a *fact* (the doc
contradicts the code) or *inferred* (a heuristic).

| Flag | Basis | Fires when |
|---|---|---|
| `param-drift` | fact | a `<param name>` that isn't one of the parameters (parameters are also matched by name for coverage) |
| `returns-on-void` | fact | a C#/Java method documents `<returns>` but returns nothing (`void`, `Task`) |
| `placeholder` | inferred | the summary is TODO/FIXME/TBD, template boilerplate, or under three words |
| `echo` | inferred | every meaningful word of the summary is already in the name, its type or a parameter ("Gets the stock level" on `GetStockLevel`) |

### Architecture rules

| Rule | Fires when | Default severity |
|---|---|---|
| ARCH-001 | a layer depends on one it may not (`allow`) | high |
| ARCH-002 | UI code calls HTTP directly instead of through a client | medium |
| ARCH-003 | two modules depend on each other (cycle) | — |
| ARCH-004 | a forbidden dependency that the allowed layers could have routed (a layer was skipped) | low |

Change a severity with `architecture.severity`. Pages also list *risks*, which break no rule but tend to hurt: entities
returned from the API, business logic in controllers, and domain code bound to external packages.

### Profiles

| Profile | For |
|---|---|
| `default` | general code: endpoints, controllers, services and components by role, a summary for everything else |
| `software` | developer reference: full signatures, returns, exceptions, effects |
| `aspnet` | ASP.NET Core backends: endpoints and the building blocks around them |
| `vue` | Vue frontends: component contracts (props, emits, state), stores, composables |
| `api` | consumers of the HTTP API: every endpoint's contract |
| `architecture` | building blocks and how they connect, rather than every member |
| `technical-publication` | task-oriented docs for readers outside the code; examples required |
| `iso-42010`, `iso-15289` | also expect hand-written pages in `docs/architecture/` (stakeholders, concerns, decisions, …) and fail `check` while they are missing |

Pass your organisation's own profile as `--profile team.yaml` or `profile: team.yaml`. It uses the same shape
(`patterns:`, `architecture_sections:`) as [Profiles.cs](src/DocWizz/Profiles.cs). Coverage is measured *against the
profile*; docwizz never claims compliance with a standard.

## What it reads

| Source | What it gets |
|---|---|
| C# (Roslyn) | types, members, doc comments, complexity, calls, DI, controllers and minimal APIs (routes, inputs, responses, auth), middleware, EF `DbContext`s |
| Java / Spring | controllers, services, repositories, entities, `@*Mapping` endpoints, injection, `application.yml` |
| Vue, React, Angular, TS/JS | components, props, emits, hooks, state, stores, routes, HTTP calls linked to the endpoints they reach |
| SQL | tables, stored procedures, functions, views (which tables each routine touches, which code calls it), migrations in order |
| Projects | `.csproj`/`.sln`, `package.json`, `pom.xml`, Gradle: packages, frameworks, what is executable |
| Configuration | `appsettings*.json`, `.env`, Kubernetes ConfigMaps, Helm `values*.yaml`: key *names* and who reads them, never values |
| Deployment | compose, Kubernetes, Dockerfiles, Terraform/Bicep, Helm, CI pipelines |

External systems (databases, caches, brokers, HTTP APIs, identity, storage, e-mail) are labelled by certainty.
*Detected* means a call shows it (`UseNpgsql`, `fetch('https://…')`). *Inferred* means only a package or a single
candidate points to it. *Unknown* means something is there but the code doesn't say what.

`generate` also writes `docs/.docwizz/documentation.json`. For every item, it lists the required sections, where each
present section comes from (`written`, `fact`, `inferred` or `ai`), and the evidence (`file:line-endLine`).
`search.json` indexes every component, endpoint, route, key and module.

## AI drafts

`--ai` asks a **self-hosted [Ollama](https://ollama.com)** to draft what's missing: summary, responsibilities,
behaviour, side effects, errors and usage for each item, plus an overview per module.

- **Only local or private addresses.** The resolved address must be loopback, 10/8, 172.16/12, 192.168/16 or an IPv6
  unique-local address. Proxies are bypassed, and Ollama `…-cloud` models are refused.
- **Grounded.** Each sentence cites the facts it rests on, and sentences that cite nothing are dropped.
- **Never authoritative.** Drafts are marked 🤖, fill only empty sections and never close a gap.
- **Sent once.** Drafts are cached in `docs/.docwizz/ai-cache.json` by symbol and code hash, so unchanged code isn't
  sent again. Without `--ai`, cached drafts are still used and nothing is sent.

`OLLAMA_HOST` picks the server (default `localhost:11434`). `DOCWIZZ_MODEL` picks the model (default
`qwen2.5-coder:7b`).
