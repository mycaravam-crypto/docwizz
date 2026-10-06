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
```

Then, in your repository:

```bash
docwizz setup .
```

That one command detects the stack, writes a `docwizz.yaml` from what it finds, analyzes the code and the
architecture, generates the docs into `docs/` and runs the quality gate. It ends with a summary and the next commands
to run:

```console
Setup complete.

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
Next useful actions:
  docwizz generate .       # refresh documentation
  docwizz check .          # run the quality gate
  docwizz architecture .   # inspect architecture findings
```

It never overwrites an existing `docwizz.yaml` without `--force`, and it never sends anything anywhere: `--ai` stays
opt-in. To try it first, run `docwizz analyze fixture` on the bundled sample app. [CLI.md](CLI.md) has every
workflow by goal (docs, architecture, CI, AI, impact analysis), every command and flag, and how they combine.

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
...
Test traceability (2 of 14 changed symbols linked) — a link means test code uses the symbol; it is not code coverage
  ~ Orders  ← OrdersTests.Places (via Orders.Place)
  ~ Orders.Place(int)  ← OrdersTests.Places
  No linked test (12)
    + Orders.Refund(int)  [high]
```

Test traceability lists, for every added and changed symbol, the test code linked to it by `tests` edges: directly, or
through its interface, an implementation or one of its members (*via*). Symbols no test code links to are listed
apart, with their documentation level. A link only says test code *uses* the symbol, not that it covers or checks it.
`--format json` gives the same report as data (`tests.linked`, `tests.unlinked`, each link `direct` or `indirect`).
Module pages show the linked tests per component, and `quality.md` marks tested items.

### Gate pull requests

`docwizz check` exits with 1 when thresholds fail. With `--since`, it fails only on what the change *introduces*, so
an old codebase can adopt it without fixing everything first. That covers critical gaps, violations and docs that
now contradict the code (fact quality flags). Docs that are *possibly stale* are listed but don't fail the check:
a symbol whose parameters, return type, exceptions or route changed while its doc comment stayed the same. With
`check.require_tests: high` (or `medium`), a new symbol at that documentation level that no test code links to fails too.

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
      - uses: actions/checkout@v7
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
| `docwizz setup [dir] [--force]` | **First run:** detect the stack, write `docwizz.yaml` from evidence, analyze, generate the docs, check |
| `docwizz analyze <dir>` | Documentation report |
| `docwizz architecture <dir>` | Layers, layer dependencies, violations, cycles |
| `docwizz check <dir> [--since <ref>]` | Report; exit 1 if thresholds fail (only on new problems with `--since`) |
| `docwizz generate <dir> [out] [--html] [--ai]` | Write the docs (default `<dir>/docs`) |
| `docwizz diff <dir> [ref]` | Changed symbols, affected pages and linked tests vs the last `generate` (or a git ref) |
| `docwizz diff [dir] <base> <head>` | The same, between two git refs |
| `docwizz init [dir]` | Write a starter `docwizz.yaml` with every default, ready to edit |
| `docwizz scan <dir> [model.json]` | Dump the raw code model (nodes and edges); for debugging |

