# How DocWizz works

DocWizz is a code intelligence model that happens to generate documentation. Scanners turn source and project files
into one language-neutral graph of facts. Everything else reads that graph: documentation analysis, architecture
rules, change impact, package reports and the generator. AI is an optional last step that turns facts into prose; it
is never a source of facts.

```text
repository files (.gitignore, exclude:)
        │
        ▼
ProjectDiscovery → ProjectScope                        ← manifests, solutions, scope:   → docwizz discover
        │  (files filtered before any parser)
        ▼
CSharpScanner  scanner-vue (Node)  JavaScanner  PhpScanner  Sql  Projects  Configuration   ← facts only
        └──────────────┬───────────────────────────────────────────────┘
                       ▼
   link: HTTP calls → endpoints, external systems, config-addressed clients, SQL calls, tests
                       ▼
                CodeModel (nodes + edges)                                  → docwizz scan
      ┌──────────┬─────┴──────┬──────────────┬─────────────────┐
      ▼          ▼            ▼              ▼                 ▼
  Analyzer  Architecture    Diff       Sbom/Remediation   AgentContext/Mcp
      │      (+Security)      │              │                 │
      │          │            │              │                 → context / mcp
      │          │            → diff         → sbom / remediate
      └────┬─────┘
           ▼                                                    → analyze / architecture / check
       Generator (+ Html, screenshots, AiProse drafts)          → generate
           ▼
   docs/*.md, docs/*.html, docs/.docwizz/{model,documentation}.json
```

[Program.cs](src/DocWizz/Program.cs) is the CLI and `BuildModel`, which runs discovery, scanners and link steps in that
order. [Cli.cs](src/DocWizz/Cli.cs) holds the command and option table that the parser, `help` and the doc tests share.
[Setup.cs](src/DocWizz/Setup/Setup.cs) backs `docwizz setup`: it runs the stages in order, isolates failures, detects
the stack from the model and narrows the default `docwizz.yaml` to the layers and test globs files actually match.

Source is organized by stage: `Discovery/`, `Scanning/`, `Model/`, `Analysis/`, `Generation/`, `Ai/`,
`Agent/`, `Product/`, `Setup/`. Folders depend one way: Model ← Scanning ← Discovery ←
Analysis ← Generation, Ai, Agent, Setup, Product.

## The code model

[CodeModel.cs](src/DocWizz/Model/CodeModel.cs) has two records. A `Node` has an id, kind, name, source location and
optional facts: visibility, doc comment XML, complexity, parameters, return type, thrown exceptions, events, state,
route, tags and a body hash. An `Edge` has from, to, kind and an optional label. Ids keep their scanner prefix so they
stay stable across runs:

| Prefix | Nodes |
|---|---|
| `cs:` | C# types and members; `cs:endpoint:VERB /route` for minimal APIs |
| `java:`, `php:` | Java and PHP types and members |
| `vue:`, `ts:` | components; modules, functions and stores (`file#name`) |
| `route:` | frontend routes |
| `proj:`, `pkg:` | project files, NuGet/npm packages |
| `ext:` | external systems (`kind: external`, tags `[category, certainty]`) |
| `config:` | configuration keys, lowercase (`kind: config`, tags `env:<environment>@<file>`, `url:<environment>=<host>`) |

| Edge kind | Meaning |
|---|---|
| `contains` | type → member, module → function |
| `calls`, `creates` | invocation; `new T()` |
| `implements`, `inherits` | type and member level |
| `injects` | constructor or minimal-API handler parameter |
| `accesses` | a member uses its own type's injected dependency (field, property, primary-constructor parameter) |
| `registers` | DI registration interface → implementation |
| `persists` | DbContext → entity (`DbSet<T>`) |
| `publishes`, `subscribes` | C# events; Vue emits (label = event name) |
| `imports`, `renders`, `routes-to` | frontend structure |
| `http` | frontend call → endpoint, or unresolved `http:VERB url` |
| `connects` | code or project → external system (label = certainty) |
| `reads`, `binds` | code → configuration key; options type → bound section |
| `references`, `depends-on` | project → project; project → package (label = version) |
| `tests` | test code → code it exercises (the only trace test code leaves) |
| `uses-namespace` | type → external namespace (`ns:<name>`): C# `using`, Java `import` (own, `System.*`, `java.*` dropped) |

