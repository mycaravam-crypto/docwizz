# docwizz

**Point it at a repository and find out what's undocumented, what breaks the architecture, and how a request travels
through the code. Then generate the docs that can be derived from the code itself.**

docwizz reads C#, Java/Spring, PHP, Vue, React, Angular, TypeScript, SQL and deployment files and builds a model of the
code: symbols, endpoints, calls, dependencies and external systems. Everything it reports comes from that model and is
labeled *detected*, *inferred* or *AI-drafted*. It never runs your build or tests (unless you ask
`remediate --validate` to) and never sends code anywhere: AI drafting is opt-in and local only.

| You get | |
|---|---|
| **Gaps** | public, complex or side-effecting code without docs, ranked by how much it needs them |
| **Architecture** | layer violations, cycles, risky patterns, optional custom and security rules |
| **Docs** | Markdown or HTML pages with Mermaid diagrams: API, request flows, system context, data, deployment |
| **CI gate** | fails a pull request only on problems *that change introduces* |
| **Extras** | context for coding agents, SBOM, package updates, requirement traceability, user guides with screenshots |

## Quick start

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), plus Node.js 22 for Java, Vue, React, Angular or
TypeScript.

```bash
git clone https://github.com/mycaravam-crypto/docwizz && cd docwizz
dotnet build src/DocWizz -c Release
npm ci --prefix scanner-vue        # only for Java/Vue/React/Angular/TypeScript
alias docwizz="dotnet $PWD/src/DocWizz/bin/Release/net10.0/DocWizz.dll"
```

To install it as a .NET tool instead, see [Getting started](docs/getting-started.md). Then, in your repository:

```bash
docwizz setup .
```

`setup` inventories the projects, detects the stack, writes a `docwizz.yaml` from what it finds, analyzes code and
architecture, generates `docs/` and runs the quality gate:

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

`SUCCESS_WITH_FINDINGS` (exit 0) is the normal first result: the findings are your starting point. `FAILED` (exit 1)
means docwizz could not run a stage, for example because `docwizz.yaml` is invalid.

All output below comes from [fixture/](fixture), a small ASP.NET API with Vue and React frontends and some deliberate
mistakes: run the commands on it to try docwizz without your own code.

## Workflow

### Find the gaps

```console
$ docwizz analyze fixture
Documentation  ███████░░░░░░░░░░░░░ 37%

Critical (10)
────────────────────────────────────────
  backend/Api/MaterialController.cs:18  Fixture.Api.MaterialController.Create(string, int, string, string, string, bool)
    undocumented, missing: summary, param  [public; 6 params; side effects: db, event; pattern endpoint]
  ...
```

