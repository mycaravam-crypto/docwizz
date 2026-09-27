# docwizz
Small tool, creates documentation. Builds a semantic model of the code first; documentation is one output of it.
Finds the code that *needs* docs and doesn't have them, and checks the architecture while it's at it.
How it works: [ARCHITECTURE.md](ARCHITECTURE.md). What's next: [ROADMAP.md](ROADMAP.md).

```bash
docwizz init [dir]                # write a starter docwizz.yaml (the defaults, to edit)
docwizz scan <dir> [model.json]   # write the code model (nodes + edges: facts only)
docwizz analyze <dir>             # documentation report
docwizz check <dir>               # report, exit 1 if thresholds fail (CI)
docwizz architecture <dir>        # layers, layer dependencies, violations, cycles
docwizz generate <dir> [out]      # Markdown + Mermaid docs (default <dir>/docs)
docwizz generate <dir> --ai       # + summaries for undocumented items, drafted by a self-hosted Ollama
docwizz generate <dir> --html     # + an HTML page next to every Markdown page (links, anchors, Mermaid, search)
docwizz diff <dir> [ref]          # changed symbols + affected doc pages (default baseline: docs/.docwizz/model.json)
docwizz diff [dir] <base> <head>  # same, between two git refs (docwizz diff HEAD~1 HEAD)
docwizz check <dir> --since <ref> # CI: fail only on critical gaps / violations introduced since <ref>
  --profile <name|file.yaml>      # documentation profile (see below)
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
`imports`, `renders`, `routes-to`, `persists`, `publishes`, `subscribes`, `http`, `references`, `depends-on`, `tests`, `connects`,
`accesses` — a member using an injected dependency of its own type, which is how a request reaches a `DbContext` —,
`reads`, `binds`).
External systems (databases, caches, message brokers, HTTP APIs, identity providers, storage, e-mail — see
[Externals.cs](src/DocWizz/Externals.cs)) are `external` nodes tagged with their category and certainty: `detected`
(a call shows it: `UseNpgsql`, `AddHttpClient<T>` with a base address, `fetch('https://…')`), `inferred` (only a package
reference or a single candidate points to it) or `unknown` (a `DbContext` whose database the code doesn't name).
Configuration keys from `appsettings*.json`, `.env` files, Kubernetes ConfigMaps (`configmap/<name>`) and the env blocks of
a Helm chart's `values*.yaml` (`helm`) are `config` nodes tagged with the environments that define them; code that reads a key (`config["A:B"]`, `GetSection`, `GetConnectionString`, `GetEnvironmentVariable`) or binds it
(`Configure<T>`, `AddOptions<T>().BindConfiguration`) points at it (`reads`, `binds`). Values are never stored — only the
host of a URL, which also names a typed `HttpClient` whose address comes from configuration. `views/deployment.md` lists
every key with its status: defined and read, set by the deployment (compose/Kubernetes `environment`), read but
not defined in the repository (unknown), or defined but unread.

`views/deployment.md` reads the descriptors themselves: compose services (image or build → the project it builds, ports,
environment variable *names*, `depends_on`, volumes, and the known system an image runs), Kubernetes workloads and Services,
Dockerfile base images and exposed ports, Terraform/Bicep resource types with the known system each provisions (also
shown as "provisioned by" in `views/context.md`) — and lists what none of them can tell.
Every node records its `language` (csharp, vue, typescript, msbuild, npm); C# types carry roles as tags
(controller, service, repository, entity, dbcontext, background-service, middleware, hub, options).
Test code (`tests:` globs) only contributes `tests` edges.

Each documentation item records which sections its profile requires and where each present section comes from:
`written` (doc comment), `fact` (derived from the model: dependencies, endpoint, input, output, authorization, events,
state — a Vue component's refs, computed values and watchers),
`inferred` (heuristics: side effects) or `ai` (drafts — shown with 🤖, never close a gap), plus the source symbols it
was derived from, with their source locations as `evidence` (`file:line-endLine`; module pages show it too).
`generate` writes it to `docs/.docwizz/documentation.json`.

## Profiles

`default`, `software`, `api`, `architecture`, `technical-publication`, `iso-42010`, `iso-15289` —
see [Profiles.cs](src/DocWizz/Profiles.cs) — or your organisation's own: a YAML file with the same shape
(`patterns:`, `architecture_sections:`), passed as `--profile team.yaml` or `profile: team.yaml`. Reports state coverage *against the profile*; nothing claims compliance
with a standard. `iso-42010`/`iso-15289` also expect human-authored pages in `docs/architecture/`
(stakeholders, concerns, decisions, …) and fail `check` while they are missing.

## Config

`docwizz.yaml` in the scanned dir (or cwd). Without one, `Config.Default` in [Analyzer.cs](src/DocWizz/Analyzer.cs)
applies — copy it as a starting point. `profile:` picks the profile; `patterns:` replaces its patterns. Sections are
XML doc tag names (`summary`, `param`, `returns`, `exception`, `example`, `remarks`, any custom tag) or the derived
ones above. Architecture rules: `layers` (path globs) and `allow`; ARCH-001 forbidden direction, ARCH-002 direct HTTP,
ARCH-003 module cycle, ARCH-004 layer bypassed. Violations carry a severity (ARCH-001 high, ARCH-002 medium, ARCH-004 low;
override with `architecture.severity`); `check.fail_on` sets the lowest severity that fails, `check.max_complexity` fails
on any symbol above that cyclomatic complexity.

## Generated docs

`index.md` (technology from project files, building blocks by role, sizes), `architecture.md` (dependency view), `api.md` (endpoints and
their flows: handler → services, through interfaces → DbContext → database / external systems), `frontend.md` (components, and
each route's flow: page → children → stores → API client → endpoint), `quality.md`, `modules/*` (one page per folder: role,
key components by use, API, data and persistence, external systems, flows through it, documentation gaps and architecture
observations with their reason — each only when the code has something to say — then the per-component reference,
with backlinks to the flows that reach each component and the configuration it reads),
`views/` (context, containers, components, data, deployment), `architecture-description.md` (structured after
ISO/IEC/IEEE 42010) and `search.json` (every component, endpoint, route, configuration key and module with its page).
Regenerating writes only the pages whose content changed and says which; unchanged pages keep their files. Pages start with a marker and are regenerated; anything without it — including everything you
write in `docs/architecture/` — is linked, never overwritten.

`--ai` sends each undocumented item's facts (graph neighbours, signature, derived sections, side effects) and its own
source lines to a **self-hosted [Ollama](https://ollama.com)** — never to a public AI service — and drafts its summary,
responsibilities, behaviour, side effects, errors and usage, plus an overview per module. Every drafted sentence cites
the facts it rests on; a sentence that cites nothing in the facts is dropped, and the rest keep their citations in
`documentation.json`. Drafts fill only what is missing, never a written or derived section. `OLLAMA_HOST` picks the
server (default `localhost:11434`), `DOCWIZZ_MODEL` the model (default `qwen2.5-coder:7b`; `ollama pull` it first).
Every connection is checked on its resolved address and must go to loopback or a private network (10/8, 172.16/12,
192.168/16, IPv6 unique-local); proxies are bypassed, and Ollama's `…-cloud` models, which run on ollama.com, are
refused. Drafts are marked 🤖, never replace written docs, and are cached in `docs/.docwizz/ai-cache.json` by
symbol + body hash together with the symbols they were generated from, so unchanged code is never sent again.
Without `--ai`, cached drafts are still used and nothing is sent.

## CI

The repository is a GitHub Action: on a pull request it runs `docwizz check --since <base>` and posts the result as one
comment, updated on every push, including ADR candidates (a new external system or layer dependency the change
introduces, for a human to record), and fails the job when the change introduces critical gaps, failing violations or
too-complex code.

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
