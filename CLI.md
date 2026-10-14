# docwizz CLI reference

New here? Run `docwizz setup .` and read what it prints. This page is for when you want something specific.

- [Workflows by goal](#workflows-by-goal): what to run for the thing you want to do, in the order you'll need it
- [Multi-project repositories](#multi-project-repositories): discovery, project selection and scope
- [SBOM inventory](#sbom-inventory) and [project evidence](#project-evidence): supply chain, traceability, product drafts
- Reference: [commands](#commands), [options](#options), [precedence](#precedence-and-conflicts),
  [exit codes](#exit-codes), [generated vs. your files](#generated-vs-your-files), [versioning](#versioning)

`docwizz help` lists the commands. `docwizz help <command>` or `docwizz <command> --help` shows one command's purpose,
an example and its options.

## Scanner distribution (G1)

To package the tool, run `npm ci --prefix scanner-vue`, then `dotnet pack src/DocWizz/DocWizz.csproj -c Release`.
Pack stages the Vue/TS and Java scanners with their pinned dependency tree in `scanner-vue/dist/` (git-ignored) and
ships them inside the .NET tool; it fails if `npm ci` has not run. The installed tool still needs Node.js 22, but it does
not install npm dependencies at runtime.

The GitHub Action loads a scanner archive from the GitHub release matching its immutable `vX.Y.Z` tag
or full commit SHA. The release workflow publishes the lockfile-built archive and SHA-256 checksum.
The Action verifies both before scanning; it never runs `npm ci`. Pin the Action to a published release tag
or release commit. Running it from a branch or moving tag fails closed, rather than returning incomplete
frontend/Java results; releases published before this change carry no payload, so pin a newer one. Only a local
`uses: ./` (this repository's own `docwizz` workflow) packs the scanner from its checkout. Node.js 22 remains a runtime
prerequisite.

## At a glance

| Command | Primary use | Typical user | Default path? |
|---|---|---|---|
| `setup` | first repository setup | all users | **yes, start here** |
| `discover` | project and solution inventory | developers | optional |
| `analyze` | documentation report | developers | yes |
| `architecture` | architecture diagnostics | architects | optional |
| `generate` | write or refresh documentation | developers | yes |
| `diff` | change impact | developers, CI | optional |
| `check` | quality gate | CI, developers | yes |
| `context` | token-budgeted facts about one target, for coding agents | coding agents | optional |
| `domain` | bounded, recursive code/requirements discovery | architects | optional |
| `mcp` | read-only MCP server over the code model | coding agents | optional |
| `sbom` | CycloneDX manifest SBOM | supply-chain inventory | optional |
| `remediate` | package update suggestions | maintainers | optional |
| `trace` | explicit traceability gaps from a YAML catalog | product documents | optional |
| `evidence-diff` | compare two evidence snapshots and explicit test impact | product documents | optional |
| `requirements` | historical work items behind a code symbol, from exported history | coding agents, reviews | optional |
| `load` | measured load-test numbers per endpoint, from powerwizz reports | performance | optional |
| `product` | product draft from a YAML template, from code and project evidence | architects | optional |
| `init` | blank config with every default | manual setup | no |
| `scan` | raw code model | advanced, debugging | no |

## Workflows by goal

### 1. First setup

```bash
docwizz setup .
```

`setup` runs these stages and prints one summary at the end:

| # | Stage | Does | If it fails |
|---|---|---|---|
| 1 | discover | reads solutions and project manifests, reports references and unassigned source files without scanning code | the rest are skipped |
| 2 | scan | reads the code and detects the stack: languages, frameworks, project files, tests, deployment files, layers | the rest are skipped |
| 3 | config | writes `docwizz.yaml`, or keeps the existing one | the rest are skipped |
| 4 | analyze | documentation coverage and critical gaps | reported; the next stages still run |
| 5 | architecture | layer violations and cycles (and security findings if they are on) | reported; the next stages still run |
| 6 | generate | writes the docs to `<dir>/docs` | reported; the next stages still run |
| 7 | check | the quality gate at the configured thresholds | reported |

With project selection, a `select` stage runs between `discover` and `scan`
([Multi-project repositories](#multi-project-repositories)). The summary starts with a status that keeps problems in
the repository apart from problems running docwizz:

| Status | Means | Exit |
|---|---|---|
| `SUCCESS` | every stage ran; the check passes and there are no architecture or security findings | 0 |
| `SUCCESS_WITH_FINDINGS` | every stage ran; the check fails or there are findings. On a new repository this is the starting point, not an error | 0 |
| `FAILED` | a stage could not run (invalid `docwizz.yaml`, unreadable files, a scanner error); its error is listed | 1 |

**What `setup` never does:** contact an AI endpoint without `--ai`; run the repository's build, tests or package tools
(`dotnet`, `npm`, `mvn`, `docker`, …); copy configuration values into `docwizz.yaml`.

**The `docwizz.yaml` it writes** starts from the defaults (the same as `docwizz init`) and changes only what the
repository shows evidence for:

- `architecture.layers` keeps the default layers that at least one source file falls into, each commented
  `# inferred: N files, e.g. <path>`; `architecture.allow` is narrowed to them. If none match, the defaults stay.
- `tests` keeps the default test globs that match test files. If none match, all defaults stay.
- In a repository with several solutions or standalone projects, a project inventory is added as comments.

Intent is never inferred: the profile, `check` thresholds, allowed dependencies, `exclude`, `comment_docs` and security
stay at their defaults. The summary's *Follow-up* list names what to decide yourself and suggests a profile when the
stack has one (`aspnet`, `vue`). The same repository and docwizz version always produce the same file.

**On an existing `docwizz.yaml`,** `setup` keeps it and runs every other stage with it. `--force` replaces it; an
identical result is reported as `unchanged`. Running `setup` again is safe.

Then read `docs/index.md` (or `docs/index.html` with `--html`) and the summary's *Next useful actions*.

### 2. Documentation generation

```bash
docwizz generate .                                         # Markdown in ./docs
docwizz generate . site --html                             # Markdown and HTML in ./site
docwizz generate . site --refresh-html-on-version-change   # re-render all HTML after a docwizz upgrade
```

Only pages whose content changed are rewritten, and the summary counts them. Every HTML page names the docwizz version
in its footer; `--refresh-html-on-version-change` (implies `--html`) rewrites all HTML when
`<out>/.docwizz/html-version.txt` differs from the running version. On an interactive terminal a live progress bar
on stderr starts at once and shows what each stage is doing (files parsed; AI drafts written, the estimated time left and the item being drafted) with the elapsed time,
leaving a ✓ line with timing and a short summary per finished stage; `--progress` forces plain one-line-per-stage
progress in CI logs (`NO_COLOR` turns colors off). The result on stdout starts with the one-line summary scripts match
(`N pages → <out> (commit …): N changed`), followed by a report: what was scanned, coverage and doc quality, gaps and
architecture findings with the page to open, and where the time went. See [Generated vs. your files](#generated-vs-your-files) for what you
may edit.

**Route screenshots** capture detected static frontend routes from an already running app with Playwright Chromium
(`npx playwright@1.63.0 install chromium`):

```bash
docwizz generate . --html --screenshots --base-url http://localhost:5173
npx playwright codegen --save-storage=.auth/docwizz-state.json http://localhost:5173   # once: sign in by hand
docwizz generate . --html --screenshots --base-url http://localhost:5173 --storage-state .auth/docwizz-state.json
```

- Each route gets a fresh, isolated browser context: anonymous by default, or initialized from the saved storage state
  (cookies and localStorage, plus `sessionStorage` entries you add to an origin in the file, for apps that keep their
  token there; see the troubleshooting guide). docwizz never automates login or handles passwords.
- With a session, each route after the first starts from the previous route's cookies and storage (kept in memory; the
  file is never written), so apps that rotate refresh tokens stay signed in. Routes such as `/logout` are skipped.
- Before each capture docwizz waits for the network to go idle (up to 5 s) plus 400 ms; `--screenshot-delay <ms>`
  adds more for slow pages.
- `--storage-state` paths are relative to the scanned directory.
- Routes of a hash-history router (`createWebHashHistory`, `createHashRouter`, `<HashRouter>`, Angular `useHash: true` /
  `withHashLocation()` in the routes file) are opened as `<base-url>/#/route`.
- A route that redirects to another of the app's routes (a router alias such as `{ path: '/erp', redirect: '/icd-schema' }`)
  is skipped as an alias; the target is captured on its own.
- Dynamic routes, HTTP errors (400+), other URL redirects, expired sessions and signed-in routes that show a password field
  (a login form without a redirect) are skipped with a warning. With a session, a route whose API calls (fetch/XHR)
  are answered 401 or 403 is skipped too: SPA servers answer every page with 200, so the API is where a rejected
  session shows. API 404s are not treated as failures. A login overlay without a password field can't be
  recognized.
- Captures go to `docs/screenshots/`, linked from `frontend.md`. A later `generate` without `--screenshots` keeps the
  last captures linked (the summary says so); only `--screenshots` replaces them. **The storage-state file holds session tokens:** keep
  it out of Git, restrict access, and use a low-privilege test account with sanitized data.

For Windows Chromium installation, missing headless-shell executables, authenticated login steps and troubleshooting,
see [Playwright screenshot troubleshooting](docs/PLAYWRIGHT_SCREENSHOTS.md).

### 3. Architecture analysis

```bash
docwizz architecture .
docwizz architecture . --format json
```

Lists the layers, the dependencies between them, violations (each `ARCH-…` rule with a severity) and module cycles.
Set the layers (path globs, first match wins) and `architecture.allow` in `docwizz.yaml`. `check.fail_on` sets the
lowest severity that counts against `max_violations`; lower ones are only reported. Custom rules:
[README: Architecture rules](README.md#architecture-rules).

### 4. Quality gates and CI

```bash
docwizz check .                      # everything: thresholds in `check:`
docwizz check . --since origin/main  # only what the change introduces
```

Thresholds live under `check:` (`min_coverage`, `max_critical`, `max_violations`, `max_cycles`, `fail_on`, and
optionally `min_quality`, `max_complexity`, `require_tests`). `--since` lets an older codebase adopt the gate without
fixing everything first. It fails on new critical gaps, new failing violations, docs that now contradict the code, new
symbols over `max_complexity`, and, with `require_tests`, new symbols that no test code links to.

GitHub Actions (posts the result on the pull request):

```yaml
on: pull_request
permissions: { contents: read, pull-requests: write }
jobs:
  docwizz:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v7
        with: { fetch-depth: 0 }
      - uses: mycaravam-crypto/docwizz@main
```

Any other CI system runs the command directly; the step fails when the gate fails (exit 1):

```bash
docwizz check . --since "$BASE_SHA" --format json > docwizz.json
```

To bootstrap a repository from a script, run `docwizz setup .` and commit the `docwizz.yaml` it writes.

### 5. AI-assisted documentation

```bash
docwizz generate . --ai
docwizz setup . --ai
```

`--ai` drafts missing summaries and module overviews to start writing from. It is off unless passed, including in
`setup`. The model must be **self-hosted** on a loopback or private address: Ollama (default) or any OpenAI-compatible
server (vLLM, NVIDIA NIM, Nutanix Enterprise AI, a LiteLLM gateway). Cloud models and public hosts are refused and
proxies are bypassed. Drafts are marked 🤖, cite their facts, fill only empty sections and **never** close a gap, raise
coverage or pass a check. Drafts are cached by code hash; without `--ai`, nothing is sent. More:
[README: AI drafts](README.md#draft-missing-docs-with-a-local-ai).

```yaml
ai:
  provider: openai-compatible   # ollama (default) or openai-compatible
  endpoint: http://10.0.0.5:8000/v1
  model: qwen3-coder-next
  api_key_env: DOCWIZZ_AI_KEY   # optional: the variable that holds the key
```

```bash
DOCWIZZ_AI_KEY=... docwizz generate . --ai
```

The key is read only from the variable `api_key_env` names; `docwizz.yaml` refuses an `api_key` setting and the key is
never printed. If the variable is named but empty, `--ai` is skipped with a message. `DOCWIZZ_MODEL` overrides
`model`; for Ollama, `OLLAMA_HOST` overrides `endpoint`. Drafts are cached per provider and model. Ollama requests ask
for a 16,384-token context (`num_ctx`): Ollama's default (often 4,096) would silently cut longer prompts.

#### Requirements in drafts

```bash
docwizz requirements import . history.json work-items/   # once (see Requirement history)
docwizz generate . --ai --requirements
```

`--requirements` gives each symbol's prompt its linked work items from the requirement index, as `businessContext`:
at most `ai.requirements.max` items (default 3) and `ai.requirements.budget` characters (default 1200), confirmed before
candidate, unchanged since before changed since, newest commit first. Criteria are cut first, then whole items; the
facts say what was left out. Work items that were linked but not exported are never sent.

- The text is marked as untrusted data, not instructions, and stays in the facts; the instructions never contain it.
- Two more sections come back: **intent** (what a requirement asked for) and **deviation** (where the source differs).
  Each sentence must cite a `WI-<id>`, and gets its status appended: `[WI-12 confirmed]`, `[WI-7 candidate, code
  changed since]`.
- A **deviation** must also cite the source and quote, in backticks, the code it is about: `` (`password.Length >= 8`) ``.
  It is kept only when every quote occurs in the symbol's source (whitespace ignored). A deviation the model can't
  point to in the code is dropped.
- The code sections (summary, behaviour, …) rest on code: work-item citations are removed from them, and a sentence
  that cites only a work item is dropped. A sentence claiming the code satisfies or implements a requirement, or
  that anything is approved or certified, is dropped wherever it is.
- A requirements draft is cached under its own key (symbol, body hash and a hash of the selected context), so a new
  revision or link re-drafts that symbol only. Without `--requirements`, prompts, cache keys and pages are exactly as
  before, and requirements drafts are never shown.

```yaml
ai:
  requirements: { max: 3, budget: 1200 }   # work items per symbol, characters of their text
```

### 6. Diff and impact analysis

```bash
docwizz diff .                # vs the docs last generated (docs/.docwizz/model.json)
docwizz diff . origin/main    # vs a git ref
docwizz diff HEAD~1 HEAD      # between two refs (directory defaults to .)
```

Lists changed, added and removed symbols, the pages they affect, docs that may now be stale, new gaps and violations,
and the test code linked to each changed symbol. A test link means test code *uses* the symbol; it is not coverage.

### 7. Raw model (advanced)

```bash
docwizz scan . model.json
```

Writes every node and edge the scanners found: for debugging why a symbol is or isn't reported, or building your own
tooling. Everyday workflows don't need it.

### 8. Context for coding agents

```bash
docwizz generate .                                    # once, and after every change
cp templates/AGENTS.md ./AGENTS.md                    # or append it to the agent file you already have
```

Coding agents (Claude Code, OpenCode, Qwen Code, Cline, Aider) can read the generated docs instead of exploring the
code file by file, which is where local models with small context windows drift on long runs.
[templates/AGENTS.md](templates/AGENTS.md) is a section for `AGENTS.md`, `CLAUDE.md`, `.clinerules` or
`CONVENTIONS.md`. It tells the agent:

- **what to read first:** `docs/index.md`, `docs/solutions/<solution>.md`, `docs/projects/<project>.md`, then
  `docs/modules/<module>.md` and `docs/architecture.md` for the folders it will touch (folder path with `/` → `-`);
- **how much to trust it:** detected facts over `(inferred)` ones; 🤖 marks a draft, never a fact;
- **that the docs are navigation:** read the real code before changing it, starting at the `file:line` links;
- **when it is done:** `docwizz check . --since <base> --format json` reports no new problems, `docwizz diff` has been
  reviewed, and `docwizz generate .` has refreshed the docs.

Most useful in practice: a module page's *Flows through this module* answers "what does this endpoint touch" in one
read; `api.md` maps a route to its handler's `file:line` and frontend callers; *Documentation gaps* is the to-do list.
Generated pages are written for people, though: a large page can crowd out an agent's context. For a budgeted slice,
use `docwizz context` or the MCP server.

#### A context package per target

```bash
docwizz context . --for Fixture.Application.MaterialService.CreateAsync   # a symbol (id or qualified name)
docwizz context . --for backend/Api/MaterialController.cs                 # a file
docwizz context . --for backend/Infrastructure                            # a folder (module)
docwizz context . --for "POST /api/materials" --budget 2000 --format json # an endpoint, as data
```

`context` writes the slice of the model that matters for one target within a token budget (default 6000, estimated as
characters / 4). Sections in priority order, cut from the end when the budget runs out:

1. header: repository, scanned commit, budget, a warning when the model is stale;
2. the target: kind, signature, visibility, layer, `file:line`, documentation and its sources, complexity, parameters,
   responses, exceptions, members;
3. neighbours one hop away: callers, callees, injections, implementations, HTTP callers;
4. further neighbours up to `--hops` (default 2, at most 4), signature and location only;
5. request flows through it, external systems with their certainty, configuration keys it reads (names only);
6. linked tests, documentation gaps and architecture findings around it;
7. *Read before you change it*: the `file:line` of the target and of the code across its edges.

- The header says what was cut. Every line ends with its provenance: `[detected]`, `[inferred]`, `[human]` or
  `[ai-drafted]`. AI drafts are left out unless you pass `--include-ai`. Output is byte-identical for the same inputs.
- `--for` resolves as: exact id (`cs:Fixture.Domain.Material`), qualified name with or without parameters, file,
  folder, then `VERB /route`. Ambiguous or unknown targets exit 1 with the candidates on stderr: it never guesses.
- It reads `docs/.docwizz/model.json` when present, else scans. A model from another commit, or older than the source
  files, is reported as stale. No network, no build, no AI. `--format json` is compact, since whitespace costs tokens.
- `--requirements` adds a *Requirements* section right after the target: the same bounded selection a model gets
  (`docwizz requirements import` first), each line `[history]`: what was asked for then, not proof of what the code
  does. Per symbol only; without the flag the package is unchanged.

#### A read-only MCP server

```bash
docwizz mcp .                  # stdio; the agent's harness starts it
docwizz mcp . --auto-rescan    # rescan when the model is stale instead of only saying so
```

`mcp` serves the code model over the [Model Context Protocol](https://modelcontextprotocol.io) on stdio:

| Tool | Answers |
|---|---|
| `find_symbol(query, kind?)` | symbols, endpoints and modules by name: exact, then prefix, then substring |
| `context(target, hops?, budget?, include_ai?, requirements?)` | the same package as `docwizz context`, as JSON |
| `callers(id)` / `callees(id)` | incoming / outgoing `calls`, `injects` and `http` edges of a symbol and its members |
| `trace_endpoint(verb, route)` | the request flow down to databases and external systems, and the frontend callers |
| `module_summary(path)` | a folder's symbols, layer, endpoints, dependencies both ways, gap and finding counts |
| `impact(ref?)` | `docwizz diff` against a ref (default `HEAD`: the uncommitted change) |
| `gaps(path?)` / `violations(path?)` | documentation gaps (critical first) / architecture violations and cycles |
| `check(since?)` | the quality gate as JSON; with `since`, only what the change introduces |
| `requirements_for_symbol(id)` | `docwizz requirements show --for` as JSON: confirmed and candidate work items, with commits and lines |
| `symbols_for_requirement(work_item)` | the symbols one work item is linked to, current code first |
| `rescan()` | scan the code again, in memory |

It is **read-only**: stdio only, no network listener, nothing written. Every answer carries a `model` object with the
scanned commit, `HEAD`, and `stale: true` with reasons when they differ. Answers take a `budget` in tokens (default
4000); long lists are paged (`offset`, `limit`, `nextOffset`) and cuts listed under `truncated`. Configuration values
never appear, only keys.

Register it with your agent (replace the path with your build of docwizz):

```bash
claude mcp add docwizz -- dotnet /opt/docwizz/DocWizz.dll mcp .     # Claude Code
```

```jsonc
// OpenCode: opencode.json
{ "mcp": { "docwizz": { "type": "local", "command": ["dotnet", "/opt/docwizz/DocWizz.dll", "mcp", "."], "enabled": true } } }

// Cline: cline_mcp_settings.json
{ "mcpServers": { "docwizz": { "command": "dotnet", "args": ["/opt/docwizz/DocWizz.dll", "mcp", "/path/to/repo"] } } }
```

## Domain discovery: first implementation

The read-only crawler explores technical code relationships from one symbol or endpoint, or from an
Azure DevOps Work Item already imported into DocWizz. It keeps historical requirement intent separate
from the observed code graph and does not declare business capabilities or bounded contexts as facts.

```bash
docwizz domain discover . --seed "POST /api/orders" --depth 3 --limit 100 --format json
docwizz domain discover . --work-item 4711 --depth 4 --limit 150 --format json
docwizz domain discover . docs/orders-domain.md --seed "POST /api/orders" --format md   # with source links
docwizz domain areas . --format json   # candidate business areas, inferred
```

Work-item seeding requires `docwizz requirements import` first. Without an index, code-seeded discovery
still runs, but marks requirement evidence as unavailable. The crawler uses a deterministic directed
BFS and never sends code or work items to AI or a network service. The JSON schema is
`docwizz.domain-discovery/v1`; `candidateTerms` are Work Item titles, not verified domain names. `--format md` renders the same report
as reviewable Markdown in three parts: As-is (Mermaid graph and symbol table), As-intended (confirmed links on unchanged
code that no cross-check questions, with criteria) and Unclear (every other link with its reason, and methods without one).
`domain areas` groups every production method by folder, then moves a method of an outer folder (controllers, jobs:
no other folder calls into it) that calls into exactly one other type to that type's area, and an interface method to
its implementations' area. Every member carries its reason; the schema is `docwizz.domain-areas/v1` and every area is
`inferred`, never confirmed.
See [domain-discovery.md](docs/domain-discovery.md) for provenance, limitations and the full
GitWizz/ADO/DocWizz workflow.

## Multi-project repositories

### Discover

`docwizz discover .` lists project manifests, solution memberships and unresolved references; `--format json` gives a
deterministic inventory. It runs no language scanners, build tools or AI, and ignores `scope:` so you always see
everything available.

### Select projects

```bash
docwizz setup .                   # new ambiguous repository in a terminal: offers selection
docwizz setup . --select-projects # select or reselect (terminal required)
docwizz setup . --all-projects    # clear the saved scope, keep the rest of docwizz.yaml
docwizz setup . --yes             # scripts and CI: never prompt
```

- **When it prompts:** only on a new repository with several solutions or standalone projects, when stdin and stdout
  are terminals and `CI` is unset. Existing configs never prompt unexpectedly. `--select-projects` fails fast without a
  terminal or with `CI` set.
- **The wizard** lists projects by path, role, solutions and reference count. Type numbers to toggle, `s1`/`s2` to
  toggle a solution, `a` all, `n` none, `d` done, `q` cancel (several commands separated by commas or spaces). It
  starts with root-level solutions selected and test projects excluded. Invalid selections show a message and can be
  corrected; cancelling changes nothing and aborts before the scan.
- **What it writes:** only the top-level `scope:` block. Other settings, comments and whitespace outside it are kept;
  comments inside it are not.
- **Stages** become `discover → select → scan → config → …`. Config still runs after the scan, so inferred layers and
  test globs are based on the selected code.

### Scope

```yaml
scope:
  projects:
    include: ["Cargo-Core/Cargo-Core.csproj", "FileAPI/FileAPI.csproj"]
    references: metadata          # metadata | include | exclude
    test_links: true
  unassigned_sources: exclude     # source files no project owns
  repository_files: include       # repository-wide deployment, CI and configuration descriptors
```

- **Paths** are manifest paths relative to the scan root, case-sensitive, with `/`; copy them from
  `docwizz discover . --format json`. No globs, absolute paths or `..`. An invalid path is an error, never a fallback
  to all projects. Several manifests in one directory must be selected together.
- **`references`:** `metadata` (default) keeps referenced, unselected projects as labeled dependency-only stubs, without
  code or packages; `include` scans every reachable referenced project; `exclude` omits them and lists each omitted
  reference in the scope report.
- **`test_links: true`** also scans test projects that reference a selected project, for traceability only; test code
  stays out of production docs and coverage.
- **`unassigned_sources`** controls loose source files. A root-level manifest owns only its own language's files (C#
  for `.csproj`, JS/TS/Vue for `package.json`, Java for Maven/Gradle).
- **`repository_files: include`** (default) keeps Docker/Compose, Kubernetes, Terraform, CI and configuration
  descriptors for the system view, except those of an excluded project.
- The scope applies **before** scanning, to every command except `discover`. `exclude:` still wins. `docs/index.md`
  discloses the requested and effective selection, stubs, omitted and missing references.
- Changing the scope makes `context` and `mcp` rescan and requires `generate` before a baseline `diff`. New project
  references or moved projects also need a fresh `generate`.

Without `scope:`, the whole repository is scanned as before.

### Generated hierarchy

With projects or solutions in the model, `generate` writes `docs/index.md` → `docs/solutions/*.md` →
`docs/projects/*.md` → `docs/modules/*.md` → components; `--html` adds a nested sidebar, breadcrumbs and search entries.

- A project page shows its path, role, SDK and frameworks, solutions, source-file count, modules, components, linked
  tests (not coverage), project references and direct packages. Metadata-only projects get a disclosure-only page.
- A shared project has one page, linked from every solution that contains it. Projects without a solution and
  unassigned modules get their own branch.
- File names combine a readable slug with a stable suffix; don't hard-code them. Generated project and solution pages
  are removed on a scope change; hand-written pages are never touched.

### Limits

Classic non-SDK ASP.NET MVC projects are inventoried, but conventional `MapRoute` routing can't be fully inferred
statically; `api.md` warns when such a project is present. The acceptance matrix and migration notes are in
[MULTI_PROJECT_ACCEPTANCE.md](MULTI_PROJECT_ACCEPTANCE.md); `bash scripts/benchmark-multi-project.sh` measures full
versus scoped scans.

## SBOM inventory

`docwizz sbom .` writes `sbom.cdx.json` (or `docwizz sbom . output.json`): CycloneDX 1.6 from NuGet, npm, Maven and
Gradle **manifests only**.

- Direct declared dependencies with a package URL (`purl`) and their declared version. Ranges, wildcards and property
  references are kept as declared but left out of `version` and the purl.
- Project-to-project references appear in the dependency graph.
- Not included: transitive dependencies, lockfiles, licenses, vulnerabilities. No restore, build or network access.
  Output is deterministic.

`generate` writes the same inventory to `views/packages.md`, listing packages declared at several versions first.

## Project evidence

These commands work on requirement and project evidence kept as YAML, CSV or Markdown, in the format described in
[docs/project-evidence.md](docs/project-evidence.md). None of them make network or AI calls (except `product --ai`),
overwrite source files, or claim conformity.

### Traceability report

```bash
docwizz trace . project-evidence.yaml [--format json]
```

Reports declared requirement → architecture → test links and the missing ones, as Markdown on stdout or JSON. CSV and
Markdown inputs contain records only, without links. Missing links are gaps, not failures; an invalid or missing
catalog exits 1. With a `source_hash`, each source is reported as current, stale, missing or unverified. It checks
declared links only, not test success.

### Evidence snapshot comparison

```bash
docwizz evidence-diff . before.yaml after.yaml [--format json]
```

Compares two evidence snapshots: changed fields, direct links, tests reachable along declared relations, added and
removed links. Output is deterministically ordered. It doesn't inspect file contents or decide whether evidence is
current.

### Product drafts

```bash
docwizz product . templates/vmodell-xt/sw-architecture.yaml [out.md] [--context project.yaml] [--ai]
docwizz product check . templates/vmodell-xt/sw-architecture.yaml --context project.yaml [--format json]
```

Renders a work product from a template, filled from the code model; anything without evidence stays `OFFEN`. Output
defaults to `docs/products/<product_id>.md`. Paths resolve from the current directory, then from `<dir>`. Unchanged
output isn't rewritten, and hand-maintained output is never replaced. The V-Modell XT templates are illustrative, not
official templates or a conformity claim.

- **`--context project.yaml`** adds sourced project statements. Statements that don't fit a section exit 1; nothing is
  silently dropped.
- **`--ai`** has a self-hosted model (the `ai:` provider) word each section that has evidence. Each piece of evidence
  gets an id (`E1`, `E2`, …) and every sentence must cite ids from its own section. A sentence that cites nothing, cites
  wrongly, or claims approval, review, completeness or conformity is dropped, and model text can't become links or
  HTML. Sentences are marked 🤖; sections without evidence are never sent; project statements are sent as data only.
  Results are cached in `docs/.docwizz/product-ai-cache.json`. If nothing could be worded, the output equals the draft
  without `--ai`.
- **`<product_id>.evidence.json`** lists per section its status (`evidenced`, `open`, `empty`), every piece of evidence
  with id, kind, origin (`detected` or `human`) and source, and any 🤖 sentences with their citations. `required`
  summarizes which required sections are evidenced or open. It is an inventory, not a statement of review.

`product check` validates a draft's references and freshness before review (exit 1 on an error; warnings never fail):

| Rule | Severity | Finds |
|---|---|---|
| PROD-001 | warning | a required section without evidence (`OFFEN`) |
| PROD-002 | error | a project statement whose source file doesn't exist |
| PROD-003 | error | a product in `dependencies` without a draft next to this one |
| PROD-004 | error | the draft or its evidence report missing, not docwizz's, or stale |
| PROD-005 | warning | a project statement that reads like an instruction to a model |
| PROD-006 | error | a 🤖 sentence citing an id that isn't evidence of its section |

It never judges content: whether a statement is true or a product can be approved stays a human decision.

## Load-test results

`docwizz load import . shop-bericht.json [more.json ...]` reads [powerwizz](https://github.com/mycaravam-crypto/powerwizz#json-report-for-docwizz)
reports (`powerwizz.report/v1`) natively, offline and without an adapter, and keeps them in `docs/.docwizz/load.json`.
The same run (start time and target) imported again replaces itself; unknown fields are ignored. A field v1 has that is
missing or of another type (`steps[0].error_rate`, `latency_ms.p95`, `started_at`, …) exits 1 naming it, so a missing
number never shows as 0. `generate` then adds **Load (measured)** to `api.md`: per matched endpoint the requests, error
rate and p50/p95/p99 latency of the newest run, one row per step of that run that called it,
with the run as evidence, and the run's label (powerwizz `--label`, e.g. `dev, Debug build`: what was measured),
users, ramp-up, duration and auth once. A run against a non-local target is
marked as not comparable, and a run older than the endpoint file's last commit is marked _stale_.

Each step is matched to a detected endpoint by method, then segment by segment:

- A route parameter (`{id}`, `:id`, `[id]`) matches any one segment, including powerwizz placeholders (`{{vu_id}}`).
  Literal segments must be equal, ignoring case. The query string is ignored.
- The base URL's path prefix (`/app/`) may precede the route; it is stripped when the rest matches.
- The most literal segments win. A tie is ambiguous and not attached; it is listed, like a step with no endpoint.

With more than one run, **Trend** lists per endpoint and step every run against the newest run's target, oldest first:
p95 and error rate, and the newest p95's change from the run before it (`+36.4 %`). Runs against another target aren't
comparable and are left out; nothing judges what counts as a regression. **Timeline** draws each shown run's per-second
`series` (all steps together) as Mermaid line charts of p95 latency and requests per second, at most 60 points: a point
spanning several seconds shows their worst p95, so a spike isn't averaged away. A `series` point without `t`,
`requests` or `p95_ms` exits 1 naming it.

## Requirement history

```bash
docwizz requirements import . history.json work-items/                    # gitwizz.history/v2 + ado.requirement-context/v1
docwizz requirements show . --for Shop.OrderService.Place [--format json] # one symbol's requirements
docwizz requirements show . --work-item 101                               # one work item's symbols
docwizz requirements query . query.json                                   # gitwizz's resolver: a PR's changed symbols
```

`import` reads what `gitwizz history export` writes (`gitwizz.history/v2`, no adapter), maps every exported commit's
changed lines onto the symbols as they were at that commit and writes `docs/.docwizz/requirements.json`; identical
inputs write nothing, and known commits aren't scanned again. `show` answers from that index and the current model
(`docwizz.requirement-context/v1`): each work item is `confirmed` (a commit the provider links to it changed the
symbol) or `candidate` (only a confirmed pull request's link, or a mention in a commit message), with the commit, PR,
file and lines, and `changedSince` when the symbol changed after that commit. The `outcome` tells `linked`, `no-link` (verified
none), `unavailable`, `not-in-history` and `not-current` apart. Files only, no network; the same inputs give the same
bytes. Input shapes and rules: [docs/requirement-context.md](docs/requirement-context.md).

`query` answers gitwizz (`gitwizz context <PR> --system`, `guide`, `evidence`) from the same index: it reads a
`gitwizz.requirement-query/v1` file and prints `gitwizz.requirement-input/v1` for the requested symbols only. Set it in
`.gitwizz.yml`:

```yaml
context:
  requirements:
    enabled: true
    command: docwizz requirements query   # gitwizz appends <repository dir> <query file>
```

A `confirmed` link is `verified`, everything else `candidate`. A missing index, a truncated or shallow history,
unknown commit links and linked work items that weren't exported are listed under `unavailable`, so gitwizz reports
`partial` instead of "no requirements". `analyzedCommit` is where the imported history ends: when it isn't the PR's
target, gitwizz marks the answer stale. A query it can't read exits 1 with the reason. No network, no AI, no writes.

## Commands

| Command | Syntax | Purpose | Use |
|---|---|---|---|
| `discover` | `docwizz discover [dir] [--format console\|json]` | read-only inventory of solutions, projects and references | everyday |
| `setup` | `docwizz setup [dir] [--select-projects] [--all-projects] [--yes] [--force] [--html] [--progress] [--refresh-html-on-version-change] [--ai] [--profile p]` | detect the stack, write `docwizz.yaml`, analyze, architecture, generate, check | start here |
| `generate` | `docwizz generate <dir> [out] [--html] [--screenshots --base-url <url> [--storage-state <file>] [--screenshot-delay <ms>]] [--progress] [--refresh-html-on-version-change] [--ai] [--requirements] [--profile p]` | write the docs (default `<dir>/docs`) | everyday |
| `check` | `docwizz check <dir> [--since ref] [--format f] [--profile p]` | quality gate; exit 1 when thresholds fail | CI |
| `analyze` | `docwizz analyze <dir> [--format f] [--profile p]` | documentation and architecture report, no gate | everyday |
| `architecture` | `docwizz architecture <dir> [--format f] [--profile p]` | layers, dependencies, violations, cycles; exit 1 above thresholds | architects |
| `diff` | `docwizz diff [dir] [base] [head] [--format f] [--profile p]` | change impact vs the last `generate` or git refs | reviews, CI |
| `context` | `docwizz context <dir> --for target [--hops n] [--budget tokens] [--format md\|json] [--include-ai] [--requirements]` | token-budgeted context package for one symbol, file, folder or endpoint | coding agents |
| `domain` | `docwizz domain discover <dir> [out] (--seed <symbol\|endpoint> \| --work-item <id>) [--depth n] [--limit n] [--format md\|json]`; `docwizz domain areas <dir> [--format json]` | recursively explore code with separately sourced historical requirements; inferred candidate business areas | architects |
| `mcp` | `docwizz mcp <dir> [--auto-rescan]` | read-only MCP server on stdio: find_symbol, context, callers, impact, check, … | coding agents |
| `evidence-diff` | `docwizz evidence-diff <dir> <before.yaml> <after.yaml> [--format json]` | explicit evidence changes and affected tests | product documents |
| `trace` | `docwizz trace <dir> <evidence.yaml> [--format json]` | explicit traceability and missing links | product documents |
| `load` | `docwizz load import <dir> <report.json>...` | store powerwizz reports; `generate` adds measured numbers per endpoint to `api.md` | performance |
| `requirements` | `docwizz requirements import <dir> <history.json> [<work-items>]`, `docwizz requirements show <dir> (--for <symbol> \| --work-item <id>) [--format json]`, `docwizz requirements query <dir> <query.json>` | index exported history; one symbol's requirements, or one work item's symbols; gitwizz's requirement query | coding agents, reviews |
| `product` | `docwizz product [check] <dir> <template.yaml> [out.md] [--context file.yaml] [--ai] [--format f]` | product draft from evidence; `check`: references and freshness | architects |
| `sbom` | `docwizz sbom [dir] [out]` | direct declared dependencies as CycloneDX 1.6 JSON | supply-chain inventory |
| `remediate` | `docwizz remediate <dir> [--package n --to v] [--validate] [--since ref] [--format f]` | package update suggestions: command or patch, impact, confidence | maintenance |
| `init` | `docwizz init [dir]` | write a `docwizz.yaml` with every default; refuses to overwrite | manual setup |
| `scan` | `docwizz scan <dir> [model.json]` | dump the raw code model | advanced |
| `help` | `docwizz help [command]` | all commands, or one | — |

`<dir>` defaults to `.` everywhere. Running `docwizz` with no command prints the recommended next step.

## Options

| Option | Default | Applies to | Interaction | Use |
|---|---|---|---|---|
| `--profile <name\|file.yaml>` | `profile:` in `docwizz.yaml`, else `default` | setup, generate, check, analyze, architecture, diff | overrides `profile:` and the file's own `patterns:` | normal |
| `--format console\|json` | `console` | discover, analyze, check, architecture, context, domain, diff, trace, evidence-diff, product, remediate, requirements | `json` prints one JSON document to stdout; `context` also takes `md` (the same as `console`) | CI, tooling |
| `--html` | off | generate, setup | adds HTML pages; the Markdown is written either way | normal |
| `--progress` | interactive terminals | generate, setup | forces progress bars on stderr when output is redirected | normal |
| `--refresh-html-on-version-change` | off | generate, setup | implies `--html`; rewrites all HTML when the docwizz version changed | normal |
| `--screenshots --base-url <url>` | off | generate | captures detected static routes from a running frontend; anonymous isolated contexts by default; HTTP errors and redirects are skipped | opt-in |
| `--storage-state <file>` | off | generate | with `--screenshots`, starts each route's context from a saved Playwright login state; path relative to the scanned directory; keep it out of Git | opt-in |
| `--screenshot-delay <ms>` | 0 | generate | with `--screenshots`, extra wait (0 to 60000 ms) after the page settles, for slow pages | opt-in |
| `--ai` | off | generate, product, setup | self-hosted model only (Ollama, or `ai:` in `docwizz.yaml`); cached drafts are used even without it, nothing new is sent | opt-in |
| `--since <ref>` | — | check, remediate | check: only problems introduced since `<ref>` fail; remediate: says whether each update touches only what changed since `<ref>` | CI |
| `--for <target>` | — | context, requirements | required for context; a symbol id or qualified name, a file, a folder, or `VERB /route`; ambiguous or unknown exits 1 with candidates. `requirements show` takes a symbol, or an id the index knows from history | coding agents |
| `--work-item <id>` | — | requirements, domain | `requirements show`: reverse lookup; `domain discover`: traverse the current linked code symbols instead of `--seed` | architects, reviews |
| `--seed <symbol\|endpoint>` | — | domain | a single exact/qualified symbol or `VERB /route`; incompatible with `--work-item` | architects |
| `--depth <n>` | 3 | domain | traversal hops, 0–8; 0 means only the start symbols | architects |
| `--limit <n>` | 100 | domain | maximum reached symbols, 1–500; truncation is explicit | architects |
| `--hops <n>` | 2 | context | how far neighbours are listed, 1 to 4; hops beyond 1 are signature only | coding agents |
| `--budget <tokens>` | 6000 | context | at least 100; estimated as characters / 4; lower-priority sections are cut first, and the cut is reported | coding agents |
| `--requirements` | off | generate, context | generate: prompts also get each symbol's linked work items (intent/deviation sections, cited and labeled); context: adds a Requirements section. Needs `docwizz requirements import`; exits 1 without an index | opt-in |
| `--include-ai` | off | context | includes cached 🤖 drafts, marked `ai-drafted`; never calls a model | coding agents |
| `--auto-rescan` | off | mcp | rescans when the model is stale instead of only reporting `stale: true` | coding agents |
| `--package <name> --to <version>` | `remediation.targets` and version drift | remediate | given together or not at all | maintenance |
| `--validate` | off | remediate | runs the configured restore/build/test commands in a temporary copy, never the working tree | maintenance |
| `--context <file.yaml>` | off | product | validates sourced project statements against the template before writing | product documents |
| `--force` | off | setup | replaces an existing `docwizz.yaml`; any other command rejects it | setup only |
| `--select-projects` | off | setup | terminal-only project selection after discovery; on an existing config replaces only `scope:` | setup only |
| `--all-projects` | off | setup | removes a saved project scope while keeping other YAML | setup only |
| `--yes` | off | setup | never prompts; does not change an existing scope | setup only |
| `--timings` | off | all | time and peak memory per stage, on stderr | benchmarks |
| `--help`, `-h` | — | all | prints the command's help and exits | — |
| `--version`, `-v` | — | all | prints `docwizz <version> (commit <sha>, .NET <runtime>, <platform>)` and exits 0, whatever the command; `docwizz version` does the same | bug reports, CI logs |

## Precedence and conflicts

- **Configuration file:** `<dir>/docwizz.yaml`, else `./docwizz.yaml`, else built-in defaults. `setup` only uses or
  writes `<dir>/docwizz.yaml`.
- **Project scope:** `scope:` applies before scanning, within what `.gitignore` and `exclude:` allow. No `scope:` means
  all code. `discover` ignores it.
- **Profile:** `--profile` beats `profile:` in the file, which beats `default`. A file with its own `patterns:` uses
  them unless `--profile` is given.
- **Existing configuration:** `init` never overwrites. `setup` keeps the file unless `--force`; `--select-projects`
  and `--all-projects` edit only the `scope:` block.
- **Unused options** (`--format` with `generate`, `--html` with `analyze`) are rejected, naming the commands that take
  them. Only `--help`, `--timings` and `--version` apply everywhere.
- **`--version` first:** it prints the version and exits 0 instead of running the command, even with `--help`. Bad
  arguments are still reported first.
- **Mistakes** (unknown command or option, missing value, `--format xml`, `--package` without `--to`) print one line
  with what is wrong and a suggestion for a likely typo. Exit 1.
- **`diff` arguments:** if the first argument is not a directory, it is a ref and the directory is `.`. No ref compares
  against the last `generate`, one against the working tree, two against each other.

## Exit codes

| Command | 0 | 1 |
|---|---|---|
| `setup` | every stage ran (`SUCCESS` or `SUCCESS_WITH_FINDINGS`) | a stage failed (`FAILED`; the others are still reported), or bad arguments |
| `check` | thresholds pass | thresholds fail, or bad arguments/config |
| `architecture` | within `max_violations`/`max_cycles` | above them |
| `discover`, `analyze`, `generate`, `scan`, `sbom` | done | bad arguments or config |
| `diff` | done | no baseline (`generate` first or pass a ref), unknown ref |
| `context` | package written | no `--for`, an ambiguous or unknown target (candidates on stderr), or bad arguments |
| `mcp` | stdin closed | bad arguments or config |
| `domain` | JSON or text summary written, even with missing optional requirements index | missing/ambiguous seed, invalid options or missing index for Work Item seed |
| `remediate` | done (suggestions alone never fail) | a `--validate` run failed, or bad arguments |
| `trace` | report on stdout | invalid/missing YAML; no data written |
| `evidence-diff` | report on stdout | invalid/missing evidence catalogs; no data written |
| `requirements` | `import`: index written or unchanged; `show`: answer on stdout (any outcome, `no-link` included); `query`: answer on stdout (a missing index included) | missing or invalid input, unknown schema, no index (`show`), unknown target, an unreadable query, bad arguments |
| `load` | reports stored (unmatched or ambiguous steps are listed on stderr) | no report, a file that isn't `powerwizz.report/v1` (wrong or missing `schema`), invalid JSON, a missing or mistyped field, a broken `load.json` |
| `product` | draft written or unchanged; `check`: no errors (warnings allowed) | invalid template, unsafe overwrite, bad arguments; `check`: an error finding |
| `init` | written | `docwizz.yaml` exists |
| no command | — | prints how to start |
| `--version`, `version` | prints the version | — |

Every command exits **2** when docwizz itself fails unexpectedly (a bug, a full disk, a permission error). It prints one
line instead of a stack trace, so CI can tell "docwizz broke" from "the gate failed". Set `DOCWIZZ_DEBUG=1` for the
full trace.

The Node scanners (Java, Vue, React, Angular, TypeScript) get 10 minutes per run. One that takes longer is stopped and
that language is skipped with a warning, so a hung scanner can't block CI; set `DOCWIZZ_SCANNER_TIMEOUT_SECONDS` to
change the limit. A scanner that fails shows the last lines it wrote to stderr in its warning.

## Generated vs. your files

| Path | Who owns it |
|---|---|
| `docwizz.yaml` | **you**. `setup`/`init` create it once and then leave it alone (`setup --force` replaces it). |
| `docs/*.md`, `docs/solutions/`, `docs/projects/`, `docs/modules/`, `docs/views/`, `*.html` | docwizz. Regenerated, so don't edit them. |
| `docs/products/*.md`, `*.evidence.json` | docwizz `product`: the draft and its evidence report; untouched by `generate`. |
| `docs/architecture/*.md` | **you**. Linked from the generated pages and never overwritten. |
| `docs/.docwizz/model.json`, `documentation.json` | docwizz: the baseline `diff` compares against, and the documentation model as data. |
| `docs/.docwizz/requirements.json` | docwizz `requirements import`: the requirement index. Contains work-item titles and criteria. |
| `docs/.docwizz/ai-cache.json`, `ai-assessments.json` | docwizz: AI drafts and ratings, cached by code hash. Commit them to avoid re-sending code. |

## Versioning

```
$ docwizz --version
docwizz 0.1.0 (commit e4320bd, .NET 10.0.12, linux-x64)
```

The line names the version, the commit it was built from, the .NET runtime and the platform: paste it into bug reports
and print it at the start of CI jobs. `docwizz setup` writes the same version into the header of the `docwizz.yaml` it
creates.

Versions follow [Semantic Versioning](https://semver.org). While the major version is `0`, a minor release may change
options or output. Git tags (`vX.Y.Z`) are the version: releases build with `-p:Version` from their tag, and any other
build derives it from the tags in its checkout ([DocWizz.csproj](src/DocWizz/DocWizz.csproj)):

| Built from | `docwizz --version` |
|---|---|
| a release tag (`v0.2.2`) | `0.2.2` |
| 4 commits after `v0.2.2` | `0.2.3-dev.4`: a pre-release of the next patch |
| no git, or no tags (a shallow clone, a source archive) | `0.0.0-dev` |

`git fetch --tags` updates the tags of an existing clone.

### Releases

Every pull request merged into `main` is a release. The [release workflow](.github/workflows/release.yml) bumps the
latest `vX.Y.Z` tag according to the PR's label, builds and tests that version, tags the merge commit and creates a
GitHub release with generated notes. Nothing is committed to `main`.

| PR label | Release |
|---|---|
| none | patch: `0.1.0` → `0.1.1` |
| `release:minor` | minor: `0.1.3` → `0.2.0` (new features) |
| `release:major` | major: `0.4.2` → `1.0.0` (breaking changes) |
| `release:none` | nothing (docs, CI); the next merge without it releases |

- The first release, with no tag yet, is `0.1.0`.
- Two release labels on one PR fail the workflow instead of guessing.
- A commit already covered by a newer tag is not released again.
- The bump rule is [semver-next.sh](.github/scripts/semver-next.sh), specified by
  [semver-next.test.sh](.github/scripts/semver-next.test.sh), which runs before every release.