`CodeModel.DependencyKinds` lists the edge kinds that count as a dependency for architecture rules. `accesses`,
`connects`, `reads` and `binds` are left out on purpose, so adding them changed no layer rule.

### What counts as a fact

- **Facts** come from syntax and semantics: symbols, signatures, calls, routes, `[Authorize]`, `DbSet<T>`.
- **Certainty** is recorded where detection isn't exact. An external system is `detected` when a call shows it
  (`UseNpgsql`, `AddHttpClient<T>` with a base address, `fetch('https://…')`), `inferred` when only a package or a
  single candidate points to it, and `unknown` when something is there but the code doesn't say what (a DbContext with
  no provider).
- **Name heuristics** are marked in code with `ponytail:` comments and only produce tags (`*Service` → service). An
  external system is never invented from a name.
- **Values** from configuration and deployment files are never stored, because they may be secrets. The one exception
  is the host of a URL.

## Discovery and project scope

Discovery runs before any language scanner, so a project selection limits what is parsed.

- **[ProjectDiscovery](src/DocWizz/Discovery/ProjectDiscovery.cs)** reads manifests and solutions from the repository
  file list (reusing `Projects.Scan` and `Projects.SolutionFolders`) and returns stable relative-path ids, solution
  memberships, references and unassigned source files. No scanner or build runs. It always sees the whole repository
  (`RepoFilesUnscoped`).
- **[ProjectScope](src/DocWizz/Discovery/ProjectScope.cs)** resolves `scope.projects.include` against that inventory:
  project references per `references` mode, test projects for `test_links`, unassigned sources, repository-wide
  descriptors. It filters files **before** every parser; other paths use the scope-aware `RepoFiles`. Paths are
  confined to the scan root and ambiguous shared directories are rejected with a `ScopeException`.
  - `metadata`: directly referenced, unselected production projects become `metadata-only` nodes without code or
    packages; test-only references create none.
  - `exclude`: reference edges are removed but kept in `CodeModel.ScopeInventory`, so `docs/index.md` discloses them.
  - `include`: reachable local projects are traversed and scanned.
- **[ProjectOwnership](src/DocWizz/Discovery/ProjectOwnership.cs)** is the one file-ownership rule shared by discovery,
  scope filtering and navigation, cached per file. A root-level manifest claims only its own language's sources, so a
  root `package.json` can't hide C# or Java orphans.
- **`CodeModel.ScopeFingerprint`** hashes the scope configuration (null without a scope; not the inventory or manifest
  contents). A saved model with another fingerprint makes `context`/`mcp` rescan and `diff` require a fresh `generate`.
- **[SetupProjectSelection](src/DocWizz/Setup/SetupProjectSelection.cs)** is the wizard `setup` runs between discovery
  and scan. It only prompts on a TTY with `CI` unset, applies the selection in memory before scanning, and writes it by
  atomically replacing the top-level `scope:` block, leaving the rest of the YAML byte-identical. Without a selection
  stage, `setup` keeps its seven stages; config still follows scan so inferred layers come from evidence.

Without `scope:`, everything is scanned as before. Limits and the acceptance criteria are in
[MULTI_PROJECT_ACCEPTANCE.md](MULTI_PROJECT_ACCEPTANCE.md).

## Scanners

- **[CSharpScanner](src/DocWizz/Scanning/CSharpScanner.cs)** uses Roslyn with BCL references only; ASP.NET, EF Core and
  other package types stay unresolved and are matched by name. It covers types, members, doc comments (with
  `comment_docs`, also the `//` block above a member), complexity, calls, injection, `accesses`, events, controllers
  and minimal APIs (`MapGroup` prefixes, group authorization, parameter binding inferred as ASP.NET does without
  `[From*]`), response codes and types (`ProducesResponseType`, `.Produces<T>()`, `Results<…>`, `Ok(x)`), the
  middleware pipeline in order, DI registrations, `AddHostedService<T>`, EF `DbSet`, external-system calls and
  configuration reads.