Each item says where it is, what is missing and *why* it needs docs; trivial code is never flagged. What counts as
documented depends on the [profile](docs/configuration.md#profiles).

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

Layers, allowed dependencies and your own rules live in `docwizz.yaml`: [architecture rules](docs/configuration.md#architecture-rules).

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

You get an index, architecture, API, frontend and quality pages, a page per module and per frontend route, and views
for context, containers, components, data and deployment. `api.md` traces every endpoint to where it ends up:

```markdown
- `PUT /api/materials/{id}`: MaterialController → MaterialService → SqlMaterialRepository → AppDbContext → SQL Server (inferred)
```

Only changed pages are rewritten, and pages you write yourself are linked, never overwritten. A local model can draft
the missing summaries (`--ai`). See [Generating documentation](docs/guides/generation.md).

### Gate pull requests

```console
$ docwizz check . --since origin/main
check: FAIL — introduced 0 critical gaps, 1 violations
```

With `--since`, only what the change *introduces* fails, so an existing codebase can adopt the gate without fixing
everything first. As a GitHub Action, it posts one pull request comment and updates it on every push:

```yaml
on: pull_request
permissions: { contents: read, pull-requests: write }
jobs:
  docwizz:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with: { fetch-depth: 0 }                # the base commit must be in the checkout
      - uses: mycaravam-crypto/docwizz@vX.Y.Z   # a release tag or its commit SHA; branches are refused
```

Inputs, other CI systems and `docwizz diff`: [Quality checks and change review](docs/guides/quality.md).

## More

- [Coding agents](docs/guides/agent-context.md): token-budgeted context per symbol, and a read-only MCP server.
- [Requirements and traceability](docs/guides/requirements.md): Azure DevOps work items and GitHub issues linked to
  code through exported history, and ADO test cases linked to requirements.
- [Screenshots and user guides](docs/guides/screenshots.md): route screenshots and step-by-step guides captured from
  the running application.
- [Domain discovery](docs/guides/domain-discovery.md): follow one endpoint or work item through the code.
- [Dependencies](docs/guides/dependencies.md): CycloneDX SBOM and evidence-backed package updates.
- [Load-test results](docs/guides/load-reports.md): measured latency per endpoint from powerwizz reports.
- [Product documents](docs/guides/product-documents.md): V-Modell XT and IT operations drafts from code and evidence,
  with document control and signed DOCX/PDF publication.
- [Multi-project repositories](docs/guides/multi-project.md): choose which projects to document.

All guides: [documentation index](docs/README.md).

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
| EF6 EDMX | conceptual entities, associations and their links to C# code ([EDMX](docs/reference/edmx.md)) |

External systems (databases, caches, brokers, HTTP APIs, identity, storage, e-mail) are *detected* when a call shows
them (`UseNpgsql`, `fetch('https://…')`), *inferred* when only a package points to them, and *unknown* otherwise.

## Commands

| Command | Does |
|---|---|
| `docwizz setup [dir]` | first run: discover, configure, analyze, generate, check |
| `docwizz analyze <dir>` | documentation gaps and quality |
| `docwizz architecture <dir>` | layers, violations, cycles, security findings |
| `docwizz generate <dir> [out]` | write the docs |
| `docwizz check <dir>` | quality gate; exit 1 on failure |
| `docwizz diff <dir> [ref]` | changed symbols, affected pages, linked tests |
| `docwizz discover [dir]` | project and solution inventory |
| `docwizz context <dir> --for <target>` | token-budgeted facts for coding agents |
| `docwizz mcp <dir>` | read-only MCP server for coding agents |
| `docwizz domain discover\|explain\|areas <dir>` | follow code and requirements from one start point |
| `docwizz requirements import\|show\|query <dir>` | work items behind a symbol, from exported history |
| `docwizz tests import <dir> <snapshot.json>` | ADO test-case traceability |
| `docwizz trace <dir> <evidence.yaml>` | requirement → architecture → test gaps |
| `docwizz evidence-diff <dir> <before> <after>` | changed evidence and affected tests |
| `docwizz product [check\|publish] <dir> <template.yaml>` | work-product draft from code and evidence |
| `docwizz load import <dir> <report.json>` | powerwizz load-test results per endpoint |
| `docwizz sbom [dir] [out]` | CycloneDX 1.6 from manifest dependencies |
| `docwizz remediate <dir>` | package update suggestions |
| `docwizz edmx <dir> <file.edmx>` | EF6 EDMX facts, graph or code links |
| `docwizz ai check [dir]` | test the local AI configuration |
| `docwizz init [dir]` | manual setup: a `docwizz.yaml` with every default |
| `docwizz scan <dir> [model.json]` | advanced: dump the raw code model |

`docwizz help <command>` explains one command. Every flag, default, precedence rule and exit code:
[CLI reference](docs/cli.md). Settings: [Configuration](docs/configuration.md).

## Development

```bash
dotnet test --project tests/DocWizz.Tests   # unit, component and journey tests (journeys need npm ci --prefix scanner-vue)
./test.sh                                   # end-to-end tests against fixture/
```

`DOCWIZZ_DLL=$(scripts/build-test-cli.sh) dotnet test …` runs the journey tests against a ReadyToRun build in about half
the time. How it works: [architecture](docs/internals/architecture.md). Benchmarks: [bench/](bench/README.md). Every
merge to `main` is released; label a PR `release:minor` or `release:major` for bigger bumps
([versioning](docs/cli.md#versioning)).