`docwizz help <command>` explains one command. [CLI.md](CLI.md) is the full reference: every option, defaults,
precedence and exit codes. Options: `--profile <name|file.yaml>` picks what counts as documented. `--format json` gives machine-readable
`analyze`/`check`/`architecture`/`diff` output. Run `dotnet test --project tests/DocWizz.Tests` for the unit and component tests and `./test.sh` for the end-to-end
tests against `fixture/` (see [ARCHITECTURE.md](ARCHITECTURE.md#tests)). `--timings` prints the time per stage and
peak memory; [bench/](bench/README.md) has reproducible benchmarks, the baseline, and advice for large repositories and
monorepos.

## Configuration

`docwizz setup` writes a `docwizz.yaml` from what the repository shows (`docwizz init` writes the plain defaults).
Edit it in place. docwizz looks for the file in the scanned directory first,
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

A pattern lists the sections an item needs. Sections are XML doc tags (`summary`, `param`, `returns`, `exception`,
`example`, `remarks`, or any custom tag) or derived ones that the code itself provides (`dependencies`, `endpoint`,
`input`, `output`, `authorization`, `events`, `state`, `side_effects`).

Besides XML doc and JSDoc comments, docwizz reads these plain comments:

- a minimal-API endpoint: a comment above the `Map*` statement, or above an `if` whose only statement it is
- a Vue component: an HTML comment before the first block, or a comment in the `<script>` before its first function
- a Vue prop: a `/** */` or `//` comment directly above it in `defineProps`

### Doc quality

Coverage says a section exists. Doc quality says whether written docs can be trusted: the share of items with written
docs that have no quality flag. `analyze` and `quality.md` list each flag and whether it is a *fact* (the doc
contradicts the code) or *inferred* (a heuristic).

| Flag | Basis | Fires when |
|---|---|---|
| `param-drift` | fact | a `<param name>` that isn't one of the parameters (parameters are also matched by name for coverage) |
| `returns-on-void` | fact | a C#/Java method documents `<returns>` but returns nothing (`void`, `Task`) |
| `empty-inheritdoc` | fact | a C# `<inheritdoc/>` with nothing to inherit from, or only from interface members without docs; it then counts as no docs |
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

#### Custom rules

Add your own fitness functions under `architecture.rules`. Each says what code (`from`) must not reach (`forbid`):

```yaml
architecture:
  rules:
    - id: ARCH-DOMAIN-001
      severity: high                                   # default medium; architecture.severity overrides it
      description: The domain stays persistence-ignorant.
      from: { layer: domain }
      forbid: { package: [Microsoft.EntityFrameworkCore, org.springframework.data] }   # C# using / Java import, by prefix
    - id: ARCH-API-001
      from: { tag: controller }
      forbid: { to: { tag: dbcontext }, edge: [injects, accesses] }                     # only these edge kinds
    - id: BC-ORDERING-001
      from: { path: "src/Ordering/*" }
      forbid: { to: { path: "src/Billing/*" } }
      except: { path: "src/Billing/Contracts/*" }                                       # the declared contract stays allowed
```

- **Selectors** (`from`, `forbid.to`, `except`) match `layer`, `path` (glob), `kind`, `tag` and `name` (glob); every
  field set must match. `kind`, `tag` and `name` also match through the containing type, so a controller's methods count
  as the controller.
- **`forbid`** takes `to` (a selector), `package` (namespace prefixes), or `edge` (edge kinds: by default the dependency
  kinds; `accesses`, `connects`, `reads` and the others in [ARCHITECTURE.md](ARCHITECTURE.md#the-code-model) can be
  named). `edge` alone forbids every edge of those kinds.
- Findings carry the rule id, severity, source file, target and the edge that shows it. They count for `architecture`,
  `check` and `check --since` like the built-in rules, and `architecture.md` lists the rules apart from the built-in ones.
- A rule that can't work (no id, a reserved id, an unknown layer, edge kind or key) stops docwizz with a message naming it.

### Security rules

Opt in with `security: { enabled: true }` for findings a security review should look at. Each one keeps what the code
shows (*detected*, with whether that detection is a fact or an inference) apart from the risk it may carry, and names
the concern behind it. They support a review; they are not an ISO/IEC 27001 assessment and claim no compliance.

| Rule | Fires when | Concern | Detection | Default severity |
|---|---|---|---|---|
| SEC-001 | a read endpoint declares neither authorization nor anonymous access | confidentiality | fact | medium |
| SEC-002 | a mutating endpoint (POST/PUT/PATCH/DELETE) doesn't require authorization (none declared, or anonymous) | integrity | fact | high |
| SEC-003 | API-layer code, a controller or an endpoint injects, accesses, calls or creates a DbContext | integrity | fact | medium |
| SEC-004 | domain code imports a security or web framework (ASP.NET Core, Microsoft.Identity, Spring Security/Web, Servlet, JAX-RS) | integrity | fact | medium |
| SEC-005 | an endpoint without required authorization can reach a sensitive external system (`security.sensitive`: database, storage, identity, email, messaging, cache) | confidentiality | inferred | high |

```yaml
security:
  enabled: true
  severity: { SEC-001: low }        # per-rule override
  sensitive: [database, storage]    # categories SEC-005 treats as sensitive
```

Authorization comes from endpoint metadata: ASP.NET `[Authorize]`/`[AllowAnonymous]` on controllers and actions,
`RequireAuthorization()`/`AllowAnonymous()` on minimal APIs and their `MapGroup`; Spring `@PreAuthorize`, `@Secured`,
`@RolesAllowed`, `@PermitAll`. A policy configured elsewhere (an ASP.NET fallback policy, a Spring security filter
chain) is invisible, which is why each risk says "unless". An explicitly anonymous read is a decision, not a finding.
Findings appear in `architecture` (their own section), count for `check` and `check --since` like violations, and are
listed under Security in `architecture-description.md`.

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
- **Rates written docs too.** The same model scores each written doc from 1 (says nothing the name doesn't) to 5
  (purpose, constraints, side effects, errors) and names what is missing. The scores are listed under *Usefulness 🤖*
  in `quality.md`. They are advisory: not part of doc quality %, never used by `check`. They are cached in
  `docs/.docwizz/ai-assessments.json` by symbol, doc and code hash.

`OLLAMA_HOST` picks the server (default `localhost:11434`). `DOCWIZZ_MODEL` picks the model (default
`qwen2.5-coder:7b`).