- **[scanner-vue](scanner-vue/index.mjs)** uses the TypeScript compiler and `@vue/compiler-sfc`, run by
  [Frontend.cs](src/DocWizz/Scanning/Frontend.cs) as a Node child process.
  - Vue: SFC props, emits, state, lifecycle hooks, composables, template renders.
  - TypeScript and plain `.js`/`.mjs` (`*.min.js` and `wwwroot/lib/` skipped): exported functions, stores, classes,
    interfaces, types, enums, imports, calls, router routes.
  - React (`.tsx`/`.jsx`): capitalised functions returning JSX are components (props, hooks, children); routes from
    `createBrowserRouter` and `<Route>`.
  - Angular: `@Component` classes (inputs, outputs, signal inputs, lifecycle, children by selector), `@Injectable`
    services, `HttpClient` calls, `Routes` incl. `loadComponent`.
  - HTTP calls: `fetch`/`axios` and wrappers (an `axios.create({ baseURL })` instance, or a helper passing URL and
    method on). A pre-pass finds wrappers in every file, so they resolve across imports.
- **[JavaScanner](src/DocWizz/Scanning/JavaScanner.cs)** gets a syntax tree from [java.mjs](scanner-vue/java.mjs)
  (tree-sitter-java as WebAssembly: nothing compiled or executed, deterministic). It covers types and members with
  Javadoc, complexity, generic signatures as written; Spring roles, `@*Mapping` endpoints with parameter sources, auth
  and unwrapped return types; constructor, `@Autowired` and Lombok injection; calls through fields, parameters, locals,
  `this`, unqualified and static calls, including inside lambdas; method-level `implements`;
  `@Value`/`@ConfigurationProperties`; imports. Projects come from `pom.xml`/`build.gradle`, configuration from
  `application*.yml|properties`.
  **Limits** (no type checker): calls on a chain (`a.b().c()`) aren't linked; overloads resolve by name and argument
  count; inherited methods aren't looked up. A file with syntax errors keeps what parsed, with a warning.
- **[PhpScanner](src/DocWizz/Scanning/PhpScanner.cs)** reads plain PHP without a parser (comments and strings masked,
  then regexes): classes, interfaces, traits and enums with PHPDoc, free functions, complexity and signatures;
  constructor property promotion as a typed property and `injects`; calls through `$this->`, `self::`/`static::`/
  `parent::` and known-typed properties; traits; namespace `use` imports. No framework awareness yet (Laravel/Symfony
  routes, DI containers, Eloquent). Types resolve by simple name, so same-named classes in different namespaces can
  collide.
- **[Sql](src/DocWizz/Scanning/Sql.cs)** reads `.sql` files: procedures, functions, views, triggers and tables (doc from
  the comment above, `@parameters`), the tables each routine touches, the procedures it `EXEC`s, and migrations (a
  `migrations/` folder or Flyway names). C# literals that `EXEC` a procedure, or name it next to
  `CommandType.StoredProcedure`, link the caller to it; EF `Migration` classes are tagged `migration`.
- **[Projects](src/DocWizz/Scanning/Projects.cs)** reads `.csproj` (SDK, frameworks, package and project references,
  role from `OutputType`, the SDK and test packages; `Directory.Packages.props` and `VersionOverride`),
  `package.json`, and `.sln`/`.slnx` solution folders. Files in a test project count as test code, whatever their path.
- **[Configuration](src/DocWizz/Scanning/Configuration.cs)** reads `appsettings*.json`, `.env`, Kubernetes ConfigMaps and
  Helm `values*.yaml` env blocks: keys per environment, no values (only a URL's host).
- **[Externals](src/DocWizz/Scanning/Externals.cs)** holds the curated list of known systems (packages, calls, container
  images, IaC resource types) and the link step: absolute URLs, package-only evidence, DbContexts without a known
  database.

## Analysis

- **[Analyzer](src/DocWizz/Analysis/Analyzer.cs)** decides per symbol whether it needs docs, from visibility,
  complexity, parameters, fan-in, side effects and the profile's pattern level. It records which sections the profile
  requires and where each present one comes from: `written`, `fact` (derived from the model), `inferred` or `ai`. AI
  drafts never close a gap. Written docs get quality flags: facts where they contradict the model, inferences where a
  heuristic says they add nothing. Doc quality % is the share of written docs without flags.
- **[Profiles](src/DocWizz/Model/Profiles.cs)** are YAML documentation patterns: match on kind, name, type, tag or
  visibility, then list the required sections. Reports say "coverage against profile X", never "compliant with".
- **[Architecture](src/DocWizz/Analysis/Architecture.cs)** covers path-glob layers, allowed dependencies, violations
  ARCH-001/002/004 and folder cycles (ARCH-003). Custom rules (`architecture.rules`) run over the same edges with a
  source selector and what it must not reach; they are validated on config load and produce the same `Violation`s,
  marked `Custom`, so `check`, `--since` and the pages need nothing extra. `architecture.md` adds coupling per module
  (fan-in, fan-out, instability) and risks that break no rule.
- **[Security](src/DocWizz/Analysis/Security.cs)** (opt-in) turns facts the model already has into review findings:
  endpoint auth, DbContext access from the API layer, framework namespaces in the domain, and flows from unprotected
  endpoints to sensitive external systems. Each finding is a `Violation` with a basis (fact or inferred), a CIA concern
  and the risk. `Architecture.Check` appends them, so gates treat them like violations but pages show them apart.
- **[Diff](src/DocWizz/Analysis/Diff.cs)** compares two models by symbol id and body hash: changed, added and removed
  symbols, affected pages (including every page along a flow through a touched symbol), introduced gaps and violations,
  and changed package versions. New external systems and layer dependencies are listed as ADR candidates. Each added or
  changed symbol gets its linked tests from [TestLinks](src/DocWizz/Model/TestLinks.cs): `tests` edges to it, its
  interface, an implementation or a member, the same rule the analyzer uses for "tested". A `tests` edge is what a call,
  import, render, injection, HTTP call or instantiation (`new T(..)`) from test code becomes; test code must be inside
  the scanned directory (`tests:` globs) for any link to exist.

## Packages

Sbom and Remediation live in `Analysis/`; the packages view is part of `Generator`.

- **[Sbom](src/DocWizz/Analysis/Sbom.cs)** writes CycloneDX 1.6 from the Projects scan: an `application` component per
  project, a `library` per declared package and version with a package URL, and project → package / project → project
  dependencies. Only a plain literal (`1.2.3`) fills `version`; ranges, wildcards, properties and git/file specs stay
  as `docwizz:declaredVersion`, marked unresolved. Direct dependencies only, byte-identical for identical inputs.
  `Sbom.Collect` also feeds `views/packages.md` ([PackagesView](src/DocWizz/Generation/PackagesView.cs)).
- **[Remediation](src/DocWizz/Analysis/Remediation.cs)** turns targets (`--package/--to`, `remediation.targets`, or
  NuGet version drift) into suggestions that keep three kinds of evidence apart:
  - *detected*: the declared version and its line, and from them a `dotnet add package` command or a one-line central
    patch (none when the version isn't a literal);
  - *inferred*: types importing the package's namespaces in referencing projects, the flows through them
    (`FlowGraph.FlowsThrough`) and their linked tests;
  - *validated*: only with `--validate`, which applies the edits in a temporary copy and runs the configured steps.

  A major update without validation is always low confidence; a failed step makes it potentially breaking; passing
  steps are evidence, never proof. Static mode runs nothing.

## Generation

`Generator` is one partial class, split by page:

| File | Pages |
|---|---|
| [Generator.cs](src/DocWizz/Generation/Generator.cs) | `index.md`, `architecture.md`, `api.md`, `frontend.md`, `quality.md`, shared helpers |
| [Modules.cs](src/DocWizz/Generation/Modules.cs) | `modules/<folder>.md`: role, key components, API, data, external systems, configuration, flows, gaps, observations, component reference |
| [ProjectPages.cs](src/DocWizz/Generation/ProjectPages.cs) | `solutions/*.md`, `projects/*.md`, from [ProjectHierarchy](src/DocWizz/Discovery/ProjectHierarchy.cs) |
| [Views.cs](src/DocWizz/Generation/Views.cs) | `views/context.md`, `containers.md`, `components.md`, `data.md`; the configuration table |
| [Deployment.cs](src/DocWizz/Generation/Deployment.cs) | `views/deployment.md`: compose, Kubernetes, Dockerfiles, IaC |
| [Description.cs](src/DocWizz/Generation/Description.cs) | `architecture-description.md`, structured after ISO/IEC/IEEE 42010 |
| [PackagesView.cs](src/DocWizz/Generation/PackagesView.cs) | `views/packages.md`: the package inventory behind `docwizz sbom` |

- **Flows.** [FlowGraph](src/DocWizz/Analysis/FlowGraph.cs) traces them for the pages, change impact, package
  remediation, security rule SEC-005 and agent context: a 0-1 breadth-first search from an endpoint or route. It follows `calls`, `accesses`, `http`, `renders`,
  `routes-to`, `connects` and minimal-API `injects`, and dispatches interface members to implementations. Steps inside
  a C# type and interface dispatch cost 0, so the hops shown are between units. Frontend flows stop at endpoints.
- **Project hierarchy.** `ProjectHierarchy` builds path-hash page ids, multi-solution membership and one
  node-per-project index, so rendering never rescans the whole model per project. Shared projects get one page linked
  from every solution; metadata-only stubs never gain source, package or coverage facts. Container views qualify a
  project name with its manifest path only when names collide.
- **HTML.** With `--html`, [Html](src/DocWizz/Generation/Html/Html.cs) renders every page with Markdig next to its
  Markdown twin, so relative source links still work. Each page embeds the whole site navigation and works from disk;
  search reads `search.js` and ranks exact names, then prefixes, then substrings. Mermaid is a pinned CDN version.
  [HtmlVersion](src/DocWizz/Generation/Html/HtmlVersion.cs) stamps the docwizz version for
  `--refresh-html-on-version-change`.
- **Screenshots.** [ScreenshotCapture](src/DocWizz/Generation/ScreenshotCapture.cs) uses Playwright to capture the
  static routes the frontend scanner found, each in a fresh context, optionally loaded from a user-saved session. It
  never automates login, and skips failures and redirects.
- **Output rules.** Sections appear only when there is content. Diagrams are capped (`MaxDiagramEdges`,
  `MaxViewNodes`) or replaced by a note. Output is deterministic without `--ai`.
- **Safe regeneration.** Every generated page starts with a marker line; only pages with it are overwritten or
  deleted. `docs/architecture/*.md` and anything else written by hand is linked, never touched.

## AI

[AiProse](src/DocWizz/Ai/AiProse.cs) drafts summary, responsibilities, behaviour, side effects, errors and usage for
items without a summary, plus an overview for each module (folder) of three or more members.

- **Input:** per item, the facts JSON from [ContextBuilder](src/DocWizz/Ai/ContextBuilder.cs) (graph neighbours,
  signature, derived sections with their origin) and that symbol's own source lines. Never the repository.
- **Output:** one cited sentence per line, `<section>: <sentence> [<fact>; <fact>]` (a JSON object of sections is still
  read). Lines cost a model a third to half the tokens of JSON, and generating them is nearly all of the time. A citation is
  a symbol name, `source`, a field of the symbol's own facts (resting on the symbol) or a neighbour list such as
  `calls` (resting on the neighbours in it); it resolves to symbol ids. A sentence without a valid citation, or one
  that only says a section is empty, is dropped.
- **Requests:** four at once (a server with `OLLAMA_NUM_PARALLEL=4` runs them together; one that doesn't queues them).
  Ratings ask for JSON; drafts ask for lines, so the provider's JSON mode is off for them.
- **Transport:** [IAiProvider](src/DocWizz/Ai/AiProvider.cs) (`OllamaProvider` for `/api/chat`,
  `OpenAiCompatibleProvider` for `/v1/chat/completions`). Every provider connects through `LocalEndpoint`: each
  resolved address must be loopback or private and proxies are bypassed; Ollama cloud models are refused. Anything else
  that needs a model adds a provider rather than its own HTTP client.
- **Cache:** per symbol, body hash, provider and model in `docs/.docwizz/ai-cache.json`, with provenance; an answer
  with nothing usable is cached too, so unchanged code is never sent again. Drafts are marked 🤖 and never override
  written docs.
- **Ratings:** the same facts plus the doc comment yield a 1–5 usefulness score, cached in `ai-assessments.json` and
  shown in `quality.md` only; never part of doc quality % or `check`.

## Agent context

- **[AgentContext](src/DocWizz/Agent/AgentContext.cs)** answers `docwizz context`. It resolves `--for` to a scope (one
  symbol and its members, a file, a folder or an endpoint; never a guess), collects lines in priority order and drops
  from the lowest-priority end until the rendering fits the budget (characters / 4). Neighbours come from
  `ContextBuilder` and flows from the generator's tracer, so a package and the pages never disagree. It reads
  `docs/.docwizz/model.json` (reporting staleness from `HEAD` and file times) or scans; no network, no model.
- **[McpServer](src/DocWizz/Agent/Mcp.cs)** puts the same model behind the Model Context Protocol: hand-written
  JSON-RPC 2.0 over stdio (initialize, ping, tools/list, tools/call), so there is no SDK dependency and no listener.
  Tools only read; `impact` and `check` are the `diff` and `check --since` reports as data. The CLI passes its loaders
  and reports in as delegates, so the server has no I/O besides stdio.

## Project evidence and products

- **Evidence catalogs** ([ProjectEvidenceCatalog](src/DocWizz/Product/ProjectEvidenceCatalog.cs)) hold explicit,
  versioned project facts: requirements, architecture elements, tests and their declared links. YAML schema v1 is the
  single authority; [ProjectEvidenceImport](src/DocWizz/Product/ProjectEvidenceImport.cs) only adapts CSV and Markdown
  to it. Nothing is inferred.
- **`trace`** ([TraceabilityReport](src/DocWizz/Generation/TraceabilityReport.cs)) reports declared links and gaps;
  [EvidenceSourceCheck](src/DocWizz/Analysis/EvidenceSourceCheck.cs) verifies declared source hashes, reading only
  declared `docs/*.md` paths and never following symlinks.
- **`evidence-diff`** ([ProjectEvidenceImpact](src/DocWizz/Product/ProjectEvidenceImpact.cs)) compares declaration
  hashes between two snapshots and follows declared links to affected tests.
- **Product drafts.** [ADR-0001](docs/adr/0001-product-template-contract.md) defines the template contract,
  independent of coverage profiles. [ProductDraft](src/DocWizz/Product/ProductDraft.cs) builds an evidence catalog per
  section (code facts and sourced statements from [ProjectContext](src/DocWizz/Discovery/ProjectContext.cs)) with
  document-wide ids; sections without evidence say `OFFEN`. With `--ai`,
  [ProductSynthesis](src/DocWizz/Product/ProductSynthesis.cs) sends each section's evidence through `IAiProvider` and
  keeps only sentences that cite that section's ids and claim no status or conformity.
  [ProductCheck](src/DocWizz/Product/ProductCheck.cs) checks references and freshness, never content.

## Requirement history

- **[RequirementIndex](src/DocWizz/Requirements/RequirementIndex.cs)** reads `gitwizz.history/v2` (gitwizz's history
  export, read as its contract defines it, closed enumerations and all) and `ado.requirement-context/v1` files and
  records facts only: per commit its provider linkage, message mentions, PR attribution and what its changed lines
  touched (file-granularity changes stay file-level), mapped
  onto the symbols of a scan **as of that commit** (`ModelAt`, the `diff` baseline scan), innermost symbol first. It is
  kept apart from the code model (no work-item text among static facts) and from evidence catalogs (no inferred link
  ever becomes a declared one). Work-item text is cleaned and bounded at import. Its fingerprint covers every input
  digest and the scan scope; known commits are reused, so a refresh scans only new ones.
- **[RequirementResolver](src/DocWizz/Requirements/RequirementResolver.cs)** decides at query time, against the current
  model: `confirmed` needs a provider-verified commit link; a confirmed PR's link or a message mention alone stays
  `candidate`, and an unconfirmed PR is not used, and `changedSince` compares the
  symbol's hash then and now. Outcomes keep "verified none" apart from "couldn't tell". No network anywhere.
- **[BusinessContext](src/DocWizz/Requirements/BusinessContext.cs)** selects what a model or agent sees with
  `--requirements`: available items only, ranked deterministically, bounded by `ai.requirements`. `ContextBuilder` adds
  it as `businessContext` (data, never instructions); `AiProse` adds its rules to the prompt only then, keys the draft
  by the selection's hash, and grounds the answer: code sections lose work-item citations, `intent`/`deviation` need
  one and get its status, fulfilment claims are dropped. Without the flag every prompt and key is unchanged.
  `context --requirements` and the MCP tools `requirements_for_symbol`/`symbols_for_requirement` read the same index.

## Tests

Four layers, so a failure points at the part that broke. CI ([test.yml](.github/workflows/test.yml)) runs each as its
own step.

| Layer | Where | What it covers | Run |
|---|---|---|---|
| Unit | [tests/DocWizz.Tests/Unit](tests/DocWizz.Tests/Unit/) | `Analyzer`, `Architecture`, `Diff`, `CodeModel`, `Versions` on small in-memory models; `Cli` parsing and the command/option matrix; CLI.md and README checked against the command table, so the docs can't drift | `dotnet test --project tests/DocWizz.Tests --filter-namespace DocWizz.Tests.Unit` |
| Component | [tests/DocWizz.Tests/Component](tests/DocWizz.Tests/Component/) | one scanner on a few source snippets → nodes and edges, incl. past regressions; project files → remediation suggestions and validation | `… --filter-namespace DocWizz.Tests.Component` (Java needs `npm ci --prefix scanner-vue`) |
| Journey | [tests/DocWizz.Tests/Journey](tests/DocWizz.Tests/Journey/) | the CLI as a process: `setup` on small C#, Java, Vue, mixed, console, partial and empty repositories; idempotent re-runs; findings vs failures; exit codes, stdout vs stderr, no stack traces; no AI without `--ai`, no secrets in `docwizz.yaml`, no repository code run | `… --filter-namespace DocWizz.Tests.Journey` (needs `npm ci --prefix scanner-vue`) |
| End-to-end | [test.sh](test.sh) | repository → model → reports → generated pages, on the fixtures | `./test.sh` |

`test.sh` runs against [fixture/](fixture/), a small ASP.NET + EF Core + Vue project with deliberate gaps,
violations, external systems, configuration and deployment descriptors. It asserts facts in the model JSON and lines in
the generated pages. When adding a feature, extend the fixture with the smallest case that exercises it and assert both
model and page. Rules and analysis logic also get a unit test; a scanner fix gets a component test with the snippet
that broke it. Multi-project boundaries (golden multi-solution repositories, symlink and root escapes, an
80-project indexing test) are covered too; see [MULTI_PROJECT_ACCEPTANCE.md](MULTI_PROJECT_ACCEPTANCE.md).

The release version rule ([next-version.sh](.github/scripts/next-version.sh)) has its own table of cases in
[next-version.test.sh](.github/scripts/next-version.test.sh), run as the first step of CI.

Performance is measured separately: [bench/](bench/README.md) generates synthetic repositories of three sizes and times
every stage (`--timings`, [Timings.cs](src/DocWizz/Timings.cs)) against a committed baseline, on demand and weekly;
`scripts/benchmark-multi-project.sh` compares full and scoped scans.
